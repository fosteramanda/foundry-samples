# Seed data for the Shared Blob Workspace sample

This folder holds the **starter material** for the demo. You upload it into *your own* blob
container once, before running the walkthrough. From then on it lives on the shared **project
drive** that Scout and Quill both mount.

> ⚠️ **All product names and data here are fictional and illustrative** — invented for a
> self-contained demo. They are *not* real products, and the pricing/features/sentiment are
> made up. This keeps the sample reproducible with **no live web access** (Scout "researches"
> by distilling this raw material into structured findings).

## What's here

| Path | What it is | Who uses it |
|---|---|---|
| `sources.csv` | Priya's starting shortlist — 5 companies + links + category | given to **Scout** in prompt 1 |
| `raw/*.md` | Messy, unstructured source notes for each company (pricing, features, target customer, chatter) | **Scout** extracts & organizes these |
| `late-arrival/claronotes.md` | A 6th competitor that "launches later" | dropped in for **prompt 5 / Session 3** |

## How to seed it into your workspace container

Upload the **contents** of this folder into the container you'll mount as the project drive,
under a `raw-inbox/` prefix (so it doesn't collide with the `ai-notes-analysis/` project the
agents build). For example, with Azure CLI:

```bash
az storage blob upload-batch \
  --account-name "$AZURE_STORAGE_ACCOUNT" \
  --destination "$WORKSPACE_CONTAINER/raw-inbox" \
  --source . --auth-mode login
```

(You only need `sources.csv` and `raw/` to start; hold `late-arrival/` back until Session 3 to
mimic a competitor appearing mid-project — or upload it all now and just tell Scout about it in
prompt 5.)

Once seeded, Scout mounts the drive and reads from `raw-inbox/`; the deliverables it and Quill
produce land under `ai-notes-analysis/`.
