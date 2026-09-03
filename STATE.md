# STATE

Last updated: 2026-09-03. Revised the same day after locating the source
session (`9bc6280f-…`) in the local session store: the decisions section is
now cited from that conversation rather than reconstructed from the diff.
Revised again after tracing the delegation path end to end, which showed
multi-agent fan-out is built rather than missing (item 4 below).

Read this before touching anything in this folder. Read AGENTS.md next.

---

## What this repo is

A working copy of the Foundry samples collection: the public catalogue of
runnable samples for Microsoft Foundry agents. Amanda uses it to build and
ship her own autopilot samples, not to maintain the catalogue as a whole.

The checkout is **sparse**. Only three directories are materialised on disk:

```
samples/csharp/foundry-workstream-manager-autopilot-agent
samples/csharp/foundry-autopilot-router-agent
samples/csharp/foundry-autopilot-router-agent-a2a
```

Everything else in the repo (root README, infrastructure, other language
samples) exists in git history but is deliberately absent from the working
tree. The patterns live in `.git/info/sparse-checkout`, in non-cone mode.
`AGENTS.md` and `STATE.md` were added to those patterns so the two root
files stay on disk. If you add another root file, add its pattern too or
git will quietly remove it from the working tree.

## The folder name is misleading

The folder is `C:\Users\fosteramanda\Code-Samples\foundry-samples-ado`.
The `-ado` suffix is **historical and means nothing today**. There is no
Azure DevOps remote. Both remotes are GitHub:

| Remote | URL | What it is |
|---|---|---|
| `origin` | `https://github.com/fosteramanda/foundry-samples.git` | Amanda's fork. Push here. |
| `upstream` | `https://github.com/microsoft-foundry/foundry-samples.git` | The public Microsoft repo. Do not push. |

Do not rename the folder to "fix" this, and do not assume an ADO pipeline
exists. Treat `-ado` as a local label only.

`upstream/main` is not fetched into this clone, so there is no local
remote-tracking ref for it. Any comparison against upstream needs an
explicit `git fetch upstream` first.

## Branch

