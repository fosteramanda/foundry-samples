using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure;
using Azure.Data.Tables;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.AgentLogic.ResponsesApi;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Xunit;

namespace WorkstreamManagerAgent.Tests;

public class StandingWorkflowTests
{
    [Fact]
    public async Task ASourceOnlyChatUpdateRunsTheBoundJobWithoutToolCoaching()
    {
        using var f = await Fixture.CreateAsync();
        f.Model.Enqueue(_ => Result(JsonSerializer.Serialize(new { action = "continue", job_id = f.Job.Id })));
        f.Model.Enqueue(request =>
        {
            Assert.DoesNotContain(request["tools"]!.AsArray(), tool => tool?["type"]?.GetValue<string>() == "mcp");
            var prompt = request["input"]!.GetValue<string>();
            var snapshot = JsonNode.Parse(prompt[prompt.IndexOf('{')..])!;
            return Call("record_standing_input", new
            {
                job_id = f.Job.Id, key = "coverage", title = "Coverage", owner_id = f.Manager.Id,
                state = "received", content = "The pilot has weekday coverage.",
                evidence_event_id = snapshot["currentEventId"]!.GetValue<string>()
            });
        });
        f.Model.Enqueue(_ => Result("<p>Weekday coverage is recorded. Weekend coverage remains open.</p>"));
        await f.Service().NewActivityReceived(f.Context("The pilot has weekday coverage."), new TurnState(), CancellationToken.None);
        Assert.True(f.Model.Count == 0, string.Join("\n", f.Adapter.Messages.Select(message => message.Text)));
        var input = Assert.Single(await f.Coordinator.RecordsAsync<StandingJobInput>(f.Job.Id, "input"));
        Assert.Equal("The pilot has weekday coverage.", input.Content);
        Assert.Equal("received", input.State);
        Assert.Single(f.Adapter.Messages);
        Assert.Empty(f.Model);
    }

    [Fact]
    public async Task AChatDeliveryThroughTheJobToolIsNotEchoedThroughTheNormalChannel()
    {
        using var f = await Fixture.CreateAsync();
        f.Model.Enqueue(_ => Result(JsonSerializer.Serialize(new { action = "continue", job_id = f.Job.Id })));
        f.Model.Enqueue(_ => Call("send_standing_message", new
        {
            job_id = f.Job.Id, purpose = "brief", delivery = "chat", subject = "Decision brief",
            body_html = "<p>The only remaining judgment is weekend coverage.</p>"
        }));
        f.Model.Enqueue(_ => Result("The same outcome would have been sent twice."));
        await f.Service().NewActivityReceived(f.Context("Here is the review update."), new TurnState(), CancellationToken.None);
        Assert.True(f.Model.Count == 0, string.Join("\n", f.Adapter.Messages.Select(message => message.Text)));
        Assert.Equal(1, f.GraphMessages);
        Assert.Empty(f.Adapter.Messages);
    }

    [Fact]
    public async Task TheRealWordMailNotificationAutomaticallyCapturesEvidenceAndRepliesOnlyInWord()
    {
        using var f = await Fixture.CreateAsync();
        await f.Coordinator.RegisterBriefAsync(f.Job.Id, f.Manager, f.Brief);
        var notes = new StandingJobEvent("notes", "chat", f.Manager.Id, "chat:review",
            "Hold launch until support ownership is confirmed.", DateTimeOffset.UtcNow, true);
        await f.Coordinator.CaptureAsync(f.Job.Id, f.Manager, notes);
        f.Model.Enqueue(request =>
        {
            var prompt = request["input"]!.GetValue<string>();
            var snapshot = JsonNode.Parse(prompt[prompt.IndexOf('{')..])!;
            Assert.Contains("Trigger: word", prompt);
            return Call("reply_standing_comment", new
            {
                job_id = f.Job.Id, comment_event_id = snapshot["currentEventId"]!.GetValue<string>(),
                source_event_id = notes.Id, source_quote = notes.Content,
                reply = "The source records a launch dependency, not a block on preparing the checklist."
            });
        });
        f.Model.Enqueue(_ => Result(""));
        var activity = f.Activity("Go to comment", channel: "email");
        var notification = new AgentNotificationActivity(activity)
        {
            EmailNotification = new EmailReference { Id = "word-mail", HtmlBody = f.NotificationHtml }
        };
        await f.Service().HandleEmailNotificationAsync(f.Context(activity), new TurnState(), notification);
        var source = Assert.Single(await f.Coordinator.RecordsAsync<StandingJobEvent>(f.Job.Id, "event"),
            item => item.Kind == "word");
        Assert.Equal("@Review Agent Why is launch blocked?", source.Content);
        Assert.Equal("36B02C00", source.CommentId);
        Assert.Equal(1, f.WordReplies);
        Assert.Equal(0, f.GraphMessages);
        Assert.Empty(f.Adapter.Messages);
        Assert.Equal("accepted", Assert.Single(await f.Coordinator.RecordsAsync<StandingJobReceipt>(f.Job.Id, "receipt")).State);
    }

