"""MCP server configuration models, matching the C# ToolingManifest schema."""

from dataclasses import dataclass, field
from typing import Any


@dataclass
class McpServerConfig:
    mcp_server_name: str = ""
    id: str = ""
    url: str = ""
    scope: str = ""
    audience: str = ""
    publisher: str = ""

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "McpServerConfig":
        return cls(
            mcp_server_name=data.get("mcpServerName", ""),
            id=data.get("id", ""),
            url=data.get("url", ""),
            scope=data.get("scope", ""),
            audience=data.get("audience", ""),
            publisher=data.get("publisher", ""),
        )


@dataclass
class ToolingManifest:
    mcp_servers: list[McpServerConfig] = field(default_factory=list)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "ToolingManifest":
        return cls(
            mcp_servers=[McpServerConfig.from_dict(s) for s in data.get("mcpServers", [])]
        )