Working branch is **`autopilot-toolbox`**, currently level with
`origin/autopilot-toolbox` at `e357e44` ("Split the router samples into an
A2A/MCP pair; fix an extraction bug that ate answers"). A second local
branch, `claude/routing-slides-clarity-14fcfd`, sits on the same commit and
appears to be a leftover. Nothing depends on it.

---

## The active sample

`samples\csharp\foundry-autopilot-router-agent-a2a`

### What it does

A Foundry hosted agent called the Workstream Manager, published as an AI
Teammate into Microsoft Agent 365 and hired as an instance by a manager. It
lives in Teams group chats and one-to-one DMs, and it also answers email and
replies to @-mentions in Word document comments.

Its day job is keeping track of a workstream: capturing the small verbal
commitments people forget ("Amanda will file a bug for that"), marking the
source message with a pin reaction, persisting owner, description, status
and ETA in Azure Table Storage, and answering questions about the workstream
from chat history plus whatever sources it has been granted.

Two behaviours matter more than the rest, because they are the reason this
sample exists:

1. **It only speaks when spoken to.** In group chats a free deterministic
   pre-filter drops messages that @-mention other people and contain no
   second-person reference. Genuinely ambiguous messages go to a cheap LLM
   judge that decides yes or no. It always answers DMs and explicit mentions.
2. **It routes questions to other agents.** When it does not know something,
   it discovers a specialist agent and hands the question over.

### The A2A/MCP pair

This sample is **one arm of a matched pair**, and that is easy to miss:

| Sample | Arm | How it reaches another agent |
|---|---|---|
| `foundry-autopilot-router-agent` | MCP | Work IQ MCP `ask`, with an `agentId` |
| `foundry-autopilot-router-agent-a2a` | A2A | Local A2A tools, direct HTTP |

They were split at `e357e44`. The point of the pair is to compare the two
transports honestly, which is why the toolbox decision below matters so much.
If you change one arm, ask whether the other needs the same change.

### What the comparison actually found

This is the headline result of the source session and the reason the pair
exists. It was missing from the first draft of this file. Measured
2026-08-18 to 2026-08-19 against `workiq.svc.cloud.microsoft`; re-verify
before relying on it, because it is a platform observation with a date on
it, not a property of this code.

| Step | A2A | MCP |
|---|---|---|
| List agents | works | works |
| Agent card / description | works | n/a |
| Invoke a **Copilot Studio** agent | works | works |
| Invoke a **Foundry-published** agent | returns empty | returns empty |

Two things make this worth writing down carefully.

**The failure is silent.** The task reports completed, HTTP is 200,
`isError` is false, and the answer is simply absent. A caller that checks
status rather than content sees success. That is precisely the failure mode
this sample was built to make visible, and it is why the delegation cue in
decision 4 is rendered from recorded calls rather than from the model's
own claim.

**The agents themselves are healthy.** Called directly on the Foundry
responses API they answer normally, in the right language, with citations.
The break is the hop from Work IQ into the agent, not the agent. Amanda
tested this from both directions, as a human caller and with the autopilot
as caller, over both transports, and the result held across all four.

Consequence for reading the code: the A2A arm is not "broken" because
delegation to a Foundry agent comes back empty. That is the finding. An
engineer on Amanda's side offered to pull Foundry-side logs for specific
failing agent names (T308); that thread was still open when the session
ended. The fullest write-up is a scorecard document Amanda had saved to
`Downloads` as v4 (T281), outside this repo.

### How it is structured

```
foundry-autopilot-router-agent-a2a/
  azure.yaml                  azd project definition
  infra/                      Bicep: project, monitoring, tables,
                              agent creation/deployment scripts, UMI
  scripts/                    PowerShell postprovision hooks
  src/workstream_manager_agent/
    Program.cs                DI wiring and host startup
    global.json               PINS .NET SDK 9.0.305
    appsettings.json          all behaviour flags, heavily commented
    ToolingManifest.json      the MCP tool manifest
    foundry-infra/Dockerfile
    AgentLogic/
      A365AgentApplication.cs turn host, proactive capture
      AgentInstructions.cs    the system prompt, built in sections
      IAgentLogicService.cs
      ResponsesApi/
        ResponsesApiAgentLogicService.cs   the main turn logic
        Helpers/  ResponsesApiClient, WorkIqA2AToolHandler,
                  WorkItemToolHandler, AccessControlService,
                  AddressedToAgentGate, McpServerHealthProbe,
                  ReactionService, TeamsActivityHelper
    Services/
      AgentTokenHelper.cs     agent-user token acquisition
      AgentTokenCredential.cs
      ConversationStateStore.cs
      WorkItemService.cs
      FoundryInstanceTelemetryInitializer.cs
      PendingDelegationStore.cs      NEW, uncommitted
      DelegationFollowUpService.cs   NEW, uncommitted
    Models/                   AgentMetadata, McpServerConfig
```

The prompt in `AgentInstructions.cs` is **assembled conditionally**, not
static. Sections are added or omitted based on configuration, so the agent
is never told it has a tool it does not hold. This is load-bearing; see the
ADO decision below.

### What works today

- The code compiled clean. Release build output is dated 2026-08-20
  00:38:46, ten seconds after the newest source edit
  (`PendingDelegationStore.cs`, 00:38:36). So the working tree as it stands,
  including the uncommitted work, built successfully at that point.
- Everything through `e357e44` is committed and pushed to the fork.

### What does not work today

- **The sample cannot be built on this machine right now.** `global.json`
  pins SDK `9.0.305` with `rollForward: latestFeature`, which will not roll
  forward to .NET 10. Only `10.0.303` is installed. `dotnet build` fails
  with "A compatible .NET SDK was not found". Fix by installing the .NET 9
  SDK. Do not "fix" it by editing `global.json`; the pin is intentional and
  the container builds against .NET 9.
- **The uncommitted asynchronous-delegation feature has never been verified
  at runtime**, on the evidence in this tree. It compiles. That is all that
  is known.
- **The readme contradicts the configuration.** The readme still advertises
  "Manages Azure DevOps" as a capability of this sample, but `ToolboxName`
  is now empty, so the ADO tools are not attached and the ADO prompt section
  is suppressed. As shipped, this arm has no Azure DevOps capability. Either
  the readme or the configuration has to give.
- There are no automated tests for this sample.

---

## Decisions, and why

**Provenance, verified.** An earlier draft of this file said the source
conversation was unavailable and that the decisions below were reconstructed
from the diff. That was wrong, and it has been corrected. The conversation
is recoverable from the local session store.

| | |
|---|---|
| Session id | `9bc6280f-942b-4616-bb60-29dedb3abf96` |
| Stored title | "Azure Login Session" (autogenerated, ignore it) |
| Ran | 2026-08-16 09:18 to 2026-08-21 16:02 |
| Turns | 329 (indices 0 to 328) |
| Working directory | `C:\Users\fosteramanda` (not this repo, which is why a search by repository path misses it) |

To read it again:

```sql
-- session_store_sql, source: local
SELECT turn_index, user_message, timestamp FROM turns
WHERE session_id = '9bc6280f-942b-4616-bb60-29dedb3abf96'
ORDER BY turn_index;
```

Turn numbers are cited below as `T<n>`. Decisions carrying a citation are
quoted from Amanda directly. Decisions without one are still inferred from
the diff and its rationale comments, and are marked *(from diff)*.

1. **Split the router sample into an A2A arm and an MCP arm.** One sample
   per transport, so each can be read on its own. Amanda's instruction was
   explicit about which folder is which: "`...router-agent-a2a` this should
   be a2a for both and then `...router-agent` should be mcp" (T298). "For
   both" means discovery *and* invocation.

2. **`ToolboxName` was deliberately emptied in this A2A arm.** This is the
   biggest decision in the diff, and Amanda drove it: "do not add mcp, just
   keep a2a" (T193), then "this should only be a2a for my agent
   autopilotroutera2a named Office of Amanda, not sure why mcp was there"
   (T312), then "I thought we got rid of toolbox mcp" (T319). The project
   toolbox bundles a `workiq` MCP
   tool whose `ask` accepts an optional `agentId`, which gives the model a
   second, MCP-based route to another agent. Measured behaviour: with the
   toolbox attached, the model chose `workiq___ask` and never touched the
   A2A tools at all. The arm was not testing what it claimed to test. The
   local handler already does discovery **and** invocation over direct HTTP,
   so no toolbox is needed here.

3. **Consequence, accepted: this arm loses Azure DevOps.** ADO tools arrive
   only through a toolbox. `BuildAdoSection` therefore returns empty when no
   toolbox is configured, rather than telling the agent that ADO is its
   source of truth for launches and backlog while it holds no ADO tool. The
   alternative was an agent that invents answers or claims a capability it
   cannot exercise. Still open: whether this arm should get ADO back.

4. **Attribution is rendered by the host, not by the model.** Amanda asked
   for this directly: "could we add visual delegation cues so when it does
   decide to delegate the end user knows, and when it doesn't delegate
   (because delegation should not always happen) then you don't see them"
   (T321). The prompt
   asks the model to name the agent it consulted, but a prompt is a request,
   not a guarantee. A model that delegates, receives nothing, and then
   answers from its own knowledge produces text indistinguishable from a
   successful hand-off, and that is exactly the case where it is most likely
   to omit the attribution. `WorkIqA2AToolHandler` records a delegation trail
   at the call site and the host appends a cue derived from calls that
   actually happened, so the cue cannot contradict reality. Empty when
   nothing was delegated, so ordinary turns look unchanged.

5. **Three outcomes, not two: Answered, NoAnswer, Pending.** Collapsing
   Pending into NoAnswer would tell the user an agent had nothing to say
   when an answer is in fact on its way.

6. **Slow delegations become follow-up messages instead of dead ends.** This
   is the last thing Amanda asked for, and it is the reason the uncommitted
   work exists. Her words (T328, the final turn of the session): "I ask it a
   question, it delegates to 1 agent. I ask another, it responds. I ask a
   third, it delegates. Then the first delegated response comes back, then
   the 3rd response goes back. Yes I think we want the delegation." An
   A2A agent can accept a request and finish minutes later. Blocking the turn
   would freeze the chat; abandoning the request would drop the question
   silently, which is the exact failure this sample exists to expose. So the
   turn ends immediately with "I have asked X", and a background poller
   delivers the answer into the same conversation afterwards.

7. **The pending queue is Azure Tables, not the SDK's `IStorage`.** An answer
   that arrives after a container restart must not be swallowed. Table
   storage is already provisioned for this agent and already carries a
   per-instance RBAC grant, so this added no new infrastructure. When no
   table is configured the store reports itself unavailable and the caller
   degrades to synchronous behaviour rather than dropping the question.

8. **Partition key is the agent name**, so one deployed agent never polls
   another agent's outstanding work.

9. **The original question is persisted alongside each pending delegation.**
   Answers arrive out of order and possibly minutes later, so every follow-up
   restates the question it belongs to. Without it, a bare answer landing
   after two unrelated turns is unreadable.

10. **`Proactive.StoreConversationAsync` runs inside the turn, and only when
    something is genuinely outstanding.** The conversation reference it
    captures does not exist outside a turn, and the common case should write
    nothing.

11. **The interface gained default no-op members** (`HasPendingDelegations`
    returning false, `PersistPendingDelegationsAsync` returning a completed
    task) so implementations without asynchronous delegation are unaffected.

12. **The poller gives up.** 45 attempts at 20 second intervals, both
    configurable. A task stuck in WORKING forever would otherwise be polled
    for the life of the container and the user would never learn that no
    answer is coming.

13. **Email gets the A2A tools too, but only those.** Without them, email was
    silently a second-class channel that answered product questions from the
    model's own knowledge with no signal that nobody had been asked. The
    work-item tools are excluded because they depend on
    `SetCurrentActivityContext`, a Teams concept that drives the pin reaction
    and is never set on the email path.

14. **A failed persist is logged as an error, loudly.** By that point the user
    has already been told a follow-up is coming, so the failure is a promise
    that cannot be kept.

The following were made verbally in the source session and are not visible in
the diff at all. They were missing from the first draft of this file.

15. **One generic A2A tool. Never per-agent connections.** Amanda, in
    capitals: "all you are supposed to do is an a2a single tool that calls
    downstream just like mcp. DO NOT EVER CONNECT THE AGENTS DIRECTLY"
    (T92). Treat this as a standing constraint on the sample, not a one-off.
    A future session that "helpfully" wires a specific downstream agent as
    its own tool or connection is breaking the design on purpose.

16. **The `-a2a` folder began as a copy of the router sample with entirely
    new infrastructure.** "Copy this code into new folder
    `foundry-autopilot-router-agent-a2a` and then create brand new infra,
    azd env, instance, all in Sweden Central, and name agent
    `autopilotroutera2a`. Make sure you do not create a new bot service"
    (T73). Region and the no-new-bot-service rule are both deliberate.

17. **The agent's persona is chief of staff, published as "Office of
    Amanda".** "Update prompt and instructions to be chief of staff
    autopilot, but do not edit any other tools/functionality" (T262). The
    instruction to leave surrounding functionality alone was explicit.

18. **Dead code gets deleted, not annotated.** An earlier turn left
    `AskViaCopilotChatAsync` in place but unreferenced with a comment
    explaining why. Amanda's response: "remove it" (T264). Do not leave
    commented-out or unreferenced paths in this sample as documentation.

19. **No speculative staged changes.** When work was staged for an approach
    the session had already measured as non-functional, Amanda rejected it
    twice: "why did you add staged changes, we just proved it won't work"
    (T271), "don't add" (T272).

20. **OAuth2 scopes are granted narrowly, and the Azure DevOps scope was
    questioned and then frozen.** The fix that unblocked Work IQ was adding
    resource apps to the blueprint's `requiredResourceAccess` so instances
    inherit them (T101). Amanda immediately narrowed it: "you don't need
    user_impersonation for everything, don't do that" (T102) and "why would
    you do that for Azure DevOps" (T103), settling on "let's just leave it,
    do not break anything" (T104-T105). So the ADO permission question was
    consciously parked, not overlooked. This is context for the ADO
    contradiction listed under "What does not work today".

21. **Routing must be dynamic, by description, not by hardcoded name.** "I
    want dynamic routing" (T316). The agent is expected to consult the
    roster and select a specialist from its description; naming the target
    agent in the prompt "would defeat the test" (T317). Amanda also asked
    the session to verify a downstream agent was not hardcoded (T128).

---

## What is left to do

1. Install the .NET 9 SDK and rebuild, to re-establish a green baseline.
2. Decide whether to commit the asynchronous-delegation work. It is
   currently uncommitted in the working tree (8 modified files, 2 new
   files). This STATE.md and AGENTS.md were committed on their own,
   deliberately, so that decision stays Amanda's.
3. Verify the follow-up loop end to end at runtime: delegate to a slow
   agent, let the turn end, confirm the answer is delivered proactively into
   the original conversation after a container restart.
4. **Verify the multi-agent delegation the session ended on.** Amanda's last
   two turns were requirements: T327, "can the main agent delegate to
   multiple at the same time?", and T328, the interleaving case where turn 1
   delegates, turn 2 answers directly, turn 3 delegates, and the turn-1
   answer arrives before the turn-3 answer. **Both are built.** Traced
   2026-09-03, end to end:

   | Link in the chain | Where | Supports many? |
   |---|---|---|
   | Model emits several tool calls in one response | `ResponsesApiClient.cs:210`, `foreach (var functionCall in functionCalls)` | yes |
   | Each pending hand-off recorded | `WorkIqA2AToolHandler._pendingHandoffs`, a `List` | yes |
   | All hand-offs persisted | `ResponsesApiAgentLogicService.cs:94`, `foreach (var handoff in ...PendingHandoffs)` | yes |
   | Row per delegation | `PendingDelegationStore`, `RowKey = Guid.NewGuid()`, commented "an agent can have several outstanding at once" | yes |
   | Poller drains all outstanding | `DelegationFollowUpService.PollOnceAsync`, `foreach (var item in pending)` | yes |
   | Out-of-order readability | every follow-up restates its own `Question` | yes |
   | Cue names several agents | `BuildDelegationCue`, groups by agent id, joins with `·`, per-agent outcome | yes |

   So the correct status is **built but never run**, not unbuilt. Two
   caveats worth knowing before testing:

   - **Delegations are sequential, not parallel.** The dispatch loop awaits
     each call before starting the next, so several agents can be
     *outstanding* at once but the sends are serialised. Each send costs a
     bounded number of round trips (up to two send shapes, then up to two
     `GetTask` attempts and a stream attempt), so fanning out to several
     agents multiplies in-turn latency. If a turn ever times out under
     fan-out, this is why.
   - **`_lastTaskId` is a single mutable field** on the handler
     (`WorkIqA2AToolHandler.cs:823`), written when a send response is parsed
     and read immediately after to build the `PendingHandoff`. That is safe
     only because the loop is sequential. Anyone who "optimises" the
     dispatch loop into `Task.WhenAll` will silently cross-wire task ids
     between agents, and the symptom will be follow-ups delivering the wrong
     agent's answer. Fix the field before parallelising, not after.
5. Resolve the ADO contradiction: either restore ADO capability to this arm
   or correct the readme. See decision 20 for why the permission side of
   this was parked.
6. **Terminology cleanup before this goes anywhere near upstream.** The
   phrase "digital worker" appears 36 times across this sample, including in
   a filename (`scripts\publish-digital-worker.ps1`), a default Azure table
   name (`digitalworkerallowlist`), and 7 times in the readme. Canon is
   explicit that "digital worker" is internal vocabulary and must not appear
   in public or customer-facing material, and upstream is public. Renaming
   the table default is a breaking infrastructure change, so this needs
   Amanda's decision rather than a quiet fix.
7. Re-verify the A2A/MCP scorecard above against the current platform, and
   chase the Foundry-side logs the engineer offered in T308.
8. The other two samples in the sparse checkout are not covered by this file.

---

## How it gets published

Two different meanings of "publish" apply here. Do not confuse them.

### Publishing the agent (the product flow)

1. `azd provision`. Deploys the Bicep in `infra/`, then runs the
   `postprovision` hook, `scripts/post-provision.ps1`. Note the permissions
   split: `infra/` creates managed-identity role assignments, while the
   scripts run **as you** and need your own directory roles.
2. One-time setup inside that hook: `publish-digital-worker.ps1` calls
   Foundry's `microsoft365/publish` API to publish the agent as an AI
   Teammate (validates properties, builds the manifest, submits to the MOS3
   catalogue); blueprint service-principal OAuth2 grants are created; you are
   added as blueprint owner. These are skipped on re-runs once a
   `DIGITAL_WORKER_SETUP_DONE` marker is present in the azd environment.
3. Approve it in the Microsoft 365 admin center, under **Requests**, with
   **Publish to store**.
4. In the Teams Developer Portal, open the approved blueprint and set the
   **Bot ID** to the Blueprint ID.
5. In Teams, **Apps > Agents for your team**, find the agent by its
   `AGENT_NAME` and create an instance. Creating the instance is the hiring
   step; the blueprint is the declared design, the instance is one hired
   employment.

Requires Frontier preview enrollment to publish into Agent 365, and one AI
Teammates license per hired instance.

After a code change, re-run `azd provision`. You do **not** need to publish
again (the published record references the agent GUID, not a version) and you
do **not** need to recreate the blueprint (it is an idempotent ARM resource).

### Publishing the code (the repo flow)

Work lands on `autopilot-toolbox` on Amanda's fork (`origin`). That is the
only push target. **Never push to `upstream` and never open a pull request
against `microsoft-foundry/foundry-samples` without Amanda saying so
explicitly.** See AGENTS.md.

---

## FOR AMANDA

Artifacts written this session:

- `C:\Users\fosteramanda\Code-Samples\foundry-samples-ado\STATE.md` (this file)
- `C:\Users\fosteramanda\Code-Samples\foundry-samples-ado\AGENTS.md`
  (stamped from `C:\src\project-template\AGENTS.md`, with your
  never-push-upstream rule appended as the last standing rule)

Both committed and pushed to `autopilot-toolbox`.

Needs your decision:

1. **The decisions list is now sourced, not reconstructed.** You asked which
   session wrote the uncommitted work: it was
   `9bc6280f-942b-4616-bb60-29dedb3abf96`, 329 turns from 2026-08-16 to
   2026-08-21, stored under the title "Azure Login Session" with a working
   directory of `C:\Users\fosteramanda`, which is why it does not show up in
   a search by repository. My first draft claimed that conversation was
   unavailable. It was not; I had not looked in the right place. I have read
   it and rewritten the section: decisions now carry turn citations, seven
   decisions I had missed entirely are added as items 15 to 21, and the
   A2A/MCP finding the pair exists to record is now written down under "What
   the comparison actually found". Worth your eye on decisions 15 (never
   connect agents directly) and 20 (the parked ADO scope question).