    [Fact]
    public async Task EmailEvidenceUsesUniqueBodyAndNeverReattributesTheQuotedThread()
    {
        using var f = await Fixture.CreateAsync();
        f.Model.Enqueue(_ => Result("<p>Completion evidence recorded.</p>"));
        var activity = f.Activity("SDK text includes quoted older mail.", channel: "email");
        var notification = new AgentNotificationActivity(activity)
        {
            EmailNotification = new EmailReference { Id = "owner-mail", HtmlBody = "Older quoted decision must not become new evidence." }
        };
        await f.Service().HandleEmailNotificationAsync(f.Context(activity), new TurnState(), notification);
        var source = Assert.Single(await f.Coordinator.RecordsAsync<StandingJobEvent>(f.Job.Id, "event"));
        Assert.Equal("The checklist is delivered.", source.Content);
        Assert.DoesNotContain("Older", source.Content);
        Assert.Equal("mail:actual-mail-thread", source.Binding);
    }

    [Fact]
    public async Task AncillaryMentionMailDoesNotBecomeAnErrorReplyOrDuplicateWordAction()
    {
        using var f = await Fixture.CreateAsync();
        f.AncillaryNotification = true;
        var activity = f.Activity("Go to comment", channel: "email");
        var notification = new AgentNotificationActivity(activity)
        {
            EmailNotification = new EmailReference { Id = "word-mail", HtmlBody = f.NotificationHtml }
        };
        await f.Service().HandleEmailNotificationAsync(f.Context(activity), new TurnState(), notification);
        Assert.Empty(f.Adapter.Messages);
        Assert.Equal(0, f.WordReplies);
        Assert.Empty(await f.Coordinator.RecordsAsync<StandingJobEvent>(f.Job.Id, "event"));
        Assert.Empty(f.Model);
    }

    [Fact]
    public async Task TheRunnerCompletesAnOutstandingManagerDeliveryAfterTheModelStopsEarly()
    {
        using var f = await Fixture.CreateAsync();
        var source = new StandingJobEvent("support", "chat", f.Manager.Id, "chat:review",
            "Weekend coverage remains disputed.", DateTimeOffset.UtcNow, true);
        await f.Coordinator.CaptureAsync(f.Job.Id, f.Manager, source);
        await f.Coordinator.RecordInputAsync(f.Job.Id, f.Manager, new StandingJobInput("weekend", "Weekend coverage",
            f.Manager.Id, "disputed", source.Content, source.Id, DateTimeOffset.UtcNow));
        f.Work.Seed(f.Job.Id, f.Manager.Id, "closed", DateTimeOffset.UtcNow);
        f.Model.Enqueue(_ => Result(""));
        f.Model.Enqueue(request =>
        {
            Assert.Contains("remaining manager decision delivery", request["input"]!.GetValue<string>());
            return Call("send_standing_message", new
            {
                job_id = f.Job.Id, purpose = "escalation", delivery = "chat", subject = "Weekend coverage",
                body_html = "<p>Keep the launch hold to avoid uncovered support, or authorize the risk to keep the date.</p>"
            });
        });
        f.Model.Enqueue(_ => Result(""));
        var activity = f.Activity(StandingReviewCoordinator.Marker(f.Job.Id) + " Check the review.");
        activity.Id = null;
        await f.Service().NewActivityReceived(f.Context(activity), new TurnState(), CancellationToken.None);
        Assert.Equal(1, f.GraphMessages);
        Assert.Empty(f.Adapter.Messages);
        Assert.Empty(f.Model);
        f.Model.Enqueue(_ => Result(""));
        await f.Service().NewActivityReceived(f.Context(activity), new TurnState(), CancellationToken.None);
        Assert.Equal(1, f.GraphMessages);
        Assert.Empty(f.Model);
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("read")]
    public void RouterCannotSelectAnUnofferedJob(string action) =>
        Assert.Throws<UnauthorizedAccessException>(() => StandingConversationRoute.Parse(
            JsonSerializer.Serialize(new { action, job_id = Guid.NewGuid() }), [], true));

