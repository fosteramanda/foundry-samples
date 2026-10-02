using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.AgentLogic.ResponsesApi;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Xunit;

namespace WorkstreamManagerAgent.Tests;

public class DelegationCardTests
{
    private const string Preview = """
        <m-planner-task-list plan='{"draftMode":"newPlan","title":"Reviewed plan","link":""}'
        tasks='[{"title":"Reviewed task"}]'></m-planner-task-list>
        """;
    private const string Saved = """
        <m-planner-task-list plan='{"title":"Reviewed plan","link":"https://planner.cloud.microsoft/webui/plan/PLAN1/view/board"}'
        tasks='[{"title":"Reviewed task","link":"https://planner.cloud.microsoft/webui/plan/PLAN1/view/board/task/TASK1"}]'></m-planner-task-list>
        """;
    private const string Readback = """{"planId":"PLAN1","planTitle":"Reviewed plan","tasks":[{"id":"TASK1","title":"Reviewed task"}]}""";

    [Fact]
    public void WorkEmailExecutesBeforeReplyingAndDescribesOnlyTheVerifiedTeamsRoute()
    {
        var prompt = ResponsesApiAgentLogicService.BuildEmailWorkPrompt(
            "owner@example.com", "Release review", "Prepare a Planner preview from the recorded decisions.", true, "");
        Assert.Contains("carry out the authorized work", prompt);
        Assert.Contains("Do not merely acknowledge", prompt);
        Assert.Contains("automatically post native delegation cards", prompt);
        Assert.Contains("leave Save to the authorized approval action", prompt);
        Assert.Contains("Prepare a Planner preview from the recorded decisions.", prompt);
        Assert.DoesNotContain("P_b2f25", prompt);
    }

    [Fact]
    public void WorkEmailDoesNotPromiseTeamsDeliveryWithoutARecordedRoute()
    {
        var prompt = ResponsesApiAgentLogicService.BuildEmailWorkPrompt(
            "owner@example.com", "Question", "Hello", false, "");
        Assert.Contains("No verified Teams route is available", prompt);
        Assert.DoesNotContain("automatically post native delegation cards", prompt);
        Assert.Contains("informational email does not require creating work", prompt);
    }

    [Fact]
    public void EmailOriginComesFromTheActualParentScopeNotSpecialistText()
    {
        var card = Card();
        card.ParentScope = "mail:actual-thread";
        Assert.Contains("\"title\":\"Source\",\"value\":\"Email\"", DelegationCardPresentation.Build(card).ToJsonString());
        card.ParentScope = "chat:actual-chat";
        card.Question = "An email said something";
        Assert.DoesNotContain("\"title\":\"Source\",\"value\":\"Email\"", DelegationCardPresentation.Build(card).ToJsonString());
    }