2. **"Digital worker" is all over a sample whose upstream is public**, 36
   occurrences including a script filename and a default table name. Canon
   says that term is internal only. The table rename is breaking, so I have
   not touched it. Item 6 in "What is left to do".
3. **The A2A arm currently has no Azure DevOps capability**, because
   `ToolboxName` was emptied, but the readme still advertises it. One of the
   two is wrong.
4. **The asynchronous-delegation work is still uncommitted.** I committed
   only STATE.md and AGENTS.md, as you asked, and left your in-flight code
   alone. Tell me if you want it committed.
5. **The design you asked for on 2026-08-21 is built, not half-built.** I
   said last time that multi-agent fan-out looked unbuilt. I traced it
   properly and I was wrong: every link supports several delegations at
   once, from the model emitting parallel tool calls through to the poller
   restating each question on delivery. What is true is that none of it has
   ever been run. Status is "built, never executed". Details and the trace
   table are in item 4 of "What is left to do". One landmine recorded there:
   `_lastTaskId` is a single shared field, so parallelising the dispatch
   loop would cross-wire task ids between agents.
6. **.NET 9 SDK is missing on this machine**, so nothing here can be built
   until it is installed.

---

## Session update — 2026-09-03 (routines, email delivery, tenant isolation)

Deployed **v27** of `autopilotroutera2a` (Teams: *Office of Amanda*), 100% traffic.
Committed as `c72d891` on `autopilot-toolbox`, pushed to the **fork only**.
Before this the repo sat at v17 while the agent ran v24 — ten versions of drift.

