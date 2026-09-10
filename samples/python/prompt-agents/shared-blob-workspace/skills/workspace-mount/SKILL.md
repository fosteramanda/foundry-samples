---
name: workspace-mount
description: Mount a user-owned Azure Blob container as a plain folder (the "project drive") inside a managed harness sandbox, using the agent's managed identity (MSI) — no account key or SAS. Use at the start of every session before reading or writing shared project files, so multiple agents and multiple sessions all see the same durable workspace. Also flushes/unmounts so writes become durable for the next session. Requires the agent identity to hold 'Storage Blob Data Contributor' on the container or account.
---

# Workspace Mount

## Overview

Give the agent a **shared project drive**: mount a blob container the *user owns* as an
ordinary folder inside the harness sandbox. Once mounted, reading and writing project files
(`sources.csv`, `findings/*.md`, `report.md`, `status.md`, …) is plain filesystem I/O — no
storage SDK, no keys, no SAS. Authentication uses the **agent's managed identity** via
blobfuse2's `MSI` auth, so no credential ever enters the command line or the model's context.

The same container mounted from any session or any agent is the **same drive** — this is what
lets Scout and Quill hand work off to each other across days and separate chats.

> This wraps `blobfuse2` (a FUSE client for Azure Blob). It is an **interim** pattern for
> giving hosted agents a durable, shareable workspace until native blob-volume mounting is
> available.

## Prerequisites

- **Managed harness sandbox** running privileged (the default for hosted-agent Hands) — the
  script needs to create `/dev/fuse` and mount FUSE.
- **RBAC**: the agent identity must have **Storage Blob Data Contributor**
  (role id `ba92f5b4-2d11-453d-a403-e96b0029c9fe`) on the target container or account.
  A mount that succeeds but shows an empty/again-empty folder, or a 403 in `blobfuse2` logs,
  means the role is missing or still propagating (allow 5–10 min).
- **Egress** to `packages.microsoft.com` (only on first run, to install `blobfuse2` if the
  base image doesn't ship it).

## Configuration (environment variables)

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `AZURE_STORAGE_ACCOUNT` | ✅ | — | Storage account name (no `.blob.core.windows.net`). |
| `WORKSPACE_CONTAINER` | ✅ | — | Blob container to mount as the project drive. |
| `MOUNT_PATH` | | `/workspace/project` | Where the drive appears in the sandbox. |
| `TMP_PATH` | | `/tmp/blobfuse2` | blobfuse2 local file cache. |
| `AZURE_STORAGE_IDENTITY_CLIENT_ID` | | — | Client ID of a **user-assigned** MI, if the identity isn't system-assigned. |

## Usage

```bash
# Mount (idempotent): ensure the project drive is available at $MOUNT_PATH.
# Values may come from the environment OR be passed inline as flags (flags win):
export AZURE_STORAGE_ACCOUNT="mystorageacct"
export WORKSPACE_CONTAINER="agent-projects"
bash scripts/mount_workspace.sh mount
#   equivalently, inline:
#   bash scripts/mount_workspace.sh mount --account mystorageacct --container agent-projects

# Make recent writes durable for the NEXT session, keep the drive usable (unmount + remount)
bash scripts/mount_workspace.sh flush

# End of a work session: flush and detach the drive
bash scripts/mount_workspace.sh unmount

# Report current state (mounted? where? source?)
bash scripts/mount_workspace.sh status
```

Subcommands:

- **`mount`** *(default)* — install blobfuse2 if missing, ensure `/dev/fuse`, clean up any
  stale/dead mount left by a recycled sandbox, mount with MSI auth, then **verify** it is a
  real mountpoint (fails loudly if the background mount silently failed).
- **`flush`** — force cached writes up to blob (unmount, which flushes, then remount) so the
  next agent/session sees them, without ending your working session.
- **`unmount`** — flush and detach; call at the end of a work session.
- **`status`** — print whether `MOUNT_PATH` is mounted and its source.

## Key rules

- **Always `mount` at the start of a session** before touching project files; it's idempotent
  and safe to run every time.
- **Durability is not automatic.** blobfuse2 caches writes locally and uploads on flush/unmount.
  If you produced files another session needs, run **`flush`** (or `unmount`) before you stop —
  otherwise the next session won't see them.
- **A silently "successful" mount is real.** Daemon-mode `blobfuse2 mount` can return exit 0 even
  when the background mount failed; this script verifies with `mountpoint -q` and exits non-zero
  on failure. Trust the exit code, not the log noise.
- **403 / empty drive = missing or still-propagating RBAC**, not transient — report it plainly.
- Treat the drive as a shared surface: read `status.md` first, and update it when you finish.
