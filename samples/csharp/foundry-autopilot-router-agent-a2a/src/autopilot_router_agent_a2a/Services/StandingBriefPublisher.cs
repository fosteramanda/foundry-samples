using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkstreamManager.Models;

namespace WorkstreamManager.Services;

internal sealed record StandingJobBrief(
    int Version, string FileName, string ItemId, string DocumentId, string Url,
    string FactsFingerprint, DateTimeOffset CreatedUtc);

// Graph readback and sharing require delegated Files.ReadWrite on the agent-user
// token. Files.ReadWrite.All is not needed for briefs created in its own drive.
internal sealed class StandingBriefPublisher(
    HttpClient http, McpServerConfig? wordServer, string wordToken, string? graphToken,
    Guid agentUserId, ILogger logger)
{
    internal async Task<StandingJobBrief> PublishAsync(
        StandingJob job, int version, string fingerprint, string content)
    {
        if (wordServer == null || string.IsNullOrWhiteSpace(graphToken))
            throw new InvalidOperationException("Word publication requires the agent's Word and Graph credentials.");
        var name = FileName(job, version);
        var graphItem = await FindExistingAsync(name);
        if (graphItem != null)
        {
            logger.LogInformation("Standing brief publication is reconciling an existing file: job={JobId} version={Version}",
                job.Id, version);
        }
        else
        {
            var initialize = await RpcAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "standing-brief", ["version"] = "1.0" }
            }, null);
            var protocol = initialize.Result?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26";
            await RpcAsync("notifications/initialized", null, initialize.Session, protocol, notification: true);
            var created = await RpcAsync("tools/call", new JsonObject
            {
                ["name"] = "CreateDocument",
                ["arguments"] = new JsonObject { ["fileName"] = name, ["contentInHtml"] = content }
            }, initialize.Session, protocol);
            var payload = ToolPayload(created.Result);
            var item = Property(payload, "driveItem")
                ?? throw new InvalidOperationException("Word returned no created document receipt.");
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
                item = JsonNode.Parse(text) ?? throw new InvalidOperationException("Word returned invalid document metadata.");
            var createdItemId = Property(item, "id")?.GetValue<string>()
                ?? throw new InvalidOperationException("Word returned no drive item ID.");
            graphItem = await GraphAsync(HttpMethod.Get,
                $"me/drive/items/{Uri.EscapeDataString(createdItemId)}?$select=id,name,webUrl,createdBy,sharepointIds", null);
        }
        return await VerifyAndShareAsync(job, version, fingerprint, name, graphItem);
    }

    internal async Task<StandingJobBrief> ReconcileAsync(
        StandingJob job, int version, string fingerprint)
    {
        return await TryReconcileAsync(job, version, fingerprint)
            ?? throw new InvalidOperationException("No existing brief was found for the uncertain publication.");
    }

    internal async Task<StandingJobBrief?> TryReconcileAsync(
        StandingJob job, int version, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(graphToken))
            throw new InvalidOperationException("Word reconciliation requires the agent's Graph credential.");
        var name = FileName(job, version);
        var graphItem = await FindExistingAsync(name);
        if (graphItem == null) return null;
        return await VerifyAndShareAsync(job, version, fingerprint, name, graphItem);
    }

    internal async Task<StandingJobEvent> ReadCommentNotificationAsync(
        StandingJob job, IReadOnlyList<StandingJobBrief> briefs, string messageId)
    {
        if (string.IsNullOrWhiteSpace(graphToken))
            throw new InvalidOperationException("Comment notification verification requires the agent's Graph credential.");
        if (briefs.Count == 0)
            throw new InvalidOperationException("The job has no verified Word brief binding.");
        var message = await GraphAsync(HttpMethod.Get,
            $"me/messages/{Uri.EscapeDataString(messageId)}?$select=id,internetMessageId,subject,bodyPreview,from,receivedDateTime",
            null);
        var sender = message["from"]?["emailAddress"]?["address"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The comment notification has no sender identity.");
        var member = job.Members.SingleOrDefault(item =>
            string.Equals(item.Email, sender, StringComparison.OrdinalIgnoreCase))
            ?? throw new UnauthorizedAccessException("The comment notification sender is not a job participant.");
        var subject = message["subject"]?.GetValue<string>() ?? string.Empty;
        var brief = briefs.SingleOrDefault(item =>
            subject.Contains(Path.GetFileNameWithoutExtension(item.FileName), StringComparison.OrdinalIgnoreCase))
            ?? throw new UnauthorizedAccessException("The notification does not identify a bound job brief.");
        var preview = message["bodyPreview"]?.GetValue<string>() ?? string.Empty;
        var lines = preview.Replace("\r", "").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var marker = lines.FindIndex(line => line.Contains("added a comment", StringComparison.OrdinalIgnoreCase));
        var rawComment = marker >= 0 && marker + 1 < lines.Count
            ? lines[marker + 1]
            : lines.FirstOrDefault(line => line.StartsWith("@Office of Amanda", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(rawComment))
            throw new InvalidOperationException("The notification contains no verifiable Word comment text.");
        var comment = Regex.Replace(rawComment, "^@Office of Amanda[\\s\\u00A0]*", "",
            RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(comment))
            throw new InvalidOperationException("The verified Word comment is empty.");
        var sourceId = message["internetMessageId"]?.GetValue<string>()
            ?? message["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The comment notification has no stable message identity.");
        var received = message["receivedDateTime"]?.GetValue<DateTimeOffset>()
            ?? throw new InvalidOperationException("The comment notification has no received time.");
        return new StandingJobEvent(
            StandingJobStore.Hash("word-comment-notification:" + sourceId), "word", member.Id,
            "word:" + Guid.Parse(brief.DocumentId).ToString("D"), comment, received, true);
    }

    private static string FileName(StandingJob job, int version)
    {
        var safeTitle = new string(job.Title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        if (safeTitle.Length > 90) safeTitle = safeTitle[..90];
        return $"{safeTitle} - {job.Id[..8]} - v{version}.docx";
    }

    private async Task<JsonNode?> FindExistingAsync(string name)
    {
        var filter = Uri.EscapeDataString($"name eq '{name.Replace("'", "''")}'");
        var matches = await GraphAsync(HttpMethod.Get,
            $"me/drive/root/children?$filter={filter}&$select=id,name,webUrl,createdBy,sharepointIds", null);
        var existing = matches["value"] as JsonArray
            ?? throw new InvalidOperationException("The agent drive returned no document listing.");
        var exact = existing.Where(item => string.Equals(
            item?["name"]?.GetValue<string>(), name, StringComparison.Ordinal)).ToList();
        if (exact.Count > 1)
            throw new InvalidOperationException("More than one matching brief exists; reconcile the agent drive manually.");
        return exact.Count == 1 ? exact[0] : null;
    }

    private async Task<StandingJobBrief> VerifyAndShareAsync(
        StandingJob job, int version, string fingerprint, string name, JsonNode graphItem)
    {
        var itemId = graphItem["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The created document has no drive item ID.");
        var creator = graphItem["createdBy"]?["user"]?["id"]?.GetValue<string>();
        if (!Guid.TryParse(creator, out var createdBy) || createdBy != agentUserId)
            throw new InvalidOperationException("The document was not verified as created by this agent user.");
        var url = graphItem["webUrl"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The created document has no openable URL.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The created document did not return a SharePoint URL.");
        var documentId = graphItem["sharepointIds"]?["listItemUniqueId"]?.GetValue<string>();
        if (!Guid.TryParse(documentId, out var documentGuid))
        {
            var sourceDoc = uri.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("sourcedoc", StringComparison.OrdinalIgnoreCase));
            if (sourceDoc == null || !Guid.TryParse(Uri.UnescapeDataString(sourceDoc[1]), out documentGuid))
                throw new InvalidOperationException("The document has no verifiable Word comment identity.");
        }
        var recipients = new JsonArray();
        foreach (var member in job.Members.DistinctBy(member => member.Id))
            recipients.Add(new JsonObject { ["email"] = member.Email });
        var permissions = await GraphAsync(HttpMethod.Post, $"me/drive/items/{Uri.EscapeDataString(itemId)}/invite",
            new JsonObject
            {
                ["recipients"] = recipients, ["roles"] = new JsonArray("write"),
                ["requireSignIn"] = true, ["sendInvitation"] = false
            });
        if (permissions["value"] is not JsonArray granted || granted.Count < recipients.Count
            || granted.Any(permission => permission?["error"] != null))
            throw new InvalidOperationException("Document creation succeeded but sharing was not confirmed.");
        logger.LogInformation("Standing brief created and shared: job={JobId} version={Version} item={ItemId}",
            job.Id, version, itemId);
        return new StandingJobBrief(version, name, itemId, documentGuid.ToString("D"), url,
            fingerprint, DateTimeOffset.UtcNow);
    }

    private async Task<JsonNode> GraphAsync(HttpMethod method, string path, JsonNode? body)
    {
        using var request = new HttpRequestMessage(method, "https://graph.microsoft.com/v1.0/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Brief document operation failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Document operation returned no receipt.");
    }

    private async Task<(JsonNode? Result, string? Session)> RpcAsync(
        string method, JsonNode? parameters, string? session, string protocol = "2025-03-26", bool notification = false)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification) body["id"] = Guid.NewGuid().ToString("N");
        if (parameters != null) body["params"] = parameters;
        using var request = new HttpRequestMessage(HttpMethod.Post, wordServer!.Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wordToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocol);
        if (session != null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Word MCP {method} failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        var returnedSession = response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)
            ? sessions.FirstOrDefault() : session;
        if (notification) return (null, returnedSession);
        var json = text.TrimStart().StartsWith('{') ? text
            : text.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].Trim()).LastOrDefault(line => line.StartsWith('{'));
        var result = JsonNode.Parse(json ?? throw new InvalidOperationException("Word MCP returned no JSON response."));
        if (result?["error"] != null || result?["result"]?["isError"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("Word MCP did not complete the requested document operation.");
        return (result?["result"], returnedSession);
    }

    internal static JsonNode ToolPayload(JsonNode? result)
    {
        if (result?["structuredContent"] is JsonNode structured) return structured;
        if (result?["content"] is JsonArray content)
        {
            foreach (var block in content)
            {
                var text = block?["text"]?.GetValue<string>();
                if (text?.TrimStart().StartsWith('{') == true)
                    return JsonNode.Parse(text)!;
            }
        }
        return result ?? throw new InvalidOperationException("Word returned no tool result.");
    }

    private static JsonNode? Property(JsonNode node, string name) =>
        node is JsonObject obj ? obj.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : null;
}