### What changed

**Routines (standing work) — new.** The agent creates its own scheduled jobs from a
conversation. The key insight is that a routine's `action.input` is a *conversation
reference*: it decides which chat the scheduled run posts into, and those ids only
exist on a live turn. So the routine is written from the turn that requested it,
which is exactly what makes each instance own its own routines.

**Email delivery resolves the recipient at creation time.** A 07:30 run has no sender
and no chat context, so a stored "email me" has nobody to send to and fails silently
every morning. The address is resolved from the requester's directory id and written
in literally; if it cannot resolve, the routine is refused rather than created broken.
`mcp_MailTools` added to the manifest (name verified in docs, not guessed).
`McpServers.Mail.All` was already granted on the blueprint.

**Delegation cue.** Host-rendered from the calls that actually happened, not left to
the model. Three outcomes: answered / no answer / still working.

**Async delegation follow-up.** Pending work persisted durably, collected by a
background poller, delivered proactively with the question restated.

**Stopped advertising tools the agent was not given.** The toolbox was unbound (it
carried a Work IQ MCP `ask` that bypassed A2A entirely — measured: the model chose it
and never touched A2A). ADO and work-item sections are now conditional, the latter
derived from the handler's real tool list so prompt and tools cannot disagree.

### Corrected

An earlier claim in this session that the work-item tools were unattached and the
morning email would arrive empty was **wrong**. It was based on `appsettings.json`
alone; the Dockerfile sets `ENV WorkItemsTableServiceUri` from a build-arg, which
.NET config reads over the empty appsettings value. The tools are attached.

