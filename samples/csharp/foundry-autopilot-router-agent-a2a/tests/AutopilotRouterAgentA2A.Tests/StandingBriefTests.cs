using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using Xunit;

namespace WorkstreamManagerAgent.Tests;

public class StandingBriefTests
{
    [Fact]
    public async Task CreatesThroughWordThenVerifiesOwnIdentityAndSharesOnlyJobMembers()
    {
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid().ToString();
        var document = Guid.NewGuid();
        var calls = new List<(string Path, string? Body)>();
        using var http = new HttpClient(new Handler(async request =>
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            calls.Add((request.RequestUri!.AbsolutePath, body));
            if (request.RequestUri.Host == "word.example.com")
            {
                var input = JsonNode.Parse(body!)!;
                return input["method"]!.GetValue<string>() switch
                {
                    "initialize" => Reply("""{"jsonrpc":"2.0","id":"1","result":{"protocolVersion":"2025-03-26"}}"""),
                    "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                    "tools/call" => Reply("""{"jsonrpc":"2.0","id":"2","result":{"structuredContent":{"driveItem":{"Id":"created-item"}}}}"""),
                    _ => throw new InvalidOperationException("Unexpected method.")
                };
            }
            if (request.Method == HttpMethod.Get)
                return Reply(System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = "created-item", webUrl = "https://tenant.sharepoint.com/brief",
                    createdBy = new { user = new { id = owner } }, sharepointIds = new { listItemUniqueId = document }
                }));
            return Reply("""{"value":[{"id":"permission"}]}""");
        }));
        var publisher = new StandingBriefPublisher(http, new McpServerConfig { Url = "https://word.example.com/mcp" },
            "word-token", "graph-token", owner, NullLogger.Instance);
        var job = new StandingJob { Id = Guid.NewGuid().ToString(), Title = "Leadership review", Members = [new(member, "manager@example.com")] };
        var result = await publisher.PublishAsync(job, 2, "facts", "<h1>Review</h1>");
        Assert.Equal(document.ToString("D"), result.DocumentId);
        Assert.Contains("v2.docx", result.FileName);
        var create = JsonNode.Parse(calls.Single(call => call.Body?.Contains("CreateDocument") == true).Body!)!;
        Assert.Equal("<h1>Review</h1>", create["params"]!["arguments"]!["contentInHtml"]!.GetValue<string>());
        var share = JsonNode.Parse(calls.Single(call => call.Path.EndsWith("/invite")).Body!)!;
        Assert.Equal(member, share["recipients"]![0]!["objectId"]!.GetValue<string>());
        Assert.Single(share["recipients"]!.AsArray());
        Assert.True(share["requireSignIn"]!.GetValue<bool>());
        Assert.False(share["sendInvitation"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WrongCreatorStopsBeforeSharing()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            requests++;
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
            if (request.Method == HttpMethod.Get)
                return Reply(System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = "item", createdBy = new { user = new { id = Guid.NewGuid() } }
                }));
            var method = JsonNode.Parse(body!)!["method"]!.GetValue<string>();
            return method switch
            {
                "initialize" => Reply("""{"result":{"protocolVersion":"2025-03-26"}}"""),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                _ => Reply("""{"result":{"structuredContent":{"driveItem":{"id":"item"}}}}""")
            };
        }));
        var publisher = new StandingBriefPublisher(http, new McpServerConfig { Url = "https://word.example.com/mcp" },
            "word", "graph", Guid.NewGuid(), NullLogger.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
            new StandingJob { Id = Guid.NewGuid().ToString(), Title = "Review" }, 1, "facts", "Content"));
        Assert.Equal(4, requests);
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

    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Theory]
    [InlineData("The Work IQ A2A call failed: Timeout.")]
    [InlineData("(no response)")]
    [InlineData("Agent 'specialist' accepted the request but returned no content.")]
    [InlineData("")]
    public void AFailedSpecialistCallIsNotACompletedAnswer(string text) =>
        Assert.False(WorkIqA2AToolHandler.HasCompletedAnswer(text));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
