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

**The machine-wide default context was nevertheless found pointing at NotARealCo** and
was restored to
`azure-openai-agents-exp-nonprod-01` / tenant `72f988bf-…` / `fosteramanda@microsoft.com`,
verified against a snapshot taken before any change.

**Root cause: the WAM broker, not `AZURE_CONFIG_DIR` itself (corrected 2026-09-07).**
An earlier note here blamed `AZURE_CONFIG_DIR` wholesale. That was wrong and made the
problem look unfixable. Measured properly:

- `AZURE_CONFIG_DIR` **does** isolate the profile and token cache — they are separate
  files per directory, verified working.
- The leak was **`az login` specifically**. On Windows the CLI authenticates through the
  WAM broker, which registers the account at OS level, outside any config directory.
  The account then became visible to the default config, which acquired four NotARealCo
  subscriptions, one of which became active.
- Ordinary commands run *with* the isolated config were verified **not** to
  re-contaminate the default, before and after cleanup.

So the dangerous operation is narrow: `az login`, and nothing else.

**Cleaned.** `az logout --username amanda@notareal.co`, run with no `AZURE_CONFIG_DIR`
set, removed all four leaked entries. Default is back to 235 Microsoft subscriptions,
0 NotARealCo entries, correct active subscription. Profile backups at
`azureProfile.json.bak-20260907-020624` in both config directories.

**Durable fix.** `C:\Users\fosteramanda\az-notarealco.ps1` pins the isolated config,
passes `--subscription` explicitly so nothing depends on the active context, refuses to
run `az login` (the one operation that reaches past the config dir), and offers
`-CheckDefault` to report contamination in both contexts. Note it deliberately has no
`param()` block: any `[Parameter()]` attribute makes it an advanced function, and
PowerShell then binds az's short flags to common parameters — `-o tsv` fails with
"the parameter name 'o' is ambiguous".

**Still true:** if other sessions ran against Azure during 2026-09-03 03:00–07:55 local,
check which subscription they used. The active default was NotARealCo for part of that
window. Her Microsoft account and all 235 subscriptions were never removed; only the
*active selection* changed.

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

### Routines API — measured behaviour (2026-09-03)

Validated by creating a throwaway routine and deleting it, rather than assuming:

- **Our payload shape is correct.** A `PUT /routines/{name}` with the
  `invoke_agent_activityprotocol_api` action and a full activity `input` round-trips
  unchanged through `GET`.
- **`30 7 * * 1-5` is accepted** — the weekday-07:30 schedule Amanda asked for.
- **Yearly cron is rejected**: a specific month (e.g. `30 7 29 2 *`) returns
  `UserError: yearly cron expression is not supported`. Daily/weekly/monthly are fine.
- **Triggers are immutable.** Changing an existing routine's cron returns
  `Routine trigger cannot be changed after creation. Delete and recreate…`. This is
  exactly what "move my morning email to 8am" looks like, so `create_routine` now
  deletes and recreates on that specific error (v28).
- **Enable/disable works** via read-modify-write with the trigger unchanged, so pause
  and resume are safe.
- The API stamps `"authorization": {"identity": "agent"}` — scheduled runs execute as
  the agent, not as the user who set the routine up.

Probing for a manual "run now" endpoint was inconclusive: `run`, `:run`, `trigger` and
`runs` all return 404, but so does any path on a routine that does not exist, so this
does not establish absence. Likewise the `mcp_MailTools` URL shape is still unverified —
an unauthenticated probe returns 401 for a deliberately nonsensical server name too,
so auth precedes routing and 401 proves nothing. The health probe will settle it in the
logs on the first real turn.

---

## Routines DO fire — but the scheduled run dies before the model (2026-09-07)

Amanda created `five-minute-test-email` from Teams chat. Two big open questions are now
answered, and one new defect is found.

### Answered: routines fire, and they wake a sleeping container

The container had no telemetry for 30 days. At 09:10:02 UTC the routine fired, the
container woke, and the synthetic activity arrived with the instruction intact —
including the **literal** address `amanda@notareal.co`, not the word "me". The
create-time resolution works, which was the risky part of the design.

### Answered: chat-driven routine creation works

The agent built the routine itself from a sentence in Teams. `Manage-Routines.ps1`
reads it back with the correct cron, timezone and embedded address.

### New defect: the scheduled turn throws before the model runs

```
09:10:02  Received message activity: (null)      <- scheduled activities have NO activity Id
09:10:02  POST .../v3/conversations/19:.../activities
09:10:03  401  "The Access Token created to respond to a request from the agent
                was rejected by the remote endpoint"
09:10:02  System.ArgumentNullException: Value cannot be null. (Parameter 'input')
```

No `Responses API request` in that window and no DM-access-control log lines, so the
turn died **before** any gate and before the model. Net effect: **no email is sent.**
The same exception appears at 09:05:04, the routine's first fire, so it is consistent
rather than a one-off.

Two things are tangled here and should be separated when fixing:

1. **`ArgumentNullException` (Parameter 'input')** — `input` is the parameter name used
   by `Regex` methods. The inbound activity has a null `Id`, which is the obvious
   difference between a scheduled activity and a real one. Not yet pinned to a line;
   `AccessControlService` has no Regex call, so it is likely in the SDK's activity
   processing or in a helper reached before the cross-tenant guard logs anything.
2. **401 posting back to the conversation.** The reply attempt is almost certainly the
   catch-block error message in `A365AgentApplication`, so it is a *consequence* of (1)
   — but it is independently interesting: it means a scheduled run may not be able to
   post into the conversation at all, which would matter for `delivery: chat` routines
   even after (1) is fixed. A real chat turn at 09:06:02 posted successfully (201), so
   this is specific to the scheduled path.

`mcp_MailTools` attached cleanly — no quarantine, no preflight failure — so the mail
server name and URL shape are probably fine. Untested beyond attachment, because the
model never got far enough to call it.

**The routine is PAUSED**, not deleted, so the definition survives for diagnosis. It was
erroring every five minutes. Resume or delete with `Manage-Routines.ps1`.

### Azure tenant isolation — root cause confirmed and fixed

The WAM broker theory was proven, not assumed. `az logout --username amanda@notareal.co`
(run with no config dir set) also invalidated the *isolated* session's ability to get new
tokens, which only makes sense if the account lives at OS level. Setting
`core.enable_broker_on_windows=false` **in the isolated config only**, then re-running
`az login`, produced **0** NotARealCo entries in the default profile where the same login
previously produced 4. Default is intact: 235 Microsoft subscriptions, correct active
subscription.

So: `AZURE_CONFIG_DIR` isolates fine; the broker was the leak; the broker is now off for
this session's config. Helper: `C:\Users\fosteramanda\az-notarealco.ps1` (`-CheckDefault`
reports both contexts).

---

## Session update - 2026-09-07 (Workstream Manager ADO authentication)

Examined `samples\csharp\foundry-workstream-manager-autopilot-agent`, not the
A2A router. Its factory obtains a Foundry-audience token through
`AgentTokenCredential`, using the agent user's ID from the activity recipient,
and attaches it to the toolbox. It does not use the human chat sender's token.
The setup describes the ADO connection as Microsoft-managed OAuth with identity
passthrough (`UserEntraToken`). Foundry project access does not replace ADO organization
and project access for the identity passed to ADO.

The existing `scripts\create-blueprintsp-oauth2-grants.ps1` also grants ADO MCP
`user_impersonation` and declares it inheritable on the blueprint. Whether that
extra grant is necessary for the Microsoft-managed connection remains open;
its presence in the script is not proof of necessity. No application code,
permission grants, identities, publishing, or deployments were changed.

---

## The null-Id crash: fixed in code (v29), NOT yet verified running (2026-09-07)

### The bug, pinned exactly

Stack trace from App Insights:

```
System.Guid.Parse
  <- A365AgentApplication.ConstructAgentMetadataFromActivity
     <- GetAgentFromRecipient
```

`A365AgentApplication.cs` line 329:

```csharp
var agenticUserId = recipient.AgenticUserId ?? recipient.AadObjectId;
UserId = Guid.Parse(agenticUserId),          // <-- both null on a scheduled activity
```

