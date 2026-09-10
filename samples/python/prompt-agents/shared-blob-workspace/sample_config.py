"""Shared, sample-owned identifiers for the shared-blob-workspace sample.

The sample provisions its own tool surface, so the toolbox name and its project connection are
**constants owned by the sample** (not user-supplied env vars). The only input needed to
address the connection is the Foundry project's ARM id (``PROJECT_RESOURCE_ID``), from which
the connection's ARM id is derived deterministically.

Both agents (Scout and Quill) share the SAME toolbox/connection — the single ``workspace-mount``
skill is all either one needs.
"""
import shutil
import subprocess

# Two specialist agents sharing one project drive.
SCOUT_AGENT_NAME = "scout-research"
QUILL_AGENT_NAME = "quill-analyst"

# Toolbox the workspace-mount skill is attached to. Created on the first
# `provision_skills.py` run (POST /toolboxes/{name}/versions creates it if absent).
TOOLBOX_NAME = "workspace-tools"

# Project connection (category RemoteTool, AgenticIdentityToken auth) that fronts the toolbox's
# MCP endpoint. Both agents reference the toolbox only through this connection.
TOOLBOX_CONNECTION_NAME = "workspace-tools-toolbox"


def run_az(args):
    """Run the Azure CLI robustly across platforms.

    On Windows the CLI is ``az.CMD``, which ``subprocess`` cannot launch by the bare name
    without a shell — and ``shell=True`` with a *list* is broken (only the first item reaches
    the shell). Resolving the real executable via ``shutil.which`` sidesteps both, with no
    shell quoting to mangle JSON/paths. Returns the completed process (stdout/stderr captured).
    """
    exe = shutil.which("az") or "az"
    return subprocess.run([exe, *args], capture_output=True, text=True)


def toolbox_mcp_url(project_endpoint: str) -> str:
    """The toolbox's data-plane MCP endpoint — the connection's ``target``."""
    return f"{project_endpoint.rstrip('/')}/toolboxes/{TOOLBOX_NAME}/mcp"


def toolbox_connection_id(project_resource_id: str) -> str:
    """Full ARM id of the toolbox connection, derived from the project resource id."""
    return f"{project_resource_id.rstrip('/')}/connections/{TOOLBOX_CONNECTION_NAME}"
