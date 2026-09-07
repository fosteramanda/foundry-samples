// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Azure.AI.AgentServer.Invocations.Voice;

namespace VoiceHostedAgent;

/// <summary>Stores a bounded sequence of model-visible turns for one Voice session.</summary>
internal sealed class ConversationHistory
{
    private const int MaximumMessages = 12;
    private const int MaximumCharacters = 24_000;
    private readonly object _sync = new();
    private readonly List<ConversationTurn> _turns = new();
    private readonly HashSet<string> _discardedResponseIds = new(StringComparer.Ordinal);

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal IReadOnlyList<ModelMessage> CreateRequest(string prompt)
    {
        lock (_sync)
        {
            var messages = new List<ModelMessage>(_turns.Count * 2 + 1);
            foreach (var turn in _turns)
            {
                messages.Add(new ModelMessage(ModelMessageRole.User, turn.Prompt));
                if (!string.IsNullOrEmpty(turn.Answer))
                {
                    messages.Add(new ModelMessage(ModelMessageRole.Assistant, turn.Answer));
                }
            }

            messages.Add(new ModelMessage(ModelMessageRole.User, BoundText(prompt)));
            return messages;
        }
    }

    internal void Commit(string responseId, string prompt, string answer)
    {
        lock (_sync)
        {
            if (_discardedResponseIds.Remove(responseId))
            {
                return;
            }
            if (_turns.Any(turn => turn.ResponseId == responseId))
            {
                return;
            }

            _turns.Add(new ConversationTurn(responseId, BoundText(prompt), BoundText(answer)));
            Trim();
        }
    }

    internal void Reconcile(string responseId, string prompt, string heardText)
    {
        lock (_sync)
        {
            var index = _turns.FindIndex(turn => turn.ResponseId == responseId);
            if (string.IsNullOrEmpty(heardText))
            {
                if (index >= 0)
                {
                    _turns.RemoveAt(index);
                }
                else
                {
                    _discardedResponseIds.Add(responseId);
                }
                return;
            }

            var reconciled = new ConversationTurn(
                responseId,
                BoundText(prompt),
                BoundText(heardText));
            if (index >= 0)
            {
                _turns[index] = reconciled;
            }
            else
            {
                _turns.Add(reconciled);
            }
            Trim();
        }
    }

    internal static string BoundText(string value) =>
        value.Length <= 8_000 ? value : value[..8_000];

    internal void CompleteResponse(string responseId)
    {
        lock (_sync)
        {
            _discardedResponseIds.Remove(responseId);
        }
    }

    internal int DiscardedResponseCount
    {
        get
        {
            lock (_sync)
            {
                return _discardedResponseIds.Count;
            }
        }
    }

    private void Trim()
    {
        while (MessageCount() > MaximumMessages || CharacterCount() > MaximumCharacters)
        {
            _turns.RemoveAt(0);
        }
    }

    private int MessageCount() =>
        _turns.Sum(turn => string.IsNullOrEmpty(turn.Answer) ? 1 : 2);

    private int CharacterCount() =>
        _turns.Sum(turn => turn.Prompt.Length + turn.Answer.Length);

    private sealed record ConversationTurn(string ResponseId, string Prompt, string Answer);
}

/// <summary>Correlates response IDs with session history for barge-in reconciliation.</summary>
internal sealed class ResponseHistoryStore
{
    private const int MaximumTrackedResponseHistories = 64;
    private readonly ConcurrentDictionary<VoiceSession, SessionResponseHistories> _sessions = new();

    internal void Track(
        VoiceSession session,
        string responseId,
        ConversationHistory history,
        string prompt)
    {
        _sessions.GetOrAdd(session, static _ => new SessionResponseHistories()).Track(
            responseId,
            history,
            ConversationHistory.BoundText(prompt));
    }

    internal void Reconcile(VoiceSession session, string responseId, string heardText)
    {
        if (_sessions.TryGetValue(session, out var histories))
        {
            histories.Reconcile(responseId, heardText);
        }
    }

    internal void Complete(VoiceSession session, string responseId)
    {
        if (_sessions.TryGetValue(session, out var histories))
        {
            histories.Complete(responseId);
        }
    }

    internal void Clear(VoiceSession session) => _sessions.TryRemove(session, out _);

    private sealed class SessionResponseHistories
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, ResponseHistoryContext> _histories =
            new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        internal void Track(string responseId, ConversationHistory history, string prompt)
        {
            lock (_sync)
            {
                if (_histories.TryAdd(
                    responseId,
                    new ResponseHistoryContext(history, prompt)))
                {
                    _order.Enqueue(responseId);
                }
                while (_histories.Count > MaximumTrackedResponseHistories)
                {
                    _histories.Remove(_order.Dequeue());
                }
            }
        }

        internal void Reconcile(string responseId, string heardText)
        {
            lock (_sync)
            {
                if (_histories.TryGetValue(responseId, out var context))
                {
                    context.History.Reconcile(responseId, context.Prompt, heardText);
                }
            }
        }

        internal void Complete(string responseId)
        {
            lock (_sync)
            {
                if (_histories.TryGetValue(responseId, out var context))
                {
                    context.History.CompleteResponse(responseId);
                }
            }
        }
    }

    private sealed record ResponseHistoryContext(
        ConversationHistory History,
        string Prompt);
}
