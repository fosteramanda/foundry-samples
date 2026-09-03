# Standing rules (stamped from project-template)

- Canon first. Before writing anything about autopilots, git pull
  C:\src\office-of-amanda and read its
  CANON.md. It outranks every doc on disk; where they disagree,
  the canon wins and the doc is stale - flag it, don't follow it.
- Canon feedback. Whenever Amanda makes a ruling in this workspace
  that adds to, changes, or resolves anything in CANON.md (a
  definition, a rule, an open question), do NOT edit CANON.md.
  Instead: in that office repo, git pull, append one line to
  CANON-PROPOSALS.md in the form "YYYY-MM-DD | <workspace> | <the
  ruling in one sentence> | <canon item it touches, or NEW>",
  commit "canon proposal: <workspace>", and push. Tell Amanda you
  filed it.
- Read STATE.md before any work. If /context/ exists, read it too.
- At the start of each task, ask Amanda how she wants to work,
  unless she already said. Common starting points: run to
  completion (one review at the end, FOR AMANDA section for
  anything needing her attention) or step through with check-ins —
  but treat these as starting points, not a menu. If Amanda
  corrects how you are working mid-task, adjust immediately and
  record her preferred working style in STATE.md so future
  sessions start that way.
- Source docs outside this folder are READ-ONLY: read them in
  place; never copy, move, or modify them. Write only inside this
  folder.
- When a customer's material is involved, open only that customer's
  folder; never open any other customer's folder.
- In customer-facing drafts: no internal codenames, no other
  customer names, and flag any line that promises a date, feature,
  or capability so Amanda decides the phrasing.
- Terminology: follow
  C:\src\autopilot-context\terminology.md
  if it is reachable. Agent identity and agent user account are
  different objects; never conflate them. Do not claim all digital
  workers are autopilots.
- Never include customer names or internal URLs in files under
  /sample.
- Before stopping, update STATE.md: what changed, what was decided,
  what is open.
- Upstream is the public microsoft-foundry/foundry-samples repo. Never push to upstream and never open a pull request without Amanda saying so explicitly. Pushing to her fork branch is fine.
