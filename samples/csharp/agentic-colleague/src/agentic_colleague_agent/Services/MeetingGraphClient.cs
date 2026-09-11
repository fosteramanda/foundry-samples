using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgenticColleague.Services;

public sealed record MeetingOccurrence(
    string EventId, string Subject, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    string JoinUrl, string SourceUrl, string Organizer);

public sealed record MeetingTranscript(
    string Id, DateTimeOffset CreatedUtc, DateTimeOffset EndUtc, string Content,
    int AvailableSegments, bool Truncated, IReadOnlyList<string> Speakers);

public sealed class MeetingReadException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Reads existing artifacts through the agent user account, never the organizer's identity.</summary>
public sealed class MeetingGraphClient(
    HttpClient httpClient, string token, Guid agentUserId, ILogger logger)
{
    public const int MaxTranscriptCharacters = 60000;
    private readonly string _userPath = $"users/{agentUserId:D}";

    public async Task<string> GetMailboxAsync()
    {
        var profile = await GetObjectAsync($"{_userPath}?$select=id,mail,userPrincipalName", "read agent profile");
        return Text(profile, "mail") is { Length: > 0 } mail ? mail
            : Text(profile, "userPrincipalName") is { Length: > 0 } upn ? upn
            : throw new MeetingReadException("mailbox_unavailable", "The agent user account has no mailbox address.");
    }

    public async Task<bool> IsManagerAsync(Guid actorId)
    {
        var manager = await GetObjectAsync($"{_userPath}/manager?$select=id", "check agent manager");
        return Guid.TryParse(Text(manager, "id"), out var id) && id == actorId;
    }

    public async Task<IReadOnlyList<MeetingOccurrence>> ListMeetingsAsync(
        DateTimeOffset from, DateTimeOffset to)
    {
        var path = $"{_userPath}/calendarView?startDateTime={Uri.EscapeDataString(from.ToUniversalTime().ToString("O"))}"
            + $"&endDateTime={Uri.EscapeDataString(to.ToUniversalTime().ToString("O"))}"
            + $"&$select={EventFields}&$orderby=start/dateTime&$top=100";
        var items = await GetCollectionAsync(path, "read agent calendar");
        return items.Select(ParseMeeting).OfType<MeetingOccurrence>()
            .DistinctBy(m => m.EventId).OrderBy(m => m.StartUtc).ToList();
    }

    public async Task<MeetingOccurrence> GetMeetingAsync(string eventId)
    {
        var value = await GetObjectAsync(
            $"{_userPath}/events/{Uri.EscapeDataString(eventId)}?$select={EventFields}",
            "read calendar occurrence");
        return ParseMeeting(value)
            ?? throw new MeetingReadException("meeting_unavailable",
                "That calendar occurrence is cancelled or has no online meeting join URL.");
    }

    public async Task<MeetingTranscript> ReadTranscriptAsync(MeetingOccurrence occurrence)
    {
        var filter = Uri.EscapeDataString($"JoinWebUrl eq '{occurrence.JoinUrl.Replace("'", "''")}'");
        var meetings = await GetCollectionAsync(
            $"{_userPath}/onlineMeetings?$filter={filter}&$select=id", "resolve online meeting");
        if (meetings.Count != 1 || string.IsNullOrWhiteSpace(Text(meetings[0], "id")))
        {
            throw new MeetingReadException("meeting_unresolved",
                "Graph did not resolve exactly one online meeting for this calendar occurrence's join URL.");
        }

        var meetingId = Uri.EscapeDataString(Text(meetings[0], "id"));
        var transcriptPath = $"{_userPath}/onlineMeetings/{meetingId}/transcripts";
        var transcripts = await GetCollectionAsync(transcriptPath, "list transcripts");
        var segments = new List<(string Id, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var transcript in transcripts)
        {
            var id = Text(transcript, "id");
            if (string.IsNullOrWhiteSpace(id)
                || !DateTimeOffset.TryParse(Text(transcript, "createdDateTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var start)
                || !DateTimeOffset.TryParse(Text(transcript, "endDateTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var end) || end <= start)
            {
                throw new MeetingReadException("invalid_transcript_metadata",
                    "Graph returned incomplete transcript timing. I cannot safely choose a meeting occurrence.");
            }

            // Recurring meetings share the join URL. Never select a different occurrence's transcript.
            if (start < occurrence.EndUtc && end > occurrence.StartUtc)
            {
                segments.Add((id, start, end));
            }
        }

        if (segments.Count == 0)
        {
            throw new MeetingReadException("no_transcript",
                "Graph returned no transcript for this occurrence. It may not have been transcribed, "
                + "may still be processing, or may no longer be available.");
        }

        var neighboringOccurrences = await ListMeetingsAsync(occurrence.StartUtc.AddDays(-1), occurrence.EndUtc.AddDays(1));
        if (segments.Any(segment => neighboringOccurrences.Any(other =>
            other.EventId != occurrence.EventId && other.JoinUrl == occurrence.JoinUrl
            && segment.Start < other.EndUtc && segment.End > other.StartUtc)))
        {
            throw new MeetingReadException("ambiguous_transcript",
                "A transcript overlaps more than one calendar occurrence sharing this join URL. "
                + "I cannot safely attribute it to just the selected occurrence.");
        }

        // Prefer the main segment over short join/admit segments; disclose the selection to the caller.
        var selected = segments.OrderByDescending(s => s.End - s.Start)
            .ThenByDescending(s => s.Start).First();
        var content = await SendAsync(
            $"{transcriptPath}/{Uri.EscapeDataString(selected.Id)}/content?$format=text/vtt",
            "read transcript content", raw: true);
        content = content.TrimStart('\uFEFF');
        if (!Regex.IsMatch(content, @"\AWEBVTT(?:[ \t][^\r\n]*)?(?:\r?\n)")
            || !HasUsableCue(content))
        {
            throw new MeetingReadException("invalid_transcript_content",
                "The transcript endpoint did not return a usable WebVTT transcript. I cannot recap it.");
        }

        var truncated = content.Length > MaxTranscriptCharacters;
        if (truncated)
        {
            content = content[..MaxTranscriptCharacters];
        }

        var speakers = Regex.Matches(content, @"<v(?:\.[^\s>]+)?\s+([^>]+)>", RegexOptions.IgnoreCase)
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value).Trim())
            .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        logger.LogInformation(
            "Meeting transcript retrieved. EventId={EventId} TranscriptId={TranscriptId} Characters={Characters} Truncated={Truncated}",
            occurrence.EventId, selected.Id, content.Length, truncated);
        return new MeetingTranscript(selected.Id, selected.Start, selected.End, content,
            segments.Count, truncated, speakers);
    }

    private const string EventFields = "id,subject,start,end,organizer,isCancelled,onlineMeeting,webLink";

    private static bool HasUsableCue(string content)
    {
        foreach (var block in Regex.Split(content, @"\r?\n[ \t]*\r?\n"))
        {
            var match = Regex.Match(block,
                @"(?m)^(?<start>(?:\d{2,9}:)?[0-5]\d:[0-5]\d\.\d{3}) --> "
                + @"(?<end>(?:\d{2,9}:)?[0-5]\d:[0-5]\d\.\d{3})[^\r\n]*\r?\n(?<text>[\s\S]+)\z");
            if (!match.Success) { continue; }
            var text = WebUtility.HtmlDecode(Regex.Replace(match.Groups["text"].Value, "<[^>]*>", string.Empty));
            if (!string.IsNullOrWhiteSpace(text)
                && TimestampSeconds(match.Groups["end"].Value) > TimestampSeconds(match.Groups["start"].Value))
            {
                return true;
            }
        }
        return false;
    }

    private static double TimestampSeconds(string value)
    {
        var parts = value.Split(':').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return parts[^1] + parts[^2] * 60 + (parts.Length == 3 ? parts[0] * 3600 : 0);
    }

    private static MeetingOccurrence? ParseMeeting(JsonObject value)
    {
        if (value["isCancelled"]?.GetValue<bool>() == true) { return null; }
        var joinUrl = Text(value["onlineMeeting"], "joinUrl");
        if (string.IsNullOrWhiteSpace(joinUrl)) { return null; }
        if (!Uri.TryCreate(joinUrl, UriKind.Absolute, out var joinUri) || joinUri.Scheme != "https")
        {
            throw new MeetingReadException("invalid_calendar_data", "The calendar returned an invalid meeting join URL.");
        }
        var id = Text(value, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new MeetingReadException("invalid_calendar_data", "The calendar returned a meeting with no event ID.");
        }
        var start = CalendarTime(value["start"]);
        var end = CalendarTime(value["end"]);
        if (end <= start)
        {
            throw new MeetingReadException("invalid_calendar_data", "The calendar returned an invalid meeting time range.");
        }
        var subject = Text(value, "subject");
        return new MeetingOccurrence(id, string.IsNullOrWhiteSpace(subject) ? "(untitled meeting)" : subject,
            start, end, joinUrl, Text(value, "webLink"), Text(value["organizer"]?["emailAddress"], "address"));
    }

    private static DateTimeOffset CalendarTime(JsonNode? node)
    {
        var time = Text(node, "dateTime");
        if (Regex.IsMatch(time, @"(?:Z|[+-]\d\d:\d\d)$", RegexOptions.IgnoreCase)
            && DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
        {
            return offset.ToUniversalTime();
        }
        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new MeetingReadException("invalid_calendar_data", "The calendar returned an unreadable meeting time.");
        }
        var zoneId = Text(node, "timeZone");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
        {
            throw new MeetingReadException("invalid_calendar_data", "The calendar returned an unknown meeting time zone.");
        }
        var local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            throw new MeetingReadException("invalid_calendar_data", "The calendar returned an ambiguous local meeting time.");
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    private async Task<List<JsonObject>> GetCollectionAsync(string path, string stage)
    {
        var values = new List<JsonObject>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(path))
        {
            if (!visited.Add(path) || visited.Count > 50)
            {
                throw new MeetingReadException("incomplete_results",
                    "Graph pagination could not be completed. I will not treat a partial list as complete.");
            }
            var page = await GetObjectAsync(path, stage);
            if (page["value"] is not JsonArray items || items.Any(i => i is not JsonObject))
            {
                throw new MeetingReadException("invalid_graph_response", "Graph returned an invalid collection response.");
            }
            values.AddRange(items.Cast<JsonObject>());
            path = Text(page, "@odata.nextLink");
        }
        return values;
    }

    private async Task<JsonObject> GetObjectAsync(string path, string stage)
    {
        var text = await SendAsync(path, stage);
        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new MeetingReadException("invalid_graph_response", "Graph returned an invalid object response.");
        }
        catch (JsonException)
        {
            throw new MeetingReadException("invalid_graph_response", "Graph returned invalid JSON.");
        }
    }

    private async Task<string> SendAsync(string path, string stage, bool raw = false)
    {
        var uri = Uri.TryCreate(path, UriKind.Absolute, out var absolute)
            ? absolute : new Uri($"https://graph.microsoft.com/v1.0/{path}");
        var ownPath = $"/v1.0/{_userPath}";
        if (uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.AbsolutePath.Equals(ownPath, StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith(ownPath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new MeetingReadException("invalid_graph_link", "Graph returned a link outside the agent user's scope.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\", IdType=\"ImmutableId\"");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(raw ? "text/vtt" : "application/json"));
        using var response = await httpClient.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        logger.LogInformation("Meeting Graph call. Stage={Stage} Status={Status} RequestId={RequestId}",
            stage, (int)response.StatusCode,
            response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null);
        if (response.IsSuccessStatusCode) { return text; }

        var graphCode = "unknown";
        try
        {
            if (JsonNode.Parse(text) is JsonObject errorResponse
                && errorResponse["error"] is JsonObject error
                && error["code"] is JsonValue code && code.TryGetValue<string>(out var codeText))
            {
                graphCode = codeText;
            }
        }
        catch (JsonException) { /* The HTTP status still describes a non-JSON gateway failure. */ }
        var reason = graphCode.Equals("GraphAccessToTranscriptsDisabled", StringComparison.OrdinalIgnoreCase)
            ? "The tenant's Teams transcript API access is disabled."
            : response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The agent user's Graph authentication failed.",
                HttpStatusCode.Forbidden => "Graph denied this agent user access. This is not evidence that no transcript exists.",
                HttpStatusCode.NotFound => "The requested meeting artifact is unavailable to this agent user.",
                HttpStatusCode.TooManyRequests => response.Headers.RetryAfter is { } retryAfter
                    ? $"Graph throttled this request; Retry-After: {retryAfter}."
                    : "Graph throttled this request; retry later.",
                _ => "Graph could not complete the request."
            };
        throw new MeetingReadException("graph_error", $"{stage}: {reason} HTTP {(int)response.StatusCode}; code={graphCode}.");
    }

    private static string Text(JsonNode? value, string key)
    {
        if (value == null) { return string.Empty; }
        if (value is JsonObject obj)
        {
            if (obj[key] == null) { return string.Empty; }
            if (obj[key] is JsonValue item && item.TryGetValue<string>(out var text)) { return text.Trim(); }
        }
        throw new MeetingReadException("invalid_graph_response", $"Graph returned an invalid {key} field.");
    }
}
