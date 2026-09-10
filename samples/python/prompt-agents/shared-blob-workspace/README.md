# Shared Blob Workspace — a project drive for your crew of agents

A **prompt agent** sample that gives a team of specialist agents one **shared, durable
workspace** backed by Azure Blob Storage. Each agent mounts the same blob container as a plain
folder, so **what one agent produces, another builds on** — even across different chats and
different days.

The workspace is just storage the *user* owns and controls. The persistence, syncing, and
mount mechanics are invisible plumbing — from the user's point of view there is only a
**"project drive"** that every agent and every session can see.

## The story

**Priya**, a product manager, is putting together a competitive analysis of the **AI
meeting‑notes market**. Instead of one mega‑agent, she works with a small crew of specialists:

- **📚 Scout** *(Research agent)* — gathers and organizes raw material.
- **✍️ Quill** *(Analyst / Writer agent)* — turns raw material into a polished deliverable.

They work like **teammates sharing a drive** — each picks up what the other produced and keeps
the project moving, even across days and separate chats. They coordinate entirely through files
in the shared **project drive**; neither has to re‑explain context to the other.

### The shared project drive

Both agents mount the same blob container as a folder and see one project directory:

```
ai-notes-analysis/
  sources.csv        # companies + links Scout found
  findings/          # one note file per competitor
  raw_notes.md       # Scout's running scratchpad
  report.md          # Quill's deliverable
  status.md          # handoff baton: "what's done / what's next"
```

`status.md` is the **handoff baton** — a tiny, human‑readable note that tells the next agent
(and Priya) where things stand, so anyone can pick up the project cold.

### The walkthrough — 5 prompts, 2 agents, 3 sessions

#### 🗓️ Session 1 — with **Scout** (Research)

**1. Kick off the research**
> *"Scout, I'm researching the AI meeting‑notes market. Find the top 5 players, and for each
> one capture pricing, key features, and target customer. Save everything to our project drive."*

Scout writes `sources.csv`, one file per competitor under `findings/`, and a `status.md` noting
what's done and what's still needed. **The drive now holds the raw research.**

**2. Deepen one thread**
> *"Good. Now dig into user sentiment — pull common complaints and praise for each, and note any
> recent funding news. Add it to what you've already got."*

Scout **reads its own earlier files**, appends a sentiment section to each note, updates
`raw_notes.md`, and revises `status.md` → *"Research complete. Ready for analysis."* Priya closes
her laptop for the day.

#### 🗓️ Session 2 — next morning, a **brand‑new conversation** — with **Quill** (Analyst)

**3. Hand off to the writer**
> *"Quill, our researcher left everything in the project drive. Read it and draft a competitive
> analysis report — exec summary, a comparison table, and a recommendation on where the gaps are."*

Quill is a **different agent in a fresh session** — yet it opens the shared drive, reads
`status.md`, `sources.csv`, and every file in `findings/`, and writes `report.md`. *This is the
"aha" moment:* a new agent, a new session, picking up the work seamlessly.

**4. Refine the deliverable**
> *"Nice first draft. Tighten the exec summary to 5 bullets, and add a pricing‑vs‑features
> quadrant so we can see who's overpriced."*

Quill reopens `report.md`, revises it in place, adds the quadrant, and marks `status.md`
*"Report v2 ready for review."*

#### 🗓️ Session 3 — a week later — back with **Scout**, then **Quill**

**5. The market moved; loop the crew again**
> *"A new competitor just launched — 'Granola.' Scout, profile it like the others and add it to
> the drive. Then Quill can fold it into the report."*

Scout adds `findings/granola.md` and a row in `sources.csv`; Quill later reads the updated drive
and refreshes the comparison table and recommendation. **Weeks later, across many sessions and
two agents, the project is a single living workspace — nothing was ever re‑explained or re‑done.**

## What this sample demonstrates

- **Specialists, not one mega‑agent** — Scout researches, Quill writes; each stays in its lane.
- **Seamless handoff** — each agent builds directly on the other's output through the shared
  drive, with no re‑briefing or repeated context.
- **Continuity across sessions & agents** — a new chat, a different agent, a week later: the
  work is all still there.
- **The user never thinks about storage** — "project drive" is the only concept the user sees;
  mounts, syncing, and persistence are handled for them.

## Status

🚧 **Work in progress.** This README captures the scenario and walkthrough. The runnable sample
(agent definitions, the shared‑drive setup, and the run script) is being added next.
