using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Xunit;

namespace WorkstreamManagerAgent.Tests;

public class A2AConversationTests
{
    [Fact]
    public async Task AFollowUpUsesTheReturnedContextAcrossHandlerInstances()
    {
        using var f = new Fixture();
        var first = f.Handler();
        first.BeginTurn("chat:one");
        Assert.Equal("answer", await Ask(first, "specialist"));
        var second = f.Handler();
        second.BeginTurn("chat:one");
        Assert.Equal("answer", await Ask(second, "specialist"));
        Assert.Null(f.Wire.Calls[0].Body["params"]!["message"]!["contextId"]);
        Assert.Equal("context-1", f.Wire.Calls[1].Body["params"]!["message"]!["contextId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("channel")]
    [InlineData("agent")]
    [InlineData("tenant")]
    [InlineData("user")]
    [InlineData("identity")]
    [InlineData("endpoint")]
    public async Task AContinuationNeverCrossesItsScope(string changed)
    {
        using var f = new Fixture();
        var first = f.Handler();
        first.BeginTurn("chat:one");
        await Ask(first, "specialist");
        var metadata = f.Metadata();
        if (changed == "tenant") metadata.TenantId = Guid.NewGuid();
        if (changed == "user") metadata.UserId = Guid.NewGuid();
        if (changed == "identity") metadata.AgentId = Guid.NewGuid();
        var second = f.Handler(metadata, changed == "endpoint" ? "https://other.example.test/a2a/" : null);
        second.BeginTurn(changed == "conversation" ? "chat:two" : changed == "channel" ? "mail:one" : "chat:one");
        await Ask(second, changed == "agent" ? "another-specialist" : "specialist");
        Assert.Null(f.Wire.Calls[1].Body["params"]!["message"]!["contextId"]);
    }

    [Fact]
    public async Task ResetStartsOnlyTheSelectedSpecialistAgain()
    {
        using var f = new Fixture();
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        await Ask(handler, "first");
        await Ask(handler, "second");
        await Ask(handler, "first", reset: true);
        await Ask(handler, "second");
        Assert.Null(f.Wire.Calls[2].Body["params"]!["message"]!["contextId"]);
        Assert.Equal("context-2", f.Wire.Calls[3].Body["params"]!["message"]!["contextId"]!.GetValue<string>());
    }

    [Fact]
    public async Task StandingJobsHaveIndependentContinuationsAcrossChannels()
    {
        using var f = new Fixture();
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        await handler.AskForStandingJobAsync("job-a", "specialist", "question");
        await handler.AskForStandingJobAsync("job-b", "specialist", "question");
        var mail = f.Handler();
        mail.BeginTurn("mail:another");
        await mail.AskForStandingJobAsync("job-a", "specialist", "follow-up");
        Assert.Null(f.Wire.Calls[1].Body["params"]!["message"]!["contextId"]);
        Assert.Equal("context-1", f.Wire.Calls[2].Body["params"]!["message"]!["contextId"]!.GetValue<string>());
    }

    [Fact]
    public async Task V1UsesTheDocumentedWireShape()
    {
        using var f = new Fixture();
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        await Ask(handler, "specialist");
        var call = Assert.Single(f.Wire.Calls);
        Assert.Equal("1.0", call.Version);
        Assert.Equal("SendMessage", call.Method);
        var message = call.Body["params"]!["message"]!;
        Assert.Equal("ROLE_USER", message["role"]!.GetValue<string>());
        Assert.Null(message["kind"]);
        Assert.Null(message["parts"]![0]!["kind"]);
    }

    [Fact]
    public async Task ARejectedV1MethodNegotiatesTheMatchingLegacyHeaderAndRole()
    {
        using var f = new Fixture(call => call.Method == "SendMessage"
            ? Json("""{"error":{"code":-32601,"message":"Method not found"}}""")
            : Json("""{"result":{"id":"task","contextId":"legacy-context","artifacts":[{"parts":[{"kind":"text","text":"legacy answer"}]}]}}"""));
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        Assert.Equal("legacy answer", await Ask(handler, "specialist"));
        Assert.Equal(2, f.Wire.Calls.Count);
        Assert.Equal("0.3", f.Wire.Calls[1].Version);
        Assert.Equal("user", f.Wire.Calls[1].Body["params"]!["message"]!["role"]!.GetValue<string>());
        Assert.Equal("text", f.Wire.Calls[1].Body["params"]!["message"]!["parts"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAcceptedWorkingRequestIsQueuedWithoutBeingResent()
    {
        using var f = new Fixture(_ => Json(TaskResult("context", "", "TASK_STATE_WORKING")));
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        var answer = await Ask(handler, "specialist");
        Assert.Contains("still working", answer);
        Assert.Single(f.Wire.Calls);
        Assert.Single(handler.PendingHandoffs);
        Assert.Equal(WorkIqA2AToolHandler.DelegationOutcome.Pending, Assert.Single(handler.Delegations).Outcome);
    }

    [Fact]
    public async Task EmptyAcceptedResponsesSubscribeToTheTaskWithoutRepeatingTheInstruction()
    {
        using var f = new Fixture(call => call.Method switch
        {
            "SendMessage" or "GetTask" or "tasks/get" => Json(TaskResult("context", "")),
            "SubscribeToTask" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"result\":{\"artifactUpdate\":{\"contextId\":\"context\",\"artifact\":{\"artifactId\":\"answer\",\"parts\":[{\"text\":\"recovered\"}]}}}}\n\n",
                    Encoding.UTF8, "text/event-stream")
            },
            _ => throw new InvalidOperationException("An unexpected request was sent.")
        });
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        Assert.Equal("recovered", await Ask(handler, "specialist"));
        Assert.Single(f.Wire.Calls, call => call.Method == "SendMessage");
        Assert.DoesNotContain(f.Wire.Calls, call => call.Method is "SendStreamingMessage" or "message/stream");
        Assert.Equal("task", f.Wire.Calls.Last().Body["params"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAcceptedResponseWithoutATaskIsNotResubmitted()
    {
        using var f = new Fixture(_ => Json("""{"result":{"message":{"contextId":"context","parts":[]}}}"""));
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        Assert.Contains("returned no content", await Ask(handler, "specialist"));
        Assert.Single(f.Wire.Calls);
    }

    [Fact]
    public async Task AFailedTaskIsNotReportedAsAnAnsweredDelegation()
    {
        using var f = new Fixture(_ => Json(TaskResult("context", "Permission denied", "TASK_STATE_FAILED")));
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        Assert.StartsWith("ERROR:", await Ask(handler, "specialist"));
        Assert.Equal(WorkIqA2AToolHandler.DelegationOutcome.NoAnswer, Assert.Single(handler.Delegations).Outcome);
        Assert.Single(f.Wire.Calls);
    }

    [Fact]
    public async Task AFailureFoundDuringTaskRetrievalRemainsAFailure()
    {
        using var f = new Fixture(call => Json(call.Method == "SendMessage"
            ? TaskResult("context", "")
            : TaskResult("context", "Permission denied", "TASK_STATE_FAILED")));
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        Assert.StartsWith("ERROR:", await Ask(handler, "specialist"));
        Assert.Equal(2, f.Wire.Calls.Count);
        Assert.Equal(WorkIqA2AToolHandler.DelegationOutcome.NoAnswer, Assert.Single(handler.Delegations).Outcome);
    }

    [Fact]
    public async Task AChangedRemoteContextIsNotSilentlySubstituted()
    {
        var calls = 0;
        using var f = new Fixture(_ => Json(TaskResult("context-" + ++calls)));
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        await Ask(handler, "specialist");
        var answer = await Ask(handler, "specialist");
        Assert.Contains("Do not repeat the request", answer);
        Assert.Equal(WorkIqA2AToolHandler.DelegationOutcome.NoAnswer, handler.Delegations.Last().Outcome);
        Assert.Equal(2, f.Wire.Calls.Count);
    }

    [Theory]
    [InlineData("""{"task":{"contextId":"opaque-context"}}""")]
    [InlineData("""{"message":{"contextId":"opaque-context"}}""")]
    [InlineData("""{"contextId":"opaque-context"}""")]
    [InlineData("""{"statusUpdate":{"contextId":"opaque-context"}}""")]
    [InlineData("""{"artifactUpdate":{"contextId":"opaque-context"}}""")]
    [InlineData("""{"contextId":"opaque-context","task":{"contextId":"opaque-context"}}""")]
    public void AllDocumentedEnvelopeShapesPreserveTheOpaqueContext(string value) =>
        Assert.Equal("opaque-context", WorkIqA2AToolHandler.ExtractContextId(JsonNode.Parse(value)));

    [Theory]
    [InlineData("""{"task":{"contextId":123}}""")]
    [InlineData("""{"task":{"contextId":""}}""")]
    [InlineData("""{"contextId":"one","message":{"contextId":"two"}}""")]
    public void InvalidOrConflictingContextsAreExplicitErrors(string value) =>
        Assert.Throws<JsonException>(() => WorkIqA2AToolHandler.ExtractContextId(JsonNode.Parse(value)));

    [Fact]
    public async Task ConcurrentFirstRepliesCannotOverwriteEachOthersContinuation()
    {
        var store = new ConversationStateStore((TableClient?)null, NullLogger<ConversationStateStore>.Instance);
        await store.SaveA2AContextAsync("owner", "scope", "first", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveA2AContextAsync("owner", "scope", "second", null));
        Assert.Equal("first", await store.LoadA2AContextAsync("owner", "scope"));
    }

    [Fact]
    public async Task AStorageReadFailureDoesNotStartAStatelessRemoteRequest()
    {
        using var storageHttp = new HttpClient(new StorageHandler(failReads: true));
        using var f = new Fixture(store: Store(storageHttp));
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        Assert.Contains("call failed", await Ask(handler, "specialist"));
        Assert.Empty(f.Wire.Calls);
    }

    [Fact]
    public async Task AStorageWriteFailureWarnsThatTheRemoteRequestAlreadyRan()
    {
        using var storageHttp = new HttpClient(new StorageHandler(failWrites: true));
        using var f = new Fixture(store: Store(storageHttp));
        var handler = f.Handler();
        handler.BeginTurn("chat:one");
        Assert.Contains("Do not repeat the request", await Ask(handler, "specialist"));
        Assert.Single(f.Wire.Calls);
        Assert.Equal(WorkIqA2AToolHandler.DelegationOutcome.NoAnswer, Assert.Single(handler.Delegations).Outcome);
    }

    [Fact]
    public async Task TheAzureTablePointerSurvivesAStoreAndHandlerRecreation()
    {
        var backend = new StorageHandler();
        using var storageHttp = new HttpClient(backend);
        using var f = new Fixture(store: Store(storageHttp));
        var first = f.Handler();
        first.BeginTurn("chat:one");
        Assert.Equal("answer", await Ask(first, "specialist"));
        f.State = Store(storageHttp);
        var second = f.Handler();
        second.BeginTurn("chat:one");
        var continued = await Ask(second, "specialist");
        Assert.True(continued == "answer", continued + "\n" + string.Join("\n", f.Logger.Errors));
        Assert.Equal("context-1", f.Wire.Calls[1].Body["params"]!["message"]!["contextId"]!.GetValue<string>());
        Assert.Single(backend.Rows);
    }

    private static ConversationStateStore Store(HttpClient http)
    {
        var options = new TableClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        var client = new TableClient(new Uri("https://example.table.core.windows.net"), "conversationstate", new Credential(), options);
        return new ConversationStateStore(client, NullLogger<ConversationStateStore>.Instance);
    }

    private static Task<string?> Ask(WorkIqA2AToolHandler handler, string agent, bool reset = false) =>
        handler.TryExecuteAsync("ask_workiq_agent",
            JsonSerializer.Serialize(new { agent_id = agent, message = "question", start_new_conversation = reset }));

    private static string TaskResult(string context, string answer = "answer", string state = "TASK_STATE_COMPLETED") =>
        JsonSerializer.Serialize(new
        {
            result = new
            {
                task = new
                {
                    id = "task", contextId = context, status = new { state },
                    artifacts = new[] { new { parts = new[] { new { text = answer } } } }
                }
            }
        });

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
    {
        var content = new StreamContent(new NetworkBody(Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(code) { Content = content };
    }

    // Azure's response buffering must see a network-like stream, not StringContent's seekable buffer.
    private sealed class NetworkBody(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }

    private sealed record Call(string Method, string Version, JsonNode Body);

    private sealed class Wire(Func<Call, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"name":"specialist","defaultInputModes":["text"],"defaultOutputModes":["text"]}""");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var call = new Call(body["method"]!.GetValue<string>(), request.Headers.GetValues("A2A-Version").Single(), body);
            Calls.Add(call);
            return respond?.Invoke(call) ?? Json(TaskResult(
                body["params"]!["message"]?["contextId"]?.GetValue<string>() ?? "context-" + Calls.Count));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Guid _tenant = Guid.NewGuid(), _user = Guid.NewGuid(), _identity = Guid.NewGuid();
        private readonly HttpClient _http;
        public Wire Wire { get; }
        public CapturingLogger Logger { get; } = new();
        public ConversationStateStore State { get; set; }
        public Fixture(Func<Call, HttpResponseMessage>? respond = null, ConversationStateStore? store = null)
        {
            Wire = new Wire(respond);
            _http = new HttpClient(Wire);
            State = store ?? new ConversationStateStore((TableClient?)null, NullLogger<ConversationStateStore>.Instance);
        }
        public AgentMetadata Metadata() => new() { TenantId = _tenant, UserId = _user, AgentId = _identity };
        public WorkIqA2AToolHandler Handler(AgentMetadata? metadata = null, string? endpoint = null) =>
            new(metadata ?? Metadata(), null, Logger, _http,
                new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["WorkIqA2ABaseUrl"] = endpoint ?? "https://example.test/a2a/" }).Build(),
                State, new Credential());
        public void Dispose() => _http.Dispose();
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) =>
            new("test", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) =>
            ValueTask.FromResult(GetToken(context, token));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<Exception> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception != null) Errors.Add(exception);
        }
    }

    private sealed class StorageHandler(bool failReads = false, bool failWrites = false) : HttpMessageHandler
    {
        public Dictionary<string, JsonObject> Rows { get; } = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if ((request.Method == HttpMethod.Get && failReads) || (request.Method != HttpMethod.Get && failWrites))
                return Json("""{"odata.error":{"code":"AuthorizationFailure","message":{"lang":"en-US","value":"denied"}}}""", HttpStatusCode.Forbidden);
            if (request.Method == HttpMethod.Get)
            {
                var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
                var row = Rows.Values.SingleOrDefault(value => path.Contains($"PartitionKey='{value["PartitionKey"]}'", StringComparison.Ordinal)
                    && path.Contains($"RowKey='{value["RowKey"]}'", StringComparison.Ordinal));
                return row == null
                    ? Json("""{"odata.error":{"code":"ResourceNotFound","message":{"lang":"en-US","value":"not found"}}}""", HttpStatusCode.NotFound)
                    : Json(row.ToJsonString());
            }
            if (request.Method != HttpMethod.Post)
                throw new InvalidOperationException("Unexpected table operation in this test.");
            var entity = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            entity["odata.etag"] = "W/\"1\"";
            entity["Timestamp"] = DateTimeOffset.UtcNow.ToString("O");
            entity["Timestamp@odata.type"] = "Edm.DateTime";
            Rows.Add(entity["PartitionKey"] + "/" + entity["RowKey"], entity);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
