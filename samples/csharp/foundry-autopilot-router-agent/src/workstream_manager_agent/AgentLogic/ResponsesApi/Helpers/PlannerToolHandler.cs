namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using WorkstreamManager.Models;

/// <summary>
/// Posts the team's work onto a Microsoft Planner board.
///
/// Why Graph directly and not an MCP server: there is no Agent 365 Planner tooling server.
/// The blueprint's granted MCP scopes cover Mail, Teams, Calendar, Word, Excel,
/// OneDrive/SharePoint and others, and none of them is Planner. Checked against the Agent 365
/// tooling servers list as well, which does not mention Planner at all. So this handler does
/// what MeetingRegistryToolHandler does: calls Graph itself on the agent user's delegated
/// token.
///
/// Identity note. These tasks are created BY THE AGENT USER, so they appear on the board as
/// having been added by the autopilot, not by the person who asked. That is the same model as
/// the meeting registry: the autopilot is a colleague with its own account, not a proxy for
/// the manager. For that to work the agent user must be a member of the group that owns the
/// plan — Planner authorises on group membership, not on a tenant-wide role.
///
/// Endpoint shape, from the Graph reference: task creation is a TENANT-level
/// POST /planner/tasks with planId in the body. It is NOT /me/planner/tasks. Only the
/// plan LOOKUP is user-scoped (GET /me/planner/plans). bucketId is optional, so a task can be
/// created on a plan that has no buckets yet, which a freshly created board does not.
/// </summary>
public class PlannerToolHandler
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string? _graphAccessToken;
    private readonly Guid _agentUserId;

    // Resolved once per turn. A board is looked up by title so the model never has to know a
    // GUID, and so the configured default keeps working if the plan is recreated.
    private readonly Dictionary<string, string> _planIdByTitle = new(StringComparer.OrdinalIgnoreCase);

    // The group owning a plan decides who can actually open the board, which is what makes an
    // assignment reach its owner rather than just look like it did.
    private readonly Dictionary<string, string> _groupIdByPlanId = new(StringComparer.OrdinalIgnoreCase);

    // Process-local, and deliberately STATIC: a handler is constructed per turn, so an instance
    // field would reset on every message and re-log the same missing-permission warning forever.
    // Same process-local pattern the activity dedupe in A365AgentApplication uses.
    private static bool _assigneeAccessCheckUnavailable;

    private IActivity? _currentActivity;

    public PlannerToolHandler(
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
    /// Off unless a default board is configured AND a Graph token exists.
    ///
    /// Requiring the board name is deliberate. Without it the agent would advertise Planner
    /// tools, then discover at call time that it cannot see any plan, and tell the user it
    /// cannot do the thing it just offered. Silence is better than an offer it cannot keep.
    /// </summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_graphAccessToken)
        && _agentUserId != Guid.Empty
        && !string.IsNullOrWhiteSpace(DefaultBoardName);

    private string? DefaultBoardName => _configuration["PlannerDefaultBoard"];

    /// <summary>
    /// Captures the turn's activity so "assign it to me" can resolve to the person speaking.
    /// Call this at the start of each turn, before invoking the Responses API.
    /// </summary>
    public void SetCurrentActivityContext(IActivity? activity) => _currentActivity = activity;

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
                "name": "create_planner_task",
                "description": "Adds a task to the team's Planner board so the work is visible to everyone, not just in this chat. Use it when the user asks to put something on the board, or to turn a confirmed action into a tracked task. Creates the task as YOU, the autopilot, so it is clear who added it.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "title": { "type": "string", "description": "The task title. Short and specific, e.g. 'Retest saved-card pagination after the fix'. Never a whole sentence of context." },
                        "notes": { "type": "string", "description": "Optional detail: why it exists, and where it came from (the meeting, the work item id, the channel message). Include the evidence so someone reading the board later can check it." },
                        "due_date": { "type": "string", "description": "Optional ISO 8601 date, e.g. 2026-09-24." },
                        "owner": { "type": "string", "description": "Optional. Who the task belongs to: display name, email or UPN, e.g. 'Sustineo Juarez'. Pass \"me\" if the person speaking is taking it on themselves. This assigns the card to them on the board so it appears in their Planner. Pass the name the user gave you; never guess one, and never put the owner in notes instead of here." },
                        "board": { "type": "string", "description": "Optional board title. Leave empty to use the team's default board." }
                    },
                    "required": ["title"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "list_planner_tasks",
                "description": "Lists what is currently on the team's Planner board, with completion state and due dates. Use it when asked what is on the board, or before adding a task, to avoid creating a duplicate of something already there.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "board": { "type": "string", "description": "Optional board title. Leave empty to use the team's default board." },
                        "include_completed": { "type": "boolean", "description": "Include tasks that are already 100% complete. Defaults to false." }
                    },
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "complete_planner_task",
                "description": "Marks a task on the Planner board as complete. Only call this when the user has clearly said the work is done; do not infer completion from a status update.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "title": { "type": "string", "description": "All or part of the task title as returned by list_planner_tasks." },
                        "board": { "type": "string", "description": "Optional board title. Leave empty to use the team's default board." }
                    },
                    "required": ["title"],
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

        if (toolName is not ("create_planner_task" or "list_planner_tasks" or "complete_planner_task"))
        {
            return null;
        }

        JsonNode? args = null;

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            try
            {
                args = JsonNode.Parse(arguments);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse arguments for {Tool}", toolName);
                return $"The arguments for {toolName} were not valid JSON.";
            }
        }

        try
        {
            return toolName switch
            {
                "create_planner_task" => await CreateTaskAsync(args),
                "list_planner_tasks" => await ListTasksAsync(args),
                "complete_planner_task" => await CompleteTaskAsync(args),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Planner tool {Tool} threw.", toolName);
            return $"The Planner call failed: {ex.Message}. Tell the user plainly; do not pretend the task was created.";
        }
    }

    private async Task<string> CreateTaskAsync(JsonNode? args)
    {
        var title = GetString(args, "title");

        if (string.IsNullOrWhiteSpace(title))
        {
            return "A task needs a title.";
        }

        var (planId, planError) = await ResolvePlanAsync(GetString(args, "board"));

        if (planId == null)
        {
            return planError!;
        }

        var body = new JsonObject
        {
            ["planId"] = planId,
            ["title"] = title,
        };

        var dueDate = GetString(args, "due_date");

        if (!string.IsNullOrWhiteSpace(dueDate) && DateTimeOffset.TryParse(dueDate, out var due))
        {
            body["dueDateTime"] = due.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        // The owner is resolved and attached BEFORE the task exists, so Planner either stores the
        // card with its owner or stores nothing at all. Creating first and patching the owner on
        // afterwards is what produces the worst outcome available here: a card sitting on the
        // board owned by nobody while the chat has already said who it was assigned to.
        var owner = GetString(args, "owner");
        var ownerRequested = !string.IsNullOrWhiteSpace(owner);
        string? ownerId = null;

        if (ownerRequested)
        {
            var (resolvedId, ownerError) = await ResolveUserIdAsync(owner!);

            if (resolvedId == null)
            {
                return ownerError!;
            }

            ownerId = resolvedId;

            // The annotation is "#microsoft.graph.plannerAssignment". Planner rejects any other
            // namespace with a 400 "untyped value ... is invalid. Consider using a OData type
            // annotation explicitly" — which reads like the annotation is missing rather than
            // wrong, and sends you looking in the wrong place. Verified against a live plan.
            body["assignments"] = new JsonObject
            {
                [ownerId] = new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.plannerAssignment",
                    ["orderHint"] = " !",
                },
            };
        }

        var (ok, response, error) = await SendGraphAsync(HttpMethod.Post, "planner/tasks", body);

        if (!ok)
        {
            // The assignment travels in this call, so a failure means no card was created at all.
            // Say so, or the model reports partial success and then retries without the owner.
            var ownerHint = ownerRequested
                ? $" Nothing was created, so '{owner}' has not been assigned anything. Report the failure; do NOT retry without the owner."
                : string.Empty;

            return $"Could not add '{title}' to the board: {error}{ownerHint}";
        }

        var taskId = JsonNode.Parse(response ?? "{}")?["id"]?.GetValue<string>();
        var notes = GetString(args, "notes");

        // Notes live on a separate entity (plannerTaskDetails) and need the task's ETag, so
        // this is a second round trip. Deliberately best-effort: the task existing matters more
        // than its notes, and failing the whole call because a description did not attach would
        // leave a task on the board while telling the user it failed.
        var notesNote = string.Empty;

        if (!string.IsNullOrWhiteSpace(notes) && !string.IsNullOrWhiteSpace(taskId))
        {
            if (!await TrySetNotesAsync(taskId!, notes!))
            {
                notesNote = " (the task was created, but the notes could not be attached)";
            }
        }

        _logger.LogInformation(
            "Planner task created: '{Title}' plan={PlanId} id={TaskId} owner={Owner}",
            title, planId, taskId, ownerRequested ? owner : "(unassigned)");

        var ownerNote = ownerRequested
            ? $" Assigned to {owner}.{await DescribeAssigneeAccessAsync(planId, ownerId!, owner!)}"
            : string.Empty;

        return $"Added '{title}' to the board.{ownerNote}{notesNote}";
    }

    /// <summary>
    /// Flags a card assigned to someone who cannot open the board it sits on.
    ///
    /// Planner accepts an assignment to a non-member without complaint — verified against a live
    /// plan — but group membership still gates who can see the board. The card is then assigned in
    /// every view the team has while never appearing in the assignee's Planner, which is the
    /// failure the board exists to prevent: it looks tracked and reaches nobody.
    ///
    /// NOTE: this needs delegated **GroupMember.Read.All**, which is NOT in the blueprint's Graph
    /// grant today (ChatMessage.Send, ChannelMessage.Send, ChatMember.Read, ChannelMessage.Read.All,
    /// User.Read.All, Tasks.ReadWrite). Without it the check cannot run and no warning is produced.
    /// Assignment itself is unaffected. The failure is logged at Warning rather than swallowed, so
    /// this shows up as a missing grant instead of a feature that quietly does nothing.
    ///
    /// Best-effort by design either way: the card already exists by this point, so a failed check
    /// must not turn a successful create into a reported error.
    /// </summary>
    private async Task<string> DescribeAssigneeAccessAsync(string planId, string userId, string ownerLabel)
    {
        if (_assigneeAccessCheckUnavailable)
        {
            return string.Empty;
        }

        var groupId = await ResolvePlanGroupIdAsync(planId);

        if (string.IsNullOrWhiteSpace(groupId))
        {
            return string.Empty;
        }

        var (ok, body, error) = await SendGraphAsync(
            HttpMethod.Post,
            $"users/{userId}/checkMemberGroups",
            new JsonObject { ["groupIds"] = new JsonArray(groupId!) });

        if (!ok)
        {
            // Almost always a missing grant rather than a transient fault, so stop retrying for
            // the life of the process: otherwise every assignment pays two Graph calls and logs
            // the same warning again, which buries the one line that explains the cause.
            _assigneeAccessCheckUnavailable = true;

            _logger.LogWarning(
                "Cannot check whether an assignee can see the board (group {GroupId}), so cards will "
                + "be assigned without confirming the owner can see them. This check is now DISABLED "
                + "for this process. It needs delegated GroupMember.Read.All, which is not in the "
                + "blueprint's Graph grant. Assignment itself is unaffected. Graph said: {Error}",
                groupId, error);

            return string.Empty;
        }

        var matched = JsonNode.Parse(body ?? "{}")?["value"] as JsonArray;

        if (matched != null && matched.Count > 0)
        {
            return string.Empty;
        }

        _logger.LogWarning(
            "Planner task assigned to {UserId}, who is not a member of owning group {GroupId}.",
            userId, groupId);

        return $" Note: {ownerLabel} is not in the group that owns this board, so the card is assigned"
            + " to them but will not show up in their Planner until they are added to that group."
            + " Tell the user this plainly — do not describe the work as visible to them.";
    }

    private async Task<string?> ResolvePlanGroupIdAsync(string planId)
    {
        if (_groupIdByPlanId.TryGetValue(planId, out var cached))
        {
            return cached;
        }

        var (ok, body, _) = await SendGraphAsync(HttpMethod.Get, $"planner/plans/{planId}");

        if (!ok)
        {
            return null;
        }

        var container = JsonNode.Parse(body ?? "{}")?["container"];
        var groupId = string.Equals(container?["type"]?.GetValue<string>(), "group", StringComparison.OrdinalIgnoreCase)
            ? container?["containerId"]?.GetValue<string>()
            : null;

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            _groupIdByPlanId[planId] = groupId!;
        }

        return groupId;
    }

    /// <summary>
    /// Finds the directory id for a person named in chat, so a card can be assigned to them.
    ///
    /// Refuses rather than guesses. Taking the first of several matches is the wrong trade here:
    /// the card looks correctly assigned on the board, so nobody checks it, and the mistake only
    /// surfaces when the wrong person is chased for work they never agreed to. An error the model
    /// can take back to the user costs one question; a silently misassigned card costs a week.
    ///
    /// The or-of-startswith shape across displayName/givenName/surname/mail/userPrincipalName is
    /// the documented v1.0 user lookup and needs no advanced query headers. Reads use
    /// User.Read.All, already in the blueprint's Graph grant, so this adds no new permission.
    /// </summary>
    private async Task<(string? UserId, string? Error)> ResolveUserIdAsync(string nameOrEmail)
    {
        var term = nameOrEmail.Trim();

        // "me" is the one case where the speaker can be inferred without guessing: they said it
        // about themselves. Anything less explicit stays a question — preferring the speaker for
        // an ambiguous name would reintroduce exactly the silent misassignment this method exists
        // to prevent.
        if (term.Equals("me", StringComparison.OrdinalIgnoreCase)
            || term.Equals("myself", StringComparison.OrdinalIgnoreCase)
            || term.Equals("i", StringComparison.OrdinalIgnoreCase))
        {
            var speakerId = _currentActivity?.From?.AadObjectId;

            return string.IsNullOrWhiteSpace(speakerId)
                ? (null, "I could not work out who \"me\" refers to, so I did not create the card. "
                    + "Ask the user for their name or email address and use that as the owner.")
                : (speakerId, null);
        }

        var escaped = term.Replace("'", "''");

        var filter = Uri.EscapeDataString(
            $"startswith(displayName,'{escaped}') or startswith(givenName,'{escaped}') "
            + $"or startswith(surname,'{escaped}') or startswith(mail,'{escaped}') "
            + $"or startswith(userPrincipalName,'{escaped}')");

        var (ok, body, error) = await SendGraphAsync(
            HttpMethod.Get,
            $"users?$filter={filter}&$select=id,displayName,userPrincipalName,mail&$top=5");

        if (!ok)
        {
            return (null, $"Could not look up '{nameOrEmail}' in the directory: {error} Nothing was created.");
        }

        var users = JsonNode.Parse(body ?? "{}")?["value"] as JsonArray;

        if (users == null || users.Count == 0)
        {
            return (null,
                $"There is nobody in the directory matching '{nameOrEmail}', so I did not create the card. "
                + "Ask the user for the person's full name or email address. Do NOT create it unassigned "
                + "and do NOT record the owner in the notes instead — neither puts the work in their queue.");
        }

        if (users.Count > 1)
        {
            // startswith means a full, correct name can still collide with a longer one
            // ("Amanda Foster" against an "Amanda Fosterman"). An exact hit on the name, mail or
            // UPN is the user being precise, so honour it instead of asking a question they have
            // already answered. A partial name like "Amanda" has no exact match and still refuses.
            var exact = users
                .Where(u => Matches(u, "displayName", term)
                    || Matches(u, "userPrincipalName", term)
                    || Matches(u, "mail", term))
                .ToList();

            if (exact.Count == 1)
            {
                var exactId = exact[0]?["id"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(exactId))
                {
                    return (exactId, null);
                }
            }

            var names = string.Join(", ", users.Select(u =>
                $"'{u?["displayName"]?.GetValue<string>()}' ({u?["userPrincipalName"]?.GetValue<string>()})"));

            return (null,
                $"'{nameOrEmail}' matches several people ({names}). Nothing was created. "
                + "Ask which one is meant, then create the card with their email address as the owner.");
        }

        var id = users[0]?["id"]?.GetValue<string>();

        return string.IsNullOrWhiteSpace(id)
            ? (null, $"The directory entry for '{nameOrEmail}' has no id, so I could not assign the card.")
            : (id, null);
    }

    private async Task<string> ListTasksAsync(JsonNode? args)
    {
        var (planId, planError) = await ResolvePlanAsync(GetString(args, "board"));

        if (planId == null)
        {
            return planError!;
        }

        var includeCompleted = args?["include_completed"]?.GetValue<bool>() ?? false;
        var (ok, response, error) = await SendGraphAsync(HttpMethod.Get, $"planner/plans/{planId}/tasks");

        if (!ok)
        {
            return $"Could not read the board: {error}";
        }

        var tasks = JsonNode.Parse(response ?? "{}")?["value"] as JsonArray;

        if (tasks == null || tasks.Count == 0)
        {
            return "The board is empty.";
        }

        var lines = new List<string>();
        var nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tasks)
        {
            var percent = t?["percentComplete"]?.GetValue<int>() ?? 0;

            if (percent >= 100 && !includeCompleted)
            {
                continue;
            }

            var titleText = t?["title"]?.GetValue<string>() ?? "(untitled)";
            var due = t?["dueDateTime"]?.GetValue<string>();
            var state = percent >= 100 ? "done" : percent > 0 ? "in progress" : "not started";
            var dueText = string.IsNullOrWhiteSpace(due) ? string.Empty : $" | due {due[..Math.Min(10, due.Length)]}";
            var ownerText = await DescribeAssigneesAsync(t?["assignments"] as JsonObject, nameCache);
            lines.Add($"- {titleText} | {state}{dueText}{ownerText}");
        }

        if (lines.Count == 0)
        {
            return "Nothing open on the board; everything on it is complete.";
        }

        return $"{lines.Count} open task(s) on the board:\n{string.Join("\n", lines)}";
    }

    /// <summary>
    /// Renders a task's assignees as "owner: Name (directory-id)".
    ///
    /// The id is included deliberately. Naming an owner in text is not enough to act on them: a
    /// digest that genuinely pings people needs the directory id to pass to the mentions argument
    /// of SendMessageToChat. Without it the model can only write the name as plain text, which
    /// looks like a mention in the transcript and notifies nobody — the failure is invisible
    /// exactly where it matters.
    /// </summary>
    private async Task<string> DescribeAssigneesAsync(JsonObject? assignments, Dictionary<string, string> nameCache)
    {
        if (assignments == null || assignments.Count == 0)
        {
            return " | owner: unassigned";
        }

        var owners = new List<string>();

        foreach (var assignment in assignments)
        {
            var id = assignment.Key;

            if (!nameCache.TryGetValue(id, out var name))
            {
                name = await ResolveUserDisplayNameAsync(id) ?? id;
                nameCache[id] = name;
            }

            owners.Add($"{name} ({id})");
        }

        return $" | owner: {string.Join(", ", owners)}";
    }

    private async Task<string?> ResolveUserDisplayNameAsync(string userId)
    {
        var (ok, body, _) = await SendGraphAsync(HttpMethod.Get, $"users/{userId}?$select=displayName");

        return ok
            ? JsonNode.Parse(body ?? "{}")?["displayName"]?.GetValue<string>()
            : null;
    }

    private async Task<string> CompleteTaskAsync(JsonNode? args)
    {
        var title = GetString(args, "title");

        if (string.IsNullOrWhiteSpace(title))
        {
            return "Which task? Give the title as it appears on the board.";
        }

        var (planId, planError) = await ResolvePlanAsync(GetString(args, "board"));

        if (planId == null)
        {
            return planError!;
        }

        var (ok, response, error) = await SendGraphAsync(HttpMethod.Get, $"planner/plans/{planId}/tasks");

        if (!ok)
        {
            return $"Could not read the board: {error}";
        }

        var tasks = JsonNode.Parse(response ?? "{}")?["value"] as JsonArray;
        var matches = tasks?
            .Where(t => (t?["title"]?.GetValue<string>() ?? string.Empty)
                .Contains(title, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        if (matches.Count == 0)
        {
            return $"Nothing on the board matches '{title}'. List the board and ask which one they mean.";
        }

        if (matches.Count > 1)
        {
            var names = string.Join(", ", matches.Select(m => $"'{m?["title"]?.GetValue<string>()}'"));
            return $"'{title}' matches several tasks ({names}). Ask which one before completing anything.";
        }

        var match = matches[0]!;
        var taskId = match["id"]?.GetValue<string>();
        var etag = match["@odata.etag"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(etag))
        {
            return "That task is missing the id or etag Planner needs for an update.";
        }

        var (patchOk, _, patchError) = await SendGraphAsync(
            HttpMethod.Patch,
            $"planner/tasks/{taskId}",
            new JsonObject { ["percentComplete"] = 100 },
            etag);

        return patchOk
            ? $"Marked '{match["title"]?.GetValue<string>()}' complete on the board."
            : $"Could not complete that task: {patchError}";
    }

    private async Task<bool> TrySetNotesAsync(string taskId, string notes)
    {
        var (ok, response, _) = await SendGraphAsync(HttpMethod.Get, $"planner/tasks/{taskId}/details");

        if (!ok)
        {
            return false;
        }

        var etag = JsonNode.Parse(response ?? "{}")?["@odata.etag"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var (patchOk, _, _) = await SendGraphAsync(
            HttpMethod.Patch,
            $"planner/tasks/{taskId}/details",
            new JsonObject { ["description"] = notes },
            etag);

        return patchOk;
    }

    /// <summary>
    /// Finds the plan id for a board title.
    ///
    /// Plan lookup must not use /me. The container authenticates as the agent identity, which
    /// has no signed-in user, so GET /me/planner/plans returns 403 "You do not have the required
    /// permissions" — an error that reads like a group-membership problem and sends the reader
    /// off to fix the wrong thing. Prefer the configured group, then the agent user by id.
    ///
    /// An empty plan list still means the agent user is not a member of the group owning the
    /// board: Planner authorises on group membership, not on a tenant-wide role.
    /// </summary>
    private async Task<(string? PlanId, string? Error)> ResolvePlanAsync(string? requestedBoard)
    {
        var boardName = string.IsNullOrWhiteSpace(requestedBoard) ? DefaultBoardName : requestedBoard;

        if (string.IsNullOrWhiteSpace(boardName))
        {
            return (null, "No Planner board is configured, so there is nowhere to put this.");
        }

        if (_planIdByTitle.TryGetValue(boardName, out var cached))
        {
            return (cached, null);
        }

        var candidatePaths = new List<string>();

        var groupId = _configuration["PlannerGroupId"];
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            candidatePaths.Add($"groups/{groupId}/planner/plans");
        }

        if (_agentUserId != Guid.Empty)
        {
            candidatePaths.Add($"users/{_agentUserId}/planner/plans");
        }

        candidatePaths.Add("me/planner/plans");

        string? response = null;
        string? lastError = null;

        foreach (var path in candidatePaths)
        {
            var (ok, body, error) = await SendGraphAsync(HttpMethod.Get, path);

            if (ok)
            {
                response = body;
                break;
            }

            lastError = error;
            _logger.LogWarning("Planner plan lookup via {Path} failed: {Error}", path, error);
        }

        if (response == null)
        {
            return (null, $"Could not list Planner boards: {lastError}");
        }

        var plans = JsonNode.Parse(response ?? "{}")?["value"] as JsonArray;

        if (plans == null || plans.Count == 0)
        {
            return (null,
                "I cannot see any Planner board. I am only shown boards owned by groups I belong to, "
                + "so tell the user I need to be added to the team that owns the board.");
        }

        foreach (var p in plans)
        {
            var t = p?["title"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(t))
            {
                _planIdByTitle[t!] = p!["id"]!.GetValue<string>();
            }
        }

        if (_planIdByTitle.TryGetValue(boardName, out var found))
        {
            return (found, null);
        }

        var available = string.Join(", ", _planIdByTitle.Keys.Select(k => $"'{k}'"));
        return (null, $"There is no board called '{boardName}'. The boards I can see are: {available}.");
    }

    private async Task<(bool Ok, string? Response, string? Error)> SendGraphAsync(
        HttpMethod method,
        string path,
        JsonObject? body = null,
        string? ifMatch = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);

            // Planner requires optimistic concurrency on every update: a PATCH without the
            // entity's current ETag is rejected outright rather than overwriting.
            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
            }

            if (body != null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Graph {Method} {Path} failed: {Status} {Body}",
                    method, path, (int)response.StatusCode, Truncate(text, 300));

                // Planner's 401/403 body says only "you do not have the required permissions",
                // which reads like a group-membership problem. It is not: it means our own
                // token is missing Tasks.ReadWrite. Say so, or the model invents a fix.
                var hint = (int)response.StatusCode is 401 or 403
                    ? " This is a missing permission on my own access token (Tasks.ReadWrite), not group membership. Do not suggest adding me to the group; report that my Planner permission needs granting."
                    : string.Empty;

                return (false, null, $"Graph returned {(int)response.StatusCode}. {Truncate(text, 200)}{hint}");
            }

            return (true, text, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graph {Method} {Path} threw.", method, path);
            return (false, null, ex.Message);
        }
    }

    private static string? GetString(JsonNode? args, string name)
    {
        var value = args?[name];
        return value == null ? null : value.GetValue<string>();
    }

    private static bool Matches(JsonNode? user, string property, string term) =>
        string.Equals(user?[property]?.GetValue<string>(), term, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
