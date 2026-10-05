# Copyright (c) Microsoft. All rights reserved.
"""GitHub Copilot SDK harness for the agent.

"""

from __future__ import annotations

import asyncio
import hashlib
import logging
import os
import uuid

from azure.identity import DefaultAzureCredential
from copilot import (
    CopilotClient,
    PermissionHandler,
    ProviderConfig,
    SessionEventType,
    ToolSet,
)

import tools

logger = logging.getLogger("github-copilot.client")

# The Copilot SDK ships a coding-assistant system prompt by default. Replace it
# with a Teams-assistant persona so the model uses our to-do / file tools
# instead of behaving like a code agent.
_SYSTEM_MESSAGE = (
    "You are a warm, concise personal assistant inside Microsoft Teams "
    "and Microsoft 365 Copilot. "
    "You help the user manage a simple to-do list, read files they have "
    "shared in the chat, and create documents for them. When the user asks "
    "you to DO something (add a task, mark it done, read a shared file, write "
    "or generate a document), you MUST use the matching tool rather than only "
    "describing how. "
    "To create or generate a file for the user, create it yourself using your "
    "shell and python tools in your workspace: write the text directly for "
    "text formats (.txt, .md, .csv, .json, .html, code), or for .docx, .pptx, "
    "and .pdf install the library you need at runtime (for example "
    "`pip install python-docx python-pptx reportlab`) and use it to build the "
    "file. Then call the deliver_file tool with the file's path to send it. "
    "Never say you have created or attached a file unless you actually created "
    "it and called deliver_file in this turn. You cannot generate images. "
    "When the user asks you to research something, plan something (a trip, an "
    "event, a launch), compare options, or write a report that needs current "
    "facts, do real research before answering: break the request into 5 to 8 "
    "specific questions, call search_web once per question (one at a time), "
    "then write a well-structured Word report (.docx via python-docx) with a "
    "short summary, sections for your findings, and a Sources section listing "
    "the URLs search_web returned. Deliver it with deliver_file, then reply "
    "with 3 to 5 bullet takeaways and the top sources. Don't ask clarifying "
    "questions first: make reasonable assumptions and state them in the "
    "report. Cite only URLs that search_web returned; never invent sources. "
    "Prefer short, friendly replies. If you are unsure, ask a brief "
    "clarifying question."
)

_ENDPOINT = os.environ.get("FOUNDRY_PROJECT_ENDPOINT", "")
_MODEL = os.environ.get("AZURE_AI_MODEL_DEPLOYMENT_NAME", "")
# Turn watchdog. The Copilot runtime retries failed model calls on its own and
# keeps emitting bookkeeping events while it waits, so "time since the last
# event" never trips. Instead, measure time since the last sign of real progress
# (a finished model call, a tool starting or finishing, reply text).
# - send: how long dispatching the message to the runtime may take.
# - stall: longest gap without real progress. Research turns can be quiet for a
#   few minutes (package installs, building a report), hence 5 minutes.
# - max: hard cap for a whole turn, including the one automatic retry.
_SEND_TIMEOUT_SECONDS = int(os.environ.get("TURN_SEND_TIMEOUT_SECONDS", "60"))
_STALL_TIMEOUT_SECONDS = int(os.environ.get("TURN_STALL_TIMEOUT_SECONDS", "300"))
_TURN_MAX_SECONDS = int(os.environ.get("TURN_MAX_SECONDS", "1500"))

_credential = DefaultAzureCredential()
_client: CopilotClient | None = None
# One Copilot SDK session per Teams/Copilot conversation, keyed by conversation id
_sessions: dict[str, tuple[object, str]] = {}
# Serialize turns per conversation: a Copilot SDK session processes one turn at
# a time, and channels like M365 Copilot can fire activities in bursts. Without
# this, overlapping sends deadlock on "waiting for idle". Per conversation, so a
# long research turn in one chat never blocks another chat.
_turn_locks: dict[str, asyncio.Lock] = {}

