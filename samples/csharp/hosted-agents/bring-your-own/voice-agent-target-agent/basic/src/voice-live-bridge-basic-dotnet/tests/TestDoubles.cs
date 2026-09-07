// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Azure.AI.AgentServer.Invocations.Voice;

namespace VoiceHostedAgent.Tests;

internal sealed class CapturingVoiceSession : VoiceSession
{
    private readonly ConcurrentQueue<VoiceOutboundMessage> _messages = new();

    internal IReadOnlyList<VoiceOutboundMessage> Messages => _messages.ToArray();

    internal void ClearMessages() => _messages.Clear();

    public override Task SendAsync(
        VoiceOutboundMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

internal sealed class BlockingReadyVoiceSession : VoiceSession
{
    private readonly ConcurrentQueue<VoiceOutboundMessage> _messages = new();

    internal TaskCompletionSource ReadySendStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseReadySend { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<VoiceOutboundMessage> Messages => _messages.ToArray();

    public override async Task SendAsync(
        VoiceOutboundMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message is VoiceSessionReadyMessage)
        {
            ReadySendStarted.TrySetResult();
            await ReleaseReadySend.Task.WaitAsync(cancellationToken);
        }
        _messages.Enqueue(message);
    }
}

internal sealed class BlockingResponseDoneVoiceSession : VoiceSession
{
    private readonly ConcurrentQueue<VoiceOutboundMessage> _messages = new();

    internal TaskCompletionSource ResponseDoneSendStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseResponseDoneSend { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<VoiceOutboundMessage> Messages => _messages.ToArray();

    internal void ClearMessages() => _messages.Clear();

    public override async Task SendAsync(
        VoiceOutboundMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message is VoiceResponseDoneMessage)
        {
            ResponseDoneSendStarted.TrySetResult();
            await ReleaseResponseDoneSend.Task.WaitAsync(cancellationToken);
        }
        _messages.Enqueue(message);
    }
}

internal sealed class ScriptedModelClient(params string[] chunks) : IStreamingModelClient
{
    private readonly string[] _chunks = chunks;
    private readonly ConcurrentQueue<IReadOnlyList<ModelMessage>> _requests = new();

    internal IReadOnlyList<IReadOnlyList<ModelMessage>> Requests => _requests.ToArray();

    public string ModelName => "test-model";

    public string ServerAddress => "test.services.ai.azure.com";

    public async IAsyncEnumerable<string> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _requests.Enqueue(messages.ToArray());
        foreach (var chunk in _chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }
}

internal sealed class BlockingModelClient : IStreamingModelClient
{
    private int _requestCount;

    internal int RequestCount => Volatile.Read(ref _requestCount);
    internal TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Cancelled { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ModelName => "blocking-model";

    public string ServerAddress => "test.services.ai.azure.com";

    public async IAsyncEnumerable<string> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _requestCount);
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
            }
        }

        yield break;
    }
}

internal sealed class InterruptibleThenScriptedModelClient : IStreamingModelClient
{
    private readonly ConcurrentQueue<IReadOnlyList<ModelMessage>> _requests = new();
    private int _callCount;

    internal TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Cancelled { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<IReadOnlyList<ModelMessage>> Requests => _requests.ToArray();

    public string ModelName => "interruptible-model";

    public string ServerAddress => "test.services.ai.azure.com";

    public async IAsyncEnumerable<string> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _requests.Enqueue(messages.ToArray());
        if (Interlocked.Increment(ref _callCount) == 1)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                }
            }
            yield break;
        }

        yield return "next answer";
    }
}
