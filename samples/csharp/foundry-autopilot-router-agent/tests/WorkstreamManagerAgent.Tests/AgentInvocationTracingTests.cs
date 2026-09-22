namespace WorkstreamManagerAgent.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using WorkstreamManager.Services;
using Xunit;

/// <summary>
/// Covers the span shape Foundry trace-based evaluations depend on.
///
/// These assertions are deliberately literal about attribute NAMES and the span-name prefix.
/// Evaluations match on those strings, so a rename that looks harmless here is exactly the change
/// that makes a run invisible to them — which fails silently in production, because the platform
/// emits its own "invoke_agent" span and the dashboard still looks populated.
///
/// Not parallelised: content capture is read from a process-wide environment variable, so two
/// tests toggling it at once would see each other's value.
/// </summary>
[CollectionDefinition(nameof(TracingCollection), DisableParallelization = true)]
public class TracingCollection { }

[Collection(nameof(TracingCollection))]
public class AgentInvocationTracingTests : IDisposable
{
    private readonly ActivityListener _listener;

    // Concurrent, not List: ActivityStopped fires on whichever thread ended the span, and the
    // concurrency test below ends three at once. A plain List silently loses entries there, which
    // looks exactly like the product bug that test exists to catch.
    private readonly ConcurrentBag<Activity> _finished = new();
    private readonly string? _originalCapture;
    private readonly string? _originalProjectId;

    public AgentInvocationTracingTests()
    {
        _originalCapture = Environment.GetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT");
        _originalProjectId = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ARM_ID");

        // Without a listener sampling AllData, StartActivity returns null and every assertion
        // below would vacuously pass against a span that was never created.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentInvocationTracing.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _finished.Add(activity),
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", _originalCapture);
        Environment.SetEnvironmentVariable("FOUNDRY_PROJECT_ARM_ID", _originalProjectId);
    }

    private static string? Tag(Activity activity, string key) =>
        activity.GetTagItem(key) as string;

    [Fact]
    public void RootInvocation_HasRequiredGenAiAttributes()
    {
        using (var activity = AgentInvocationTracing.StartInvocation("travel", "travel:3"))
        {
            Assert.NotNull(activity);
        }

        var span = Assert.Single(_finished);

        Assert.Equal("invoke_agent travel", span.OperationName);
        Assert.Equal("invoke_agent", Tag(span, "gen_ai.operation.name"));
        Assert.Equal("travel:3", Tag(span, "gen_ai.agent.id"));
        Assert.Equal("travel", Tag(span, "gen_ai.agent.name"));
    }

    [Fact]
    public void RootInvocation_IsItsOwnMainAgent()
    {
        using (AgentInvocationTracing.StartInvocation("travel", "travel:3")) { }

        var span = Assert.Single(_finished);
        Assert.Equal("travel:3", Tag(span, "microsoft.gen_ai.main_agent.id"));
    }

    [Fact]
    public void ChildInvocation_KeepsRootMainAgentId_AndIsParentedToRoot()
    {
        using (AgentInvocationTracing.StartInvocation("travel", "travel:3"))
        {
            // Nested inside the root's scope, which is how a function-tool dispatch runs.
            using (AgentInvocationTracing.StartInvocation("travel_planner", "travel_planner:1", mainAgentId: "travel:3")) { }
        }

        Assert.Equal(2, _finished.Count);

        var child = _finished.Single(a => a.OperationName == "invoke_agent travel_planner");
        var root = _finished.Single(a => a.OperationName == "invoke_agent travel");

        // Parenting is what keeps the nested call inside the same evaluated run.
        Assert.Equal(root.TraceId, child.TraceId);
        Assert.Equal(root.SpanId, child.ParentSpanId);

        // The child identifies ITSELF as the agent, but the ROOT as the run.
        Assert.Equal("travel_planner:1", Tag(child, "gen_ai.agent.id"));
        Assert.Equal("travel:3", Tag(child, "microsoft.gen_ai.main_agent.id"));
    }

    [Fact]
    public void ResponseId_IsRecorded()
    {
        using (var activity = AgentInvocationTracing.StartInvocation("travel", "travel:3"))
        {
            AgentInvocationTracing.RecordResponseId(activity, "resp_abc123");
        }

        Assert.Equal("resp_abc123", Tag(Assert.Single(_finished), "gen_ai.response.id"));
    }

    [Fact]
    public void ResponseId_IsOmittedWhenMissing()
    {
        using (var activity = AgentInvocationTracing.StartInvocation("travel", "travel:3"))
        {
            AgentInvocationTracing.RecordResponseId(activity, null);
            AgentInvocationTracing.RecordResponseId(activity, "   ");
        }

        Assert.Null(Tag(Assert.Single(_finished), "gen_ai.response.id"));
    }

