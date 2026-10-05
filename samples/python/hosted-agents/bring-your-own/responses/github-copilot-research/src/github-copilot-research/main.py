# Copyright (c) Microsoft. All rights reserved.

"""Research assistant on the Responses protocol.

The same GitHub Copilot SDK agent as the Activity build (to-do list, reading
shared files, web research), but this code speaks only the Responses protocol.
It has no Teams or Microsoft 365 code. When the agent is published to Teams and
Microsoft 365 Copilot, Foundry turns on the Activity protocol for it and
translates between the two: Teams messages become Responses requests, and the
response stream becomes Teams messages.

Hosted by ``azure-ai-agentserver-responses`` for the Foundry platform contract.
"""

import asyncio
import base64
import logging
import os
import re
import time
from collections.abc import AsyncIterable
from pathlib import Path
from typing import Any

_LOG_LEVEL = os.environ.get("LOG_LEVEL", "INFO").upper()
logging.basicConfig(
    level=getattr(logging, _LOG_LEVEL, logging.INFO),
    format="%(asctime)s %(levelname)s %(name)s:%(lineno)d | %(message)s",
)
logger = logging.getLogger("github-copilot")

from azure.ai.agentserver.responses import (
    CreateResponse,
    ResponseContext,
    ResponseEventStream,
    ResponsesAgentServerHost,
    ResponsesServerOptions,
)

import client as copilot_client

# Shared files are saved under $HOME so the model can open them with its own
# file tools. $HOME persists across a hosted agent session's idle and resume.
_FILES_DIR = Path(os.environ.get("HOME") or ".") / ".github-copilot" / "files"
_IMAGE_EXT = {"image/png": ".png", "image/jpeg": ".jpg", "image/gif": ".gif", "image/webp": ".webp"}


def _field(obj: Any, name: str) -> Any:
    """Read a field from a generated model or a plain dict."""
    if obj is None:
        return None
    try:
        return obj[name]
    except (KeyError, TypeError, IndexError):
        return getattr(obj, name, None)


def _conversation_key(context: ResponseContext) -> str:
    """Pick a stable key so each conversation keeps its own Copilot session."""
    for value in (
        getattr(context, "conversation_id", None),
        getattr(context, "conversation_chain_id", None),
        getattr(context, "session_id", None),
        os.environ.get("FOUNDRY_AGENT_SESSION_ID"),
    ):
        if isinstance(value, str) and value:
            return value
    return context.response_id


async def _read_file_part(part: Any) -> tuple[bytes, str] | None:
    """Return (bytes, mime) for an input_file / input_image part, or None."""
    data_url = _field(part, "file_data") or _field(part, "image_url") or ""
    match = re.match(r"^data:([^;,]+)?(?:;[^,]*)?;base64,(.*)$", data_url, re.DOTALL)
    if match:
        return base64.b64decode(match.group(2)), (match.group(1) or "application/octet-stream").lower()
    url = _field(part, "file_url") or (data_url if data_url.startswith("http") else "")
    if not url:
        return None
    import httpx
    async with httpx.AsyncClient(timeout=60, follow_redirects=True) as http:
        resp = await http.get(url)
        resp.raise_for_status()
        return resp.content, (resp.headers.get("content-type") or "application/octet-stream").split(";")[0].lower()


async def _save_input_files(context: ResponseContext, conversation_id: str) -> list[dict[str, str]]:
    """Save files and images the user shared, for the model to read itself."""
    saved: list[dict[str, str]] = []
    dest = _FILES_DIR / (re.sub(r"[^A-Za-z0-9_-]", "_", conversation_id)[-64:] or "default")
    for item in await context.get_input_items():
        content = _field(item, "content")
        if not isinstance(content, list):
            continue
        for part in content:
            ptype = _field(part, "type")
            if ptype not in ("input_file", "input_image"):
                continue
            try:
                got = await _read_file_part(part)
            except Exception as exc:  # pylint: disable=broad-exception-caught
                logger.warning("could not read shared %s: %s", ptype, exc)
                continue
            if got is None:
                continue
            data, mime = got
            is_image = ptype == "input_image" or mime.startswith("image/")
            name = _field(part, "filename") or f"image_{len(saved) + 1}{_IMAGE_EXT.get(mime, '.img')}"
            name = re.sub(r"[\\/]", "_", name)
            dest.mkdir(parents=True, exist_ok=True)
            path = dest / name
            path.write_bytes(data)
            entry = {"name": name, "path": str(path), "kind": "image" if is_image else "file"}
            if is_image:
                entry["mime"] = mime
            saved.append(entry)
    return saved


