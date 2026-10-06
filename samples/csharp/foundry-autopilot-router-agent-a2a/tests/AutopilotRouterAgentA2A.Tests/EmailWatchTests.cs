using System.Net;
using System.Text;
using Azure;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.AgentLogic;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Xunit;

namespace WorkstreamManagerAgent.Tests;

public class EmailWatchTests
{
    private const string Manager = "ce327c62-1afd-4f40-991b-57420f681c45";
    private const string Teammate = "11111111-2222-3333-4444-555555555555";
    private const string Chat = "19:manager_office@unq.gbl.spaces";
    private static readonly StandingJobMember Sustineo = new("aaaaaaaa-0000-0000-0000-000000000001", "sustineo@notareal.co", "Sustineo Juarez");

    [Fact]
    public void ToolsAreOfferedOnlyWithStorageAndGraph()
    {
        Assert.Empty(new Fixture(useStore: false).Tools.GetToolDefinitions());
        Assert.Empty(new Fixture(graphToken: null).Tools.GetToolDefinitions());
        Assert.Empty(new Fixture(enabled: false).Tools.GetToolDefinitions());
        Assert.Equal(["watch_email_from", "list_email_watches", "stop_email_watch"],
            new Fixture().Tools.GetToolDefinitions().Select(tool => tool["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ManagerCanWatchSomeoneByNameInTheirOneToOneChat()
    {
        var fixture = new Fixture();
        fixture.Turn(Manager);
        var result = await fixture.Tools.TryExecuteAsync("watch_email_from", """{"sender":"Sustineo","note":"reply about the 9pm meeting"}""");
        Assert.Contains("Watch saved for email from Sustineo Juarez (sustineo@notareal.co)", result);
        Assert.Contains("officeofamanda@notareal.co", result);
        var watch = Assert.Single(fixture.Store.Rows);
        Assert.Equal(Chat, watch.ConversationId);
        Assert.Equal(Manager, watch.RequesterId);
        Assert.False(watch.Repeat);
        Assert.True(watch.Matches("SUSTINEO@notareal.co"));
        Assert.Equal("reply about the 9pm meeting", watch.Note);
    }

    [Fact]
    public async Task OnlyTheManagerCanSetAWatch()
    {
        var fixture = new Fixture();
        fixture.Turn(Teammate);
        var result = await fixture.Tools.TryExecuteAsync("watch_email_from", """{"sender":"sustineo@notareal.co"}""");
        Assert.Contains("Only your manager", result);
        Assert.Empty(fixture.Store.Rows);
    }

    [Fact]
    public async Task WatchesAreNotSetInGroupChats()
    {
        var fixture = new Fixture();
        fixture.Turn(Manager, conversationType: "groupChat");
        var result = await fixture.Tools.TryExecuteAsync("watch_email_from", """{"sender":"sustineo@notareal.co"}""");
        Assert.Contains("1:1 chat", result);
        Assert.Empty(fixture.Store.Rows);
    }

    [Fact]
    public async Task AnUnknownNameIsNotGuessed()
    {
        var fixture = new Fixture();
        fixture.Turn(Manager);
        var result = await fixture.Tools.TryExecuteAsync("watch_email_from", """{"sender":"Somebody Else"}""");
        Assert.Contains("Could not find exactly one person", result);
        Assert.Empty(fixture.Store.Rows);
    }

    [Fact]
    public async Task AnOutsideAddressIsAcceptedAsTyped()
    {
        var fixture = new Fixture();
        fixture.Turn(Manager);
        var result = await fixture.Tools.TryExecuteAsync("watch_email_from", """{"sender":"Partner@Contoso.com","repeat":"true"}""");
        Assert.Contains("partner@contoso.com", result);
        var watch = Assert.Single(fixture.Store.Rows);
        Assert.True(watch.Repeat);
        Assert.Equal("partner@contoso.com", watch.SenderAddress);
    }

    [Fact]
    public async Task AOneTimeWatchPostsOnceAndThenStops()
    {
        var fixture = new Fixture();
        await fixture.WatchSustineoAsync();
        Assert.Equal(1, await fixture.Tools.NotifyAsync("msg-1", null, null, null));
        var (chat, html) = Assert.Single(fixture.Posts);
        Assert.Equal(Chat, chat);
        Assert.Contains("Sustineo Juarez", html);
        Assert.Contains("Re: 9pm meeting", html);
        Assert.Contains("Sounds good, 9pm works.", html);
        Assert.Contains("stopped watching", html);
        Assert.Empty(fixture.Store.Rows);
        Assert.Equal(0, await fixture.Tools.NotifyAsync("msg-2", null, null, null));
        Assert.Single(fixture.Posts);
    }

    [Fact]
    public async Task ARepeatingWatchPostsForEachNewEmailButNotForADuplicate()
    {
        var fixture = new Fixture();
        await fixture.WatchSustineoAsync(repeat: true);
        Assert.Equal(1, await fixture.Tools.NotifyAsync("msg-1", null, null, null));
        Assert.Equal(0, await fixture.Tools.NotifyAsync("msg-1", null, null, null));
        Assert.Equal(1, await fixture.Tools.NotifyAsync("msg-2", null, null, null));
        Assert.Equal(2, fixture.Posts.Count);
        Assert.Equal(2, Assert.Single(fixture.Store.Rows).NotifiedCount);
        Assert.Contains("keep watching", fixture.Posts[1].Html);
    }

    [Fact]
    public async Task OtherSendersAreIgnoredAndNoMailIsReadWithoutAWatch()
    {
        var fixture = new Fixture();
        Assert.Equal(0, await fixture.Tools.NotifyAsync("msg-1", null, null, null));
        Assert.Empty(fixture.Graph.Calls);

        await fixture.WatchSustineoAsync();
        fixture.FromAddress = "someone@notareal.co";
        Assert.Equal(0, await fixture.Tools.NotifyAsync("msg-3", null, null, null));
        Assert.Empty(fixture.Posts);
        Assert.Single(fixture.Store.Rows);
    }

    [Fact]
    public async Task TheNotificationSenderIsUsedWhenTheMessageCannotBeRead()
    {
        var fixture = new Fixture();
        await fixture.WatchSustineoAsync();
        fixture.MessageReadable = false;
        Assert.Equal(1, await fixture.Tools.NotifyAsync("msg-1", "sustineo@notareal.co", "Fallback subject", "Fallback body"));
        Assert.Contains("Fallback subject", fixture.Posts.Single().Html);
    }

    [Fact]
    public async Task AFailedNoticeKeepsTheWatch()
    {
        var fixture = new Fixture { FailPosts = true };
        await fixture.WatchSustineoAsync();
        Assert.Equal(0, await fixture.Tools.NotifyAsync("msg-1", null, null, null));
        Assert.Single(fixture.Store.Rows);
    }

    [Fact]
    public async Task StopRemovesOnlyTheMatchingWatchInThisChat()
    {
        var fixture = new Fixture();
        await fixture.WatchSustineoAsync();
        fixture.Turn(Manager);
        await fixture.Tools.TryExecuteAsync("watch_email_from", """{"sender":"partner@contoso.com"}""");
        var listed = await fixture.Tools.TryExecuteAsync("list_email_watches", "{}");
        Assert.Contains("Sustineo Juarez (sustineo@notareal.co): next email only", listed);
        Assert.Contains("partner@contoso.com", listed);

        Assert.Contains("Stopped 1", await fixture.Tools.TryExecuteAsync("stop_email_watch", """{"sender":"Sustineo"}"""));
        Assert.Equal("partner@contoso.com", Assert.Single(fixture.Store.Rows).SenderAddress);

        fixture.Turn(Manager, chat: "19:another@unq.gbl.spaces");
        Assert.Equal("No email watches are set up in this chat.", await fixture.Tools.TryExecuteAsync("list_email_watches", "{}"));
    }

    [Fact]
    public void TheNoticeEncodesEmailContent()
    {
        var html = EmailWatchToolHandler.BuildNotice(new EmailWatchEntity { SenderName = "X" },
            "<b>Name</b>", "x@example.com", "<script>alert(1)</script>", "a & b");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a &amp; b", html);
    }

    [Fact]
    public void RowKeysAreScopedToTheChatAndIgnoreAddressCase()
    {
        var key = EmailWatchStore.RowKeyFor(Chat, "Sustineo@NotARealCo.co");
        Assert.StartsWith(EmailWatchStore.RowPrefix, key);
        Assert.Equal(key, EmailWatchStore.RowKeyFor(Chat, "sustineo@notarealco.co"));
        Assert.NotEqual(key, EmailWatchStore.RowKeyFor("19:other", "sustineo@notarealco.co"));
    }

    [Fact]
    public void ThePromptDescribesWatchesOnlyWhenTheToolsAreAttached()
    {
        var on = AgentInstructions.GetInstructions(new AgentMetadata(), routinesEnabled: true, emailWatchesEnabled: true);
        Assert.Contains("watch_email_from", on);
        Assert.Contains("Do NOT create a routine to poll", on);
        var off = AgentInstructions.GetInstructions(new AgentMetadata(), routinesEnabled: true);
        Assert.DoesNotContain("watch_email_from", off);
    }

    private sealed class Fixture
    {
        public MemoryStore Store { get; } = new();
        public GraphHandler Graph { get; }
        public EmailWatchToolHandler Tools { get; }
        public List<(string Chat, string Html)> Posts { get; } = [];
        public bool FailPosts { get; set; }
        public string FromAddress { get => Graph.From; set => Graph.From = value; }
        public bool MessageReadable { get => Graph.Readable; set => Graph.Readable = value; }

        public Fixture(bool useStore = true, string? graphToken = "agent-user-token", bool enabled = true)
        {
            Graph = new GraphHandler();
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["EnableEmailWatches"] = enabled ? "true" : "false" }).Build();
            Tools = new EmailWatchToolHandler(useStore ? Store : null,
                new AgentMetadata { TenantId = Guid.NewGuid(), UserId = Guid.NewGuid() },
                config, NullLogger.Instance, new HttpClient(Graph), graphToken,
                identifier => Task.FromResult(identifier.Contains("sustineo", StringComparison.OrdinalIgnoreCase) ? Sustineo : null),
                (chat, html) =>
                {
                    if (FailPosts) throw new HttpRequestException("rejected");
                    Posts.Add((chat, html));
                    return Task.FromResult("receipt-" + Posts.Count);
                });
        }

        public void Turn(string callerId, string conversationType = "personal", string chat = Chat) =>
            Tools.SetTurn(new Activity
            {
                Type = ActivityTypes.Message, ChannelId = "msteams",
                Conversation = new ConversationAccount { Id = chat, ConversationType = conversationType }
            }, () => Task.FromResult<StandingJobCaller?>(new StandingJobCaller(callerId, Manager, null)));

        public async Task WatchSustineoAsync(bool repeat = false)
        {
            Turn(Manager);
            var result = await Tools.TryExecuteAsync("watch_email_from",
                $$"""{"sender":"sustineo@notareal.co","repeat":{{(repeat ? "true" : "false")}}}""");
            Assert.Contains("Watch saved", result);
        }
    }

    private sealed class GraphHandler : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public string From { get; set; } = "sustineo@notareal.co";
        public bool Readable { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.ToString();
            Calls.Add(url);
            if (url.Contains("/me?"))
                return Task.FromResult(Json("""{"mail":"officeofamanda@notareal.co"}"""));
            if (url.Contains("/me/messages/") && Readable)
            {
                var id = request.RequestUri.Segments.Last();
                var body = "{\"id\":\"" + id + "\",\"subject\":\"Re: 9pm meeting\",\"bodyPreview\":\"Sounds good, 9pm works.\","
                    + "\"from\":{\"emailAddress\":{\"name\":\"Sustineo Juarez\",\"address\":\"" + From + "\"}}}";
                return Task.FromResult(Json(body));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class MemoryStore : IEmailWatchStore
    {
        private readonly Dictionary<string, (EmailWatchEntity Entity, int Version)> _rows = [];
        public bool IsAvailable => true;
        public IReadOnlyList<EmailWatchEntity> Rows => _rows.Values.Select(row => row.Entity.Copy()).ToList();

        public Task SaveAsync(EmailWatchEntity watch)
        {
            var version = _rows.TryGetValue(watch.RowKey, out var row) ? row.Version + 1 : 1;
            watch.ETag = new ETag(version.ToString());
            _rows[watch.RowKey] = (watch.Copy(), version);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EmailWatchEntity>> ListAsync(string partition) =>
            Task.FromResult<IReadOnlyList<EmailWatchEntity>>(
                _rows.Values.Where(row => row.Entity.PartitionKey == partition).Select(row => row.Entity.Copy()).ToList());

        public Task<bool> RemoveAsync(EmailWatchEntity watch)
        {
            if (!_rows.TryGetValue(watch.RowKey, out var row) || row.Entity.ETag != watch.ETag)
                return Task.FromResult(false);
            _rows.Remove(watch.RowKey);
            return Task.FromResult(true);
        }

        public Task<bool> ReplaceAsync(EmailWatchEntity watch)
        {
            if (!_rows.TryGetValue(watch.RowKey, out var row) || row.Entity.ETag != watch.ETag)
                return Task.FromResult(false);
            watch.ETag = new ETag((row.Version + 1).ToString());
            _rows[watch.RowKey] = (watch.Copy(), row.Version + 1);
            return Task.FromResult(true);
        }
    }
}
