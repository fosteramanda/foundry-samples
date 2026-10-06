# Email watches

An email watch lets the manager say, in a 1:1 Teams chat with the agent,
"tell me when sustineo@notareal.co emails back". The agent saves the watch.
When an email from that sender reaches the agent's own mailbox, the agent
posts a short notice in the same chat: who it is from, the subject and a
preview of the text.

## What the manager can say

- "Notify me when Sustineo emails back." The agent looks the name up in the
  directory. An email address works too, including addresses outside the
  organization.
- "Tell me every time Finance emails you." "Every time" or "whenever" keeps
  the watch after the first notice. Otherwise it ends after one notice.
- "Email Sustineo about tonight's meeting and tell me when they reply." The
  agent sends the email as itself, then sets the watch.
- "What emails are you watching for?" and "Stop watching for Sustineo."

## What it can and cannot see

The agent only sees email that reaches its own mailbox: email sent to it or
copied to it. It never sees the manager's inbox. A watch on someone who
replies only to the manager will never fire. The agent says this when it
confirms a watch, with its own address, so the manager knows to copy it or to
have it send the email.

A watch only notifies. It never replies to the sender and never acts on the
email's content. That is why it works for any sender, including people the
agent otherwise ignores: email from anyone who is not the manager or an
approved teammate still gets no reply, exactly as before.

Only the manager can set or stop a watch, and only in a 1:1 chat, because the
notice quotes part of the email. In a group chat, or from anyone else, the
tool refuses and nothing is saved.

## How it works

Three local tools are attached to chat turns by `EmailWatchToolHandler`:

| Tool | What it does |
|---|---|
| `watch_email_from` | Resolves the sender (name through the directory, or an address as typed), then saves a watch for this chat. |
| `list_email_watches` | Lists the watches saved for this chat. |
| `stop_email_watch` | Deletes the matching watches in this chat. |

Before saving or stopping a watch, the handler checks that the turn is a Teams
`personal` conversation and that the speaker resolves, through the existing
access-control lookup, to the agent's current manager.

Each watch is one row in the existing conversation-state table, in the agent
instance's partition (`{tenantId}:{agentUserId}`). The row key is
`email-watch-` plus a hash of the chat ID and the sender address, so asking
twice for the same sender in the same chat updates one watch instead of adding
a second. No other row in that table uses the prefix. The row records the
chat, the requester, every address that counts as the sender, the optional
note, whether it repeats, and the last email it notified about.

Every email that reaches the agent's mailbox arrives as an Agent 365 email
notification. `HandleEmailNotificationAsync` now calls the watch check first,
before the sender-approval check:

1. If no watches exist, it stops. No mail is read.
2. It reads the message from the agent's own mailbox through Microsoft Graph
   (`me/messages/{id}`, selecting the sender, subject and preview). If that
   read fails, it falls back to the notification's sender and text.
3. For each watch on that sender, it claims the notice before posting: a
   one-time watch is deleted, and a repeating watch records the message, both
   with an ETag check. A duplicate notification for the same email therefore
   cannot post twice.
4. It posts the notice into the watch's chat through the same Graph chat
   delivery that scheduled routines use, acting as the agent user. If posting
   fails, the watch is put back so the next email can still notify.

A failure anywhere in the watch check is logged and never stops the normal
email handling that follows.

The prompt section on watches appears only when the tools are attached. It
tells the model to use `watch_email_from` instead of creating a routine to poll
for email, and to state the mailbox limit in its confirmation.

## Configuration

`EnableEmailWatches` defaults to `true`. The tools are attached only when it is
true, durable conversation-state storage is configured, and the agent has a
Graph token. No new Azure resource, table, role or Graph permission is needed:
the agent already reads its own mail and posts to its own chats.

## Checking it

Unit tests are in `tests/AutopilotRouterAgentA2A.Tests/EmailWatchTests.cs`:
manager-only and 1:1-only creation, name lookup, outside addresses, one notice
for a one-time watch, one notice per new email for a repeating watch,
duplicates, other senders, the fallback sender, restoring after a failed post,
HTML encoding of email content, and the prompt section.

End to end: in the manager's 1:1 chat, ask the agent to tell you when a test
user emails it. Send an email from that user to the agent's address. Within
about a minute the chat shows the notice. In Application Insights, look for
`Email watch saved` and then `Email watch notice posted`.