    [Fact]
    public void RouterCannotResumePausedWorkOrConfigureForAMember()
    {
        var job = new StandingJob { Id = Guid.NewGuid().ToString(), Paused = true };
        Assert.Throws<InvalidOperationException>(() => StandingConversationRoute.Parse(
            JsonSerializer.Serialize(new { action = "continue", job_id = job.Id }), [job], true));
        Assert.Throws<UnauthorizedAccessException>(() => StandingConversationRoute.Parse(
            """{"action":"configure","job_id":null}""", [job], false));
    }

    [Fact]
    public void BriefRendersReadableOwnedWorkAndOnlyCitedEvidence()
    {
        var manager = Guid.NewGuid().ToString();
        var job = new StandingJob { Title = "Leadership review", Members = [new(manager, "manager@example.com", "Review owner")] };
        var source = new StandingJobEvent("notes", "chat", manager, "chat:review", "The checklist is ready.", DateTimeOffset.UtcNow, true);
        var unrelated = source with { Id = "debug", Content = "Internal operator instructions and a secret-like debug receipt." };
        var inputs = new[] { new StandingJobInput("checklist", "Readiness checklist", manager, "received", source.Content, source.Id, DateTimeOffset.UtcNow) };
        var taskId = Guid.NewGuid().ToString();
        var tasks = JsonSerializer.SerializeToNode(new[] { new
        {
            id = taskId, name = "Publish readiness note", ownerAadObjectId = manager,
            status = "closed", eta = "2026-10-01T12:00:00Z", completionEvidence = "notes: The checklist is ready."
        } })!.AsArray();
        var html = StandingBriefRenderer.Render(job, 2, "The checklist is ready.", "Confirm coverage?", ["Hold until confirmed."],
            ["Resolve coverage, then confirm the launch gate."], inputs, [], tasks, [], [source, unrelated], []);
        Assert.Contains("Review owner", html);
        Assert.Contains("1 Oct 2026, 12:00 UTC", html);
        Assert.DoesNotContain(taskId, html);
        Assert.DoesNotContain(unrelated.Content, html);
        Assert.DoesNotContain("<table", html);
        Assert.Contains("font-weight:normal", html);
    }

    [Fact]
    public void ProductionTaskProjectionPreservesTheOwnerIdentityUsedByTheBrief()
    {
        var manager = Guid.NewGuid().ToString();
        var job = new StandingJob { Title = "Review", Members = [new(manager, "manager@example.com", "Review owner")] };
        var entity = new WorkItemEntity
        {
            RowKey = Guid.NewGuid().ToString(), Name = "Complete the readiness checklist",
            Owner = "manager@example.com", OwnerAadObjectId = manager, Status = "open",
            ETA = "2026-10-01T12:00:00Z", DependencyIdsJson = "[]"
        };
        var tasks = new JsonArray(JsonSerializer.SerializeToNode(WorkItemService.ToListItem(entity)));
        Assert.Equal(manager, tasks[0]!["ownerAadObjectId"]!.GetValue<string>());
        var html = StandingBriefRenderer.Render(job, 1, "Readiness is in progress.", "",
            [], [], [], [], tasks, [], [], []);
        Assert.Contains("Review owner", html);
        Assert.DoesNotContain("Owner not confirmed", html);
    }

