using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Data.Tables;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WorkstreamManager.AgentLogic;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Services;
using Xunit;

namespace WorkstreamManagerAgent.Tests;

public class StandingJobTests
{
    [Fact]
    public async Task JobStateIsSharedByParticipantsButIsolatedByInstance()
    {
        var h = await Harness.CreateAsync();
        Assert.Equal(h.Job.Title, (await h.Coordinator.GetAsync(h.Job.Id, h.Owner)).Title);
        Assert.Empty(await h.Coordinator.ListAsync(new StandingJobCaller(Guid.NewGuid().ToString(), h.Manager.Id, "outsider@example.com")));
        var otherInstance = new StandingReviewCoordinator(h.Store,
            StandingJobStore.Partition(Guid.NewGuid(), Guid.NewGuid()), NullLogger.Instance, h.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => otherInstance.GetAsync(h.Job.Id, h.Manager));
        var restarted = new StandingReviewCoordinator(h.Store, h.Partition, NullLogger.Instance, h.Clock);
        Assert.Equal(h.Job.Mandate, (await restarted.GetAsync(h.Job.Id, h.Manager)).Mandate);
    }

    [Fact]
    public async Task OnlyRealManagerTurnsCanConfigureTheMandate()
    {
        var h = await Harness.CreateAsync();
        h.SetTurn(h.Owner, "owner-event", "Change the mandate.");
        Assert.DoesNotContain(h.Tools.GetToolDefinitions(), item => item["name"]!.GetValue<string>() == "update_standing_job");
        h.SetTurn(h.Manager, "timer", "Change the mandate.", automatic: true);
        Assert.DoesNotContain(h.Tools.GetToolDefinitions(), item => item["name"]!.GetValue<string>() == "create_standing_job");
        Assert.DoesNotContain(h.Tools.GetToolDefinitions(), item => item["name"]!.GetValue<string>() == "list_standing_jobs");
        Assert.Null(await h.Tools.TryExecuteAsync("update_standing_job", Args(new { job_id = h.Job.Id, mandate = "New job" })));
        Assert.Equal(h.Job.Mandate, (await h.Coordinator.GetAsync(h.Job.Id, h.Manager)).Mandate);
        Assert.False(new StandingJobCaller("", "", null).IsManager);
    }

    [Fact]
    public async Task FailedScheduleLeavesTheSavedJobPaused()
    {
        var h = new Harness { ScheduleWorks = false };
        h.SetTurn(h.Manager, "create-event", "Own the review.");
        var result = await h.Call("create_standing_job", new { name = "Review", mandate = "Own the review." });
        Assert.False(result["success"]!.GetValue<bool>());
        var job = Assert.Single(await h.Coordinator.ListAsync(h.Manager));
        Assert.True(job.Paused);
        Assert.Equal(1, h.ScheduleCalls);
    }

    [Fact]
    public async Task ResumeMigratesLegacyRoutineNameBelowTheServiceLimit()
    {
        var h = await Harness.CreateAsync();
        var legacy = await h.Coordinator.ChangeAsync(h.Job.Id, h.Manager, job =>
        {
            job.Paused = true;
            job.RoutineName = "standing-" + Guid.Parse(job.Id).ToString("N");
        });
        h.SetTurn(h.Manager, "resume-event", "Resume the review.");

        var result = await h.Call("update_standing_job", new { job_id = legacy.Id, enabled = true });

        Assert.True(result["paused"]!.GetValue<bool>() is false);
        var updated = await h.Coordinator.GetAsync(legacy.Id, h.Manager);
        Assert.Equal(RoutineToolHandler.BuildStandingRoutineName(legacy.Id), updated.RoutineName);
        Assert.True((updated.RoutineName.Length * 2) + 1 <= 80);
        Assert.Equal(1, h.ScheduleCalls);
    }

    [Fact]
    public async Task ParticipantIdAndEmailMustAgreeWithTheDirectory()
    {
        var h = new Harness();
        h.SetTurn(h.Manager, "create-event", "Own the review.");
        var result = await h.Call("create_standing_job", new
        {
            name = "Review", mandate = "Own the review.",
            members = new[] { new { id = h.Owner.Id, email = "wrong@example.com" } }
        });
        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Empty(await h.Coordinator.ListAsync(h.Manager));
        Assert.Equal(0, h.ScheduleCalls);
    }

    [Fact]
    public async Task HumanEventsAreImmutableAndRedeliveryDoesNotDuplicateThem()
    {
        var h = await Harness.CreateAsync();
        var source = h.Source(h.Owner, "owner-event", "Forecast delivered.");
        await h.Coordinator.CaptureAsync(h.Job.Id, h.Owner, source);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Coordinator.CaptureAsync(h.Job.Id, h.Owner, source with { ReceivedUtc = h.Clock.GetUtcNow() });
        Assert.Single(await h.Coordinator.RecordsAsync<StandingJobEvent>(h.Job.Id, "event"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Coordinator.CaptureAsync(h.Job.Id, h.Owner, source with { Content = "Different evidence." }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            h.Coordinator.CaptureAsync(h.Job.Id, h.Manager, source));
    }

    [Fact]
    public async Task DecisionsNeedTheManagersLiteralSourceAndPreserveCorrections()
    {
        var h = await Harness.CreateAsync();
        var evidence = h.Source(h.Manager, "decision-one", "Approve the review. The forecast is ready.");
        await h.Coordinator.CaptureAsync(h.Job.Id, h.Manager, evidence);
        var first = await h.Coordinator.RecordDecisionAsync(h.Job.Id, h.Manager,
            evidence.Id, "Approve the review.", "The forecast is ready.", null);
        var duplicate = await h.Coordinator.RecordDecisionAsync(h.Job.Id, h.Manager,
            evidence.Id, "Approve the review.", "The forecast is ready.", null);
        Assert.Equal(first.Id, duplicate.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Coordinator.RecordDecisionAsync(
            h.Job.Id, h.Manager, evidence.Id, "Approve a different project.", "", null));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Coordinator.RecordDecisionAsync(
            h.Job.Id, h.Manager, evidence.Id, "Approve the review.", "Invented rationale.", null));
        var ownerEvidence = h.Source(h.Owner, "owner-decision", "Approve the review.");
        await h.Coordinator.CaptureAsync(h.Job.Id, h.Owner, ownerEvidence);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Coordinator.RecordDecisionAsync(
            h.Job.Id, h.Manager, ownerEvidence.Id, "Approve the review.", "", null));
        var correction = h.Source(h.Manager, "decision-two", "Defer the review.");
        await h.Coordinator.CaptureAsync(h.Job.Id, h.Manager, correction);
        var second = await h.Coordinator.RecordDecisionAsync(h.Job.Id, h.Manager,
            correction.Id, correction.Content, "", first.Id);
        Assert.Equal(first.Id, second.Supersedes);
        Assert.Equal(2, (await h.Coordinator.RecordsAsync<StandingJobDecision>(h.Job.Id, "decision")).Count);
    }

