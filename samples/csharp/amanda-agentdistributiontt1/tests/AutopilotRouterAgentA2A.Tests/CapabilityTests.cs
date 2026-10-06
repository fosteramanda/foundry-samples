using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
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
            ["send_email_as_agent", "create_calendar_event_for_agent", "list_agent_calendar", "get_manager_contact"],
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

    [Fact]
    public void ADisabledDelegationPathDoesNotAdvertiseADelegateOrItsTools()
    {
        var prompt = AgentInstructions.GetInstructions(new AgentMetadata(),
            sourceOfTruthAgentId: "specialist-id", sourceOfTruthAgentName: "Specialist",
            delegationEnabled: false);
        Assert.DoesNotContain("ask_workiq_agent", prompt);
        Assert.DoesNotContain("list_workiq_agents", prompt);
        Assert.DoesNotContain("specialist-id", prompt);
    }

    [Fact]
    public void PinnedDelegateUsesTheActualA2ASchema()
    {
        var prompt = AgentInstructions.GetInstructions(new AgentMetadata(),
            sourceOfTruthAgentId: "specialist-id", sourceOfTruthAgentName: "Specialist");
        Assert.Contains("ask_workiq_agent A2A tool", prompt);
        Assert.Contains("agent_id=\"specialist-id\"", prompt);
        Assert.DoesNotContain("Reach it with the Work IQ `ask`", prompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurnInstructionsOnlyAdvertiseAttachedLocalTools(bool includeDelegation)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var client = new ResponsesApiClient(new AgentMetadata(), NullLogger.Instance,
            Config(("AzureOpenAIEndpoint", "https://example.openai.azure.com"), ("ModelDeployment", "test")),
            "test-token", [], http)
        {
            AgentMailboxEnabled = true,
            ManagerMailboxEnabled = true,
            RoutinesEnabled = true,
            MeetingRegistryEnabled = true,
            WorkItemsEnabled = true,
        };
        List<JsonNode>? tools = includeDelegation
            ? [JsonNode.Parse("""{"type":"function","name":"ask_workiq_agent","parameters":{"type":"object","properties":{}}}""")!]
            : null;
        var prompt = client.BuildTurnInstructions(tools);
        Assert.Empty(handler.Calls);
        Assert.Equal(includeDelegation, prompt.Contains("ask_workiq_agent", StringComparison.Ordinal));
        foreach (var absent in new[] { "send_email_as_agent", "send_email_as_manager", "create_routine", "create_work_item", "track_meeting" })
        {
            Assert.DoesNotContain(absent, prompt);
        }
    }

    [Fact]
    public async Task ToolIterationLimitDoesNotPersistAnUnfinishedResponse()
    {
        RecordingHandler? handler = null;
        handler = new RecordingHandler(responder: _ =>
        {
            var call = handler!.Calls.Count;
            var body = call <= 11
                ? $$"""{"id":"resp-{{call}}","status":"completed","output":[{"type":"function_call","call_id":"call-{{call}}","name":"test_tool","arguments":"{}"}]}"""
                : """{"id":"resp-final","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"recovered"}]}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var client = new ResponsesApiClient(new AgentMetadata(), NullLogger.Instance,
            Config(("AzureOpenAIEndpoint", "https://example.openai.azure.com"), ("ModelDeployment", "test")),
            "test-token", [], http, responseCredential: new TestTokenCredential());
        var tools = new List<JsonNode>
        {
            JsonNode.Parse("""{"type":"function","name":"test_tool","parameters":{"type":"object","properties":{}}}""")!
        };
        var conversationId = "tool-limit-" + Guid.NewGuid();

        await client.InvokeAsync("first", conversationId, includeMcpTools: false,
            additionalTools: tools, localToolExecutor: (_, _) => Task.FromResult<string?>("{}"));
        var result = await client.InvokeAsync("second", conversationId, includeMcpTools: false,
            additionalTools: tools, localToolExecutor: (_, _) => Task.FromResult<string?>("{}"));

        Assert.Equal("recovered", result);
        Assert.Equal(12, handler.Calls.Count);
        Assert.DoesNotContain("previous_response_id", handler.Calls[11].Body);
        client.ClearPreviousResponseId(conversationId);
    }

    [Fact]
    public async Task MissingToolOutputClearsTheBrokenChainAndRetriesFresh()
    {
        RecordingHandler? handler = null;
        handler = new RecordingHandler(responder: _ =>
        {
            var call = handler!.Calls.Count;
            if (call == 1)
            {
                return JsonResponse("""{"id":"resp-old","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"saved"}]}]}""");
            }
            if (call == 2)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"No tool output found for function call call-broken.","type":"invalid_request_error","param":"input","code":null}}""",
                        Encoding.UTF8, "application/json")
                };
            }
            return JsonResponse("""{"id":"resp-new","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"recovered"}]}]}""");
        });
        using var http = new HttpClient(handler);
        var client = new ResponsesApiClient(new AgentMetadata(), NullLogger.Instance,
            Config(("AzureOpenAIEndpoint", "https://example.openai.azure.com"), ("ModelDeployment", "test")),
            "test-token", [], http, responseCredential: new TestTokenCredential());
        var conversationId = "broken-chain-" + Guid.NewGuid();

        Assert.Equal("saved", await client.InvokeAsync("first", conversationId, includeMcpTools: false));
        Assert.Equal("recovered", await client.InvokeAsync("second", conversationId, includeMcpTools: false));

        Assert.Equal(3, handler.Calls.Count);
        Assert.Contains("previous_response_id", handler.Calls[1].Body);
        Assert.DoesNotContain("previous_response_id", handler.Calls[2].Body);
        client.ClearPreviousResponseId(conversationId);
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

    [Fact]
    public async Task ManagerContactIsResolvedWithoutSendingOrCreatingAnything()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"mail":"manager@example.com","displayName":"Manager"}""");
        using var http = new HttpClient(handler);
        var tools = new MailboxToolHandler(new AgentMetadata { UserId = Guid.NewGuid() },
            NullLogger.Instance, http, Config(), "test-token");
        var result = await tools.TryExecuteAsync("get_manager_contact", "{}");
        Assert.Equal("manager@example.com", JsonNode.Parse(result!)!["email"]!.GetValue<string>());
        Assert.EndsWith("/manager", Assert.Single(handler.Calls).Uri.AbsolutePath);
    }

    [Fact]
    public async Task ScheduledChatUsesTheAgentGraphTokenAndRequiresAReceipt()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, """{"id":"message-1"}""");
        using var http = new HttpClient(handler);
        var delivery = new ScheduledChatDelivery(http, "agent-user-token", NullLogger.Instance);
        var activity = new Activity
        {
            Type = ActivityTypes.Message, ChannelId = "msteams",
            Conversation = new ConversationAccount { Id = "19:test@unq.gbl.spaces" }
        };
        await delivery.SendAsync(activity, "<p>Scheduled check-in</p>", CancellationToken.None);
        var call = Assert.Single(handler.Calls);
        Assert.EndsWith("/messages", call.Uri.AbsolutePath);
        Assert.Contains("19%3Atest", call.Uri.AbsoluteUri);
        Assert.Equal("Scheduled check-in", System.Text.RegularExpressions.Regex.Replace(
            JsonNode.Parse(call.Body!)!["body"]!["content"]!.GetValue<string>(), "<[^>]+>", ""));
        activity.Id = "user-message";
        Assert.False(ScheduledChatDelivery.IsScheduledChat(activity));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"Unauthorized"}}""")]
    [InlineData(HttpStatusCode.Created, """{}""")]
    public async Task ScheduledDeliveryNeverRetriesAnUnconfirmedPost(HttpStatusCode status, string body)
    {
        var handler = new RecordingHandler(status, body);
        using var http = new HttpClient(handler);
        var delivery = new ScheduledChatDelivery(http, "agent-user-token", NullLogger.Instance);
        var activity = new Activity
        {
            Type = ActivityTypes.Message, ChannelId = "msteams",
            Conversation = new ConversationAccount { Id = "19:test@unq.gbl.spaces" }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => delivery.SendAsync(activity, "Test", CancellationToken.None));
        Assert.Single(handler.Calls);
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

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class TestTokenCredential : TokenCredential
    {
        private static readonly AccessToken Token = new("test-token", DateTimeOffset.MaxValue);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            Token;

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Token);
    }
}
