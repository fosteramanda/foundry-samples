# Copyright (c) Microsoft. All rights reserved.

"""Hybrid LangGraph Responses agent for Foundry and on-premises hosts."""

from __future__ import annotations

import asyncio
import logging
import os
from pathlib import Path

from azure.ai.agentserver.core import AgentConfig
from dotenv import load_dotenv
from langchain.agents import create_agent
from langchain_azure_ai.agents.hosting import ResponsesHostServer
from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver
from langgraph.store.sqlite import AsyncSqliteStore
from langmem import create_manage_memory_tool, create_search_memory_tool
from model import build_chat_model
from sqlite_conversation_chain_store import SqliteConversationChainStore
from sqlite_response_store import SqliteResponseStore

load_dotenv()

tools = [
    create_manage_memory_tool(
        namespace="memories",
        instructions="Save, update, or delete facts only when the user asks.",
    ),
    create_search_memory_tool(
        namespace="memories",
        instructions=(
            "Retrieve saved facts when the user asks what you remember. "
            "This sample has no vector index; results are not similarity-ranked."
        ),
    ),
]


async def main() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    state_root = Path.home() if AgentConfig.from_env().is_hosted else Path.cwd()
    async with (
        SqliteConversationChainStore(
            state_root / "conversation_chains.sqlite"
        ) as conversation_chain_store,
        AsyncSqliteSaver.from_conn_string(
            str(state_root / "checkpoints.sqlite")
        ) as checkpointer,
        AsyncSqliteStore.from_conn_string(
            str(state_root / "memories.sqlite")
        ) as memory_store,
        SqliteResponseStore(state_root / "responses.sqlite") as response_store,
    ):
        await memory_store.setup()
        graph = create_agent(
            build_chat_model(),
            tools=tools,
            system_prompt=(
                "Use manage_memory only when the user asks to remember, update, "
                "or forget a fact. Use search_memory when asked about saved facts."
            ),
            checkpointer=checkpointer,
            store=memory_store,
        )
        server = ResponsesHostServer(
            graph,
            store=response_store,
            conversation_chain_store=conversation_chain_store,
        )
        await server.run_async(port=int(os.environ.get("PORT", "8088")))


if __name__ == "__main__":
    asyncio.run(main())