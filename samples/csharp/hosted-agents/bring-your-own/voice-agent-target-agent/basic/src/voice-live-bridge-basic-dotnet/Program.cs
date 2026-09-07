// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.AI.AgentServer.Invocations.Voice;
using VoiceHostedAgent;

VoiceServer.Run<VoiceAgent>(args, builder =>
{
    builder.Services.AddSingleton<IStreamingModelClient>(
        AzureOpenAIResponsesClient.CreateFromEnvironment());
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddSource(ModelTelemetry.SourceName)
            .AddSource(ResponseTelemetry.SourceName));
});