    [Fact]
    public async Task AStandingMandateIsNotItselfALeadershipDecision()
    {
        var h = await Harness.CreateAsync();
        var source = h.Source(h.Manager, "mandate", "Own the review.") with { Kind = "mandate" };
        await h.Coordinator.CaptureAsync(h.Job.Id, h.Manager, source);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Coordinator.RecordDecisionAsync(
            h.Job.Id, h.Manager, source.Id, source.Content, "", null));
    }

    [Fact]
    public async Task OnlyOneReplicaOwnsARunAndPauseStopsItsNextAction()
    {
        var h = await Harness.CreateAsync();
        var second = new StandingReviewCoordinator(h.Store, h.Partition, NullLogger.Instance, h.Clock);
        var leases = await Task.WhenAll(
            h.Coordinator.TryBeginRunAsync(h.Job.Id, h.Manager, true),
            second.TryBeginRunAsync(h.Job.Id, h.Manager, true));
        var lease = Assert.Single(leases, value => value != null)!;
        await h.Coordinator.ChangeAsync(h.Job.Id, h.Manager, job => job.Paused = true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.EnsureRunAsync(h.Job.Id, lease));
        await h.Coordinator.EndRunAsync(h.Job.Id, lease);
        Assert.True((await h.Coordinator.GetAsync(h.Job.Id, h.Manager)).Paused);
        Assert.Null(await h.Coordinator.TryBeginRunAsync(h.Job.Id, h.Manager, true));
    }

    [Fact]
    public async Task AChangedConfigurationCannotBeOverwrittenByLateScheduleCompletion()
    {
        var h = await Harness.CreateAsync();
        var before = await h.Coordinator.GetAsync(h.Job.Id, h.Manager);
        await h.Coordinator.ChangeAsync(h.Job.Id, h.Manager, job => job.Paused = true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ChangeAsync(
            h.Job.Id, h.Manager, job => job.Paused = false, before.Revision));
        Assert.True((await h.Coordinator.GetAsync(h.Job.Id, h.Manager)).Paused);
    }

    [Fact]
    public async Task PauseHasANarrowToolAndPreservesPublishedBindingsAndAllPermissions()
    {
        var h = await Harness.CreateAsync();
        var brief = new StandingJobBrief(1, "Review.docx", "one", Guid.NewGuid().ToString(),
            "https://tenant.sharepoint.com/review", "facts", h.Clock.GetUtcNow());
        await h.Coordinator.RegisterBriefAsync(h.Job.Id, h.Manager, brief);
        h.SetTurn(h.Manager, "pause", "Pause the review. Keep its record.");
        var definition = h.Tools.GetToolDefinitions().Single(tool => tool["name"]!.GetValue<string>() == "set_standing_job_enabled");
        Assert.Equal(new[] { "job_id", "enabled" }, definition["parameters"]!["properties"]!.AsObject().Select(p => p.Key));
        await h.Call("set_standing_job_enabled", new { job_id = h.Job.Id, enabled = false });
        var job = await h.Coordinator.GetAsync(h.Job.Id, h.Manager);
        Assert.True(job.Paused);
        Assert.Equal(h.Job.Members, job.Members);
        Assert.Equal(h.Job.Recipients, job.Recipients);
        Assert.Equal(h.Job.SpecialistAgentIds, job.SpecialistAgentIds);
        Assert.Contains("word:" + brief.DocumentId, job.Bindings);
        Assert.Contains(h.Job.Bindings[0], job.Bindings);
    }

    [Fact]
    public async Task NullableConfigurationFieldsDoNotEraseTheCurrentJob()
    {
        var h = await Harness.CreateAsync();
        h.SetTurn(h.Manager, "update", "Keep the current configuration.");
        await h.Call("update_standing_job", new
        {
            job_id = h.Job.Id, members = (object?)null, recipients = (object?)null,
            source_bindings = (object?)null, specialist_agent_ids = (object?)null,
            mandate = (string?)null, enabled = (bool?)null, review_utc = (string?)null
        });
        var updated = await h.Coordinator.GetAsync(h.Job.Id, h.Manager);
        Assert.False(updated.Paused);
        Assert.Equal(h.Job.Members, updated.Members);
        Assert.Equal(h.Job.Recipients, updated.Recipients);
        Assert.Equal(h.Job.Bindings, updated.Bindings);
        Assert.Equal(h.Job.SpecialistAgentIds, updated.SpecialistAgentIds);
        Assert.Equal(h.Job.Mandate, updated.Mandate);
    }

    [Fact]
    public async Task VerifiedPublicationBindingsSurviveAnEmptySourceUpdate()
    {
        var h = await Harness.CreateAsync();
        var brief = new StandingJobBrief(1, "Review.docx", "one", Guid.NewGuid().ToString(),
            "https://tenant.sharepoint.com/review", "facts", h.Clock.GetUtcNow());
        await h.Coordinator.RegisterBriefAsync(h.Job.Id, h.Manager, brief);
        h.SetTurn(h.Manager, "update", "Remove the manually selected sources.");
        await h.Call("update_standing_job", new { job_id = h.Job.Id, source_bindings = Array.Empty<string>() });
        var job = await h.Coordinator.GetAsync(h.Job.Id, h.Manager);
        Assert.Equal("word:" + brief.DocumentId, Assert.Single(job.Bindings));
    }

    [Fact]
    public async Task ActionReceiptsPreventRepeatAttemptsAcrossRestarts()
    {
        var h = await Harness.CreateAsync();
        var calls = 0;
        Task<(bool? Accepted, string Detail)> Send()
        {
            calls++;
            return Task.FromResult<(bool?, string)>((true, "receipt"));
        }
        var first = await h.Coordinator.OnceAsync(h.Job.Id, h.Manager, "mail", "key", "payload", Send);
        var restarted = new StandingReviewCoordinator(h.Store, h.Partition, NullLogger.Instance, h.Clock);
        var second = await restarted.OnceAsync(h.Job.Id, h.Manager, "mail", "key", "different payload", Send);
        Assert.Equal("accepted", first.State);
        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UncertainDeliveryBlocksTheSameScopeEvenOnAnotherDay()
    {
        var h = await Harness.CreateAsync();
        var calls = 0;
        Task<(bool? Accepted, string Detail)> Send()
        {
            calls++;
            throw new HttpRequestException("Response lost after submission.");
        }
        var first = await h.Coordinator.OnceAsync(h.Job.Id, h.Manager, "mail", "day-one", "payload", Send, scope: "recipient");
        h.Clock.Advance(TimeSpan.FromDays(1));
        var second = await h.Coordinator.OnceAsync(h.Job.Id, h.Manager, "mail", "day-two", "payload", Send, scope: "recipient");
        Assert.Equal("uncertain", first.State);
        Assert.Equal(first.Key, second.Key);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AnUncertainIdempotentActionCanReconcileItsExistingResult()
    {
        var h = await Harness.CreateAsync();
        var calls = 0;
        Task<(bool? Accepted, string Detail)> Send()
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("Response lost after submission.");
            return Task.FromResult<(bool?, string)>((true, "reconciled"));
        }
        var first = await h.Coordinator.OnceAsync(
            h.Job.Id, h.Manager, "publish", "brief", "payload", Send, scope: "brief");
        var second = await h.Coordinator.OnceAsync(
            h.Job.Id, h.Manager, "publish", "brief", "payload", Send, scope: "brief", reconcileUncertain: true);
        Assert.Equal("uncertain", first.State);
        Assert.Equal("accepted", second.State);
        Assert.Equal("reconciled", second.Detail);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AnUncertainReceiptCanBeFinalizedAfterDestinationReconciliation()
    {
        var h = await Harness.CreateAsync();
        Task<(bool? Accepted, string Detail)> Send() =>
            throw new HttpRequestException("Response lost after submission.");
        await h.Coordinator.OnceAsync(
            h.Job.Id, h.Manager, "publish", "brief:original", "payload", Send, scope: "publish_brief");

        var uncertain = await h.Coordinator.ScopeReceiptAsync(h.Job.Id, h.Manager, "publish_brief");
        Assert.Equal("uncertain", uncertain!.State);
        await h.Coordinator.CompleteUncertainAsync(
            h.Job.Id, h.Manager, "publish_brief", "verified existing file");

        var accepted = await h.Coordinator.ScopeReceiptAsync(h.Job.Id, h.Manager, "publish_brief");
        Assert.Equal("accepted", accepted!.State);
        Assert.Equal("verified existing file", accepted.Detail);
    }

    [Fact]
    public async Task ConcurrentDifferentKeysCannotBothClaimAnExternalActionScope()
    {
        var h = await Harness.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<(bool? Accepted, string Detail)> Send()
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
            return (true, "receipt");
        }
        var first = h.Coordinator.OnceAsync(h.Job.Id, h.Manager, "mail", "first", "body", Send, scope: "owner");
        await entered.Task;
        var second = await h.Coordinator.OnceAsync(h.Job.Id, h.Manager, "mail", "second", "body", Send, scope: "owner");
        Assert.Equal("pending", second.State);
        release.SetResult();
        await first;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AutomaticTurnsCannotReadAnotherJob()
    {
        var h = await Harness.CreateAsync();
        var other = h.NewJob();
        await h.Coordinator.CreateAsync(other, h.Manager);
        h.SetTurn(h.Manager, "tick", "", automatic: true);
        var result = await h.Call("get_standing_job", new { job_id = other.Id });
        Assert.False(result["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MatchingUsesExplicitJobReferencesOrExactDocumentBindings()
    {
        var h = await Harness.CreateAsync();
        Assert.Single(await h.Coordinator.FindAsync(h.Owner, StandingReviewCoordinator.Marker(h.Job.Id), null));
        Assert.Single(await h.Coordinator.FindAsync(h.Owner, "A comment", h.Job.Bindings[0]));
        Assert.Empty(await h.Coordinator.FindAsync(h.Owner, h.Job.Title, "word:" + Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Coordinator.FindAsync(
            h.Owner, StandingReviewCoordinator.Marker(h.Job.Id), "word:" + Guid.NewGuid()));
        Assert.Null(StandingReviewCoordinator.ReferencedJob("[job:not-an-id]"));
    }

    [Fact]
    public async Task UnapprovedRecipientsAreRejectedBeforeTheMailCall()
    {
        var h = await Harness.CreateAsync();
        var result = await h.Call("send_standing_message", new
        {
            job_id = h.Job.Id, purpose = "brief", delivery = "email",
            recipients = new[] { "outside@example.com" }, subject = "Review", body_html = "Brief"
        });
        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal(0, h.MailCalls);
    }

    [Fact]
    public async Task ManagerJudgmentsUseTheConfiguredSurfaceAndDoNotSilentlySubstituteEmail()
    {
        var h = await Harness.CreateAsync();
        var request = new
        {
            job_id = h.Job.Id, purpose = "escalation", delivery = "email",
            recipients = new[] { h.Manager.Email }, subject = "Decision needed", body_html = "Choose the next step."
        };
        Assert.False((await h.Call("send_standing_message", request))["success"]!.GetValue<bool>());
        Assert.Equal(0, h.MailCalls);
        h.SetTurn(h.Manager, "delivery-choice", "Send my decisions by email.");
        await h.Call("update_standing_job", new { job_id = h.Job.Id, decision_delivery = "email" });
        Assert.Equal("accepted", (await h.Call("send_standing_message", request))["state"]!.GetValue<string>());
        Assert.Equal(1, h.MailCalls);
    }

    [Fact]
    public async Task FollowUpToTheWrongOwnerOrAClosedItemIsRejected()
    {
        var h = await Harness.CreateAsync();
        var id = h.Items.Seed(h.Job.Id, h.Owner.Id, "closed", h.Clock.GetUtcNow());
        var result = await h.Call("send_standing_message", new
        {
            job_id = h.Job.Id, purpose = "follow_up", related_id = id, delivery = "email",
            recipients = new[] { h.Owner.Email }, subject = "Status", body_html = "Status?"
        });
        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal(0, h.MailCalls);
    }

    [Fact]
    public async Task FollowUpWaitsForTheActualDeadlineAndThenRunsOnlyOnce()
    {
        var h = await Harness.CreateAsync();
        var id = h.Items.Seed(h.Job.Id, h.Owner.Id, "open", h.Clock.GetUtcNow().AddMinutes(5));
        h.SetTurn(h.Manager, "tick", "", automatic: true);
        var args = new
        {
            job_id = h.Job.Id, purpose = "follow_up", related_id = id, delivery = "email",
            recipients = new[] { h.Owner.Email }, subject = "Confirmation needed", body_html = "Please confirm completion."
        };
        Assert.False((await h.Call("send_standing_message", args))["success"]!.GetValue<bool>());
        Assert.Equal(0, h.MailCalls);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("accepted", (await h.Call("send_standing_message", args))["state"]!.GetValue<string>());
        await h.Call("send_standing_message", args);
        Assert.Equal(1, h.MailCalls);
    }

    [Fact]
    public async Task PendingSpecialistIsNotRecordedAsAnAnswerOrAutomaticallyReissued()
    {
        var h = await Harness.CreateAsync();
        h.SpecialistResponse = """{"outcome":"pending","task_ids":["task-one"],"answer":null}""";
        var request = new { job_id = h.Job.Id, agent_id = "approved-specialist", question = "What is recorded?" };
        var first = await h.Call("ask_standing_specialist", request);
        await h.Coordinator.ChangeAsync(h.Job.Id, h.Manager, job => job.Mandate += " Additional detail.");
        var second = await h.Call("ask_standing_specialist", request);
        Assert.Equal("uncertain", first["state"]!.GetValue<string>());
        Assert.Equal(first["key"]!.GetValue<string>(), second["key"]!.GetValue<string>());
        Assert.Equal(1, h.DelegateCalls);
    }

    [Fact]
    public async Task SpecialistPermissionIsNotInheritedFromDiscovery()
    {
        var h = await Harness.CreateAsync();
        var result = await h.Call("ask_standing_specialist",
            new { job_id = h.Job.Id, agent_id = "not-permitted", question = "Question" });
        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal(0, h.DelegateCalls);
    }

    [Fact]
    public async Task CompleteInputDecisionCommitmentLoopKeepsEvidenceAndReusesExistingActions()
    {
        var h = await Harness.CreateAsync();
        h.SetTurn(h.Manager, "initial", "Finance must provide the forecast.");
        Assert.True((await h.Call("record_standing_input", new
        {
            job_id = h.Job.Id, key = "forecast", title = "Forecast", owner_id = h.Owner.Id,
            state = "missing", content = ""
        }))["success"]!.GetValue<bool>());
        h.SetTurn(h.Manager, "tick", "", automatic: true);
        var request = new
        {
            job_id = h.Job.Id, purpose = "request_input", related_id = "forecast",
            delivery = "email", recipients = new[] { h.Owner.Email }, subject = "Forecast",
            body_html = "Please provide the forecast."
        };
        await h.Call("send_standing_message", request);
        await h.Call("send_standing_message", request);
        Assert.Equal(1, h.MailCalls);
        Assert.Contains(StandingReviewCoordinator.Marker(h.Job.Id), h.LastSubject);

        h.SetTurn(h.Owner, "owner-update", "The forecast is ready.", kind: "mail");
        await h.Call("record_standing_input", new
        {
            job_id = h.Job.Id, key = "forecast", title = "Forecast", owner_id = h.Owner.Id,
            state = "received", content = "The forecast is ready."
        });
        Assert.Equal("received", Assert.Single(await h.Coordinator.RecordsAsync<StandingJobInput>(h.Job.Id, "input")).State);

        h.SetTurn(h.Manager, "decision", "Proceed with the review. Finance will send the final forecast.");
        var decision = await h.Call("record_standing_decision", new
        {
            job_id = h.Job.Id, statement_quote = "Proceed with the review.", rationale = ""
        });
        var created = await h.Call("create_standing_commitment", new
        {
            job_id = h.Job.Id, title = "Final forecast", description = "Deliver the final forecast",
            owner_id = h.Owner.Id, due_utc = h.Clock.GetUtcNow().AddHours(1).ToString("o"),
            decision_id = decision["id"]!.GetValue<string>(),
            evidence_quote = "Finance will send the final forecast."
        });
        var itemId = created["taskId"]!.GetValue<string>();
        h.SetTurn(h.Owner, "completion", "The final forecast is delivered.", kind: "word");
        var closed = await h.Call("update_standing_commitment", new
        {
            job_id = h.Job.Id, item_id = itemId, status = "closed",
            evidence_quote = "The final forecast is delivered."
        });
        Assert.Equal("accepted", closed["state"]!.GetValue<string>());
        var item = JsonNode.Parse(await h.Items.GetWorkItemAsync(h.Partition, itemId))!;
        Assert.Equal("closed", item["status"]!.GetValue<string>());
        Assert.Equal(h.Owner.Id, item["completionConfirmedBy"]!.GetValue<string>());
        Assert.Contains("The final forecast is delivered.", item["completionEvidence"]!.GetValue<string>());
        Assert.Equal(1, h.Items.Creates);
        Assert.Equal(1, h.Items.Updates);
    }

    [Fact]
    public async Task AnotherParticipantCannotSupplyAnOwnersCompletionEvidence()
    {
        var h = await Harness.CreateAsync();
        var other = new StandingJobCaller(Guid.NewGuid().ToString(), h.Manager.Id, "other@example.com");
        await h.Coordinator.ChangeAsync(h.Job.Id, h.Manager,
            job => job.Members.Add(new StandingJobMember(other.Id, other.Email!)));
        var task = h.Items.Seed(h.Job.Id, h.Owner.Id, "open", h.Clock.GetUtcNow());
        h.SetTurn(other, "other-message", "The forecast is complete.");
        var result = await h.Call("update_standing_commitment", new
        {
            job_id = h.Job.Id, item_id = task, status = "closed", evidence_quote = "The forecast is complete."
        });
        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal(0, h.Items.Updates);
    }

    [Fact]
    public async Task CommitmentCannotCloseWhileAnExplicitDependencyIsOpen()
    {
        var h = await Harness.CreateAsync();
        var dependency = h.Items.Seed(h.Job.Id, h.Owner.Id, "open", h.Clock.GetUtcNow());
        var task = h.Items.Seed(h.Job.Id, h.Owner.Id, "open", h.Clock.GetUtcNow(), [dependency]);
        h.SetTurn(h.Owner, "completion", "My task is complete.");
        var result = await h.Call("update_standing_commitment", new
        {
            job_id = h.Job.Id, item_id = task, status = "closed", evidence_quote = "My task is complete."
        });
        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal(0, h.Items.Updates);
    }

    [Fact]
    public void OldTaskRowsRemainReadableAndJobTasksRejectTheLegacyBypass()
    {
        var row = new TableEntity("instance", Guid.NewGuid().ToString()) { ["Name"] = "Old task" };
        var old = WorkItemService.MapTableEntity(row);
        Assert.Equal("", old.StandingJobId);
        Assert.Equal("[]", old.DependencyIdsJson);
        Assert.True(WorkItemService.IsLegacyVisible(old));
        WorkItemService.ValidateStandingUpdate(old, null, "closed", null, null);
        old.StandingJobId = Guid.NewGuid().ToString();
        Assert.False(WorkItemService.IsLegacyVisible(old));
        Assert.Throws<UnauthorizedAccessException>(() =>
            WorkItemService.ValidateStandingUpdate(old, null, "closed", "source", Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() =>
            WorkItemService.ValidateStandingUpdate(old, old.StandingJobId, "closed", null, null));
    }

    [Fact]
    public void AutomaticInstructionsDoNotOfferASecondMessagingOrDelegationPath()
    {
        Assert.Contains("No native MCP mutation tools", AgentInstructions.StandingJobRunInstructions);
        Assert.Contains("only automated messaging path", AgentInstructions.StandingJobRunInstructions);
        Assert.Contains("you never make that decision", AgentInstructions.StandingJobRunInstructions);
    }

    [Fact]
    public void ReviewPhaseIsDerivedFromRecordedFactsNotAnUnchangingPromptLabel()
    {
        var job = new StandingJob { Paused = false };
        var input = new StandingJobInput("forecast", "Forecast", "owner", "missing", "", "", DateTimeOffset.UtcNow);
        Assert.Equal("collecting_inputs", StandingReviewCoordinator.Phase(job, [input], [], 0, 0));
        Assert.Equal("needs_manager", StandingReviewCoordinator.Phase(job, [input with { State = "disputed" }], [], 0, 0));
        Assert.Equal("decision_ready", StandingReviewCoordinator.Phase(job, [input with { State = "received" }], [], 0, 0));
        Assert.Equal("following_up", StandingReviewCoordinator.Phase(job, [], [], 1, 1));
        Assert.Equal("review_complete", StandingReviewCoordinator.Phase(job, [], [], 0, 1));
        job.Paused = true;
        Assert.Equal("paused", StandingReviewCoordinator.Phase(job, [], [], 1, 1));
    }

    [Fact]
    public async Task ARecordedDecisionAndCommitmentAreVisibleFromTheWordBoundJob()
    {
        var h = await Harness.CreateAsync();
        h.SetTurn(h.Manager, "notes", "Proceed with the review.");
        await h.Call("record_standing_decision", new
        {
            job_id = h.Job.Id, statement_quote = "Proceed with the review.", rationale = ""
        });
        h.SetTurn(h.Owner, "word-question", "Why was that decision recorded?", kind: "word");
        var matched = Assert.Single(await h.Coordinator.FindAsync(h.Owner, "A comment", h.Job.Bindings[0]));
        var view = await h.Call("get_standing_job", new { job_id = matched.Id });
        Assert.Equal("Proceed with the review.", view["state"]!["decisions"]![0]!["statement"]!.GetValue<string>());
        Assert.Equal("needs_commitments", view["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadingAPausedJobDoesNotIngestTheQuestionAsNewEvidence()
    {
        var h = await Harness.CreateAsync();
        await h.Coordinator.ChangeAsync(h.Job.Id, h.Manager, job => job.Paused = true);
        h.SetTurn(h.Owner, "read-only", "What is the current state?");
        var result = await h.Call("get_standing_job", new { job_id = h.Job.Id });
        Assert.Equal("paused", result["phase"]!.GetValue<string>());
        Assert.Empty(await h.Coordinator.RecordsAsync<StandingJobEvent>(h.Job.Id, "event"));
    }

    private static string Args(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task PublishedBriefAddsOnlyItsVerifiedCommentBindingWithoutChangingFactRevision()
    {
        var h = await Harness.CreateAsync();
        var before = await h.Coordinator.StateFingerprintAsync(h.Job.Id);
        var brief = new StandingJobBrief(1, "Review v1.docx", "item-one", Guid.NewGuid().ToString(),
            "https://tenant.sharepoint.com/review", before, h.Clock.GetUtcNow());
        await h.Coordinator.RegisterBriefAsync(h.Job.Id, h.Manager, brief);
        await h.Coordinator.RegisterBriefAsync(h.Job.Id, h.Manager, brief);
        Assert.Single(await h.Coordinator.RecordsAsync<StandingJobBrief>(h.Job.Id, "brief"));
        Assert.Single(await h.Coordinator.FindAsync(h.Owner, "A review comment", "word:" + brief.DocumentId));
        Assert.Equal(before, await h.Coordinator.StateFingerprintAsync(h.Job.Id));
    }

    [Fact]
    public async Task PublicationReusesFactsAndDoesNotVersionModelRewording()
    {
        var h = await Harness.CreateAsync();
        h.SetTurn(h.Manager, "input", "The budget is confirmed.");
        await h.Call("record_standing_input", new { job_id = h.Job.Id, key = "budget", title = "Budget",
            owner_id = h.Manager.Id, state = "received", content = "The budget is confirmed." });
        var one = await h.Call("publish_standing_brief", new { job_id = h.Job.Id, summary = "Budget confirmed.", decision_question = "" });
        var two = await h.Call("publish_standing_brief", new { job_id = h.Job.Id, summary = "Different wording of the same facts.", decision_question = "" });
        Assert.Equal(one["key"]!.GetValue<string>(), two["key"]!.GetValue<string>());
        Assert.Equal(1, h.Publications);
        Assert.Single(await h.Coordinator.RecordsAsync<StandingJobBrief>(h.Job.Id, "brief"));
    }

    [Fact]
    public async Task ASourceSupportedClarificationGetsANewVersionWithoutInventingADispute()
    {
        var h = await Harness.CreateAsync();
        h.SetTurn(h.Manager, "input", "The budget is confirmed.");
        await h.Call("record_standing_input", new { job_id = h.Job.Id, key = "budget", title = "Budget",
            owner_id = h.Manager.Id, state = "received", content = "The budget is confirmed." });
        await h.Call("publish_standing_brief", new { job_id = h.Job.Id, summary = "Budget confirmed.", decision_question = "" });
        h.SetTurn(h.Manager, "word-clarification", "The ceiling applies to the pilot, not the entire program.", "word");
        var next = await h.Call("publish_standing_brief", new { job_id = h.Job.Id, summary = "Pilot ceiling clarified.", decision_question = "",
            change_event_id = "word-clarification", change_quote = "The ceiling applies to the pilot, not the entire program." });
        Assert.Equal("accepted", next["state"]!.GetValue<string>());
        h.SetTurn(h.Manager, "timer", "", automatic: true);
        await h.Call("publish_standing_brief", new { job_id = h.Job.Id, summary = "Unchanged.", decision_question = "" });
        Assert.Equal(2, h.Publications);
        Assert.DoesNotContain(await h.Coordinator.RecordsAsync<StandingJobInput>(h.Job.Id, "input"), input => input.State == "disputed");
    }

    [Fact]
    public async Task RecoveryUsesTheOriginalDraftBeforePublishingChangedFacts()
    {
        var h = await Harness.CreateAsync();
        h.SetTurn(h.Manager, "old", "The budget is provisional.");
        await h.Call("record_standing_input", new { job_id = h.Job.Id, key = "budget", title = "Budget",
            owner_id = h.Manager.Id, state = "received", content = "The budget is provisional." });
        h.FailPublication = true;
        var uncertain = await h.Call("publish_standing_brief", new { job_id = h.Job.Id, summary = "Provisional.", decision_question = "" });
        Assert.Equal("uncertain", uncertain["state"]!.GetValue<string>());
        h.SetTurn(h.Manager, "new", "The budget is confirmed.");
        await h.Call("record_standing_input", new { job_id = h.Job.Id, key = "budget", title = "Budget",
            owner_id = h.Manager.Id, state = "received", content = "The budget is confirmed." });
        var current = await h.Call("publish_standing_brief", new { job_id = h.Job.Id, summary = "Confirmed.", decision_question = "" });
        Assert.Equal("accepted", current["state"]!.GetValue<string>());
        Assert.Equal(1, h.Reconciliations);
        Assert.Equal(2, h.Publications);
        Assert.Contains("The budget is provisional.", h.DraftContents[1]);
        Assert.Contains("The budget is confirmed.", h.DraftContents[2]);
        Assert.Equal(new[] { 1, 2 }, (await h.Coordinator.RecordsAsync<StandingJobBrief>(h.Job.Id, "brief")).Select(b => b.Version).Order());
    }

    [Fact]
    public async Task LegacyUncertainPublicationIsNotImportedByFileName()
    {
        var h = await Harness.CreateAsync();
        await h.Coordinator.OnceAsync(h.Job.Id, h.Manager, "publish_brief", "brief:legacy", "old payload",
            () => throw new HttpRequestException("Unknown destination."), scope: "publish_brief");
        var result = await h.Call("publish_standing_brief", new { job_id = h.Job.Id, summary = "Attempt.", decision_question = "" });
        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Contains("no saved draft", result["error"]!.GetValue<string>());
        Assert.Equal(0, h.Publications);
        Assert.Equal(0, h.Reconciliations);
    }

    [Fact]
    public async Task RegisteringTheSameFactsCannotReplaceTheRealFileIdentity()
    {
        var h = await Harness.CreateAsync();
        var first = new StandingJobBrief(1, "Review v1.docx", "one", Guid.NewGuid().ToString(),
            "https://tenant.sharepoint.com/brief", "facts", h.Clock.GetUtcNow());
        await h.Coordinator.RegisterBriefAsync(h.Job.Id, h.Manager, first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.RegisterBriefAsync(
            h.Job.Id, h.Manager, first with { ItemId = "other-file" }));
        Assert.Equal("one", Assert.Single(await h.Coordinator.RecordsAsync<StandingJobBrief>(h.Job.Id, "brief")).ItemId);
    }

    [Fact]
    public async Task ACompletedCheckDoesNotSkipTheNextFiveMinuteCronTick()
    {
        var h = await Harness.CreateAsync();
        var lease = await h.Coordinator.TryBeginRunAsync(h.Job.Id, h.Manager, true);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Coordinator.EndRunAsync(h.Job.Id, lease!);
        h.Clock.Advance(TimeSpan.FromMinutes(4.5));
        Assert.NotNull(await h.Coordinator.TryBeginRunAsync(h.Job.Id, h.Manager, true));
    }

    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    internal sealed class MemoryStore : IStandingJobStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string, string), StandingJobRow> _rows = [];
        private long _version;
        public bool IsAvailable => true;
        private static StandingJobRow Copy(StandingJobRow row) => new()
        {
            PartitionKey = row.PartitionKey, RowKey = row.RowKey, ETag = row.ETag,
            Timestamp = row.Timestamp, Data = row.Data
        };
        public Task<StandingJobRow?> ReadAsync(string partition, string key, CancellationToken token = default)
        {
            lock (_gate)
                return Task.FromResult(_rows.TryGetValue((partition, key), out var row) ? Copy(row) : null);
        }
        public Task<IReadOnlyList<StandingJobRow>> ListAsync(string partition, string prefix, CancellationToken token = default)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<StandingJobRow>>(_rows.Values
                    .Where(row => row.PartitionKey == partition && row.RowKey.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(Copy).ToList());
        }
        public Task<bool> TryAddAsync(StandingJobRow row, CancellationToken token = default)
        {
            lock (_gate)
            {
                if (_rows.ContainsKey((row.PartitionKey, row.RowKey))) return Task.FromResult(false);
                var copy = Copy(row); copy.ETag = new ETag((++_version).ToString());
                _rows[(row.PartitionKey, row.RowKey)] = copy;
                return Task.FromResult(true);
            }
        }
        public Task<bool> TryReplaceAsync(StandingJobRow row, ETag expected, CancellationToken token = default)
        {
            lock (_gate)
            {
                if (!_rows.TryGetValue((row.PartitionKey, row.RowKey), out var current) || current.ETag != expected)
                    return Task.FromResult(false);
                var copy = Copy(row); copy.ETag = new ETag((++_version).ToString());
                _rows[(row.PartitionKey, row.RowKey)] = copy;
                return Task.FromResult(true);
            }
        }
    }

    internal sealed class MemoryWorkItems() : WorkItemService(
        new ConfigurationBuilder().Build(), NullLogger<WorkItemService>.Instance)
    {
        private readonly Dictionary<string, JsonObject> _items = [];
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public string Seed(string job, string owner, string status, DateTimeOffset due, string[]? dependencies = null)
        {
            var id = Guid.NewGuid().ToString();
            _items[id] = new JsonObject
            {
                ["id"] = id, ["standingJobId"] = job, ["ownerAadObjectId"] = owner,
                ["status"] = status, ["eta"] = due.ToString("o"),
                ["dependencyIds"] = JsonSerializer.SerializeToNode(dependencies ?? [])
            };
            return id;
        }
        public override Task<string> CreateWorkItemAsync(string partitionKey, string name, string description, string owner, string ownerAadObjectId, string eta,
            string? standingJobId = null, string? decisionId = null, string? stableId = null, string[]? dependencyIds = null)
        {
            var id = stableId ?? Guid.NewGuid().ToString();
            if (!_items.ContainsKey(id))
            {
                Creates++;
                _items[id] = new JsonObject
                {
                    ["id"] = id, ["name"] = name, ["description"] = description,
                    ["owner"] = owner, ["ownerAadObjectId"] = ownerAadObjectId,
                    ["standingJobId"] = standingJobId, ["decisionId"] = decisionId,
                    ["eta"] = eta, ["status"] = "open",
                    ["dependencyIds"] = JsonSerializer.SerializeToNode(dependencyIds ?? [])
                };
            }
            return Task.FromResult(Args(new { success = true, id }));
        }
        public override Task<string> GetWorkItemAsync(string partitionKey, string rowKey) =>
            Task.FromResult(_items.TryGetValue(rowKey, out var item) ? item.ToJsonString() : "Error: task not found.");
        public override Task<string> ListWorkItemsAsync(string partitionKey, string? statusFilter = null, string? ownerFilter = null,
            string? nameFilter = null, string? standingJobId = null) => Task.FromResult(Args(new
            {
                items = _items.Values.Where(item => standingJobId == null || item["standingJobId"]?.GetValue<string>() == standingJobId).ToArray()
            }));
        public override Task<string> UpdateWorkItemAsync(string partitionKey, string rowKey, string? name = null, string? description = null,
            string? owner = null, string? ownerAadObjectId = null, string? eta = null, string? status = null,
            string? standingJobId = null, string? completionEvidence = null, string? completionConfirmedBy = null, DateTimeOffset? lastFollowUpUtc = null)
        {
            var item = _items[rowKey];
            ValidateStandingUpdate(new WorkItemEntity { StandingJobId = item["standingJobId"]!.GetValue<string>() },
                standingJobId, status, completionEvidence, completionConfirmedBy);
            if (status != null) item["status"] = status;
            if (eta != null) item["eta"] = eta;
            if (ownerAadObjectId != null) item["ownerAadObjectId"] = ownerAadObjectId;
            if (completionEvidence != null) item["completionEvidence"] = completionEvidence;
            if (completionConfirmedBy != null) item["completionConfirmedBy"] = completionConfirmedBy;
            Updates++;
            return Task.FromResult("""{"success":true}""");
        }
    }

    private sealed class Harness
    {
        public MemoryStore Store { get; } = new();
        public MemoryWorkItems Items { get; } = new();
        public TestClock Clock { get; } = new();
        public StandingJobCaller Manager { get; }
        public StandingJobCaller Owner { get; }
        public StandingReviewCoordinator Coordinator { get; }
        public StandingJobToolHandler Tools { get; }
        public string Partition { get; } = StandingJobStore.Partition(Guid.NewGuid(), Guid.NewGuid());
        public StandingJob Job { get; private set; } = null!;
        public bool ScheduleWorks { get; set; } = true;
        public int ScheduleCalls { get; private set; }
        public int MailCalls { get; private set; }
        public int DelegateCalls { get; private set; }
        public int Publications { get; private set; }
        public int Reconciliations { get; private set; }
        public bool FailPublication { get; set; }
        public Dictionary<int, string> DraftContents { get; } = [];
        private readonly Dictionary<int, StandingJobBrief> _published = [];
        public string LastSubject { get; private set; } = "";
        public string SpecialistResponse { get; set; } = """{"outcome":"answered","answer":"Recorded decision","citations":["https://example.com/source"]}""";

        public Harness()
        {
            var manager = Guid.NewGuid().ToString();
            Manager = new StandingJobCaller(manager, manager, "manager@example.com");
            Owner = new StandingJobCaller(Guid.NewGuid().ToString(), manager, "owner@example.com");
            Coordinator = new StandingReviewCoordinator(Store, Partition, NullLogger.Instance, Clock);
            Tools = new StandingJobToolHandler(Coordinator, Items,
                (job, enabled) => { ScheduleCalls++; return Task.FromResult((ScheduleWorks, "schedule result")); },
                (to, subject, body) => { MailCalls++; LastSubject = subject; return Task.FromResult((true, "mail accepted")); },
                (job, html) => Task.FromResult("chat-receipt"),
                (jobId, id, question) => { DelegateCalls++; return Task.FromResult<string?>(SpecialistResponse); },
                id => Task.FromResult(id == Manager.Id ? new StandingJobMember(Manager.Id, Manager.Email!)
                    : id == Owner.Id ? new StandingJobMember(Owner.Id, Owner.Email!) : null),
                NullLogger.Instance,
                (job, version, fingerprint, content) =>
                {
                    Publications++;
                    DraftContents.Add(version, content);
                    var brief = new StandingJobBrief(version, $"Review v{version}.docx", $"item-{version}",
                        Guid.NewGuid().ToString(), $"https://tenant.sharepoint.com/v{version}", fingerprint, Clock.GetUtcNow());
                    _published.Add(version, brief);
                    if (FailPublication)
                    {
                        FailPublication = false;
                        throw new HttpRequestException("Sharing response lost after creation.");
                    }
                    return Task.FromResult(brief);
                },
                (job, version, fingerprint, content) =>
                {
                    Reconciliations++;
                    Assert.Equal(DraftContents[version], content);
                    Assert.Equal(_published[version].FactsFingerprint, fingerprint);
                    return Task.FromResult(_published[version]);
                });
            SetTurn(Manager, "first-message", "Own the review.");
        }

        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            h.Job = h.NewJob();
            await h.Coordinator.CreateAsync(h.Job, h.Manager);
            h.Job = await h.Coordinator.ChangeAsync(h.Job.Id, h.Manager, job => job.Paused = false);
            return h;
        }

        public StandingJob NewJob() => new()
        {
            Id = Guid.NewGuid().ToString(), Title = "Weekly leadership review",
            Mandate = "Prepare the decision and follow through.",
            ManagerId = Manager.Id,
            Members = [new(Manager.Id, Manager.Email!), new(Owner.Id, Owner.Email!)],
            Recipients = [Manager.Email!, Owner.Email!],
            Bindings = ["word:" + Guid.NewGuid()],
            SpecialistAgentIds = ["approved-specialist"],
            ConversationId = "19:manager-agent@unq.gbl.spaces",
            CronExpression = "*/15 * * * *", RoutineName = "standing-test"
        };

        public StandingJobEvent Source(StandingJobCaller actor, string id, string content, string kind = "chat") =>
            new(id, kind, actor.Id, kind == "word" && Job != null ? Job.Bindings[0] : kind + ":thread",
                content, Clock.GetUtcNow(), true);

        public void SetTurn(StandingJobCaller actor, string id, string content, string kind = "chat", bool automatic = false) =>
            Tools.Turn = new StandingJobTurn(actor, automatic ? null : Source(actor, id, content, kind),
                new Activity
                {
                    Type = ActivityTypes.Message, ChannelId = "msteams",
                    Id = automatic ? null : id,
                    Conversation = new ConversationAccount { Id = "19:manager-agent@unq.gbl.spaces", ConversationType = "personal" }
                }, automatic, automatic ? Job.Id : null);

        public async Task<JsonNode> Call(string tool, object args) =>
            JsonNode.Parse(await Tools.TryExecuteAsync(tool, Args(args))
                ?? throw new InvalidOperationException("The test called an unavailable tool."))!;
    }
}