# Events that show the turn is actually moving. Everything else (usage info,
# debug logs, a model call starting or failing and being retried) is bookkeeping.
_PROGRESS_EVENTS = {
    SessionEventType.MODEL_CALL_FINISHED,
    SessionEventType.TOOL_EXECUTION_START,
    SessionEventType.TOOL_EXECUTION_PROGRESS,
    SessionEventType.TOOL_EXECUTION_COMPLETE,
    SessionEventType.EXTERNAL_TOOL_COMPLETED,
    SessionEventType.ASSISTANT_MESSAGE_DELTA,
    SessionEventType.ASSISTANT_STREAMING_DELTA,
    SessionEventType.ASSISTANT_REASONING_DELTA,
    SessionEventType.ASSISTANT_MESSAGE,
}
# Events worth a log line, so a stuck turn can be diagnosed from container logs.
_LOGGED_EVENTS = {
    SessionEventType.MODEL_CALL_START,
    SessionEventType.MODEL_CALL_FINISHED,
    SessionEventType.TOOL_EXECUTION_START,
    SessionEventType.TOOL_EXECUTION_COMPLETE,
    SessionEventType.SESSION_IDLE,
}
_LOGGED_DETAIL_EVENTS = {
    SessionEventType.MODEL_CALL_FAILURE,
    SessionEventType.ASSISTANT_TURN_RETRY,
    SessionEventType.SESSION_WARNING,
    SessionEventType.SESSION_ERROR,
    SessionEventType.ABORT,
}


def _fresh_token() -> str:
    return _credential.get_token("https://ai.azure.com/.default").token


def _sdk_session_id(conversation_id: str) -> str:
    """Derive a stable, short SDK session id from the conversation.

    The SDK session id doubles as the provider ``prompt_cache_key`` which has a
    64-char limit, so hash the (long) Teams/Copilot conversation id to a compact,
    stable token instead of embedding it raw.
    """
    if not conversation_id:
        return f"conv-{uuid.uuid4().hex}"
    digest = hashlib.sha256(conversation_id.encode("utf-8")).hexdigest()[:32]
    return f"conv-{digest}"


async def _get_session(conversation_id: str):
    """Create/reuse a per-conversation session, refreshing on token change."""
    global _client
    if not _ENDPOINT or not _MODEL:
        raise RuntimeError(
            "FOUNDRY_PROJECT_ENDPOINT and AZURE_AI_MODEL_DEPLOYMENT_NAME must be set."
        )
    if _client is None:
        _client = CopilotClient()
        await _client.start()

    token = await asyncio.to_thread(_fresh_token)
    # Reuse the conversation's session unless the bearer token rotated, so a
    # durable session never keeps calling the model with an expired token.
    cached = _sessions.get(conversation_id)
    if cached is not None and cached[1] == token:
        return cached[0]

    sid = _sdk_session_id(conversation_id)
    opts = dict(
        provider=ProviderConfig(
            type="azure",
            base_url=_ENDPOINT,
            wire_api="responses",
            bearer_token=token,
        ),
        model=_MODEL,
        tools=tools.build_tools(conversation_id),
        available_tools=ToolSet().add_builtin("*").add_custom("*"),
        system_message={"mode": "replace", "content": _SYSTEM_MESSAGE},
        on_permission_request=PermissionHandler.approve_all,
        streaming=True,
    )
    try:
        session = await _client.resume_session(sid, **opts)
        logger.info("Resumed Copilot session %s", sid)
    except Exception:  # pylint: disable=broad-exception-caught
        session = await _client.create_session(session_id=sid, **opts)
        logger.info("Created Copilot session %s", sid)
    _sessions[conversation_id] = (session, token)
    return session


async def _reset_session(conversation_id: str) -> None:
    """Drop a conversation's cached session so the next turn rebuilds it clean."""
    entry = _sessions.pop(conversation_id, None)
    if entry is not None:
        try:
            await asyncio.wait_for(entry[0].abort(), timeout=10)
        except Exception:  # pylint: disable=broad-exception-caught
            pass


