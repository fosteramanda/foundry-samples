namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkstreamManager.Models;
using WorkstreamManager.Services;

/// <summary>
/// Records which meetings the autopilot has been invited to, and separately, whether it has
/// been permitted to use what was said in them.
///
/// The autopilot is invited like a person. Its agent user account is a real directory user
/// with its own mailbox and calendar, so an organizer adds officeofamanda@... to the invite
/// exactly as they would a colleague. Everything below therefore reads the AGENT'S OWN
/// calendar, never the manager's.
///
/// That distinction is the whole design. An earlier version of this handler resolved the
/// manager's mailbox and looked for meetings there, which is the model the mailbox tools use
/// because those act on the manager's behalf. It is the wrong model here: it made the agent a
/// third party reading someone else's meeting, which needs tenant-wide application permissions
/// that Entra refuses to grant to agent identities at all
/// ("The specified app role cannot be granted to agent identities"). Being an attendee in its
/// own right sidesteps that entirely, and it is also what the organizer expects when they add
/// the autopilot to the invite.
///
/// Agent identity and agent user account are different objects and are not interchangeable.
/// The identity is the service principal that authenticates; the user account is the member of
/// the organization that gets invited, has a mailbox, and shows up in the attendee list.
/// AgentMetadata.UserId below is the USER account.
///
/// Two flags gate ingestion and both are required:
///
///   CaptureApproved   the organizer said yes
///   NoticeSentUtc     the people in the room were told
///
/// They are deliberately not one field. The organizer approving is not the same event as the
/// room being informed, and an organizer cannot consent on behalf of the other attendees. A
/// design that collapses them reads as consent while only ever having recorded permission.
/// </summary>
public class MeetingRegistryToolHandler
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string? _graphAccessToken;
    private readonly Guid _agentUserId;
    private readonly MeetingRegistryStore _store;

    private string? _agentMailbox;
    private bool _mailboxResolved;

    public MeetingRegistryToolHandler(
        AgentMetadata agentMetadata,
        ILogger logger,
        HttpClient httpClient,
        IConfiguration configuration,
        string? graphAccessToken,
        MeetingRegistryStore store)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _graphAccessToken = graphAccessToken;
        _agentUserId = agentMetadata?.UserId ?? Guid.Empty;
    }

    /// <summary>
    /// Requires durable storage as well as a Graph token. Without the table a capture decision
    /// cannot be persisted, and a decision that is not recorded is the same as one nobody made.
    /// </summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_graphAccessToken)
        && _agentUserId != Guid.Empty
        && _store.IsAvailable;

    public List<JsonNode> GetToolDefinitions()
    {
        if (!IsEnabled)
        {
            return [];
        }

        return
        [
            JsonNode.Parse("""
            {
                "type": "function",
                "name": "track_meeting",
                "description": "Registers a meeting YOU WERE INVITED TO so it can later be recapped. Looks at your own calendar, not anyone else's: someone must have added you to the invite first, the same way they would add a colleague. This ONLY registers the meeting. It does NOT give permission to read what was said: tracking always starts with capture switched off, and the organizer must approve it separately. Use this when the user asks you to follow, track, watch, or take notes on a meeting.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "subject": { "type": "string", "description": "All or part of the meeting subject as it appears on your calendar." },
                        "on_or_after": { "type": "string", "description": "Optional ISO 8601 date to start searching from, e.g. 2026-09-08. Defaults to 30 days ago." },
                        "on_or_before": { "type": "string", "description": "Optional ISO 8601 date to search until. Defaults to 30 days ahead." }
                    },
                    "required": ["subject"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "list_tracked_meetings",
                "description": "Lists meetings currently being followed, showing for each whether capture was approved, whether participants were notified, and whether it is therefore eligible to be read. Use when the user asks what meetings you are tracking or what you are allowed to use.",
                "parameters": { "type": "object", "properties": {}, "additionalProperties": false }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "set_meeting_capture",
                "description": "Turns permission to use a meeting's content on or off. Only the organizer should be doing this. If the meeting is not registered yet it is picked up from your calendar automatically. If the user says in the same breath that attendees were told (for example 'yes, I told everyone' or 'they know it's being captured'), set attendees_notified true so they are not asked twice. Read the meeting subject back so a wrong one is caught immediately.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "subject": { "type": "string", "description": "Subject of the meeting, as it appears on the calendar invite." },
                        "approved": { "type": "boolean", "description": "true to permit using this meeting's content, false to withdraw permission." },
                        "attendees_notified": { "type": "boolean", "description": "Set true ONLY when the user has actually confirmed the other attendees were told the meeting may be captured. Never assume it from approval alone." },
                        "approve_retention": { "type": "boolean", "description": "Optional. true if the user also agreed the meeting may be kept beyond the immediate recap. Only set this when they said so explicitly; do not infer it from approving capture." }
                    },
                    "required": ["subject", "approved"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "record_capture_notice",
                "description": "Records that participants have been told this meeting may be captured. Call this ONLY after the notice has actually been delivered to the meeting's attendees, never in advance and never because the organizer said it was fine. Until this is recorded the meeting cannot be read even if capture was approved.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "subject": { "type": "string", "description": "Subject of a meeting already returned by list_tracked_meetings." },
                        "how_delivered": { "type": "string", "description": "Briefly how participants were told, e.g. 'posted in the meeting chat' or 'stated at the top of the meeting'." }
                    },
                    "required": ["subject", "how_delivered"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "read_meeting_transcript",
                "description": "Fetches the transcript of a tracked meeting so it can be recapped. Refuses unless capture was approved AND participants were notified. Use this when asked what was decided, what was discussed, what actions came out of a meeting, or for a recap. Returns the raw transcript text; you then extract decisions, actions with owners, blockers and unresolved items yourself.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "subject": { "type": "string", "description": "Subject of a meeting already returned by list_tracked_meetings." }
                    },
                    "required": ["subject"],
                    "additionalProperties": false
                }
            }
            """)!,
        ];
    }

    public async Task<string?> TryExecuteAsync(string toolName, string arguments)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (toolName is not ("track_meeting" or "list_tracked_meetings" or "set_meeting_capture" or "record_capture_notice" or "read_meeting_transcript"))
        {
            return null;
        }

        JsonNode? args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meeting registry tool {Tool} called with unparseable arguments.", toolName);
            return $"Could not parse arguments for {toolName}.";
        }

        var mailbox = await ResolveAgentMailboxAsync();
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            return "I could not resolve my own mailbox, so I cannot see which meetings I was invited to. "
                 + "Tell the user plainly rather than guessing.";
        }

        return toolName switch
        {
            "track_meeting" => await TrackMeetingAsync(mailbox, args),
            "list_tracked_meetings" => await ListTrackedAsync(mailbox),
            "set_meeting_capture" => await SetCaptureAsync(mailbox, args),
            "record_capture_notice" => await RecordNoticeAsync(mailbox, args),
            "read_meeting_transcript" => await ReadTranscriptAsync(mailbox, args),
            _ => null,
        };
    }

    private async Task<string> TrackMeetingAsync(string mailbox, JsonNode? args)
    {
        var subject = GetString(args, "subject");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "A meeting subject is required to track a meeting.";
        }

        var from = ParseDateOrDefault(GetString(args, "on_or_after"), DateTimeOffset.UtcNow.AddDays(-30));
        var to = ParseDateOrDefault(GetString(args, "on_or_before"), DateTimeOffset.UtcNow.AddDays(30));

        var (entity, error, wasAlreadyTracked) = await TrackFromCalendarAsync(mailbox, subject, from, to);
        if (entity == null)
        {
            return error!;
        }

        var already = wasAlreadyTracked ? " It was already being tracked." : string.Empty;
        return $"Now tracking '{entity.Subject}' ({entity.StartUtc:yyyy-MM-dd HH:mm} {DisplayTimeZone}).{already} "
             + $"Capture is {(entity.CaptureApproved ? "APPROVED" : "NOT approved")} and participants have "
             + $"{(entity.NoticeSentUtc.HasValue ? "been notified" : "NOT been notified")}. "
             + "Tell the user tracking alone does not let me read what was said, and ask whether they want to approve capture.";
    }

    /// <summary>
    /// Registers a meeting from the agent's own calendar, or returns the existing record.
    ///
    /// Separated out so the other tools can call it. Requiring the user to run track_meeting as a
    /// distinct step before anything else was pure bookkeeping: the meeting is already on the
    /// agent's calendar because someone invited it, so asking a human to announce that fact adds
    /// nothing. Registering is not a permission, so doing it automatically grants nothing.
    /// </summary>
    private async Task<(TrackedMeetingEntity? Entity, string? Error, bool WasAlreadyTracked)> TrackFromCalendarAsync(
        string mailbox,
        string subject,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null)
    {
        var from = fromUtc ?? DateTimeOffset.UtcNow.AddDays(-30);
        var to = toUtc ?? DateTimeOffset.UtcNow.AddDays(30);

        var path = $"users/{Uri.EscapeDataString(mailbox)}/calendarView"
                 + $"?startDateTime={from:yyyy-MM-ddTHH:mm:ssZ}&endDateTime={to:yyyy-MM-ddTHH:mm:ssZ}"
                 + "&$select=subject,start,end,organizer,isOnlineMeeting,onlineMeeting&$orderby=start/dateTime&$top=100";

        var (ok, response, error) = await SendGraphAsync(HttpMethod.Get, path, preferTimeZone: DisplayTimeZone);
        if (!ok)
        {
            return (null, DescribeFailure("read my own calendar", mailbox, error), false);
        }

        var items = JsonNode.Parse(response ?? "{}")?["value"] as JsonArray;
        var matches = (items ?? [])
            .Where(e => e?["isOnlineMeeting"]?.GetValue<bool>() == true)
            .Where(e => (e?["subject"]?.GetValue<string>() ?? string.Empty)
                .Contains(subject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            var visible = (items ?? [])
                .Where(e => e?["isOnlineMeeting"]?.GetValue<bool>() == true)
                .Select(e => $"'{e?["subject"]?.GetValue<string>()}'")
                .Distinct()
                .Take(5)
                .ToList();

            // Naming what IS on the calendar turns "I cannot find it" into something the user can
            // act on: usually they used a different word than the actual subject.
            var alternatives = visible.Count > 0
                ? $" Meetings I was invited to in that window: {string.Join(", ", visible)}."
                : " I have no Teams meetings on my calendar in that window at all, so nobody has invited me to one.";

            return (null,
                $"No Teams meeting matching '{subject}' is on my calendar between {from:yyyy-MM-dd} and "
                + $"{to:yyyy-MM-dd}.{alternatives} I can only recap meetings I was invited to. If it is missing, "
                + "the fix is to add me to the invite the way you would add a colleague. Do not invent a meeting "
                + "and do not look at anyone else's calendar.", false);
        }

        if (matches.Count > 1)
        {
            var list = string.Join("; ", matches.Take(5).Select(m =>
                $"'{m?["subject"]?.GetValue<string>()}' on {m?["start"]?["dateTime"]?.GetValue<string>()}"));
            return (null, $"'{subject}' matched {matches.Count} meetings: {list}. Ask the user which one before going further.", false);
        }

        var ev = matches[0];
        var joinUrl = ev?["onlineMeeting"]?["joinUrl"]?.GetValue<string>() ?? string.Empty;
        var threadId = ExtractThreadId(joinUrl);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return (null, "That meeting has no Teams thread id on the calendar entry, so there is nothing stable "
                        + "to track it by. Tell the user it cannot be tracked rather than storing a partial record.", false);
        }

        // Partitioned by the agent's own mailbox, because that is whose calendar these meetings are
        // on and what list_tracked_meetings reads back. The organizer is kept as data: it is who must
        // approve capture, but it is not the key.
        var organizer = ev?["organizer"]?["emailAddress"]?["address"]?.GetValue<string>() ?? string.Empty;
        var existing = await _store.GetAsync(mailbox, threadId);

        var entity = existing ?? new TrackedMeetingEntity
        {
            PartitionKey = MeetingRegistryStore.ToPartitionKey(mailbox),
            RowKey = MeetingRegistryStore.ToRowKey(threadId),
        };

        entity.Subject = ev?["subject"]?.GetValue<string>() ?? subject;
        entity.OrganizerAddress = organizer;
        entity.ThreadId = threadId;
        entity.JoinWebUrl = joinUrl;
        entity.StartUtc = ParseDateOrNull(ev?["start"]?["dateTime"]?.GetValue<string>());
        entity.EndUtc = ParseDateOrNull(ev?["end"]?["dateTime"]?.GetValue<string>());
        entity.TrackedBy = mailbox;

        // Never reset an existing approval by re-tracking, and never grant one here.
        if (existing == null)
        {
            entity.CaptureApproved = false;
            entity.IngestionState = "none";
        }

        if (!await _store.UpsertAsync(entity))
        {
            return (null, "I could not save that meeting to the registry, so it is NOT being tracked. Say so plainly.", false);
        }

        return (entity, null, existing != null);
    }

    private async Task<string> ListTrackedAsync(string mailbox)
    {
        var all = await _store.ListAsync(mailbox);

        // Also show meetings sitting on the agent's own calendar that are not registered yet.
        // Without this the agent answers "I am not tracking anything" and then asks the user
        // which meeting they mean, which it cannot do anything with: the user names a meeting,
        // the agent still has no list. It has a calendar. It should read it.
        var candidates = new List<string>();
        var from = DateTimeOffset.UtcNow.AddDays(-30);
        var to = DateTimeOffset.UtcNow.AddDays(30);
        var path = $"users/{Uri.EscapeDataString(mailbox)}/calendarView"
                 + $"?startDateTime={from:yyyy-MM-ddTHH:mm:ssZ}&endDateTime={to:yyyy-MM-ddTHH:mm:ssZ}"
                 + "&$select=subject,start,isOnlineMeeting&$orderby=start/dateTime&$top=100";

        var (ok, response, _) = await SendGraphAsync(HttpMethod.Get, path, preferTimeZone: DisplayTimeZone);
        if (ok)
        {
            var items = JsonNode.Parse(response ?? "{}")?["value"] as JsonArray;
            foreach (var e in items ?? [])
            {
                if (e?["isOnlineMeeting"]?.GetValue<bool>() != true) { continue; }
                var subj = e?["subject"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(subj)) { continue; }
                if (all.Any(t => string.Equals(t.Subject, subj, StringComparison.OrdinalIgnoreCase))) { continue; }
                candidates.Add($"- '{subj}' {e?["start"]?["dateTime"]?.GetValue<string>()} (on my calendar, not registered yet)");
            }
        }

        if (all.Count == 0 && candidates.Count == 0)
        {
            return "I am not tracking any meetings, and there are none on my calendar either. Nobody has "
                 + "invited me to a Teams meeting. Tell the user to add me to the invite the way they would "
                 + "add a colleague.";
        }

        var lines = all.Select(m =>
            $"- '{m.Subject}' {m.StartUtc:yyyy-MM-dd HH:mm} {DisplayTimeZone} | capture={(m.CaptureApproved ? "approved" : "not approved")}"
            + $" | notice={(m.NoticeSentUtc.HasValue ? m.NoticeSentUtc.Value.ToString("yyyy-MM-dd") : "not sent")}"
            + $" | readable={(m.IsIngestionEligible ? "yes" : "NO")}"
            + $" | retention={(m.RetentionApproved ? "approved" : "not approved")}"
            + $" | state={m.IngestionState}").ToList();

        var sb = new System.Text.StringBuilder();
        if (lines.Count > 0)
        {
            sb.AppendLine("Registered meetings:");
            sb.AppendLine(string.Join("\n", lines));
            sb.AppendLine("'readable=NO' means I must not use that meeting's content, whatever else is set.");
        }
        if (candidates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("On my calendar but not registered yet:");
            sb.AppendLine(string.Join("\n", candidates));
            sb.AppendLine("These can be recapped straight away: just call read_meeting_transcript with the "
                        + "subject. Registering happens automatically. If exactly one of these is an obvious "
                        + "match for what the user asked, use it rather than asking them to choose.");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> SetCaptureAsync(string mailbox, JsonNode? args)
    {
        var subject = GetString(args, "subject");
        var approved = args?["approved"]?.GetValue<bool>() ?? false;
        var approveRetention = args?["approve_retention"]?.GetValue<bool>();
        var attendeesNotified = args?["attendees_notified"]?.GetValue<bool>();

        var match = await FindTrackedAsync(mailbox, subject);
        if (match.Error != null)
        {
            return match.Error;
        }

        var entity = match.Entity!;
        entity.CaptureApproved = approved;

        if (approved)
        {
            entity.ApprovedBy = mailbox;
            entity.ApprovedUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            // Withdrawing permission must also stop anything queued, otherwise revoking is
            // advisory only and the meeting still gets read on the next pass.
            entity.ApprovedBy = string.Empty;
            entity.ApprovedUtc = null;
            entity.RetentionApproved = false;
            if (entity.IngestionState == "pending")
            {
                entity.IngestionState = "excluded";
            }
        }

        if (approveRetention.HasValue && approved)
        {
            entity.RetentionApproved = approveRetention.Value;
        }

        // Recorded here only when the user actually said the attendees were told. It is still a
        // separate field and a separate assertion; this just avoids making them say it twice in
        // the same conversation.
        if (approved && attendeesNotified == true && !entity.NoticeSentUtc.HasValue)
        {
            entity.NoticeSentUtc = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "Capture notice recorded alongside approval for '{Subject}' ({ThreadId})",
                entity.Subject, entity.ThreadId);
        }

        if (!await _store.UpsertAsync(entity))
        {
            return "I could not save that change, so the previous setting still stands. Say so plainly.";
        }

        if (!approved)
        {
            return $"Capture is now OFF for '{entity.Subject}'. I will not use its content, and anything queued for it is excluded.";
        }

        return entity.NoticeSentUtc.HasValue
            ? $"Capture is now ON for '{entity.Subject}' and participants were notified on {entity.NoticeSentUtc:yyyy-MM-dd}. It is eligible to be read, so go ahead and answer what the user originally asked."
            : $"Capture is now ON for '{entity.Subject}', but participants have NOT been notified yet, so I still must not read it. "
              + "Ask the user one short question: have the attendees been told the meeting may be captured? If they confirm, "
              + "call set_meeting_capture again with attendees_notified true.";
    }

    private async Task<string> RecordNoticeAsync(string mailbox, JsonNode? args)
    {
        var subject = GetString(args, "subject");
        var how = GetString(args, "how_delivered");

        if (string.IsNullOrWhiteSpace(how))
        {
            return "Record how participants were told before recording the notice. Do not record a notice that was not sent.";
        }

        var match = await FindTrackedAsync(mailbox, subject);
        if (match.Error != null)
        {
            return match.Error;
        }

        var entity = match.Entity!;
        entity.NoticeSentUtc = DateTimeOffset.UtcNow;

        if (!await _store.UpsertAsync(entity))
        {
            return "I could not record the notice, so the meeting stays unreadable. Say so plainly.";
        }

        _logger.LogInformation(
            "Capture notice recorded for '{Subject}' ({ThreadId}) delivered by: {How}",
            entity.Subject,
            entity.ThreadId,
            how);

        return entity.CaptureApproved
            ? $"Notice recorded for '{entity.Subject}' ({how}). Capture was already approved, so it is now eligible to be read."
            : $"Notice recorded for '{entity.Subject}' ({how}), but capture is still NOT approved, so I must not read it.";
    }

    /// <summary>
    /// Fetches a tracked meeting's transcript, but only when both gates are satisfied.
    ///
    /// The eligibility check is first and has no override. Everything below it is plumbing;
    /// this is the line that makes the registry mean anything.
    /// </summary>
    private async Task<string> ReadTranscriptAsync(string mailbox, JsonNode? args)
    {
        var subject = GetString(args, "subject");
        var match = await FindTrackedAsync(mailbox, subject);
        if (match.Error != null)
        {
            return match.Error;
        }

        var entity = match.Entity!;

        if (!entity.IsIngestionEligible)
        {
            var missing = !entity.CaptureApproved && !entity.NoticeSentUtc.HasValue
                ? "capture has not been approved and participants have not been notified"
                : !entity.CaptureApproved
                    ? "capture has not been approved"
                    : "participants have not been notified";

            return $"I must not read '{entity.Subject}': {missing}. Tell the user exactly what is missing "
                 + "and do not summarise the meeting from the calendar entry, the chat, or anything earlier "
                 + "in this conversation. Refusing is the correct outcome here.";
        }

        if (string.IsNullOrWhiteSpace(entity.JoinWebUrl))
        {
            return $"'{entity.Subject}' has no join URL stored, so I cannot resolve the meeting. Track it again.";
        }

        // The join URL contains a "?context={...}" suffix. Left raw in the request URI, that '?'
        // is parsed as the start of a new query parameter, which truncates the filter and returns
        // "unterminated string literal". The value has to be percent-encoded. Note also that only
        // JoinWebUrl and joinMeetingId are filterable here: threadId is explicitly rejected.
        var encoded = Uri.EscapeDataString(entity.JoinWebUrl);
        var lookupPath = $"users/{Uri.EscapeDataString(mailbox)}/onlineMeetings?$filter=JoinWebUrl%20eq%20'{encoded}'";

        var (okMeeting, meetingResponse, meetingError) = await SendGraphAsync(HttpMethod.Get, lookupPath);
        if (!okMeeting)
        {
            return DescribeTranscriptFailure("resolve the meeting", entity.Subject, meetingError);
        }

        var meetingId = (JsonNode.Parse(meetingResponse ?? "{}")?["value"] as JsonArray)?
            .FirstOrDefault()?["id"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(meetingId))
        {
            return $"Teams has no online meeting matching '{entity.Subject}'. It may have expired: the "
                 + "transcript API only works while the meeting still exists. Say that plainly.";
        }

        var (okList, listResponse, listError) = await SendGraphAsync(
            HttpMethod.Get, $"users/{Uri.EscapeDataString(mailbox)}/onlineMeetings/{meetingId}/transcripts");

        if (!okList)
        {
            return DescribeTranscriptFailure("list transcripts for", entity.Subject, listError);
        }

        var transcripts = JsonNode.Parse(listResponse ?? "{}")?["value"] as JsonArray;
        if (transcripts == null || transcripts.Count == 0)
        {
            entity.IngestionState = "none";
            await _store.UpsertAsync(entity);
            return $"'{entity.Subject}' has no transcript. Nobody turned transcription on during the meeting, "
                 + "so there is nothing recorded to read. Tell the user that specifically, because it is not a "
                 + "permission problem and retrying will not help. Do not reconstruct the meeting from anything else.";
        }

        // Newest last: Graph returns them in creation order and a meeting can hold several.
        var latest = transcripts[^1];
        var transcriptId = latest?["id"]?.GetValue<string>();

        var (okContent, content, contentError) = await SendGraphAsync(
            HttpMethod.Get,
            $"users/{Uri.EscapeDataString(mailbox)}/onlineMeetings/{meetingId}/transcripts/{transcriptId}/content?$format=text/vtt",
            acceptRawText: true);

        if (!okContent || string.IsNullOrWhiteSpace(content))
        {
            return DescribeTranscriptFailure("read the transcript of", entity.Subject, contentError);
        }

        entity.TranscriptId = transcriptId ?? string.Empty;
        entity.IngestedUtc = DateTimeOffset.UtcNow;
        entity.IngestionState = "ingested";
        await _store.UpsertAsync(entity);

        _logger.LogInformation(
            "Transcript read for '{Subject}': {Chars} characters, transcript {TranscriptId}",
            entity.Subject,
            content.Length,
            transcriptId);

        var attributionNote = content.Contains("<v ", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "\n\nNOTE: this transcript carries no speaker names, so you cannot say who said or owns anything. "
            + "Do not guess an owner. Say that attribution is unavailable if the user asks who.";

        return $"Transcript of '{entity.Subject}' ({entity.StartUtc:yyyy-MM-dd HH:mm} {DisplayTimeZone}):{attributionNote}\n\n"
             + Truncate(content, 60000);
    }

    /// <summary>
    /// Separates the tenant-level switch from everything else. A 403 here usually is not the
    /// agent's permissions: Teams has a tenant setting that blocks Graph transcript access for
    /// every app at once, and the fix is in the Teams admin center, not Entra.
    /// </summary>
    private string DescribeTranscriptFailure(string action, string subject, string? error)
    {
        _logger.LogWarning("Failed to {Action} '{Subject}': {Error}", action, subject, error);

        if (error != null && error.Contains("GraphAccessToTranscriptsDisabled", StringComparison.OrdinalIgnoreCase))
        {
            return $"I could not {action} '{subject}': Graph access to transcripts is switched off for this "
                 + "tenant. That is a Teams admin setting (Meetings > Meeting settings > Transcript API access), "
                 + "not an app permission, and no permission change will work around it. Say that plainly.";
        }

        if (error != null && error.Contains("403"))
        {
            return $"I could not {action} '{subject}': access was denied. Tell the user I need permission to read "
                 + "their meeting artifacts, and that it is granted by a Teams administrator. Do not retry.";
        }

        return $"I could not {action} '{subject}': {error}. Say plainly that it failed and do not summarise the "
             + "meeting from any other source.";
    }

    private async Task<(TrackedMeetingEntity? Entity, string? Error)> FindTrackedAsync(string mailbox, string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return (null, "A meeting subject is required.");
        }

        var all = await _store.ListAsync(mailbox);
        var matches = all
            .Where(m => m.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            // Not in the registry yet. Rather than telling the user to go and run a separate
            // tracking command, look on the agent's own calendar and register it. Registering is
            // not permission: the entry is created with capture off, so the gates still apply
            // unchanged. This exists because making a human run bookkeeping steps before the agent
            // will answer a question is the agent failing to do its job.
            var (entity, error, _) = await TrackFromCalendarAsync(mailbox, subject);
            return entity != null ? (entity, null) : (null, error);
        }

        if (matches.Count > 1)
        {
            var list = string.Join("; ", matches.Take(5).Select(m => $"'{m.Subject}' {m.StartUtc:yyyy-MM-dd}"));
            return (null, $"'{subject}' matched {matches.Count} tracked meetings: {list}. Ask which one before changing any permission.");
        }

        return (matches[0], null);
    }

    /// <summary>
    /// The autopilot's OWN mailbox, from its agent user account. Not the manager's.
    ///
    /// The agent is an attendee in its own right, so the meetings it may recap are the ones on
    /// its own calendar, put there by someone inviting it. Reading the manager's calendar here
    /// would let it recap meetings it was never invited to, which is both wrong and unnecessary.
    /// </summary>
    private async Task<string?> ResolveAgentMailboxAsync()
    {
        if (_mailboxResolved)
        {
            return _agentMailbox;
        }

        _mailboxResolved = true;

        var (ok, response, error) = await SendGraphAsync(
            HttpMethod.Get,
            $"users/{_agentUserId}?$select=mail,userPrincipalName,displayName");

        if (!ok)
        {
            _logger.LogWarning("Could not resolve the agent's own mailbox for the meeting registry: {Error}", error);
            return null;
        }

        var node = JsonNode.Parse(response ?? "{}");
        _agentMailbox = node?["mail"]?.GetValue<string>() ?? node?["userPrincipalName"]?.GetValue<string>();

        _logger.LogInformation(
            "Meeting registry acting as the agent user {Mailbox} ({Name}), reading its own calendar.",
            _agentMailbox,
            node?["displayName"]?.GetValue<string>());

        return _agentMailbox;
    }

    private async Task<(bool Ok, string? Response, string? Error)> SendGraphAsync(
        HttpMethod method,
        string path,
        bool acceptRawText = false,
        string? preferTimeZone = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);

            // Without this Graph returns calendar times in UTC. They then get shown to the user
            // as-is and read as local, so a 2:30pm meeting was reported as 9:30pm. Asking Graph
            // for the target zone is the fix; formatting the number afterwards is not, because
            // nothing downstream knows what zone it was in.
            if (!string.IsNullOrWhiteSpace(preferTimeZone))
            {
                request.Headers.TryAddWithoutValidation("Prefer", $"outlook.timezone=\"{preferTimeZone}\"");
            }

            if (acceptRawText)
            {
                request.Headers.TryAddWithoutValidation("Accept", "text/vtt");
            }

            using var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            return response.IsSuccessStatusCode
                ? (true, text, null)
                : (false, null, $"HTTP {(int)response.StatusCode} {Truncate(text, 400)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graph call failed: {Method} {Path}", method, path);
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Windows time zone id the agent reports meeting times in. Defaults to the manager's zone
    /// rather than UTC, because every consumer of these strings is a human reading chat.
    /// </summary>
    private string DisplayTimeZone =>
        _configuration["MeetingDisplayTimeZone"] ?? "Pacific Standard Time";

    private string DescribeFailure(string action, string mailbox, string? error)
    {
        _logger.LogWarning("Failed to {Action} {Mailbox}: {Error}", action, mailbox, error);

        if (error != null && (error.Contains("403") || error.Contains("ErrorAccessDenied", StringComparison.OrdinalIgnoreCase)))
        {
            return $"I could not {action} {mailbox}: Exchange denied access. That is a mailbox permission, not a "
                 + "Graph one. Tell the user I need delegate access to their calendar, granted in Outlook or the "
                 + "Microsoft 365 admin center.";
        }

        return $"I could not {action} {mailbox}: {error}. Tell the user plainly that it failed.";
    }

    /// <summary>
    /// Pulls the Teams thread id out of a join URL. Used as the registry key because it is present
    /// on the calendar entry itself. Note it cannot be used to look the meeting up in Graph later:
    /// only JoinWebUrl and joinMeetingId are filterable on onlineMeetings, which is why the join
    /// URL is stored alongside it.
    /// </summary>
    private static string ExtractThreadId(string joinUrl)
    {
        if (string.IsNullOrWhiteSpace(joinUrl))
        {
            return string.Empty;
        }

        var m = Regex.Match(joinUrl, @"meetup-join/([^/?]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : string.Empty;
    }

    private static DateTimeOffset ParseDateOrDefault(string value, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : fallback;

    private static DateTimeOffset? ParseDateOrNull(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string GetString(JsonNode? args, string name) =>
        args?[name]?.GetValue<string>()?.Trim() ?? string.Empty;

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value ?? string.Empty : value[..max];
}


