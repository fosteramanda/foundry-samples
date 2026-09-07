// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace VoiceHostedAgent;

public enum ModelMessageRole
{
    User,
    Assistant,
}

public sealed record ModelMessage(ModelMessageRole Role, string Content);

public interface IStreamingModelClient
{
    string ModelName { get; }

    string ServerAddress { get; }

    IAsyncEnumerable<string> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        CancellationToken cancellationToken = default);
}
