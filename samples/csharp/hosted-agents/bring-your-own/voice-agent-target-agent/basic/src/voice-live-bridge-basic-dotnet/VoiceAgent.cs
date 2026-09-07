// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Azure.AI.AgentServer.Invocations.Voice;

namespace VoiceHostedAgent;

/// <summary>Voice Bridge Protocol 1.0 sample backed by the Foundry Responses API.</summary>
public class VoiceAgent : VoiceHandler
{
    private const int MaximumInputCharacters = 8_000;
    private const string HelpText =
        "Commands: /stream text, /done text, /voice text, /none, /proactive text, " +
        "/cancel text, /error, /end, /end-now, and /help.";

    private readonly ConditionalWeakTable<VoiceSession, VoiceConnectionState> _connections = new();
    private readonly VoiceResponseCoordinator _responses;
    private readonly ILogger<VoiceAgent> _logger;

    public VoiceAgent(IStreamingModelClient modelClient, ILogger<VoiceAgent> logger)
    {
        _responses = new VoiceResponseCoordinator(modelClient, logger);
        _logger = logger;
    }

    protected override async Task OnSessionStartAsync(
        VoiceSession session,
        VoiceSessionStartEvent start,
        CancellationToken cancellationToken)
    {
        var state = _connections.GetValue(session, static _ => new VoiceConnectionState());
        if (!state.TryBeginStart())
        {
            _logger.LogWarning("Ignored duplicate or late session.start");
            return;
        }

        if (!string.Equals(start.ProtocolVersion, "1.0", StringComparison.Ordinal))
        {
            state.Reject();
            await session.SendAsync(
                new VoiceSessionRejectedMessage("protocol_mismatch", retriable: false),
                cancellationToken);
            return;
        }

        _logger.LogInformation(
            "Voice session {SessionId} started; reconnect={Reconnect}",
            session.InvocationContext.SessionId,
            start.Reconnect);
        try
        {
            await session.SendAsync(new VoiceSessionReadyMessage(), cancellationToken);
            state.Activate();
        }
        catch
        {
            if (state.TryBeginTermination(out _, out _))
            {
                state.CompleteTermination();
            }
            throw;
        }
    }

    protected override async Task OnUserMessageAsync(
        VoiceSession session,
        VoiceUserMessageEvent message,
        CancellationToken cancellationToken)
    {
        if (!TryTrackInput(session, message.ItemId, out var state, out var rejectionReason))
        {
            await TryDeclineInputAsync(
                session,
                state,
                message.ItemId,
                rejectionReason,
                cancellationToken);
            return;
        }

        var userMessageReceivedTimestamp = Stopwatch.GetTimestamp();
        var inputBuilder = new StringBuilder();
        foreach (var part in message.Content)
        {
            if (part.Text.Length > MaximumInputCharacters - inputBuilder.Length)
            {
                await _responses.SendNoneAsync(
                    session,
                    state,
                    message.ItemId,
                    "input_too_large");
                return;
            }
            inputBuilder.Append(part.Text);
        }
        var input = inputBuilder.ToString().Trim();
        var (command, argument) = ParseCommand(input);

        switch (command)
        {
            case "/none":
                await _responses.SendNoneAsync(session, state, message.ItemId, "no_reply_needed");
                return;
            case "/proactive":
                if (!state.TryReserveProactive(
                    VoiceIds.CreateResponseId(),
                    ResponsePlan.Model(
                        string.IsNullOrWhiteSpace(argument)
                            ? "Start a brief proactive conversation with the caller."
                            : argument),
                    out var proactiveResponseId))
                {
                    await _responses.SendNoneAsync(
                        session,
                        state,
                        message.ItemId,
                        "capacity_exceeded");
                    return;
                }
                await _responses.SendNoneAsync(session, state, message.ItemId, "no_reply_needed");
                await _responses.SendProactiveRequestAsync(session, state, proactiveResponseId);
                return;
            case "/cancel":
                _responses.StartSelfCancellingResponse(
                    session,
                    state,
                    message.ItemId,
                    string.IsNullOrWhiteSpace(argument)
                        ? "This response cancels itself."
                        : argument,
                    userMessageReceivedTimestamp);
                return;
            case "/error":
                await _responses.SendErrorAsync(session, state, message.ItemId);
                return;
            case "/end":
            case "/end-now":
                await _responses.TerminateAndEndCallAsync(
                    session,
                    state,
                    command == "/end-now" ? VoiceEndCallMode.Immediate : VoiceEndCallMode.Drain,
                    cancellationToken);
                return;
            case "/done":
                StartResponse(
                    session,
                    state,
                    message.ItemId,
                    ResponsePlan.Model(argument, streaming: false),
                    userMessageReceivedTimestamp);
                return;
            case "/voice":
                StartResponse(
                    session,
                    state,
                    message.ItemId,
                    ResponsePlan.Model(
                        argument,
                        streaming: false,
                        voice: BinaryData.FromString("""{"rate":"+10%"}""")),
                    userMessageReceivedTimestamp);
                return;
            case "/help":
                StartResponse(
                    session,
                    state,
                    message.ItemId,
                    ResponsePlan.Fixed(HelpText),
                    userMessageReceivedTimestamp);
                return;
            case "/stream":
                input = argument;
                break;
        }

        StartResponse(
            session,
            state,
            message.ItemId,
            ResponsePlan.Model(input),
            userMessageReceivedTimestamp);
    }

