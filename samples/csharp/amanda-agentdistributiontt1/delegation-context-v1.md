# Continuing a delegated conversation

The router uses one generic Work IQ A2A tool. It discovers agents and
selects a target by its published description; it does not create a
connection for each specialist.

Work IQ returns a `contextId` on an A2A task, message or streaming event.
The next message must include that identifier to continue the specialist's
conversation. The parent model's `previous_response_id` is a different
identifier and does not preserve downstream context.

The wire contract is documented in the
[Work IQ A2A overview](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/a2a/overview)
and [quickstart](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/a2a/quickstart).

## How the sample stores continuation

`WorkIqA2AToolHandler` loads the target's context before sending a question
and retains the context returned by the service. The stored key includes
the tenant, agent user account, calling agent identity, endpoint, parent
conversation and exact opaque target-agent ID.

Chat and email use separate parent-conversation scopes. A standing job
uses its job ID, so the same job can continue across its approved surfaces
without sharing context with another job.

`ConversationStateStore` stores A2A pointers in the existing conversation
table, using an `a2a-` row-key prefix separate from Responses API pointers.
It uses `ConversationStateTableServiceUri`, falling back to the configured
`WorkItemsTableServiceUri`, and the existing `ConversationStateTableName`.
No new table or permission grant is required when that store is already
configured.

Conditional writes prevent competing first replies from replacing each
other's continuation. A configured storage failure is an error, not a
reason to repeat the question in a fresh conversation. If the remote
request already ran but its context could not be saved, the tool explicitly
warns not to repeat it because side effects may already exist.

With no table configured, the shared store uses memory and logs that
continuation will not survive a restart or work across replicas. This
development fallback is not durable storage.

## Starting deliberately new work

`ask_workiq_agent` accepts `agent_id`, `message` and optional
`start_new_conversation`. The last option defaults to `false`.

Use `true` only for an explicitly new, unrelated task. It clears the
selected specialist's pointer in the current parent conversation. It does
not delete or cancel remote tasks, clear another specialist's context, or
change permissions. It must not be used as an automatic retry for an
uncertain write.

## Accepted work is not submitted again

The client tries A2A v1 with `A2A-Version: 1.0`, `SendMessage` and
`ROLE_USER`. A legacy fallback uses the matching v0.3 header, method and
role only after an explicit method or parameter rejection.

Once a request is accepted, recovery retrieves the existing task with
`GetTask` or subscribes to it with `SubscribeToTask`. It does not send the
instruction again through a streaming send. Working tasks retain their
existing queued follow-up behavior.

## Approval and artifact boundaries

Conversation continuity is not approval. This change does not invent
reviewed-draft annotations, turn a text acknowledgement into a native
save action, or bypass a specialist's approval requirements.

An A2A answer can contain a refusal, a preview or a file reference.
Returned text is not proof that a plan, task, app or file was saved.
Verify the actual artifact and preserve its access controls before
reporting a completed business operation.

## Focused regression checks

From the sample directory:

```powershell
dotnet test .\tests\AutopilotRouterAgentA2A.Tests\AutopilotRouterAgentA2A.Tests.csproj --no-restore --filter "FullyQualifiedName~A2AConversationTests|FullyQualifiedName~StandingJobTests|FullyQualifiedName~StandingWorkflowTests|FullyQualifiedName~CapabilityTests"
```

The tests cover continuation across handler/store recreation; isolation
between instances, channels, conversations, targets and jobs; explicit
reset; legacy negotiation; accepted-task recovery without resubmission;
and storage failures before and after a remote request.

A live check must also show the same returned context being sent on the
next real delegated turn. Passing the tests does not establish that a
specialist's native approval or artifact-download interface is supported.
