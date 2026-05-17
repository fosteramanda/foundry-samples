"""Agent metadata extracted from an incoming activity recipient."""

from dataclasses import dataclass
from uuid import UUID


@dataclass
class AgentMetadata:
    user_id: UUID
    agent_id: UUID
    agent_application_id: UUID
    tenant_id: UUID
    email_id: str = ""
    is_messaging_enabled: bool = False