    [Fact]
    public void NativeCardContainsReadablePreviewAndOnlyOpaqueActionReferences()
    {
        var parsed = DelegationCardPresentation.ReadPlanner(Preview)!;
        var card = Card();
        card.State = DelegationCardStates.Review;
        card.Answer = Preview;
        card.PreviewJson = JsonSerializer.Serialize(parsed);
        var activity = DelegationCardPresentation.Activity(card);
        Assert.Equal("application/vnd.microsoft.card.adaptive", Assert.Single(activity.Attachments).ContentType);
        var content = Assert.IsType<JsonObject>(activity.Attachments[0].Content);
        Assert.Equal("1.5", content["version"]!.GetValue<string>());
        Assert.Contains("Reviewed plan", content.ToJsonString());
        Assert.Contains("Reviewed task", content.ToJsonString());
        var actions = content["actions"]!.AsArray().Where(action => action!["type"]!.GetValue<string>() == "Action.Submit").ToArray();
        Assert.Equal(2, actions.Length);
        foreach (var action in actions)
        {
            var data = action!["data"]!.AsObject();
            Assert.Equal(new[] { "action", "id", "kind" }, data.Select(pair => pair.Key).Order().ToArray());
            Assert.DoesNotContain("ContextId", data.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AgentId", data.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ApostrophesInTheReviewDoNotBreakAttributeParsing()
    {
        var preview = Preview.Replace("Reviewed plan", "Leaders' plan", StringComparison.Ordinal);
        Assert.Equal("Leaders' plan", DelegationCardPresentation.ReadPlanner(preview)!.Title);
    }

    [Fact]
    public void ChatFallbackRemovesRawAndHtmlEncodedPlannerMarkup()
    {
        var text = "Review is in the card. " + Preview + " Nothing saved yet.";
        Assert.DoesNotContain("m-planner-task-list", DelegationCardPresentation.WithoutPlannerMarkup(text));
        Assert.DoesNotContain("m-planner-task-list", DelegationCardPresentation.WithoutPlannerMarkup(
            System.Net.WebUtility.HtmlEncode(text)));
    }

    [Fact]
    public void TaskStatusIsReadableAndTheFullWorkRequestRemainsReachable()
    {
        var card = Card();
        card.State = DelegationCardStates.Saved;
        card.Answer = Saved;
        card.Question = new string('q', 400);
        card.PreviewJson = JsonSerializer.Serialize(DelegationCardPresentation.ReadPlanner(
            Saved.Replace("\"title\":\"Reviewed task\"", "\"title\":\"Reviewed task\",\"percentComplete\":0,\"priority\":\"Medium\"", StringComparison.Ordinal)));
        var rendered = DelegationCardPresentation.Build(card);
        Assert.Contains("Not started", rendered.ToJsonString());
        Assert.DoesNotContain("percentComplete:", rendered.ToJsonString());
        Assert.Contains("Work details", rendered.ToJsonString());
        Assert.Contains(new string('q', 400), rendered.ToJsonString());
    }

    [Theory]
    [InlineData("https://evil.example/webui/plan/PLAN1/view/board")]
    [InlineData("http://planner.cloud.microsoft/webui/plan/PLAN1/view/board")]
    [InlineData("https://planner.cloud.microsoft.evil.example/webui/plan/PLAN1/view/board")]
    [InlineData("https://person@planner.cloud.microsoft/webui/plan/PLAN1/view/board")]
    [InlineData("https://planner.cloud.microsoft/webui/plan/PLAN1/view/board?redirect=elsewhere")]
    [InlineData("javascript:alert(1)")]
    public void UnsafeOrUnexpectedPlanLinksAreRejected(string url) =>
        Assert.Throws<FormatException>(() => DelegationCardPresentation.ReadPlannerUrl(url));

    [Fact]
    public void HiddenUnsupportedPreviewFieldsCannotReceiveAnApprovalButton()
    {
        var raw = Preview.Replace("\"link\":\"\"", "\"link\":\"\",\"groupId\":\"unreviewed-group\"", StringComparison.Ordinal);
        Assert.False(DelegationCardPresentation.ReadPlanner(raw)!.CanApprove);
    }

    [Theory]
    [InlineData("another-chat", "requester", "manager")]
    [InlineData("chat-one", "different-user", "manager")]
    [InlineData("chat-one", "requester", "new-manager")]
    public void ACardCannotAuthorizeAnotherConversationActorOrManager(string conversation, string actor, string manager)
    {
        var owner = Owner();
        var card = Card(owner);
        Assert.Throws<UnauthorizedAccessException>(() => DelegationCardCoordinator.Authorize(
            card, owner, conversation, new StandingJobCaller(actor, manager, null)));
    }

    [Fact]
    public void OriginalRequesterAndCurrentManagerCanReviewTheirOwnCard()
    {
        var owner = Owner();
        var card = Card(owner);
        card.RequesterId = Guid.NewGuid().ToString("D");
        card.ManagerId = Guid.NewGuid().ToString("D");
        DelegationCardCoordinator.Authorize(card, owner, "chat-one", new StandingJobCaller(card.RequesterId, card.ManagerId, null));
        DelegationCardCoordinator.Authorize(card, owner, "chat-one", new StandingJobCaller(card.ManagerId, card.ManagerId, null));
    }

    [Fact]
    public void AClientCannotSubstituteAnAgentOrApprovalText()
    {
        var action = new { kind = DelegationCardPresentation.ActionKind, id = Guid.NewGuid().ToString("N"),
            action = "approve", agent_id = "different-agent", message = "Write something else" };
        Assert.Throws<ArgumentException>(() => DelegationCardPresentation.ReadAction(action));
    }

    [Fact]
    public void APreviewIsNotPresentedAsSavedAndLargeCardsDoNotSilentlyDropContent()
    {
        var card = Card();
        card.State = DelegationCardStates.Review;
        card.Answer = Preview;
        card.PreviewJson = JsonSerializer.Serialize(DelegationCardPresentation.ReadPlanner(Preview));
        var json = DelegationCardPresentation.Build(card).ToJsonString();
        Assert.DoesNotContain("\"title\":\"Open plan\"", json);
        card.Question = new string('x', 30_000);
        Assert.Throws<InvalidOperationException>(() => DelegationCardPresentation.Build(card));
    }

    [Fact]
    public async Task RealLifecycleUpdatesTheSameCardAndReadsBackBeforeCallingItSaved()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        var final = await f.Store.GetAsync(f.Partition, id);
        Assert.Equal(DelegationCardStates.Saved, final!.State);
        Assert.Equal(1, f.Wire.SaveCalls);
        Assert.Equal(1, f.Wire.ReadCalls);
        Assert.Single(f.Sent);
        Assert.True(f.Updated.Count >= 3);
        Assert.All(f.Updated, update => Assert.Equal("card-message", update.Id));
        Assert.Contains("Open plan", JsonSerializer.Serialize(f.Updated.Last().Attachments[0].Content));
        Assert.DoesNotContain("Reviewed plan", f.Wire.ReadQuestion);
        Assert.DoesNotContain("Reviewed task", f.Wire.ReadQuestion);
        Assert.Equal("context-1", f.Wire.SaveContext);
    }

    [Fact]
    public async Task DuplicateApprovalCannotIssueASecondSave()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        Assert.Equal(1, f.Wire.SaveCalls);
        Assert.Contains(f.Notices, notice => notice.Contains("already saved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConcurrentApprovalsClaimTheReceiptBeforeTheRemoteWrite()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        f.Wire.SaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Wire.AllowSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Coordinator.HandleActionAsync(Action(id, "approve"));
        await f.Wire.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        f.Wire.AllowSave.SetResult();
        await first;
        Assert.Equal(1, f.Wire.SaveCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewerSpecialistTurnInvalidatesAnOldApproval(bool newConversation)
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        await f.Router.TryExecuteAsync("ask_workiq_agent",
            JsonSerializer.Serialize(new { agent_id = "planner", message = "Prepare a revised preview", start_new_conversation = newConversation }));
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        Assert.Equal(0, f.Wire.SaveCalls);
        Assert.Equal(DelegationCardStates.Stale, (await f.Store.GetAsync(f.Partition, id))!.State);
    }

    [Fact]
    public async Task DeclineDoesNotSendAnyRemoteInstruction()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        var before = f.Wire.Calls;
        await f.Coordinator.HandleActionAsync(Action(id, "decline"));
        Assert.Equal(before, f.Wire.Calls);
        Assert.Equal(DelegationCardStates.Declined, (await f.Store.GetAsync(f.Partition, id))!.State);
    }

    [Fact]
    public async Task AStorageClaimFailureCannotSendSave()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        f.Store.FailReplace = true;
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        Assert.Equal(0, f.Wire.SaveCalls);
    }

    [Fact]
    public async Task AnUncertainSaveIsNotOfferedForAutomaticRetry()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        f.Wire.FailSave = true;
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        Assert.Equal(1, f.Wire.SaveCalls);
        Assert.Equal(DelegationCardStates.Unconfirmed, (await f.Store.GetAsync(f.Partition, id))!.State);
    }

    [Fact]
    public async Task APreviewReturnedAfterApprovalIsNotCalledSaved()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        f.Wire.SaveReturnsPreview = true;
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        Assert.Equal(DelegationCardStates.Unconfirmed, (await f.Store.GetAsync(f.Partition, id))!.State);
        Assert.Equal(0, f.Wire.ReadCalls);
    }

