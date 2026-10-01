using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkstreamManager.Models;

namespace WorkstreamManager.Services;

internal sealed record StandingWordDocument(
    string ItemId, string DriveId, string Content, IReadOnlyDictionary<string, string> Comments);

internal sealed class StandingWordClient(HttpClient http, McpServerConfig? server, string token)
{
    private string? _session;
    private string _protocol = "2025-03-26";
    private Dictionary<string, string>? _tools;

    internal async Task<JsonNode> CallAsync(string operation, JsonObject arguments)
    {
        if (server == null)
            throw new InvalidOperationException("The Word tool server is unavailable.");
        if (_tools == null)
        {
            var initialized = await RpcAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = _protocol,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "standing-review", ["version"] = "2.0" }
            });
            _protocol = initialized["protocolVersion"]?.GetValue<string>() ?? _protocol;
            await RpcAsync("notifications/initialized", null, notification: true);
            var advertised = await RpcAsync("tools/list", new JsonObject());
            var names = (advertised["tools"] as JsonArray
                ?? throw new InvalidOperationException("Word returned no tool inventory."))
                .Select(tool => tool?["name"]?.GetValue<string>())
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            _tools = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, aliases) in new Dictionary<string, string[]>
            {
                ["create"] = ["CreateDocument", "WordCreateNewDocument"],
                ["read"] = ["GetDocumentContent", "WordGetDocumentContent"],
                ["reply"] = ["ReplyToComment", "WordReplyToComment"]
            })
            {
                var name = aliases.FirstOrDefault(names.Contains);
                if (name != null) _tools[key] = name;
            }
        }
        if (!_tools.TryGetValue(operation, out var tool))
            throw new InvalidOperationException($"The Word server does not advertise the required {operation} operation.");
        return await RpcAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });
    }

    internal async Task<StandingWordDocument> ReadAsync(StandingJobBrief brief)
    {
        var payload = StandingBriefPublisher.ToolPayload(await CallAsync("read",
            new JsonObject { ["url"] = brief.Url }));
        var item = payload["documentId"]?.GetValue<string>();
        if (item != brief.ItemId)
            throw new InvalidOperationException("Word returned a different document than the bound brief.");
        var drive = payload["driveId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Word returned no drive identity.");
        var content = payload["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Word returned no document content.");
        var comments = ParseComments(payload["comments"]?.GetValue<string>() ?? string.Empty);
        return new StandingWordDocument(item, drive, content, comments);
    }

    internal async Task<string> ReplyAsync(StandingJobEvent source, string reply)
    {
        if (source.DocumentItemId == null || source.DriveId == null || source.CommentId == null)
            throw new InvalidOperationException("The source has no verified Word comment target.");
        var result = await CallAsync("reply", new JsonObject
        {
            ["driveId"] = source.DriveId, ["documentId"] = source.DocumentItemId,
            ["commentId"] = source.CommentId, ["newComment"] = reply
        });
        var text = string.Join("\n", (result["content"] as JsonArray ?? [])
            .Select(block => block?["text"]?.GetValue<string>()).OfType<string>());
        var id = Regex.Match(text, @"\bCommentId\s*=\s*([A-Fa-f0-9]+)\b").Groups[1].Value;
        if (id.Length == 0)
            id = result["structuredContent"]?["commentId"]?.GetValue<string>() ?? string.Empty;
        if (id.Length == 0)
            throw new InvalidOperationException("Word did not return a reply receipt; do not post again.");
        return id;
    }

    internal static IReadOnlyDictionary<string, string> ParseComments(string text)
    {
        var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("No comments found.", StringComparison.OrdinalIgnoreCase))
            return comments;
        var matches = Regex.Matches(text,
            @"(?:^|\r?\n)Comment (?<id>[A-Fa-f0-9]+):[ \t]*(?<text>.*?)(?=\r?\nAnchor \k<id>:|\r?\nComment [A-Fa-f0-9]+:|\z)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        foreach (Match match in matches)
            if (!comments.TryAdd(match.Groups["id"].Value.ToUpperInvariant(), match.Groups["text"].Value.Trim()))
                throw new InvalidOperationException("Word returned an ambiguous comment identity.");
        if (comments.Count == 0)
            throw new InvalidOperationException("The Word comment response format was not recognized; no comment was ingested.");
        return comments;
    }

    private async Task<JsonNode> RpcAsync(string method, JsonNode? parameters, bool notification = false)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification) body["id"] = Guid.NewGuid().ToString("N");
        if (parameters != null) body["params"] = parameters;
        using var request = new HttpRequestMessage(HttpMethod.Post, server!.Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocol);
        if (_session != null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Word {method} failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions))
            _session = sessions.Single();
        if (notification) return new JsonObject();
        var json = text.TrimStart().StartsWith('{') ? text
            : text.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].Trim()).LastOrDefault(line => line.StartsWith('{'));
        var envelope = JsonNode.Parse(json ?? throw new InvalidOperationException("Word returned no RPC response."));
        if (envelope?["error"] != null || envelope?["result"]?["isError"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"Word did not complete {method}; no successful result is claimed.");
        return envelope?["result"] ?? throw new InvalidOperationException("Word returned no operation result.");
    }
}
