"""
Factory that builds a ResponsesApiAgentLogicService for a turn.

Discovers MCP servers either from the Agent365 API or from a local
ToolingManifest.json, controlled by the "McpDiscoverySource" configuration
setting ("API" or "Manifest"). Mirrors ResponsesApiAgentLogicServiceFactory.cs.
"""

from __future__ import annotations

import json
import logging
import os
from pathlib import Path
from typing import Any

import httpx
from microsoft_agents.hosting.core import TurnContext

from ...models import AgentMetadata, McpServerConfig, ToolingManifest
from ...services import AgentTokenCredential, AgentTokenHelper
from .responses_api_agent_logic_service import ResponsesApiAgentLogicService

logger = logging.getLogger(__name__)

# Scope used for the MCP servers (matches the audience in ToolingManifest.json).
_MCP_SCOPE = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1/.default"


class ResponsesApiAgentLogicServiceFactory:
    def __init__(self, configuration: dict[str, Any], token_helper: AgentTokenHelper):
        self._configuration = configuration
        self._token_helper = token_helper

    async def create(
        self, agent: AgentMetadata, _turn_context: TurnContext
    ) -> ResponsesApiAgentLogicService:
        credential = AgentTokenCredential(self._token_helper, agent)
        access_token = await credential.get_token([_MCP_SCOPE])
        logger.info(
            "Acquired token for Responses API MCP tools. Expires at: %s",
            access_token.expires_on,
        )

        mcp_servers = await self._get_mcp_servers(agent.agent_id, access_token.token)

        return ResponsesApiAgentLogicService(
            agent=agent,
            configuration=self._configuration,
            access_token=access_token.token,
            mcp_servers=mcp_servers,
        )

    async def _get_mcp_servers(self, agent_instance_id, access_token: str) -> list[McpServerConfig]:
        source = (
            self._configuration.get("McpDiscoverySource")
            or os.getenv("McpDiscoverySource")
            or "API"
        )

        if source.lower() == "manifest":
            logger.info("Loading MCP servers from tooling_manifest.json")
            return self._load_from_manifest()

        logger.info(
            "Discovering MCP servers from API for agent %s", agent_instance_id
        )
        return await self._discover_from_api(agent_instance_id, access_token)

    def _load_from_manifest(self) -> list[McpServerConfig]:
        # The Dockerfile copies the manifest next to the entry script; for local
        # runs we look one directory up to find it relative to the package root.
        candidates = [
            Path(__file__).resolve().parent.parent.parent.parent / "tooling_manifest.json",
            Path.cwd() / "tooling_manifest.json",
        ]
        for path in candidates:
            if path.exists():
                manifest = ToolingManifest.from_dict(
                    json.loads(path.read_text(encoding="utf-8"))
                )
                logger.info(
                    "Loaded %d MCP servers from %s", len(manifest.mcp_servers), path
                )
                return manifest.mcp_servers

        logger.warning("tooling_manifest.json not found in %s", candidates)
        return []

    async def _discover_from_api(self, agent_instance_id, access_token: str) -> list[McpServerConfig]:
        url = (
            f"https://agent365.svc.cloud.microsoft/agents/v2/{agent_instance_id}/mcpServers"
        )
        logger.info("Discovering MCP servers from %s", url)

        async with httpx.AsyncClient(timeout=httpx.Timeout(30.0)) as client:
            response = await client.get(
                url, headers={"Authorization": f"Bearer {access_token}"}
            )

        if response.status_code >= 400:
            logger.error(
                "Failed to discover MCP servers. Status: %s, Response: %s",
                response.status_code,
                response.text,
            )
            return []

        servers = [McpServerConfig.from_dict(s) for s in response.json()]
        logger.info(
            "Discovered %d MCP servers for agent %s", len(servers), agent_instance_id
        )
        for server in servers:
            logger.info("  MCP Server: %s (%s)", server.mcp_server_name, server.url)
        return servers