app = ResponsesAgentServerHost(options=ResponsesServerOptions(default_fetch_history_count=20))


@app.response_handler
async def handle_response(
    request: CreateResponse,
    context: ResponseContext,
    cancellation_signal: asyncio.Event,
) -> AsyncIterable[dict[str, Any]]:
    """One chat turn: fold in shared files, then stream the Copilot SDK's answer.

    Reply text streams as an output message. Progress (which tool is running)
    streams as reasoning summaries, the Responses protocol's place for "what the
    agent is doing", so it never ends up inside the reply text.
    """
    conversation_id = _conversation_key(context)
    stream = ResponseEventStream(response_id=context.response_id, request=request)
    yield stream.emit_created()
    yield stream.emit_in_progress()

    user_text = (await context.get_input_text() or "").strip()
    shared_files = await _save_input_files(context, conversation_id)
    if shared_files:
        names = ", ".join(f["name"] for f in shared_files)
        prompt = user_text or f"Please read the shared file(s) ({names}) and give me the key insights and a short summary."
    else:
        prompt = user_text or "Hello!"

    started = time.monotonic()
    logger.info("turn started | conversation=%s | chars=%d | files=%d", conversation_id[-12:], len(prompt), len(shared_files))

    queue: asyncio.Queue = asyncio.Queue()

    async def _pump() -> None:
        try:
            async for item in copilot_client.ask_stream(conversation_id, prompt, shared_files):
                await queue.put(item)
        except Exception as exc:  # pylint: disable=broad-exception-caught
            logger.error("turn failed: %s", exc, exc_info=True)
            await queue.put(("final", f"Sorry, something went wrong: {exc}"))
        finally:
            await queue.put(None)

    pump = asyncio.create_task(_pump())
    message = None
    text_part = None
    message_text: list[str] = []
    wrote_text = False

    def _close_message():
        nonlocal message, text_part, message_text
        events = []
        if message is not None:
            events = [
                text_part.emit_text_done("".join(message_text)),
                text_part.emit_done(),
                message.emit_done(),
            ]
        message, text_part, message_text = None, None, []
        return events

    try:
        while True:
            if cancellation_signal.is_set():
                logger.info("turn cancelled by caller | conversation=%s", conversation_id[-12:])
                pump.cancel()
                await copilot_client.abort_turn(conversation_id)
                for event in _close_message():
                    yield event
                yield stream.emit_incomplete(reason="cancelled")
                return
            try:
                item = await asyncio.wait_for(queue.get(), timeout=0.5)
            except asyncio.TimeoutError:
                continue
            if item is None:
                break
            kind, chunk = item
            if not chunk:
                continue
            if kind == "progress":
                for event in _close_message():
                    yield event
                reasoning = stream.add_output_item_reasoning_item()
                yield reasoning.emit_added()
                summary = reasoning.add_summary_part()
                yield summary.emit_added()
                yield summary.emit_text_delta(chunk)
                yield summary.emit_text_done(chunk)
                yield summary.emit_done()
                yield reasoning.emit_done()
            elif kind in ("delta", "final"):
                if message is None:
                    message = stream.add_output_item_message()
                    yield message.emit_added()
                    text_part = message.add_text_content()
                    yield text_part.emit_added()
                message_text.append(chunk)
                wrote_text = True
                yield text_part.emit_delta(chunk)

        if not wrote_text:
            message = stream.add_output_item_message()
            yield message.emit_added()
            text_part = message.add_text_content()
            yield text_part.emit_added()
            message_text.append("(no response)")
            yield text_part.emit_delta("(no response)")
        for event in _close_message():
            yield event
        yield stream.emit_completed()
    finally:
        if not pump.done():
            pump.cancel()
        logger.info("turn ended | conversation=%s | seconds=%d", conversation_id[-12:], int(time.monotonic() - started))


if __name__ == "__main__":
    logger.info("Starting the GitHub Copilot SDK research agent (Responses) ...")
    app.run()