    protected override Task OnUserNoInputAsync(
        VoiceSession session,
        VoiceUserNoInputEvent noInput,
        CancellationToken cancellationToken)
    {
        if (!TryTrackInput(session, noInput.ItemId, out var state, out var rejectionReason))
        {
            return TryDeclineInputAsync(
                session,
                state,
                noInput.ItemId,
                rejectionReason,
                cancellationToken);
        }

        if (noInput.Count >= 3)
        {
            return _responses.TerminateAndEndCallAsync(
                session,
                state,
                VoiceEndCallMode.Drain,
                cancellationToken);
        }

        _responses.StartResponse(
            session,
            state,
            noInput.ItemId,
            ResponsePlan.Fixed("Are you still there?"));
        return Task.CompletedTask;
    }

    protected override Task OnUserSpeechStartedAsync(
        VoiceSession session,
        VoiceUserSpeechStartedEvent speechStarted,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Caller speech started");
        return Task.CompletedTask;
    }

    protected override Task OnBargeInAsync(
        VoiceSession session,
        VoiceBargeInEvent bargeIn,
        CancellationToken cancellationToken)
    {
        _responses.ReconcileHeardText(session, bargeIn.ResponseId, bargeIn.HeardText);
        if (_connections.TryGetValue(session, out var state))
        {
            _responses.CancelOperation(state, bargeIn.ResponseId, ResponseOutcome.Cancelled);
        }
        return Task.CompletedTask;
    }

    protected override Task OnResponseAcceptedAsync(
        VoiceSession session,
        VoiceResponseAcceptedEvent accepted,
        CancellationToken cancellationToken) =>
        _connections.TryGetValue(session, out var state)
            ? _responses.AcceptProactiveAsync(session, state, accepted.ResponseId)
            : Task.CompletedTask;

    protected override Task OnResponseDroppedAsync(
        VoiceSession session,
        VoiceResponseDroppedEvent dropped,
        CancellationToken cancellationToken)
    {
        if (_connections.TryGetValue(session, out var state))
        {
            state.RemoveProactive(dropped.ResponseId);
        }
        _logger.LogInformation(
            "Proactive response {ResponseId} was dropped: {Reason}",
            dropped.ResponseId,
            dropped.Reason);
        return Task.CompletedTask;
    }

    protected override Task OnResponseCancelledAsync(
        VoiceSession session,
        VoiceResponseCancelledEvent cancelled,
        CancellationToken cancellationToken)
    {
        _responses.ReconcileHeardText(session, cancelled.ResponseId, cancelled.HeardText);
        if (_connections.TryGetValue(session, out var state))
        {
            _responses.CancelOperation(state, cancelled.ResponseId, ResponseOutcome.Cancelled);
        }
        return Task.CompletedTask;
    }

    protected override Task OnResponseTimeoutAsync(
        VoiceSession session,
        VoiceResponseTimeoutEvent timeout,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(session, out var state))
        {
            return Task.CompletedTask;
        }

        if (timeout.ResponseId is not null)
        {
            state.RemoveProactive(timeout.ResponseId);
            _responses.CancelOperation(state, timeout.ResponseId, ResponseOutcome.Timeout);
        }
        else if (timeout.ItemIds is not null)
        {
            foreach (var itemId in timeout.ItemIds)
            {
                if (state.TryGetResponseForInput(itemId, out var responseId))
                {
                    _responses.CancelOperation(state, responseId, ResponseOutcome.Timeout);
                }
            }
        }
        return Task.CompletedTask;
    }

    protected override async Task OnSessionEndAsync(
        VoiceSession session,
        VoiceSessionEndEvent end,
        CancellationToken cancellationToken)
    {
        if (_connections.TryGetValue(session, out var state))
        {
            await _responses.TerminateSessionAsync(session, state, ResponseOutcome.EndCall);
        }
    }

    protected override void OnConnectionTerminating(VoiceSession session)
    {
        if (_connections.TryGetValue(session, out var state))
        {
            _responses.Observe(
                _responses.TerminateSessionAsync(
                    session,
                    state,
                    ResponseOutcome.TransportError));
        }
    }

    private void StartResponse(
        VoiceSession session,
        VoiceConnectionState state,
        string inputItemId,
        ResponsePlan plan,
        long userMessageReceivedTimestamp) =>
        _responses.StartResponse(
            session,
            state,
            inputItemId,
            plan,
            userMessageReceivedTimestamp: userMessageReceivedTimestamp);

    private bool TryTrackInput(
        VoiceSession session,
        string inputItemId,
        out VoiceConnectionState state,
        out string? rejectionReason)
    {
        state = _connections.GetValue(session, static _ => new VoiceConnectionState());
        switch (state.TryTrackInput(inputItemId))
        {
            case InputAdmission.Accepted:
                rejectionReason = null;
                return true;
            case InputAdmission.Duplicate:
                rejectionReason = null;
                _logger.LogWarning("Ignored duplicate Voice input item");
                return false;
            case InputAdmission.CapacityExceeded:
                rejectionReason = "session_input_limit";
                return false;
            default:
                rejectionReason = null;
                _logger.LogDebug("Ignored input outside an active Voice session");
                return false;
        }
    }

    private Task TryDeclineInputAsync(
        VoiceSession session,
        VoiceConnectionState state,
        string inputItemId,
        string? reason,
        CancellationToken cancellationToken) =>
        reason switch
        {
            null => Task.CompletedTask,
            "session_input_limit" => _responses.TerminateAndEndCallAsync(
                session,
                state,
                VoiceEndCallMode.Drain,
                cancellationToken,
                reason),
            _ => _responses.SendNoneAsync(session, state, inputItemId, reason),
        };

    private static (string Command, string Argument) ParseCommand(string input)
    {
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return (string.Empty, input);
        }

        var separator = input.IndexOf(' ');
        return separator < 0
            ? (input.ToLowerInvariant(), string.Empty)
            : (input[..separator].ToLowerInvariant(), input[(separator + 1)..].Trim());
    }
}
