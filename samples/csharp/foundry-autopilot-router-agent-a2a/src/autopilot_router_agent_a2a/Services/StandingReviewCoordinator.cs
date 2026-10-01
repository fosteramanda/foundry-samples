using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;

namespace WorkstreamManager.Services;

internal sealed class StandingReviewCoordinator(
    IStandingJobStore store, string partition, ILogger logger, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly Regex Reference = new(@"\[job:([a-fA-F0-9-]{36})\]", RegexOptions.CultureInvariant);

    internal bool IsAvailable => store.IsAvailable;
    internal string Partition => partition;
    internal DateTimeOffset Now => _clock.GetUtcNow();
    internal static string Marker(string id) => $"[job:{Guid.Parse(id):D}]";
    internal static string? ReferencedJob(string? text)
    {
        var match = Reference.Match(text ?? string.Empty);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var id) ? id.ToString("D") : null;
    }

    internal async Task<StandingJob> GetAsync(string id, StandingJobCaller caller)
    {
        var row = await store.ReadAsync(partition, StandingJobStore.JobKey(id))
            ?? throw new InvalidOperationException("Standing job not found.");
        var job = StandingJobStore.Value<StandingJob>(row);
        if (!caller.IsManager && !job.Allows(caller.Id))
            throw new UnauthorizedAccessException("This actor is not a participant in the standing job.");
        return job;
    }

    internal async Task<IReadOnlyList<StandingJob>> ListAsync(StandingJobCaller caller)
    {
        var rows = await store.ListAsync(partition, "job-");
        return rows.Select(StandingJobStore.Value<StandingJob>)
            .Where(job => caller.IsManager || job.Allows(caller.Id)).ToList();
    }

    internal async Task<IReadOnlyList<StandingJob>> FindAsync(StandingJobCaller caller, string? text, string? binding)
    {
        var explicitId = ReferencedJob(text);
        if (explicitId != null)
        {
            var job = await GetAsync(explicitId, caller);
            if (binding?.StartsWith("word:", StringComparison.Ordinal) == true
                && !job.Bindings.Contains(binding, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("This Word document is not an approved collaboration binding for the referenced job.");
            return [job];
        }
        if (string.IsNullOrWhiteSpace(binding))
            return [];
        return (await ListAsync(caller))
            .Where(job => job.Bindings.Contains(binding, StringComparer.Ordinal)).ToList();
    }

    internal async Task CreateAsync(StandingJob job, StandingJobCaller caller)
    {
        RequireManager(caller);
        job.ManagerId = caller.Id;
        job.CreatedUtc = job.UpdatedUtc = Now;
        job.Paused = true;
        if (!await store.TryAddAsync(StandingJobStore.Row(partition, StandingJobStore.JobKey(job.Id), job)))
            throw new InvalidOperationException("That standing job already exists; read it before changing it.");
    }

    internal async Task<StandingJob> ChangeAsync(string id, StandingJobCaller caller, Action<StandingJob> change, long? expectedRevision = null)
    {
        RequireManager(caller);
        var row = await store.ReadAsync(partition, StandingJobStore.JobKey(id))
            ?? throw new InvalidOperationException("Standing job not found.");
        var job = StandingJobStore.Value<StandingJob>(row);
        if (expectedRevision.HasValue && job.Revision != expectedRevision)
            throw new InvalidOperationException("The manager changed the job while this operation was in progress.");
        change(job);
        job.ManagerId = caller.ManagerId;
        job.Revision++;
        job.UpdatedUtc = Now;
        row.Data = JsonSerializer.Serialize(job, StandingJobStore.Json);
        if (!await store.TryReplaceAsync(row, row.ETag))
            throw new InvalidOperationException("The job changed concurrently. Read the current job before retrying.");
        return job;
    }

    internal async Task<StandingJobEvent> CaptureAsync(string id, StandingJobCaller caller, StandingJobEvent input)
    {
        var job = await GetAsync(id, caller);
        if (job.Paused && (input.Kind is not ("mandate" or "configuration") || !caller.IsManager))
            throw new InvalidOperationException("The standing job is paused.");
        if (!input.IsHuman || input.ActorId != caller.Id)
            throw new UnauthorizedAccessException("A scheduled run cannot manufacture human evidence.");
        if (string.IsNullOrWhiteSpace(input.Id) || input.Content.Length > 8000)
            throw new ArgumentException("The event needs a stable ID and at most 8,000 characters of source text.");
        var key = StandingJobStore.Key(id, "event", input.Id);
        var row = StandingJobStore.Row(partition, key, input);
        if (!await store.TryAddAsync(row))
        {
            var existing = StandingJobStore.Value<StandingJobEvent>(
                await store.ReadAsync(partition, key) ?? throw new InvalidOperationException("The event receipt disappeared."));
            if (existing.ActorId != input.ActorId || existing.Content != input.Content || existing.Binding != input.Binding)
                throw new InvalidOperationException("The same event ID was received with different evidence.");
            return existing;
        }
        return input;
    }

    internal async Task<StandingJobEvent> EvidenceAsync(string id, string eventId, string quote)
    {
        var row = await store.ReadAsync(partition, StandingJobStore.Key(id, "event", eventId))
            ?? throw new InvalidOperationException("The cited human event is not recorded for this job.");
        var evidence = StandingJobStore.Value<StandingJobEvent>(row);
        if (!evidence.IsHuman || string.IsNullOrWhiteSpace(quote)
            || !evidence.Content.Contains(quote, StringComparison.Ordinal))
            throw new ArgumentException("Evidence must quote the recorded human input verbatim.");
        return evidence;
    }

    internal async Task RecordInputAsync(string id, StandingJobCaller caller, StandingJobInput input)
    {
        var job = await GetAsync(id, caller);
        if (input.State is not ("missing" or "received" or "disputed"))
            throw new ArgumentException("Input state must be missing, received or disputed.");
        if (!job.Allows(input.OwnerId))
            throw new UnauthorizedAccessException("The input owner must be a job participant.");
        if (input.State != "missing")
        {
            var evidence = await EvidenceAsync(id, input.EvidenceEventId, input.Content);
            if (evidence.ActorId != input.OwnerId && evidence.ActorId != job.ManagerId)
                throw new UnauthorizedAccessException("Only the input owner or manager can provide its evidence.");
        }
        else if (!caller.IsManager)
        {
            throw new UnauthorizedAccessException("Only the manager can assign a new missing input.");
        }
        var key = StandingJobStore.Key(id, "input", input.Key);
        var prior = await store.ReadAsync(partition, key);
        if (prior != null)
        {
            var current = StandingJobStore.Value<StandingJobInput>(prior);
            if (current.OwnerId != input.OwnerId && !caller.IsManager)
                throw new UnauthorizedAccessException("Only the manager can reassign an input.");
            if (current with { UpdatedUtc = input.UpdatedUtc } == input)
                return;
            var replacement = StandingJobStore.Row(partition, key, input with { UpdatedUtc = Now });
            if (!await store.TryReplaceAsync(replacement, prior.ETag))
                throw new InvalidOperationException("The input changed concurrently; read it before retrying.");
        }
        else if (!await store.TryAddAsync(StandingJobStore.Row(partition, key, input with { UpdatedUtc = Now })))
        {
            throw new InvalidOperationException("That input was added concurrently; read it before retrying.");
        }
    }

    internal async Task<StandingJobDecision> RecordDecisionAsync(
        string id, StandingJobCaller caller, string eventId, string statement, string rationale, string? supersedes)
    {
        var job = await GetAsync(id, caller);
        var evidence = await EvidenceAsync(id, eventId, statement);
        if (evidence.ActorId != job.ManagerId || evidence.Kind is "mandate" or "configuration")
            throw new UnauthorizedAccessException("A confirmed decision must quote the manager's recorded input.");
        if (!string.IsNullOrWhiteSpace(rationale) && !evidence.Content.Contains(rationale, StringComparison.Ordinal))
            throw new ArgumentException("The rationale must also quote the same source; leave it empty if no rationale was given.");
        if (!string.IsNullOrEmpty(supersedes)
            && await store.ReadAsync(partition, StandingJobStore.Key(id, "decision", supersedes)) == null)
            throw new ArgumentException("The decision being corrected does not exist in this job.");
        var decisionId = StandingJobStore.Hash(eventId + "\n" + statement + "\n" + supersedes)[..24];
        var decision = new StandingJobDecision(decisionId, statement, rationale, eventId,
            evidence.ActorId, supersedes, Now);
        var key = StandingJobStore.Key(id, "decision", decisionId);
        if (!await store.TryAddAsync(StandingJobStore.Row(partition, key, decision)))
            return StandingJobStore.Value<StandingJobDecision>(
                await store.ReadAsync(partition, key) ?? throw new InvalidOperationException("Decision receipt disappeared."));
        return decision;
    }

    internal async Task<IReadOnlyList<T>> RecordsAsync<T>(string id, string kind)
    {
        var rows = await store.ListAsync(partition, StandingJobStore.Prefix(id, kind));
        return rows.Select(StandingJobStore.Value<T>).ToList();
    }

    internal async Task<object> SnapshotAsync(string id, StandingJobCaller caller)
    {
        var job = await GetAsync(id, caller);
        var events = await RecordsAsync<StandingJobEvent>(id, "event");
        return new
        {
            job,
            inputs = await RecordsAsync<StandingJobInput>(id, "input"),
            decisions = await RecordsAsync<StandingJobDecision>(id, "decision"),
            receipts = await RecordsAsync<StandingJobReceipt>(id, "receipt"),
            briefs = await RecordsAsync<StandingJobBrief>(id, "brief"),
            nowUtc = Now,
            attention = (await RecordsAsync<StandingJobInput>(id, "input"))
                .Where(item => item.State is "missing" or "disputed")
                .Select(item => new { item.Key, item.OwnerId, item.State, item.Title }),
            recentEvents = events.OrderByDescending(item => item.ReceivedUtc).Take(20),
            olderEventCount = Math.Max(0, events.Count - 20)
        };
    }

    internal static string Phase(StandingJob job, IReadOnlyList<StandingJobInput> inputs,
        IReadOnlyList<StandingJobDecision> decisions, int openCommitments, int totalCommitments)
    {
        if (job.Paused) return "paused";
        if (inputs.Any(input => input.State == "disputed")) return "needs_manager";
        if (inputs.Any(input => input.State == "missing")) return "collecting_inputs";
        if (openCommitments > 0) return "following_up";
        if (totalCommitments > 0) return "review_complete";
        return decisions.Count > 0 ? "needs_commitments" : inputs.Count > 0 ? "decision_ready" : "collecting_inputs";
    }

    internal async Task<string> StateFingerprintAsync(string id, bool includeSpecialists = true)
    {
        var inputs = await RecordsAsync<StandingJobInput>(id, "input");
        var decisions = await RecordsAsync<StandingJobDecision>(id, "decision");
        var jobRow = await store.ReadAsync(partition, StandingJobStore.JobKey(id))
            ?? throw new InvalidOperationException("Standing job not found.");
        var job = StandingJobStore.Value<StandingJob>(jobRow);
        var specialists = includeSpecialists
            ? (await RecordsAsync<StandingJobReceipt>(id, "receipt"))
                .Where(item => item.Operation == "delegate" && item.State == "accepted")
                .Select(item => new { item.Key, AnswerHash = StandingJobStore.Hash(item.Detail) })
                .OrderBy(item => item.Key).ToArray()
            : null;
        return StandingJobStore.Hash(JsonSerializer.Serialize(new
        {
            job.Revision,
            inputs = inputs.OrderBy(item => item.Key),
            decisions = decisions.OrderBy(item => item.Id),
            changes = (await RecordsAsync<StandingBriefChange>(id, "brief_change")).OrderBy(item => item.EvidenceEventId),
            specialists
        }, StandingJobStore.Json));
    }

    internal async Task RecordBriefChangeAsync(string id, StandingJobCaller caller, string eventId, string quote)
    {
        await GetAsync(id, caller);
        await EvidenceAsync(id, eventId, quote);
        await store.TryAddAsync(StandingJobStore.Row(partition,
            StandingJobStore.Key(id, "brief_change", eventId + "\n" + quote), new StandingBriefChange(eventId, quote)));
    }

    internal async Task<StandingBriefDraft> SaveDraftAsync(string id, StandingJobCaller caller, StandingBriefDraft draft)
    {
        await GetAsync(id, caller);
        var key = StandingJobStore.Key(id, "brief_draft", draft.FactsFingerprint);
        if (await store.TryAddAsync(StandingJobStore.Row(partition, key, draft))) return draft;
        return StandingJobStore.Value<StandingBriefDraft>(
            await store.ReadAsync(partition, key) ?? throw new InvalidOperationException("The saved publication draft disappeared."));
    }

    internal async Task<StandingBriefDraft> ReadDraftAsync(string id, string fingerprint) =>
        StandingJobStore.Value<StandingBriefDraft>(
            await store.ReadAsync(partition, StandingJobStore.Key(id, "brief_draft", fingerprint))
                ?? throw new InvalidOperationException("The prior publication has no saved draft. Reconcile it explicitly; no file was imported by name."));

    internal async Task RegisterBriefAsync(string id, StandingJobCaller caller, StandingJobBrief brief)
    {
        await GetAsync(id, caller);
        if (!Guid.TryParse(brief.DocumentId, out var document) || string.IsNullOrWhiteSpace(brief.ItemId))
            throw new ArgumentException("The brief must have a real document receipt.");
        var key = StandingJobStore.Key(id, "brief", brief.FactsFingerprint);
        var versions = await RecordsAsync<StandingJobBrief>(id, "brief");
        if (versions.Any(existing => existing.Version == brief.Version
            && (existing.ItemId != brief.ItemId || existing.FactsFingerprint != brief.FactsFingerprint)))
            throw new InvalidOperationException("That brief version is already bound to a different publication.");
        if (!await store.TryAddAsync(StandingJobStore.Row(partition, key, brief)))
        {
            var existing = StandingJobStore.Value<StandingJobBrief>(
                await store.ReadAsync(partition, key) ?? throw new InvalidOperationException("The brief record disappeared."));
            if (existing.ItemId != brief.ItemId || existing.DocumentId != brief.DocumentId || existing.Version != brief.Version)
                throw new InvalidOperationException("The facts revision is already bound to a different document.");
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var row = await store.ReadAsync(partition, StandingJobStore.JobKey(id))
                ?? throw new InvalidOperationException("The standing job disappeared.");
            var job = StandingJobStore.Value<StandingJob>(row);
            var binding = "word:" + document.ToString("D");
            if (job.Bindings.Contains(binding, StringComparer.Ordinal)) return;
            job.Bindings.Add(binding);
            row.Data = JsonSerializer.Serialize(job, StandingJobStore.Json);
            if (await store.TryReplaceAsync(row, row.ETag)) return;
        }
        throw new InvalidOperationException("The brief exists but its comment binding was not saved; reconcile before republishing.");
    }

    internal async Task<string?> TryBeginRunAsync(string id, StandingJobCaller caller, bool scheduled)
    {
        var row = await store.ReadAsync(partition, StandingJobStore.JobKey(id))
            ?? throw new InvalidOperationException("Standing job not found.");
        var job = StandingJobStore.Value<StandingJob>(row);
        if (!caller.IsManager && !job.Allows(caller.Id))
            throw new UnauthorizedAccessException("This actor is not a participant in the standing job.");
        if (job.ManagerId != caller.ManagerId)
            throw new UnauthorizedAccessException("The manager changed; the current manager must reauthorize this job.");
        if (job.Paused || (scheduled && job.NextCheckUtc > Now) || job.LeaseUntilUtc > Now)
            return null;
        var lease = Guid.NewGuid().ToString("N");
        job.LeaseId = lease;
        job.LeaseUntilUtc = Now.AddMinutes(15);
        row.Data = JsonSerializer.Serialize(job, StandingJobStore.Json);
        return await store.TryReplaceAsync(row, row.ETag) ? lease : null;
    }

    internal async Task EnsureRunAsync(string id, string? lease)
    {
        if (lease == null)
            return;
        var row = await store.ReadAsync(partition, StandingJobStore.JobKey(id))
            ?? throw new InvalidOperationException("Standing job disappeared.");
        var job = StandingJobStore.Value<StandingJob>(row);
        if (job.Paused || job.LeaseId != lease || job.LeaseUntilUtc <= Now)
            throw new InvalidOperationException("The job was paused or this run no longer owns the lease.");
        job.LeaseUntilUtc = Now.AddMinutes(15);
        row.Data = JsonSerializer.Serialize(job, StandingJobStore.Json);
        if (!await store.TryReplaceAsync(row, row.ETag))
            throw new InvalidOperationException("The job changed while renewing its run lease.");
    }

    internal async Task EndRunAsync(string id, string lease, bool succeeded = true, string detail = "")
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var row = await store.ReadAsync(partition, StandingJobStore.JobKey(id));
            if (row == null)
                return;
            var job = StandingJobStore.Value<StandingJob>(row);
            if (job.LeaseId != lease)
                return;
            job.LeaseId = null;
            job.LeaseUntilUtc = null;
            job.NextCheckUtc = Now;
            job.LastRunUtc = Now;
            job.LastRunStatus = succeeded ? "checked" : "failed";
            job.LastRunDetail = detail.Length > 1500 ? detail[..1500] : detail;
            row.Data = JsonSerializer.Serialize(job, StandingJobStore.Json);
            if (await store.TryReplaceAsync(row, row.ETag))
                return;
        }
        logger.LogWarning("Standing-job lease release conflicted repeatedly for {JobId}; its lease will expire.", id);
    }

    internal async Task<StandingJobReceipt> OnceAsync(
        string id, StandingJobCaller caller, string operation, string key, string payload,
        Func<Task<(bool? Accepted, string Detail)>> action, string? lease = null, string? scope = null,
        bool reconcileUncertain = false)
    {
        var job = await GetAsync(id, caller);
        if (job.Paused)
            throw new InvalidOperationException("The standing job is paused.");
        await EnsureRunAsync(id, lease);
        var receipt = new StandingJobReceipt
        {
            Key = key, Scope = scope ?? key, Operation = operation, PayloadHash = StandingJobStore.Hash(payload), CreatedUtc = Now
        };
        var rowKey = StandingJobStore.Key(id, "receipt", key);
        var scopeKey = StandingJobStore.Key(id, "action_scope", receipt.Scope);
        var scopeRow = await store.ReadAsync(partition, scopeKey);
        var reconciling = false;
        if (scopeRow != null)
        {
            var prior = StandingJobStore.Value<StandingJobReceipt>(scopeRow);
            if (prior.Key == key && prior.State == "uncertain" && reconcileUncertain)
            {
                if (prior.PayloadHash != receipt.PayloadHash)
                    throw new InvalidOperationException("Reconciliation must use the exact saved action payload.");
                receipt.CreatedUtc = prior.CreatedUtc;
                if (!await store.TryReplaceAsync(StandingJobStore.Row(partition, scopeKey, receipt), scopeRow.ETag))
                    throw new InvalidOperationException("Another run claimed this uncertain action for reconciliation.");
                reconciling = true;
            }
            else if (prior.Key == key || prior.State is "pending" or "uncertain")
                return prior;
            else if (!await store.TryReplaceAsync(StandingJobStore.Row(partition, scopeKey, receipt), scopeRow.ETag))
                throw new InvalidOperationException("Another run claimed this action scope; no action was attempted.");
        }
        else if (!await store.TryAddAsync(StandingJobStore.Row(partition, scopeKey, receipt)))
        {
            throw new InvalidOperationException("Another run claimed this action scope; no action was attempted.");
        }
        if (!reconciling && !await store.TryAddAsync(StandingJobStore.Row(partition, rowKey, receipt)))
        {
            var existing = StandingJobStore.Value<StandingJobReceipt>(
                await store.ReadAsync(partition, rowKey) ?? throw new InvalidOperationException("Action receipt disappeared."));
            logger.LogInformation("Standing-job action already attempted: job={JobId} operation={Operation} state={State}",
                id, operation, existing.State);
            await FinishScopeAsync(scopeKey, existing);
            return existing;
        }
        try
        {
            var result = await action();
            receipt.State = result.Accepted switch { true => "accepted", false => "failed", null => "uncertain" };
            receipt.Detail = result.Detail;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            receipt.State = "uncertain";
            receipt.Detail = "The action outcome is uncertain. Reconcile the destination before trying again.";
            logger.LogError(ex, "Standing-job action outcome is uncertain: job={JobId} operation={Operation}", id, operation);
        }
        receipt.CompletedUtc = Now;
        var stored = await store.ReadAsync(partition, rowKey)
            ?? throw new InvalidOperationException("The action receipt disappeared after the attempt.");
        var replacement = StandingJobStore.Row(partition, rowKey, receipt);
        if (!await store.TryReplaceAsync(replacement, stored.ETag))
            throw new InvalidOperationException("The action ran but its receipt could not be finalized; do not repeat it.");
        await FinishScopeAsync(scopeKey, receipt);
        return receipt;
    }

    internal async Task<StandingJobReceipt?> ScopeReceiptAsync(
        string id, StandingJobCaller caller, string scope)
    {
        await GetAsync(id, caller);
        var row = await store.ReadAsync(partition, StandingJobStore.Key(id, "action_scope", scope));
        return row == null ? null : StandingJobStore.Value<StandingJobReceipt>(row);
    }

    internal async Task CompleteUncertainAsync(
        string id, StandingJobCaller caller, string scope, string detail)
    {
        await GetAsync(id, caller);
        var scopeKey = StandingJobStore.Key(id, "action_scope", scope);
        var scopeRow = await store.ReadAsync(partition, scopeKey)
            ?? throw new InvalidOperationException("The uncertain action scope no longer exists.");
        var prior = StandingJobStore.Value<StandingJobReceipt>(scopeRow);
        if (prior.State == "accepted") return;
        if (prior.State != "uncertain")
            throw new InvalidOperationException("Only an uncertain external action can be reconciled.");
        prior.State = "accepted";
        prior.Detail = detail;
        prior.CompletedUtc = Now;
        var receiptKey = StandingJobStore.Key(id, "receipt", prior.Key);
        var receiptRow = await store.ReadAsync(partition, receiptKey)
            ?? throw new InvalidOperationException("The uncertain action receipt no longer exists.");
        if (!await store.TryReplaceAsync(StandingJobStore.Row(partition, receiptKey, prior), receiptRow.ETag))
            throw new InvalidOperationException("The reconciled action receipt changed concurrently.");
        scopeRow = await store.ReadAsync(partition, scopeKey)
            ?? throw new InvalidOperationException("The uncertain action scope disappeared during reconciliation.");
        if (StandingJobStore.Value<StandingJobReceipt>(scopeRow).Key != prior.Key)
            throw new InvalidOperationException("The action scope moved to another publication during reconciliation.");
        if (!await store.TryReplaceAsync(StandingJobStore.Row(partition, scopeKey, prior), scopeRow.ETag))
            throw new InvalidOperationException("The receipt was reconciled but its action scope changed concurrently.");
    }

    private async Task FinishScopeAsync(string key, StandingJobReceipt receipt)
    {
        var row = await store.ReadAsync(partition, key)
            ?? throw new InvalidOperationException("The action scope disappeared; reconcile before retrying.");
        var current = StandingJobStore.Value<StandingJobReceipt>(row);
        if (current.Key != receipt.Key || !await store.TryReplaceAsync(
                StandingJobStore.Row(partition, key, receipt), row.ETag))
            throw new InvalidOperationException("The action scope changed; reconcile before retrying.");
    }

    internal static void RequireManager(StandingJobCaller caller)
    {
        if (!caller.IsManager)
            throw new UnauthorizedAccessException("Only the current manager may change a standing mandate.");
    }
}