    [Fact]
    public void MessageContent_IsCaptured_WhenEnabled()
    {
        Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");

        using (var activity = AgentInvocationTracing.StartInvocation("travel", "travel:3"))
        {
            AgentInvocationTracing.RecordInput(activity, "book me a flight");
            AgentInvocationTracing.RecordOutput(activity, "booked");
        }

        var span = Assert.Single(_finished);

        var input = JsonNode.Parse(Tag(span, "gen_ai.input.messages")!)!.AsArray();
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("book me a flight", input[0]!["parts"]![0]!["content"]!.GetValue<string>());

        var output = JsonNode.Parse(Tag(span, "gen_ai.output.messages")!)!.AsArray();
        Assert.Equal("assistant", output[0]!["role"]!.GetValue<string>());
        Assert.Equal("booked", output[0]!["parts"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void MessageContent_IsOmittedButStructurePreserved_WhenDisabled()
    {
        Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "false");

        using (var activity = AgentInvocationTracing.StartInvocation("travel", "travel:3"))
        {
            AgentInvocationTracing.RecordInput(activity, "something private");
        }

        var span = Assert.Single(_finished);
        var input = JsonNode.Parse(Tag(span, "gen_ai.input.messages")!)!.AsArray();

        // The envelope must survive so the trace is still structurally scoreable...
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("text", input[0]!["parts"]![0]!["type"]!.GetValue<string>());

        // ...but the text itself must not be on the span.
        Assert.Null(input[0]!["parts"]![0]!["content"]);
        Assert.DoesNotContain("something private", Tag(span, "gen_ai.input.messages"));
    }

    [Fact]
    public void Exception_MarksSpanAsError()
    {
        using (var activity = AgentInvocationTracing.StartInvocation("travel", "travel:3"))
        {
            AgentInvocationTracing.RecordException(activity, new InvalidOperationException("model exploded"));
        }

        var span = Assert.Single(_finished);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("model exploded", span.StatusDescription);
    }

    [Fact]
    public void ProjectId_IsAttached_WhenConfigured()
    {
        Environment.SetEnvironmentVariable("FOUNDRY_PROJECT_ARM_ID", "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/a/projects/p");

        using (AgentInvocationTracing.StartInvocation("travel", "travel:3")) { }

        Assert.Equal(
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/a/projects/p",
            Tag(Assert.Single(_finished), "microsoft.foundry.project.id"));
    }

    [Fact]
    public void ProjectId_IsOmitted_WhenNotConfigured()
    {
        Environment.SetEnvironmentVariable("FOUNDRY_PROJECT_ARM_ID", null);

        using (AgentInvocationTracing.StartInvocation("travel", "travel:3")) { }

        Assert.Null(Tag(Assert.Single(_finished), "microsoft.foundry.project.id"));
    }

    [Fact]
    public void AgentId_IsStableAcrossCalls_NotRandom()
    {
        var first = AgentInvocationTracing.BuildAgentId("travel", "3");
        var second = AgentInvocationTracing.BuildAgentId("travel", "3");

        Assert.Equal("travel:3", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AgentId_FallsBackWhenVersionMissing()
    {
        Assert.Equal("travel:unknown", AgentInvocationTracing.BuildAgentId("travel", null));
        Assert.Equal("travel:unknown", AgentInvocationTracing.BuildAgentId("travel", "  "));
    }

    [Fact]
    public async Task ConcurrentInvocations_DoNotShareSpanState()
    {
        // The failure this guards against: holding the active span in a static field. Two turns
        // would then write their response ids onto whichever span was stored last.
        async Task RunAsync(string name, string responseId)
        {
            using var activity = AgentInvocationTracing.StartInvocation(name, $"{name}:1");
            await Task.Delay(Random.Shared.Next(5, 25));
            AgentInvocationTracing.RecordResponseId(activity, responseId);
        }

        await Task.WhenAll(
            RunAsync("alpha", "resp_alpha"),
            RunAsync("beta", "resp_beta"),
            RunAsync("gamma", "resp_gamma"));

        Assert.Equal(3, _finished.Count);

        foreach (var name in new[] { "alpha", "beta", "gamma" })
        {
            var span = _finished.Single(a => a.OperationName == $"invoke_agent {name}");
            Assert.Equal($"resp_{name}", Tag(span, "gen_ai.response.id"));
        }
    }

    [Fact]
    public void Helpers_TolerateNullActivity()
    {
        // StartInvocation returns null when nothing is listening. A turn must not fail for that.
        AgentInvocationTracing.RecordInput(null, "x");
        AgentInvocationTracing.RecordOutput(null, "y");
        AgentInvocationTracing.RecordResponseId(null, "resp");
        AgentInvocationTracing.RecordException(null, new InvalidOperationException("boom"));
    }
}
