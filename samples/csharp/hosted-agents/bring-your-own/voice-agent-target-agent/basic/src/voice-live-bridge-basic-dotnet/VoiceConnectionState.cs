// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace VoiceHostedAgent;

internal enum ConnectionPhase
{
    AwaitingStart,
    Starting,
    Active,
    Rejected,
    Terminating,
}

internal enum InputAdmission
{
    Inactive,
    Accepted,
    Duplicate,
    CapacityExceeded,
}

internal enum ProactiveAdmission
{
    NotFound,
    Accepted,
    ActiveCapacityExceeded,
    Inactive,
}

/// <summary>Owns the bounded, atomic state for one physical Voice connection.</summary>
internal sealed class VoiceConnectionState
{
    internal const int MaximumActiveResponses = 8;
    internal const int MaximumPendingProactiveResponses = 8;
    internal const int MaximumTerminalSends = 8;
    internal const int MaximumTrackedInputItems = 4096;

    private readonly object _sync = new();
    private readonly HashSet<string> _seenInputItemDigests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResponseOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _inputResponses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResponsePlan> _proactiveRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeResponseIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _terminalResponseIds = new(StringComparer.Ordinal);
    private ConnectionPhase _phase;
    private TaskCompletionSource? _terminationCompletion;

    internal CancellationTokenSource Lifetime { get; } = new();

    internal bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _phase == ConnectionPhase.Active;
            }
        }
    }

    internal bool TryBeginStart()
    {
        lock (_sync)
        {
            if (_phase != ConnectionPhase.AwaitingStart)
            {
                return false;
            }

            _phase = ConnectionPhase.Starting;
            return true;
        }
    }

    internal void Activate()
    {
        lock (_sync)
        {
            if (_phase == ConnectionPhase.Starting)
            {
                _phase = ConnectionPhase.Active;
            }
        }
    }

    internal void Reject()
    {
        lock (_sync)
        {
            if (_phase == ConnectionPhase.Starting)
            {
                _phase = ConnectionPhase.Rejected;
            }
        }
    }

    internal InputAdmission TryTrackInput(string inputItemId)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(inputItemId)));
        lock (_sync)
        {
            if (_phase != ConnectionPhase.Active)
            {
                return InputAdmission.Inactive;
            }
            if (_seenInputItemDigests.Contains(digest))
            {
                return InputAdmission.Duplicate;
            }
            if (_seenInputItemDigests.Count >= MaximumTrackedInputItems)
            {
                return InputAdmission.CapacityExceeded;
            }

            _seenInputItemDigests.Add(digest);
            return InputAdmission.Accepted;
        }
    }

    internal bool TryReserveOperation(ResponseOperation operation)
    {
        lock (_sync)
        {
            if (_phase != ConnectionPhase.Active ||
                _activeResponseIds.Count >= MaximumActiveResponses ||
                _terminalResponseIds.Count >= MaximumTerminalSends ||
                _operations.ContainsKey(operation.ResponseId))
            {
                return false;
            }

            _operations.Add(operation.ResponseId, operation);
            _activeResponseIds.Add(operation.ResponseId);
            if (operation.InputItemId is not null)
            {
                _inputResponses[operation.InputItemId] = operation.ResponseId;
            }
            return true;
        }
    }

    internal bool TryReserveProactive(
        string responseId,
        ResponsePlan plan,
        out string reservedResponseId)
    {
        lock (_sync)
        {
            reservedResponseId = responseId;
            if (_phase != ConnectionPhase.Active ||
                _proactiveRequests.Count >= MaximumPendingProactiveResponses)
            {
                return false;
            }

            _proactiveRequests.Add(responseId, plan);
            return true;
        }
    }

    internal ProactiveAdmission TryAcceptProactive(
        string responseId,
        out ResponseOperation? operation,
        out ResponsePlan? plan)
    {
        lock (_sync)
        {
            operation = null;
            if (!_proactiveRequests.Remove(responseId, out plan))
            {
                return ProactiveAdmission.NotFound;
            }
            if (_phase != ConnectionPhase.Active)
            {
                return ProactiveAdmission.Inactive;
            }
            if (_activeResponseIds.Count >= MaximumActiveResponses)
            {
                return ProactiveAdmission.ActiveCapacityExceeded;
            }
            if (_terminalResponseIds.Count >= MaximumTerminalSends)
            {
                return ProactiveAdmission.ActiveCapacityExceeded;
            }

            operation = new ResponseOperation(responseId, inputItemId: null);
            _operations.Add(responseId, operation);
            _activeResponseIds.Add(responseId);
            return ProactiveAdmission.Accepted;
        }
    }

    internal void RemoveProactive(string responseId)
    {
        lock (_sync)
        {
            _proactiveRequests.Remove(responseId);
        }
    }

    internal bool TryGetResponseForInput(string inputItemId, out string responseId)
    {
        lock (_sync)
        {
            return _inputResponses.TryGetValue(inputItemId, out responseId!);
        }
    }

    internal void MarkTerminal(string responseId)
    {
        lock (_sync)
        {
            if (_activeResponseIds.Remove(responseId))
            {
                _terminalResponseIds.Add(responseId);
            }
            if (_operations.TryGetValue(responseId, out var operation) &&
                operation.InputItemId is not null)
            {
                _inputResponses.Remove(operation.InputItemId);
            }
        }
    }

    internal bool TryRemoveOperation(string responseId, out ResponseOperation operation)
    {
        lock (_sync)
        {
            if (!_operations.Remove(responseId, out operation!))
            {
                return false;
            }

            _activeResponseIds.Remove(responseId);
            _terminalResponseIds.Remove(responseId);
            if (operation.InputItemId is not null)
            {
                _inputResponses.Remove(operation.InputItemId);
            }
            return true;
        }
    }

    internal bool TryBeginTermination(
        out IReadOnlyList<ResponseOperation> operations,
        out Task completion)
    {
        lock (_sync)
        {
            if (_terminationCompletion is not null)
            {
                operations = Array.Empty<ResponseOperation>();
                completion = _terminationCompletion.Task;
                return false;
            }

            _terminationCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _phase = ConnectionPhase.Terminating;
            Lifetime.Cancel();
            operations = _operations.Values.ToArray();
            _operations.Clear();
            _activeResponseIds.Clear();
            _terminalResponseIds.Clear();
            _inputResponses.Clear();
            _proactiveRequests.Clear();
            completion = _terminationCompletion.Task;
            return true;
        }
    }

    internal void CompleteTermination()
    {
        lock (_sync)
        {
            _terminationCompletion?.TrySetResult();
        }
    }
}