async def _restart_runtime() -> None:
    """Stop the Copilot runtime process; the next turn starts a fresh one.

    Used when the runtime stops responding (a send that never returns, or a turn
    with no progress). Conversation history survives: sessions are resumed by id.
    """
    global _client
    for conversation_id in list(_sessions):
        await _reset_session(conversation_id)
    client, _client = _client, None
    if client is None:
        return
    try:
        await asyncio.wait_for(client.stop(), timeout=15)
    except Exception:  # pylint: disable=broad-exception-caught
        try:
            await asyncio.wait_for(client.force_stop(), timeout=10)
        except Exception:  # pylint: disable=broad-exception-caught
            pass
    logger.warning("Copilot runtime restarted")


# Friendly progress labels for the built-in / custom tools, shown to the user as
# transient "informative updates" while the model works (they vanish on the final
# streamed reply).
_TOOL_LABELS = {
    "add_task": "Adding your task…",
    "list_tasks": "Looking up your tasks…",
    "complete_task": "Marking the task done…",
    "search_web": "Searching the web…",
    # built-in file tools the model uses to read shared files
    "view": "Reading the file…",
    "read_file": "Reading the file…",
    "bash": "Working with the file…",
    "grep": "Searching the file…",
    "glob": "Looking through the files…",
    "str_replace": "Editing the file…",
}


def _tool_label(name: str) -> str:
    return _TOOL_LABELS.get(name, f"Using {name.replace('_', ' ')}…")


def _build_attachments(files: list[dict[str, str]] | None) -> list[dict]:
    attachments = []
    for f in (files or []):
        path = f.get("path")
        if not path:
            continue
        if f.get("kind") == "image" and f.get("mime"):
            # Inline image → base64 blob so the model can see it (vision).
            try:
                import base64
                with open(path, "rb") as fh:
                    data = base64.b64encode(fh.read()).decode("ascii")
                attachments.append({
                    "type": "blob",
                    "data": data,
                    "mimeType": f["mime"],
                    "displayName": f.get("name", ""),
                })
            except Exception as ex:  # pylint: disable=broad-exception-caught
                logger.warning("could not encode image %s: %s", path, ex)
        else:
            attachments.append({"type": "file", "path": path, "displayName": f.get("name", "")})
    return attachments


def _event_detail(data) -> str:
    detail = getattr(data, "__dict__", data)
    return str(detail)[:800]


