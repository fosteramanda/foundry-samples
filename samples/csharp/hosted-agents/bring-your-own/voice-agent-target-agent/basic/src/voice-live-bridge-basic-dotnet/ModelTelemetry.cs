// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace VoiceHostedAgent;

internal static class ModelTelemetry
{
    internal const string SourceName = "VoiceHostedAgent.Model";
    internal const string TimeToFirstChunkTag = "gen_ai.response.time_to_first_chunk";
    internal const string FirstTokenLatencyTag = "voice_agent.model.first_token_latency_ms";
    internal const string ResponseDurationTag = "voice_agent.model.response_duration_ms";
    private static readonly ActivitySource s_activitySource = new(SourceName);

    internal static Activity? Start(string modelName, string serverAddress)
    {
        var activity = s_activitySource.StartActivity("chat", ActivityKind.Client);
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.provider.name", "Azure OpenAI");
        activity?.SetTag("gen_ai.request.model", modelName);
        activity?.SetTag("server.address", serverAddress);
        return activity;
    }

    internal static void RecordFirstToken(Activity? activity, TimeSpan latency)
    {
        activity?.SetTag(FirstTokenLatencyTag, latency.TotalMilliseconds);
        activity?.SetTag(TimeToFirstChunkTag, latency.TotalSeconds);
    }

    internal static void RecordCompleted(Activity? activity, TimeSpan duration)
    {
        activity?.SetTag(ResponseDurationTag, duration.TotalMilliseconds);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    internal static void RecordFailure(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", exception.GetType().FullName);
    }
}
