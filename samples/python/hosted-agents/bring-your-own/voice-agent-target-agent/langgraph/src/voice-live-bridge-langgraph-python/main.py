# Copyright (c) Microsoft. All rights reserved.

"""Entry point for the LangGraph Python Voice Live Bridge hosted agent."""

from __future__ import annotations

import logging
import os
from typing import Any

from azure.ai.agentserver.invocations.voice import VoiceAgentServerHost

from langgraph_agent import LangGraphAgentClient
from model_contract import StreamingModelClient
from voice_runtime import create_app as create_voice_app


def create_app(
    model_client: StreamingModelClient | None = None,
    **host_options: Any,
) -> VoiceAgentServerHost:
    """Create the Voice Live Bridge application."""
    return create_voice_app(
        model_client or LangGraphAgentClient.from_environment(),
        **host_options,
    )


def main() -> None:
    """Run the hosted-agent server."""
    logging.basicConfig(level=os.getenv("LOG_LEVEL", "INFO").upper())
    create_app().run()


if __name__ == "__main__":
    main()