A routine's synthetic recipient carries only `id`, `agenticAppId` and
`agenticAppBlueprintId`. A real Teams activity also supplies `aadObjectId`; the scheduled
one does not. `Guid.Parse(null)` throws `ArgumentNullException (Parameter 'input')` inside
`GetAgentFromRecipient`, before any gate, before the model, before the mail tool. The
turn is lost. The 401 seen afterwards is the catch block trying to post an error reply.

### The fix (v29)

`ResolveAgentUserId(agenticUserId, recipient.Id)` — falls back to extracting the GUID
from the MRI. Teams MRIs are `8:orgid:{objectId}`, and the trailing segment is the same
GUID `aadObjectId` carries; verifiable on any real message, where
`from.id = "8:orgid:X"` accompanies `from.aadObjectId = "X"`. Validated against five
inputs including the exact failing value and a garbage case that must still throw.

Two adjacent fragilities fixed at the same time: `Guid.Parse` on the blueprint id became
`TryParse` with a null-safe `Properties` check, and `tenantId` now falls back to
`conversation.TenantId` (scheduled recipients carry no tenantId, so it was `Guid.Empty`).

### Why it is still unverified — and this matters beyond this bug

**A new agent version plus a traffic repin does NOT restart a running container.**
Measured: container instance `296678b9-9f0b-47d7-bf8f-8f0d827e9944` started 09:05:03 and
was still serving at 09:45:01, across a v29 deploy at ~09:24, a traffic repin, and an
11-minute idle gap. The endpoint reported `serving: v29` throughout while the process
kept executing v28 code — the stack trace still showed `Guid.Parse`, which v29 removes.

`PATCH {"state":"disabled"}` was rejected silently: the response came back `enabled`.

Consequence to keep in mind: **any "deployed and verified" claim in this session made
while the container was already warm may have been testing older code.** Most of the
session the container was cold (30 days idle), so cold starts did pick up new versions —
but the two are worth distinguishing when something behaves unexpectedly.

### To finish this

The routine is PAUSED so it stops erroring every five minutes. Once the container really
scales to zero (idle timeout is longer than 11 minutes and was not measured), resuming it
will cold-start on v29 and the scheduled run should reach the model and send mail.

```powershell
C:\Users\fosteramanda\Manage-Routines.ps1 -Resume five-minute-test-email
# then check: exceptions gone, a Responses API request appears, mail arrives
```

Still unproven downstream of the crash: whether `mcp_MailTools` actually sends, and
whether the 401 on posting back to the conversation is only the error-reply path or would
also block `delivery: chat` routines.

---

## v29 verified: crash fixed. Next blocker is identity, and it looks like a platform limit

Verified on a genuine cold start (instance `e8a07f00-83b6-42dd-ac41-fe7994b549de`,
started 10:05:05, i.e. actually running v29):

| Time | Path | Exception |
|---|---|---|
| 09:04-09:06 | Amanda's chat turns | none - worked |
| 09:10, 09:15, 09:30, 09:45 | scheduled runs on v28 | `ArgumentNullException` x4 |
| 10:05 | scheduled run on v29 | `InvalidOperationException` (FIC) |

**The null-Id crash is fixed.** The scheduled run now gets past `GetAgentFromRecipient`
and reaches token acquisition, which is further than it has ever got.

### New blocker

```
AADSTS7002203: No matching federated identity record found for presented assertion
subject 'f3e89c82-f3c2-4a70-a7f8-d594ad11f265'
```

That subject is the agent **instance** identity (`agenticAppId` / instance client id),
not the agent user. The same exchange succeeds on a normal chat turn - Amanda's 09:04
turns produced no exceptions at all - so this is specific to the scheduled path.

**Likely a platform constraint rather than our bug.** The Routines API stamps
`"authorization": {"identity": "agent"}` on every routine, so a scheduled run executes as
the agent identity, not on behalf of a user. The agent-USER federated credential exchange
appears not to be available in that context. If that reading is right, then in a scheduled
run the agent cannot obtain an agent-user token, and **every tool that depends on one is
unavailable** - Work IQ A2A delegation and sending mail as the user included. That would
make routines useful only for work the agent identity can do on its own, which is a much
narrower feature than "run my morning summary".

Not yet confirmed. To settle it, either check whether a federated identity credential can
be registered for that subject, or ask the Foundry team whether user-identity token
exchange is supported inside `invoke_agent_activityprotocol_api`. Worth asking before
building around it - the answer decides whether routines can deliver personal email at all.

### Operational finding worth keeping

**A new agent version plus a traffic repin does not restart a running container.**
Instance `296678b9` started 09:05 and was still executing v28 code at 09:45, across a v29
deploy and a repin, while the endpoint reported `serving: v29`. Idle timeout is somewhere
between 11 and 15 minutes: pausing the routine at ~09:47 left the container quiet, and by
10:01 a resume produced a genuine cold start. `PATCH {"state":"disabled"}` is silently
ignored - the response comes back `enabled`.

So verifying a deploy requires either a cold start or a check that the running instance id
changed. The routine is PAUSED again to stop the error loop.

---

## The FIC blocker, diagnosed precisely — needs Amanda's approval to fix (2026-09-07)

`AADSTS7002203` on every scheduled run. The mismatch is exact:

| | Value |
|---|---|
| FIC registered on the blueprint (`ProjectManagedIdentityFederatedIdentityCredential`) | `4c6f972f-cdbc-4b0f-bdce-7b146e61e3f5` — the **project** managed identity (`PROJECT_PRINCIPAL_ID` in .env) |
| Subject the scheduled run actually presents | `f3e89c82-f3c2-4a70-a7f8-d594ad11f265` — the **agent version's instance identity** |

The blueprint carries only two credentials: that project-MI one, and an `fmi-fic` whose
subject is an FMI path. Neither matches the agent instance identity, so the agent-user
token exchange fails and the turn ends before the model runs.

### Proposed fix — NOT applied, identity change requires approval

Add a third federated identity credential to blueprint app
`ac1da9f1-7fa9-4681-ac62-aea889177a37`:

```
subject : f3e89c82-f3c2-4a70-a7f8-d594ad11f265
issuer  : https://login.microsoftonline.com/dfa98250-28be-4cda-b270-45c05319c07c/v2.0
audience: api://AzureADTokenExchange
```

Held back deliberately: the workspace rules require Amanda's explicit approval for
identity changes, and this grants a new identity the ability to federate into the
blueprint. It is narrow — one subject, same tenant, same audience as the existing two —
but it is still a permission grant and hers to authorise.

### Open question this does not answer

Why the chat path works and the scheduled path does not, when both run in the same
container under the same instance identity. Two readings:

1. Chat turns never need the agent-user exchange (a cached or differently-scoped token
   covers them), and routines are simply the first thing to exercise it. The FIC would
   then be a genuine gap in provisioning.
2. The platform is meant to supply user identity to a scheduled run and does not, in
   which case adding the FIC papers over a platform bug rather than fixing it.

Worth asking the Foundry team which, because the answer changes whether this is a
sample-level fix or something they need to address. The routine stamps
`"authorization": {"identity": "agent"}`, which leans toward (2).

### Also fixed in v30 (deployed, unverified)

`create_routine` never copied `aadObjectId` / `agenticUserId` from the live recipient into
the stored activity, so a scheduled activity's recipient was thinner than the chat one it
was created from. It now carries them through when present, and logs the live recipient at
creation so the two can be compared directly. Whether the live activity even has those
fields is still unmeasured — every activity captured so far has been a routine, and
Amanda's real turns had aged out of App Insights before they could be inspected.

---

## Routines WORK end to end (2026-09-07, v31)

Amanda received the email. Full chain proven: routine fires on schedule -> wakes a
scaled-to-zero container -> acquires a token -> runs the model -> calls mcp_MailTools ->
delivers to the inbox. Confirmed in logs (0 exceptions, 2 Responses API calls) and in
Outlook ("Five-minute routine test", 15:20 PDT).

### The real cause of AADSTS7002203 - it was ours, not the platform

Earlier in this session the failure was attributed to a probable platform limitation:
that a scheduled run executes as `"authorization": {"identity": "agent"}` and therefore
cannot perform the agent-user token exchange. **That was wrong**, and a federated identity
credential was nearly added to the blueprint on the strength of it. Amanda's Teams turn
made the real comparison possible:

```
inbound Teams activity  recipient.agenticAppId : fa259cf9-76ee-45da-9729-764bbcc7129d
agent version instance identity               : f3e89c82-f3c2-4a70-a7f8-d594ad11f265
```

Different identities. The routine was storing the second, because
`ReadRecipientProperty` failed to read the value off the live activity and the code fell
back to `FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID`. The FIC error named that exact subject
all along.

Substituting the correct value in the stored routine fixed it immediately - no identity
change, no new credential. **The proposed FIC was not needed and would have masked a bug
in this sample as a platform gap.**

### Fixed in v31

- `ReadRecipientProperty` matches case-insensitively and also checks the properties bag,
  since which surface carries `agenticAppId` varies by SDK version.
- The instance-identity fallback is removed entirely. When `agenticAppId` cannot be
  resolved, creation is refused and logged rather than producing a routine that is
  accepted, fires forever, and never authenticates.

### Failure shape worth remembering

The bad routine was accepted by the API, appeared correct in `GET /routines`, fired on
time, and woke the container - then died before the model with nothing surfaced to the
user. Every visible signal said healthy. Only the exceptions table showed it, and only the
assertion subject in the error text identified which identity was wrong.

### Operational notes

- `Manage-Routines.ps1` (in the user profile) lists, shows, pauses, resumes and deletes
  routines, and surfaces the delivery address so a wrong one is visible at a glance.
- Routine `five-min-email-test` was deleted after the successful test. None remain.
- A traffic repin does not restart a running container; verifying a deploy needs a cold
  start or a check that the instance id changed.

---

## Meeting capture: registry built, ingestion blocked at tenant level (2026-09-08)