    [Fact]
    public async Task AMismatchingReadbackCannotBecomeASavedCard()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        f.Wire.BadReadback = true;
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        Assert.Equal(DelegationCardStates.Unconfirmed, (await f.Store.GetAsync(f.Partition, id))!.State);
    }

    [Fact]
    public async Task PendingSaveKeepsTheOriginalReviewAndCannotBeApprovedAgain()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        f.Wire.PendingSave = true;
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        var card = (await f.Store.GetAsync(f.Partition, id))!;
        Assert.Equal(DelegationCardStates.Pending, card.State);
        Assert.True(card.ApprovalIssued);
        Assert.NotEmpty(card.ReviewedPreviewJson);
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        Assert.Equal(1, f.Wire.SaveCalls);
        f.Wire.PendingSave = false;
        await f.Coordinator.CompletePendingAsync(card, Saved, false);
        Assert.Equal(DelegationCardStates.Saved, (await f.Store.GetAsync(f.Partition, id))!.State);
    }

    [Fact]
    public async Task SlowVerificationIsBoundToTheOriginalCardAndNeverRepeatsSave()
    {
        using var f = new Fixture();
        var id = await f.PreviewAsync();
        f.Wire.PendingRead = true;
        await f.Coordinator.HandleActionAsync(Action(id, "approve"));
        var card = (await f.Store.GetAsync(f.Partition, id))!;
        Assert.Equal(DelegationCardStates.Verifying, card.State);
        var pending = Assert.Single(f.Router.PendingHandoffs);
        Assert.True(pending.CardVerification);
        Assert.Equal(id, pending.CardId);
        await f.Coordinator.CompleteVerificationAsync(card, Readback, false);
        Assert.Equal(DelegationCardStates.Saved, (await f.Store.GetAsync(f.Partition, id))!.State);
        Assert.Equal(1, f.Wire.SaveCalls);
    }

    [Fact]
    public async Task ACardUpdateFailurePreservesTheActualTextAnswerAndSignalsTheFallback()
    {
        using var f = new Fixture();
        f.FailUpdate = true;
        Assert.Equal(Preview, await f.Router.TryExecuteAsync("ask_workiq_agent",
            JsonSerializer.Serialize(new { agent_id = "planner", message = "Prepare a plan" })));
        Assert.True(f.Router.CardDeliveryFailed);
        Assert.Single(f.Sent);
    }

    [Fact]
    public void DurableProactiveReferenceKeepsTheAuthenticatedConversationButRejectsAnotherTenant()
    {
        var tenant = Guid.NewGuid().ToString();
        var reference = new Activity
        {
            ChannelId = "msteams", ServiceUrl = "https://example.test/connector",
            Conversation = new ConversationAccount { Id = "chat-one", TenantId = tenant },
            Recipient = new ChannelAccount { Id = "agent" }, From = new ChannelAccount { Id = "requester" }
        }.GetConversationReference();
        var conversation = new Conversation(new ClaimsIdentity([new Claim("aud", "agent-app"), new Claim("tid", tenant)]), reference);
        var restored = DelegationCardChannel.ReadConversation(conversation.ToJson(), "chat-one", tenant);
        Assert.Equal("chat-one", restored.Reference.Conversation.Id);
        Assert.Equal("agent-app", restored.Identity.FindFirst("aud")!.Value);
        Assert.Throws<UnauthorizedAccessException>(() => DelegationCardChannel.ReadConversation(
            conversation.ToJson(), "chat-one", Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task EmailRoutesAreBoundToTheSameRequesterAndCallingInstance()
    {
        var store = new MemoryStore();
        await store.SaveRouteAsync(new DelegationCardRouteEntity
        {
            PartitionKey = "owner", OwnerAgentId = "agent-a", RequesterId = "person-a", ConversationId = "private-a"
        });
        Assert.Equal("private-a", (await store.GetRouteAsync("owner", "person-a", "agent-a"))!.ConversationId);
        Assert.Null(await store.GetRouteAsync("owner", "person-b", "agent-a"));
        Assert.Null(await store.GetRouteAsync("other-owner", "person-a", "agent-a"));
        Assert.Null(await store.GetRouteAsync("owner", "person-a", "agent-b"));
    }

    [Fact]
    public void ReceiptLookupRequiresBothExactCorrelationAndTheSendingAgentUser()
    {
        var owner = Guid.NewGuid();
        var marker = "delegation-card-" + Guid.NewGuid().ToString("N");
        using var messages = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                new { id = "wrong-owner", from = new { user = new { id = Guid.NewGuid() } },
                    attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive",
                        content = JsonSerializer.Serialize(new { body = new[] { new { id = marker } } }) } } },
                new { id = "wrong-card", from = new { user = new { id = owner } },
                    attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive",
                        content = JsonSerializer.Serialize(new { body = new[] { new { id = "another-card" } } }) } } },
                new { id = "system-message", from = new { user = (object?)null, application = new { id = owner } },
                    attachments = Array.Empty<object>() },
                new { id = "exact-card", from = new { user = new { id = owner } },
                    attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive",
                        content = JsonSerializer.Serialize(new { body = new[] { new { id = marker } } }) } } }
            }
        }));
        Assert.Equal("exact-card", Assert.Single(DelegationCardReceipt.Find(messages.RootElement, marker, owner)));
    }

    [Fact]
    public void CardOnlyActivityAvoidsTheAgenticTextAndAttachmentSplit()
    {
        var state = Card();
        var activity = DelegationCardPresentation.Activity(state);
        Assert.True(string.IsNullOrEmpty(activity.Text));
        var attachment = Assert.Single(activity.Attachments);
        Assert.Equal("delegation-" + state.RowKey, attachment.Name);
        var card = Assert.IsType<JsonObject>(attachment.Content);
        Assert.Equal(attachment.Name, card["body"]![0]!["id"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(card["fallbackText"]!.GetValue<string>()));
    }

    private static object Action(string id, string action) => new { kind = DelegationCardPresentation.ActionKind, id, action };
    private static AgentMetadata Owner() => new() { TenantId = Guid.NewGuid(), UserId = Guid.NewGuid(), AgentId = Guid.NewGuid() };
    private static DelegationCardEntity Card(AgentMetadata? owner = null)
    {
        owner ??= Owner();
        return new DelegationCardEntity
        {
            PartitionKey = $"{owner.TenantId:D}:{owner.UserId:D}", RowKey = "card-" + Guid.NewGuid().ToString("N"),
            OwnerAgentId = owner.AgentId.ToString("D"), OwnerName = "Review agent", ConversationId = "chat-one",
            AgentName = "Planner", Question = "Prepare the follow-through", RequesterId = "requester", ManagerId = "manager"
        };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        internal AgentMetadata Owner { get; } = DelegationCardTests.Owner();
        internal MemoryStore Store { get; } = new();
        internal Wire Wire { get; } = new();
        internal WorkIqA2AToolHandler Router { get; }
        internal DelegationCardCoordinator Coordinator { get; }
        internal List<Activity> Sent { get; } = [];
        internal List<Activity> Updated { get; } = [];
        internal List<string> Notices { get; } = [];
        internal bool FailUpdate { get; set; }
        internal string Partition => $"{Owner.TenantId:D}:{Owner.UserId:D}";
        internal Fixture()
        {
            _http = new HttpClient(Wire);
            var contexts = new ConversationStateStore((TableClient?)null, NullLogger<ConversationStateStore>.Instance);
            Router = new WorkIqA2AToolHandler(Owner, null, NullLogger.Instance, _http,
                new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["WorkIqA2ABaseUrl"] = "https://example.test/a2a/" }).Build(),
                contexts, new Credential(), Store);
            Router.BeginTurn("chat:chat-one");
            Coordinator = new DelegationCardCoordinator(Owner, Store, Router, "chat-one", "{}", "Review agent",
                new StandingJobCaller("requester", "manager", null),
                activity => { Sent.Add(activity); return Task.FromResult("card-message"); },
                activity =>
                {
                    if (FailUpdate) throw new HttpRequestException("channel unavailable");
                    Updated.Add(activity);
                    return Task.CompletedTask;
                },
                text => { Notices.Add(text); return Task.CompletedTask; }, null, NullLogger.Instance);
            Router.Observer = Coordinator.ObserveAsync;
        }
        internal async Task<string> PreviewAsync()
        {
            Assert.Equal(Preview, await Router.TryExecuteAsync("ask_workiq_agent",
                JsonSerializer.Serialize(new { agent_id = "planner", message = "Prepare a plan" })));
            return Store.Cards.Values.Single().RowKey["card-".Length..];
        }
        public void Dispose() => _http.Dispose();
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken token) =>
            new("test", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken token) =>
            ValueTask.FromResult(GetToken(requestContext, token));
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal int Calls, SaveCalls, ReadCalls;
        internal string? SaveContext, ReadQuestion;
        internal bool FailSave, SaveReturnsPreview, BadReadback, PendingSave, PendingRead;
        internal TaskCompletionSource? SaveStarted, AllowSave;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return Reply("""{"name":"Planner"}""");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("SendMessage", body["method"]!.GetValue<string>());
            var message = body["params"]!["message"]!;
            var text = message["parts"]![0]!["text"]!.GetValue<string>();
            Calls++;
            var context = message["contextId"]?.GetValue<string>() ?? "context-" + Calls;
            var answer = Preview;
            var pending = false;
            if (text == "Save")
            {
                SaveCalls++;
                SaveContext = context;
                SaveStarted?.TrySetResult();
                if (AllowSave != null) await AllowSave.Task.WaitAsync(token);
                if (FailSave) throw new HttpRequestException("The connection closed after sending.");
                answer = SaveReturnsPreview ? Preview : Saved;
                pending = PendingSave;
            }
            else if (text.StartsWith("Read only. Retrieve existing Planner plan", StringComparison.Ordinal))
            {
                ReadCalls++;
                ReadQuestion = text;
                answer = BadReadback ? Readback.Replace("Reviewed task", "Unexpected task", StringComparison.Ordinal) : Readback;
                pending = PendingRead;
            }
            return Reply(JsonSerializer.Serialize(new
            {
                result = new { task = new { id = "remote-task", contextId = context,
                    status = new { state = pending ? "TASK_STATE_WORKING" : "TASK_STATE_COMPLETED" },
                    artifacts = new[] { new { parts = new[] { new { text = pending ? "" : answer } } } } } }
            }));
        }
        private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class MemoryStore : IDelegationCardStore
    {
        internal Dictionary<string, DelegationCardEntity> Cards { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Revision, bool Busy)> _scopes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DelegationCardRouteEntity> _routes = new(StringComparer.Ordinal);
        private int _etag;
        internal bool FailReplace;
        public bool IsAvailable => true;
        private static string Key(string partition, string row) => partition + "|" + row;
        private static DelegationCardEntity Copy(DelegationCardEntity card)
        {
            var copy = JsonSerializer.Deserialize<DelegationCardEntity>(JsonSerializer.Serialize(card))
                ?? throw new InvalidOperationException("Could not clone the test receipt.");
            copy.ETag = card.ETag;
            return copy;
        }
        public Task AddAsync(DelegationCardEntity card)
        {
            card.ETag = new ETag((++_etag).ToString());
            Cards.Add(Key(card.PartitionKey, card.RowKey), Copy(card));
            return Task.CompletedTask;
        }
        public Task<DelegationCardEntity?> GetAsync(string partition, string id) =>
            Task.FromResult(Cards.TryGetValue(Key(partition, "card-" + id), out var row) ? Copy(row) : null);
        public Task<bool> ReplaceAsync(DelegationCardEntity card)
        {
            if (FailReplace) throw new RequestFailedException(503, "Storage unavailable");
            var key = Key(card.PartitionKey, card.RowKey);
            if (!Cards.TryGetValue(key, out var prior) || prior.ETag != card.ETag) return Task.FromResult(false);
            card.ETag = new ETag((++_etag).ToString());
            Cards[key] = Copy(card);
            return Task.FromResult(true);
        }
        public Task<DelegationScopeLease> AcquireScopeAsync(string partition, string scope, string? expectedRevision = null)
        {
            var key = Key(partition, scope);
            _scopes.TryGetValue(key, out var current);
            if (current.Busy) throw new InvalidOperationException("The specialist is busy.");
            if (expectedRevision != null && current.Revision != expectedRevision)
                throw new InvalidOperationException("The preview is stale.");
            var revision = Guid.NewGuid().ToString("N");
            _scopes[key] = (revision, true);
            return Task.FromResult(new DelegationScopeLease(partition, scope, revision));
        }
        public Task ReleaseScopeAsync(DelegationScopeLease lease)
        {
            var key = Key(lease.Partition, lease.Scope);
            Assert.Equal(lease.Revision, _scopes[key].Revision);
            _scopes[key] = (lease.Revision, false);
            return Task.CompletedTask;
        }
        public Task<string?> GetRevisionAsync(string partition, string scope) =>
            Task.FromResult(_scopes.TryGetValue(Key(partition, scope), out var state) ? state.Revision : null);
        public Task SaveRouteAsync(DelegationCardRouteEntity route)
        {
            _routes[Key(route.PartitionKey, route.OwnerAgentId + "|" + route.RequesterId)] = route;
            return Task.CompletedTask;
        }
        public Task<DelegationCardRouteEntity?> GetRouteAsync(string partition, string requesterId, string ownerAgentId) =>
            Task.FromResult(_routes.GetValueOrDefault(Key(partition, ownerAgentId + "|" + requesterId)));
    }
}
