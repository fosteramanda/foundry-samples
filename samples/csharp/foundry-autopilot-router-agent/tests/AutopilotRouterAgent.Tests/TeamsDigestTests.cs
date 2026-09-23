namespace WorkstreamManagerAgent.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.AgentLogic;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Xunit;

public sealed class TeamsDigestTests
{
    private const string OwnerId = "10000000-0000-0000-0000-000000000001";
    private const string OtherOwnerId = "10000000-0000-0000-0000-000000000002";
    private const string ChatId = "19:digest-test@thread.v2";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T02:00:00Z");
    private static readonly DigestOwner Owner = new(OwnerId, "Alex Example");

    [Fact]
    public void CardHasReadableCountsRowsAndOpenUrlAction()
    {
        var board = new PlannerDigestSnapshot("Test board",
        [
            TaskItem("Old item", Now.AddDays(-2)),
            TaskItem("Today's item", Now),
            new("3", "Unassigned item", 50, null, []),
            new("4", "Completed item", 100, Now.AddDays(-3), [Owner]),
        ]);
        var digest = TeamsDigestCard.Build("Daily digest", [new("Activity", "Two updates.")],
            board, Now, TimeZoneInfo.Utc);
        Assert.Equal("1.2", digest.Card["version"]!.GetValue<string>());
        Assert.Equal("AdaptiveCard", digest.Card["type"]!.GetValue<string>());
        Assert.Contains("3 open, 1 past due, 1 unassigned", digest.Summary);
        Assert.DoesNotContain("Completed item", digest.Card.ToJsonString());
        Assert.Contains("Alex Example", digest.Card.ToJsonString());
        Assert.Equal("Action.OpenUrl", digest.Card["actions"]![0]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("Action.Submit", digest.Card.ToJsonString());
        Assert.DoesNotContain("Action.ToggleVisibility", digest.Card.ToJsonString());
        Assert.All(Objects(digest.Card).Where(node => node["type"]?.GetValue<string>() == "TextBlock"),
            node => Assert.True(node["wrap"]!.GetValue<bool>()));
    }

    [Fact]
    public void DateOnlyPastDueUsesDisplayTimeZoneNotUtcRollover()
    {
        var board = new PlannerDigestSnapshot("Test board", [TaskItem("Due today", Now.AddHours(-2))]);
        var digest = TeamsDigestCard.Build("Daily digest", [], board, Now,
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        Assert.Contains("0 past due", digest.Summary);
        Assert.Contains("Due Sep 22", digest.Card.ToJsonString());
        Assert.DoesNotContain("Past due Sep 22", digest.Card.ToJsonString());
    }

    [Fact]
    public void LongBoardKeepsAccurateTotalsAndExplicitPreview()
    {
        var board = new PlannerDigestSnapshot("Test board",
            Enumerable.Range(0, 60).Select(index => TaskItem($"Item {index}", Now.AddDays(index - 3))).ToArray());
        var digest = TeamsDigestCard.Build("Digest", [], board, Now, TimeZoneInfo.Utc);
        Assert.Contains("60 open", digest.Summary);
        Assert.Contains("Showing 6 of 60", digest.Card.ToJsonString());
        Assert.Contains("Item 0", digest.Card.ToJsonString());
        Assert.DoesNotContain("Item 59", digest.Card.ToJsonString());
        Assert.True(Encoding.UTF8.GetByteCount(digest.Card.ToJsonString()) <= TeamsDigestCard.CardByteBudget);
    }

    [Fact]
    public void OversizedRowsShrinkPreviewWithoutLyingAboutTotal()
    {
        var board = new PlannerDigestSnapshot("Test board",
            Enumerable.Range(0, 13).Select(index => TaskItem(new string('界', 800) + index, null)).ToArray());
        var digest = TeamsDigestCard.Build("Digest", [], board, Now, TimeZoneInfo.Utc);
        Assert.Contains("13 open", digest.Summary);
        Assert.True(Encoding.UTF8.GetByteCount(digest.Card.ToJsonString()) <= TeamsDigestCard.CardByteBudget);
        Assert.Contains("of 13 open items", digest.Card.ToJsonString());
    }

    [Fact]
    public void OversizedSummaryFailsInsteadOfSilentlyDroppingEvidence()
    {
        Assert.Throws<ArgumentException>(() => TeamsDigestCard.Build("Digest",
            [new("Evidence", new string('界', 10000))], null, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void EmptyBoardIsExplicitAndGeneralBriefingNeedsNoPlanner()
    {
        var empty = TeamsDigestCard.Build("Digest", [], new("Test board", []), Now, TimeZoneInfo.Utc);
        Assert.Contains("No open items", empty.Card.ToJsonString());
        Assert.Contains("0 open", empty.Summary);
        var general = TeamsDigestCard.Build("Briefing", [new("Activity", "No new messages.")], null, Now, TimeZoneInfo.Utc);
        Assert.Null(general.Card["actions"]);
        Assert.DoesNotContain("No open items", general.Card.ToJsonString());
    }

    [Fact]
    public void MentionsAreRealDistinctAndNotRepeatedInEveryTask()
    {
        var digest = TeamsDigestCard.Build("Digest", [],
            new("Test board", [TaskItem("First", null), TaskItem("Second", null)]), Now, TimeZoneInfo.Utc);
        var message = TeamsDigestCard.BuildGraphMessage(digest, true);
        Assert.Single(message["mentions"]!.AsArray());
        Assert.Contains("<at id=\"0\">Alex Example</at>", message["body"]!["content"]!.GetValue<string>());
        Assert.Equal(OwnerId, message["mentions"]![0]!["mentioned"]!["user"]!["id"]!.GetValue<string>());
        Assert.DoesNotContain("<at", digest.Card.ToJsonString());
        Assert.Empty(TeamsDigestCard.BuildGraphMessage(digest, false)["mentions"]!.AsArray());
    }

    [Fact]
    public void GraphAttachmentIdMatchesBodyAndContentIsSerializedCard()
    {
        var digest = TeamsDigestCard.Build("Digest", [new("Activity", "Ready.")], null, Now, TimeZoneInfo.Utc);
        var message = TeamsDigestCard.BuildGraphMessage(digest, false);
        var attachment = message["attachments"]![0]!;
        Assert.Contains($"id=\"{attachment["id"]!.GetValue<string>()}\"",
            message["body"]!["content"]!.GetValue<string>());
        Assert.Equal(TeamsDigestCard.ContentType, attachment["contentType"]!.GetValue<string>());
        Assert.Equal("AdaptiveCard", JsonNode.Parse(attachment["content"]!.GetValue<string>())!["type"]!.GetValue<string>());
        Assert.True(Encoding.UTF8.GetByteCount(message.ToJsonString()) <= TeamsDigestCard.MessageByteBudget);
    }

    [Fact]
    public void UserTextCannotAddHtmlMentionsOrMarkdownLinks()
    {
        var malicious = new DigestOwner(OwnerId, "<at>All</at> & [click](https://example.com)");
        var digest = TeamsDigestCard.Build("<b>Digest</b>",
            [new("Activity", "[click](https://example.com)")],
            new("Test board", [new("1", "Task <at>everyone</at>", 0, null, [malicious])]), Now, TimeZoneInfo.Utc);
        var textBlocks = Objects(digest.Card).Where(node => node["type"]?.GetValue<string>() == "TextBlock");
        Assert.All(textBlocks, block => Assert.DoesNotContain("<at>", block["text"]!.GetValue<string>()));
        var message = TeamsDigestCard.BuildGraphMessage(digest, true);
        Assert.DoesNotContain("<at>All</at>", message["body"]!["content"]!.GetValue<string>());
        Assert.Contains("&lt;at&gt;All&lt;/at&gt;", message["body"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void MentionLimitIsExplicitAndInvalidIdsAreRejected()
    {
        var owners = Enumerable.Range(0, 21).Select(index => new DigestOwner(
            $"10000000-0000-0000-0000-{index:D12}", $"Person {index:D2}")).ToArray();
        var digest = TeamsDigestCard.Build("Digest", [],
            new("Test board", [new("1", "Shared task", 0, null, owners)]), Now, TimeZoneInfo.Utc);
        var message = TeamsDigestCard.BuildGraphMessage(digest, true);
        Assert.Equal(20, message["mentions"]!.AsArray().Count);
        Assert.Contains("20 of 21 owners", message["body"]!["content"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => TeamsDigestCard.BuildGraphMessage(
            digest with { Owners = [new("not-a-user-id", "Unknown")] }, true));
    }

    [Theory]
    [InlineData("outlook", "personal")]
    [InlineData("msteams", "channel")]
    public void ToolIsNotOfferedOutsideSupportedTeamsChats(string channel, string conversationType)
    {
        var (_, handler) = Handler();
        handler.SetCurrentActivityContext(Activity(channel, conversationType));
        Assert.False(handler.IsEnabled);
        Assert.Empty(handler.GetToolDefinitions());
    }

    [Fact]
    public void ToolIsNotOfferedWithoutTokenOrConversation()
    {
        var (_, handler) = Handler(token: null);
        Assert.Empty(handler.GetToolDefinitions());
        var (_, withToken) = Handler();
        withToken.SetCurrentActivityContext(new Activity { ChannelId = "msteams" });
        Assert.Empty(withToken.GetToolDefinitions());
    }

    [Fact]
    public void InstructionsOnlyAdvertiseTheToolWhenAttached()
    {
        Assert.DoesNotContain("send_teams_digest", AgentInstructions.GetInstructions(new AgentMetadata()));
        var instructions = AgentInstructions.GetInstructions(new AgentMetadata(), digestCardsEnabled: true);
        Assert.Contains("send_teams_digest", instructions);
        Assert.Contains("older routine", instructions);
        Assert.Contains("Email it", instructions);
    }

    [Fact]
    public async Task SuccessfulSendUsesCurrentChatAndDoesNotDuplicate()
    {
        var (http, handler) = Handler();
        var first = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
        var second = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
        Assert.True(handler.HasDelivered);
        Assert.Equal(first, second);
        var post = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal($"/v1.0/chats/{Uri.EscapeDataString(ChatId)}/messages", post.Uri.AbsolutePath);
        Assert.True(JsonNode.Parse(first!)!["sent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LiveBoardSnapshotReadsAllPagesAndResolvesOwnerOnce()
    {
        var (http, handler) = Handler();
        http.Response = request =>
        {
            if (request.Method == HttpMethod.Post) return Response(HttpStatusCode.Created, """{"id":"message-1"}""");
            if (request.Uri.AbsolutePath.Contains("/groups/")) return Plans();
            if (request.Uri.AbsolutePath.Contains("/users/")) return Response(HttpStatusCode.OK, """{"displayName":"Alex Example"}""");
            return request.Uri.Query.Contains("skiptoken")
                ? Response(HttpStatusCode.OK, TaskPage("second"))
                : Response(HttpStatusCode.OK, TaskPage("first", "https://graph.microsoft.com/v1.0/planner/plans/plan-1/tasks?$skiptoken=next"));
        };
        await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments());
        Assert.True(handler.HasDelivered);
        Assert.Equal(2, http.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/tasks")));
        Assert.Single(http.Requests, request => request.Uri.AbsolutePath.Contains("/users/"));
        var post = Assert.Single(http.Requests, request => request.Method == HttpMethod.Post);
        var card = JsonNode.Parse(JsonNode.Parse(post.Body!)!["attachments"]![0]!["content"]!.GetValue<string>());
        Assert.Contains("2 open", card!["fallbackText"]!.GetValue<string>());
        Assert.Contains("first", card.ToJsonString());
        Assert.Contains("second", card.ToJsonString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"title":"Digest","sections":null}""")]
    [InlineData("""{"title":"Digest","sections":[null]}""")]
    [InlineData("""{"title":"Digest","sections":[],"include_board":false}""")]
    [InlineData("""{"title":"Digest","sections":[],"chat_id":"another-chat"}""")]
    public async Task InvalidArgumentsNeverSend(string arguments)
    {
        var (http, handler) = Handler();
        var result = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, arguments);
        Assert.Contains("Nothing was sent", result);
        Assert.Empty(http.Requests);
        Assert.False(handler.HasDelivered);
    }

    [Fact]
    public async Task BoardReadFailureDoesNotBecomeAnEmptyBoardCard()
    {
        var (http, handler) = Handler();
        http.Response = request => request.Uri.AbsolutePath.Contains("/groups/")
            ? Plans() : Response(HttpStatusCode.Forbidden, """{"error":{"message":"Not authorized"}}""");
        var result = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments());
        Assert.Contains("Do not report an empty board", result);
        Assert.DoesNotContain(http.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task PaginationCannotForwardCredentialsToAnotherHost()
    {
        var (http, handler) = Handler();
        http.Response = request => request.Uri.AbsolutePath.Contains("/groups/")
            ? Plans() : Response(HttpStatusCode.OK, TaskPage("first", "https://example.com/steal"));
        var result = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments());
        Assert.Contains("invalid task page", result);
        Assert.All(http.Requests, request => Assert.Equal("graph.microsoft.com", request.Uri.Host));
        Assert.DoesNotContain(http.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task RejectedAndUncertainSendsAreNotRetriedOrReportedSuccessful()
    {
        foreach (var networkFailure in new[] { false, true })
        {
            var (http, handler) = Handler();
            http.Response = _ => networkFailure
                ? throw new HttpRequestException("Connection interrupted.")
                : Response(HttpStatusCode.Forbidden, """{"error":{"message":"Forbidden"}}""");
            var first = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
            var again = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
            Assert.Equal(first, again);
            Assert.False(handler.HasDelivered);
            Assert.Single(http.Requests);
            Assert.Contains(networkFailure ? "could not be confirmed" : "rejected", first);
        }
    }

    [Fact]
    public async Task SeparateTurnsAndChatsDoNotShareDeliveryState()
    {
        var (firstHttp, first) = Handler();
        var (secondHttp, second) = Handler();
        second.SetCurrentActivityContext(Activity(chatId: "19:other-test@thread.v2"));
        await Task.WhenAll(
            first.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false)),
            second.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false)));
        Assert.Single(firstHttp.Requests);
        Assert.Single(secondHttp.Requests);
        Assert.NotEqual(firstHttp.Requests[0].Uri, secondHttp.Requests[0].Uri);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{not-json")]
    [InlineData("""{"id":42}""")]
    public async Task AcceptedSendWithUnreadableReceiptIsUncertainAndNeverRetried(string body)
    {
        var (http, handler) = Handler();
        http.Response = _ => Response(HttpStatusCode.Created, body);
        var result = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
        var repeated = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
        Assert.Contains("could not be confirmed", result);
        Assert.Equal(result, repeated);
        Assert.False(handler.HasDelivered);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task TimedOutPostIsNotRetried()
    {
        var (http, handler) = Handler();
        http.Response = _ => throw new TaskCanceledException("Request timed out.");
        var result = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
        Assert.Contains("could not be confirmed", result);
        await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
        Assert.Single(http.Requests);
        Assert.False(handler.HasDelivered);
    }

    [Fact]
    public async Task InvalidTimeZoneDoesNotSendOrSilentlyUseServerTime()
    {
        var http = new FakeHttp();
        var client = new HttpClient(http);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["MeetingDisplayTimeZone"] = "not-a-time-zone" }).Build();
        var planner = new PlannerToolHandler(new AgentMetadata(), NullLogger.Instance, client, configuration, "test-token");
        var handler = new TeamsDigestToolHandler(planner, client, NullLogger.Instance, configuration, "test-token");
        handler.SetCurrentActivityContext(Activity());
        var result = await handler.TryExecuteAsync(TeamsDigestToolHandler.ToolName, Arguments(includeBoard: false));
        Assert.Contains("Nothing was sent", result);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void PreviewFixtureUsesTheProductionRenderer()
    {
        var titles = new[]
        {
            "Checkout rollout", "Payout reconciliation", "Dispute flow review", "Step-up rules",
            "Merchant onboarding", "Wallet parity", "Settlement timing", "Refund investigation",
            "Compliance attestation", "Review checklist", "Draft Q4 summary", "Verify onboarding", "Follow up",
        };
        var board = new PlannerDigestSnapshot("Team board", titles.Select((title, index) =>
            new DigestTask(index.ToString(), title, index % 3 == 0 ? 50 : 0,
                index == 12 ? null : Now.AddDays(index - 4),
                index == 12 ? [] : [index % 2 == 0 ? Owner : new DigestOwner(OtherOwnerId, "Taylor Example")]))
            .ToArray());
        var card = TeamsDigestCard.Build("End-of-day team digest",
            [new("Activity", "Two updates reviewed. One decision needs follow-up."),
             new("Decisions", "The rollout checkpoint remains open. Next review: tomorrow.")],
            board, Now, TimeZoneInfo.Utc).Card;
        Assert.Contains("13 open", card["fallbackText"]!.GetValue<string>());
        var previewPath = Environment.GetEnvironmentVariable("DIGEST_PREVIEW_PATH");
        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            File.WriteAllText(previewPath, card.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var longCard = TeamsDigestCard.Build(new string('A', 120),
                [new("Long content", string.Join(" ", Enumerable.Repeat("A readable summary.", 50)))],
                new("Test board",
                [
                    new("long", new string('W', 250), 0, Now.AddDays(-1), [Owner]),
                    new("unicode", string.Concat(Enumerable.Repeat("文書の確認", 30)), 50, null, []),
                ]), Now, TimeZoneInfo.Utc).Card;
            File.WriteAllText(Path.ChangeExtension(previewPath, ".long.json"),
                longCard.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static DigestTask TaskItem(string title, DateTimeOffset? due) =>
        new(title, title, 0, due, [Owner]);

    private static IEnumerable<JsonObject> Objects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.Select(pair => pair.Value).OfType<JsonNode>())
                foreach (var nested in Objects(child)) yield return nested;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
                foreach (var nested in Objects(child)) yield return nested;
        }
    }

    private static string Arguments(bool includeBoard = true) => JsonSerializer.Serialize(new
    {
        title = "Daily digest",
        sections = new[] { new { heading = "Activity", text = "No new updates found in the sources checked." } },
        include_board = includeBoard,
        mention_owners = true,
    });

    private static Activity Activity(
        string channel = "msteams", string conversationType = "groupChat", string chatId = ChatId) => new()
    {
        ChannelId = channel,
        Type = ActivityTypes.Message,
        Conversation = new ConversationAccount { Id = chatId, ConversationType = conversationType },
    };

    private static (FakeHttp Http, TeamsDigestToolHandler Handler) Handler(string? token = "test-token")
    {
        var http = new FakeHttp();
        var client = new HttpClient(http);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlannerDefaultBoard"] = "Test board",
            ["PlannerGroupId"] = "group-1",
            ["MeetingDisplayTimeZone"] = "UTC",
        }).Build();
        var planner = new PlannerToolHandler(
            new AgentMetadata { UserId = Guid.Parse(OwnerId) }, NullLogger.Instance, client, configuration, token);
        var handler = new TeamsDigestToolHandler(planner, client, NullLogger.Instance, configuration, token);
        handler.SetCurrentActivityContext(Activity());
        return (http, handler);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Plans() =>
        Response(HttpStatusCode.OK, """{"value":[{"id":"plan-1","title":"Test board"}]}""");

    private static string TaskPage(string title, string? next = null)
    {
        var page = new JsonObject
        {
            ["value"] = new JsonArray(new JsonObject
            {
                ["id"] = title, ["title"] = title, ["percentComplete"] = 0,
                ["assignments"] = new JsonObject { [OwnerId] = new JsonObject() },
            }),
        };
        if (next != null) page["@odata.nextLink"] = next;
        return page.ToJsonString();
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body);

    private sealed class FakeHttp : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public Func<CapturedRequest, HttpResponseMessage> Response { get; set; } =
            _ => TeamsDigestTests.Response(HttpStatusCode.Created, """{"id":"message-1"}""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(request.Method, request.RequestUri!,
                request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            return Response(captured);
        }
    }
}
