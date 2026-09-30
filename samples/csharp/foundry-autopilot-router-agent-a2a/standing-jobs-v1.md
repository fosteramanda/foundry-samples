# Standing jobs

A standing job is a durable responsibility for this sample's existing autopilot.
It does not create a new agent, identity or permission grant. The agent still acts
with its own agent identity and agent user account.

## What the code adds

`StandingJobStore` stores mandates, participants, source evidence, input state,
decisions and action receipts in the existing Azure Table account. Every row is
partitioned by tenant and agent user account. A job is shared by its approved
participants, not tied to one private conversation.

`StandingReviewCoordinator` correlates source updates, derives the current phase,
checks evidence and leases a run so replicas do not act concurrently. Action
scopes are claimed before an external call. An uncertain call stays unresolved
instead of being repeated after a restart or at the next timer tick.

`StandingJobToolHandler` exposes the manager's configuration tools and the
bounded operations the job can perform. Existing mail, task, A2A and Graph chat
delivery implementations are reused.

## Configure a job

Use the manager's personal Teams conversation to assign the responsibility.
The manager may create, revise, pause or resume it. Resolve participants through
`resolve_standing_member`; the directory ID and primary address must match.

A mandate specifies:

- Its purpose and the review time, if known.
- The participants who may read and contribute to this job.
- Permitted outbound email recipients, independently of the participant list.
- Exact source bindings: `word:<document-guid>` or `mail:<conversation-id>`.
- Explicitly permitted specialist IDs discovered through the existing A2A path.
- A five-field schedule and time zone. The default check is every 15 minutes;
  the service requires at least five minutes between scheduled occurrences.

The manager is the only default participant and recipient. Adding someone to a
job does not add them to the instance's inbound allowlist or grant file access.
The existing instance access checks and source permissions still apply.
Bind only documents approved for that job's shared information: a bound Word
thread can receive answers informed by the job record. Personal Teams chats,
approved email and bound document comments can access standing jobs; ordinary
group chats do not expose these job tools to a potentially wider audience.

The job is first saved paused. It becomes active only after its routine is
configured. Schedule failures are explicit; a saved mandate is not reported as
running when scheduling failed. Pause is applied to local job state before the
remote schedule is disabled, so a concurrent tick cannot ignore it.

## Carry work across surfaces

Job mail subjects contain `[job:<guid>]`. Replies with that reference, and
comments on bound Word documents, reach the same job record. Only the latest
authored email text is eligible as evidence; quoted thread history is not
automatically treated as the current sender's decision.

The existing email and Word reply paths receive the resulting job state. A
comment can therefore refer to the actual shared decision or commitment rather
than a separate chat's memory.

Automatic checks get only the controlled standing-job tools. They do not inherit
the manager's credentials or receive unrestricted native MCP mutation tools.
They use recorded inputs and authenticated updates, not a crawl of every file in
the tenant. Normal, explicitly requested Word and Excel operations keep their
existing tools and behavior. This version does not automatically synchronize
the job ledger into a SharePoint list or workbook.

## Decisions and commitments

`record_standing_input` distinguishes missing, received and disputed inputs.
Received facts must quote a stored input from the owner or manager.
`record_standing_decision` records the manager's literal words, not a decision
invented by the model. A rationale must also be supported by that source.
Corrections append a new record identifying the decision they supersede.

Commitments retain their existing task storage and gain job and decision links,
explicit dependency IDs, completion evidence and follow-up history. Creation
uses a stable ID to avoid duplicates. The ordinary task tools cannot bypass
the standing-job evidence checks.
Job-owned commitments are excluded from the legacy unscoped task lists and
summaries. Use the job-aware tools, which check the caller's participation.

An owner or manager's actual source event is required to change a commitment.
Only the manager can reassign its owner. A task cannot close while an explicit
dependency is open, or without recorded completion confirmation.

## Follow through safely

Owner input requests and due follow-ups go only to the recorded owner's
permitted email. A follow-up is eligible within one day of its explicit due
time. Attempts are limited to one per job, purpose and recipient per UTC day.
Briefs and escalations are deduplicated against the recorded facts.

Chat updates go to the job's original manager conversation. A Graph message ID
confirms the post. Graph acceptance of email is recorded as submission, not
independent proof of inbox receipt.

Specialists use the same generic A2A handler as ordinary turns. A missing or
pending specialist answer is not a completed result. A pending result is
retained for reconciliation rather than automatically reissued or promised
through an unrelated proactive delivery path.

The run status and individual receipts remain inspectable through
`get_standing_job`. A successful model turn alone does not mean every action
succeeded. Pending or uncertain receipts need destination reconciliation before
retrying; they are not silently cleared.

## Storage and operation

`EnableStandingJobs` defaults to true when durable storage is available.
`StandingJobsTableServiceUri` overrides `WorkItemsTableServiceUri`, then the
existing allowlist account. `StandingJobsTableName` defaults to `standingjobs`.
The runtime uses its existing managed-identity storage access. If the store
cannot initialize, job tools are disabled explicitly; there is no local-file
fallback for standing responsibilities.

Review times are explicit UTC-offset values. Calendar administration and the
CEO's decisions remain human responsibilities. Meeting registration is not
attendance or transcript access.

For a first run, use sandbox participants and a short-lived test mandate.
Prove a missing-input request, an owner update, a manager decision, a linked
commitment and evidence-based closure. Pause the test job afterward. Check its
receipts and the actual destination, not only its final natural-language reply.