    private static JsonNode Result(string text) => JsonSerializer.SerializeToNode(new
    {
        id = Guid.NewGuid().ToString("N"), status = "completed",
        output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } }
    })!;

    private static JsonNode Call(string name, object arguments) => JsonSerializer.SerializeToNode(new
    {
        id = Guid.NewGuid().ToString("N"), status = "completed",
        output = new[] { new { type = "function_call", call_id = Guid.NewGuid().ToString("N"), name, arguments = JsonSerializer.Serialize(arguments) } }
    })!;

    private sealed class Fixture : IDisposable
    {
        public StandingJobTests.MemoryStore Store { get; } = new();
        public StandingJobTests.MemoryWorkItems Work { get; } = new();
        public RecordingAdapter Adapter { get; } = new();
        public Queue<Func<JsonNode, JsonNode>> Model { get; } = new();
        public AgentMetadata Agent { get; } = new() { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        public StandingJobCaller Manager { get; }
        public StandingJob Job { get; private set; }
        public StandingJobBrief Brief { get; }
        public StandingReviewCoordinator Coordinator { get; }
        public string NotificationHtml { get; }
        public int GraphMessages { get; private set; }
        public int WordReplies { get; private set; }
        public bool AncillaryNotification { get; set; }
        private readonly HttpClient _http;
        private readonly IConfiguration _configuration;
        private ResponsesApiAgentLogicService? _service;

        private Fixture()
        {
            var manager = Guid.NewGuid().ToString();
            Manager = new(manager, manager, "manager@example.com");
            Coordinator = new(Store, StandingJobStore.Partition(Agent.TenantId, Agent.UserId), NullLogger.Instance);
            Job = new()
            {
                Id = Guid.NewGuid().ToString(), Title = "Leadership review", Mandate = "Own preparation and follow-through.",
                Members = [new(manager, Manager.Email!, "Review owner")], Recipients = [Manager.Email!],
                ConversationId = "19:review", CronExpression = "*/5 * * * *"
            };
            var documentId = Guid.NewGuid();
            Brief = new(1, "Review v1.docx", "word-item", documentId.ToString("D"),
                $"https://tenant.sharepoint.com/Doc.aspx?sourcedoc={documentId:D}", "facts", DateTimeOffset.UtcNow);
            NotificationHtml = $"<a href=\"https://tenant.sharepoint.com/review.docx?d=w{documentId:N}&amp;nav=eyJjIjo5MTc1MTUyNjR9\">Go to comment</a>";
            _http = new HttpClient(new Handler(Respond));
            _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAIEndpoint"] = "https://model.example.com",
                ["ModelDeployment"] = "test",
                ["EnableStreamingUpdates"] = "false",
                ["EnablePassiveWorkItemDetection"] = "false"
            }).Build();
        }

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f.Coordinator.CreateAsync(f.Job, f.Manager);
            f.Job = await f.Coordinator.ChangeAsync(f.Job.Id, f.Manager, job => job.Paused = false);
            return f;
        }

        public ResponsesApiAgentLogicService Service() => _service ??= new(Agent, _configuration, NullLogger.Instance,
            "word-token", [new McpServerConfig { McpServerName = "mcp_WordServer", Url = "https://word.example.com" }],
            "graph-token", standingJobs: Store, transport: _http, workItemStore: Work,
            responseCredential: new Credential(), allowListTable: new SeededAllowList());

        public Activity Activity(string text, string channel = "msteams") => new()
        {
            Type = ActivityTypes.Message, Id = Guid.NewGuid().ToString(), Text = text, ChannelId = channel,
            Timestamp = DateTimeOffset.UtcNow,
            From = new ChannelAccount { Id = Manager.Id, AadObjectId = Manager.Id, Name = "Review owner" },
            Recipient = new ChannelAccount { Id = Agent.UserId.ToString(), AadObjectId = Agent.UserId.ToString() },
            Conversation = new ConversationAccount { Id = "19:review", ConversationType = "personal", TenantId = Agent.TenantId.ToString() },
            ServiceUrl = "https://channel.example.com"
        };

        public TurnContext Context(string text) => Context(Activity(text));
        public TurnContext Context(IActivity activity) => new(Adapter, activity, new ClaimsIdentity());

        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            var body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync());
            var uri = request.RequestUri!;
            if (uri.Host == "model.example.com")
            {
                Assert.NotEmpty(Model);
                return Reply(Model.Dequeue()(body!));
            }
            if (uri.Host == "word.example.com")
            {
                return body!["method"]!.GetValue<string>() switch
                {
                    "initialize" => Reply(new { result = new { protocolVersion = "2025-03-26" } }),
                    "notifications/initialized" => new(HttpStatusCode.Accepted),
                    "tools/list" => Reply(new { result = new { tools = new[] { new { name = "GetDocumentContent" }, new { name = "ReplyToComment" } } } }),
                    "tools/call" => Word(body["params"]!),
                    _ => throw new InvalidOperationException("Unexpected Word request.")
                };
            }
            if (uri.AbsolutePath.EndsWith("/setReaction", StringComparison.Ordinal)) return new(HttpStatusCode.NoContent);
            if (uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                GraphMessages++;
                return Reply(new { id = "actual-chat-message" });
            }
            if (uri.AbsolutePath.Contains("/messages/", StringComparison.Ordinal))
            {
                var isWord = uri.AbsolutePath.EndsWith("/word-mail", StringComparison.Ordinal);
                return Reply(new
                {
                    id = isWord ? "word-mail" : "owner-mail",
                    internetMessageId = isWord
                        ? AncillaryNotification ? "<MentionNotification-test@example.com>" : "<CommentWord-test@odspnotify>"
                        : "<owner-mail@example.com>",
                    from = new { emailAddress = new { address = Manager.Email } }, receivedDateTime = DateTimeOffset.UtcNow,
                    body = new { content = isWord ? NotificationHtml : "Older quoted decision." },
                    uniqueBody = new { content = "<p>The checklist is delivered.</p>" },
                    conversationId = "actual-mail-thread", subject = StandingReviewCoordinator.Marker(Job.Id)
                });
            }
            if (uri.AbsolutePath.EndsWith("/me/manager", StringComparison.Ordinal)
                || uri.AbsolutePath.Contains("/users/", StringComparison.Ordinal))
                return Reply(new { id = Manager.Id, displayName = "Review owner", mail = Manager.Email, userPrincipalName = Manager.Email });
            throw new InvalidOperationException("Unexpected Graph request: " + uri.AbsolutePath);
        }

        private HttpResponseMessage Word(JsonNode parameters)
        {
            if (parameters["name"]!.GetValue<string>() == "GetDocumentContent")
                return Reply(new { result = new { structuredContent = new
                {
                    documentId = "word-item", driveId = "word-drive", content = "Hold launch until support ownership is confirmed.",
                    comments = "Comment 36B02C00: @Review Agent Why is launch blocked?\r\nAnchor 36B02C00: Review\r\n"
                } } });
            WordReplies++;
            Assert.Equal("36B02C00", parameters["arguments"]!["commentId"]!.GetValue<string>());
            return Reply(new { result = new { content = new[] { new { type = "text", text = "WordCommentInfo [CommentId=ABC01234, Content=Reply]" } } } });
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class RecordingAdapter : ChannelAdapter
    {
        public List<IActivity> Messages { get; } = [];
        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext context, IActivity[] activities, CancellationToken token)
        {
            Messages.AddRange(activities);
            return Task.FromResult(activities.Select(_ => new ResourceResponse(Guid.NewGuid().ToString())).ToArray());
        }
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) => new("test", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) => ValueTask.FromResult(GetToken(context, token));
    }

    private sealed class SeededAllowList() : TableClient(new Uri("https://storage.example.com"), "allowlist", new Credential())
    {
        public override Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(string partitionKey, string rowKey,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            var entity = new TableEntity(partitionKey, rowKey)
            {
                ["UsersJson"] = "[]", ["ManagerOnboardingSentAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1)
            };
            if (entity is not T typed) throw new InvalidOperationException("Unexpected allowlist entity type.");
            return Task.FromResult<NullableResponse<T>>(Response.FromValue(typed, null!));
        }
    }

    private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request);
    }
}
