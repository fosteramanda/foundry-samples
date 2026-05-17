"""
Foundry A365 Agent - Python entry point.

Equivalent to Program.cs from the C# sample: stands up an aiohttp server that
hosts an AgentApplication and routes /api/messages to the agent adapter.
"""

from __future__ import annotations

import json
import logging
import os
from os import environ
from pathlib import Path
from typing import Any

from aiohttp.web import Application, Request, Response, run_app
from dotenv import load_dotenv
from microsoft_agents.activity import load_configuration_from_env
from microsoft_agents.authentication.msal import MsalConnectionManager
from microsoft_agents.hosting.aiohttp import (
    CloudAdapter,
    jwt_authorization_middleware,
    start_agent_process,
)
from microsoft_agents.hosting.core import (
    AgentApplication,
    Authorization,
    MemoryStorage,
    TurnState,
)

from .agent_logic import register_a365_agent_handlers
from .agent_logic.responses_api import ResponsesApiAgentLogicServiceFactory
from .services import AgentTokenHelper

logger = logging.getLogger(__name__)


def _configure_logging() -> None:
    logging.basicConfig(level=logging.INFO)
    # Match the appsettings.json defaults.
    logging.getLogger("microsoft_agents.authentication.msal").setLevel(logging.WARNING)
    logging.getLogger("msal").setLevel(logging.WARNING)


def _load_app_settings() -> dict[str, Any]:
    """Load appsettings.json next to this module if present, then overlay env vars.

    Only a few keys are surfaced through configuration (AzureOpenAIEndpoint,
    ModelDeployment, McpDiscoverySource); the rest are consumed directly by
    the Microsoft Agents SDK via load_configuration_from_env.
    """
    settings: dict[str, Any] = {}
    settings_path = Path(__file__).resolve().parent.parent / "appsettings.json"
    if settings_path.exists():
        try:
            settings = json.loads(settings_path.read_text(encoding="utf-8"))
        except Exception:
            logger.exception("Failed to parse appsettings.json at %s", settings_path)

    # Environment variables override file values to match the Docker contract.
    for key in ("AzureOpenAIEndpoint", "ModelDeployment", "McpDiscoverySource"):
        value = os.getenv(key)
        if value:
            settings[key] = value

    return settings


def _create_app() -> Application:
    load_dotenv(override=True)
    _configure_logging()

    app_settings = _load_app_settings()
    sdk_config = load_configuration_from_env(environ)

    # Core SDK plumbing — mirrors the AddAgent / AddAgentApplicationOptions / IStorage
    # registration in Program.cs.
    storage = MemoryStorage()
    connection_manager = MsalConnectionManager(**sdk_config)
    adapter = CloudAdapter(connection_manager=connection_manager)
    authorization = Authorization(storage, connection_manager, **sdk_config)

    agent_app = AgentApplication[TurnState](
        storage=storage,
        adapter=adapter,
        authorization=authorization,
        **sdk_config,
    )

    token_helper = AgentTokenHelper()
    factory = ResponsesApiAgentLogicServiceFactory(
        configuration=app_settings, token_helper=token_helper
    )
    register_a365_agent_handlers(agent_app, factory)

    async def messages(request: Request) -> Response:
        return await start_agent_process(request, agent_app, adapter)

    async def root(_request: Request) -> Response:
        return Response(text="Hello World from HelloWorldA365Agent!")

    aio_app = Application(middlewares=[jwt_authorization_middleware])
    aio_app["agent_app"] = agent_app
    aio_app["adapter"] = adapter

    aio_app.router.add_post("/api/messages", messages)
    aio_app.router.add_get("/", root)
    aio_app.router.add_get("/liveness", root)
    aio_app.router.add_get("/readiness", root)

    return aio_app


def main() -> None:
    app = _create_app()
    # Match the Dockerfile ASPNETCORE_URLS port choice (8088), but allow PORT to override.
    port = int(os.getenv("PORT", "8088"))
    host = "0.0.0.0"
    logger.warning("Application starting on %s:%s", host, port)
    run_app(app, host=host, port=port)


if __name__ == "__main__":
    main()
