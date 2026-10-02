using System.Text;
using System.Text.Json;
using Azure;
using Microsoft.Agents.Core.Errors;
using Microsoft.Agents.Core.Models;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Models;

namespace WorkstreamManager.Services;

internal sealed class DelegationCardCoordinator(
    AgentMetadata owner,
    IDelegationCardStore store,
    WorkIqA2AToolHandler router,
    string conversationId,
    string conversationJson,
    string ownerName,
    StandingJobCaller caller,
    Func<Activity, Task<string>> send,
    Func<Activity, Task> update,
    Func<string, Task> notify,
    Func<DelegationCardEntity, StandingJobCaller, Task>? validateJob,
    ILogger logger)
{
    private enum Verification { Verified, Pending, Unconfirmed }
    private string Partition => $"{owner.TenantId:D}:{owner.UserId:D}";
    internal bool HasReadablePlannerCard { get; private set; }

    internal async Task ObserveAsync(WorkIqA2AToolHandler.DelegationExchange exchange)
    {
        if (exchange.Starting)
        {
            if (exchange.Question.Length > 12_000)
                throw new InvalidOperationException("The delegation request is too large for a live card; use the full text response.");
            var card = new DelegationCardEntity
            {
                PartitionKey = Partition, RowKey = "card-" + exchange.Id,
                ConversationId = conversationId, ConversationJson = conversationJson,
                RequesterId = caller.Id, ManagerId = caller.ManagerId, OwnerAgentId = owner.AgentId.ToString("D"),
                OwnerName = ownerName, AgentId = exchange.AgentId, AgentName = exchange.DisplayName,
                ParentScope = exchange.ParentScope, ContextScope = exchange.ContextScope,
                ContextId = exchange.ContextId ?? "", ContextRevision = exchange.Revision,
                Question = exchange.Question, CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow
            };
            await store.AddAsync(card);
            card.ActivityId = await send(DelegationCardPresentation.Activity(card));
            if (string.IsNullOrWhiteSpace(card.ActivityId))
                throw new InvalidOperationException("The card was sent without a usable message receipt. It will not be sent again blindly.");
            if (!await store.ReplaceAsync(card))
                throw new InvalidOperationException("The new delegation card changed before its message receipt was saved.");
            logger.LogInformation("Native delegation card created. card={CardId} message={MessageId} agent={AgentId}",
                card.RowKey, card.ActivityId, card.AgentId);
            return;
        }

        var current = await store.GetAsync(Partition, exchange.Id)
            ?? throw new InvalidOperationException("The delegation card receipt was not saved.");
        current.ContextId = exchange.ContextId ?? "";
        current.ContextRevision = exchange.Revision;
        await ApplyResultAsync(current, exchange.Answer, exchange.Outcome, allowApproval: true);
    }

    internal async Task HandleActionAsync(object value)
    {
        DelegationCardEntity? card = null;
        var attemptedSave = false;
        try
        {
            var action = DelegationCardPresentation.ReadAction(value);
            card = await store.GetAsync(Partition, action.Id)
                ?? throw new InvalidOperationException("This approval card is no longer available.");
            Authorize(card, owner, conversationId, caller);
            if (validateJob != null) await validateJob(card, caller);
            if (card.State != DelegationCardStates.Review)
            {
                await notify(card.State == DelegationCardStates.Saved
                    ? "This plan was already saved. No second Save request was sent."
                    : "This card is not awaiting approval. No Save request was sent.");
                return;
            }
            var preview = JsonSerializer.Deserialize<PlannerCardResult>(card.PreviewJson)
                ?? throw new InvalidOperationException("The saved preview is unavailable.");
            if (!preview.IsPreview || !preview.CanApprove || string.IsNullOrWhiteSpace(card.ContextId)
                || string.IsNullOrWhiteSpace(card.ContextRevision))
                throw new InvalidOperationException("This preview cannot be approved through this card.");

            if (action.Action == "decline")
            {
                card.State = DelegationCardStates.Declined;
                card.Detail = "No Save request was sent to the specialist.";
                if (!await store.ReplaceAsync(card))
                    throw new InvalidOperationException("Another action already changed this card.");
                await UpdateAsync(card);
                return;
            }

            if (await store.GetRevisionAsync(Partition, card.ContextScope) != card.ContextRevision)
            {
                card.State = DelegationCardStates.Stale;
                card.Detail = "The specialist conversation changed. Ask for a fresh preview before approving.";
                if (await store.ReplaceAsync(card)) await UpdateAsync(card);
                await notify(card.Detail);
                return;
            }
            card.State = DelegationCardStates.Saving;
            card.ApprovalIssued = true;
            card.Detail = "Sending your approval to the same specialist conversation.";
            if (!await store.ReplaceAsync(card))
                throw new InvalidOperationException("Another action already claimed this approval. No additional Save request was sent.");
            logger.LogInformation("Delegation approval claimed. card={CardId} actor={ActorId} agent={AgentId}",
                card.RowKey, caller.Id, card.AgentId);
            await UpdateAsync(card);
            attemptedSave = true;
            var result = await router.AskBoundAsync(card, "Save");
            card.ContextRevision = result.Revision;
            card.ContextId = result.ContextId ?? card.ContextId;
            await ApplyResultAsync(card, result.Answer, result.Outcome, allowApproval: false, expectedPreview: preview);
            await notify(card.State == DelegationCardStates.Saved
                ? card.AgentName + " saved the reviewed plan and tasks. Their stored IDs and titles were read back before the card was marked saved."
                : card.State is DelegationCardStates.Pending or DelegationCardStates.Verifying
                    ? "The specialist accepted the approval and is still working. This is not a saved-result confirmation."
                    : card.Detail);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException
            or RequestFailedException or HttpRequestException or ErrorResponseException or JsonException
            or FormatException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Delegation card action did not complete. card={CardId} saveAttempted={SaveAttempted}",
                card?.RowKey, attemptedSave);
            if (card != null && attemptedSave)
            {
                card.State = DelegationCardStates.Unconfirmed;
                card.Detail = "The result could not be confirmed. No automatic repeat will be sent.";
                try
                {
                    if (await store.ReplaceAsync(card)) await UpdateAsync(card);
                }
                catch (Exception deliveryError) when (deliveryError is RequestFailedException or HttpRequestException
                    or ErrorResponseException or InvalidOperationException or OperationCanceledException)
                {
                    logger.LogError(deliveryError, "Could not update the uncertain approval card {CardId}.", card.RowKey);
                }
            }
            await notify(attemptedSave
                ? "The approval result is not confirmed. Do not repeat the save; the specialist may already have acted."
                : "The card action was not accepted. " + ex.Message);
        }
    }

    internal static void Authorize(
        DelegationCardEntity card, AgentMetadata owner, string conversationId, StandingJobCaller caller)
    {
        if (card.PartitionKey != $"{owner.TenantId:D}:{owner.UserId:D}"
            || card.OwnerAgentId != owner.AgentId.ToString("D") || card.ConversationId != conversationId)
            throw new UnauthorizedAccessException("This card does not belong to this conversation.");
        if (card.ManagerId != caller.ManagerId || (caller.Id != card.RequesterId && !caller.IsManager))
            throw new UnauthorizedAccessException("Only the original requester or the current manager can approve this work.");
    }

    internal async Task CompletePendingAsync(DelegationCardEntity card, string answer, bool timedOut)
    {
        if (timedOut)
        {
            card.State = DelegationCardStates.Unconfirmed;
            card.Detail = "The specialist did not return a completed result in time. No work was resubmitted.";
            if (!await store.ReplaceAsync(card)) throw new InvalidOperationException("The pending card changed.");
            await UpdateAsync(card);
            return;
        }
        await ApplyResultAsync(card, answer, WorkIqA2AToolHandler.HasCompletedAnswer(answer)
            ? WorkIqA2AToolHandler.DelegationOutcome.Answered : WorkIqA2AToolHandler.DelegationOutcome.NoAnswer,
            allowApproval: !card.ApprovalIssued,
            expectedPreview: string.IsNullOrEmpty(card.ReviewedPreviewJson)
                ? null : JsonSerializer.Deserialize<PlannerCardResult>(card.ReviewedPreviewJson));
    }

    internal async Task CompleteVerificationAsync(DelegationCardEntity card, string answer, bool timedOut)
    {
        var saved = string.IsNullOrEmpty(card.PreviewJson) ? null : JsonSerializer.Deserialize<PlannerCardResult>(card.PreviewJson);
        var expected = string.IsNullOrEmpty(card.ReviewedPreviewJson)
            ? saved : JsonSerializer.Deserialize<PlannerCardResult>(card.ReviewedPreviewJson);
        var verified = !timedOut && saved != null && expected != null && VerifyReadback(answer, saved, expected);
        card.State = verified ? DelegationCardStates.Saved : DelegationCardStates.Unconfirmed;
        card.Detail = verified
            ? "The plan and tasks were saved and checked. Review them here; opening Planner uses your existing plan permissions."
            : "The independent check did not confirm every item. No Save request was repeated.";
        if (!await store.ReplaceAsync(card)) throw new InvalidOperationException("The verification card changed.");
        await UpdateAsync(card);
    }

    private async Task ApplyResultAsync(
        DelegationCardEntity card, string answer, WorkIqA2AToolHandler.DelegationOutcome outcome,
        bool allowApproval, PlannerCardResult? expectedPreview = null)
    {
        if (answer.Length > 30_000)
        {
            card.State = DelegationCardStates.Unconfirmed;
            card.Detail = "The complete response is too large for this card. Use the original text reply; no partial preview can be approved.";
            card.Answer = "";
            card.PreviewJson = "";
        }
        else
        {
            card.Answer = answer;
            card.State = outcome switch
            {
                WorkIqA2AToolHandler.DelegationOutcome.Pending => DelegationCardStates.Pending,
                WorkIqA2AToolHandler.DelegationOutcome.NoAnswer => card.ApprovalIssued
                    ? DelegationCardStates.Unconfirmed : DelegationCardStates.Failed,
                _ => DelegationCardStates.Response
            };
            card.Detail = outcome == WorkIqA2AToolHandler.DelegationOutcome.Pending
                ? "The handoff was accepted. A finished result has not arrived yet."
                : outcome == WorkIqA2AToolHandler.DelegationOutcome.NoAnswer
                    ? "No successful specialist result was confirmed. Do not repeat an uncertain save." : "The specialist returned the response below.";
            PlannerCardResult? planner = null;
            try { planner = DelegationCardPresentation.ReadPlanner(answer); }
            catch (Exception ex) when (ex is FormatException or JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                logger.LogWarning(ex, "A returned Planner preview cannot be approved safely. card={CardId}", card.RowKey);
                card.Detail = "The specialist response could not be interpreted as a complete reviewable preview. No approval button is available.";
            }
            card.PreviewJson = planner == null ? "" : JsonSerializer.Serialize(planner);
            if (planner != null && outcome == WorkIqA2AToolHandler.DelegationOutcome.Answered)
            {
                if (planner.IsPreview)
                {
                    var currentRevision = await store.GetRevisionAsync(Partition, card.ContextScope);
                    if (allowApproval && planner.CanApprove && currentRevision == card.ContextRevision
                        && !string.IsNullOrWhiteSpace(card.ContextId) && !string.IsNullOrWhiteSpace(card.ContextRevision))
                    {
                        card.State = DelegationCardStates.Review;
                        card.ReviewedPreviewJson = card.PreviewJson;
                        card.Detail = "Review the complete plan and tasks below. Nothing has been saved. Approve and save continues this exact specialist conversation.";
                    }
                    else
                    {
                        var stale = currentRevision != card.ContextRevision;
                        card.State = stale ? DelegationCardStates.Stale : DelegationCardStates.Unconfirmed;
                        card.Detail = stale
                            ? "The specialist conversation changed. A fresh preview is required before approval."
                            : !planner.CanApprove
                                ? "This preview contains details the card cannot safely approve. Review the original response; no Save was sent."
                                : "The specialist returned another preview, not a saved plan. No automatic Save will be repeated.";
                    }
                }
                else if (planner.PlanId != null && planner.Tasks.Count > 0 && planner.Tasks.All(task => task.Id != null))
                {
                    card.PlanId = planner.PlanId;
                    card.PlanUrl = planner.PlanUrl ?? "";
                    card.TasksJson = JsonSerializer.Serialize(planner.Tasks);
                    var expected = expectedPreview ?? planner;
                    var verification = await VerifySavedAsync(card, planner, expected);
                    card.State = verification == Verification.Verified ? DelegationCardStates.Saved
                        : verification == Verification.Pending ? DelegationCardStates.Verifying : DelegationCardStates.Unconfirmed;
                    card.Detail = verification == Verification.Verified
                        ? "The plan and tasks were saved and checked. Review them here; opening Planner uses your existing plan permissions."
                        : verification == Verification.Pending
                            ? "The specialist returned plan links. An independent read is still checking the stored items; this is not yet a saved-result confirmation."
                            : "The specialist returned plan links, but an independent read did not confirm every reviewed item. Inspect the returned plan before treating it as complete.";
                }
            }
        }
        try { _ = DelegationCardPresentation.Build(card); }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Delegation card exceeded its display budget. card={CardId}", card.RowKey);
            card.State = DelegationCardStates.Unconfirmed;
            card.Detail = "The complete response exceeds this card's display budget. Use the original text reply. No partial preview can be approved.";
            card.PreviewJson = "";
            card.Answer = "";
            card.Question = "See the original request in this conversation.";
        }
        if (!await store.ReplaceAsync(card))
            throw new InvalidOperationException("The delegation card changed before its result was stored.");
        await UpdateAsync(card);
        HasReadablePlannerCard |= !string.IsNullOrEmpty(card.PreviewJson);
    }

    private async Task<Verification> VerifySavedAsync(
        DelegationCardEntity card, PlannerCardResult saved, PlannerCardResult expected)
    {
        var ids = saved.Tasks.Select(task => task.Id).ToArray();
        var question = "Read only. Retrieve existing Planner plan " + saved.PlanId
            + " and these existing task IDs: " + JsonSerializer.Serialize(ids)
            + ". Do not create, modify, approve, share, assign or delete anything. "
            + "Return one JSON object containing planId, planTitle and tasks, where each task has id and title. "
            + "Use only the actually stored names and IDs. If an item cannot be read, return an error instead of guessing. "
            + "Do not return a creation preview.";
        var actual = await router.AskVerificationAsync(card, question);
        if (actual.Outcome == WorkIqA2AToolHandler.DelegationOutcome.Pending)
            return Verification.Pending;
        return actual.Outcome == WorkIqA2AToolHandler.DelegationOutcome.Answered
            && VerifyReadback(actual.Answer, saved, expected) ? Verification.Verified : Verification.Unconfirmed;
    }

    internal static bool VerifyReadback(string answer, PlannerCardResult saved, PlannerCardResult expected)
    {
        var offset = answer.IndexOf('{');
        if (offset < 0 || answer.Length > 30_000 || saved.Tasks.Count != expected.Tasks.Count) return false;
        if (!saved.Tasks.Select(task => task.Title).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Tasks.Select(task => task.Title).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return false;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(answer[offset..]));
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (!root.TryGetProperty("planId", out var planId) || planId.GetString() != saved.PlanId
                || !root.TryGetProperty("planTitle", out var title) || title.GetString() != expected.Title
                || !root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array
                || tasks.GetArrayLength() != saved.Tasks.Count) return false;
            var rows = tasks.EnumerateArray().ToArray();
            for (var index = 0; index < saved.Tasks.Count; index++)
            {
                var matches = rows.Where(row => row.TryGetProperty("id", out var id) && id.GetString() == saved.Tasks[index].Id).ToArray();
                if (matches.Length != 1 || !matches[0].TryGetProperty("title", out var taskTitle)
                    || taskTitle.GetString() != saved.Tasks[index].Title) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task UpdateAsync(DelegationCardEntity card)
    {
        if (string.IsNullOrWhiteSpace(card.ActivityId))
            throw new InvalidOperationException("The existing card has no message receipt. It will not be sent again blindly.");
        await update(DelegationCardPresentation.Activity(card));
        logger.LogInformation("Native delegation card updated. card={CardId} message={MessageId} state={State} agent={AgentId}",
            card.RowKey, card.ActivityId, card.State, card.AgentId);
    }
}
