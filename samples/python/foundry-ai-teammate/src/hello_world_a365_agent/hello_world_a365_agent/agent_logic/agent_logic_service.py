"""
Protocol describing the per-activity logic for an A365 agent.
Mirrors IAgentLogicService.cs.
"""

from typing import Any, Protocol

from microsoft_agents.hosting.core import TurnContext, TurnState


class AgentLogicService(Protocol):
    """Per-turn agent logic. Concrete implementations may use any backend."""

    async def new_activity_received(
        self, turn_context: TurnContext, turn_state: TurnState
    ) -> None: ...

    async def new_email_received(
        self, from_email: str, subject: str, message_body: str
    ) -> str: ...

    async def new_chat_received(
        self, chat_id: str, from_user: str, message_body: str
    ) -> str: ...

    async def handle_email_notification(
        self,
        turn_context: TurnContext,
        turn_state: TurnState,
        notification_activity: Any,
    ) -> None: ...

    async def handle_comment_notification(
        self,
        turn_context: TurnContext,
        turn_state: TurnState,
        notification_activity: Any,
    ) -> None: ...

    async def handle_teams_message(
        self,
        turn_context: TurnContext,
        turn_state: TurnState,
        notification_activity: Any,
    ) -> None: ...

    async def handle_installation_update(
        self,
        turn_context: TurnContext,
        turn_state: TurnState,
        notification_activity: Any,
    ) -> None: ...
