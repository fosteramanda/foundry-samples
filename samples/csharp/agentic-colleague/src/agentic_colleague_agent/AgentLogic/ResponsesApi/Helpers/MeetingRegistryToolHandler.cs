namespace AgenticColleague.AgentLogic.ResponsesApi.Helpers;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgenticColleague.Models;
using AgenticColleague.Services;
using Azure;

public sealed class MeetingRegistryToolHandler
{
    private readonly ILogger _logger;
    private readonly MeetingGraphClient _graph;
    private readonly IMeetingRegistryStore _store;
    private readonly TimeZoneInfo _displayZone;
    private readonly TimeProvider _clock;
    private readonly bool _hasIdentity;
    private Guid _actorId;

    public MeetingRegistryToolHandler(
        AgentMetadata agentMetadata, ILogger logger, HttpClient httpClient, IConfiguration configuration,
        string? graphAccessToken, IMeetingRegistryStore store, TimeProvider? clock = null)
    {
        _logger = logger;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _hasIdentity = agentMetadata.UserId != Guid.Empty && !string.IsNullOrWhiteSpace(graphAccessToken);
        _graph = new MeetingGraphClient(httpClient, graphAccessToken ?? string.Empty, agentMetadata.UserId, logger);
        var zone = configuration["MeetingDisplayTimeZone"];
        _displayZone = TimeZoneInfo.FindSystemTimeZoneById(
            string.IsNullOrWhiteSpace(zone) ? "Pacific Standard Time" : zone);
    }

    public bool IsEnabled => _hasIdentity && _store.IsAvailable;

    public void SetCurrentActor(string? aadObjectId) =>
        _actorId = Guid.TryParse(aadObjectId, out var id) ? id : Guid.Empty;

    public List<JsonNode> GetToolDefinitions() => !IsEnabled ? [] :
    [
        JsonNode.Parse("""
        {
          "type": "function",
          "name": "list_tracked_meetings",
          "description": "Read your own calendar, including untracked and untitled meetings. Returns an event_id for each occurrence. Call before choosing a meeting when the request is vague; never guess an event_id. Dates use MeetingDisplayTimeZone. An empty list is sayable only after this call succeeds.",
          "parameters": {
            "type": "object",
            "properties": {
              "on_date": { "type": "string", "description": "Optional local calendar date, YYYY-MM-DD." },
              "on_or_after": { "type": "string", "description": "Optional first local date, YYYY-MM-DD; defaults to 30 days ago." },
              "on_or_before": { "type": "string", "description": "Optional last local date inclusive, YYYY-MM-DD; defaults to 30 days ahead." }
            },
            "additionalProperties": false
          }
        }
        """)!,
        JsonNode.Parse("""
        {
          "type": "function",
          "name": "read_meeting_transcript",
          "description": "Retrieve an existing transcript through your own agent user account. Use event_id from the calendar for an exact occurrence, or subject with on_date to disambiguate repeated meetings. No tracking or capture-confirmation step is required. Explicit exclusions still apply. Does not start recording, notify attendees, retain transcript text, or update a board.",
          "parameters": {
            "type": "object",
            "properties": {
              "event_id": { "type": "string", "description": "Exact occurrence ID returned by list_tracked_meetings." },
              "subject": { "type": "string", "description": "All or part of the calendar subject, if event_id is not known." },
              "on_date": { "type": "string", "description": "Optional local date, YYYY-MM-DD, to distinguish recurring occurrences." }
            },
            "additionalProperties": false
          }
        }
        """)!,
        JsonNode.Parse("""
        {
          "type": "function",
          "name": "track_meeting",
          "description": "Bookmark the metadata of an invited calendar occurrence when explicitly asked. This is optional bookkeeping, not a prerequisite for reading a transcript and not consent to record or retain anything.",
          "parameters": {
            "type": "object",
            "properties": {
              "event_id": { "type": "string", "description": "Exact occurrence ID returned by list_tracked_meetings." },
              "subject": { "type": "string", "description": "Calendar subject if event_id is not known." },
              "on_date": { "type": "string", "description": "Optional local date, YYYY-MM-DD." },
              "on_or_after": { "type": "string", "description": "Optional first local date, YYYY-MM-DD." },
              "on_or_before": { "type": "string", "description": "Optional last local date inclusive, YYYY-MM-DD." }
            },
            "additionalProperties": false
          }
        }
        """)!,
        JsonNode.Parse("""
        {
          "type": "function",
          "name": "set_meeting_capture",
          "description": "Manager-only: exclude this specific calendar occurrence from future retrieval (approved=false), or remove its local exclusion (approved=true). Use only on an explicit request, never to manufacture approval for a recap. Does not alter Graph permissions, Teams recording, participant notice, or retention.",
          "parameters": {
            "type": "object",
            "properties": {
              "event_id": { "type": "string", "description": "Exact occurrence ID returned by list_tracked_meetings." },
              "subject": { "type": "string", "description": "Calendar subject if event_id is not known." },
              "on_date": { "type": "string", "description": "Optional local date, YYYY-MM-DD." },
              "approved": { "type": "boolean", "description": "false excludes this occurrence; true removes its local exclusion." }
            },
            "required": ["approved"],
            "additionalProperties": false
          }
        }
        """)!
    ];

