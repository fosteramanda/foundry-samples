using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Microsoft.Agents.Core.Models;
using WorkstreamManager.Services;

namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

internal sealed record StandingJobTurn(
    StandingJobCaller Caller, StandingJobEvent? Event, IActivity Activity,
    bool Automatic = false, string? JobId = null, string? LeaseId = null);

internal sealed class StandingJobToolHandler(
    StandingReviewCoordinator coordinator,
    WorkItemService? workItems,
    Func<StandingJob, bool, Task<(bool Ok, string Detail)>> configureSchedule,
    Func<IReadOnlyList<string>, string, string, Task<(bool Accepted, string Detail)>> sendMail,
    Func<StandingJob, string, Task<string>> sendChat,
    Func<string, string, Task<string?>> askAgent,
    Func<string, Task<StandingJobMember?>> resolveMember,
    ILogger logger,
    Func<StandingJob, int, string, string, Task<StandingJobBrief>>? publishBrief = null,
    Func<StandingJob, int, string, Task<StandingJobBrief>>? reconcileBrief = null,
    Func<StandingJob, int, string, Task<StandingJobBrief?>>? tryReconcileBrief = null,
    Func<StandingJob, IReadOnlyList<StandingJobBrief>, string, Task<StandingJobEvent>>? readCommentNotification = null)
{
    internal StandingJobTurn? Turn { get; set; }
    internal bool IsEnabled => coordinator.IsAvailable && Turn != null;
    internal bool Automatic => Turn?.Automatic == true;

    internal List<JsonNode> GetToolDefinitions()
    {
        if (!IsEnabled) return [];
        var tools = new List<JsonNode>
        {
            Tool("list_standing_jobs", "List standing jobs this approved actor can access.", "{}", []),
            Tool("get_standing_job", "Read the durable mandate, inputs, decisions, commitments and action receipts. Use event IDs and exact source quotes for updates.",
                """{"job_id":{"type":"string"},"event_id":{"type":"string"}}""", ["job_id"]),
            Tool("record_standing_input", "Record an input owned by a job participant. Received/disputed facts must quote a stored owner or manager event; missing means still unprovided, never an invented answer.",
                """{"job_id":{"type":"string"},"key":{"type":"string"},"title":{"type":"string"},"owner_id":{"type":"string"},"state":{"type":"string","enum":["missing","received","disputed"]},"content":{"type":"string"},"evidence_event_id":{"type":"string"}}""",
                ["job_id", "key", "title", "owner_id", "state", "content"]),
            Tool("record_standing_decision", "Record the manager's actual decision, quoted verbatim from a recorded event. This tool cannot make a decision. Corrections append a new decision with a supersedes ID.",
                """{"job_id":{"type":"string"},"statement_quote":{"type":"string"},"rationale":{"type":"string"},"evidence_event_id":{"type":"string"},"supersedes":{"type":"string"}}""",
                ["job_id", "statement_quote", "rationale"]),
            Tool("create_standing_commitment", "Create a job-linked commitment from a recorded manager or owner statement. Use an explicit owner and UTC due time; do not guess either.",
                """{"job_id":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"},"owner_id":{"type":"string"},"due_utc":{"type":"string"},"decision_id":{"type":"string"},"dependency_ids":{"type":"array","items":{"type":"string"}},"evidence_event_id":{"type":"string"},"evidence_quote":{"type":"string"}}""",
                ["job_id", "title", "description", "owner_id", "due_utc", "evidence_quote"]),
            Tool("update_standing_commitment", "Update or close a commitment only with quoted evidence from its recorded owner or the manager. Closing requires actual completion confirmation, not merely sending a reminder.",
                """{"job_id":{"type":"string"},"item_id":{"type":"string"},"status":{"type":"string","enum":["open","in_progress","closed"]},"due_utc":{"type":"string"},"owner_id":{"type":"string"},"evidence_event_id":{"type":"string"},"evidence_quote":{"type":"string"}}""",
                ["job_id", "item_id", "evidence_quote"]),
            Tool("send_standing_message", "Send a job message as the agent, within the manager-approved recipient list. Reminders are limited to one per recipient/purpose/day. Briefs and escalations are deduplicated against recorded facts. Use email for owners; chat is the job's original manager conversation. Never claim accepted email is independently verified inbox delivery.",
                """{"job_id":{"type":"string"},"purpose":{"type":"string","enum":["request_input","follow_up","brief","escalation"]},"related_id":{"type":"string"},"delivery":{"type":"string","enum":["email","chat"]},"recipients":{"type":"array","items":{"type":"string"}},"subject":{"type":"string"},"body_html":{"type":"string"}}""",
                ["job_id", "purpose", "delivery", "subject", "body_html"]),
            Tool("ask_standing_specialist", "Use the existing generic A2A handler for a specialist explicitly permitted for this job. An empty or pending result is not a completed answer. Calls with the same question and facts are not automatically repeated.",
                """{"job_id":{"type":"string"},"agent_id":{"type":"string"},"question":{"type":"string"}}""",
                ["job_id", "agent_id", "question"])
        };
        if (!Automatic && Turn!.Caller.IsManager)
        {
            tools.Add(Tool("resolve_standing_member", "Resolve a participant's real tenant directory ID and email before configuring a job. Use an email, ID or exact display name; ambiguous names are not guessed.",
                """{"identifier":{"type":"string"}}""", ["identifier"]));
            tools.Add(Tool("create_standing_job", "Save a manager-owned standing responsibility and create its periodic check. Configure it in the manager's personal Teams chat. Members, recipients and document/mail bindings are separate; this never grants platform or resource permissions.",
                """
                {"name":{"type":"string"},"mandate":{"type":"string"},
                 "members":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"email":{"type":"string"}},"required":["id","email"],"additionalProperties":false}},
                 "recipients":{"type":"array","items":{"type":"string"}},
                 "source_bindings":{"type":"array","items":{"type":"string"},"description":"Exact word:<document GUID> or mail:<conversation ID> bindings. No wildcards."},
                 "specialist_agent_ids":{"type":"array","items":{"type":"string"}},
                 "cron_expression":{"type":"string","description":"Five-field cron, at least five minutes apart. Default is every 15 minutes."},
                 "time_zone":{"type":"string"},"review_utc":{"type":"string"}}
                """, ["name", "mandate"]));
            tools.Add(Tool("update_standing_job", "Manager-only: revise or pause/resume the mandate and its permitted participants, recipients and bindings. A paused job cannot perform actions.",
                """
                {"job_id":{"type":"string"},"mandate":{"type":"string"},"enabled":{"type":"boolean"},
                 "members":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"email":{"type":"string"}},"required":["id","email"],"additionalProperties":false}},
                 "recipients":{"type":"array","items":{"type":"string"}},"source_bindings":{"type":"array","items":{"type":"string"}},
                 "specialist_agent_ids":{"type":"array","items":{"type":"string"}},"review_utc":{"type":"string"}}
                """, ["job_id"]));
            if (readCommentNotification != null)
                tools.Add(Tool("ingest_standing_comment_notification", "Verify a real Word comment notification in this agent user's mailbox, bind it to an existing job brief, and record the exact human comment as durable evidence. Use the Graph message ID returned by the mailbox search; the host independently verifies sender, document, text, and received time.",
                    """{"job_id":{"type":"string"},"message_id":{"type":"string"}}""", ["job_id", "message_id"]));
        }
        if (Automatic)
            tools.RemoveAll(tool => tool["name"]!.GetValue<string>() == "list_standing_jobs");
        if (publishBrief != null)
            tools.Add(Tool("publish_standing_brief", "Publish a new versioned Word decision brief and shared ledger from this job's recorded facts. It is shared only with configured job members and its comments bind back to this job. Reuses the existing revision when facts did not change; earlier revisions are never overwritten. Provide the exact CEO question and options; the host includes the evidence, decisions and commitment ledger.",
                """{"job_id":{"type":"string"},"summary":{"type":"string"},"decision_question":{"type":"string"},"options":{"type":"array","items":{"type":"string"}}}""",
                ["job_id", "summary", "decision_question"]));
        return tools;
    }

    internal async Task<string?> TryExecuteAsync(string name, string arguments)
    {
        if (!GetToolDefinitions().Any(tool => tool["name"]!.GetValue<string>() == name)) return null;
        try
        {
            var args = JsonNode.Parse(arguments) as JsonObject ?? throw new ArgumentException("Tool arguments must be an object.");
            if (name == "create_standing_job") return Json(await CreateAsync(args));
            if (name == "resolve_standing_member")
            {
                RequireHumanManager();
                return Json(await resolveMember(Text(args, "identifier", 254))
                    ?? throw new ArgumentException("No unique directory contact was found."));
            }
            if (name == "list_standing_jobs") return Json(await coordinator.ListAsync(Turn!.Caller));
            var id = Id(args, "job_id");
            if (Turn!.JobId != null && Turn.JobId != id)
                throw new UnauthorizedAccessException("This automatic turn is bound to a different job.");
            var job = await coordinator.GetAsync(id, Turn.Caller);
            if (job.ManagerId != Turn.Caller.ManagerId && name != "update_standing_job")
                throw new UnauthorizedAccessException("The current manager must reauthorize this standing job.");
            if (job.Paused && name is not ("get_standing_job" or "update_standing_job"))
                throw new InvalidOperationException("This standing job is paused.");
            if (Turn.Event != null && name != "get_standing_job")
                await coordinator.CaptureAsync(id, Turn.Caller, Turn.Event);
            await coordinator.EnsureRunAsync(id, Turn.LeaseId);
            return name switch
            {
                "get_standing_job" => await ReadAsync(job, args),
                "update_standing_job" => Json(await UpdateAsync(job, args)),
                "record_standing_input" => await InputAsync(job, args),
                "record_standing_decision" => Json(await coordinator.RecordDecisionAsync(id, Turn.Caller,
                    EventId(args), Text(args, "statement_quote", 4000), Text(args, "rationale", 4000, allowEmpty: true),
                    Optional(args, "supersedes"))),
                "create_standing_commitment" => await CreateCommitmentAsync(job, args),
                "update_standing_commitment" => await UpdateCommitmentAsync(job, args),
                "send_standing_message" => Json(await SendAsync(job, args)),
                "ask_standing_specialist" => Json(await DelegateAsync(job, args)),
                "publish_standing_brief" => Json(await PublishAsync(job, args)),
                "ingest_standing_comment_notification" => Json(await IngestCommentNotificationAsync(job, args)),
                _ => null
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException
            or JsonException or RequestFailedException or FormatException)
        {
            logger.LogWarning(ex, "Standing-job tool {Tool} did not complete.", name);
            return Json(new { success = false, error = ex.Message });
        }
    }

    private async Task<StandingJob> CreateAsync(JsonObject args)
    {
        RequireHumanManager();
        var activity = Turn!.Activity;
        if (activity.ChannelId?.ToString() != "msteams"
            || !string.Equals(activity.Conversation?.ConversationType, "personal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Create the standing job in the manager's personal Teams conversation.");
        var title = Text(args, "name", 160);
        var eventId = Turn.Event?.Id ?? throw new InvalidOperationException("A human mandate event is required.");
        var bytes = Convert.FromHexString(StandingJobStore.Hash(eventId + "\n" + title));
        var job = new StandingJob
        {
            Id = new Guid(bytes.AsSpan(0, 16)).ToString("D"),
            Title = title,
            Mandate = Text(args, "mandate", 4000),
            Members = await MembersAsync(args, Turn.Caller),
            Recipients = Strings(args, "recipients", 50).Select(Email).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Bindings = Bindings(args),
            SpecialistAgentIds = Strings(args, "specialist_agent_ids", 30),
            ConversationId = activity.Conversation?.Id ?? throw new InvalidOperationException("The chat ID is missing."),
            CronExpression = Optional(args, "cron_expression") ?? "*/15 * * * *",
            TimeZone = Optional(args, "time_zone") ?? "UTC",
            ReviewUtc = Utc(args, "review_utc")
        };
        if (job.CronExpression.Length > 100 || job.CronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 5)
            throw new ArgumentException("Use a five-field cron expression.");
        if (job.Recipients.Count == 0) job.Recipients.Add(job.Members.Single(member => member.Id == Turn.Caller.ManagerId).Email);
        job.RoutineName = RoutineToolHandler.BuildStandingRoutineName(job.Id);
        await coordinator.CreateAsync(job, Turn.Caller);
        await coordinator.CaptureAsync(job.Id, Turn.Caller, Turn.Event! with { Kind = "mandate" });
        var scheduled = await configureSchedule(job, true);
        if (!scheduled.Ok)
            throw new InvalidOperationException($"Job {job.Id} is saved but paused; its schedule was not configured: {scheduled.Detail}");
        return await coordinator.ChangeAsync(job.Id, Turn.Caller, current => current.Paused = false, job.Revision);
    }

    private async Task<StandingJob> UpdateAsync(StandingJob job, JsonObject args)
    {
        RequireHumanManager();
        var members = args.ContainsKey("members") || job.ManagerId != Turn!.Caller.ManagerId
            ? await MembersAsync(args, Turn!.Caller) : null;
        var updated = await coordinator.ChangeAsync(job.Id, Turn!.Caller, current =>
        {
            if (current.ManagerId != Turn.Caller.ManagerId)
            {
                if (Turn.Activity.ChannelId?.ToString() != "msteams" || Turn.Activity.Conversation?.ConversationType != "personal")
                    throw new InvalidOperationException("The new manager must reauthorize the job in their personal Teams chat.");
                current.Members = members!;
                current.Recipients = [members!.Single(member => member.Id == Turn.Caller.ManagerId).Email];
                current.Bindings = [];
                current.SpecialistAgentIds = [];
                current.ConversationId = Turn.Activity.Conversation.Id;
                current.Paused = true;
            }
            if (args.ContainsKey("mandate")) current.Mandate = Text(args, "mandate", 4000);
            if (members != null) current.Members = members;
            if (args.ContainsKey("recipients")) current.Recipients = Strings(args, "recipients", 50).Select(Email).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (args.ContainsKey("source_bindings")) current.Bindings = Bindings(args);
            if (args.ContainsKey("specialist_agent_ids")) current.SpecialistAgentIds = Strings(args, "specialist_agent_ids", 30);
            if (args.ContainsKey("review_utc")) current.ReviewUtc = Utc(args, "review_utc");
            current.RoutineName = RoutineToolHandler.BuildStandingRoutineName(current.Id);
            if (args.ContainsKey("enabled")) current.Paused = true;
        });
        if (args["enabled"] is JsonValue enabled)
        {
            var shouldEnable = enabled.GetValue<bool>();
            var result = await configureSchedule(updated, shouldEnable);
            if (!result.Ok)
                throw new InvalidOperationException($"The job remains paused; schedule update failed: {result.Detail}");
            if (shouldEnable)
                updated = await coordinator.ChangeAsync(job.Id, Turn.Caller, current => current.Paused = false, updated.Revision);
        }
        return updated;
    }

    private async Task<string> ReadAsync(StandingJob job, JsonObject args)
    {
        var eventId = Optional(args, "event_id");
        if (eventId != null)
        {
            var record = (await coordinator.RecordsAsync<StandingJobEvent>(job.Id, "event")).SingleOrDefault(item => item.Id == eventId)
                ?? throw new ArgumentException("That event does not belong to this job.");
            return Json(record);
        }
        var commitments = workItems == null ? null : ParseWorkResult(await workItems.ListWorkItemsAsync(
            coordinator.Partition, standingJobId: job.Id));
        var items = commitments?["items"] as JsonArray;
        return Json(new
        {
            currentEventId = Turn!.Event?.Id,
            state = await coordinator.SnapshotAsync(job.Id, Turn.Caller),
            phase = StandingReviewCoordinator.Phase(job,
                await coordinator.RecordsAsync<StandingJobInput>(job.Id, "input"),
                await coordinator.RecordsAsync<StandingJobDecision>(job.Id, "decision"),
                items?.Count(item => item?["status"]?.GetValue<string>() != "closed") ?? 0, items?.Count ?? 0),
            commitments
        });
    }

    private async Task<string> InputAsync(StandingJob job, JsonObject args)
    {
        var state = Text(args, "state", 20);
        var input = new StandingJobInput(Text(args, "key", 120), Text(args, "title", 300),
            Id(args, "owner_id"), state, Text(args, "content", 4000, allowEmpty: state == "missing"),
            state == "missing" ? string.Empty : EventId(args), coordinator.Now);
        await coordinator.RecordInputAsync(job.Id, Turn!.Caller, input);
        return Json(new { success = true, input.Key, input.State });
    }

    private async Task<string> CreateCommitmentAsync(StandingJob job, JsonObject args)
    {
        var service = workItems ?? throw new InvalidOperationException("Work-item storage is unavailable.");
        var ownerId = Id(args, "owner_id");
        var owner = job.Members.SingleOrDefault(member => member.Id == ownerId)
            ?? throw new ArgumentException("The commitment owner must be a job participant.");
        var evidence = await coordinator.EvidenceAsync(job.Id, EventId(args), Text(args, "evidence_quote", 4000));
        if (evidence.ActorId != ownerId && evidence.ActorId != job.ManagerId)
            throw new UnauthorizedAccessException("A commitment must be assigned by its owner or the manager.");
        var decisionId = Optional(args, "decision_id");
        if (decisionId != null && !(await coordinator.RecordsAsync<StandingJobDecision>(job.Id, "decision")).Any(item => item.Id == decisionId))
            throw new ArgumentException("The decision does not belong to this job.");
        var title = Text(args, "title", 300);
        var due = Utc(args, "due_utc") ?? throw new ArgumentException("An explicit UTC due time is required.");
        var key = $"commitment:{evidence.Id}:{ownerId}:{title.Trim().ToLowerInvariant()}";
        var idBytes = Convert.FromHexString(StandingJobStore.Hash(job.Id + key));
        var taskId = new Guid(idBytes.AsSpan(0, 16)).ToString("D");
        var dependencies = Strings(args, "dependency_ids", 20).Select(value => Guid.Parse(value).ToString("D")).Distinct().ToArray();
        foreach (var dependency in dependencies)
        {
            if (dependency == taskId) throw new ArgumentException("A commitment cannot depend on itself.");
            var item = ParseWorkResult(await service.GetWorkItemAsync(coordinator.Partition, dependency));
            if (item["standingJobId"]?.GetValue<string>() != job.Id)
                throw new ArgumentException("Dependencies must be existing commitments in the same job.");
        }
        var receipt = await coordinator.OnceAsync(job.Id, Turn!.Caller, "create_commitment", key, args.ToJsonString(),
            async () =>
            {
                var result = await service.CreateWorkItemAsync(coordinator.Partition, title,
                    Text(args, "description", 4000), owner.Email, owner.Id, due.ToString("o"),
                    job.Id, decisionId, taskId, dependencies);
                var parsed = ParseWorkResult(result);
                return (parsed["success"]?.GetValue<bool>() == true, result);
            }, Turn.LeaseId);
        return Json(new { taskId, receipt });
    }

    private async Task<string> UpdateCommitmentAsync(StandingJob job, JsonObject args)
    {
        var service = workItems ?? throw new InvalidOperationException("Work-item storage is unavailable.");
        var itemId = Id(args, "item_id");
        var item = ParseWorkResult(await service.GetWorkItemAsync(coordinator.Partition, itemId));
        if (item["standingJobId"]?.GetValue<string>() != job.Id)
            throw new UnauthorizedAccessException("The commitment does not belong to this job.");
        var quote = Text(args, "evidence_quote", 4000);
        var evidence = await coordinator.EvidenceAsync(job.Id, EventId(args), quote);
        var owner = item["ownerAadObjectId"]?.GetValue<string>();
        if (evidence.ActorId != owner && evidence.ActorId != job.ManagerId)
            throw new UnauthorizedAccessException("Only the commitment owner or manager can confirm its state.");
        var status = Optional(args, "status");
        if (status is not (null or "open" or "in_progress" or "closed"))
            throw new ArgumentException("Unsupported commitment state.");
        var due = Utc(args, "due_utc");
        var newOwnerId = args["owner_id"] == null ? null : Id(args, "owner_id");
        StandingJobMember? newOwner = null;
        if (newOwnerId != null)
        {
            if (evidence.ActorId != job.ManagerId)
                throw new UnauthorizedAccessException("Only the manager can reassign a commitment.");
            newOwner = job.Members.SingleOrDefault(member => member.Id == newOwnerId)
                ?? throw new ArgumentException("The new owner must be a job participant.");
        }
        if (status == null && due == null && newOwner == null)
            throw new ArgumentException("Provide a status, due time or owner change.");
        if (status == "closed" && item["dependencyIds"] is JsonArray dependencies)
        {
            foreach (var dependency in dependencies)
            {
                var required = ParseWorkResult(await service.GetWorkItemAsync(coordinator.Partition, dependency!.GetValue<string>()));
                if (required["standingJobId"]?.GetValue<string>() != job.Id || required["status"]?.GetValue<string>() != "closed")
                    throw new InvalidOperationException("An explicit dependency is still open; do not close this commitment.");
            }
        }
        var receipt = await coordinator.OnceAsync(job.Id, Turn!.Caller, "update_commitment",
            $"commitment-update:{itemId}:{evidence.Id}:{status}:{due:o}:{newOwnerId}", args.ToJsonString(),
            async () =>
            {
                var result = await service.UpdateWorkItemAsync(coordinator.Partition, itemId,
                    owner: newOwner?.Email, ownerAadObjectId: newOwner?.Id, eta: due?.ToString("o"), status: status,
                    standingJobId: job.Id, completionEvidence: status == "closed" ? $"{evidence.Id}: {quote}" : null,
                    completionConfirmedBy: status == "closed" ? evidence.ActorId : null);
                return (ParseWorkResult(result)["success"]?.GetValue<bool>() == true, result);
            }, Turn.LeaseId);
        return Json(receipt);
    }

    private async Task<StandingJobReceipt> SendAsync(StandingJob job, JsonObject args)
    {
        var purpose = Text(args, "purpose", 30);
        if (purpose is not ("request_input" or "follow_up" or "brief" or "escalation"))
            throw new ArgumentException("Unsupported standing-job message purpose.");
        var delivery = Text(args, "delivery", 10);
        if (delivery is not ("chat" or "email")) throw new ArgumentException("Choose chat or email.");
        var recipients = Strings(args, "recipients", 30).Select(Email).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (delivery == "email" && (recipients.Count == 0 || recipients.Any(value => !job.Recipients.Contains(value, StringComparer.OrdinalIgnoreCase))))
            throw new UnauthorizedAccessException("Every email recipient must be explicitly permitted by the manager for this job.");
        var ownerEmail = job.Members.Single(member => member.Id == job.ManagerId).Email;
        if (purpose == "escalation" && recipients.Any(value => !string.Equals(value, ownerEmail, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("A decision escalation goes only to the manager.");
        var related = Optional(args, "related_id");
        if (purpose == "request_input")
        {
            var input = (await coordinator.RecordsAsync<StandingJobInput>(job.Id, "input")).SingleOrDefault(item => item.Key == related)
                ?? throw new ArgumentException("Name the recorded missing input.");
            if (input.State != "missing") throw new InvalidOperationException("That input has already been received.");
            ValidateOwnerRecipients(job, input.OwnerId, recipients, delivery);
        }
        if (purpose == "follow_up")
        {
            var service = workItems ?? throw new InvalidOperationException("Work-item storage is unavailable.");
            var item = ParseWorkResult(await service.GetWorkItemAsync(coordinator.Partition, Guid.Parse(related ?? "").ToString("D")));
            if (item["standingJobId"]?.GetValue<string>() != job.Id || item["status"]?.GetValue<string>() == "closed")
                throw new InvalidOperationException("Follow-ups require an open commitment in this job.");
            if (!DateTimeOffset.TryParse(item["eta"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var due) || due > coordinator.Now.AddDays(1))
                throw new InvalidOperationException("This commitment is not yet within one day of its recorded due time.");
            ValidateOwnerRecipients(job, item["ownerAadObjectId"]!.GetValue<string>(), recipients, delivery);
        }
        var facts = await FactsAsync(job);
        var stamp = purpose is "request_input" or "follow_up"
            ? coordinator.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : facts;
        var scope = $"message:{purpose}:{delivery}:{string.Join(",", recipients)}";
        var key = $"{scope}:{stamp}";
        var subject = $"{StandingReviewCoordinator.Marker(job.Id)} {Text(args, "subject", 160)}";
        var html = Text(args, "body_html", 12000);
        return await coordinator.OnceAsync(job.Id, Turn!.Caller, purpose, key, subject + "\n" + html,
            async () =>
            {
                if (delivery == "chat")
                    return (true, "Graph message receipt: " + await sendChat(job, html));
                var result = await sendMail(recipients, subject, html);
                if (result.Accepted && purpose == "follow_up" && workItems != null && related != null)
                {
                    var saved = await workItems.UpdateWorkItemAsync(coordinator.Partition, related,
                        standingJobId: job.Id, lastFollowUpUtc: coordinator.Now);
                    if (saved.StartsWith("Error", StringComparison.Ordinal))
                        logger.LogWarning("Mail was accepted but its commitment follow-up timestamp was not saved: {Error}", saved);
                }
                return (result.Accepted, result.Accepted
                    ? "Graph accepted this email submission. Recipient inbox delivery is not independently verified."
                    : result.Detail);
            }, Turn.LeaseId, scope);
    }

    private async Task<string> FactsAsync(StandingJob job)
    {
        var tasks = workItems == null ? null : ParseWorkResult(await workItems.ListWorkItemsAsync(
            coordinator.Partition, standingJobId: job.Id))["items"] as JsonArray;
        var stableTasks = tasks?.Select(item => new
        {
            id = item?["id"]?.GetValue<string>(), name = item?["name"]?.GetValue<string>(),
            status = item?["status"]?.GetValue<string>(), owner = item?["owner"]?.GetValue<string>(),
            eta = item?["eta"]?.GetValue<string>(), evidence = item?["completionEvidence"]?.GetValue<string>()
        }).OrderBy(item => item.id);
        return StandingJobStore.Hash(await coordinator.StateFingerprintAsync(job.Id)
            + JsonSerializer.Serialize(stableTasks, StandingJobStore.Json));
    }

    private async Task<StandingJobReceipt> PublishAsync(StandingJob job, JsonObject args)
    {
        if (publishBrief == null) throw new InvalidOperationException("Word publication is not configured.");
        var fingerprint = await FactsAsync(job);
        var existing = (await coordinator.RecordsAsync<StandingJobBrief>(job.Id, "brief")).ToList();
        if (existing.Count == 0 && reconcileBrief != null)
        {
            var uncertain = await coordinator.ScopeReceiptAsync(job.Id, Turn!.Caller, "publish_brief");
            if (uncertain?.State == "uncertain"
                && uncertain.Key.StartsWith("brief:", StringComparison.Ordinal)
                && uncertain.Key.Length > "brief:".Length)
            {
                var priorFingerprint = uncertain.Key["brief:".Length..];
                var recovered = await reconcileBrief(job, 1, priorFingerprint);
                await coordinator.RegisterBriefAsync(job.Id, Turn.Caller, recovered);
                await coordinator.CompleteUncertainAsync(
                    job.Id, Turn.Caller, "publish_brief", Json(recovered));
                existing = [recovered];
            }
        }
        if (tryReconcileBrief != null)
        {
            for (var version = existing.Count == 0 ? 1 : existing.Max(brief => brief.Version) + 1;
                version <= 100; version++)
            {
                var recovered = await tryReconcileBrief(
                    job, version, StandingJobStore.Hash($"reconciled-existing:{job.Id}:{version}"));
                if (recovered == null) break;
                await coordinator.RegisterBriefAsync(job.Id, Turn!.Caller, recovered);
                existing.Add(recovered);
            }
        }
        var nextVersion = existing.Count == 0 ? 1 : existing.Max(brief => brief.Version) + 1;
        var content = new StringBuilder();
        static string Html(string value) => System.Net.WebUtility.HtmlEncode(value);
        content.Append($"<h1>{Html(job.Title)}</h1><p>Leadership review brief v{nextVersion}</p>");
        content.Append($"<p>{Html(Text(args, "summary", 4000))}</p><h2>Decision needed</h2>");
        content.Append($"<p>{Html(Text(args, "decision_question", 2000, allowEmpty: true))}</p><ul>");
        foreach (var option in Strings(args, "options", 5))
            content.Append($"<li>{Html(option)}</li>");
        content.Append("</ul><h2>Inputs and unresolved assumptions</h2><table><tr><th>Input</th><th>Status</th><th>Evidence</th></tr>");
        foreach (var input in await coordinator.RecordsAsync<StandingJobInput>(job.Id, "input"))
            content.Append($"<tr><td>{Html(input.Title)}</td><td>{Html(input.State)}</td><td>{Html(input.Content)}</td></tr>");
        content.Append("</table><h2>Decision ledger</h2>");
        var decisions = await coordinator.RecordsAsync<StandingJobDecision>(job.Id, "decision");
        var superseded = decisions.Where(item => item.Supersedes != null).Select(item => item.Supersedes).ToHashSet();
        foreach (var decision in decisions.OrderBy(item => item.RecordedUtc))
            content.Append($"<p><strong>{(superseded.Contains(decision.Id) ? "Superseded: " : "")}{Html(decision.Statement)}</strong><br/>{Html(decision.Rationale)}</p>");
        content.Append("<h2>Commitment ledger</h2><table><tr><th>Commitment</th><th>Owner</th><th>Due</th><th>Status</th><th>Completion evidence</th></tr>");
        if (workItems != null && ParseWorkResult(await workItems.ListWorkItemsAsync(
            coordinator.Partition, standingJobId: job.Id))["items"] is JsonArray tasks)
            foreach (var item in tasks)
                content.Append("<tr>" + string.Concat(new[] { "name", "owner", "eta", "status", "completionEvidence" }
                    .Select(field => $"<td>{Html(item?[field]?.GetValue<string>() ?? "")}</td>")) + "</tr>");
        content.Append("</table><h2>Specialist evidence</h2>");
        foreach (var receipt in (await coordinator.RecordsAsync<StandingJobReceipt>(job.Id, "receipt"))
            .Where(item => item.Operation == "delegate" && item.State == "accepted"))
        {
            var answer = JsonNode.Parse(receipt.Detail);
            content.Append($"<p><strong>{Html(answer?["agent_name"]?.GetValue<string>() ?? "Authorized specialist")}</strong></p>");
            content.Append($"<p>{Html(answer?["answer"]?.GetValue<string>() ?? receipt.Detail)}</p>");
        }
        content.Append("<h2>Source notes</h2>");
        foreach (var source in (await coordinator.RecordsAsync<StandingJobEvent>(job.Id, "event")).OrderBy(item => item.ReceivedUtc))
            content.Append($"<p><strong>{Html(source.Kind)} {source.ReceivedUtc:O}</strong><br/>{Html(source.Content)}</p>");
        return await coordinator.OnceAsync(job.Id, Turn!.Caller, "publish_brief", "brief:" + fingerprint,
            content.ToString(), async () =>
            {
                var artifact = await publishBrief(job, nextVersion, fingerprint, content.ToString());
                await coordinator.RegisterBriefAsync(job.Id, Turn.Caller, artifact);
                return (true, Json(artifact));
            }, Turn.LeaseId, "publish_brief", reconcileUncertain: true);
    }

    private async Task<StandingJobEvent> IngestCommentNotificationAsync(StandingJob job, JsonObject args)
    {
        RequireHumanManager();
        if (readCommentNotification == null)
            throw new InvalidOperationException("Comment notification verification is not configured.");
        var briefs = await coordinator.RecordsAsync<StandingJobBrief>(job.Id, "brief");
        var source = await readCommentNotification(job, briefs, Text(args, "message_id", 2000));
        var member = job.Members.Single(item => item.Id == source.ActorId);
        return await coordinator.CaptureAsync(job.Id,
            new StandingJobCaller(member.Id, job.ManagerId, member.Email), source);
    }

    private async Task<StandingJobReceipt> DelegateAsync(StandingJob job, JsonObject args)
    {
        var agentId = Text(args, "agent_id", 500);
        if (!job.SpecialistAgentIds.Contains(agentId, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("This specialist is not explicitly permitted for the job.");
        var question = Text(args, "question", 6000);
        var scope = "delegate:" + agentId + ":" + StandingJobStore.Hash(question);
        var key = scope + ":" + await coordinator.StateFingerprintAsync(job.Id);
        return await coordinator.OnceAsync(job.Id, Turn!.Caller, "delegate", key, question,
            async () =>
            {
                var answer = await askAgent(agentId, question);
                if (string.IsNullOrWhiteSpace(answer)) return (false, "No specialist result was returned.");
                var result = JsonNode.Parse(answer);
                var outcome = result?["outcome"]?.GetValue<string>();
                return (outcome == "pending" ? null : outcome == "answered", answer);
            }, Turn.LeaseId, scope);
    }

    private void RequireHumanManager()
    {
        if (Turn?.Event?.IsHuman != true || Automatic)
            throw new UnauthorizedAccessException("A scheduled run cannot create or change its own mandate.");
        StandingReviewCoordinator.RequireManager(Turn.Caller);
    }

    private string EventId(JsonObject args) => Optional(args, "evidence_event_id")
        ?? Turn?.Event?.Id ?? throw new ArgumentException("Cite a recorded human evidence event.");

    private static void ValidateOwnerRecipients(StandingJob job, string ownerId, List<string> recipients, string delivery)
    {
        var owner = job.Members.SingleOrDefault(member => member.Id == ownerId)
            ?? throw new ArgumentException("The owner is not a current job participant.");
        if (delivery != "email" || recipients.Count != 1 || !string.Equals(recipients[0], owner.Email, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("This owner follow-up must go only to the recorded owner's permitted email.");
    }

    private async Task<List<StandingJobMember>> MembersAsync(JsonObject args, StandingJobCaller caller)
    {
        var manager = await resolveMember(caller.ManagerId)
            ?? throw new ArgumentException("The manager's actual directory contact could not be resolved.");
        var members = new List<StandingJobMember> { manager };
        if (args["members"] is JsonArray values)
        {
            if (values.Count > 50) throw new ArgumentException("A job may name at most 50 participants.");
            foreach (var value in values)
            {
                var obj = value as JsonObject ?? throw new ArgumentException("Each participant needs an ID and email.");
                var id = Id(obj, "id");
                var member = await resolveMember(id)
                    ?? throw new ArgumentException("The participant is not a resolvable tenant directory identity.");
                if (!string.Equals(member.Email, Email(Text(obj, "email", 254)), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The participant ID and email do not match the directory. Resolve the contact before configuring it.");
                members.Add(member);
            }
        }
        if (members.GroupBy(member => member.Id).Any(group => group.Select(member => member.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            throw new ArgumentException("One participant ID was given conflicting email addresses.");
        return members.DistinctBy(member => member.Id).ToList();
    }

    private static List<string> Bindings(JsonObject args)
    {
        var values = Strings(args, "source_bindings", 30);
        if (values.Any(value => value.Length > 1000 || value.Contains('*')
            || !(value.StartsWith("word:", StringComparison.Ordinal) || value.StartsWith("mail:", StringComparison.Ordinal))))
            throw new ArgumentException("Bindings must be exact word: or mail: resource references.");
        return values.Select(value => value.StartsWith("word:", StringComparison.Ordinal)
            ? "word:" + Guid.Parse(value[5..]).ToString("D") : value).Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> Strings(JsonObject args, string key, int maximum)
    {
        if (args[key] == null) return [];
        var values = args[key] as JsonArray ?? throw new ArgumentException($"{key} must be an array.");
        if (values.Count > maximum) throw new ArgumentException($"{key} has too many entries.");
        return values.Select(value => value?.GetValue<string>()?.Trim()
            ?? throw new ArgumentException($"{key} cannot contain null entries.")).ToList();
    }

    private static string Email(string value)
    {
        if (!MailAddress.TryCreate(value.Trim(), out var address) || address.Address != value.Trim())
            throw new ArgumentException("Use a complete, unambiguous email address.");
        return address.Address.ToLowerInvariant();
    }

    private static DateTimeOffset? Utc(JsonObject args, string name)
    {
        var value = Optional(args, name);
        if (value == null) return null;
        if (!value.Contains('T') || !(value.EndsWith('Z') || value.LastIndexOfAny(['+', '-']) > value.IndexOf('T')))
            throw new ArgumentException($"{name} needs an explicit UTC offset.");
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
    }

    private static string Id(JsonObject args, string key)
    {
        var id = Guid.Parse(Text(args, key, 36));
        return id == Guid.Empty ? throw new ArgumentException($"{key} cannot be an empty ID.") : id.ToString("D");
    }
    private static string? Optional(JsonObject args, string key) => args[key]?.GetValue<string>() is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private static string Text(JsonObject args, string key, int maximum, bool allowEmpty = false)
    {
        var value = Optional(args, key) ?? string.Empty;
        if ((!allowEmpty && value.Length == 0) || value.Length > maximum)
            throw new ArgumentException($"{key} must contain {(allowEmpty ? 0 : 1)} to {maximum} characters.");
        return value;
    }
    private static JsonNode ParseWorkResult(string result)
    {
        if (result.StartsWith("Error", StringComparison.Ordinal))
            throw new InvalidOperationException(result);
        return JsonNode.Parse(result) ?? throw new InvalidOperationException("The work-item store returned no result.");
    }
    private static string Json(object value) => JsonSerializer.Serialize(value, StandingJobStore.Json);
    private static JsonNode Tool(string name, string description, string properties, string[] required)
    {
        var fields = new JsonArray();
        foreach (var field in required) fields.Add(field);
        return new JsonObject
        {
            ["type"] = "function", ["name"] = name, ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = JsonNode.Parse(properties),
                ["required"] = fields, ["additionalProperties"] = false
            }
        };
    }
}
