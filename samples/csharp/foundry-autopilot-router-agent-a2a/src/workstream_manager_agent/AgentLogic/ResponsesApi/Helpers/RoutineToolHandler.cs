namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Agents.Core.Models;
using WorkstreamManager.Models;
using WorkstreamManager.Services;

/// <summary>
/// Lets the autopilot set up its own standing work — "routines" — from a normal conversation,
/// instead of someone hand-writing a REST call.
///
/// Why the agent has to be the one to create them: a routine's action carries a full activity
/// payload (channelId, serviceUrl, from, conversation, recipient). That payload is a CONVERSATION
/// REFERENCE — it says which chat the scheduled run posts into. Those ids only exist on a live
/// turn, so a routine created outside a conversation has nowhere to deliver. Creating it from the
/// turn that requested it is what makes each instance own its own routines: the same agent in two
/// chats produces two routines that post to two different places.
///
/// The routine fires by calling the agent's own activity-protocol endpoint with a synthetic user
/// message, so a scheduled run is handled by exactly the same code path as a typed request — the
/// agent needs no special "scheduled mode".
///
/// Preview: the API requires the "Foundry-Features: Routines=V1Preview" header.
/// </summary>
public class RoutineToolHandler
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly AgentTokenCredential? _credential;
    private readonly string? _projectEndpoint;
    private readonly string? _agentName;
    private readonly string? _graphAccessToken;

    // The activity that triggered the current turn. Everything needed to address a future
    // scheduled run is taken from it.
    private IActivity? _currentActivity;

    private const string FeaturesHeader = "Routines=V1Preview";
    private const string ApiVersion = "2025-11-15-preview";

    public RoutineToolHandler(
        AgentMetadata agentMetadata,
        AgentTokenHelper? tokenHelper,
        ILogger logger,
        HttpClient httpClient,
        IConfiguration configuration,
        string? graphAccessToken = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _graphAccessToken = graphAccessToken;

        _projectEndpoint = configuration["FoundryProjectEndpoint"]
            ?? Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        _agentName = configuration["FoundryAgentName"]
            ?? Environment.GetEnvironmentVariable("FOUNDRY_AGENT_NAME");

        if (tokenHelper != null)
        {
            _credential = new AgentTokenCredential(tokenHelper, agentMetadata);
        }
    }

    /// <summary>
    /// True when routines can actually be managed. False disables the tools entirely rather than
    /// advertising them: an agent told it can schedule work, that then cannot, will promise a
    /// standing job it never created.
    /// </summary>
    public bool IsEnabled =>
        _credential != null
        && !string.IsNullOrWhiteSpace(_projectEndpoint)
        && !string.IsNullOrWhiteSpace(_agentName);

    /// <summary>Captures the turn's activity so a created routine can address this conversation.</summary>
    public void SetCurrentActivityContext(IActivity activity) => _currentActivity = activity;

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
                "name": "create_routine",
                "description": "Sets up standing work that runs on a schedule in THIS conversation — for example a weekday morning summary, a weekly digest, or a recurring check. Use this whenever the user asks for something to happen regularly, repeatedly, every day/week, on a schedule, or 'from now on'. Convert the user's wording into a cron expression yourself. Output goes to this chat by default, or by email if they ask for an email.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string", "description": "Short kebab-case identifier, e.g. 'weekday-morning-summary'. Lowercase letters, digits and hyphens only." },
                        "description": { "type": "string", "description": "One line describing what this routine does, for later listing." },
                        "cron_expression": { "type": "string", "description": "5-field cron: minute hour day-of-month month day-of-week. Weekdays at 07:30 is '30 7 * * 1-5'. Every day at 09:00 is '0 9 * * *'. Fridays at 15:00 is '0 15 * * 5'." },
                        "time_zone": { "type": "string", "description": "IANA time zone the cron is interpreted in, e.g. 'America/Los_Angeles' for Pacific, 'America/New_York' for Eastern, 'UTC'. Always set this from what the user said; never assume UTC when they named a local time." },
                        "instruction": { "type": "string", "description": "The instruction to give yourself when the routine fires, written as if the user had just typed it. Be specific about the output format so recurring posts stay consistent, e.g. 'Post a short summary of what is open and anything waiting on someone. One line per item.'" },
                        "delivery": { "type": "string", "enum": ["chat", "email", "both"], "description": "Where the scheduled run sends its output. 'chat' (default) posts into this conversation. Use 'email' when the user asks to be emailed, e.g. 'send me a morning email'. 'both' does each." },
                        "recipient": { "type": "string", "description": "Email address for email delivery. LEAVE THIS EMPTY when the user says 'me', 'my', or otherwise means themselves — it is resolved automatically from who is speaking. Only set it when they name a different person's address explicitly." }
                    },
                    "required": ["name", "description", "cron_expression", "time_zone", "instruction"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "list_routines",
                "description": "Lists the standing work currently set up on this agent, with each routine's schedule and whether it is enabled. Use this when the user asks what is scheduled, what standing work exists, or what you are running for them.",
                "parameters": { "type": "object", "properties": {}, "additionalProperties": false }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "set_routine_enabled",
                "description": "Pauses or resumes an existing routine without deleting it. Use this when the user asks to pause, stop for now, suspend, or resume standing work.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string", "description": "Routine name exactly as returned by list_routines" },
                        "enabled": { "type": "boolean", "description": "false to pause, true to resume" }
                    },
                    "required": ["name", "enabled"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "delete_routine",
                "description": "Permanently removes a routine. Prefer set_routine_enabled when the user only wants to pause it. Confirm with the user before deleting.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "name": { "type": "string", "description": "Routine name exactly as returned by list_routines" }
                    },
                    "required": ["name"],
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

        JsonNode? args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routine tool {Tool} called with unparseable arguments.", toolName);
            return $"Could not parse arguments for {toolName}.";
        }

        return toolName switch
        {
            "create_routine" => await CreateRoutineAsync(args),
            "list_routines" => await ListRoutinesAsync(),
            "set_routine_enabled" => await SetEnabledAsync(
                GetString(args, "name"),
                args?["enabled"]?.GetValue<bool>() ?? true),
            "delete_routine" => await DeleteRoutineAsync(GetString(args, "name")),
            _ => null,
        };
    }

    private async Task<string> CreateRoutineAsync(JsonNode? args)
    {
        var name = Slugify(GetString(args, "name"));
        var description = GetString(args, "description");
        var cron = GetString(args, "cron_expression");
        var timeZone = GetString(args, "time_zone");
        var instruction = GetString(args, "instruction");
        var delivery = GetString(args, "delivery");
        delivery = string.IsNullOrWhiteSpace(delivery) ? "chat" : delivery.Trim().ToLowerInvariant();
        var recipient = GetString(args, "recipient");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cron) || string.IsNullOrWhiteSpace(instruction))
        {
            return "name, cron_expression and instruction are all required to create a routine.";
        }

        if (_currentActivity == null)
        {
            return "Cannot create a routine outside a conversation: there is no chat for the scheduled run to post into.";
        }

        // Resolve the recipient NOW, while there is a speaker to resolve. When this routine fires
        // at 07:30 there is no inbound message and no "me" — an instruction containing the word
        // "me" would have nobody to send to, and would fail silently every morning. So the address
        // is turned into a literal here or the routine is not created at all.
        var wantsEmail = delivery is "email" or "both";
        if (wantsEmail && string.IsNullOrWhiteSpace(recipient))
        {
            var (resolved, failure) = await ResolveRequesterEmailAsync();
            if (resolved == null)
            {
                return $"Cannot set up an email routine: {failure} "
                     + "Tell the user you could not work out which address to send to, and ask them "
                     + "to give it explicitly. Do NOT create the routine.";
            }

            recipient = resolved;
        }

        var storedInstruction = BuildStoredInstruction(instruction, delivery, recipient);

        var body = new JsonObject
        {
            ["description"] = description,
            ["enabled"] = true,
            ["triggers"] = new JsonObject
            {
                [name] = new JsonObject
                {
                    ["type"] = "schedule",
                    ["cron_expression"] = cron,
                    ["time_zone"] = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone,
                },
            },
            ["action"] = new JsonObject
            {
                ["type"] = "invoke_agent_activityprotocol_api",
                ["agent_name"] = _agentName,
                // The conversation reference is kept even for email-only delivery: it is how the
                // scheduled run reaches this agent at all, which is separate from where the output
                // is sent.
                ["input"] = BuildActivityInput(storedInstruction),
            },
        };

        if (body["action"]!["input"] == null)
        {
            return "Cannot create a routine here: this conversation is missing the routing details "
                 + "(conversation id or service URL) a scheduled run needs to reach the agent.";
        }

        var (ok, response, error) = await SendAsync(HttpMethod.Put, $"routines/{Uri.EscapeDataString(name)}", body);

        // A routine's trigger is immutable: the API rejects a PUT that changes the schedule of an
        // existing routine with "Routine trigger cannot be changed after creation. Delete and
        // recreate...". That is exactly what "move my morning email to 8am" looks like, so handle
        // it here rather than surfacing a platform error the user cannot act on. Delete and
        // recreate once; the routine keeps its name and the user sees a normal reschedule.
        if (!ok && error != null && error.Contains("trigger cannot be changed", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Routine {Name} exists with a different schedule; deleting and recreating to change the trigger.",
                name);

            var (deleted, _, deleteError) = await SendAsync(HttpMethod.Delete, $"routines/{Uri.EscapeDataString(name)}", null);
            if (!deleted)
            {
                return $"Could not reschedule '{name}': its trigger cannot be changed in place and "
                     + $"removing the old one failed ({deleteError}).";
            }

            (ok, response, error) = await SendAsync(HttpMethod.Put, $"routines/{Uri.EscapeDataString(name)}", body);
        }

        if (!ok)
        {
            _logger.LogError("Routine create failed for {Name}: {Error}", name, error);
            return $"Could not create the routine: {error}";
        }

        _logger.LogInformation(
            "Routine created: {Name} cron='{Cron}' tz={TimeZone} delivery={Delivery} recipient={Recipient} conversation={ConversationId}",
            name,
            cron,
            timeZone,
            delivery,
            string.IsNullOrWhiteSpace(recipient) ? "(chat)" : recipient,
            _currentActivity.Conversation?.Id);

        var where = delivery switch
        {
            "email" => $"emails it to {recipient}",
            "both" => $"posts here and emails it to {recipient}",
            _ => "posts into this conversation",
        };

        return $"Routine '{name}' created and enabled. It runs on cron '{cron}' ({timeZone}) and {where}. "
             + "Tell the user in one short line what was scheduled, when it next runs, and — if it emails — "
             + "the exact address it will send to, so a wrong address is caught now rather than after a week "
             + $"of silence. Raw response: {Truncate(response, 300)}";
    }

    /// <summary>
    /// Wraps the user's instruction with an explicit delivery directive for the scheduled run.
    ///
    /// The future run is a fresh turn with no memory of this conversation, so everything it needs
    /// has to be in the text: where the output goes, and to whom. "Email me" cannot survive that
    /// boundary; "email amanda@example.com" can.
    /// </summary>
    private static string BuildStoredInstruction(string instruction, string delivery, string? recipient)
    {
        return delivery switch
        {
            "email" =>
                $"{instruction}\n\n"
                + $"Deliver this by email to {recipient} using your mail tools. Write a clear subject line. "
                + "Do not post the content in this chat — email is the only delivery for this routine. "
                + "If there is nothing to report, send nothing at all rather than an empty email.",

            "both" =>
                $"{instruction}\n\n"
                + $"Post this in the chat AND email the same content to {recipient} using your mail tools. "
                + "If there is nothing to report, do neither.",

            _ =>
                $"{instruction}\n\n"
                + "Post this in the chat. If there is nothing to report, post nothing.",
        };
    }

    /// <summary>
    /// Resolves the person who asked for the routine to a real mailbox, via their directory object
    /// id on the inbound activity.
    ///
    /// Returns (null, reason) rather than throwing or guessing: a wrong address is invisible until
    /// someone notices the mail never arrived, so the caller refuses to create the routine instead.
    /// A display name is never used to infer an address.
    /// </summary>
    private async Task<(string? Address, string? Failure)> ResolveRequesterEmailAsync()
    {
        var aadObjectId = _currentActivity?.From?.AadObjectId;
        if (string.IsNullOrWhiteSpace(aadObjectId))
        {
            return (null, "this message carries no directory id for the sender, so their mailbox cannot be looked up.");
        }

        if (string.IsNullOrWhiteSpace(_graphAccessToken))
        {
            return (null, "no Microsoft Graph token is available to look up the sender's mailbox.");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(aadObjectId)}?$select=mail,userPrincipalName,displayName");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);

            using var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Graph lookup for routine recipient {ObjectId} failed: HTTP {Status} {Body}",
                    aadObjectId,
                    (int)response.StatusCode,
                    Truncate(text, 200));
                return (null, $"the directory lookup failed (HTTP {(int)response.StatusCode}).");
            }

            var node = JsonNode.Parse(text);
            // Prefer mail; fall back to UPN, which is a routable address in most tenants but not
            // guaranteed to be a mailbox — worth preferring mail when both exist.
            var address = node?["mail"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(address))
            {
                address = node?["userPrincipalName"]?.GetValue<string>();
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                return (null, "the sender has no mail address or UPN in the directory.");
            }

            _logger.LogInformation(
                "Resolved routine recipient {ObjectId} -> {Address}",
                aadObjectId,
                address);

            return (address, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve routine recipient {ObjectId}.", aadObjectId);
            return (null, "the directory lookup threw an error.");
        }
    }

    private async Task<string> ListRoutinesAsync()
    {
        var (ok, response, error) = await SendAsync(HttpMethod.Get, "routines", null);
        if (!ok)
        {
            return $"Could not list routines: {error}";
        }

        var data = JsonNode.Parse(response ?? "{}")?["data"] as JsonArray;
        if (data == null || data.Count == 0)
        {
            return "No routines are set up on this agent.";
        }

        return data.ToJsonString();
    }

    private async Task<string> SetEnabledAsync(string name, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "name is required.";
        }

        // Read-modify-write: the routine's trigger and action must be preserved, and a PUT that
        // omitted them would replace the routine with one that has no schedule and no target.
        var (readOk, current, readErr) = await SendAsync(HttpMethod.Get, $"routines/{Uri.EscapeDataString(name)}", null);
        if (!readOk)
        {
            return $"Could not find routine '{name}': {readErr}";
        }

        var routine = JsonNode.Parse(current ?? "{}") as JsonObject;
        if (routine == null)
        {
            return $"Routine '{name}' returned an unreadable definition.";
        }

        routine["enabled"] = enabled;
        routine.Remove("name");
        routine.Remove("object");
        routine.Remove("created_at");

        var (ok, _, error) = await SendAsync(HttpMethod.Put, $"routines/{Uri.EscapeDataString(name)}", routine);
        return ok
            ? $"Routine '{name}' is now {(enabled ? "enabled" : "paused")}."
            : $"Could not update routine '{name}': {error}";
    }

    private async Task<string> DeleteRoutineAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "name is required.";
        }

        var (ok, _, error) = await SendAsync(HttpMethod.Delete, $"routines/{Uri.EscapeDataString(name)}", null);
        return ok ? $"Routine '{name}' deleted." : $"Could not delete routine '{name}': {error}";
    }

    /// <summary>
    /// Builds the activity the scheduled run will deliver to this agent. This is the conversation
    /// reference: it tells the routine which chat to post into and as whom.
    ///
    /// Returns null when the turn lacks the ids a delivery needs, so the caller can say so plainly
    /// rather than creating a routine that fires into nowhere.
    /// </summary>
    private JsonObject? BuildActivityInput(string instruction)
    {
        var activity = _currentActivity!;
        var conversationId = activity.Conversation?.Id;
        var serviceUrl = activity.ServiceUrl;

        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(serviceUrl))
        {
            return null;
        }

        var from = new JsonObject { ["id"] = activity.From?.Id };
        if (!string.IsNullOrWhiteSpace(activity.From?.AadObjectId))
        {
            from["aadObjectId"] = activity.From!.AadObjectId;
        }

        var conversation = new JsonObject { ["id"] = conversationId };
        var tenantId = activity.Conversation?.TenantId;
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            conversation["tenantId"] = tenantId;
        }

        var recipient = new JsonObject { ["id"] = activity.Recipient?.Id };

        // agenticAppId / agenticAppBlueprintId identify which agent version answers the scheduled
        // run. The platform stamps them on inbound activities; fall back to configuration so a
        // routine can still be created if the inbound activity omits them.
        var agenticAppId = ReadRecipientProperty(activity, "agenticAppId")
            ?? _configuration["AgenticAppId"]
            ?? Environment.GetEnvironmentVariable("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID");
        var blueprintId = ReadRecipientProperty(activity, "agenticAppBlueprintId")
            ?? _configuration["AgenticAppBlueprintId"]
            ?? Environment.GetEnvironmentVariable("FOUNDRY_AGENT_BLUEPRINT_CLIENT_ID");

        if (!string.IsNullOrWhiteSpace(agenticAppId))
        {
            recipient["agenticAppId"] = agenticAppId;
        }
        if (!string.IsNullOrWhiteSpace(blueprintId))
        {
            recipient["agenticAppBlueprintId"] = blueprintId;
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["channelId"] = activity.ChannelId?.ToString() ?? "msteams",
            ["serviceUrl"] = serviceUrl,
            ["from"] = from,
            ["conversation"] = conversation,
            ["recipient"] = recipient,
            ["text"] = instruction,
        };
    }

    /// <summary>
    /// Reads a non-standard property off the activity's recipient. The SDK's ChannelAccount does
    /// not model agenticAppId, so it arrives in the extension-data bag.
    /// </summary>
    private static string? ReadRecipientProperty(IActivity activity, string property)
    {
        try
        {
            var json = JsonSerializer.Serialize(activity.Recipient);
            var value = JsonNode.Parse(json)?[property]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Ok, string? Response, string? Error)> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body)
    {
        try
        {
            var token = await _credential!.GetTokenAsync(
                new TokenRequestContext(["https://ai.azure.com/.default"]),
                CancellationToken.None);

            var url = $"{_projectEndpoint!.TrimEnd('/')}/{path}?api-version={ApiVersion}";
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Headers.TryAddWithoutValidation("Foundry-Features", FeaturesHeader);

            if (body != null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"HTTP {(int)response.StatusCode} {Truncate(text, 300)}");
            }

            return (true, text, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Routine API call failed: {Method} {Path}", method, path);
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Routine names travel in the URL path, so restrict them to a safe shape rather than letting
    /// a model-chosen name produce a malformed request.
    /// </summary>
    private static string Slugify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var chars = raw.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    private static string GetString(JsonNode? node, string name) =>
        node?[name]?.GetValue<string>() ?? string.Empty;

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max] + "...";
}