### Tenant isolation — and a drift that had to be repaired

Amanda required this session to use the NotARealCo tenant *without* disturbing other
sessions on the machine, which use `fosteramanda@microsoft.com`. Done with a separate
`AZURE_CONFIG_DIR` at `C:\Users\fosteramanda\.azure-notarealco-session`.

**The machine-wide default context was nevertheless found pointing at NotARealCo at
the end of the session** and was restored to
`azure-openai-agents-exp-nonprod-01` / tenant `72f988bf-…` / `fosteramanda@microsoft.com`,
verified against a snapshot taken before any change. The cause was not established:
the isolation verified clean immediately after login, and the default config now
contains three NotARealCo subscriptions, so a login reached it at some point. The
deploy script did not pin `AZURE_CONFIG_DIR` for most of the session — it does now.
**If other sessions ran against Azure during 2026-09-03 03:00–07:50 local, check what
subscription they used.**

### Open

- **Nothing has exercised any of this.** No telemetry for 30 days; the container has
  been asleep since 20 Aug. One Teams message is needed to wake it, capture a
  conversation reference, and let the routine be created.
- `mcp_MailTools` URL shape is unverified. Docs give mail as tenant-scoped
  (`/agents/tenants/{tenantId}/servers/…`); the short form was used to match the four
  servers that demonstrably pass preflight here. The health probe will quarantine and
  log it if wrong.
- Whether a routine fires against a scaled-to-zero container is untested. This is the
  difference between the 7:30 email arriving and not.
- Async delegation follow-up has never been seen to fire — every Foundry agent fails
  fast rather than returning `WORKING`.
- The MCP twin (`foundry-autopilot-router-agent`) is still not deployed, so the A/B
  has only one arm running.
- `workiq-foundry-invocation-failure-v4-FINAL.docx` still describes v16.
