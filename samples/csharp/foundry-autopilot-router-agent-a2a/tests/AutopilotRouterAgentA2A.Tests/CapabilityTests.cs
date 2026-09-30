using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.AgentLogic;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Xunit;

namespace WorkstreamManagerAgent.Tests;

public class CapabilityTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(value => value.Key, value => value.Value)).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConversationStorageInheritsConfiguredWorkItemStorage(string? overrideUri)
    {
        var config = Config(("ConversationStateTableServiceUri", overrideUri),
            ("WorkItemsTableServiceUri", "https://example.table.core.windows.net"));
        Assert.Equal("https://example.table.core.windows.net", ConversationStateStore.ResolveTableServiceUri(config));
    }

    [Fact]
    public void ConversationStorageKeepsAnExplicitOverride()
    {
        var config = Config(("ConversationStateTableServiceUri", "https://override.table.core.windows.net"),
            ("WorkItemsTableServiceUri", "https://example.table.core.windows.net"));
        Assert.Equal("https://override.table.core.windows.net", ConversationStateStore.ResolveTableServiceUri(config));
    }

    [Fact]
    public void MailboxToolsDefaultToTheAgentIdentity()
    {
        using var http = new HttpClient(new RecordingHandler());
        var tools = new MailboxToolHandler(new AgentMetadata { UserId = Guid.NewGuid() },
            NullLogger.Instance, http, Config(), "test-token");
        Assert.False(tools.ActsAsManager);
        Assert.Equal(
            ["send_email_as_agent", "create_calendar_event_for_agent", "list_agent_calendar"],
            tools.GetToolDefinitions().Select(tool => tool["name"]!.GetValue<string>()));
        Assert.All(tools.GetToolDefinitions(),
            tool => Assert.DoesNotContain("FROM the manager", tool["description"]!.GetValue<string>()));
    }

    [Fact]
    public void LegacyManagerToolsRequireExplicitOptIn()
    {
        using var http = new HttpClient(new RecordingHandler());
        var tools = new MailboxToolHandler(new AgentMetadata { UserId = Guid.NewGuid() },
            NullLogger.Instance, http, Config(("EnableManagerMailboxTools", "true")), "test-token");
        Assert.True(tools.ActsAsManager);
        Assert.Contains(tools.GetToolDefinitions(), tool => tool["name"]!.GetValue<string>() == "send_email_as_manager");
        Assert.DoesNotContain(tools.GetToolDefinitions(), tool => tool["name"]!.GetValue<string>() == "send_email_as_agent");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MailboxToolsAreNotAdvertisedWithoutGraphCredentials(string? token)
    {
        using var http = new HttpClient(new RecordingHandler());
        var tools = new MailboxToolHandler(new AgentMetadata { UserId = Guid.NewGuid() },
            NullLogger.Instance, http, Config(), token);
        Assert.Empty(tools.GetToolDefinitions());
    }

    [Fact]
    public async Task SendUsesOnlyTheAgentMailboxAndNeverResolvesTheManager()
    {
        var agentId = Guid.NewGuid();
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        using var http = new HttpClient(handler);
        var tools = new MailboxToolHandler(new AgentMetadata { UserId = agentId },
            NullLogger.Instance, http, Config(("ManagerMailboxUpn", "other@example.com")), "test-token");
        var result = await tools.TryExecuteAsync("send_email_as_agent",
            """{"to":["recipient@example.com"],"subject":"Test","body_html":"<p>Test</p>"}""");
        var call = Assert.Single(handler.Calls);
        Assert.Equal($"/v1.0/users/{agentId:D}/sendMail", call.Uri.AbsolutePath);
        Assert.Null(JsonNode.Parse(call.Body!)!["message"]!["from"]);
        Assert.Contains("my own agent mailbox", result);
        Assert.Null(await tools.TryExecuteAsync("send_email_as_manager", "{}"));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task CalendarCreationUsesTheAgentAsOrganizer()
    {
        var agentId = Guid.NewGuid();
        var handler = new RecordingHandler(HttpStatusCode.Created, """{"webLink":"https://example.com/event"}""");
        using var http = new HttpClient(handler);
        var tools = new MailboxToolHandler(new AgentMetadata { UserId = agentId },
            NullLogger.Instance, http, Config(), "test-token");
        await tools.TryExecuteAsync("create_calendar_event_for_agent",
            """{"subject":"Demo","start":"2026-09-30T10:00:00","end":"2026-09-30T10:30:00","attendees":["manager@example.com"]}""");
        var call = Assert.Single(handler.Calls);
        Assert.Equal($"/v1.0/users/{agentId:D}/events", call.Uri.AbsolutePath);
        var body = JsonNode.Parse(call.Body!)!;
        Assert.Null(body["organizer"]);
        Assert.Equal("manager@example.com", body["attendees"]![0]!["emailAddress"]!["address"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("2026-09-30T09:00:00", "2026-09-30T16:00:00Z")]
    [InlineData("2026-09-30", "2026-09-30T07:00:00Z")]
    [InlineData("2026-09-30T09:00:00-04:00", "2026-09-30T13:00:00Z")]
    [InlineData("2026-09-30T09:00:00Z", "2026-09-30T09:00:00Z")]
    public void CalendarRangeRespectsTheRequestedTimeZone(string input, string expected)
    {
        Assert.Equal(expected, MailboxToolHandler.CalendarRangeUtc(input, "Pacific Standard Time"));
    }

    [Fact]
    public async Task CalendarRangeDoesNotHidePagination()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"value":[{"subject":"Meeting"}],"@odata.nextLink":"https://graph.microsoft.com/next"}""");
        using var http = new HttpClient(handler);
        var tools = new MailboxToolHandler(new AgentMetadata { UserId = Guid.NewGuid() },
            NullLogger.Instance, http, Config(), "test-token");
        var result = await tools.TryExecuteAsync("list_agent_calendar",
            """{"start":"2026-09-30T00:00:00","end":"2026-10-01T00:00:00"}""");
        Assert.True(JsonNode.Parse(result!)!["partial"]!.GetValue<bool>());
        Assert.Contains("2026-09-30T07:00:00Z", Uri.UnescapeDataString(Assert.Single(handler.Calls).Uri.Query));
    }

    [Fact]
    public async Task MailFailureDoesNotClaimSuccessOrSwitchMailboxes()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, """{"error":{"code":"AccessDenied"}}""");
        using var http = new HttpClient(handler);
        var tools = new MailboxToolHandler(new AgentMetadata { UserId = Guid.NewGuid() },
            NullLogger.Instance, http, Config(), "test-token");
        var result = await tools.TryExecuteAsync("send_email_as_agent",
            """{"to":["recipient@example.com"],"subject":"Test","body_html":"Test"}""");
        Assert.Contains("No action succeeded", result);
        Assert.Contains("403", result);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public void AgentInstructionsKeepTheManagerAndAgentDistinct()
    {
        var prompt = AgentInstructions.GetInstructions(new AgentMetadata(), agentMailboxEnabled: true);
        Assert.Contains("act as yourself", prompt);
        Assert.Contains("send_email_as_agent", prompt);
        Assert.DoesNotContain("send_email_as_manager", prompt);
        Assert.DoesNotContain("Delegation goes through the Work IQ `ask` tool", prompt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotificationSenderMustBeTheManagerOrAnApprovedTeammate(bool isManager)
    {
        var managerId = Guid.NewGuid().ToString();
        var senderId = isManager ? managerId : Guid.NewGuid().ToString();
        var handler = new RecordingHandler(responder: request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/manager")
                    ? $$"""{"id":"{{managerId}}","displayName":"Manager"}"""
                    : $$"""{"id":"{{senderId}}","displayName":"Sender"}""")
            });
        using var http = new HttpClient(handler);
        var agent = new AgentMetadata { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var work = new WorkItemToolHandler(agent, NullLogger.Instance, "test-token", http, null,
            new ReactionService(NullLogger.Instance, "test-token", http));
        var access = new AccessControlService(agent, NullLogger.Instance, Config(), "test-token",
            http, new TeamsActivityHelper(NullLogger.Instance), work);
        Assert.Equal(isManager, await access.IsNotificationSenderApprovedAsync(new ChannelAccount { Id = senderId }));
    }

    [Fact]
    public async Task NotificationRejectsAnOutOfTenantSenderBeforeCallingGraph()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var agent = new AgentMetadata { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var work = new WorkItemToolHandler(agent, NullLogger.Instance, "test-token", http, null,
            new ReactionService(NullLogger.Instance, "test-token", http));
        var access = new AccessControlService(agent, NullLogger.Instance, Config(), "test-token",
            http, new TeamsActivityHelper(NullLogger.Instance), work);
        Assert.False(await access.IsNotificationSenderApprovedAsync(
            new ChannelAccount { Id = Guid.NewGuid().ToString(), TenantId = Guid.NewGuid().ToString() }));
        Assert.Empty(handler.Calls);
    }

    private sealed class RecordingHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string responseBody = "{}",
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Add((request.RequestUri!, request.Content == null ? null : await request.Content.ReadAsStringAsync(token)));
            return responder?.Invoke(request) ?? new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
