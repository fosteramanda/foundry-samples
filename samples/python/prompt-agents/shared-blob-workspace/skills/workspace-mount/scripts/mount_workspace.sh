#!/usr/bin/env bash
# Mount a user-owned Azure Blob container as a plain folder ("project drive") inside a
# managed harness sandbox, using the agent's managed identity (MSI) — no key or SAS.
#
# Subcommands: mount (default) | flush | unmount | status
#
# Configuration via environment (see SKILL.md):
#   AZURE_STORAGE_ACCOUNT              (required) storage account name
#   WORKSPACE_CONTAINER               (required) container to mount
#   MOUNT_PATH                        (default $HOME/project)
#   TMP_PATH                          (default /tmp/blobfuse2)
#   AZURE_STORAGE_IDENTITY_CLIENT_ID  (optional) user-assigned MI client id
#
# Everything here was validated live on a real hosted-agent Hand:
#   * the base image ships no blobfuse2/libfuse3/fusermount3 -> install on demand
#   * /dev/fuse is absent on a fresh/reset Hand -> create it (mknod c 10 229)
#   * daemon-mode `blobfuse2 mount` can return exit 0 even on a failed background mount
#     -> verify with `mountpoint -q` and fail loudly
#   * a recycled Hand can leave a dead FUSE handle -> clean it up before remounting
set -o pipefail

MOUNT_PATH="${MOUNT_PATH:-/workspace/project}"
TMP_PATH="${TMP_PATH:-/tmp/blobfuse2}"

log()  { printf '%s %s\n' "[workspace-mount]" "$*" >&2; }
fail() { printf '%s ERROR: %s\n' "[workspace-mount]" "$*" >&2; exit 1; }

require_config() {
  [ -n "${AZURE_STORAGE_ACCOUNT:-}" ] || fail "AZURE_STORAGE_ACCOUNT is not set (env or --account)"
  [ -n "${WORKSPACE_CONTAINER:-}" ]   || fail "WORKSPACE_CONTAINER is not set (env or --container)"
}

# Flags override env, so the agent can pass values inline: `... mount --account X --container Y`.
parse_flags() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --account)    AZURE_STORAGE_ACCOUNT="$2"; shift 2 ;;
      --container)  WORKSPACE_CONTAINER="$2";   shift 2 ;;
      --mount-path) MOUNT_PATH="$2";            shift 2 ;;
      --tmp-path)   TMP_PATH="$2";              shift 2 ;;
      --client-id)  AZURE_STORAGE_IDENTITY_CLIENT_ID="$2"; shift 2 ;;
      *)            fail "unknown option '$1'" ;;
    esac
  done
}

is_mounted() { mountpoint -q "$MOUNT_PATH" 2>/dev/null; }

ensure_blobfuse2() {
  if command -v blobfuse2 >/dev/null 2>&1; then
    return 0
  fi
  log "blobfuse2 not found; installing from packages.microsoft.com ..."
  export DEBIAN_FRONTEND=noninteractive
  # shellcheck disable=SC1091
  . /etc/os-release
  local deb="/tmp/packages-microsoft-prod.deb"
  curl -fsSL -o "$deb" "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb" \
    || fail "failed to download packages-microsoft-prod.deb (check egress)"
  dpkg -i "$deb" >/dev/null 2>&1 || true
  apt-get update -y >/dev/null 2>&1 || fail "apt-get update failed"
  apt-get install -y blobfuse2 >/dev/null 2>&1 || fail "apt-get install blobfuse2 failed"
  command -v blobfuse2 >/dev/null 2>&1 || fail "blobfuse2 still not on PATH after install"
}

ensure_dev_fuse() {
  # Fresh/reset Hands lack /dev/fuse; without it blobfuse2 dies with "device not found".
  if [ ! -e /dev/fuse ]; then
    log "creating /dev/fuse"
    mknod /dev/fuse c 10 229 || fail "could not create /dev/fuse (is the sandbox privileged?)"
    chmod 666 /dev/fuse 2>/dev/null || true
  fi
}

cleanup_stale() {
  # A recycled Hand can leave a dead FUSE handle: 'mountpoint' is unreliable and remount
  # fails with "directory not empty" / "transport endpoint not connected". Clear it.
  fusermount3 -u "$MOUNT_PATH" >/dev/null 2>&1 || true
  fusermount  -u "$MOUNT_PATH" >/dev/null 2>&1 || true
}

do_mount() {
  require_config
  if is_mounted; then
    log "already mounted at $MOUNT_PATH"
    return 0
  fi
  ensure_blobfuse2
  ensure_dev_fuse
  cleanup_stale
  mkdir -p "$MOUNT_PATH" "$TMP_PATH"

  export AZURE_STORAGE_ACCOUNT
  export AZURE_STORAGE_ACCOUNT_TYPE="block"
  export AZURE_STORAGE_AUTH_TYPE="MSI"
  # User-assigned MI, if provided; system-assigned needs nothing extra.
  if [ -n "${AZURE_STORAGE_IDENTITY_CLIENT_ID:-}" ]; then
    export AZURE_STORAGE_IDENTITY_CLIENT_ID
  fi

  log "mounting container '$WORKSPACE_CONTAINER' from account '$AZURE_STORAGE_ACCOUNT' at $MOUNT_PATH (MSI auth)"
  blobfuse2 mount "$MOUNT_PATH" \
    --container-name="$WORKSPACE_CONTAINER" \
    --tmp-path="$TMP_PATH"

  # blobfuse2 daemonizes; give the background mount a moment, then VERIFY.
  sleep 3
  if ! is_mounted; then
    fail "mount reported success but $MOUNT_PATH is not a mountpoint (check RBAC/account/container)"
  fi
  log "mounted OK: $(findmnt -no FSTYPE,SOURCE "$MOUNT_PATH" 2>/dev/null)"
}

do_unmount() {
  if ! is_mounted; then
    cleanup_stale
    log "nothing mounted at $MOUNT_PATH"
    return 0
  fi
  sync
  log "flushing and unmounting $MOUNT_PATH"
  fusermount3 -u "$MOUNT_PATH" >/dev/null 2>&1 || fusermount -u "$MOUNT_PATH" >/dev/null 2>&1 \
    || fail "unmount failed"
  log "unmounted (writes flushed to blob)"
}

do_flush() {
  # Force cached writes up to blob for the next session, but keep the drive usable.
  if is_mounted; then
    do_unmount
  fi
  do_mount
  log "flush complete: recent writes are durable and drive remounted"
}

do_status() {
  if is_mounted; then
    printf 'mounted: %s\nsource:  %s\n' "$MOUNT_PATH" "$(findmnt -no FSTYPE,SOURCE "$MOUNT_PATH" 2>/dev/null)"
  else
    printf 'not mounted: %s\n' "$MOUNT_PATH"
  fi
}

case "${1:-mount}" in
  mount)   shift 2>/dev/null || true; parse_flags "$@"; do_mount ;;
  flush)   shift 2>/dev/null || true; parse_flags "$@"; do_flush ;;
  unmount) shift 2>/dev/null || true; parse_flags "$@"; do_unmount ;;
  status)  shift 2>/dev/null || true; parse_flags "$@"; do_status ;;
  *)       fail "unknown subcommand '$1' (use: mount | flush | unmount | status)" ;;
esac
