// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Azure.AI.AgentServer.Invocations.Voice;

namespace VoiceHostedAgent;

/// <summary>Coordinates bounded response execution and its Voice wire lifecycle.</summary>
internal sealed class VoiceResponseCoordinator
{
    private const int MaximumOutputChunks = 4096;
    private const int MaximumOutputUtf8Bytes = 512 * 1024;
    private static readonly AsyncLocal<ResponseOperation?> s_currentOperation = new();
    private readonly ConcurrentDictionary<VoiceSession, ConversationHistory> _histories = new();
    private readonly ResponseHistoryStore _responseHistories = new();
    private readonly IStreamingModelClient _modelClient;
    private readonly ILogger<VoiceAgent> _logger;

    internal VoiceResponseCoordinator(
        IStreamingModelClient modelClient,
        ILogger<VoiceAgent> logger)
    {
        _modelClient = modelClient;
        _logger = logger;
    }

    internal void StartResponse(
        VoiceSession session,
        VoiceConnectionState state,
        string? inputItemId,
        ResponsePlan plan,
        string? responseId = null,
        long? userMessageReceivedTimestamp = null)
    {
        responseId ??= VoiceIds.CreateResponseId();
        var operation = new ResponseOperation(
            responseId,
            inputItemId,
            userMessageReceivedTimestamp);

        if (!state.TryReserveOperation(operation))
        {
            operation.Dispose();
            if (inputItemId is not null)
            {
                Observe(SendNoneAsync(session, state, inputItemId, "capacity_exceeded"));
            }
            return;
        }

        StartReservedResponse(session, state, operation, plan);
    }

    internal void StartSelfCancellingResponse(
        VoiceSession session,
        VoiceConnectionState state,
        string inputItemId,
        string text,
        long userMessageReceivedTimestamp)
    {
        var responseId = VoiceIds.CreateResponseId();
        var operation = new ResponseOperation(responseId, inputItemId, userMessageReceivedTimestamp);
        if (!state.TryReserveOperation(operation))
        {
            operation.Dispose();
            Observe(SendNoneAsync(session, state, inputItemId, "capacity_exceeded"));
            return;
        }

        operation.TryStart(async () =>
        {
            await Task.Yield();
            await RunAsCurrentOperationAsync(
                operation,
                () => RunSelfCancellingResponseAsync(session, state, operation, text));
        });
    }

    internal Task SendNoneAsync(
        VoiceSession session,
        VoiceConnectionState state,
        string inputItemId,
        string reason) =>
        TrySendAsync(
            state,
            () => session.SendAsync(
                new VoiceResponseNoneMessage(new[] { inputItemId }, reason),
                state.Lifetime.Token));

    internal async Task SendErrorAsync(
        VoiceSession session,
        VoiceConnectionState state,
        string inputItemId)
    {
        var responseId = VoiceIds.CreateResponseId();
        await TrySendAsync(state, async () =>
        {
            await session.SendAsync(
                new VoiceResponseCreatedMessage(responseId, new[] { inputItemId }),
                state.Lifetime.Token);
            await session.SendAsync(
                new VoiceErrorMessage(
                    "sample_error",
                    "The sample emitted the requested error.",
                    responseId),
                state.Lifetime.Token);
        });
    }

