# Copyright (c) Microsoft. All rights reserved.
"""Custom tools the Copilot SDK can call: a per-conversation to-do list and web research.

Same tools as the Activity build of this agent, minus everything that needs a
channel-specific UI (the Adaptive Card task board and file downloads): this agent
speaks only the Responses protocol, so every result goes back to the user as text.

Tasks are persisted to a small JSON file under ``$HOME`` so they survive sandbox
idle/recycle. Foundry hosted agents give each session a **persistent ``$HOME``**.
See https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents#session-storage
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import re
import uuid
from pathlib import Path
from typing import Any

from copilot import Tool, define_tool
from pydantic import BaseModel, Field

logger = logging.getLogger("github-copilot.tools")

# Persist under $HOME (durable across idle/recycle); fall back to cwd for local runs.
_HOME = os.environ.get("HOME") or "."
_STORE_DIR = Path(_HOME) / ".github-copilot" / "tasks"


class AddTaskParams(BaseModel):
    title: str = Field(description="The task description.")


class CompleteTaskParams(BaseModel):
    task_id: str = Field(description="The id of the task to mark done.")


class SearchWebParams(BaseModel):
    query: str = Field(description="One specific research question or search query.")


class _NoParams(BaseModel):
    pass


# Real web research: Foundry's built-in web_search tool (Grounding with Bing),
# called through the project's OpenAI-compatible Responses endpoint with the
# hosted agent's managed identity. Returns grounded text plus source URLs.
# Searches run on their own deployment (gpt-4o by default) so they don't eat
# the chat model's rate limit.
_PROJECT_ENDPOINT = os.environ.get("FOUNDRY_PROJECT_ENDPOINT", "").rstrip("/")
_SEARCH_MODEL = os.environ.get("SEARCH_MODEL_DEPLOYMENT_NAME", "gpt-4o")
_search_client = None


def _get_search_client():
    global _search_client
    if _search_client is None:
        from azure.identity import DefaultAzureCredential, get_bearer_token_provider
        from openai import OpenAI
        token_provider = get_bearer_token_provider(DefaultAzureCredential(), "https://ai.azure.com/.default")
        # Retries back off on 429s, pacing a burst of searches under the
        # deployment's tokens-per-minute limit.
        _search_client = OpenAI(base_url=f"{_PROJECT_ENDPOINT}/openai/v1/", api_key=token_provider,
                                max_retries=6)
    return _search_client


def _search_web_sync(query: str) -> str:
    response = _get_search_client().responses.create(
        model=_SEARCH_MODEL,
        tools=[{"type": "web_search"}],
        tool_choice="required",
        instructions=("You are a research assistant. Use web search to answer with "
                      "specific, current facts and cite sources."),
        input=query,
    )
    sources: list[str] = []
    for item in response.output:
        if getattr(item, "type", "") != "message":
            continue
        for part in getattr(item, "content", None) or []:
            for ann in getattr(part, "annotations", None) or []:
                if getattr(ann, "type", "") == "url_citation":
                    entry = f"- {getattr(ann, 'title', '') or 'Source'}: {ann.url}"
                    if entry not in sources:
                        sources.append(entry)
    text = (response.output_text or "").strip()[:4000]
    if not sources:
        return text + "\n\n(No web sources were returned for this query; do not cite it.)"
    return text + "\n\nSources:\n" + "\n".join(sources[:10])


def _task_file(conversation_id: str) -> Path:
    # Sanitize the conversation id so it is safe to use as a file name.
    safe = re.sub(r"[^A-Za-z0-9_-]", "_", conversation_id) or "default"
    return _STORE_DIR / f"{safe}.json"


def _load_tasks(conversation_id: str) -> list[dict[str, Any]]:
    path = _task_file(conversation_id)
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return []
    except ValueError as exc:
        logger.warning("task store corrupt (%s) -> []", exc)
        return []


def _save_tasks(conversation_id: str, tasks: list[dict[str, Any]]) -> None:
    path = _task_file(conversation_id)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(tasks), encoding="utf-8")


def build_tools(conversation_id: str) -> list[Tool]:
    """Return the tool set bound to ``conversation_id`` (file-backed)."""

    def _add_task(params: AddTaskParams, _inv: Any) -> str:
        title = (params.title or "").strip()
        tasks = _load_tasks(conversation_id)
        for t in tasks:  # idempotent: don't duplicate an open task
            if not t["done"] and t["title"].casefold() == title.casefold():
                return f"Task '{title}' is already on the list."
        task = {"id": uuid.uuid4().hex[:8], "title": title, "done": False}
        tasks.append(task)
        _save_tasks(conversation_id, tasks)
        return f"Added task '{title}' (id {task['id']})."

    def _list_tasks(_params: _NoParams, _inv: Any) -> str:
        tasks = _load_tasks(conversation_id)
        if not tasks:
            return "The to-do list is empty."
        lines = [f"- [{'x' if t['done'] else ' '}] {t['title']} (id {t['id']})" for t in tasks]
        return "Current tasks:\n" + "\n".join(lines)

    def _complete_task(params: CompleteTaskParams, _inv: Any) -> str:
        tasks = _load_tasks(conversation_id)
        for t in tasks:
            if t["id"] == params.task_id:
                t["done"] = True
                _save_tasks(conversation_id, tasks)
                return f"Marked '{t['title']}' as done."
        return f"No task with id '{params.task_id}'."

    async def _search_web(params: SearchWebParams, _inv: Any) -> str:
        # Off the event loop: the Copilot SDK awaits tool handlers on the
        # agent's loop, and a blocking HTTP call would stall the response stream.
        query = (params.query or "").strip()
        if not query:
            return "Provide a specific search query."
        logger.info("search_web: %s", query)
        try:
            return await asyncio.to_thread(_search_web_sync, query)
        except Exception as exc:  # pylint: disable=broad-exception-caught
            logger.error("search_web failed: %s", exc, exc_info=True)
            return f"Web search failed ({type(exc).__name__}). Try a different query."

    return [
        define_tool("add_task", description="Add a task / to-do item.",
                    handler=_add_task, params_type=AddTaskParams),
        define_tool("list_tasks",
                    description="Show the user's to-do list. Use this whenever the "
                                "user wants to see, list, or review their tasks.",
                    handler=_list_tasks, params_type=_NoParams),
        define_tool("complete_task", description="Mark a task as done by its id.",
                    handler=_complete_task, params_type=CompleteTaskParams),
        define_tool("search_web",
                    description="Search the public web for current, cited facts. "
                                "Use one specific question per call. Returns an "
                                "answer plus source URLs; cite only those URLs.",
                    handler=_search_web, params_type=SearchWebParams),
    ]
