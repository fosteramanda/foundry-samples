# Leadership review publication

This extension gives an existing standing job a versioned Word brief and a
shared, inspectable decision and commitment ledger. The manager's mandate,
participant boundaries and evidence requirements remain unchanged.

`publish_standing_brief` reads the job's stored inputs, decisions, specialist
receipts and linked commitments. It combines those facts with the proposed
summary, remaining decision and options. The host renders those sections as
HTML and calls the existing Word MCP `CreateDocument` tool. It does not edit
document XML or overwrite an earlier Word revision.

The resulting file is read back through the agent user's own Graph drive.
Publication verifies that the creator is that agent user, obtains the real
Word document identity, and grants edit access only to the job's configured
members. Creation without a verified sharing result is not reported as a
completed publication.

Each new facts fingerprint produces a new numbered document. Repeating the
same publication reuses its receipt rather than creating duplicate files.
The recorded file URL is used for the pre-read and subsequent updates.
Generated brief documents automatically become approved comment bindings
for the same job, so a comment can challenge the record using its source
notes. That derived binding does not alter the mandate or cause a loop of
unnecessary publications.

The automatic job still uses the controlled standing-job tools. It does not
receive unrestricted Word, email or calendar mutation tools. Calendar
logistics remain outside the standing review, and all leadership decisions
come from recorded manager evidence. The specialist is invoked through the
existing generic A2A path, never a new per-agent connection.

Demonstrate a whole cycle before recording: a standing mandate, missing
inputs, an actual specialist answer, a pre-read, manager decision notes,
source-supported owner updates and one closed commitment. A final escalation
should contain only the unresolved judgment, with its evidence and options.
An available tool or successful container startup does not prove this loop.
