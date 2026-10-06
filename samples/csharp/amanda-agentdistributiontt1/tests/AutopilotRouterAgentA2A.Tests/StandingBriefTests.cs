using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Xunit;

namespace WorkstreamManagerAgent.Tests;

public class StandingBriefTests
{
    private const string Html = "<h1>Review</h1><p>Source confirmed.</p>";
    private const string Text = "ReviewSource confirmed.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatesVerifiesContentAndSharesOnlyConfiguredMembers(bool renamedTools)
    {
        using var fixture = new Fixture { RenamedTools = renamedTools };
        var brief = await fixture.Publisher.PublishAsync(fixture.Job, 1, "facts", Html);
        Assert.Equal(fixture.DocumentId.ToString("D"), brief.DocumentId);
        Assert.Equal(fixture.Brief.FileName, brief.FileName);
        Assert.Equal(fixture.Created, brief.CreatedUtc);
        Assert.Equal(1, fixture.Creates);
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(1, fixture.Invites);
        Assert.Equal("manager@example.com", fixture.LastInvite!["recipients"]![0]!["email"]!.GetValue<string>());
        Assert.Null(fixture.LastInvite["recipients"]![0]!["objectId"]);
        Assert.Single(fixture.LastInvite["recipients"]!.AsArray());
        Assert.False(fixture.LastInvite["sendInvitation"]!.GetValue<bool>());
        Assert.True(fixture.LastInvite["requireSignIn"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LostSharingResultResumesTheSameVerifiedFileWithoutAnotherCreation()
    {
        using var fixture = new Fixture { FailSharing = true };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Publisher.PublishAsync(fixture.Job, 1, "facts", Html));
        fixture.FailSharing = false;
        var recovered = await fixture.Publisher.ReconcileAsync(fixture.Job, 1, "facts", Html);
        Assert.Equal("created-item", recovered.ItemId);
        Assert.Equal(1, fixture.Creates);
        Assert.Equal(fixture.Created, recovered.CreatedUtc);
    }

    [Fact]
    public async Task SameFileNameDoesNotProveTheSameContent()
    {
        using var fixture = new Fixture { Exists = true, Content = "A different manager edit." };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Publisher.ReconcileAsync(fixture.Job, 1, "facts", Html));
        Assert.Equal(0, fixture.Creates);
        Assert.Equal(0, fixture.Invites);
    }

    [Fact]
    public async Task WrongCreatorStopsBeforeReadingOrSharing()
    {
        using var fixture = new Fixture { Exists = true, WrongCreator = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Publisher.PublishAsync(fixture.Job, 1, "facts", Html));
        Assert.Equal(0, fixture.Reads);
        Assert.Equal(0, fixture.Invites);
    }

    [Fact]
    public async Task UncertainMissingFileIsNotCreatedAgain()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Publisher.ReconcileAsync(fixture.Job, 1, "facts", Html));
        Assert.Equal(0, fixture.Creates);
    }

    [Fact]
    public async Task ReadPermissionIsNotReportedAsEditAccess()
    {
        using var fixture = new Fixture { PermissionRole = "read" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Publisher.PublishAsync(fixture.Job, 1, "facts", Html));
    }

    [Fact]
    public async Task ReadsTheActualWordCommentRatherThanTheTruncatedMailPreview()
    {
        using var fixture = new Fixture();
        var comment = "@Review Agent Why is the launch blocked?\n" + new string('x', 500);
        fixture.Comments = $"Comment 36B02C00: {comment}\r\nAnchor 36B02C00: Review\r\n";
        var notification = fixture.Notification();
        notification["bodyPreview"] = "A truncated and misleading preview.";
        var source = await fixture.Publisher.ReadCommentNotificationAsync(
            fixture.Job, [fixture.Brief], notification, fixture.Caller);
        Assert.Equal(comment, source.Content);
        Assert.Equal("36B02C00", source.CommentId);
        Assert.Equal(fixture.Caller.Id, source.ActorId);
        Assert.Equal("word:" + fixture.DocumentId.ToString("D"), source.Binding);
        Assert.Equal("created-item", source.DocumentItemId);
        Assert.Equal("drive-id", source.DriveId);
        Assert.True(source.IsHuman);
    }

    [Fact]
    public async Task NativeAndMailNotificationsProduceTheSameEvidenceIdentity()
    {
        using var fixture = new Fixture();
        var mail = await fixture.Publisher.ReadCommentNotificationAsync(
            fixture.Job, [fixture.Brief], fixture.Notification(), fixture.Caller);
        var native = await fixture.Publisher.ReadCommentAsync(
            fixture.Brief, "36B02C00", fixture.Caller.Id, fixture.Created.AddMinutes(1));
        Assert.Equal(mail.Id, native.Id);
        Assert.Equal(mail.Content, native.Content);
    }

    [Theory]
    [InlineData("21CB172", 35434866u)]
    [InlineData("A", 10u)]
    [InlineData("0", 0u)]
    public async Task UnpaddedWordIdsMatchNumericNotificationsAndKeepTheirReplyForm(string wordId, uint notificationId)
    {
        using var fixture = new Fixture();
        const string comment = "@Review Agent Readiness approval is not launch authorization.";
        fixture.Comments = $"Comment {wordId}: {comment}\r\nAnchor {wordId}: Review\r\n";
        var mail = await fixture.Publisher.ReadCommentNotificationAsync(
            fixture.Job, [fixture.Brief], fixture.Notification(commentId: notificationId), fixture.Caller);
        var native = await fixture.Publisher.ReadCommentAsync(
            fixture.Brief, notificationId.ToString("X8"), fixture.Caller.Id, fixture.Created);

        Assert.Equal(comment, mail.Content);
        Assert.Equal(wordId, mail.CommentId);
        Assert.Equal(mail.Id, native.Id);
        Assert.Equal(StandingJobStore.Hash(
            $"word:{fixture.DocumentId:D}:{wordId}:{fixture.Caller.Id}:{comment}"), mail.Id);
        Assert.Equal("ABC01234", await fixture.Publisher.ReplyAsync(mail, "Launch still needs both commitments."));
        Assert.Equal(wordId, fixture.LastReply!["commentId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReplyUsesTheCurrentWireFormOfTheSameNumericComment()
    {
        using var fixture = new Fixture();
        const string comment = "The checklist is independent.";
        fixture.Comments = $"Comment 21CB172: {comment}\r\nAnchor 21CB172: Review\r\n";
        var source = await fixture.Publisher.ReadCommentNotificationAsync(
            fixture.Job, [fixture.Brief], fixture.Notification(commentId: 35434866), fixture.Caller);
        fixture.Comments = $"Comment 021CB172: {comment}\r\nAnchor 021CB172: Review\r\n";
        var reread = await fixture.Publisher.ReadCommentAsync(
            fixture.Brief, "021CB172", fixture.Caller.Id, fixture.Created);
        Assert.Equal(source.Id, reread.Id);

        await fixture.Publisher.ReplyAsync(source, "Readiness can progress independently.");

        Assert.Equal("021CB172", fixture.LastReply!["commentId"]!.GetValue<string>());
    }

    [Fact]
    public async Task DifferentCommentIdentityIsNotMatchedByEqualText()
    {
        using var fixture = new Fixture();
        fixture.Comments = "Comment 21CB173: Same text.\r\nAnchor 21CB173: Review\r\n";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Publisher.ReadCommentNotificationAsync(
                fixture.Job, [fixture.Brief], fixture.Notification(commentId: 35434866), fixture.Caller));
        Assert.Equal(0, fixture.Replies);
    }

    [Fact]
    public async Task NotificationSenderMustMatchTheAuthenticatedParticipant()
    {
        using var fixture = new Fixture();
        var notification = fixture.Notification();
        notification["from"]!["emailAddress"]!["address"] = "outsider@example.com";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publisher.ReadCommentNotificationAsync(fixture.Job, [fixture.Brief], notification, fixture.Caller));
        Assert.Equal(0, fixture.Reads);
    }

    [Fact]
    public async Task ATitleMatchCannotSubstituteForAnExactDocumentBinding()
    {
        using var fixture = new Fixture();
        var notification = fixture.Notification(Guid.NewGuid());
        notification["subject"] = fixture.Brief.FileName;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Publisher.ReadCommentNotificationAsync(fixture.Job, [fixture.Brief], notification, fixture.Caller));
        Assert.Equal(0, fixture.Reads);
    }

    [Fact]
    public async Task OrdinaryMailCannotMasqueradeAsAWordNotification()
    {
        using var fixture = new Fixture();
        var notification = fixture.Notification();
        notification["internetMessageId"] = "<ordinary@example.com>";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Publisher.ReadCommentNotificationAsync(fixture.Job, [fixture.Brief], notification, fixture.Caller));
    }

    [Fact]
    public async Task CommentMustStillMatchBeforePostingTheReply()
    {
        using var fixture = new Fixture();
        var source = await fixture.Publisher.ReadCommentNotificationAsync(
            fixture.Job, [fixture.Brief], fixture.Notification(), fixture.Caller);
        fixture.Comments = "Comment 36B02C00: Changed question.\r\nAnchor 36B02C00: Review\r\n";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Publisher.ReplyAsync(source, "A reply."));
        Assert.Equal(0, fixture.Replies);
    }

    [Fact]
    public async Task ReplyUsesOnlyTheVerifiedDocumentAndCommentIdentity()
    {
        using var fixture = new Fixture();
        var source = await fixture.Publisher.ReadCommentNotificationAsync(
            fixture.Job, [fixture.Brief], fixture.Notification(), fixture.Caller);
        Assert.Equal("ABC01234", await fixture.Publisher.ReplyAsync(source, "The source records a launch hold."));
        Assert.Equal("36B02C00", fixture.LastReply!["commentId"]!.GetValue<string>());
        Assert.Equal("created-item", fixture.LastReply["documentId"]!.GetValue<string>());
        Assert.Equal("drive-id", fixture.LastReply["driveId"]!.GetValue<string>());
    }

    [Fact]
    public async Task GraphMessageIdIsEscapedAsOnePathSegment()
    {
        using var fixture = new Fixture();
        await fixture.Publisher.ReadNotificationAsync("id/with+reserved==");
        Assert.Contains("id%2Fwith%2Breserved%3D%3D", fixture.MessageUri!.AbsoluteUri);
        Assert.Contains("uniqueBody", fixture.MessageUri.Query);
    }

    [Fact]
    public void ParsesRealCommentAndAnchorBlocksWithoutMixingReplies()
    {
        var comments = StandingWordClient.ParseComments(
            "Comment ABCD1234: First line\r\nSecond line\r\nAnchor ABCD1234: A heading\r\n"
            + "Comment 36B02C00: Other comment\r\nAnchor 36B02C00: Another heading\r\n");
        Assert.Equal("First line\r\nSecond line", comments["ABCD1234"]);
        Assert.Equal("Other comment", comments["36B02C00"]);
        Assert.Throws<InvalidOperationException>(() => StandingWordClient.ParseComments("An unrecognized response"));
        Assert.Empty(StandingWordClient.ParseComments("No comments found.\r\n"));
    }

    [Theory]
    [InlineData("ABC123", "00ABC123")]
    [InlineData("21cb172", "021CB172")]
    public void DifferentWireFormsOfOneCommentCannotBecomeTwoSources(string first, string second)
    {
        Assert.Throws<InvalidOperationException>(() => StandingWordClient.ParseComments(
            $"Comment {first}: First text.\r\nAnchor {first}: Review\r\n"
            + $"Comment {second}: Different text.\r\nAnchor {second}: Review\r\n"));
    }

    [Fact]
    public void CommentIdsOutsideTheNumericNotificationRangeAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => StandingWordClient.ParseComments(
            "Comment 100000000: Unsupported identity.\r\nAnchor 100000000: Review\r\n"));
    }

    [Fact]
    public void CommentNavigationUsesTheActualDocumentGuidAndDecimalWordCommentId()
    {
        using var fixture = new Fixture();
        var result = StandingBriefPublisher.ParseCommentReference(fixture.Notification()["body"]!["content"]!.GetValue<string>());
        Assert.Equal(fixture.DocumentId, result!.DocumentId);
        Assert.Equal("36B02C00", result.CommentId);
        Assert.Null(StandingBriefPublisher.ParseCommentReference("<p>Just a document title</p>"));
    }

    [Fact]
    public void MoreThanOneCommentReferenceIsNotGuessed()
    {
        using var fixture = new Fixture();
        var one = fixture.Notification()["body"]!["content"]!.GetValue<string>();
        var two = fixture.Notification(Guid.NewGuid())["body"]!["content"]!.GetValue<string>();
        Assert.Throws<InvalidOperationException>(() => StandingBriefPublisher.ParseCommentReference(one + two));
    }

    [Fact]
    public void ReadsBothMcpPayloadShapesWithoutInventingAReceipt()
    {
        var structured = StandingBriefPublisher.ToolPayload(JsonNode.Parse("""{"structuredContent":{"driveItem":{"id":"a"}}}"""));
        var text = StandingBriefPublisher.ToolPayload(JsonNode.Parse("""{"content":[{"type":"text","text":"{\"driveItem\":{\"id\":\"b\"}}"}]}"""));
        Assert.Equal("a", structured["driveItem"]!["id"]!.GetValue<string>());
        Assert.Equal("b", text["driveItem"]!["id"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => StandingBriefPublisher.ToolPayload(null));
    }

    [Theory]
    [InlineData("The Work IQ A2A call failed: Timeout.")]
    [InlineData("(no response)")]
    [InlineData("Agent 'specialist' accepted the request but returned no content.")]
    [InlineData("")]
    public void AFailedSpecialistCallIsNotACompletedAnswer(string text) =>
        Assert.False(WorkIqA2AToolHandler.HasCompletedAnswer(text));

    private sealed class Fixture : IDisposable
    {
        public Guid AgentUser { get; } = Guid.NewGuid();
        public Guid DocumentId { get; } = Guid.NewGuid();
        public DateTimeOffset Created { get; } = DateTimeOffset.Parse("2026-10-01T09:00:00Z");
        public StandingJob Job { get; }
        public StandingJobCaller Caller { get; }
        public StandingJobBrief Brief { get; }
        public StandingBriefPublisher Publisher { get; }
        private readonly HttpClient _http;
        public bool Exists { get; set; }
        public bool FailSharing { get; set; }
        public bool WrongCreator { get; set; }
        public bool RenamedTools { get; set; }
        public string PermissionRole { get; set; } = "write";
        public string Content { get; set; } = Text;
        public string Comments { get; set; } = "Comment 36B02C00: @Review Agent Why is launch blocked?\r\nAnchor 36B02C00: Review\r\n";
        public int Creates { get; private set; }
        public int Invites { get; private set; }
        public int Reads { get; private set; }
        public int Replies { get; private set; }
        public JsonNode? LastInvite { get; private set; }
        public JsonNode? LastReply { get; private set; }
        public Uri? MessageUri { get; private set; }

        public Fixture()
        {
            var manager = Guid.NewGuid().ToString();
            Caller = new(manager, manager, "manager@example.com");
            Job = new()
            {
                Id = Guid.NewGuid().ToString(), Title = "Leadership review", ManagerId = manager,
                Members = [new(manager, "manager@example.com", "Review owner")]
            };
            Brief = new(1, StandingBriefPublisher.FileName(Job, 1), "created-item", DocumentId.ToString("D"),
                $"https://tenant.sharepoint.com/Doc.aspx?sourcedoc={DocumentId:D}", "facts", Created);
            _http = new(new Handler(Respond));
            Publisher = new(_http, new McpServerConfig { Url = "https://word.example.com/mcp" },
                "word-token", "graph-token", AgentUser, NullLogger.Instance);
        }

        public JsonNode Notification(Guid? document = null, uint commentId = 917515264)
        {
            var nav = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { c = commentId })));
            var url = $"https://tenant.sharepoint.com/Documents/review.docx?d=w{document ?? DocumentId:N}&amp;nav={nav}";
            return JsonSerializer.SerializeToNode(new
            {
                internetMessageId = "<CommentWord-test@odspnotify>",
                from = new { emailAddress = new { address = Caller.Email } },
                receivedDateTime = Created,
                body = new { content = $"<p>Review owner added a comment</p><a href=\"{url}\">Go to comment</a>" }
            })!;
        }

        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync());
            if (request.RequestUri!.Host == "word.example.com")
            {
                switch (body!["method"]!.GetValue<string>())
                {
                    case "initialize": return Reply(new { result = new { protocolVersion = "2025-03-26" } });
                    case "notifications/initialized": return new(HttpStatusCode.Accepted);
                    case "tools/list":
                        return Reply(new { result = new { tools = (RenamedTools
                            ? new[] { "WordCreateNewDocument", "WordGetDocumentContent", "WordReplyToComment" }
                            : new[] { "CreateDocument", "GetDocumentContent", "ReplyToComment" }).Select(name => new { name }) } });
                    case "tools/call":
                        var tool = body["params"]!["name"]!.GetValue<string>();
                        if (tool is "CreateDocument" or "WordCreateNewDocument")
                        {
                            Creates++; Exists = true;
                            Assert.Equal(Html, body["params"]!["arguments"]!["contentInHtml"]!.GetValue<string>());
                            return Reply(new { result = new { structuredContent = new { driveItem = new { Id = "created-item" } } } });
                        }
                        if (tool is "GetDocumentContent" or "WordGetDocumentContent")
                        {
                            Reads++;
                            return Reply(new { result = new { structuredContent = new
                            { documentId = "created-item", driveId = "drive-id", content = Content, comments = Comments } } });
                        }
                        if (tool is "ReplyToComment" or "WordReplyToComment")
                        {
                            Replies++; LastReply = body["params"]!["arguments"]!.DeepClone();
                            return Reply(new { result = new { content = new[] { new { type = "text", text = "WordCommentInfo [CommentId=ABC01234, Content=Reply]" } } } });
                        }
                        break;
                }
                throw new InvalidOperationException("Unexpected Word operation.");
            }
            if (request.RequestUri.AbsolutePath.Contains("/messages/", StringComparison.Ordinal))
            {
                MessageUri = request.RequestUri;
                return Reply(Notification());
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/invite", StringComparison.Ordinal))
            {
                Invites++; LastInvite = body;
                if (FailSharing) return new(HttpStatusCode.BadRequest);
                return Reply(new { value = new[] { new { id = "permission", roles = new[] { PermissionRole }, grantedToV2 = new { user = new { id = Caller.Id } } } } });
            }
            if (!Exists) return new(HttpStatusCode.NotFound);
            return Reply(new
            {
                id = "created-item", name = Brief.FileName, webUrl = Brief.Url, createdDateTime = Created,
                createdBy = new { user = new { id = WrongCreator ? Guid.NewGuid() : AgentUser } },
                sharepointIds = new { listItemUniqueId = DocumentId }
            });
        }

        public void Dispose() => _http.Dispose();
    }

    private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request);
    }
}
