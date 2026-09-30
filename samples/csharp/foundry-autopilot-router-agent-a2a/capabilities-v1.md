# A2A router capability configuration

This sample uses its agent identity and agent user account to act as itself.
Its manager grants access; working for the manager does not make the manager
the sender of its email or the organizer of its meetings. See
[What is an autopilot?](https://learn.microsoft.com/azure/foundry/agents/concepts/autopilot-overview).

## Durable work and conversation state

Set `WORK_ITEMS_TABLE_SERVICE_URI` and `WORK_ITEMS_TABLE_NAME` in the existing
deployment environment. The agent-creation script supplies those settings to
each new version and uses the same table account for conversation state.
The runtime identity needs Storage Table Data Contributor on that account.

`ConversationStateTableServiceUri` overrides the shared account when nonempty.
Missing, empty and whitespace values inherit `WorkItemsTableServiceUri`.
Conversation pointers and tracked work use separate tables and per-instance
partitions. A storage failure is logged explicitly; container-local conversation
fallback does not provide continuity across restarts.

## Mail and calendar belong to the agent

With its agent-user Graph token, the local mailbox handler exposes:

- `send_email_as_agent`: send from the agent's mailbox.
- `create_calendar_event_for_agent`: create an event in its calendar and invite
  the requested participants. Include the manager if the manager should attend.
- `list_agent_calendar`: read its calendar, including invitations it received.

These tools cannot establish the manager's availability or read their private
calendar. An invitation does not mean that the agent joins the meeting.
Calendar ranges without an explicit offset use the requested time zone rather
than the host's local time. Large result sets are reported as partial.

The older delegated-manager experiment is off by default. It can only be
selected explicitly with `EnableManagerMailboxTools=true`, requires separate
Exchange grants, and is not the as-itself behavior described above. There is no
automatic fallback between mailboxes.

## Access applies across surfaces

Email and document-comment notifications must come from the instance's manager
or a manager-approved teammate. The host resolves the sender's directory
identity and uses the same persistent allowlist as Teams. Unknown senders,
unresolved managers and cross-tenant notifications are rejected before model or
tool execution, with a warning in the application logs.

For email and comment turns, instructions advertise only the local tools
actually attached on that path. Word comments still reply through the document
comment tool rather than sending duplicate chat or email responses.

## Delegation and evidence

Discovery, agent-card reads and calls continue through the generic Work IQ A2A
handler. A specialist is not installed as its own tool. Empty or pending answers
are not presented as successful completed work.

Tool registration alone does not prove permissions, downstream availability or
delivery. Verify a real Teams turn, a saved work item, an email received from the
agent, and a document comment before describing those actions as working.
Creating a routine does not prove scheduled delivery, and a registered meeting
does not prove transcript access.
