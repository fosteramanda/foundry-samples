using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.Core.Models;

namespace WorkstreamManager.Services;

internal sealed record PlannerCardTask(string Title, string Details, string? Id, string? Url, string Notes = "");
internal sealed record PlannerCardResult(
    string Title, IReadOnlyList<PlannerCardTask> Tasks, bool IsPreview, bool CanApprove,
    string? PlanId, string? PlanUrl, string PreviewJson, string PlanDetails = "");

internal static class DelegationCardPresentation
{
    internal const string ActionKind = "office.delegation.v1";
    internal const int CardByteBudget = 24 * 1024;
    private static readonly Regex PlannerTag = new(
        @"<m-planner-task-list\b(?<attributes>.*?)>\s*</m-planner-task-list>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
    private static readonly Regex PlannerAttribute = new(
        @"\b(?<name>plan|tasks)\s*=\s*(?<quote>['""])(?<json>\{.*?\}|\[.*?\])\k<quote>(?=\s|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
    private static readonly Regex EncodedPlannerTag = new(
        @"&lt;m-planner-task-list\b.*?&gt;\s*&lt;/m-planner-task-list&gt;",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
    private static readonly Regex PlanPath = new(
        @"^/webui/plan/(?<plan>[A-Za-z0-9_-]{1,128})/view/board(?:/task/(?<task>[A-Za-z0-9_-]{1,128}))?/?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static PlannerCardResult? ReadPlanner(string answer)
    {
        if (answer.Length > 30_000) return null;
        var decoded = WebUtility.HtmlDecode(answer);
        var matches = PlannerTag.Matches(decoded);
        if (matches.Count == 0) return null;
        if (matches.Count != 1) throw new FormatException("More than one Planner preview needs separate review.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in PlannerAttribute.Matches(matches[0].Groups["attributes"].Value))
            if (!values.TryAdd(attribute.Groups["name"].Value, attribute.Groups["json"].Value))
                throw new FormatException("The Planner preview repeats an attribute.");
        if (!values.TryGetValue("plan", out var planText) || !values.TryGetValue("tasks", out var tasksText))
            throw new FormatException("The Planner preview is incomplete.");
        var plan = JsonNode.Parse(planText) as JsonObject ?? throw new FormatException("The Planner plan is not an object.");
        var tasks = JsonNode.Parse(tasksText) as JsonArray ?? throw new FormatException("The Planner task list is not an array.");
        var title = Text(plan, "title", true);
        var link = Text(plan, "link");
        var isPreview = Text(plan, "draftMode") == "newPlan" && string.IsNullOrEmpty(link);
        string? planId = null;
        if (!string.IsNullOrEmpty(link))
        {
            (planId, var taskId) = ReadPlannerUrl(link);
            if (taskId != null) throw new FormatException("The plan link points to a task.");
        }
        var knownPlan = new HashSet<string>(["draftMode", "title", "taskListDescription", "link", "buckets", "goals"], StringComparer.Ordinal);
        var knownTask = new HashSet<string>(["title", "percentComplete", "dueDateTime", "startDateTime",
            "dueDate", "startDate", "notes", "description", "assignments", "priority", "link",
            "bucketName", "goalName"], StringComparer.Ordinal);
        var canApprove = isPreview && tasks.Count is > 0 and <= 25 && plan.All(field => knownPlan.Contains(field.Key));
        var planDetails = new List<string>();
        var description = Text(plan, "taskListDescription");
        if (!string.IsNullOrWhiteSpace(description)) planDetails.Add(description);
        var buckets = new HashSet<string>(StringComparer.Ordinal);
        var goals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in new[] { "buckets", "goals" })
        {
            if (plan[collection] == null) continue;
            if (plan[collection] is not JsonArray items)
            {
                canApprove = false;
                continue;
            }
            foreach (var item in items)
            {
                string name;
                if (collection == "buckets")
                {
                    if (item is not JsonValue bucket || !bucket.TryGetValue<string>(out var bucketName)
                        || string.IsNullOrWhiteSpace(bucketName) || bucketName.Length > 2000)
                        throw new FormatException("A Planner task group is not a valid name.");
                    name = bucketName;
                    canApprove &= buckets.Add(name);
                    planDetails.Add("Task group: " + name);
                }
                else
                {
                    var goal = item as JsonObject ?? throw new FormatException("A Planner goal is not an object.");
                    name = Text(goal, "name", true);
                    var status = Text(goal, "status");
                    canApprove &= goals.Add(name) && goal.All(field => field.Key is "name" or "status");
                    var readableStatus = status switch
                    {
                        "notStarted" => "Not started",
                        "inProgress" => "In progress",
                        "completed" => "Complete",
                        _ => status
                    };
                    planDetails.Add("Goal: " + name + (string.IsNullOrEmpty(status) ? "" : " (Status: " + readableStatus + ")"));
                }
            }
        }
        var rows = new List<PlannerCardTask>();
        foreach (var value in tasks)
        {
            var task = value as JsonObject ?? throw new FormatException("A Planner task is not an object.");
            var taskTitle = Text(task, "title", true);
            var taskLink = Text(task, "link");
            string? taskId = null;
            if (!string.IsNullOrEmpty(taskLink))
            {
                var ids = ReadPlannerUrl(taskLink);
                if (ids.Plan != planId || ids.Task == null)
                    throw new FormatException("A task link does not belong to the returned plan.");
                taskId = ids.Task;
                canApprove = false;
            }
            canApprove &= task.All(field => knownTask.Contains(field.Key));
            var bucketName = Text(task, "bucketName");
            var goalName = Text(task, "goalName");
            canApprove &= (string.IsNullOrEmpty(bucketName) || buckets.Contains(bucketName))
                && (string.IsNullOrEmpty(goalName) || goals.Contains(goalName));
            var details = new List<string>();
            var notes = new List<string>();
            foreach (var field in task.Where(field => field.Key is not ("title" or "link" or "planName")))
            {
                var valueText = field.Value is JsonValue scalar && scalar.TryGetValue<string>(out var plain)
                    ? plain : field.Value?.ToJsonString() ?? "not set";
                if (field.Key is "notes" or "description")
                {
                    if (field.Value is not JsonValue note || !note.TryGetValue<string>(out var noteText))
                        throw new FormatException("The Planner task notes are not text.");
                    notes.Add(noteText);
                    continue;
                }
                details.Add(field.Key switch
                {
                    "percentComplete" => valueText == "0" ? "Not started" : valueText == "100" ? "Complete" : valueText + "% complete",
                    "priority" => "Priority: " + valueText,
                    "dueDateTime" or "dueDate" => field.Value == null ? "No due date" : "Due: " + valueText,
                    "startDateTime" or "startDate" => field.Value == null ? "No start date" : "Start: " + valueText,
                    "assignments" => field.Value is JsonObject assignments && assignments.Count == 0
                        ? "Unassigned" : "Assignments: " + valueText,
                    "bucketName" => string.IsNullOrEmpty(bucketName) ? "No task group" : "Task group: " + bucketName,
                    "goalName" => string.IsNullOrEmpty(goalName) ? "No goal" : "Goal: " + goalName,
                    _ => field.Key + ": " + valueText
                });
            }
            if (isPreview && !task.ContainsKey("assignments")) details.Add("Unassigned");
            if (isPreview && !task.ContainsKey("dueDateTime") && !task.ContainsKey("dueDate")) details.Add("No due date");
            rows.Add(new PlannerCardTask(taskTitle, string.Join("; ", details), taskId,
                string.IsNullOrWhiteSpace(taskLink) ? null : taskLink, string.Join("\n\n", notes)));
        }
        return new PlannerCardResult(title, rows, isPreview, canApprove, planId,
            string.IsNullOrWhiteSpace(link) ? null : link,
            new JsonObject { ["plan"] = plan.DeepClone(), ["tasks"] = tasks.DeepClone() }.ToJsonString(),
            string.Join("\n\n", planDetails));
    }

    internal static (string Plan, string? Task) ReadPlannerUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !string.Equals(uri.Host, "planner.cloud.microsoft", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsDefaultPort)
            throw new FormatException("The returned Planner link is not a supported secure Planner URL.");
        var match = PlanPath.Match(uri.AbsolutePath);
        if (!match.Success) throw new FormatException("The returned Planner link has an unsupported path.");
        return (match.Groups["plan"].Value, match.Groups["task"].Success ? match.Groups["task"].Value : null);
    }

    private static string Text(JsonObject value, string name, bool required = false)
    {
        if (value[name] == null)
        {
            if (required) throw new FormatException("The Planner preview is missing " + name + ".");
            return "";
        }
        if (value[name] is not JsonValue field || !field.TryGetValue<string>(out var text)
            || (required && string.IsNullOrWhiteSpace(text)) || text.Length > 2000)
            throw new FormatException("The Planner preview contains an invalid " + name + ".");
        return text;
    }

    internal static bool IsCardAction(object? value)
    {
        if (value == null) return false;
        return JsonSerializer.SerializeToNode(value) is JsonObject payload
            && payload["kind"] is JsonValue kind && kind.TryGetValue<string>(out var name)
            && name.StartsWith("office.delegation.", StringComparison.Ordinal);
    }

    internal static string WithoutPlannerMarkup(string response) =>
        EncodedPlannerTag.Replace(PlannerTag.Replace(response, ""), "").Trim();

    internal static (string Id, string Action) ReadAction(object value)
    {
        var payload = JsonSerializer.SerializeToNode(value) as JsonObject ?? throw new ArgumentException("Invalid card action.");
        if (payload.Any(field => field.Key is not ("kind" or "id" or "action")))
            throw new ArgumentException("Unexpected card action data.");
        var kind = Text(payload, "kind", true);
        var id = Text(payload, "id", true);
        var action = Text(payload, "action", true);
        if (kind != ActionKind || !Guid.TryParseExact(id, "N", out _) || action is not ("approve" or "decline"))
            throw new ArgumentException("This card action is not supported.");
        return (id, action);
    }

    internal static Activity Activity(DelegationCardEntity state)
    {
        var content = Build(state);
        return new Activity
        {
            Type = ActivityTypes.Message,
            Id = string.IsNullOrEmpty(state.ActivityId) ? null : state.ActivityId,
            Attachments = [new Attachment
            {
                ContentType = "application/vnd.microsoft.card.adaptive", Content = content,
                Name = "delegation-" + state.RowKey
            }]
        };
    }

    internal static JsonObject Build(DelegationCardEntity state)
    {
        var body = new JsonArray(
            Block(string.IsNullOrWhiteSpace(state.OwnerName) ? "Delegated work" : state.OwnerName, "Small", "Bolder"),
            Block(Status(state.State), "Large", "Bolder"),
            new JsonObject
            {
                ["type"] = "FactSet",
                ["facts"] = new JsonArray(
                    new JsonObject { ["title"] = "Working with", ["value"] = Plain(state.AgentName) },
                    new JsonObject { ["title"] = "Work", ["value"] = Plain(state.Question.Length <= 200
                        ? state.Question : state.Question[..197] + "...") })
            });
        body[0]!["id"] = "delegation-" + state.RowKey;
        if (state.ParentScope.StartsWith("mail:", StringComparison.Ordinal))
            body[2]!["facts"]!.AsArray().Insert(0, new JsonObject { ["title"] = "Source", ["value"] = "Email" });
        var actions = new JsonArray();
        if (state.Question.Length > 200)
            actions.Add(new JsonObject
            {
                ["type"] = "Action.ShowCard", ["title"] = "Work details",
                ["card"] = new JsonObject
                {
                    ["type"] = "AdaptiveCard", ["version"] = "1.5",
                    ["body"] = new JsonArray(Block(state.Question))
                }
            });
        if (!string.IsNullOrWhiteSpace(state.Detail)) body.Add(Block(state.Detail));
        var planner = string.IsNullOrEmpty(state.PreviewJson)
            ? null : JsonSerializer.Deserialize<PlannerCardResult>(state.PreviewJson)
                ?? throw new InvalidOperationException("The stored card preview is invalid.");
        if (planner != null)
        {
            body.Add(Block(planner.Title, "Medium", "Bolder"));
            if (!string.IsNullOrWhiteSpace(planner.PlanDetails)) body.Add(Block(planner.PlanDetails));
            foreach (var task in planner.Tasks)
            {
                body.Add(Block(task.Title, weight: "Bolder"));
                body.Add(Block(task.Details, "Small"));
            }
            if (planner.Tasks.Any(task => !string.IsNullOrWhiteSpace(task.Notes)))
            {
                var evidence = new JsonArray(Block("Task evidence and notes", "Medium", "Bolder"));
                foreach (var task in planner.Tasks.Where(task => !string.IsNullOrWhiteSpace(task.Notes)))
                {
                    evidence.Add(Block(task.Title, weight: "Bolder"));
                    evidence.Add(Block(task.Notes));
                }
                actions.Add(new JsonObject
                {
                    ["type"] = "Action.ShowCard", ["title"] = "Evidence and notes",
                    ["card"] = new JsonObject { ["type"] = "AdaptiveCard", ["version"] = "1.5", ["body"] = evidence }
                });
            }
            if (state.State == DelegationCardStates.Review && planner.CanApprove)
            {
                actions.Add(Submit("Approve and save", "approve", state.RowKey["card-".Length..]));
                actions.Add(Submit("Do not save", "decline", state.RowKey["card-".Length..]));
            }
            if (state.State == DelegationCardStates.Saved && planner.PlanUrl != null)
                actions.Add(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open plan", ["url"] = planner.PlanUrl });
            else if (planner.PlanUrl != null)
                actions.Add(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Inspect returned plan", ["url"] = planner.PlanUrl });
        }
        else if (!string.IsNullOrWhiteSpace(state.Answer))
        {
            body.Add(Block(state.Answer.Length <= 600 ? state.Answer : "Response preview:\n" + state.Answer[..597] + "..."));
        }
        if (!string.IsNullOrWhiteSpace(state.Answer))
        {
            actions.Add(new JsonObject
            {
                ["type"] = "Action.ShowCard", ["title"] = "Original response",
                ["card"] = new JsonObject
                {
                    ["type"] = "AdaptiveCard", ["version"] = "1.5",
                    ["body"] = new JsonArray(Block(state.Answer))
                }
            });
        }
        var card = new JsonObject
        {
            ["$schema"] = "https://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard", ["version"] = "1.5",
            ["fallbackText"] = $"{state.AgentName}: {Status(state.State)}. {state.Detail}",
            ["body"] = body, ["actions"] = actions
        };
        if (Encoding.UTF8.GetByteCount(card.ToJsonString()) > CardByteBudget)
            throw new InvalidOperationException("The complete preview exceeds the card's display budget. Use the original text response; no partial preview can be approved.");
        return card;
    }

    private static JsonObject Submit(string title, string action, string id) => new()
    {
        ["type"] = "Action.Submit", ["title"] = title,
        ["data"] = new JsonObject { ["kind"] = ActionKind, ["id"] = id, ["action"] = action }
    };

    private static JsonObject Block(string text, string size = "Default", string weight = "Default") => new()
    {
        ["type"] = "TextBlock", ["text"] = Plain(text), ["size"] = size, ["weight"] = weight, ["wrap"] = true
    };

    private static string Plain(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Status(string state) => state switch
    {
        DelegationCardStates.Working => "Delegating the work",
        DelegationCardStates.Pending => "The specialist is still working",
        DelegationCardStates.Review => "Review before saving",
        DelegationCardStates.Saving => "Saving the approved plan",
        DelegationCardStates.Verifying => "Checking the saved work",
        DelegationCardStates.Saved => "Plan and tasks saved",
        DelegationCardStates.Failed => "The specialist did not complete the request",
        DelegationCardStates.Unconfirmed => "The result is not confirmed",
        DelegationCardStates.Stale => "This preview needs a new review",
        DelegationCardStates.Declined => "Not saved",
        _ => "Response received"
    };
}
