# Native delegation cards

Set `EnableDelegationCards` to `true` to show delegated work as native
Adaptive Cards in Teams. The default remains the existing text experience.
The card supplements that path; its Original response control retains the
specialist's full response.

This implementation uses the Activity attachment and Action.Submit
patterns demonstrated by
[FoundryChartsAgent](https://github.com/davrous/FoundryChartsAgent).
It does not embed the chart renderer, copy its Python implementation, or
assume SDK streaming works for agentic Teams requests.

## How a handoff becomes visible

`WorkIqA2AToolHandler` emits a host-owned observation immediately before
calling the selected agent and after receiving its real result.
`DelegationCardCoordinator` creates a message with an
`application/vnd.microsoft.card.adaptive` attachment, records the returned
message ID, then updates that same message.

The card names the specialist, shows the actual work requested, and
distinguishes working, waiting, response received, review required, saving,
verification and failure. A nonempty answer is not treated as a saved
business result. The generic A2A tool remains the only delegation path.

## Email can trigger the work

An authorized personal Teams conversation records a delivery route for
that requester and agent instance. The stored SDK conversation contains
its conversation reference and selected identity claims, not an access
token.

When an approved email sender has that current personal route, the email
handler uses a fresh proactive Teams turn to send and update the handoff
cards. The email still receives its ordinary response. Without a matching
authorized route, it remains an email-only interaction; the implementation
does not choose someone else's chat.

To exercise this flow, first speak to the agent in its personal Teams
chat, then send an ordinary email that requires specialist work. The
email does not need to name an agent or ask for a routing test.

## Review and approval

A complete supported Planner creation preview is rendered as a readable
plan and task list. Unsupported or incomplete preview fields cannot
receive an approval button. A card that exceeds the application's
24 KB display budget does not silently approve a shortened preview.

Action.Submit sends only an action kind, an opaque card reference and an
action name. It never sends an authoritative agent ID, destination,
conversation token or replacement draft.

On each click the server:

1. Rechecks normal tenant, direct-message/group-chat and participant access.
2. Loads the durable receipt in the current agent instance and conversation.
3. Requires the original requester or current manager; standing-job
   actions also recheck the job and its specialist permission.
4. Claims the approval with an Azure Tables conditional update.
5. Acquires the exact specialist conversation revision. Another turn,
   reset or concurrent request makes an old approval stale.
6. Sends exactly `Save` through the existing generic A2A handler.

No reviewed-draft annotation is fabricated, and no direct Planner or Graph
write is substituted. An uncertain Save is not retried by another click.
Do not save closes the card without sending a write to the specialist.

## Confirming a saved result

Returned Planner links must be HTTPS links to the supported Planner host,
with matching plan/task paths. The agent then uses a separate specialist
conversation to read the returned opaque IDs. Expected titles are not
included in that read request.

The card is marked saved only when the returned stored IDs and titles
match the reviewed work. Missing data, a mismatching read, another preview
or an incomplete operation stays explicitly unconfirmed. Open plan points
to the actual returned plan, not a fabricated or local substitute.

## Durable state and late results

Card receipts, private delivery routes and short-lived conversation
coordination rows reuse the configured conversation table. They are scoped
to the owning tenant and agent user, and bind the calling identity, parent
conversation and target specialist. No new table or permission grant is
needed when the existing conversation store is available.

The pending-delegation queue retains the card reference and serialized
SDK conversation. Late results and late verification update the original
card through an authenticated proactive turn. A failed delivery does not
remove the pending record. Approval coordination and the card receipt
prevent a duplicate Save; a lost message receipt is not blindly sent again.

## Validation

From the sample folder:

```powershell
dotnet test .\tests\AutopilotRouterAgentA2A.Tests\AutopilotRouterAgentA2A.Tests.csproj --no-restore --filter "FullyQualifiedName~DelegationCardTests|FullyQualifiedName~A2AConversationTests|FullyQualifiedName~StandingWorkflowTests|FullyQualifiedName~StandingJobTests|FullyQualifiedName~CapabilityTests"
```

Also verify the deployed Activity path: observe the real progress card,
click its actual approval button, inspect the same-message update, and
read the resulting stored plan/task from the service. A JSON snapshot or
successful build is not proof that the intended Teams client renders and
submits the card.
