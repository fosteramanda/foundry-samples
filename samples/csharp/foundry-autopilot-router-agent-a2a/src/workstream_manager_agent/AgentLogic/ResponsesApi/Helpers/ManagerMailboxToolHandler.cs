namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using WorkstreamManager.Models;

/// <summary>
/// Sends mail and creates calendar events in the MANAGER'S mailbox, not the agent's own.
///
/// Why this exists as a separate handler: an autopilot has its own mailbox, and every default
/// path leads there. `/me/sendMail` sends from the agent. `/me/events` writes to the agent's
/// empty calendar. Both succeed, and neither does what the manager asked for. Acting on the
/// manager's behalf means naming the manager's mailbox explicitly on every call.
///
/// Two separate permission systems both have to be in place, and they fail differently:
///
///   1. Microsoft Graph delegated scopes on the blueprint: Calendars.ReadWrite.Shared and
///      Mail.Send.Shared. Missing these fails at token acquisition, so no call is made.
///
///   2. An Exchange grant on the manager's mailbox: calendar delegation, and Send on Behalf
///      (or Send As) for mail. Missing these fails at send time with ErrorAccessDenied.
///
/// Microsoft Graph cannot report which mailboxes the caller holds permissions for, so neither
/// this code nor the model can check (2) in advance. The only signal is the 403 at send time,
/// which is why it is translated into a specific message below rather than surfaced raw.
/// </summary>
public class ManagerMailboxToolHandler
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string? _graphAccessToken;
    private readonly Guid _agentUserId;

    // Resolved once per handler instance. The manager relationship does not change mid-turn,
    // and resolving it is a Graph round trip.
    private string? _managerMailbox;
    private bool _managerResolved;

    public ManagerMailboxToolHandler(
        AgentMetadata agentMetadata,
        ILogger logger,
        HttpClient httpClient,
        IConfiguration configuration,
        string? graphAccessToken)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _graphAccessToken = graphAccessToken;
        _agentUserId = agentMetadata?.UserId ?? Guid.Empty;
    }

    /// <summary>
    /// True when a Graph token is available. Without one these tools are not attached at all,
    /// rather than being offered and failing on use.
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_graphAccessToken) && _agentUserId != Guid.Empty;

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
                "name": "send_email_as_manager",
                "description": "Sends an email FROM the manager's mailbox, on their behalf. Use this when the manager asks you to email someone, send a note, follow up, or reply to a person. Recipients see it as coming from the manager. Do NOT use this to reply in chat - your chat reply is delivered automatically.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "to": { "type": "array", "items": { "type": "string" }, "description": "Recipients. Either email addresses, or just people's names as the manager said them, e.g. 'Sustineo Juarez'. Names are looked up in the directory automatically, so never stop to ask the manager for an email address you were not given." },
                        "subject": { "type": "string", "description": "Subject line. Be specific; this is what the recipient sees first." },
                        "body_html": { "type": "string", "description": "Body as HTML. Write it as the manager would, in their voice, not as a report about the manager." },
                        "cc": { "type": "array", "items": { "type": "string" }, "description": "Optional CC addresses." }
                    },
                    "required": ["to", "subject", "body_html"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "create_calendar_event_for_manager",
                "description": "Creates an event or meeting in the MANAGER'S calendar. Use this when asked to schedule, book, set up a meeting, or block time. Attendees receive the invitation from the manager. Always confirm the time and attendees with the manager before creating, unless they gave both explicitly.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "subject": { "type": "string", "description": "Meeting title." },
                        "start": { "type": "string", "description": "Start as local date-time with no offset, e.g. 2026-09-08T09:00:00" },
                        "end": { "type": "string", "description": "End as local date-time with no offset, e.g. 2026-09-08T09:30:00" },
                        "time_zone": { "type": "string", "description": "Windows time zone name for start/end, e.g. 'Pacific Standard Time'. Defaults to Pacific Standard Time when omitted." },
                        "attendees": { "type": "array", "items": { "type": "string" }, "description": "Attendees. Either email addresses, or just people's names as the manager said them, e.g. 'Sustineo Juarez'. Names are looked up in the directory automatically, so never stop to ask the manager for an email address. Omit for a personal time block." },
                        "body_html": { "type": "string", "description": "Optional agenda or description, as HTML." },
                        "is_online_meeting": { "type": "boolean", "description": "Add a Teams link. Defaults to true when there are attendees." }
                    },
                    "required": ["subject", "start", "end"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "list_manager_calendar",
                "description": "Lists events from the MANAGER'S calendar over a date range. Use this to answer what is on their calendar, whether they are free, or to find a slot before scheduling. Never answer calendar questions from memory or from the conversation; always read the calendar.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "start": { "type": "string", "description": "Range start as an ISO date-time, e.g. 2026-09-08T00:00:00" },
                        "end": { "type": "string", "description": "Range end as an ISO date-time, e.g. 2026-09-09T00:00:00" },
                        "time_zone": { "type": "string", "description": "Windows time zone for the range and returned times. Defaults to Pacific Standard Time." }
                    },
                    "required": ["start", "end"],
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

        if (toolName is not ("send_email_as_manager" or "create_calendar_event_for_manager" or "list_manager_calendar"))
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
            _logger.LogWarning(ex, "Manager mailbox tool {Tool} called with unparseable arguments.", toolName);
            return $"Could not parse arguments for {toolName}.";
        }

        var mailbox = await ResolveManagerMailboxAsync();
        if (mailbox == null)
        {
            return "I could not work out which mailbox to act on. No manager is set on my account and no "
                 + "ManagerMailboxUpn is configured, so I do not know who I am acting for. Tell the user this "
                 + "plainly rather than sending anything from my own mailbox.";
        }

        return toolName switch
        {
            "send_email_as_manager" => await SendMailAsync(mailbox, args),
            "create_calendar_event_for_manager" => await CreateEventAsync(mailbox, args),
            "list_manager_calendar" => await ListCalendarAsync(mailbox, args),
            _ => null,
        };
    }

    /// <summary>
    /// Works out whose mailbox to act on: the manager relationship on the agent's own user
    /// account, which is the directory's own record of who this autopilot works for.
    ///
    /// Deliberately not a hardcoded address. The agent user already carries a manager, so
    /// reading it keeps the sample deployable by anyone without editing configuration, and it
    /// means the mailbox the agent acts on and the person accountable for the agent cannot
    /// drift apart. ManagerMailboxUpn overrides it for deployments where the two differ.
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
            _logger.LogInformation("Manager mailbox from configuration: {Mailbox}", _managerMailbox);
            return _managerMailbox;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{_agentUserId}/manager?$select=mail,userPrincipalName,displayName");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);

            using var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Could not resolve the agent's manager: HTTP {Status} {Body}",
                    (int)response.StatusCode,
                    Truncate(text, 200));
                return null;
            }

            var node = JsonNode.Parse(text);
            _managerMailbox = node?["mail"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(_managerMailbox))
            {
                _managerMailbox = node?["userPrincipalName"]?.GetValue<string>();
            }

            _logger.LogInformation(
                "Resolved manager mailbox: {Mailbox} ({Name})",
                _managerMailbox,
                node?["displayName"]?.GetValue<string>());

            return _managerMailbox;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve the agent's manager.");
            return null;
        }
    }

    private async Task<string> SendMailAsync(string mailbox, JsonNode? args)
    {
        var toRaw = ReadAddresses(args, "to");
        if (toRaw.Count == 0)
        {
            return "At least one recipient is required.";
        }

        var (to, toFailed) = await ResolvePeopleAsync(toRaw);
        var (cc, ccFailed) = await ResolvePeopleAsync(ReadAddresses(args, "cc"));
        var allFailed = toFailed.Concat(ccFailed).ToList();
        if (allFailed.Count > 0)
        {
            return $"I could not find {string.Join(" or ", allFailed.Select(f => $"'{f}'"))} in the directory. "
                 + "Ask the user for that person's email address, or for a more complete name. Do not send to "
                 + "anyone else and do not guess.";
        }
        if (to.Count == 0)
        {
            return "At least one recipient is required.";
        }

        var message = new JsonObject
        {
            ["subject"] = GetString(args, "subject"),
            ["body"] = new JsonObject
            {
                ["contentType"] = "HTML",
                ["content"] = GetString(args, "body_html"),
            },
            ["toRecipients"] = BuildRecipients(to),
        };

        if (cc.Count > 0)
        {
            message["ccRecipients"] = BuildRecipients(cc);
        }

        var body = new JsonObject
        {
            ["message"] = message,
            ["saveToSentItems"] = true,
        };

        // Sent through the MANAGER'S mailbox, which is what makes it come from them. The same
        // call against /me would send from the agent's own mailbox and look like a different
        // sender entirely.
        var (ok, _, error) = await SendGraphAsync(
            HttpMethod.Post,
            $"users/{Uri.EscapeDataString(mailbox)}/sendMail",
            body);

        if (!ok)
        {
            return DescribeFailure("send mail from", mailbox, error);
        }

        _logger.LogInformation("Sent mail on behalf of {Mailbox} to {Count} recipient(s).", mailbox, to.Count);
        return $"Sent from {mailbox} to {string.Join(", ", to)}. Tell the user in one short line what was sent and to whom.";
    }

    private async Task<string> CreateEventAsync(string mailbox, JsonNode? args)
    {
        var timeZone = GetString(args, "time_zone");
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            timeZone = "Pacific Standard Time";
        }

        var attendeesRaw = ReadAddresses(args, "attendees");
        var (attendees, attendeeFailed) = await ResolvePeopleAsync(attendeesRaw);
        if (attendeeFailed.Count > 0)
        {
            return $"I could not find {string.Join(" or ", attendeeFailed.Select(f => $"'{f}'"))} in the directory. "
                 + "Ask the user for that person's email address, or for a fuller name. Do not create the meeting "
                 + "without them and do not invite someone else.";
        }

        var body = new JsonObject
        {
            ["subject"] = GetString(args, "subject"),
            ["start"] = new JsonObject { ["dateTime"] = GetString(args, "start"), ["timeZone"] = timeZone },
            ["end"] = new JsonObject { ["dateTime"] = GetString(args, "end"), ["timeZone"] = timeZone },
        };

        var bodyHtml = GetString(args, "body_html");
        if (!string.IsNullOrWhiteSpace(bodyHtml))
        {
            body["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = bodyHtml };
        }

        if (attendees.Count > 0)
        {
            var list = new JsonArray();
            foreach (var a in attendees)
            {
                list.Add(new JsonObject
                {
                    ["emailAddress"] = new JsonObject { ["address"] = a },
                    ["type"] = "required",
                });
            }
            body["attendees"] = list;

            // Default a meeting with attendees to online. A meeting with people in it and no
            // join link is almost never what was wanted.
            var online = args?["is_online_meeting"]?.GetValue<bool>() ?? true;
            if (online)
            {
                body["isOnlineMeeting"] = true;
                body["onlineMeetingProvider"] = "teamsForBusiness";
            }
        }
        else if (args?["is_online_meeting"]?.GetValue<bool>() == true)
        {
            body["isOnlineMeeting"] = true;
            body["onlineMeetingProvider"] = "teamsForBusiness";
        }

        var (ok, response, error) = await SendGraphAsync(
            HttpMethod.Post,
            $"users/{Uri.EscapeDataString(mailbox)}/events",
            body);

        if (!ok)
        {
            return DescribeFailure("create an event in", mailbox, error);
        }

        var link = JsonNode.Parse(response ?? "{}")?["webLink"]?.GetValue<string>();
        _logger.LogInformation("Created event in {Mailbox} with {Count} attendee(s).", mailbox, attendees.Count);

        return $"Created in {mailbox}'s calendar. "
             + (attendees.Count > 0 ? $"Invitations sent to {string.Join(", ", attendees)}. " : string.Empty)
             + (string.IsNullOrWhiteSpace(link) ? string.Empty : $"Link: {link} ")
             + "Confirm to the user in one short line what was scheduled and when.";
    }

    private async Task<string> ListCalendarAsync(string mailbox, JsonNode? args)
    {
        var timeZone = GetString(args, "time_zone");
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            timeZone = "Pacific Standard Time";
        }

        var start = GetString(args, "start");
        var end = GetString(args, "end");
        var path = $"users/{Uri.EscapeDataString(mailbox)}/calendarView"
                 + $"?startDateTime={Uri.EscapeDataString(start)}&endDateTime={Uri.EscapeDataString(end)}"
                 + "&$select=subject,start,end,organizer,attendees,isAllDay,showAs,webLink&$orderby=start/dateTime&$top=50";

        // calendarView expands recurring series into occurrences; /events would return the
        // series master instead and misreport what is actually on a given day.
        var (ok, response, error) = await SendGraphAsync(HttpMethod.Get, path, null, timeZone);
        if (!ok)
        {
            return DescribeFailure("read the calendar of", mailbox, error);
        }

        var items = JsonNode.Parse(response ?? "{}")?["value"] as JsonArray;
        if (items == null || items.Count == 0)
        {
            return $"Nothing on {mailbox}'s calendar between {start} and {end}.";
        }

        _logger.LogInformation("Read {Count} event(s) from {Mailbox}.", items.Count, mailbox);
        return items.ToJsonString();
    }

    private async Task<(bool Ok, string? Response, string? Error)> SendGraphAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        string? preferTimeZone = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);

            if (!string.IsNullOrWhiteSpace(preferTimeZone))
            {
                request.Headers.TryAddWithoutValidation("Prefer", $"outlook.timezone=\"{preferTimeZone}\"");
            }

            if (body != null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"HTTP {(int)response.StatusCode} {Truncate(text, 400)}");
            }

            return (true, text, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graph call failed: {Method} {Path}", method, path);
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Turns a Graph failure into something the model can tell the user honestly.
    ///
    /// The 403 case is the one worth separating out. It does not mean the Graph scope is
    /// missing; it means the Exchange grant on the manager's mailbox is. Those are configured
    /// in different places by different people, and conflating them sends whoever is
    /// troubleshooting to the wrong system.
    /// </summary>
    private string DescribeFailure(string action, string mailbox, string? error)
    {
        _logger.LogWarning("Failed to {Action} {Mailbox}: {Error}", action, mailbox, error);

        if (error != null && (error.Contains("403") || error.Contains("ErrorAccessDenied", StringComparison.OrdinalIgnoreCase)))
        {
            return $"I could not {action} {mailbox}: access was denied by Exchange. This is a mailbox "
                 + "permission, not a Graph one. Tell the user I need delegate access to their calendar, "
                 + "or Send on Behalf for their mailbox, and that it is granted in Outlook or the Microsoft "
                 + "365 admin center. Do not retry and do not send from my own mailbox instead.";
        }

        return $"I could not {action} {mailbox}: {error}. Tell the user plainly that it failed and what the "
             + "error was. Do not claim it succeeded and do not fall back to my own mailbox.";
    }

    private static JsonArray BuildRecipients(List<string> addresses)
    {
        var list = new JsonArray();
        foreach (var a in addresses)
        {
            list.Add(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a } });
        }
        return list;
    }

    /// <summary>
    /// Turns whatever the model passed for a person into a real address.
    ///
    /// The model gets names, not addresses. A manager says "schedule 30 minutes with Sustineo",
    /// or @-mentions someone in Teams, and the mention markup is stripped to a display name
    /// before the model ever sees it. Requiring an email address means the agent has to stop and
    /// ask for something the manager reasonably expects it to know, which is what a chief of
    /// staff would never do.
    ///
    /// So: anything already shaped like an address passes through; anything else is looked up in
    /// the directory. Names that match nothing, or match more than one person, are returned as
    /// failures rather than guessed at, because inviting the wrong person to a meeting is worse
    /// than asking.
    /// </summary>
    private async Task<(List<string> Resolved, List<string> Failed)> ResolvePeopleAsync(List<string> inputs)
    {
        var resolved = new List<string>();
        var failed = new List<string>();

        foreach (var raw in inputs)
        {
            var value = raw.Trim().Trim('<', '>');

            // Already an address. Not a strict validation: an address with an apostrophe or
            // unusual local part is still an address, and Exchange will reject anything truly
            // malformed with a clearer error than we could produce here.
            if (value.Contains('@') && !value.Contains(' '))
            {
                resolved.Add(value);
                continue;
            }

            var match = await LookupPersonAsync(value);
            if (match == null)
            {
                failed.Add(value);
            }
            else
            {
                _logger.LogInformation("Resolved attendee '{Input}' to {Address}.", value, match);
                resolved.Add(match);
            }
        }

        return (resolved, failed);
    }

    /// <summary>
    /// Finds one person in the directory by display name. Returns null when there is no match or
    /// when the name is ambiguous, so the caller can say which name it could not place.
    /// </summary>
    private async Task<string?> LookupPersonAsync(string name)
    {
        try
        {
            var escaped = name.Replace("'", "''");
            var filter = Uri.EscapeDataString($"startswith(displayName,'{escaped}')");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users?$filter={filter}&$select=displayName,mail,userPrincipalName&$top=5");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Directory lookup for '{Name}' failed: HTTP {Status}",
                    name,
                    (int)response.StatusCode);
                return null;
            }

            var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["value"] as JsonArray;
            if (items == null || items.Count == 0)
            {
                return null;
            }

            // Ambiguity is a real answer, not an inconvenience: two people whose names both start
            // with "Chris" must not be silently collapsed into whichever came back first.
            if (items.Count > 1)
            {
                var exact = items.FirstOrDefault(i =>
                    string.Equals(i?["displayName"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
                if (exact == null)
                {
                    _logger.LogInformation("Directory lookup for '{Name}' was ambiguous ({Count} matches).", name, items.Count);
                    return null;
                }
                return exact["mail"]?.GetValue<string>() ?? exact["userPrincipalName"]?.GetValue<string>();
            }

            return items[0]?["mail"]?.GetValue<string>() ?? items[0]?["userPrincipalName"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Directory lookup for '{Name}' threw.", name);
            return null;
        }
    }

    private static List<string> ReadAddresses(JsonNode? node, string name)
    {
        var result = new List<string>();
        if (node?[name] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var v = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    result.Add(v.Trim());
                }
            }
        }
        else
        {
            var single = node?[name]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(single))
            {
                result.Add(single.Trim());
            }
        }
        return result;
    }

    private static string GetString(JsonNode? node, string name) =>
        node?[name]?.GetValue<string>() ?? string.Empty;

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max] + "...";
}