Amanda asked for the autopilot to join meetings and participate live. Reading her own
user stories (Downloads\meeting-user-stories.xlsx) showed they ask for something else:
every story is pre-meeting or post-meeting. AC-MVP-US-009 reads like joining ("Add
Agentic Colleague to a meeting") but its acceptance criteria say "accesses approved
meeting artifacts per capture rules". Nothing requires the agent to be in the meeting.

That removes the calling-bot stack entirely, and with it the Windows hosting blocker:
application-hosted media is Windows only, and this container is Linux aspnet:9.0.

### The blocker that matters

Transcript ingestion cannot work in this tenant today:

```
GET /v1.0/users/{id}/onlineMeetings/getAllTranscripts(meetingOrganizerUserId='...')
403 GraphAccessToTranscriptsDisabled   (v1.0 and beta both)
```

Learn: "By default, Microsoft Graph access is off, so agents and apps can't access
meeting transcripts, regardless of app-level permissions." No request-side workaround.

Remediation is a Teams admin action, NOT an app permission:
Teams admin center > Meetings > Meeting settings > Transcript API access, or
`Set-CsTeamsMeetingConfiguration -EnableGraphTranscriptAccess $true -Identity Global`.

DONE 2026-09-08. `EnableGraphTranscriptAccess` False -> True on the Global policy.
Verified: `getAllTranscripts` went from 403 GraphAccessToTranscriptsDisabled to HTTP 200
after roughly two minutes of propagation. It returns 0 transcripts, which is correct
rather than a failure: no meeting in this tenant has ever had transcription switched on.

Note for anyone repeating this: `Connect-MicrosoftTeams` interactive auth fails in a
non-interactive host ("A window handle must be configured"), and the device-code flow
expires quickly. What worked was passing access tokens straight from the Azure CLI:

```powershell
$graph = az account get-access-token --resource "https://graph.microsoft.com" --query accessToken -o tsv
$teams = az account get-access-token --resource "48ac35b8-9aa8-4d74-927d-1f4a14a0b239" --query accessToken -o tsv
Connect-MicrosoftTeams -AccessTokens @($graph, $teams)
```

`EnableAttributedTranscripts` remains **False**, deliberately. Amanda asked what it meant
and had not answered. Until it is on, transcripts returned through Graph carry no speaker
names, so a recap cannot attribute a decision or an action to a person. That is most of
US-017. Turning it on also makes named speech records of every participant readable by any
app holding transcript permission, tenant wide, so it is her call and not a default.

### What was built instead

Step 1 of the plan, the gate that has to exist before any ingestion:

- `Services/MeetingRegistryStore.cs`: Azure Tables, same account and RBAC grant as
  PendingDelegationStore, so no new infrastructure.
- `AgentLogic/ResponsesApi/Helpers/MeetingRegistryToolHandler.cs`: track_meeting,
  list_tracked_meetings, set_meeting_capture, record_capture_notice.
- Conditional prompt section, wired through Program.cs, the factory and ResponsesApiClient
  the same way the routine and mailbox tools are.

Two flags gate ingestion and both are required: `CaptureApproved` (the organizer said
yes) and `NoticeSentUtc` (the room was told). They are deliberately separate fields. The
organizer cannot consent on behalf of the other attendees, and Teams cannot express
per-meeting permission at all: an application access policy names an organizer and opens
every meeting that person runs. The per-meeting control does not exist in the platform,
so it exists here.

Builds clean, 0 errors. NOT deployed: deployment needs Amanda's approval.

Validated behaviourally, not just compiled. 29 checks run against the built assembly:
`ExtractThreadId` against the real join URL from the Design review event (the one whose
`?context={...}` suffix broke the OData filter), table key sanitisation, the two-flag gate
truth table, closed-by-default field values, and that the prompt section is absent when the
handler is not attached. That last one is the defect class that has already bitten this
sample twice, where the prompt described tools the agent never had.

### Corrections to earlier claims in this session

- I told Amanda the routine tools have no pause. Wrong. `set_routine_enabled` exists and
  does exactly that; my grep looked for `pause_routine` and missed it.
- I said broken blueprint inheritance "had not bitten yet". Wrong. `a365 query-entra`
  confirmed 7 of 8 resources BROKEN, and the Foundry-created identity
  `f3e89c82-f3c2-4a70-a7f8-d594ad11f265` has zero grants as a result. The agent works
  only because `fa259cf9` (Office of Amanda) was granted directly, per instance.
- `inheritablePermissions` is a `/beta` path in the CLI, not `/v1.0` as I first said.

### Open, flagged not resolved

The user stories call the agent "Agentic Colleague". CANON.md defines "autopilot" and
does not mention that term. I did not assume they are the same thing and kept the name
out of the code. Amanda should say which it is before anything user-facing uses either.

Related: the stories record whether a meeting is approved "for persistent memory", but
CANON.md lists memory scope owner as an open question. The registry records the flag and
implements no retention policy, so nothing here settles that question.

### Phase ordering risk in the backlog

Capture ships in Phase 2; opt-in (US-058) and participant notice (US-059) are Phase 4.
That is two phases of ingesting meeting content without the notice US-059 says
participants are owed. The registry above builds the Phase 4 controls now, alongside the
capability, rather than retrofitting them.

---

## Transcript ingestion built; one hard platform limit found (2026-09-08)

Both Teams toggles are now on in Not A Real Co (tenant confirmed by name, not just id):

```
EnableGraphTranscriptAccess : True
EnableAttributedTranscripts : True
```

Changing the second one restarted propagation: `getAllTranscripts` had already gone green,
then both endpoints returned GraphAccessToTranscriptsDisabled again for roughly two more
minutes before settling. Worth expecting rather than debugging.

### Agent identities cannot hold this app role

The documented path for an app to read another user's transcripts is an application
permission plus a CsApplicationAccessPolicy. The first half is not available here:

```
POST /servicePrincipals/{agentIdentity}/appRoleAssignments
400 Request_BadRequest
"The specified app role cannot be granted to agent identities."
```

That is a platform refusal, not a consent problem, and it applies to
OnlineMeetingTranscript.Read.All and OnlineMeetings.Read.All alike.

The delegated equivalent WAS accepted. `oauth2PermissionGrants` on Office of Amanda
(fa259cf9) now reads:

```
User.Read.All Chat.ReadWrite Mail.ReadWrite Mail.Send Calendars.ReadWrite.Shared
Mail.Send.Shared OnlineMeetingTranscript.Read.All OnlineMeetings.Read
```

An access policy `OfficeOfAmanda-Transcripts` was created and granted to Amanda's user
anyway, since it is keyed on appId and costs nothing if unused.

**Still unproven, and it is the real risk:** whether an agent identity token carrying a
delegated transcript scope can read the MANAGER'S transcripts. Delegated normally means
"the signed-in user's own meetings", and the agent is not the organizer. The mailbox tools
work because `.Shared` scopes plus Exchange delegation cover that case, and transcripts
have no `.Shared` variant. If this fails at runtime the fallback worth trying is
`OnlineMeetingTranscript.Read.Chat`, which uses resource-specific consent granted when the
app is added to the meeting chat. That is also a better fit for US-009, because RSC is
inherently per-meeting rather than per-organizer.

### The filter bug that blocked meeting resolution

Resolving an onlineMeeting by join URL kept failing with "unterminated string literal at
position 128". The join URL ends in `?context={...}`, and left raw in the request URI that
`?` starts a new query parameter, truncating the filter before its closing quote. Encoding
the value fixes it:

```
$filter=JoinWebUrl%20eq%20'<Uri.EscapeDataString(joinUrl)>'
```

Confirmed against the real Design review event. Only `JoinWebUrl` and `joinMeetingId` are
filterable; `chatInfo/threadId` is rejected outright, which is why the registry stores the
join URL alongside the thread id it uses as a key.

### Added

`read_meeting_transcript`, which refuses unless both gates are satisfied and says which one
is missing. It reports "no transcript" and "tenant switch is off" as different things,
because they are, and it detects missing speaker attribution from the absence of `<v ` in
the VTT and tells the model not to guess owners.

39 checks pass against the built assembly, including the encoding fix and that none of the
four tools appear in the prompt when the handler is not attached.

### Deployed as v35 (2026-09-08)

55/55 preflight, ACR build, traffic repinned to v35 at 100% and verified. Eleven new
assertions cover the meeting registry, including the two most likely to rot silently:
that `read_meeting_transcript` checks eligibility before any Graph call, and that the
join URL is percent-encoded in the filter.

`MeetingRegistryStore` resolves its table from `WorkItemsTableServiceUri`
(https://autopilotroutera2astorag.table.core.windows.net), which the Dockerfile passes as
a build arg. Table `meetingregistry` is created on first use. Note the third fallback,
`DirectMessageAllowListTableServiceUri`, is present in .env but is NOT passed as a build
arg, so it would be empty inside the container: the work-items URI is what is actually
carrying this.

Not yet exercised. App Insights shows no traces in the last two hours, so the container is
scaled to zero and will cold-start on v35 at the first message. One Teams turn completes
verification:

  "Track the Design review meeting"    -> registers it, capture off
  "Approve capture for Design review"  -> on, still refuses, notice outstanding
  "Recap the Design review"            -> refuses, and naming which gate is missing is
                                          the correct result, not a bug

### Corrected: the agent is INVITED, it does not read the manager's calendar (v36)

Amanda's ruling: "it's an agent user so it can be invited directly like a human."

The first implementation was wrong and she was right to stop it. It resolved the MANAGER'S
mailbox and looked for meetings there, copying the model the mailbox tools use. Those tools
act on the manager's behalf, so that model is correct for them and wrong here. It turned the
autopilot into a third party reading someone else's meeting, which needs tenant-wide
application permissions that Entra refuses to grant to agent identities at all.

The agent USER account is a directory member with its own mailbox and calendar:

```
Office of Amanda   officeofamanda@notareal.co
  agent user account : e27199b1-e82b-4b4d-b178-d1648c335dfb
  agent identity  SP : fa259cf9-76ee-45da-9729-764bbcc7129d
```

Those are different objects and this session conflated them, which is the exact error
CANON.md warns about. Someone adds officeofamanda@notareal.co to a meeting invite the way
they would add a colleague, and the agent then recaps a meeting it was actually invited to.
Being an attendee in its own right removes the whole application-permission problem: no
access policy, no RSC manifest change, no third-party access to anyone's calendar.

Note it is invited, not attending. It never joins the call and never appears in the roster.

Also fixed a partition bug this surfaced: meetings were written under the organizer's
partition key and listed back under the caller's, so every tracked meeting would have
vanished from list_tracked_meetings. Both are now the agent's own mailbox, with the
organizer kept as data since the organizer is who approves capture.

The delegated OnlineMeetingTranscript.Read.All grant made earlier on the agent identity is
probably inert and should be reviewed: for the app-installed-to-meeting resource the docs
say "Delegated: Not supported", and delegated otherwise means the signed-in user's own
meetings. The access policy OfficeOfAmanda-Transcripts is likewise probably unnecessary
under this model. Neither was removed, but neither should be assumed load-bearing.

Deployed v36, 58/58 preflight. Three new assertions guard the corrected model, including
that ResolveManagerMailboxAsync does not come back.

---

## Live meeting participation IS possible: Copilot Realtime Activity Feed (2026-09-08)

Amanda pushed back on "real-time is impossible". She was right to. There is a shipping
path, and it is not any of the ones ruled out earlier.

**Microsoft Graph beta: `/copilot/communications/realtimeActivityFeed/multiActivitySubscriptions`**

It returns a WebSocket URL that streams **speaker-attributed live captions mid-meeting**.
This is a different surface from the `callTranscript` API, which is post-meeting only. The
payload type is literally `liveCaptionDataV2`, carrying `speaker.displayName`, `text` and
`audioCaptureTime`, so "Amanda said X" is available while the meeting is running.

Verified present in the live Graph beta `$metadata`. Official sample:
`microsoftgraph/copilot-realtime-activity-subscription-samples`. Entirely ASP.NET Core and
`ClientWebSocket`, so it runs on the existing Linux container. No Windows requirement.

Flow: subscribe to `getCallEvents(organizers=[...])` -> on `TranscriptionStarted`, POST a
multiActivitySubscription -> read `activities.transcript.transport.url` -> open the socket.

### The app-role refusal is PER-ROLE, not a ban on agent identities

Earlier this session an app role was refused with "The specified app role cannot be granted
to agent identities", and that was generalised into "agent identities cannot hold
application permissions". **That generalisation was wrong.** Measured on the same identity:

```
OnlineMeetingTranscript.Read.All  -> 400, cannot be granted to agent identities
RealTimeActivityFeed.Read.All     -> GRANTED, and it persisted
```

Office of Amanda (fa259cf9) now holds `RealTimeActivityFeed.Read.All` as an application
permission and also as a delegated scope. So the realtime path needs no separate app
registration, which was the workaround the research assumed would be necessary. That one
role being allow-listed where the transcript role is refused reads as deliberate: this
looks like the sanctioned way for an agent to be present in a live meeting.

### Already satisfied by work done earlier today

Both tenant toggles this API depends on were switched on this morning for the post-meeting
work, and they gate this too:

```
EnableGraphTranscriptAccess : True
EnableAttributedTranscripts : True   <- without this the stream has no speaker names
```

### Still to establish before building

- Subscriptions are organizer-scoped, `getCallEvents(organizers=[...])`. Whether an agent
  invited as an ATTENDEE can subscribe to a meeting someone else organised is unproven and
  is the first thing to test. If it cannot, the invited-attendee model and the realtime
  model do not compose.
- Certificate auth is required, not a client secret, and on Linux the cert must be loaded
  from file rather than the Windows certificate store.
- A Copilot licence is listed as a prerequisite.
- Requires a public HTTPS webhook that echoes `validationToken` and RSA-decrypts payloads.
- No Learn conceptual page exists. Graph beta plus one sample repo. Treat as preview.

### Output side

Replying in meeting chat is the ordinary Teams proactive-message path. Separately, the CART
captions endpoint (`api.captions.office.microsoft.com/cartcaption`) injects text into the
live caption stream, so the agent can appear on screen mid-meeting. Plain HTTPS POST, Linux
fine, but the URL is obtained by hand per meeting from Meeting options, so it does not
automate cleanly.

### Confirmed dead ends, do not revisit

- ACS Call Automation: `ConnectCall` takes Server/Group/Room locators only, no Teams
  locator. Teams interop marks speech-to-text unsupported and needs a Teams Phone licence.
- Application-hosted media: Windows Server only, and Microsoft now explicitly says
  "Real-time Media bots are not recommended for AI agent scenarios."
- Service-hosted media: PlayPrompt, Record and DTMF only. Cannot hear words.
- Facilitator: first-party, no developer surface at all.
- Meeting extensibility: every caption API is write-only. There is no live-caption read.

### Probed the realtime API directly: the gate is Teams-store registration, not permissions

Delegated `RealTimeActivityFeed.Read.All` consented as Amanda, then POSTed a real
multiActivitySubscription for a meeting she organises:

```
POST /beta/copilot/communications/realtimeActivityFeed/multiActivitySubscriptions
403  {"code":"7503","message":"Application is not registered in our store."}
```

That is the caller being rejected, not the permission and not the tenant. Auth and tenant
config both passed; the API refused Microsoft Graph PowerShell because it is not a
registered Teams application. This matches the sample's "Register Bot in Teams Store" step
and its requirement for an Azure Bot registration with the Teams channel and calling
enabled.

Useful because it narrows the remaining unknown sharply. The open question is no longer
"do agent identities have access" but "does the Agent 365 Teams app registration behind
this autopilot satisfy 7503". GET on the collection returns an empty UnknownError, so the
collection is not readable; POST is the only verb worth probing.

Note the organizer-scope question is still unanswered: this probe used Amanda as organizer,
so it did not exercise whether an invited attendee can subscribe to someone else's meeting.

### v37: stop answering empty messages in meeting chats

Measured in a real meeting. The autopilot was added to a meeting chat and posted three
visible junk replies during a 65 second meeting:

  "I can't respond to that chat because the message content is empty."
  "I can't respond meaningfully because the chat message content is blank."
  "I can't respond to an empty message."

Cause: `NewActivityReceived` had no empty-text guard. Teams delivers meeting lifecycle
events (meeting started, recording started, participant joined) into the meeting chat as
message activities carrying no text. Each one was wrapped into "Respond to this chat
message... Message: " and handed to the model, which answered honestly that there was
nothing there. In a 1:1 or a chat the agent is a member of, the addressed-to-agent gate
passes by definition, so nothing stopped it.

Fixed: message activities with no text, no attachments and no value return silently.
Silence is the only correct response to a message with nothing in it.

Worth noting this only surfaced because the agent was added to a meeting for the first
time. Every prior test was a 1:1 chat where lifecycle events never appear.

Also confirmed from the same meeting: a transcript now exists (1 speaker, 43 seconds), so
the tenant toggles enabled earlier today are producing real artifacts and the end to end
recap path is finally testable.

### v38: stop making the human do the bookkeeping

Amanda: "I'M CONFUSED". She was right, and the confusion was the design.

Getting one recap required four typed commands: track, approve capture, record notice,
recap. Three of those were the agent asking a human to tell it things it could work out
itself. The meeting is already on the agent's calendar because someone invited it, so
making the user announce that fact adds nothing.

Changed:
- `read_meeting_transcript` and `set_meeting_capture` now register the meeting from the
  agent's own calendar automatically when it is not in the registry yet. Registering is not
  permission: the entry is still created with capture off, so both gates apply unchanged.
- `set_meeting_capture` takes `attendees_notified`, so approval and notice can be given in
  one sentence when the user offers both. They remain separate stored fields and separate
  assertions; the user is just not made to say it twice.
- The prompt now shows the intended shape and forbids replying with a list of commands.

Target flow is two turns:

  User: "Recap the meeting"
  You:  "That's on my calendar. You're the organizer: do you approve me using what was
         said, and have the attendees been told?"
  User: "yes, and I told them"
  You:  [recap]

The failure that triggered this: "Recap the meeting" returned "I'm not tracking any
meetings". Logs confirmed the store initialised and
`Meeting registry acting as the agent user officeofamanda@notareal.co` ran fine, so
nothing was broken. The registry was simply empty because the separate track step had
never been run. A design where the happy path requires the user to remember step one is a
design that will keep producing that message.

Also verified end to end from Amanda's own token, which is what makes this worth shipping:

```
WEBVTT
00:00:14.303 --> 00:00:33.223
<v Amanda Foster>...I'm going to take ownership for the migration. Also, Farzad's going
to take the API work, and we're blocked on the checkout release renewal right now.</v>
```

Speaker attribution present, so `EnableAttributedTranscripts` is doing its job. Owners,
delegated owners and blockers are all in the text, which is exactly what US-017 asks the
agent to extract. Whether the AGENT'S token can fetch the same transcript is still the
open question; v38 is what makes that testable in two messages instead of four.

Preflight caught a real regression during this deploy: the "asks for invite" assertion
failed because the wording changed. The rule held, the regex was stale, and the check
saying what it defends is what made that distinguishable in seconds.

### v39: the agent asked "which meeting?" while holding an unread calendar

v38 auto-registers a meeting from the calendar, but the agent never got that far. Given
"recap the meeting" it called `list_tracked_meetings`, got "no meetings are being tracked",
and replied "Which meeting should I recap? I'm not currently tracking any meetings, so
I'll need to be on the invite before I can read a transcript."

Every clause of that is wrong in a different way. It WAS on the invite. There WAS a meeting
with a transcript. And asking the user which meeting is useless when the tool that answers
"which meetings exist" only reports registered ones, so the user's answer lands in the same
empty registry.

Root cause was the listing tool, not the auto-track. `list_tracked_meetings` reported the
registry only. Fixed: it now also lists meetings on the agent's own calendar that are not
registered yet, marked as such, with an explicit note that they can be recapped straight
away. Prompt now says "the meeting" and "today's meeting" mean read your calendar, and that
asking which meeting while holding an unread calendar is making the user do your work.

Confirmed from App Insights that this was NOT a stale container: `Application starting...`
at 05:10:58 UTC, the same minute Amanda typed. v38 was genuinely running and genuinely
produced that answer.

Deployed v39, 63/63.

### v40: meeting times were UTC labelled as Pacific

Amanda: "im confused? are these real". They were real; the times were not.

The agent listed 'meeting' as 9:30 PM PT. It is 21:30 UTC, which is 2:30 PM PT, and the
meeting chat confirms it: "Meeting started 2:30 PM". The agent took the UTC number and
presented it as local, a clean seven hour lie in a string that looked entirely plausible.

Cause: `MeetingRegistryToolHandler.SendGraphAsync` never sent the `Prefer:
outlook.timezone` header, so Graph returned UTC. `ManagerMailboxToolHandler` has always
sent it. Same codebase, two calendar readers, one of them wrong.

Worth recording that I made the identical mistake in the verification script while checking
this, double converting an already-UTC value and getting 4:30 AM. It is an easy error to
make twice in five minutes, which is the argument for asking Graph for the right zone
rather than converting anywhere downstream.

Fixed:
- Both calendar reads now send `Prefer: outlook.timezone`, defaulting to Pacific Standard
  Time and overridable with `MeetingDisplayTimeZone`.
- Every displayed time carries the zone name, so a bare number can no longer be read as
  local by whoever sees it.

Known wart left in place: the entity field is still called `StartUtc` while now holding a
zoned local time. Renaming it touches the stored schema, so it is labelled at the display
boundary instead. Flagged rather than hidden.

Deployed v40, 65/65.

---

## Chasing the Checkout v4.3 BadRequest (2026-09-09)

The failure Amanda opened this session with. Instance `Checkout v4.3 Workstream Manager`
(identity 952d2b7d, agent user c7dcc4ea) answering:
"I encountered an error processing your request. Status: BadRequest."

### Reproduced the documented cause

That string is generated by `ResponsesApiClient.cs`, and the readme already documents why:
a toolbox that cannot enumerate its tools fails the whole Responses API call, on every turn,
including ones needing none of those tools. Probing the toolbox directly:

```
workstream-manager-ado v5 : JSON-RPC error -32007
    "tools/list failed for 2 tool source(s), succeeded for 1"
    source-of-truth-a2a (a2a_preview): HTTP_400
    "Connection resolution failed. HTTP status: BadRequest.
     Reason: AgenticIdentityToken auth type for conn..."
v6 : 42 tools, healthy
v7 : 53 tools, healthy
```

So the literal words "HTTP status: BadRequest" come out of toolbox v5, for exactly the
reason the readme names: an `a2a_preview` tool whose connection uses `AgenticIdentityToken`
cannot be resolved through the MCP proxy, because the proxy has no agent context.

### Already mitigated on the current version

`ToolboxVersion` history on the agent:

```
v16, v15, v14, v13  (Aug 17)  ToolboxVersion=7   <- serving, healthy
v12, v11            (Aug 16)  ToolboxVersion=2
v10, v9, v8         (Aug 3-7) NOT SET  -> follows the toolbox DEFAULT version
```

The unpinned versions are the exposed ones: without a pin the agent loads whatever the
toolbox default points at, so a default pointing at v5 breaks every turn with no change to
the agent. That is the failure mode the pin exists to prevent.

### What I could NOT establish, and it matters

**There is not one container log line in 90 days**, in either App Insights:

```
workstreammanagerado-appi      traces 0, exceptions 0   (only toolbox-proxy requests,
                                                         including my own probes today)
foundryworkstreammanger-appi   traces 0, exceptions 0
```

Both projects have a default AppInsights connection, and both samples wire
`AddApplicationInsightsTelemetry` plus `builder.Logging.AddApplicationInsights()`
identically. `autopilotroutera2a` logs normally through the same shape.

That leaves a contradiction I did not resolve: the error string is produced INSIDE the
container, but the container has never logged. Either telemetry is not reaching App
Insights for this agent, or the message never reached the container and the string came
from somewhere else. Those have completely different fixes, so it should not be guessed.

### Instance permissions, checked while here

All 7 instances under blueprint `workstreammanagerado-maib` (a029bdcc) have ZERO direct
grants, so they depend entirely on inheritance. The blueprint SP does hold grants, and the
inheritable lists are populated, but they do not line up:

```
granted AND inheritable : Chat.ReadWrite, User.Read.All          -> flows
inheritable, NOT granted: Mail.ReadWrite, Mail.Send              -> nothing to inherit
granted, NOT inheritable: ChatMessage.Send, ChannelMessage.Send,
                          ChatMember.Read, ChannelMessage.Read.All -> will not flow
```

`ChatMessage.Send` and `ChannelMessage.Send` being granted but not inheritable is worth a
look on its own: those are how a bot posts to chats and channels.

Correction to an earlier claim in this session: I treated `kind=enumerated` as meaning
inheritance is broken. Here the enumerated lists are populated and largely match the
grants, so inheritance is probably working with the gaps above. Learn is explicit that
inherited permissions are only observable in token contents at runtime, so this cannot be
settled by inspection either way.

### Next step

Amanda re-tests Checkout v4.3. If it still fails, fix telemetry first: debugging an agent
that cannot be observed is guesswork, and every conclusion above about the current state
is inference rather than measurement.

### Granted and made inheritable on the workstreammanagerado blueprint (2026-09-09)

Amanda: "actually grant these and set as inheritable". Both halves, per her standing
correction that a grant without inheritance does nothing.

Blueprint `a029bdcc` (SP f3f939c9), Microsoft Graph. Before:

```
granted     : ChatMessage.Send ChannelMessage.Send ChatMember.Read
              ChannelMessage.Read.All User.Read.All Chat.ReadWrite
inheritable : Chat.ReadWrite Mail.ReadWrite Mail.Send User.Read.All
```

After, both sides now the union of eight:

```
ChannelMessage.Read.All ChannelMessage.Send Chat.ReadWrite ChatMember.Read
ChatMessage.Send Mail.ReadWrite Mail.Send User.Read.All
```

### How the inheritablePermissions write API actually works

Earlier notes in this file said the PATCH takes scope names, which is true but incomplete
and cost three failed attempts. The real shape:

`kind` is a DERIVED, read-only property. Writing it fails with
"Invalid value specified for property 'kind' of resource 'InheritableScopes'" for every
value including `allAllowed`, which reads like the value is wrong when the problem is the
property. The discriminator is `@odata.type`:

```
PATCH /beta/applications/microsoft.graph.agentIdentityBlueprint/{appId}/inheritablePermissions/{resourceAppId}
{"inheritableScopes":{"@odata.type":"#microsoft.graph.enumeratedScopes","scopes":["..."]}}
```

GET returns both, so the entity looks like it has a writable `kind` when it does not.
`#microsoft.graph.noRoles` is the equivalent type behind `kind: none`.

This also means the earlier read of the a365 CLI guidance needs revisiting: switching a
blueprint to `allAllowed` is presumably `@odata.type: #microsoft.graph.allScopes` or
similar, NOT `kind: allAllowed`. Untested, and not attempted here because Amanda asked for
these scopes specifically, not a change of inheritance model.

### State across the whole blueprint after the change

```
Microsoft Graph                  MATCHED
Messaging Bot API Application    MATCHED
Agent Tools                      MATCHED
Azure Machine Learning Services  MATCHED
Azure DevOps MCP                 MATCHED
Work IQ                          MATCHED
Power Platform API               MISMATCH - Connectivity.Connections.Read is inheritable
                                 but not granted, so there is nothing to inherit
```

Power Platform was not in the set Amanda approved and is left alone deliberately.

### Unverifiable by inspection

All 7 instances still hold zero direct grants, so this only helps if inheritance actually
delivers. Learn states inherited permissions "aren't visible through Microsoft Graph. They
are only observable in the token contents at runtime", so whether these eight now reach an
instance token cannot be confirmed from here. A live turn from Checkout v4.3 is the test.

---

## workstreammanagerado v17: meetings + routines ported to the ADO agent (2026-09-09)

Amanda asked for a new agent version of the WORKSTREAM MANAGER agent, implementing what a
customer backlog asks for and what we are confident in. Her instance
`Checkout v4.3 Workstream Manager` is what should see the change.

Critical routing fact, easy to get wrong: `Checkout v4.3` is an instance of the
`workstreammanagerado` Foundry agent, which builds from
`samples/csharp/foundry-workstream-manager-autopilot-agent`. It is NOT the agent Office of
Amanda runs (`autopilotroutera2a`, from `foundry-autopilot-router-agent-a2a`). All the
meeting and routine work this session landed in the latter, so it had to be ported.

### What was ported, and what deliberately was not

Ported: `RoutineToolHandler`, `MeetingRegistryToolHandler`, `MeetingRegistryStore`, plus the
prompt sections and the DI/factory/client wiring.

NOT ported, on purpose:
- `ManagerMailboxToolHandler` — sends mail and books meetings AS the manager. That is a
  different trust decision for a team agent than for a personal one, and it needs an
  Exchange delegation this agent user does not have.
- `WorkIqA2AToolHandler`, `PendingDelegationStore`, `DelegationFollowUpService` — this agent
  already has its own source-of-truth delegation path.

### Surgical port, not a tree sync

The first instinct was to copy the newer tree wholesale, since the target is 24 files and
all of them exist in the source. That was wrong. Diffing what the copy would REMOVE showed
the target has content of its own:

```
"You are a helpful agent named Workstream Manager Autopilot"   <- its identity
DefaultAgentName = "Workstream Manager Autopilot"              <- addressed-to-agent gate
ComputeMcpFingerprint(...)                                     <- MCP fingerprinting
```

So only the three genuinely new files were copied, `AgentInstructions.cs` was taken and its
persona restored, and everything else was edited in place.

### The silent failure that would have shipped

`RoutineToolHandler.IsEnabled` needs `FoundryProjectEndpoint` and `FoundryAgentName`.
`appsettings.json` ships both empty, and empty is not null, so the `?? Environment.
GetEnvironmentVariable(...)` fallback never fires. The values come from the agent's
`environment_variables`, set in `agent-creation-script.ps1` — and the workstream manager
copy of that script never set them. Routines would have deployed, advertised nothing, and
raised no error. Both are now set, confirmed on the deployed v17.

### Phantom capability caught by its own test

Copying `AgentInstructions.cs` brought `BuildManagerMailboxSection` with it while the
handler was deliberately left behind. The prompt would have described `send_email_as_manager`
if the flag were ever set. Removed the section and the parameter entirely, so it cannot be
switched on. Preflight now asserts its absence.

### Backlog coverage claimed

Meeting capture, opt-in and participant notice, transcript use, and scheduled recurring work
are covered by the ported handlers. Answer-quality behaviour (separating evidence from
recommendation, and naming the missing source rather than answering unsupported) is covered
by prompt language rather than new code. Deliberately NOT attempted: shared memory, the
kanban board, cross-source state reconstruction, redaction and retention. Those are
subsystems, not prompt changes, and claiming them would be dishonest.

Story-by-story mapping is deliberately kept out of the sample: AGENTS.md forbids customer
names under samples, and the sample now contains none.

### Deployed

v17, 24/24 preflight, traffic repinned to 100% and verified. A new deploy wrapper lives at
`%TEMP%\deploy_wsm.ps1`; the target's `agent-creation-script.ps1` does not repin traffic on
its own, so without the wrapper a new version is built and live nowhere.

25 behavioural checks pass against the built assembly, including that the agent kept its own
identity, that the ADO guidance survived the port, and that every new tool is absent from the
prompt when its handler is not attached.

### Open

Still unverified at runtime, and the same blocker as this morning: this agent has no
container telemetry in 90 days. Build args and Dockerfile are identical to the agent that
does log, and the App Insights connection matches, so the container appears never to have
started. If v17 produces logs on first use, that resolves itself; if it does not, the
messages are not reaching the container and no amount of code change will show up.

### Runtime-verified, and one real defect found (v18, 2026-09-10)

Amanda asked "did you test this". Honest answer at the time was no: build, preflight and
25 in-process assertions had passed, but nothing had run. She tested. Results:

**Test 1, meetings — PASS, and it proves the whole port.** The agent answered "I'm not
tracking any meetings, and I don't have any meeting invites on my calendar. Add me to the
invite the same way you'd add a colleague." Telemetry confirms it was real work, not a
plausible sentence:

```
07:07:53  Application starting...
07:07:55  MeetingRegistryStore initialized with table meetingregistry
07:07:59  "You are a Workstream Manager autopilot."
07:10:21  Meeting registry acting as the agent user
          checkout-workstream-manager@notareal.co, reading its own calendar.
```

That single line settles four separate open questions: the container starts, the store
resolves a table (the fallback chain was inference until now), the persona survived the
port, and the handler resolves ITS OWN mailbox rather than the manager's.

**Test 3, ADO — PASS, no regression.** Real data: Epic #61, all 8 launch gates Active, 5
Active features, 7 open bugs, named blockers. The port did not break what this agent
existed for, which was the largest risk in taking instructions from a sibling sample.

**The 90-day telemetry mystery is closed.** It was not broken. The container had simply
never been invoked in the retention window. Nothing to fix.

**Test 2, routines — FAIL, and Amanda spotted it.** Asked what standing work was scheduled,
the agent replied "No standing work is scheduled." That is a true statement it had no way
to know. Dependency telemetry shows no call to the routines API, and all prompt sections
including the routines one were present, so the tools were attached and simply never
called. It guessed, and was right by luck.

Worth being precise about why this was nearly missed: the answer was correct, brief and
confident. Only the absence of an outbound call gave it away. A wiring bug would have been
obvious; a model answering from nothing looks like success.

Fixed by making the obligation explicit rather than implied, the same way the calendar
tools already say to read every time: routines questions must call list_routines first, and
"no standing work is scheduled" is only sayable after an empty result. Applied to both this
sample and the router sample, which had the identical gap. Preflight now asserts it.

Deployed v18, 25/25.

### v19: the port carried a contradictory delegation model

Retest of routines PASSED, verified by outbound call rather than text:

```
07:25:47  GET /api/projects/workstreammanagerado/routines  200
```

The v18 fix works: the agent now calls list_routines before saying nothing is scheduled.

But the same turn exposed a regression the port introduced. The agent prefixed its answer
with "I couldn't post directly to that chat: no Teams send tool is available here, and
WorkIQ denied chat-message access. Reply to Amanda: ..." — it tried to find a tool to
deliver its own reply.

Cause: `AgentInstructions.cs` was taken from the router sample, which carries two sections
built for an agent that has the A2A delegation handler. That handler was deliberately NOT
ported, so the prompt described tools that do not exist here:

```
BuildRoutingSection      -> list_workiq_agents, ask_workiq_agent   (no such tools)
BuildMcpDelegationGuard  -> "Delegation goes through ask_workiq_agent. Always."
                            "never pass agentId to workiq___ask"
```

The guard is not merely describing something absent, it is backwards. This agent's ONLY
delegation path is `workiq___ask` WITH an agentId, which is exactly what BuildDelegationSection
tells it to do. The prompt contradicted itself, so the model went looking for a send tool.

Caught the mailbox version of this mistake during the port and missed this one. The
difference is instructive: the mailbox section named a tool that plainly did not exist, while
this one named tools that sound like the toolbox tools that DO exist. The toolbox has 11
`workiq___*` tools, so `workiq___ask` is real and `ask_workiq_agent` is not, and the two are
one underscore apart.

Fixed by removing both sections. `BuildDelegationSection` stays: it is the delegation model
this agent actually has, and it was in the target before the port.

Preflight now asserts both directions — no phantom A2A tool names, and the real
source-of-truth delegation still present, so the fix cannot be over-applied later.

Deployed v19, 27/27.

**v19 verified at runtime.** Cold start 07:32:32, routines API called 07:32:43 (200), and the
reply came back as a clean "No standing work is scheduled." with no delivery preamble. Both
the v18 obligation-to-check fix and the v19 phantom-delegation removal are confirmed working
against the deployed container, by outbound call rather than by reading the answer text.

Still untested on this agent: creating an actual routine, and meeting capture end to end.

### v20: two bugs from the first real meeting test

Amanda invited the agent to a Teams meeting, ran it with transcription on, and hit two
things. Both were mine.

**1. The empty-message guard was never ported.** It was fixed on the router sample as v37 and
the fix simply did not come across, because the port copied three capability files and edited
the service by hand. Result, in front of everyone in the meeting chat:

```
4:23 AM  I can't respond because the message content is empty, and I don't have a
         Teams send-message tool attached in this turn.
4:24 AM  I can't respond because the message content is empty.
```

Now ported. Preflight asserts it in this sample too, which is what should have happened the
first time: a fix worth a guard in one sample is worth the same guard in its sibling.

**2. A "Meet now" meeting has a BLANK calendar subject.** The chat shows a name, the calendar
entry has none:

```
subject   : ''
online    : True
attendees : checkout-workstream-manager@notareal.co
```

The agent was correctly invited and the meeting was genuinely on its calendar, but the code
skipped blank-subject meetings when listing and could never match one by name. So it answered
"there are no Teams meeting invites on my calendar" while looking straight at one, and sent
Amanda off to fix an invite that was already correct. That is worse than an error: it
misdirects.

Fixed three ways:
- `DescribeSubject` gives untitled meetings a real label, `(untitled meeting 2026-09-11 11:00)`,
  used for listing, matching and storage.
- Blank subjects are no longer skipped in the listing.
- When the subject matches nothing but exactly ONE online meeting is on the calendar, that one
  is used. A name the user cannot type must not be the only way in. Never applied when several
  could be meant.

Applied to both samples. Deployed v20 on workstreammanagerado, 30/30.

### Worth remembering about my own tooling

While diagnosing this I wrote a calendar query using `(Get-Date).AddHours(-6)` and formatted
it with a trailing `Z`, which labels local time as UTC without converting. The window was
wrong by the offset and the meeting appeared to not exist. That is the identical bug fixed in
the agent at v40, made again in a diagnostic script an hour later. When a time looks absent or
impossible, suspect the query before the data.

### v21: the empty-message guard was wrong, and the calendar scope was missing

Amanda ran a second meeting on v20 and the chat noise came back. It was NOT a stale
container: the container started 11:57:32 UTC and v20 was created 11:40:59, so v20 really
did produce it. The guard was simply wrong.

**What Teams actually posts into a meeting chat**, from the activity log:

```
New activity received: (null)
New activity received: <URIObject format_version="1.1" type="Video.2/CallRecording.1">
                         <RecordingStatus status="Initial" code="0" ...
New activity received: {"scopeId":"...","storageId":"...","callId":"..."}
```

The v20 guard required blank text AND no attachments AND no value. That caught almost none
of it: the recording payloads HAVE text, and the blank ones carry attachments. So the model
was handed recording XML, found nothing to answer, and said so in front of the meeting.

Replaced with `IsTeamsSystemPayload`, which recognises the payloads themselves: blank text,
anything starting `<URIObject` or containing `RecordingStatus` or `Video.2/CallRecording`,
and JSON carrying `callId` together with `scopeId` or `storageId`. The JSON case matches on
the pair rather than either id alone so a person quoting one of those words is not silenced.

Worth naming the mistake: v20 was written from a guess about what an "empty" activity looks
like, and shipped without ever reading one. The activity log had the answer the whole time.
A guard against a category should be written from a sample of the category.

**Second bug, same test.** "I can't recap Team huddle: Exchange denied access to my calendar
mailbox. I need calendar delegate access granted in Outlook..."

Two things wrong. The blueprint had no calendar scope at all, and the error text was written
for reading the MANAGER'S mailbox, so it asked Amanda to delegate her calendar to the agent.
The agent was reading its OWN calendar. Nobody delegates a mailbox to its own owner, so
following that advice could never have fixed it.

Granted and made inheritable on the blueprint:

```
Calendars.Read  OnlineMeetings.Read  OnlineMeetingTranscript.Read.All
```

joining the eight already there. Both sides verified after the PATCH.

The 403 message now names the missing Graph scopes and says explicitly not to ask for
delegate access to the user's own calendar.

Deployed v21, 30/30. Both fixes applied to the router sample too.

### v22: the second consent gate was my design error, removed

Amanda: "I don't think this approval stuff is right, it's why I get weird messages. Maybe
remove whatever you have is wrong." She was right, and the requirement doc agrees with her.

The calendar fix in v21 worked: the agent now finds "Team huddle" and reaches the gate.
But the gate was a dead end:

```
5:22  capture has not been approved, and participants have not been notified.
      If you're the organizer: do you approve..., and have the attendees been told?
5:33  capture approval is missing, and attendees have not been notified.
      If you're the organizer, confirm both...
```

Two questions in one breath, and re-asking just repeated it.

**AC-MVP-US-059 says: "Participant notice enforced by platform."** Teams shows every
participant a recording and transcription banner the moment transcription starts. Nobody can
transcribe a meeting without the room being told. I read that requirement, built a second
human-attested gate anyway, and so required someone to confirm something the platform
already guarantees. That is not caution, it is a checkbox that protects nothing and blocks
the user.

Now one gate, which is what US-058 actually asks for ("Opt-in status; excluded if not
enabled"):

- `IsIngestionEligible => CaptureApproved`
- `NoticeSentUtc` is still recorded, automatically, when a transcript is read, with
  `NoticeSource = "Teams transcription banner (platform-enforced)"`. US-059 also asks for
  "capture status; audit record", and that is satisfied by recording provenance rather than
  by interrogating the user.
- `record_capture_notice` and the `attendees_notified` parameter are gone.
- The prompt now says ask ONCE, and says explicitly not to ask about notifying attendees.

The general lesson, which cost several deploys: a consent gate is only worth having if the
user can actually pass it and if it protects something the platform does not already.
Requiring a human to vouch for a platform guarantee gives an appearance of rigour and a dead
end in practice.

Also worth recording as process: two attempts to strip the notice code with regex broke the
file, the second time silently removing ReadTranscriptAsync and four other methods before the
compiler caught it. `git checkout --` on the single file and redoing it with exact-match edits
was faster both times than debugging the damage. Multi-line regex against C# is not worth it.

Deployed v22, 30/30.

### v23: "there is no approval" - the invitation IS the consent

Amanda, after v22 still asked her to approve: "there is no approval."

She is right, and v22 only went half way. By the time a meeting is on the agent's own
calendar with a transcript, the organizer has already performed two deliberate acts:

1. They added the agent to the invite, the same way they would add a colleague.
2. Someone started transcription, which Teams announced to every participant.

A third confirmation adds no protection. It just blocks the person who already said yes
twice. The gate was ceremony.

Now:
- `CaptureApproved` defaults to **true**, because a meeting only reaches the registry by
  being on the agent's own calendar, which means it was invited.
- `set_meeting_capture` is now an **exclusion** control, not a prerequisite. Its description
  says so, and the refusal path only fires for a meeting explicitly excluded.
- The prompt says consent is the invitation, and lists asking for approval as a Bad example
  rather than a Good one. The worked example it used to carry was literally the exchange
  Amanda saw and objected to.
- Re-tracking never resets an explicit exclusion.

US-058 is still satisfied: "control whether the agent uses my meeting content. Opt-in
status; excluded if not enabled; registry updated." The control exists and the registry
records it. What changed is the default, and the default follows from the invitation.

### On how this went wrong three times

v21 added a calendar scope and the gate became reachable. v22 removed the notice half of it.
v23 removed the rest. Each step was driven by Amanda hitting the same wall and saying so.

The original design came from reading two requirements (US-058, US-059) and implementing
each as a literal gate, without asking what act in the real flow already constitutes the
consent. The requirement said "opt in"; the invitation was the opt-in all along.

Preflight caught my own bad assertion on the way: I asserted the prompt does not contain
"do you approve", but the prompt legitimately contains `Do NOT ask "do you approve"`. The
check now matches the prohibition rather than the absence of a phrase.

Deployed v23, 30/30.

### v24: the routine WAS firing, and failing 401 every time

Checking whether v23 had been exercised turned up something unrelated and more useful. The
5-minute routine is firing on schedule, and failing:

```
12:50:01  POST /00000000-0000-0000-0000-000000000000/oauth2/v2.0/token   400
12:50:01  POST .../v3/conversations/19%3ac7dcc4ea-...                    401
12:55:00  (identical)
```

An all-zero tenant id. The stored routine is correct in every respect: right instance
(952d2b7d), right agent user, right blueprint, and `conversation.tenantId` present. But:

```
recipient   : {id, agenticUserId, agenticAppId, agenticAppBlueprintId}   <- no tenantId
channelData : null                                                       <- Teams normally carries tenant here
```

`A365AgentApplication` read the tenant only off the recipient, got nothing, and fell to
`Guid.Empty`. Every fire then requested a token for the all-zero tenant and could not post.

**The fix already existed in the router sample and was never ported.** That is the third
time in this build: the empty-message guard, the untitled-meeting handling, and now this.
Each was found the same way, by a failure in front of Amanda rather than by comparing the
two trees. The router sample even carries the explanatory comment.

Ported, with the measured symptom written into the comment so the next person recognises the
401 immediately. Preflight now asserts it here too.

### Why this kept happening, and what changed

The port copied three capability files and hand-edited the shared ones. Anything fixed in the
sibling that lived in a shared file was invisible to that process. Three assertions now cover
the three that bit, but the general risk remains for any future divergence, and the honest
statement is that the two samples are not automatically kept in step.

Deployed v24, 31/31.

### The stale container trap, which wasted several rounds

Amanda asked whether the approval message at 06:02 was intentional. It was not: v23 removed
approval entirely and was deployed at 12:50 UTC. But:

```
v22 created 12:44   v23 created 12:50   v24 created 13:00
container starts in the last 40 minutes: NONE
prompt at 13:02 contains "do you approve", not "Consent is the invitation"
```

So the container had been running continuously since before 12:44 and was still serving a
pre-v23 image. Every version deployed in that window went live nowhere.

This is the gotcha already recorded earlier in this file, met again and not recognised: a
traffic repin does not restart a running container. What was NOT understood before is the
feedback loop it creates.

**Testing a fix keeps the container warm, which prevents the fix from loading.** The more
promptly a fix is tested, the longer it takes to arrive. Amanda was testing continuously and
in good faith, and each test both kept the old code alive and produced a symptom I then
diagnosed as though it came from the code I had just written. Some of that diagnosis was
real and some was chasing behaviour already replaced.

The honest accounting: v22, v23 and v24 were deployed and verified as "serving 100%" by
checking the traffic pin, which was true and meaningless. The pin is not evidence the code
is running. Only a container start after the version was created is.

Fixed in the deploy wrapper. After repinning it now queries recent trace volume and, when
the container is warm, prints a warning rather than a success:

  WARNING: the container has been active in the last 15 minutes.
  It is still running the PREVIOUS image and will not pick up vN
  until it has been idle for roughly 11-15 minutes.
  Do not test immediately: wait for the idle timeout, THEN send the first message.

That turns a silent trap into an instruction. "Deployed and serving" was never a lie, but it
was the wrong thing to report.
