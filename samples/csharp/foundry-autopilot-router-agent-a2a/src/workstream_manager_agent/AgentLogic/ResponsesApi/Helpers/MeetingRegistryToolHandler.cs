namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkstreamManager.Models;
using WorkstreamManager.Services;

/// <summary>
/// Records which meetings the autopilot has been asked to follow, and separately, whether it has
/// been permitted to use what was said in them.
///
/// Why the permission is a separate act from the association: Teams can only grant meeting access
/// per organizer, never per meeting. An application access policy naming this agent and that
/// organizer opens every meeting that person runs, all at once. The finer control, "this meeting
/// yes, last Tuesday's one to one no", does not exist in the platform, so it has to exist here.
///
/// Two flags gate ingestion and both are required:
///
///   CaptureApproved   the organizer said yes
///   NoticeSentUtc     the people in the room were told
///
/// They are deliberately not one field. The organizer approving is not the same event as the
/// room being informed, and an organizer cannot consent on behalf of the other attendees. A
/// design that collapses them reads as consent while only ever having recorded permission.
///
/// This handler intentionally does no transcript reading. It is the gate, not the door. At the
/// time of writing the door is closed anyway: NotARealCo has Graph transcript access turned off
/// at tenant level, which returns GraphAccessToTranscriptsDisabled regardless of app permissions.
/// </summary>
public class MeetingRegistryToolHandler
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string? _graphAccessToken;
    private readonly Guid _agentUserId;
    private readonly MeetingRegistryStore _store;

    private string? _managerMailbox;
    private bool _managerResolved;

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
                "description": "Starts following a meeting on the manager's calendar so it can later be recapped. This ONLY registers the meeting. It does NOT give permission to read what was said: tracking always starts with capture switched off, and the user must approve it separately. Use this when the user asks you to follow, track, watch, or take notes on a meeting.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "subject": { "type": "string", "description": "All or part of the meeting subject as it appears on the calendar." },
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
                "description": "Turns permission to use a tracked meeting's content on or off. Only the organizer should be doing this. Approving does NOT by itself make the meeting readable: participants must also have been notified, which is recorded with record_capture_notice. Say plainly which meeting is being changed and read the subject back.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "subject": { "type": "string", "description": "Subject of a meeting already returned by list_tracked_meetings." },
                        "approved": { "type": "boolean", "description": "true to permit using this meeting's content, false to withdraw permission." },
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
        ];
    }

    public async Task<string?> TryExecuteAsync(string toolName, string arguments)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (toolName is not ("track_meeting" or "list_tracked_meetings" or "set_meeting_capture" or "record_capture_notice"))
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

        var mailbox = await ResolveManagerMailboxAsync();
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            return "I could not work out whose calendar to look at. My user account has no manager set in the "
                 + "directory. Tell the user plainly rather than guessing an address.";
        }

        return toolName switch
        {
            "track_meeting" => await TrackMeetingAsync(mailbox, args),
            "list_tracked_meetings" => await ListTrackedAsync(mailbox),
            "set_meeting_capture" => await SetCaptureAsync(mailbox, args),
            "record_capture_notice" => await RecordNoticeAsync(mailbox, args),
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

        var path = $"users/{Uri.EscapeDataString(mailbox)}/calendarView"
                 + $"?startDateTime={from:yyyy-MM-ddTHH:mm:ssZ}&endDateTime={to:yyyy-MM-ddTHH:mm:ssZ}"
                 + "&$select=subject,start,end,organizer,isOnlineMeeting,onlineMeeting&$orderby=start/dateTime&$top=100";

        var (ok, response, error) = await SendGraphAsync(HttpMethod.Get, path);
        if (!ok)
        {
            return DescribeFailure("read the calendar of", mailbox, error);
        }

        var items = JsonNode.Parse(response ?? "{}")?["value"] as JsonArray;
        var matches = (items ?? [])
            .Where(e => e?["isOnlineMeeting"]?.GetValue<bool>() == true)
            .Where(e => (e?["subject"]?.GetValue<string>() ?? string.Empty)
                .Contains(subject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return $"No Teams meeting matching '{subject}' was found on {mailbox}'s calendar between "
                 + $"{from:yyyy-MM-dd} and {to:yyyy-MM-dd}. Do not invent one; ask the user to check the subject or the dates.";
        }

        if (matches.Count > 1)
        {
            var list = string.Join("; ", matches.Take(5).Select(m =>
                $"'{m?["subject"]?.GetValue<string>()}' on {m?["start"]?["dateTime"]?.GetValue<string>()}"));
            return $"'{subject}' matched {matches.Count} meetings: {list}. Ask the user which one before tracking anything.";
        }

        var ev = matches[0];
        var joinUrl = ev?["onlineMeeting"]?["joinUrl"]?.GetValue<string>() ?? string.Empty;
        var threadId = ExtractThreadId(joinUrl);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return "That meeting has no Teams thread id on the calendar entry, so there is nothing stable to "
                 + "track it by. Tell the user it cannot be tracked rather than storing a partial record.";
        }

        var organizer = ev?["organizer"]?["emailAddress"]?["address"]?.GetValue<string>() ?? mailbox;
        var existing = await _store.GetAsync(organizer, threadId);

        var entity = existing ?? new TrackedMeetingEntity
        {
            PartitionKey = MeetingRegistryStore.ToPartitionKey(organizer),
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
            return "I could not save that meeting to the registry, so it is NOT being tracked. Say so plainly.";
        }

        var already = existing != null ? " It was already being tracked." : string.Empty;
        return $"Now tracking '{entity.Subject}' ({entity.StartUtc:yyyy-MM-dd HH:mm} UTC).{already} "
             + $"Capture is {(entity.CaptureApproved ? "APPROVED" : "NOT approved")} and participants have "
             + $"{(entity.NoticeSentUtc.HasValue ? "been notified" : "NOT been notified")}. "
             + "Tell the user tracking alone does not let me read what was said, and ask whether they want to approve capture.";
    }

    private async Task<string> ListTrackedAsync(string mailbox)
    {
        var all = await _store.ListAsync(mailbox);
        if (all.Count == 0)
        {
            return $"No meetings are being tracked for {mailbox}.";
        }

        var lines = all.Select(m =>
            $"- '{m.Subject}' {m.StartUtc:yyyy-MM-dd HH:mm} UTC | capture={(m.CaptureApproved ? "approved" : "not approved")}"
            + $" | notice={(m.NoticeSentUtc.HasValue ? m.NoticeSentUtc.Value.ToString("yyyy-MM-dd") : "not sent")}"
            + $" | readable={(m.IsIngestionEligible ? "yes" : "NO")}"
            + $" | retention={(m.RetentionApproved ? "approved" : "not approved")}"
            + $" | state={m.IngestionState}");

        return $"Tracked meetings for {mailbox}:\n{string.Join("\n", lines)}\n"
             + "'readable=NO' means I must not use that meeting's content, whatever else is set.";
    }

    private async Task<string> SetCaptureAsync(string mailbox, JsonNode? args)
    {
        var subject = GetString(args, "subject");
        var approved = args?["approved"]?.GetValue<bool>() ?? false;
        var approveRetention = args?["approve_retention"]?.GetValue<bool>();

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

        if (!await _store.UpsertAsync(entity))
        {
            return "I could not save that change, so the previous setting still stands. Say so plainly.";
        }

        if (!approved)
        {
            return $"Capture is now OFF for '{entity.Subject}'. I will not use its content, and anything queued for it is excluded.";
        }

        return entity.NoticeSentUtc.HasValue
            ? $"Capture is now ON for '{entity.Subject}' and participants were notified on {entity.NoticeSentUtc:yyyy-MM-dd}. It is eligible to be read."
            : $"Capture is now ON for '{entity.Subject}', but participants have NOT been notified yet, so I still must not read it. "
              + "Tell the user the notice has to go to the attendees first, then call record_capture_notice once it has actually been sent.";
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
            return (null, $"'{subject}' is not being tracked. Track it first; do not assume a permission that was never recorded.");
        }

        if (matches.Count > 1)
        {
            var list = string.Join("; ", matches.Take(5).Select(m => $"'{m.Subject}' {m.StartUtc:yyyy-MM-dd}"));
            return (null, $"'{subject}' matched {matches.Count} tracked meetings: {list}. Ask which one before changing any permission.");
        }

        return (matches[0], null);
    }

    /// <summary>
    /// The manager relationship on the agent's own user account. The agent user account and the
    /// agent identity are different objects; this is deliberately the user account, because a
    /// manager is a directory relationship between people and their autopilots.
    /// </summary>
    private async Task<string?> ResolveManagerMailboxAsync()
    {
        if (_managerResolved)
        {
            return _managerMailbox;
        }

        _managerResolved = true;

        var configured = _configuration["ManagerMailboxUpn"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _managerMailbox = configured.Trim();
            return _managerMailbox;
        }

        var (ok, response, error) = await SendGraphAsync(
            HttpMethod.Get,
            $"users/{_agentUserId}/manager?$select=mail,userPrincipalName,displayName");

        if (!ok)
        {
            _logger.LogWarning("Could not resolve the agent's manager for the meeting registry: {Error}", error);
            return null;
        }

        var node = JsonNode.Parse(response ?? "{}");
        _managerMailbox = node?["mail"]?.GetValue<string>() ?? node?["userPrincipalName"]?.GetValue<string>();
        return _managerMailbox;
    }

    private async Task<(bool Ok, string? Response, string? Error)> SendGraphAsync(HttpMethod method, string path)
    {
        try
        {
            using var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);

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
