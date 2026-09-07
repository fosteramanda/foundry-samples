// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.AI.AgentServer.Invocations.Voice;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace VoiceHostedAgent.Tests;

[TestFixture]
public class VoiceAgentTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);
    private static int s_inputSequence;

    [Test]
    public async Task SessionStart_AcceptsProtocol10()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        await agent.SessionStartAsync(session, SessionStart("1.0"));

        Assert.That(session.Messages, Has.Count.EqualTo(1));
        Assert.That(session.Messages.Single(), Is.TypeOf<VoiceSessionReadyMessage>());
    }

    [Test]
    public async Task SessionStart_RejectsUnsupportedProtocol()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        await agent.SessionStartAsync(session, SessionStart("2.0"));

        var rejected = session.Messages.OfType<VoiceSessionRejectedMessage>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(rejected.Code, Is.EqualTo("protocol_mismatch"));
            Assert.That(rejected.Retriable, Is.False);
        });
    }

    [Test]
    public async Task ConcurrentInvalidSessionStart_DoesNotRejectClaimedValidStart()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new BlockingReadyVoiceSession();

        var validStart = agent.SessionStartAsync(session, SessionStart("1.0"));
        await session.ReadySendStarted.Task.WaitAsync(TestTimeout);
        await agent.SessionStartAsync(session, SessionStart("2.0"));

        Assert.That(session.Messages.OfType<VoiceSessionRejectedMessage>(), Is.Empty);
        session.ReleaseReadySend.TrySetResult();
        await validStart;
        Assert.That(session.Messages.OfType<VoiceSessionReadyMessage>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task InputBeforeSessionStart_IsIgnored()
    {
        var model = new ScriptedModelClient("must not run");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.RawUserMessageAsync(session, UserMessage("too early"));

        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Is.Empty);
            Assert.That(session.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task InputAfterRejectedSession_IsIgnored()
    {
        var model = new ScriptedModelClient("must not run");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();
        await agent.SessionStartAsync(session, SessionStart("2.0"));
        session.ClearMessages();

        await agent.RawUserMessageAsync(session, UserMessage("after rejection"));

        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Is.Empty);
            Assert.That(session.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task InputAfterSessionEnd_IsIgnored()
    {
        var model = new ScriptedModelClient("must not run");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();
        await agent.UserMessageAsync(session, UserMessage("/none"));
        await agent.SessionEndAsync(
            session,
            new VoiceSessionEndEvent("m_end", DateTimeOffset.UtcNow, "caller_hangup"));
        session.ClearMessages();

        await agent.RawUserMessageAsync(session, UserMessage("after end"));

        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Is.Empty);
            Assert.That(session.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task DuplicateInputItemId_DoesNotCallModelAgain()
    {
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();
        var duplicate = UserMessage("same input", "in_duplicate");

        await agent.UserMessageAsync(session, duplicate);
        await model.Started.Task.WaitAsync(TestTimeout);
        await agent.UserMessageAsync(session, duplicate);

        Assert.Multiple(() =>
        {
            Assert.That(model.RequestCount, Is.EqualTo(1));
            Assert.That(session.Messages.OfType<VoiceResponseNoneMessage>(), Is.Empty);
        });
        await agent.SessionEndAsync(
            session,
            new VoiceSessionEndEvent("m_end", DateTimeOffset.UtcNow, "test_complete"));
    }

    [Test]
    public async Task NinthActiveResponse_IsDeclinedAtCapacity()
    {
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        for (var index = 0; index < 9; index++)
        {
            await agent.UserMessageAsync(session, UserMessage($"request {index}"));
        }
        await WaitUntilAsync(() => session.Messages.OfType<VoiceResponseCreatedMessage>().Count() == 8);

        Assert.Multiple(() =>
        {
            Assert.That(session.Messages.OfType<VoiceResponseCreatedMessage>().Count(), Is.EqualTo(8));
            Assert.That(
                session.Messages.OfType<VoiceResponseNoneMessage>().Single().Reason,
                Is.EqualTo("capacity_exceeded"));
        });
        await agent.SessionEndAsync(
            session,
            new VoiceSessionEndEvent("m_end", DateTimeOffset.UtcNow, "test_complete"));
    }

    [Test]
    public async Task NinthResponse_IsDeclinedWhileEightResponseDoneSendsAreBlocked()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new BlockingResponseDoneVoiceSession();

        for (var index = 0; index < VoiceConnectionState.MaximumTerminalSends; index++)
        {
            await agent.UserMessageAsync(session, UserMessage("/help"));
        }
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseOutputTextDoneMessage>().Count() ==
                VoiceConnectionState.MaximumTerminalSends);

        await agent.UserMessageAsync(session, UserMessage("/help"));

        Assert.That(
            session.Messages.OfType<VoiceResponseNoneMessage>().Single().Reason,
            Is.EqualTo("capacity_exceeded"));
        session.ReleaseResponseDoneSend.TrySetResult();
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() ==
                VoiceConnectionState.MaximumTerminalSends);
    }

    [Test]
    public async Task NinthPendingProactiveResponse_IsDeclinedAtCapacity()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        for (var index = 0; index < 9; index++)
        {
            await agent.UserMessageAsync(session, UserMessage($"/proactive request {index}"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(session.Messages.OfType<VoiceResponseCreatedMessage>().Count(), Is.EqualTo(8));
            Assert.That(
                session.Messages.OfType<VoiceResponseNoneMessage>()
                    .Count(message => message.Reason == "capacity_exceeded"),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task InputBeyondConnectionLimit_EndsCallOnce()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        for (var index = 0; index <= VoiceConnectionState.MaximumTrackedInputItems; index++)
        {
            await agent.UserMessageAsync(
                session,
                UserMessage("/none", $"in_limit_{index}"));
        }

        var end = session.Messages.OfType<VoiceEndCallMessage>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(end.Reason, Is.EqualTo("session_input_limit"));
            Assert.That(end.Mode, Is.EqualTo(VoiceEndCallMode.Drain));
        });
    }

    [Test]
    public async Task OversizedMultipartInput_IsDeclinedBeforeModelCall()
    {
        var model = new ScriptedModelClient("must not run");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();
        var message = new VoiceUserMessageEvent(
            "m_oversized",
            DateTimeOffset.UtcNow,
            "in_oversized",
            new[]
            {
                new VoiceInputTextPart(new string('a', 4_001)),
                new VoiceInputTextPart(new string('b', 4_000)),
            });

        await agent.UserMessageAsync(session, message);

        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Is.Empty);
            Assert.That(
                session.Messages.OfType<VoiceResponseNoneMessage>().Single().Reason,
                Is.EqualTo("input_too_large"));
        });
    }

    [Test]
    public async Task AcceptedProactiveResponse_IsCancelledWhenActiveCapacityIsFull()
    {
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();
        await agent.UserMessageAsync(session, UserMessage("/proactive later"));
        var proactiveResponseId = session.Messages
            .OfType<VoiceResponseCreatedMessage>()
            .Single(message => message.InReplyTo is null)
            .ResponseId;
        for (var index = 0; index < 8; index++)
        {
            await agent.UserMessageAsync(session, UserMessage($"active {index}"));
        }
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseCreatedMessage>()
                .Count(message => message.InReplyTo is not null) == 8);

        await agent.ResponseAcceptedAsync(
            session,
            new VoiceResponseAcceptedEvent(
                "m_accepted_at_capacity",
                DateTimeOffset.UtcNow,
                proactiveResponseId));

        var cancel = session.Messages.OfType<VoiceResponseCancelMessage>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(cancel.ResponseId, Is.EqualTo(proactiveResponseId));
            Assert.That(cancel.Reason, Is.EqualTo("capacity_exceeded"));
        });
        await agent.SessionEndAsync(
            session,
            new VoiceSessionEndEvent("m_end", DateTimeOffset.UtcNow, "test_complete"));
    }

    [Test]
    public async Task EndCommand_CancelsInflightWorkBeforeEndCall()
    {
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();
        await agent.UserMessageAsync(session, UserMessage("long response"));
        await model.Started.Task.WaitAsync(TestTimeout);

        await agent.UserMessageAsync(session, UserMessage("/end"));

        var messages = session.Messages;
        var endIndex = messages.ToList().FindIndex(message => message is VoiceEndCallMessage);
        Assert.Multiple(() =>
        {
            Assert.That(model.Cancelled.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(endIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(messages.Skip(endIndex + 1), Is.Empty);
            Assert.That(messages.OfType<VoiceResponseDoneMessage>(), Is.Empty);
            Assert.That(messages.OfType<VoiceErrorMessage>(), Is.Empty);
        });
    }

    [Test]
    public async Task ConcurrentTermination_WaitsForSharedWorkAndSendsOneEndCall()
    {
        var coordinator = new VoiceResponseCoordinator(
            new ScriptedModelClient("unused"),
            NullLogger<VoiceAgent>.Instance);
        var state = new VoiceConnectionState();
        Assert.That(state.TryBeginStart(), Is.True);
        state.Activate();
        var operation = new ResponseOperation("resp_termination", "in_termination");
        Assert.That(state.TryReserveOperation(operation), Is.True);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Work = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, operation.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                await releaseCancellation.Task;
            }
        });
        var session = new CapturingVoiceSession();

        var first = coordinator.TerminateAndEndCallAsync(
            session,
            state,
            VoiceEndCallMode.Drain,
            CancellationToken.None);
        await cancellationObserved.Task.WaitAsync(TestTimeout);
        var second = coordinator.TerminateAndEndCallAsync(
            session,
            state,
            VoiceEndCallMode.Immediate,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.IsCompleted, Is.False);
            Assert.That(session.Messages.OfType<VoiceEndCallMessage>(), Is.Empty);
        });
        releaseCancellation.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TestTimeout);
        Assert.That(session.Messages.OfType<VoiceEndCallMessage>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task UserMessage_StreamsModelResponse()
    {
        var model = new ScriptedModelClient("Hello", " world");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("How are you?"));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);

        var created = session.Messages.OfType<VoiceResponseCreatedMessage>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(created.InReplyTo?.Single(), Does.StartWith("in_test_"));
            Assert.That(session.Messages.Select(message => message.MessageType), Is.EqualTo(new[]
            {
                "response.created",
                "response.output_text.delta",
                "response.output_text.delta",
                "response.output_text.done",
                "response.done",
            }));
            Assert.That(
                session.Messages.OfType<VoiceResponseOutputTextDoneMessage>().Single().Text,
                Is.EqualTo("Hello world"));
            Assert.That(model.Requests.Single().Last().Content, Is.EqualTo("How are you?"));
        });
    }

    [Test]
    public async Task NoneCommand_DeclinesInputWithoutOpeningResponse()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/none"));

        var none = session.Messages.OfType<VoiceResponseNoneMessage>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(none.InReplyTo?.Single(), Does.StartWith("in_test_"));
            Assert.That(session.Messages.OfType<VoiceResponseCreatedMessage>(), Is.Empty);
        });
    }

    [Test]
    public async Task DoneCommand_SendsOnlyCompletedOutput()
    {
        var agent = CreateAgent(new ScriptedModelClient("Complete", " answer"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/done explain this"));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);

        Assert.Multiple(() =>
        {
            Assert.That(session.Messages.OfType<VoiceResponseOutputTextDeltaMessage>(), Is.Empty);
            Assert.That(
                session.Messages.OfType<VoiceResponseOutputTextDoneMessage>().Single().Text,
                Is.EqualTo("Complete answer"));
        });
    }

    [Test]
    public async Task VoiceCommand_AttachesSynthesisPatch()
    {
        var agent = CreateAgent(new ScriptedModelClient("Faster"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/voice speak faster"));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);

        var completed = session.Messages.OfType<VoiceResponseOutputTextDoneMessage>().Single();
        Assert.That(completed.Voice?.ToString(), Is.EqualTo("{\"rate\":\"+10%\"}"));
    }

    [Test]
    public async Task ProactiveCommand_WaitsForAdmissionBeforeOutput()
    {
        var model = new ScriptedModelClient("Proactive reply");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/proactive check in"));

        var created = session.Messages.OfType<VoiceResponseCreatedMessage>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(created.InReplyTo, Is.Null);
            Assert.That(created.AdmissionTimeoutMs, Is.EqualTo(5000));
            Assert.That(session.Messages.OfType<VoiceResponseOutputTextDoneMessage>(), Is.Empty);
            Assert.That(model.Requests, Is.Empty);
        });

        await agent.ResponseAcceptedAsync(
            session,
            new VoiceResponseAcceptedEvent(
                "m_accepted",
                DateTimeOffset.UtcNow,
                created.ResponseId));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);

        Assert.Multiple(() =>
        {
            Assert.That(model.Requests.Single().Last().Content, Is.EqualTo("check in"));
            Assert.That(
                session.Messages.OfType<VoiceResponseOutputTextDoneMessage>().Single().Text,
                Is.EqualTo("Proactive reply"));
        });
    }

    [Test]
    public async Task DroppedProactiveRequest_DoesNotGenerateOutput()
    {
        var model = new ScriptedModelClient("must not run");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/proactive check in"));
        var created = session.Messages.OfType<VoiceResponseCreatedMessage>().Single();
        await agent.ResponseDroppedAsync(
            session,
            new VoiceResponseDroppedEvent(
                "m_dropped",
                DateTimeOffset.UtcNow,
                created.ResponseId,
                "superseded"));
        await agent.ResponseAcceptedAsync(
            session,
            new VoiceResponseAcceptedEvent(
                "m_late_accept",
                DateTimeOffset.UtcNow,
                created.ResponseId));

        Assert.Multiple(() =>
        {
            Assert.That(model.Requests, Is.Empty);
            Assert.That(session.Messages.OfType<VoiceResponseOutputTextDoneMessage>(), Is.Empty);
        });
    }

    [Test]
    public async Task NoInput_UsesBridgeInputAndFixedReminder()
    {
        var model = new ScriptedModelClient("unused");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.NoInputAsync(
            session,
            new VoiceUserNoInputEvent("m_no_input", DateTimeOffset.UtcNow, "in_silence", 1));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                session.Messages.OfType<VoiceResponseCreatedMessage>().Single().InReplyTo,
                Is.EqualTo(new[] { "in_silence" }));
            Assert.That(
                session.Messages.OfType<VoiceResponseOutputTextDoneMessage>().Single().Text,
                Is.EqualTo("Are you still there?"));
            Assert.That(model.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task ThirdNoInput_EndsCall()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        await agent.NoInputAsync(
            session,
            new VoiceUserNoInputEvent("m_no_input", DateTimeOffset.UtcNow, "in_silence", 3));

        var end = session.Messages.OfType<VoiceEndCallMessage>().Single();
        Assert.That(end.Mode, Is.EqualTo(VoiceEndCallMode.Drain));
    }

    [Test]
    public async Task BargeIn_CancelsInFlightModelStream()
    {
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("Long answer"));
        await model.Started.Task.WaitAsync(TestTimeout);
        var responseId = session.Messages.OfType<VoiceResponseCreatedMessage>().Single().ResponseId;

        await agent.BargeInAsync(
            session,
            new VoiceBargeInEvent(
                "m_barge",
                DateTimeOffset.UtcNow,
                responseId,
                heardText: string.Empty));
        await model.Cancelled.Task.WaitAsync(TestTimeout);

        Assert.Multiple(() =>
        {
            Assert.That(session.Messages.OfType<VoiceResponseDoneMessage>(), Is.Empty);
            Assert.That(session.Messages.OfType<VoiceErrorMessage>(), Is.Empty);
        });
    }

    [Test]
    public async Task BargeInDuringGeneration_CommitsOnlyHeardText()
    {
        var model = new InterruptibleThenScriptedModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("interrupted question"));
        await model.Started.Task.WaitAsync(TestTimeout);
        var responseId = session.Messages.OfType<VoiceResponseCreatedMessage>().Single().ResponseId;
        await agent.BargeInAsync(
            session,
            new VoiceBargeInEvent(
                "m_barge",
                DateTimeOffset.UtcNow,
                responseId,
                heardText: "heard prefix"));
        await model.Cancelled.Task.WaitAsync(TestTimeout);

        await agent.UserMessageAsync(session, UserMessage("next question"));
        await WaitUntilAsync(() => model.Requests.Count == 2);

        Assert.That(model.Requests[1], Is.EqualTo(new[]
        {
            new ModelMessage(ModelMessageRole.User, "interrupted question"),
            new ModelMessage(ModelMessageRole.Assistant, "heard prefix"),
            new ModelMessage(ModelMessageRole.User, "next question"),
        }));
    }

    [Test]
    public async Task EmptyBargeInDuringGeneration_DoesNotRetainInterruptedPrompt()
    {
        var model = new InterruptibleThenScriptedModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("interrupted question"));
        await model.Started.Task.WaitAsync(TestTimeout);
        var responseId = session.Messages.OfType<VoiceResponseCreatedMessage>().Single().ResponseId;
        await agent.BargeInAsync(
            session,
            new VoiceBargeInEvent(
                "m_empty_barge",
                DateTimeOffset.UtcNow,
                responseId,
                heardText: string.Empty));
        await model.Cancelled.Task.WaitAsync(TestTimeout);

        await agent.UserMessageAsync(session, UserMessage("next question"));
        await WaitUntilAsync(() => model.Requests.Count == 2);

        Assert.That(model.Requests[1], Is.EqualTo(new[]
        {
            new ModelMessage(ModelMessageRole.User, "next question"),
        }));
    }

    [Test]
    public async Task BargeInAfterResponseDone_ReplacesUnheardSuffix()
    {
        var model = new ScriptedModelClient("full answer with unheard suffix");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("first question"));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);
        var responseId = session.Messages.OfType<VoiceResponseCreatedMessage>().Single().ResponseId;
        await agent.BargeInAsync(
            session,
            new VoiceBargeInEvent(
                "m_barge_after_done",
                DateTimeOffset.UtcNow,
                responseId,
                heardText: "heard prefix"));

        await agent.UserMessageAsync(session, UserMessage("second question"));
        await WaitUntilAsync(() => model.Requests.Count == 2);

        Assert.That(model.Requests[1], Is.EqualTo(new[]
        {
            new ModelMessage(ModelMessageRole.User, "first question"),
            new ModelMessage(ModelMessageRole.Assistant, "heard prefix"),
            new ModelMessage(ModelMessageRole.User, "second question"),
        }));
    }

    [Test]
    public async Task ResponseCancelledAfterDone_ReconcilesCallerHeardText()
    {
        var model = new ScriptedModelClient("full answer with unheard suffix");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("first question"));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);
        var responseId = session.Messages.OfType<VoiceResponseCreatedMessage>().Single().ResponseId;
        await agent.ResponseCancelledAsync(
            session,
            new VoiceResponseCancelledEvent(
                "m_cancelled_after_done",
                DateTimeOffset.UtcNow,
                responseId,
                "heard prefix"));

        await agent.UserMessageAsync(session, UserMessage("second question"));
        await WaitUntilAsync(() => model.Requests.Count == 2);

        Assert.That(model.Requests[1], Is.EqualTo(new[]
        {
            new ModelMessage(ModelMessageRole.User, "first question"),
            new ModelMessage(ModelMessageRole.Assistant, "heard prefix"),
            new ModelMessage(ModelMessageRole.User, "second question"),
        }));
    }

    [Test]
    public async Task OversizedModelOutput_FailsWithoutCompletingResponse()
    {
        var model = new ScriptedModelClient(new string('x', 512 * 1024 + 1));
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("generate too much"));
        await WaitUntilAsync(() => session.Messages.OfType<VoiceErrorMessage>().Any());

        Assert.Multiple(() =>
        {
            Assert.That(session.Messages.OfType<VoiceResponseDoneMessage>(), Is.Empty);
            Assert.That(session.Messages.OfType<VoiceResponseOutputTextDoneMessage>(), Is.Empty);
        });
    }

    [Test]
    public async Task ResponseTimeout_CancelsInFlightModelStream()
    {
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("Long answer"));
        await model.Started.Task.WaitAsync(TestTimeout);
        var responseId = session.Messages.OfType<VoiceResponseCreatedMessage>().Single().ResponseId;
        await agent.ResponseTimeoutAsync(
            session,
            new VoiceResponseTimeoutEvent(
                "m_timeout",
                DateTimeOffset.UtcNow,
                "idle",
                responseId));
        await model.Cancelled.Task.WaitAsync(TestTimeout);

        Assert.That(session.Messages.OfType<VoiceResponseDoneMessage>(), Is.Empty);
    }

    [Test]
    public async Task SelfCancel_WaitsForTerminalCallback()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/cancel correction"));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseCancelMessage>().Count() == 1);
        var cancel = session.Messages.OfType<VoiceResponseCancelMessage>().Single();
        await agent.ResponseCancelledAsync(
            session,
            new VoiceResponseCancelledEvent(
                "m_cancelled",
                DateTimeOffset.UtcNow,
                cancel.ResponseId,
                "correction"));

        Assert.Multiple(() =>
        {
            Assert.That(session.Messages.OfType<VoiceResponseDoneMessage>(), Is.Empty);
            Assert.That(cancel.Reason, Is.EqualTo("sample_self_correction"));
        });
    }

    [Test]
    public async Task ErrorCommand_SendsResponseScopedError()
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/error"));

        var created = session.Messages.OfType<VoiceResponseCreatedMessage>().Single();
        var error = session.Messages.OfType<VoiceErrorMessage>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(error.ResponseId, Is.EqualTo(created.ResponseId));
            Assert.That(error.Code, Is.EqualTo("sample_error"));
            Assert.That(session.Messages.OfType<VoiceResponseDoneMessage>(), Is.Empty);
        });
    }

    [TestCase("/end", VoiceEndCallMode.Drain)]
    [TestCase("/end-now", VoiceEndCallMode.Immediate)]
    public async Task EndCommands_SelectRequestedMode(string command, VoiceEndCallMode expected)
    {
        var agent = CreateAgent(new ScriptedModelClient("unused"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage(command));

        Assert.That(session.Messages.OfType<VoiceEndCallMessage>().Single().Mode, Is.EqualTo(expected));
    }

    [Test]
    public async Task CompletedTurns_AreIncludedInNextModelRequest()
    {
        var model = new ScriptedModelClient("answer");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("first question"));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);
        await agent.UserMessageAsync(session, UserMessage("second question"));
        await WaitUntilAsync(
            () => session.Messages.OfType<VoiceResponseDoneMessage>().Count() == 2);

        Assert.That(model.Requests[1], Is.EqualTo(new[]
        {
            new ModelMessage(ModelMessageRole.User, "first question"),
            new ModelMessage(ModelMessageRole.Assistant, "answer"),
            new ModelMessage(ModelMessageRole.User, "second question"),
        }));
    }

    [Test]
    public async Task SessionEnd_CancelsWorkAndClearsHistory()
    {
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("before end"));
        await model.Started.Task.WaitAsync(TestTimeout);
        await agent.SessionEndAsync(
            session,
            new VoiceSessionEndEvent("m_end", DateTimeOffset.UtcNow, "caller_hangup"));
        await model.Cancelled.Task.WaitAsync(TestTimeout);

        Assert.That(session.Messages.OfType<VoiceResponseDoneMessage>(), Is.Empty);
    }

    [Test]
    public async Task SessionEnd_DoesNotClearAnotherSessionsResponseHistory()
    {
        var model = new ScriptedModelClient("full answer");
        var agent = CreateAgent(model);
        var endedSession = new CapturingVoiceSession();
        var activeSession = new CapturingVoiceSession();

        await agent.UserMessageAsync(endedSession, UserMessage("ended question"));
        await WaitUntilAsync(
            () => endedSession.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);
        await agent.UserMessageAsync(activeSession, UserMessage("active question"));
        await WaitUntilAsync(
            () => activeSession.Messages.OfType<VoiceResponseDoneMessage>().Count() == 1);
        var activeResponseId = activeSession.Messages
            .OfType<VoiceResponseCreatedMessage>()
            .Single()
            .ResponseId;

        await agent.SessionEndAsync(
            endedSession,
            new VoiceSessionEndEvent("m_end", DateTimeOffset.UtcNow, "caller_hangup"));
        await agent.BargeInAsync(
            activeSession,
            new VoiceBargeInEvent(
                "m_barge_after_other_end",
                DateTimeOffset.UtcNow,
                activeResponseId,
                heardText: "heard prefix"));
        await agent.UserMessageAsync(activeSession, UserMessage("next question"));
        await WaitUntilAsync(() => model.Requests.Count == 3);

        Assert.That(model.Requests[2], Is.EqualTo(new[]
        {
            new ModelMessage(ModelMessageRole.User, "active question"),
            new ModelMessage(ModelMessageRole.Assistant, "heard prefix"),
            new ModelMessage(ModelMessageRole.User, "next question"),
        }));
    }

    [Test]
    public void ResponseHistoryCapacity_IsIndependentPerSession()
    {
        var store = new ResponseHistoryStore();
        var firstSession = new CapturingVoiceSession();
        var busySession = new CapturingVoiceSession();
        var firstHistory = new ConversationHistory();
        var busyHistory = new ConversationHistory();
        store.Track(firstSession, "resp_first", firstHistory, "first question");
        for (var index = 0; index < 65; index++)
        {
            store.Track(busySession, $"resp_busy_{index}", busyHistory, $"busy {index}");
        }

        store.Reconcile(firstSession, "resp_first", "heard answer");

        Assert.That(firstHistory.CreateRequest("next question"), Is.EqualTo(new[]
        {
            new ModelMessage(ModelMessageRole.User, "first question"),
            new ModelMessage(ModelMessageRole.Assistant, "heard answer"),
            new ModelMessage(ModelMessageRole.User, "next question"),
        }));
    }

    [Test]
    public void EmptyHeardText_PreventsLaterConcurrentCommit()
    {
        var history = new ConversationHistory();

        history.Reconcile("resp_empty", "interrupted question", string.Empty);
        history.Commit("resp_empty", "interrupted question", "unheard answer");

        Assert.That(history.CreateRequest("next question"), Is.EqualTo(new[]
        {
            new ModelMessage(ModelMessageRole.User, "next question"),
        }));
    }

    [Test]
    public void CompletedResponse_ClearsDiscardTombstone()
    {
        var history = new ConversationHistory();

        history.Reconcile("resp_empty", "interrupted question", string.Empty);
        Assert.That(history.DiscardedResponseCount, Is.EqualTo(1));

        history.CompleteResponse("resp_empty");

        Assert.That(history.DiscardedResponseCount, Is.Zero);
    }

    [Test]
    public async Task ModelSpan_InheritsResponseSpan()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == ModelTelemetry.SourceName ||
                source.Name == ResponseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("voice-callback").Start();
        var agent = CreateAgent(new ScriptedModelClient("answer"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("hello"));
        await WaitUntilAsync(() => activities.Count == 2);

        var modelActivity = activities.Single(activity => activity.Source.Name == ModelTelemetry.SourceName);
        var responseActivity = activities.Single(activity => activity.Source.Name == ResponseTelemetry.SourceName);
        var timeToFirstChunk = (double)modelActivity.GetTagItem(ModelTelemetry.TimeToFirstChunkTag)!;
        var firstTokenLatencyMs = (double)modelActivity.GetTagItem(ModelTelemetry.FirstTokenLatencyTag)!;
        var responseDurationMs = (double)modelActivity.GetTagItem(ModelTelemetry.ResponseDurationTag)!;
        Assert.Multiple(() =>
        {
            Assert.That(responseActivity.ParentSpanId, Is.EqualTo(parent.SpanId));
            Assert.That(modelActivity.ParentSpanId, Is.EqualTo(responseActivity.SpanId));
            Assert.That(timeToFirstChunk, Is.EqualTo(firstTokenLatencyMs / 1000).Within(0.000001));
            Assert.That(firstTokenLatencyMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(responseDurationMs, Is.GreaterThanOrEqualTo(firstTokenLatencyMs));
        });
    }

    [Test]
    public async Task ResponseSpan_RecordsUserMessageToResponseDoneSendCompletionDuration()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResponseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("voice-callback").Start();
        var agent = CreateAgent(new ScriptedModelClient("answer"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("hello"));
        await WaitUntilAsync(() => activities.Count == 1);

        var activity = activities.Single();
        var responseDurationMs = (double)activity.GetTagItem(ResponseTelemetry.ResponseDurationTag)!;
        var responseId = session.Messages.OfType<VoiceResponseDoneMessage>().Single().ResponseId;
        Assert.Multiple(() =>
        {
            Assert.That(activity.ParentSpanId, Is.EqualTo(parent.SpanId));
            Assert.That(activity.GetTagItem(ResponseTelemetry.ResponseIdTag), Is.EqualTo(responseId));
            Assert.That(
                activity.GetTagItem(ResponseTelemetry.InputItemIdTag),
                Is.EqualTo(session.Messages.OfType<VoiceResponseCreatedMessage>().Single().InReplyTo!.Single()));
            Assert.That(responseDurationMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatusCode.Ok));
            Assert.That(
                activity.GetTagItem(ResponseTelemetry.OutcomeTag),
                Is.EqualTo("response"));
        });
    }

    [Test]
    public async Task ResponseSpan_RecordsTimeoutOutcome()
    {
        using var capture = new ResponseActivityCapture();
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("hello"));
        await model.Started.Task.WaitAsync(TestTimeout);
        var responseId = session.Messages.OfType<VoiceResponseCreatedMessage>().Single().ResponseId;
        await agent.ResponseTimeoutAsync(
            session,
            new VoiceResponseTimeoutEvent(
                "m_timeout",
                DateTimeOffset.UtcNow,
                "first_output",
                responseId));
        var activity = await capture.WaitForSingleAsync();

        AssertResponseActivity(capture, activity, "timeout", ActivityStatusCode.Error);
    }

    [Test]
    public async Task ResponseSpan_RecordsCancelledOutcome()
    {
        using var capture = new ResponseActivityCapture();
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("hello"));
        await model.Started.Task.WaitAsync(TestTimeout);
        var responseId = session.Messages.OfType<VoiceResponseCreatedMessage>().Single().ResponseId;
        await agent.BargeInAsync(
            session,
            new VoiceBargeInEvent(
                "m_barge",
                DateTimeOffset.UtcNow,
                responseId,
                heardText: string.Empty));
        var activity = await capture.WaitForSingleAsync();

        AssertResponseActivity(capture, activity, "cancelled", ActivityStatusCode.Ok);
    }

    [Test]
    public async Task ResponseSpan_RecordsErrorOutcome()
    {
        using var capture = new ResponseActivityCapture();
        var agent = CreateAgent(new ScriptedModelClient(new string('x', 512 * 1024 + 1)));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("generate too much"));
        var activity = await capture.WaitForSingleAsync();

        AssertResponseActivity(capture, activity, "error", ActivityStatusCode.Error);
    }

    [Test]
    public async Task ResponseSpan_RecordsEndCallOutcome()
    {
        using var capture = new ResponseActivityCapture();
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("hello"));
        await model.Started.Task.WaitAsync(TestTimeout);
        await agent.SessionEndAsync(
            session,
            new VoiceSessionEndEvent("m_end", DateTimeOffset.UtcNow, "caller_hangup"));
        var activity = await capture.WaitForSingleAsync();

        AssertResponseActivity(capture, activity, "end_call", ActivityStatusCode.Ok);
    }

    [Test]
    public async Task ResponseSpan_RecordsTransportErrorOutcome()
    {
        using var capture = new ResponseActivityCapture();
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("hello"));
        await model.Started.Task.WaitAsync(TestTimeout);
        agent.ConnectionTerminating(session);
        var activity = await capture.WaitForSingleAsync();

        AssertResponseActivity(capture, activity, "transport_error", ActivityStatusCode.Error);
    }

    [Test]
    public async Task ResponseSpan_RecordsAbandonedOutcomeOnDispose()
    {
        using var capture = new ResponseActivityCapture();

        using (var operation = new ResponseOperation(
            "resp_abandoned",
            "in_abandoned",
            Stopwatch.GetTimestamp()))
        {
        }
        var activity = await capture.WaitForSingleAsync();

        AssertResponseActivity(capture, activity, "abandoned", ActivityStatusCode.Error);
    }

    [Test]
    public async Task NoneOutcome_DoesNotCreateResponseActivity()
    {
        using var capture = new ResponseActivityCapture();
        var agent = CreateAgent(new ScriptedModelClient("must not run"));
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/none"));

        Assert.Multiple(() =>
        {
            Assert.That(session.Messages.OfType<VoiceResponseNoneMessage>().Count(), Is.EqualTo(1));
            Assert.That(session.Messages.OfType<VoiceResponseCreatedMessage>(), Is.Empty);
            Assert.That(capture.Activities, Is.Empty);
        });
    }

    [Test]
    public async Task FixedResponseSpan_RecordsResponseDurationWithoutModelCall()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResponseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var model = new ScriptedModelClient("must not run");
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("/help"));
        await WaitUntilAsync(() => activities.Count == 1);

        var activity = activities.Single();
        Assert.Multiple(() =>
        {
            Assert.That(
                (double)activity.GetTagItem(ResponseTelemetry.ResponseDurationTag)!,
                Is.GreaterThanOrEqualTo(0));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatusCode.Ok));
            Assert.That(model.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task ResponseSpan_CompletesAfterResponseDoneSendCompletes()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResponseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var agent = CreateAgent(new ScriptedModelClient("answer"));
        var session = new BlockingResponseDoneVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("hello"));
        await session.ResponseDoneSendStarted.Task.WaitAsync(TestTimeout);

        Assert.That(activities, Is.Empty);

        session.ReleaseResponseDoneSend.TrySetResult();
        await WaitUntilAsync(() => activities.Count == 1);

        var activity = activities.Single();
        Assert.Multiple(() =>
        {
            Assert.That(
                (double)activity.GetTagItem(ResponseTelemetry.ResponseDurationTag)!,
                Is.GreaterThanOrEqualTo(0));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatusCode.Ok));
            Assert.That(session.Messages.OfType<VoiceResponseDoneMessage>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ResponseSpan_DoesNotReplaceCallbackAmbientActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResponseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("voice-callback").Start();
        var model = new BlockingModelClient();
        var agent = CreateAgent(model);
        var session = new CapturingVoiceSession();

        await agent.UserMessageAsync(session, UserMessage("hello"));
        await model.Started.Task.WaitAsync(TestTimeout);

        Assert.That(Activity.Current, Is.SameAs(parent));

        await agent.SessionEndAsync(
            session,
            new VoiceSessionEndEvent("m_end", DateTimeOffset.UtcNow, "caller_hangup"));
        await model.Cancelled.Task.WaitAsync(TestTimeout);
    }

    [Test]
    public void ModelEndpoint_AcceptsFoundryProjectEndpoint()
    {
        using var endpoint = new EnvironmentVariableScope(
            "FOUNDRY_PROJECT_ENDPOINT",
            "https://test.services.ai.azure.com/api/projects/test-project/");
        using var deployment = new EnvironmentVariableScope(
            "AZURE_AI_MODEL_DEPLOYMENT_NAME",
            "test-model");

        var client = AzureOpenAIResponsesClient.CreateFromEnvironment();

        Assert.That(
            client.Endpoint,
            Is.EqualTo(new Uri("https://test.services.ai.azure.com/api/projects/test-project/openai/v1")));
    }

    [Test]
    public void ModelEndpoint_RejectsCleartextHttp()
    {
        using var endpoint = new EnvironmentVariableScope(
            "FOUNDRY_PROJECT_ENDPOINT",
            "http://test.services.ai.azure.com/api/projects/test-project");
        using var deployment = new EnvironmentVariableScope(
            "AZURE_AI_MODEL_DEPLOYMENT_NAME",
            "test-model");

        Assert.That(
            AzureOpenAIResponsesClient.CreateFromEnvironment,
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.Contains("HTTPS"));
    }

    [TestCase("https://test.services.ai.azure.com/")]
    [TestCase("https://test.services.ai.azure.com/openai/v1")]
    [TestCase("https://test.services.ai.azure.com/api/projects/test-project/openai/v1")]
    public void ModelEndpoint_RejectsNonProjectPath(string value)
    {
        using var endpoint = new EnvironmentVariableScope("FOUNDRY_PROJECT_ENDPOINT", value);
        using var deployment = new EnvironmentVariableScope(
            "AZURE_AI_MODEL_DEPLOYMENT_NAME",
            "test-model");

        Assert.That(
            AzureOpenAIResponsesClient.CreateFromEnvironment,
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.Contains("/api/projects/<project>"));
    }

    [Test]
    public void ModelEndpoint_RejectsEmbeddedCredentials()
    {
        using var endpoint = new EnvironmentVariableScope(
            "FOUNDRY_PROJECT_ENDPOINT",
            "https://user:secret@test.services.ai.azure.com/api/projects/test-project");
        using var deployment = new EnvironmentVariableScope(
            "AZURE_AI_MODEL_DEPLOYMENT_NAME",
            "test-model");

        Assert.That(
            AzureOpenAIResponsesClient.CreateFromEnvironment,
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.Contains("absolute HTTPS"));
    }

    private static TestableVoiceAgent CreateAgent(IStreamingModelClient modelClient) =>
        new(modelClient, NullLogger<VoiceAgent>.Instance);

    private static VoiceSessionStartEvent SessionStart(string protocolVersion) =>
        new(
            "m_start",
            DateTimeOffset.UtcNow,
            protocolVersion,
            reconnect: false,
            new VoiceResponseTimeouts(1000, 1000, 30000),
            greeting: null,
            noInputTimeoutMs: null,
            caller: null);

    private static VoiceUserMessageEvent UserMessage(string text, string? itemId = null) =>
        new(
            "m_user",
            DateTimeOffset.UtcNow,
            itemId ?? $"in_test_{Interlocked.Increment(ref s_inputSequence)}",
            new[] { new VoiceInputTextPart(text) });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static void AssertResponseActivity(
        ResponseActivityCapture capture,
        Activity activity,
        string outcome,
        ActivityStatusCode status)
    {
        Assert.Multiple(() =>
        {
            Assert.That(capture.Activities, Has.Count.EqualTo(1));
            Assert.That(activity.Status, Is.EqualTo(status));
            Assert.That(activity.GetTagItem(ResponseTelemetry.OutcomeTag), Is.EqualTo(outcome));
            Assert.That(
                (double)activity.GetTagItem(ResponseTelemetry.ResponseDurationTag)!,
                Is.GreaterThanOrEqualTo(0));
        });
    }

    private sealed class TestableVoiceAgent : VoiceAgent
    {
        private readonly object _sessionSync = new();
        private readonly HashSet<VoiceSession> _startedSessions = new();

        internal TestableVoiceAgent(
            IStreamingModelClient modelClient,
            Microsoft.Extensions.Logging.ILogger<VoiceAgent> logger)
            : base(modelClient, logger)
        {
        }

        internal Task SessionStartAsync(VoiceSession session, VoiceSessionStartEvent start) =>
            OnSessionStartAsync(session, start, CancellationToken.None);

        internal async Task UserMessageAsync(VoiceSession session, VoiceUserMessageEvent message)
        {
            await EnsureStartedAsync(session);
            await OnUserMessageAsync(session, message, CancellationToken.None);
        }

        internal Task RawUserMessageAsync(VoiceSession session, VoiceUserMessageEvent message) =>
            OnUserMessageAsync(session, message, CancellationToken.None);

        internal async Task NoInputAsync(VoiceSession session, VoiceUserNoInputEvent noInput)
        {
            await EnsureStartedAsync(session);
            await OnUserNoInputAsync(session, noInput, CancellationToken.None);
        }

        internal Task BargeInAsync(VoiceSession session, VoiceBargeInEvent bargeIn) =>
            OnBargeInAsync(session, bargeIn, CancellationToken.None);

        internal Task ResponseAcceptedAsync(
            VoiceSession session,
            VoiceResponseAcceptedEvent accepted) =>
            OnResponseAcceptedAsync(session, accepted, CancellationToken.None);

        internal Task ResponseDroppedAsync(
            VoiceSession session,
            VoiceResponseDroppedEvent dropped) =>
            OnResponseDroppedAsync(session, dropped, CancellationToken.None);

        internal Task ResponseCancelledAsync(
            VoiceSession session,
            VoiceResponseCancelledEvent cancelled) =>
            OnResponseCancelledAsync(session, cancelled, CancellationToken.None);

        internal Task ResponseTimeoutAsync(
            VoiceSession session,
            VoiceResponseTimeoutEvent timeout) =>
            OnResponseTimeoutAsync(session, timeout, CancellationToken.None);

        internal Task SessionEndAsync(VoiceSession session, VoiceSessionEndEvent end) =>
            OnSessionEndAsync(session, end, CancellationToken.None);

        internal void ConnectionTerminating(VoiceSession session) =>
            OnConnectionTerminating(session);

        private async Task EnsureStartedAsync(VoiceSession session)
        {
            lock (_sessionSync)
            {
                if (!_startedSessions.Add(session))
                {
                    return;
                }
            }

            await OnSessionStartAsync(session, SessionStart("1.0"), CancellationToken.None);
            switch (session)
            {
                case CapturingVoiceSession capturing:
                    capturing.ClearMessages();
                    break;
                case BlockingResponseDoneVoiceSession blocking:
                    blocking.ClearMessages();
                    break;
            }
        }
    }

    private sealed class ResponseActivityCapture : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _activities = new();
        private readonly ActivityListener _listener;

        internal ResponseActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ResponseTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _activities.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal IReadOnlyCollection<Activity> Activities => _activities.ToArray();

        internal async Task<Activity> WaitForSingleAsync()
        {
            await WaitUntilAsync(() => _activities.Count == 1);
            return _activities.Single();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        internal EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