    internal async Task SendProactiveRequestAsync(
        VoiceSession session,
        VoiceConnectionState state,
        string responseId)
    {
        try
        {
            await session.SendAsync(
                new VoiceResponseCreatedMessage(
                    responseId,
                    admissionTimeoutMs: 5000,
                    supersedeKey: "voice-live-bridge-basic-dotnet"),
                state.Lifetime.Token);
        }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested)
        {
            state.RemoveProactive(responseId);
        }
        catch (Exception exception)
        {
            state.RemoveProactive(responseId);
            _logger.LogWarning(
                exception,
                "Could not request proactive response {ResponseId}",
                responseId);
        }
    }

    internal async Task AcceptProactiveAsync(
        VoiceSession session,
        VoiceConnectionState state,
        string responseId)
    {
        var admission = state.TryAcceptProactive(responseId, out var operation, out var plan);
        if (admission == ProactiveAdmission.Accepted)
        {
            StartReservedResponse(session, state, operation!, plan!);
        }
        else if (admission == ProactiveAdmission.ActiveCapacityExceeded)
        {
            await TrySendAsync(
                state,
                () => session.SendAsync(
                    new VoiceResponseCancelMessage(responseId, "capacity_exceeded"),
                    state.Lifetime.Token));
        }
    }

    internal void ReconcileHeardText(
        VoiceSession session,
        string responseId,
        string heardText) =>
        _responseHistories.Reconcile(session, responseId, heardText);

    internal void CancelOperation(
        VoiceConnectionState state,
        string responseId,
        ResponseOutcome outcome)
    {
        if (state.TryRemoveOperation(responseId, out var operation))
        {
            operation.SelectOutcome(outcome);
            Observe(CancelAndDisposeAsync(operation));
        }
    }

    internal async Task TerminateAndEndCallAsync(
        VoiceSession session,
        VoiceConnectionState state,
        VoiceEndCallMode mode,
        CancellationToken cancellationToken,
        string reason = "sample_completed")
    {
        if (await TerminateAsync(state, ResponseOutcome.EndCall))
        {
            await session.SendAsync(
                new VoiceEndCallMessage(reason, mode),
                cancellationToken);
        }
    }

    internal async Task TerminateSessionAsync(
        VoiceSession session,
        VoiceConnectionState state,
        ResponseOutcome outcome)
    {
        await TerminateAsync(state, outcome);
        ClearSessionHistory(session);
    }

    internal void Observe(Task task) => _ = ObserveAsync(task);

    private void StartReservedResponse(
        VoiceSession session,
        VoiceConnectionState state,
        ResponseOperation operation,
        ResponsePlan plan)
    {
        operation.TryStart(async () =>
        {
            await Task.Yield();
            await RunAsCurrentOperationAsync(
                operation,
                () => RunResponseAsync(session, state, operation, plan));
        });
    }

    private static async Task RunAsCurrentOperationAsync(
        ResponseOperation operation,
        Func<Task> work)
    {
        var previous = s_currentOperation.Value;
        s_currentOperation.Value = operation;
        try
        {
            await work();
        }
        finally
        {
            s_currentOperation.Value = previous;
        }
    }

    private async Task RunResponseAsync(
        VoiceSession session,
        VoiceConnectionState state,
        ResponseOperation operation,
        ResponsePlan plan)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operation.Cancellation.Token,
            state.Lifetime.Token);
        var cancellationToken = cancellation.Token;
        try
        {
            if (operation.InputItemId is not null)
            {
                await session.SendAsync(
                    new VoiceResponseCreatedMessage(
                        operation.ResponseId,
                        new[] { operation.InputItemId }),
                    cancellationToken);
            }

            if (plan.UseModel)
            {
                await SendModelOutputAsync(session, state, operation, plan, cancellationToken);
            }
            else
            {
                await SendFixedOutputAsync(session, state, operation, plan, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            operation.RecordFailure(exception);
            _logger.LogError(exception, "Voice response {ResponseId} failed", operation.ResponseId);
            await TrySendErrorAsync(session, state, operation.ResponseId);
        }
        finally
        {
            RemoveOperation(session, state, operation.ResponseId);
        }
    }

    private async Task SendModelOutputAsync(
        VoiceSession session,
        VoiceConnectionState state,
        ResponseOperation operation,
        ResponsePlan plan,
        CancellationToken cancellationToken)
    {
        var history = _histories.GetOrAdd(session, static _ => new ConversationHistory());
        _responseHistories.Track(session, operation.ResponseId, history, plan.Text);
        await history.Gate.WaitAsync(cancellationToken);
        try
        {
            using var activity = ModelTelemetry.Start(_modelClient.ModelName, _modelClient.ServerAddress);
            var modelStartedAt = Stopwatch.GetTimestamp();
            TimeSpan? firstTokenLatency = null;
            var itemId = VoiceIds.CreateItemId();
            var text = new StringBuilder();
            var firstChunk = true;
            var chunkCount = 0;
            var utf8Bytes = 0;
            try
            {
                await foreach (var chunk in _modelClient
                    .CompleteAsync(history.CreateRequest(plan.Text), cancellationToken)
                    .WithCancellation(cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                    {
                        continue;
                    }

                    var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
                    if (chunkCount >= MaximumOutputChunks ||
                        utf8Bytes > MaximumOutputUtf8Bytes - chunkBytes)
                    {
                        throw new InvalidOperationException(
                            "The language model output exceeded sample limits.");
                    }
                    chunkCount++;
                    utf8Bytes += chunkBytes;

                    if (firstTokenLatency is null)
                    {
                        firstTokenLatency = Stopwatch.GetElapsedTime(modelStartedAt);
                        ModelTelemetry.RecordFirstToken(activity, firstTokenLatency.Value);
                    }

                    text.Append(chunk);
                    if (plan.Streaming)
                    {
                        await session.SendAsync(
                            new VoiceResponseOutputTextDeltaMessage(
                                operation.ResponseId,
                                itemId,
                                chunk,
                                firstChunk ? plan.Voice : null),
                            cancellationToken);
                        firstChunk = false;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ModelTelemetry.RecordFailure(activity, exception);
                throw;
            }

            if (text.Length == 0)
            {
                throw new InvalidOperationException("The language model returned no text.");
            }

            var responseDuration = Stopwatch.GetElapsedTime(modelStartedAt);
            ModelTelemetry.RecordCompleted(activity, responseDuration);
            _logger.LogInformation(
                "Model response {ResponseId} completed: first token {FirstTokenLatencyMs:F1} ms, total {ResponseDurationMs:F1} ms",
                operation.ResponseId,
                firstTokenLatency!.Value.TotalMilliseconds,
                responseDuration.TotalMilliseconds);

            await CompleteOutputAsync(
                session,
                state,
                operation,
                itemId,
                text.ToString(),
                plan.Voice,
                cancellationToken);
            history.Commit(operation.ResponseId, plan.Text, text.ToString());
        }
        finally
        {
            history.Gate.Release();
        }
    }

    private static async Task SendFixedOutputAsync(
        VoiceSession session,
        VoiceConnectionState state,
        ResponseOperation operation,
        ResponsePlan plan,
        CancellationToken cancellationToken)
    {
        var itemId = VoiceIds.CreateItemId();
        if (plan.Streaming)
        {
            await session.SendAsync(
                new VoiceResponseOutputTextDeltaMessage(
                    operation.ResponseId,
                    itemId,
                    plan.Text,
                    plan.Voice),
                cancellationToken);
        }

        await CompleteOutputAsync(
            session,
            state,
            operation,
            itemId,
            plan.Text,
            plan.Voice,
            cancellationToken);
    }

    private static async Task CompleteOutputAsync(
        VoiceSession session,
        VoiceConnectionState state,
        ResponseOperation operation,
        string itemId,
        string text,
        BinaryData? voice,
        CancellationToken cancellationToken)
    {
        await session.SendAsync(
            new VoiceResponseOutputTextDoneMessage(
                operation.ResponseId,
                itemId,
                text,
                voice),
            cancellationToken);
        state.MarkTerminal(operation.ResponseId);
        await session.SendAsync(
            new VoiceResponseDoneMessage(operation.ResponseId),
            cancellationToken);
        operation.RecordCompleted();
    }

    private async Task RunSelfCancellingResponseAsync(
        VoiceSession session,
        VoiceConnectionState state,
        ResponseOperation operation,
        string text)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operation.Cancellation.Token,
            state.Lifetime.Token);
        var cancellationToken = cancellation.Token;
        try
        {
            var itemId = VoiceIds.CreateItemId();
            await session.SendAsync(
                new VoiceResponseCreatedMessage(
                    operation.ResponseId,
                    new[] { operation.InputItemId! }),
                cancellationToken);
            await session.SendAsync(
                new VoiceResponseOutputTextDeltaMessage(
                    operation.ResponseId,
                    itemId,
                    text),
                cancellationToken);
            await session.SendAsync(
                new VoiceResponseCancelMessage(
                    operation.ResponseId,
                    "sample_self_correction"),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Self-cancelling response failed");
            RemoveOperation(session, state, operation.ResponseId);
        }
    }

    private async Task TrySendErrorAsync(
        VoiceSession session,
        VoiceConnectionState state,
        string responseId)
    {
        try
        {
            if (!state.IsActive)
            {
                return;
            }
            await session.SendAsync(
                new VoiceErrorMessage(
                    "generation_failed",
                    "The response could not be generated.",
                    responseId),
                state.Lifetime.Token);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not report response failure");
        }
    }

    private async Task<bool> TerminateAsync(
        VoiceConnectionState state,
        ResponseOutcome outcome)
    {
        var ownsTermination = state.TryBeginTermination(
            out var operations,
            out var completion);
        if (ownsTermination)
        {
            try
            {
                foreach (var operation in operations)
                {
                    operation.SelectOutcome(outcome);
                }
                await Task.WhenAll(operations.Select(CancelAndDisposeAsync));
            }
            finally
            {
                state.CompleteTermination();
            }
        }

        await completion;
        return ownsTermination;
    }

    private void ClearSessionHistory(VoiceSession session)
    {
        _responseHistories.Clear(session);
        _histories.TryRemove(session, out _);
    }

    private void RemoveOperation(
        VoiceSession session,
        VoiceConnectionState state,
        string responseId)
    {
        _responseHistories.Complete(session, responseId);
        if (state.TryRemoveOperation(responseId, out var operation))
        {
            operation.Dispose();
        }
    }

    private async Task CancelAndDisposeAsync(ResponseOperation operation)
    {
        try
        {
            await operation.Cancellation.CancelAsync();
            var work = operation.Work;
            if (work is not null &&
                !ReferenceEquals(s_currentOperation.Value, operation) &&
                work.Id != Task.CurrentId)
            {
                await work;
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Response cancellation failed");
        }
        finally
        {
            operation.Dispose();
        }
    }

    private async Task TrySendAsync(VoiceConnectionState state, Func<Task> send)
    {
        if (!state.IsActive)
        {
            return;
        }

        try
        {
            await send();
        }
        catch (OperationCanceledException) when (state.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Voice protocol message could not be sent");
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Voice response cleanup failed");
        }
    }
}