    public async Task<string?> TryExecuteAsync(string toolName, string arguments)
    {
        if (toolName is not ("track_meeting" or "list_tracked_meetings"
            or "set_meeting_capture" or "read_meeting_transcript")) { return null; }
        if (!IsEnabled)
        {
            return Failure("meeting_tools_unavailable",
                "Meeting retrieval needs this instance's agent user account, a Graph token and the exclusion store.");
        }
        try
        {
            var args = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments) as JsonObject
                ?? throw new ArgumentException("Meeting tool arguments must be an object.");
            var mailbox = await _graph.GetMailboxAsync();
            if (toolName == "list_tracked_meetings")
            {
                var (from, to) = Window(args);
                var meetings = await _graph.ListMeetingsAsync(from, to);
                var preferences = await _store.ListAsync(mailbox);
                return JsonSerializer.Serialize(new
                {
                    status = "ok", time_zone = _displayZone.Id,
                    meetings = meetings.Select(m => Describe(m, FindPreference(preferences, m)))
                });
            }
            if (toolName == "set_meeting_capture"
                && (_actorId == Guid.Empty || !await _graph.IsManagerAsync(_actorId)))
            {
                return Failure("manager_required", "Only this instance's manager can change meeting exclusions.");
            }
            var eventId = Text(args, "event_id");
            MeetingOccurrence occurrence;
            if (eventId.Length > 0)
            {
                occurrence = await _graph.GetMeetingAsync(eventId);
            }
            else
            {
                var (from, to) = Window(args);
                var meetings = await _graph.ListMeetingsAsync(from, to);
                var subject = Text(args, "subject");
                var matches = meetings.Where(m => subject.Length == 0
                    || m.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                {
                    return JsonSerializer.Serialize(new
                    {
                        status = matches.Count == 0 ? "no_matching_meeting" : "ambiguous_meeting",
                        message = "Choose an occurrence from this calendar result using its event_id; do not substitute another meeting.",
                        candidates = (matches.Count == 0 ? meetings : matches).Select(m => Describe(m, null))
                    });
                }
                occurrence = matches[0];
            }

            if (Text(args, "on_date").Length > 0)
            {
                var (from, to) = Window(args);
                if (occurrence.StartUtc >= to || occurrence.EndUtc <= from)
                {
                    return Failure("date_mismatch", "That event_id is not on the requested date. Choose the correct calendar occurrence.");
                }
            }

            var preference = await PreferenceAsync(mailbox, occurrence);
            if (toolName == "read_meeting_transcript")
            {
                if (IsExcluded(preference))
                {
                    return Failure("meeting_excluded",
                        "This occurrence has a saved exclusion or legacy restriction. Its manager can remove that restriction; no transcript was fetched.");
                }
                if (occurrence.EndUtc > _clock.GetUtcNow())
                {
                    return Failure("meeting_not_ended", "This calendar occurrence has not ended; a post-meeting transcript is not ready.");
                }
                var transcript = await _graph.ReadTranscriptAsync(occurrence);
                return JsonSerializer.Serialize(new
                {
                    status = "ok", meeting = Describe(occurrence, preference),
                    transcript_id = transcript.Id,
                    segment_start = transcript.CreatedUtc, segment_end = transcript.EndUtc,
                    available_segments = transcript.AvailableSegments,
                    coverage = transcript.AvailableSegments > 1 ? "longest segment of this occurrence" : "one available segment",
                    truncated = transcript.Truncated, speakers = transcript.Speakers,
                    attribution_available = transcript.Speakers.Count > 0,
                    source_url = occurrence.SourceUrl.Length > 0 ? occurrence.SourceUrl : occurrence.JoinUrl,
                    content = transcript.Content
                });
            }

            var entity = NewPreference(mailbox, occurrence, preference);
            if (toolName == "set_meeting_capture")
            {
                if (args["approved"] is not JsonValue value || !value.TryGetValue<bool>(out var approved))
                {
                    throw new ArgumentException("approved must be true or false.");
                }
                entity.Excluded = !approved;
                entity.UpdatedBy = _actorId.ToString("D");
            }
            if (!await _store.UpsertAsync(entity))
            {
                return Failure("registry_write_failed", "The meeting preference was not saved; no setting was changed.");
            }
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                action = toolName == "track_meeting" ? "tracked" : entity.Excluded ? "excluded" : "included",
                meeting = Describe(occurrence, entity)
            });
        }
        catch (MeetingReadException ex)
        {
            _logger.LogWarning("Meeting tool failed. Tool={Tool} Code={Code} Reason={Reason}", toolName, ex.Code, ex.Message);
            return Failure(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            _logger.LogWarning(ex, "Invalid meeting tool arguments. Tool={Tool}", toolName);
            return Failure("invalid_arguments", "Use a returned event_id or a calendar subject, and dates in YYYY-MM-DD format.");
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Meeting registry operation failed. Tool={Tool}", toolName);
            return Failure("registry_unavailable", "Meeting preferences could not be checked or saved. No recap was generated.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Meeting Graph transport failed. Tool={Tool}", toolName);
            return Failure("graph_unavailable", "Graph could not be reached or timed out. This does not mean the meeting has no transcript.");
        }
    }

    private async Task<TrackedMeetingEntity?> PreferenceAsync(string mailbox, MeetingOccurrence meeting)
    {
        var current = await _store.GetAsync(mailbox, OccurrenceKey(meeting.EventId));
        if (current != null) { return current; }
        var thread = LegacyThreadId(meeting.JoinUrl);
        return thread.Length == 0 ? null : await _store.GetAsync(mailbox, thread);
    }

    private static TrackedMeetingEntity? FindPreference(
        IEnumerable<TrackedMeetingEntity> preferences, MeetingOccurrence meeting)
    {
        var key = OccurrenceKey(meeting.EventId);
        var thread = LegacyThreadId(meeting.JoinUrl);
        return preferences.FirstOrDefault(p => p.RowKey == key)
            ?? (thread.Length == 0 ? null : preferences.FirstOrDefault(p => p.RowKey == MeetingRegistryStore.ToRowKey(thread)));
    }

    private static bool IsExcluded(TrackedMeetingEntity? preference) =>
        preference != null && (preference.Excluded || preference.IngestionState == "excluded"
            || (preference.PolicyVersion < 2 && (!preference.CaptureApproved || !preference.NoticeSentUtc.HasValue)));

    private static TrackedMeetingEntity NewPreference(
        string mailbox, MeetingOccurrence meeting, TrackedMeetingEntity? previous) => new()
    {
        PartitionKey = MeetingRegistryStore.ToPartitionKey(mailbox), RowKey = OccurrenceKey(meeting.EventId),
        ETag = previous?.RowKey == OccurrenceKey(meeting.EventId) ? previous.ETag : default,
        EventId = meeting.EventId, Subject = meeting.Subject, OrganizerAddress = meeting.Organizer,
        StartUtc = meeting.StartUtc, EndUtc = meeting.EndUtc, JoinWebUrl = meeting.JoinUrl,
        ThreadId = LegacyThreadId(meeting.JoinUrl), PolicyVersion = 2,
        Excluded = IsExcluded(previous), TrackedBy = mailbox,
        UpdatedBy = previous?.UpdatedBy ?? string.Empty, CreatedUtc = previous?.CreatedUtc ?? default
    };

    private object Describe(MeetingOccurrence meeting, TrackedMeetingEntity? preference) => new
    {
        event_id = meeting.EventId, subject = meeting.Subject,
        start = TimeZoneInfo.ConvertTime(meeting.StartUtc, _displayZone),
        end = TimeZoneInfo.ConvertTime(meeting.EndUtc, _displayZone),
        time_zone = _displayZone.Id, excluded = IsExcluded(preference),
        source_url = meeting.SourceUrl.Length > 0 ? meeting.SourceUrl : meeting.JoinUrl
    };

    private (DateTimeOffset From, DateTimeOffset To) Window(JsonObject args)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _displayZone).DateTime);
        var date = Text(args, "on_date");
        var from = date.Length > 0 ? ParseDate(date) : ParseDate(Text(args, "on_or_after"), today.AddDays(-30));
        var through = date.Length > 0 ? from : ParseDate(Text(args, "on_or_before"), today.AddDays(30));
        if (through < from || through.DayNumber - from.DayNumber > 366)
        {
            throw new ArgumentException("Meeting date range is invalid or longer than one year.");
        }
        return (MidnightUtc(from), MidnightUtc(through.AddDays(1)));
    }

    private DateTimeOffset MidnightUtc(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _displayZone));

    private static DateOnly ParseDate(string text, DateOnly? fallback = null) =>
        text.Length == 0 && fallback.HasValue ? fallback.Value
        : DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string OccurrenceKey(string eventId) =>
        "event-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId)));

    private static string LegacyThreadId(string url)
    {
        var match = Regex.Match(url, @"meetup-join/([^/?]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : string.Empty;
    }

    private static string Text(JsonObject args, string key)
    {
        if (args[key] == null) { return string.Empty; }
        if (args[key] is JsonValue value && value.TryGetValue<string>(out var text)) { return text.Trim(); }
        throw new ArgumentException($"{key} must be a string.");
    }

    private static string Failure(string code, string message) => JsonSerializer.Serialize(new
    {
        status = code, message, instruction = "Report this limitation plainly. Do not invent a recap or claim success."
    });
}
