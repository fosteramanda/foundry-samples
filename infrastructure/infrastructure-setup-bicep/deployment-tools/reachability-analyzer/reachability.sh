#!/usr/bin/env bash
# Copyright (c) Microsoft. All rights reserved.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<'EOF'
Usage:
  reachability.sh --account ACCOUNT_ID --endpoint HOST --port PORT [options]
  reachability.sh --project PROJECT_ID --endpoint HOST --port PORT [options]
  reachability.sh --subnet-id SUBNET_ID --endpoint HOST --port PORT [options]
  reachability.sh -g RG --vnet VNET --subnet SUBNET --endpoint HOST --port PORT [options]

Read-only control-plane analysis; no resources are deployed.

Options:
  --subscription SUB   Subscription ID or name (defaults to the source resource).
  --html FILE          HTML report path (defaults to reachability-HOST_PORT.html).
  --no-open            Do not open the HTML report in a browser.
  --mode static        Optional compatibility flag; only static mode is supported.
  -h, --help           Show this help.

For live DNS/TCP/TLS/HTTP checks, use the existing diagnostic agent:
  samples/python/hosted-agents/bring-your-own/invocations/diagnostic-agent

Exit codes: 0 configuration permits the path, 1 blocked, 3 indeterminate,
            2 usage or setup error.
EOF
}

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 2
}

require_value() {
  [[ $# -ge 2 && -n "$2" && "$2" != -* ]] || fail "$1 requires a value."
}

SOURCE_KIND="" SOURCE_ID="" RG="" VNET="" SUBNET=""
ENDPOINT="" PORT="" SUBSCRIPTION="" HTML="" MODE="static"
NO_OPEN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --account|--project|--subnet-id)
      require_value "$@"
      [[ -z "$SOURCE_KIND" ]] || fail "Provide only one source."
      SOURCE_KIND="$1"
      SOURCE_ID="$2"
      shift 2
      ;;
    -g|--resource-group|--vnet|--subnet|--endpoint|--port|--subscription|--html|--mode)
      require_value "$@"
      case "$1" in
        -g|--resource-group) RG="$2";;
        --vnet) VNET="$2";;
        --subnet) SUBNET="$2";;
        --endpoint) ENDPOINT="$2";;
        --port) PORT="$2";;
        --subscription) SUBSCRIPTION="$2";;
        --html) HTML="$2";;
        --mode) MODE="$2";;
      esac
      shift 2
      ;;
    --no-open) NO_OPEN=1; shift;;
    -h|--help) usage; exit 0;;
    *) fail "Unknown argument: $1. Use --help for supported options.";;
  esac
done

[[ "$MODE" == "static" ]] ||
  fail "Only static mode is included. Use the existing diagnostic agent for live checks (see --help)."
[[ -n "$ENDPOINT" && -n "$PORT" ]] || fail "--endpoint and --port are required."
[[ "$PORT" =~ ^[0-9]{1,5}$ ]] || fail "--port must be an integer from 1 to 65535."
((10#$PORT >= 1 && 10#$PORT <= 65535)) || fail "--port must be an integer from 1 to 65535."
PORT="$((10#$PORT))"

if [[ -n "$RG" || -n "$VNET" || -n "$SUBNET" ]]; then
  [[ -z "$SOURCE_KIND" ]] || fail "Do not combine resource IDs with -g/--vnet/--subnet."
  [[ -n "$RG" && -n "$VNET" && -n "$SUBNET" ]] ||
    fail "Provide all of -g/--resource-group, --vnet, and --subnet."
elif [[ -z "$SOURCE_KIND" ]]; then
  fail "Provide --account, --project, --subnet-id, or -g/--vnet/--subnet."
fi

command -v python3 >/dev/null || fail "python3 is required."
command -v az >/dev/null || fail "Azure CLI (az) is required."

SUB_ARGS=()
[[ -z "$SUBSCRIPTION" ]] || SUB_ARGS=(--subscription "$SUBSCRIPTION")
if [[ -z "$SOURCE_KIND" ]]; then
  SOURCE_KIND="--subnet-id"
  SOURCE_ID="$(az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SUBNET" \
    "${SUB_ARGS[@]}" --query id -o tsv)" || fail "Could not resolve the subnet resource ID."
  [[ -n "$SOURCE_ID" ]] || fail "Azure CLI returned no subnet resource ID."
fi

if [[ -z "$HTML" ]]; then
  SAFE="$(printf '%s' "${ENDPOINT}_${PORT}" | tr -c 'A-Za-z0-9._-' '_')"
  HTML="$PWD/reachability-${SAFE}.html"
fi

RC=0
python3 "$SCRIPT_DIR/analyze.py" "$SOURCE_KIND" "$SOURCE_ID" \
  --endpoint "$ENDPOINT" --port "$PORT" "${SUB_ARGS[@]}" --html "$HTML" || RC=$?

# A setup error may leave an old report at this path; do not open that report.
if [[ "$NO_OPEN" -eq 0 && -f "$HTML" ]] && [[ "$RC" -eq 0 || "$RC" -eq 1 || "$RC" -eq 3 ]]; then
  OPENER=""
  for candidate in wslview xdg-open open; do
    if command -v "$candidate" >/dev/null 2>&1; then
      OPENER="$candidate"
      break
    fi
  done
  if [[ -z "$OPENER" ]]; then
    printf 'Open %s in a browser to view the report.\n' "$HTML"
  elif ! "$OPENER" "$HTML"; then
    printf 'WARNING: Could not open the browser; open %s manually.\n' "$HTML" >&2
  fi
fi

exit "$RC"
