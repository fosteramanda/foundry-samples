using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WorkstreamManager.Models;

namespace WorkstreamManager.Services;

internal sealed record StandingJobBrief(
    int Version, string FileName, string ItemId, string DocumentId, string Url,
    string FactsFingerprint, DateTimeOffset CreatedUtc);

internal sealed record StandingCommentReference(Guid DocumentId, string CommentId, string Url);

internal sealed class StandingBriefPublisher(
    HttpClient http, McpServerConfig? wordServer, string wordToken, string? graphToken,
    Guid agentUserId, ILogger logger)
{
    private readonly StandingWordClient _word = new(http, wordServer, wordToken);
    private const string ItemFields = "id,name,webUrl,createdBy,createdDateTime,sharepointIds";

    internal Task<StandingJobBrief> PublishAsync(StandingJob job, int version, string fingerprint, string content) =>
        PublishCoreAsync(job, version, fingerprint, content, reconcileOnly: false);

    internal Task<StandingJobBrief> ReconcileAsync(StandingJob job, int version, string fingerprint, string content) =>
        PublishCoreAsync(job, version, fingerprint, content, reconcileOnly: true);

    private async Task<StandingJobBrief> PublishCoreAsync(
        StandingJob job, int version, string fingerprint, string content, bool reconcileOnly)
    {
        var name = FileName(job, version);
        var item = await GraphAsync(HttpMethod.Get,
            $"me/drive/root:/{Uri.EscapeDataString(name)}?$select={ItemFields}", null, allowNotFound: true);
        if (item == null)
        {
            if (reconcileOnly)
                throw new InvalidOperationException("The uncertain brief has no verified destination yet; no second creation was attempted.");
            var result = ToolPayload(await _word.CallAsync("create", new JsonObject
            {
                ["fileName"] = name, ["contentInHtml"] = content
            }));
            var created = Property(result, "driveItem")
                ?? throw new InvalidOperationException("Word returned no created file receipt.");
            if (created is JsonValue value && value.TryGetValue<string>(out var metadata))
                created = JsonNode.Parse(metadata) ?? throw new InvalidOperationException("Word returned invalid file metadata.");
            var itemId = Property(created, "id")?.GetValue<string>()
                ?? throw new InvalidOperationException("Word returned no file identity.");
            item = await GraphAsync(HttpMethod.Get,
                $"me/drive/items/{Uri.EscapeDataString(itemId)}?$select={ItemFields}", null);
        }

        var brief = VerifyItem(item!, name, version, fingerprint);
        var readback = await _word.ReadAsync(brief);
        if (NormalizeText(readback.Content) != NormalizeText(XElement.Parse("<div>" + content + "</div>").Value))
            throw new InvalidOperationException("The existing Word version differs from its saved publication draft. It was not overwritten or registered as new evidence.");
        await ShareAsync(job, brief.ItemId);
        logger.LogInformation("Standing brief verified: job={JobId} version={Version} item={ItemId}",
            job.Id, version, brief.ItemId);
        return brief;
    }

    internal Task<StandingWordDocument> ReadAsync(StandingJobBrief brief) => _word.ReadAsync(brief);
    internal async Task<string> ReplyAsync(StandingJobEvent source, string reply)
    {
        if (source.SourceUrl == null || source.DocumentItemId == null || source.CommentId == null)
            throw new InvalidOperationException("The recorded comment has no verified destination.");
        var brief = new StandingJobBrief(0, string.Empty, source.DocumentItemId, source.Binding["word:".Length..],
            source.SourceUrl, string.Empty, source.ReceivedUtc);
        var current = await _word.ReadAsync(brief);
        if (!current.Comments.TryGetValue(source.CommentId, out var text) || text != source.Content)
            throw new InvalidOperationException("The source comment changed before the reply; reread it before responding.");
        return await _word.ReplyAsync(source, reply);
    }

    internal async Task<JsonNode> ReadNotificationAsync(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("The notification needs the message identity supplied by the mail channel.");
        return (await GraphAsync(HttpMethod.Get,
            $"me/messages/{Uri.EscapeDataString(messageId)}?$select=id,internetMessageId,from,receivedDateTime,body,uniqueBody,conversationId,subject,webLink", null))!;
    }

    internal async Task<StandingJobEvent> ReadCommentNotificationAsync(
        StandingJob job, IReadOnlyList<StandingJobBrief> briefs, JsonNode message, StandingJobCaller caller)
    {
        var internetId = message["internetMessageId"]?.GetValue<string>() ?? string.Empty;
        if (!internetId.StartsWith("<CommentWord-", StringComparison.OrdinalIgnoreCase)
            || !internetId.EndsWith("@odspnotify>", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This is not a Word-generated comment notification.");
        var email = message["from"]?["emailAddress"]?["address"]?.GetValue<string>();
        var member = job.Members.SingleOrDefault(person =>
            person.Id == caller.Id && string.Equals(person.Email, email, StringComparison.OrdinalIgnoreCase))
            ?? throw new UnauthorizedAccessException("The notification sender is not the authenticated job participant.");
        var reference = ParseCommentReference(message["body"]?["content"]?.GetValue<string>() ?? string.Empty)
            ?? throw new InvalidOperationException("The Word notification has no verifiable document and comment reference.");
        var brief = briefs.SingleOrDefault(item =>
            Guid.TryParse(item.DocumentId, out var id) && id == reference.DocumentId)
            ?? throw new UnauthorizedAccessException("The comment is not on a registered brief for this job.");
        if (!string.Equals(new Uri(brief.Url).Host, new Uri(reference.Url).Host, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The notification link does not belong to the registered document host.");
        var received = message["receivedDateTime"]?.GetValue<DateTimeOffset>()
            ?? throw new InvalidOperationException("The notification has no received timestamp.");
        return await ReadCommentAsync(brief, reference.CommentId, member.Id, received, reference.Url);
    }

    internal async Task<StandingJobEvent> ReadCommentAsync(
        StandingJobBrief brief, string commentId, string actorId, DateTimeOffset received, string? sourceUrl = null)
    {
        var document = await _word.ReadAsync(brief);
        if (!document.Comments.TryGetValue(commentId, out var content))
            throw new InvalidOperationException("The referenced comment is not present in the real Word document.");
        if (content.Length > 8000)
            throw new InvalidOperationException("The Word comment exceeds the evidence limit; it was not truncated.");
        var binding = "word:" + Guid.Parse(brief.DocumentId).ToString("D");
        var eventId = StandingJobStore.Hash($"{binding}:{commentId.ToUpperInvariant()}:{actorId}:{content}");
        return new StandingJobEvent(eventId, "word", actorId, binding, content, received, true,
            sourceUrl ?? brief.Url, document.ItemId, document.DriveId, commentId.ToUpperInvariant());
    }

    internal static StandingCommentReference? ParseCommentReference(string html)
    {
        var references = new List<StandingCommentReference>();
        foreach (Match match in Regex.Matches(html, """href\s*=\s*["'](?<url>[^"']+)["']""", RegexOptions.IgnoreCase))
        {
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
                continue;
            var query = Query(uri);
            Guid document;
            if (query.TryGetValue("d", out var d) && d.StartsWith('w') && Guid.TryParse(d[1..], out document))
            { }
            else if (!query.TryGetValue("sourcedoc", out var source) || !Guid.TryParse(source, out document))
                continue;
            if (!query.TryGetValue("nav", out var nav)) continue;
            try
            {
                var encoded = nav.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                var data = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                if (data?["c"] is JsonValue c && c.TryGetValue<uint>(out var id))
                    references.Add(new(document, id.ToString("X8", CultureInfo.InvariantCulture), url));
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                throw new InvalidOperationException("The Word notification contains an invalid comment reference.", ex);
            }
        }
        var unique = references.DistinctBy(item => (item.DocumentId, item.CommentId)).ToList();
        if (unique.Count > 1)
            throw new InvalidOperationException("The notification identifies more than one comment; it was not guessed.");
        return unique.SingleOrDefault();
    }

    private StandingJobBrief VerifyItem(JsonNode item, string name, int version, string fingerprint)
    {
        if (!string.Equals(item["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
            throw new InvalidOperationException("The created file name does not match the reserved brief version.");
        if (!Guid.TryParse(item["createdBy"]?["user"]?["id"]?.GetValue<string>(), out var creator)
            || creator != agentUserId)
            throw new InvalidOperationException("The document was not created by this agent user.");
        var itemId = item["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Graph returned no file identity.");
        var url = item["webUrl"]?.GetValue<string>() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Graph returned no openable SharePoint document link.");
        var doc = item["sharepointIds"]?["listItemUniqueId"]?.GetValue<string>();
        if (!Guid.TryParse(doc, out var documentId)
            && (!Query(uri).TryGetValue("sourcedoc", out doc) || !Guid.TryParse(doc, out documentId)))
            throw new InvalidOperationException("Graph returned no verifiable Word document identity.");
        var created = item["createdDateTime"]?.GetValue<DateTimeOffset>()
            ?? throw new InvalidOperationException("Graph returned no file creation timestamp.");
        return new(version, name, itemId, documentId.ToString("D"), url, fingerprint, created);
    }

    private async Task ShareAsync(StandingJob job, string itemId)
    {
        var members = job.Members.DistinctBy(member => member.Id).ToList();
        var recipients = new JsonArray();
        foreach (var member in members) recipients.Add(new JsonObject { ["email"] = member.Email });
        var result = await GraphAsync(HttpMethod.Post, $"me/drive/items/{Uri.EscapeDataString(itemId)}/invite",
            new JsonObject
            {
                ["recipients"] = recipients, ["roles"] = new JsonArray("write"),
                ["requireSignIn"] = true, ["sendInvitation"] = false
            });
        if (result?["value"] is not JsonArray permissions || permissions.Any(p => p?["error"] != null)
            || members.Any(member => !permissions.Any(p => GrantsWrite(p, member))))
            throw new InvalidOperationException("The brief exists but edit access for every approved member was not confirmed.");
    }

    internal static bool GrantsWrite(JsonNode? permission, StandingJobMember member)
    {
        if (permission?["roles"] is not JsonArray roles
            || !roles.Any(role => role?.GetValue<string>() is "write" or "owner")) return false;
        if (string.Equals(permission["invitation"]?["email"]?.GetValue<string>(), member.Email, StringComparison.OrdinalIgnoreCase))
            return true;
        var identities = new List<JsonNode?>();
        foreach (var field in new[] { "grantedToV2", "grantedTo" }) identities.Add(permission[field]);
        foreach (var field in new[] { "grantedToIdentitiesV2", "grantedToIdentities" })
            if (permission[field] is JsonArray values) identities.AddRange(values);
        return identities.Any(identity =>
            string.Equals(identity?["user"]?["id"]?.GetValue<string>(), member.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(identity?["user"]?["email"]?.GetValue<string>(), member.Email, StringComparison.OrdinalIgnoreCase)
            || string.Equals(identity?["siteUser"]?["email"]?.GetValue<string>(), member.Email, StringComparison.OrdinalIgnoreCase));
    }

    internal static string FileName(StandingJob job, int version)
    {
        var title = Regex.Replace(job.Title, """[\x00-\x1f"*:<>\?/\\|]""", " ").Trim(' ', '.');
        if (title.Length > 90) title = title[..90].TrimEnd();
        if (title.Length == 0) throw new ArgumentException("The job needs a usable document title.");
        return $"{title} - {job.Id[..8]} - v{version}.docx";
    }

    private static string NormalizeText(string text) =>
        Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", string.Empty);

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).Where(parts => parts.Length == 2)
        .GroupBy(parts => Uri.UnescapeDataString(parts[0]), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => Uri.UnescapeDataString(group.Last()[1]), StringComparer.OrdinalIgnoreCase);

    private async Task<JsonNode?> GraphAsync(HttpMethod method, string path, JsonNode? body, bool allowNotFound = false)
    {
        if (string.IsNullOrWhiteSpace(graphToken))
            throw new InvalidOperationException("The agent user's Graph credential is unavailable.");
        using var request = new HttpRequestMessage(method, "https://graph.microsoft.com/v1.0/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Standing document operation failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Graph returned no document receipt.");
    }

    internal static JsonNode ToolPayload(JsonNode? result)
    {
        if (result?["structuredContent"] is JsonNode structured) return structured;
        if (result?["content"] is JsonArray content)
            foreach (var block in content)
                if (block?["text"]?.GetValue<string>() is { } text && text.TrimStart().StartsWith('{'))
                    return JsonNode.Parse(text)!;
        return result ?? throw new InvalidOperationException("Word returned no tool result.");
    }

    private static JsonNode? Property(JsonNode node, string name) => node is JsonObject obj
        ? obj.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : null;
}
