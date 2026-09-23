namespace WorkstreamManager.Services;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Emits the Foundry GenAI spans that trace-based evaluations look for.
///
/// C# counterpart of the Python sample's request_correlation.py. The shape has to match exactly,
/// because evaluations locate a run by span name and attribute names rather than by anything
/// structural: a span called "invoke_agent travel" with gen_ai.* attributes is found, and the
/// same span called anything else is invisible to them.
///
/// Why <see cref="Activity"/> and not a field holding the current span: Activity.Current is
/// backed by AsyncLocal, so it already flows correctly across awaits and stays per-request under
/// concurrency. A static "current span" field would be shared by every turn in the process and
/// two simultaneous conversations would overwrite each other's response id.
///
/// NOTE ON THE PLATFORM'S OWN SPAN. The Foundry host also emits a span named exactly
/// "invoke_agent" carrying azure.ai.agentserver.* attributes and a GUID gen_ai.agent.id. This one
/// is named "invoke_agent {agentName}" and uses a stable "{name}:{version}" id. They coexist and
/// are only distinguishable by that suffix and id shape, so do not "fix" the name to match the
/// platform's: they would become impossible to tell apart, and it is this one that carries the
/// input/output messages evaluations score.
/// </summary>
public static class AgentInvocationTracing
{
    /// <summary>Must be registered with the tracer provider or every span here is dropped silently.</summary>
    public const string ActivitySourceName = "Foundry.Agent.Invocation";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private const string OperationName = "invoke_agent";

    /// <summary>
    /// Starts the invocation span. Returns null when nothing is listening, which callers must
    /// tolerate: tracing is diagnostics, and a turn must never fail because no exporter is wired.
    /// </summary>
    /// <param name="agentName">Agent name; also the span-name suffix.</param>
    /// <param name="agentId">Stable identity, "{name}:{version}". Never a per-request GUID.</param>
    /// <param name="mainAgentId">
    /// Root invocation's identity. A root passes its own id; a child agent passes the ROOT's id so
    /// an evaluation can tell which run a nested call belongs to.
    /// </param>
    public static Activity? StartInvocation(string agentName, string agentId, string? mainAgentId = null)
    {
        var activity = Source.StartActivity($"{OperationName} {agentName}", ActivityKind.Internal);

        if (activity == null)
        {
            return null;
        }

        activity.SetTag("gen_ai.operation.name", OperationName);
        activity.SetTag("gen_ai.agent.id", agentId);
        activity.SetTag("gen_ai.agent.name", agentName);
        activity.SetTag("microsoft.gen_ai.main_agent.id", string.IsNullOrWhiteSpace(mainAgentId) ? agentId : mainAgentId);

        var projectId = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ARM_ID");
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            activity.SetTag("microsoft.foundry.project.id", projectId);
        }

        return activity;
    }

    /// <summary>Builds the stable "{name}:{version}" identity. Never call Guid.NewGuid() for this.</summary>
    public static string BuildAgentId(string agentName, string? agentVersion) =>
        $"{agentName}:{(string.IsNullOrWhiteSpace(agentVersion) ? "unknown" : agentVersion)}";

    /// <summary>
    /// Agent name and version as the Foundry host supplies them. Matches the Python sample's
    /// fallback so a locally run agent still produces a well-formed, stable identity rather than
    /// an empty one that evaluations cannot group by.
    /// </summary>
    public static string ResolveAgentName() =>
        Environment.GetEnvironmentVariable("FOUNDRY_AGENT_NAME") is { Length: > 0 } name
            ? name
            : "FoundryDigitalWorker";

    public static string ResolveAgentVersion() =>
        Environment.GetEnvironmentVariable("FOUNDRY_AGENT_VERSION") is { Length: > 0 } version
            ? version
            : "unknown";

    public static void RecordInput(Activity? activity, string? text) =>
        SetMessages(activity, "gen_ai.input.messages", "user", text);

    public static void RecordOutput(Activity? activity, string? text) =>
        SetMessages(activity, "gen_ai.output.messages", "assistant", text);

    /// <summary>Links the span to the Responses API call that produced it.</summary>
    public static void RecordResponseId(Activity? activity, string? responseId)
    {
        if (activity == null || string.IsNullOrWhiteSpace(responseId))
        {
            return;
        }

        activity.SetTag("gen_ai.response.id", responseId);
    }

    /// <summary>
    /// Marks the span failed. Kept separate from the exception filter in callers so a handled
    /// failure still shows as an error on the trace rather than a silently successful turn.
    /// </summary>
    public static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddException(ex);
    }

    /// <summary>
    /// Whether message text may be attached to spans. Off by default: this content is the user's
    /// conversation, and it ends up in whatever telemetry store the exporter points at.
    /// </summary>
    internal static bool CaptureMessageContent =>
        string.Equals(
            Environment.GetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Emits the role/parts envelope evaluations expect. When capture is disabled the envelope is
    /// still written and only the text is dropped, so a trace remains structurally scoreable
    /// instead of looking like a turn that never had any input.
    /// </summary>
    private static void SetMessages(Activity? activity, string attribute, string role, string? text)
    {
        if (activity == null)
        {
            return;
        }

        var part = new JsonObject { ["type"] = "text" };

        if (CaptureMessageContent && !string.IsNullOrEmpty(text))
        {
            part["content"] = text;
        }

        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(text))
        {
            parts.Add(part);
        }

        var payload = new JsonArray
        {
            new JsonObject { ["role"] = role, ["parts"] = parts },
        };

        activity.SetTag(attribute, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }
}
