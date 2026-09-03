# STATE

Last updated: 2026-09-03.

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

**Provenance note, stated plainly:** the session that produced the
uncommitted work ran on 2026-08-19/20 and its conversation is not available
to the session writing this file on 2026-09-03. The decisions below are
reconstructed from `e357e44` and from the rationale comments written into
the diff itself, which are unusually explicit. They are evidence, not
recollection. Where a decision was made verbally and never written down, it
is not here. Amanda should skim this list and correct it.

1. **Split the router sample into an A2A arm and an MCP arm.** One sample
   per transport, so each can be read on its own.

2. **`ToolboxName` was deliberately emptied in this A2A arm.** This is the
   biggest decision in the diff. The project toolbox bundles a `workiq` MCP
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

4. **Attribution is rendered by the host, not by the model.** The prompt
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

6. **Slow delegations become follow-up messages instead of dead ends.** An
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
4. Resolve the ADO contradiction: either restore ADO capability to this arm
   or correct the readme.
5. **Terminology cleanup before this goes anywhere near upstream.** The
   phrase "digital worker" appears 36 times across this sample, including in
   a filename (`scripts\publish-digital-worker.ps1`), a default Azure table
   name (`digitalworkerallowlist`), and 7 times in the readme. Canon is
   explicit that "digital worker" is internal vocabulary and must not appear
   in public or customer-facing material, and upstream is public. Renaming
   the table default is a breaking infrastructure change, so this needs
   Amanda's decision rather than a quiet fix.
6. The other two samples in the sparse checkout are not covered by this file.

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

1. **The decisions list is reconstructed, not remembered.** You asked for
   "every decision we made in this session", but the work in the working tree
   is from 2026-08-19/20 and that conversation is not available to this
   session. I rebuilt the list from the commit and from the rationale
   comments in the diff rather than inventing continuity. Please skim
   "Decisions, and why" and correct anything I got wrong or missed.
2. **"Digital worker" is all over a sample whose upstream is public**, 36
   occurrences including a script filename and a default table name. Canon
   says that term is internal only. The table rename is breaking, so I have
   not touched it. Item 5 in "What is left to do".
3. **The A2A arm currently has no Azure DevOps capability**, because
   `ToolboxName` was emptied, but the readme still advertises it. One of the
   two is wrong.
4. **The asynchronous-delegation work is still uncommitted.** I committed
   only STATE.md and AGENTS.md, as you asked, and left your in-flight code
   alone. Tell me if you want it committed.
5. **.NET 9 SDK is missing on this machine**, so nothing here can be built
   until it is installed.
