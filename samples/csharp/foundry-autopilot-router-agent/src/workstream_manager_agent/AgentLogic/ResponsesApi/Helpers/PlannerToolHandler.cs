namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
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

        var (ok, response, error) = await SendGraphAsync(HttpMethod.Post, "planner/tasks", body);

        if (!ok)
        {
            return $"Could not add '{title}' to the board: {error}";
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

        _logger.LogInformation("Planner task created: '{Title}' plan={PlanId} id={TaskId}", title, planId, taskId);
        return $"Added '{title}' to the board.{notesNote}";
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
            lines.Add($"- {titleText} | {state}{dueText}");
        }

        if (lines.Count == 0)
        {
            return "Nothing open on the board; everything on it is complete.";
        }

        return $"{lines.Count} open task(s) on the board:\n{string.Join("\n", lines)}";
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
    /// Plans are listed user-scoped (/me/planner/plans) because that returns the boards this
    /// account can actually act on. An empty list is the signal that the agent user is not a
    /// member of the group owning the board — Planner authorises on group membership, so the
    /// fix is to add the agent user to that group, not to grant a wider permission.
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

        var (ok, response, error) = await SendGraphAsync(HttpMethod.Get, "me/planner/plans");

        if (!ok)
        {
            return (null, $"Could not list Planner boards: {error}");
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

                return (false, null, $"Graph returned {(int)response.StatusCode}. {Truncate(text, 200)}");
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

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