async def _run_turn(conversation_id: str, text: str, attachments: list[dict], deadline: float, outcome: dict):
    """Run one attempt of a turn, yielding ``(kind, text)`` like ask_stream.

    Sets ``outcome["stalled"]`` when the runtime stopped making progress, and
    ``outcome["got_text"]`` once any reply text has been streamed.
    """
    loop = asyncio.get_running_loop()
    started = loop.time()
    try:
        session = await asyncio.wait_for(_get_session(conversation_id), timeout=_SEND_TIMEOUT_SECONDS)
    except asyncio.TimeoutError:
        logger.warning("turn %s: opening the Copilot session timed out", conversation_id[-12:])
        outcome["stalled"] = True
        return
    except Exception as ex:  # pylint: disable=broad-exception-caught
        logger.error("turn %s: session setup failed: %s", conversation_id[-12:], ex, exc_info=True)
        outcome["got_text"] = True
        yield ("final", f"Sorry, something went wrong: {ex}")
        return

    queue: asyncio.Queue = asyncio.Queue()

    def _on_event(ev):
        # May be invoked from the SDK's reader thread; hop to our loop safely.
        loop.call_soon_threadsafe(queue.put_nowait, ev)

    unsubscribe = session.on(_on_event)
    final_text = ""
    try:
        try:
            message_id = await asyncio.wait_for(
                session.send(text, attachments=attachments or None), timeout=_SEND_TIMEOUT_SECONDS
            )
        except asyncio.TimeoutError:
            logger.warning("turn %s: sending the message to the Copilot runtime timed out", conversation_id[-12:])
            outcome["stalled"] = True
            return
        logger.info("turn %s: message dispatched (%s)", conversation_id[-12:], message_id)

        last_progress = loop.time()
        while True:
            now = loop.time()
            wait = min(last_progress + _STALL_TIMEOUT_SECONDS, deadline) - now
            if wait <= 0:
                logger.warning(
                    "turn %s: %s (no progress for %ss, attempt running %ss); abandoning this attempt",
                    conversation_id[-12:],
                    "turn time limit reached" if now >= deadline else "stalled",
                    int(now - last_progress), int(now - started),
                )
                outcome["stalled"] = True
                return
            try:
                ev = await asyncio.wait_for(queue.get(), timeout=wait)
            except asyncio.TimeoutError:
                continue
            etype = ev.type
            data = ev.data
            if etype in _PROGRESS_EVENTS:
                last_progress = loop.time()
            if etype in _LOGGED_EVENTS:
                tool = getattr(data, "tool_name", "") or ""
                logger.info("turn %s: %s %s", conversation_id[-12:], etype.value, tool)
            elif etype in _LOGGED_DETAIL_EVENTS:
                logger.warning("turn %s: %s %s", conversation_id[-12:], etype.value, _event_detail(data))

            if etype == SessionEventType.TOOL_EXECUTION_START:
                yield ("progress", _tool_label(getattr(data, "tool_name", "") or ""))
            elif etype == SessionEventType.ASSISTANT_MESSAGE_DELTA:
                chunk = getattr(data, "delta_content", "") or ""
                if chunk:
                    outcome["got_text"] = True
                    yield ("delta", chunk)
            elif etype == SessionEventType.ASSISTANT_MESSAGE:
                final_text = getattr(data, "content", "") or final_text
            elif etype in (SessionEventType.SESSION_IDLE, SessionEventType.ASSISTANT_IDLE):
                break
            elif etype == SessionEventType.SESSION_ERROR:
                if not outcome["got_text"]:
                    outcome["got_text"] = True
                    yield ("final", "Sorry, I hit a problem answering that.")
                return
    except Exception as ex:  # pylint: disable=broad-exception-caught
        logger.error("turn %s failed: %s", conversation_id[-12:], ex, exc_info=True)
        await _reset_session(conversation_id)
        if not outcome["got_text"]:
            outcome["got_text"] = True
            yield ("final", f"Sorry, something went wrong: {ex}")
        return
    finally:
        try:
            unsubscribe()
        except Exception:  # pylint: disable=broad-exception-caught
            pass

    logger.info("turn %s: finished in %ss", conversation_id[-12:], int(loop.time() - started))
    if not outcome["got_text"]:
        outcome["got_text"] = True
        yield ("final", final_text.strip() or "(no response)")


async def ask_stream(conversation_id: str, text: str, files: list[dict[str, str]] | None = None):
    """Drive one turn and yield ``(kind, text)`` tuples as the model works.

    ``files`` is an optional list of ``{name, path}`` raw files to hand to the
    model as attachments — it reads/analyzes them itself (any type).

    ``kind`` is one of:
      - ``"progress"`` — a transient status line (tool activity); show + replace.
      - ``"delta"``    — an incremental chunk of the assistant's reply text.
      - ``"final"``    — the whole reply (only emitted when no deltas streamed,
                         e.g. an error string).

    If the Copilot runtime stops making progress before any reply text has
    streamed, the runtime is restarted and the turn is retried once.
    """
    lock = _turn_locks.setdefault(conversation_id, asyncio.Lock())
    async with lock:  # one turn at a time per conversation
        deadline = asyncio.get_running_loop().time() + _TURN_MAX_SECONDS
        attachments = _build_attachments(files)
        for attempt in (1, 2):
            outcome = {"stalled": False, "got_text": False}
            async for item in _run_turn(conversation_id, text, attachments, deadline, outcome):
                yield item
            if not outcome["stalled"]:
                return
            await _restart_runtime()
            if outcome["got_text"]:
                yield ("delta", "\n\n(I had to stop before finishing. Please ask again.)")
                return
            if attempt == 2:
                break
            logger.warning("turn %s: retrying on a fresh Copilot runtime", conversation_id[-12:])
            yield ("progress", "Still working on it…")
        if not outcome["got_text"]:
            yield ("final", "Sorry, that took too long and I had to stop. Please try again.")
