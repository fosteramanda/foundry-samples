"""
Handler registration for incoming activities.

Mirrors A365AgentApplication.cs: routes message and notification activities
to the appropriate handler, resolves the AgentMetadata from the recipient,
and builds a per-turn AgentLogicService via the factory.
"""

from __future__ import annotations

import logging
import os
import traceback
from typing import Any
from uuid import UUID

from microsoft_agents.activity import Activity
from microsoft_agents.hosting.core import (
    AgentApplication,
    MessageFactory,
    TurnContext,
    TurnState,
)
from microsoft_agents_a365.notifications.agent_notification import (
    AgentNotification,
    AgentNotificationActivity,
    ChannelId,
    NotificationTypes,
)

from ..models import AgentMetadata
from .responses_api import ResponsesApiAgentLogicServiceFactory

logger = logging.getLogger(__name__)

# Type alias for clarity.
A365AgentApplication = AgentApplication


def register_a365_agent_handlers(
    agent_app: AgentApplication,
    factory: ResponsesApiAgentLogicServiceFactory,
    notification: AgentNotification | None = None,
) -> None:
    """Wire up handlers on an existing AgentApplication."""

    notification = notification or AgentNotification(agent_app)

    # ------------------------------------------------------------------
    # Notification handlers (email + Office document comments).
    # ------------------------------------------------------------------
    @notification.on_agent_notification(
        ChannelId(channel="email", sub_channel="*")
    )
    async def _on_email(context: TurnContext, state: TurnState, activity: AgentNotificationActivity):
        agent = _agent_metadata_from_activity(context.activity)
        service = await factory.create(agent, context)
        try:
            await service.handle_email_notification(context, state, activity)
        finally:
            await service.aclose()

    @notification.on_agent_notification(
        ChannelId(channel="wxp", sub_channel="word")
    )
    async def _on_word(context: TurnContext, state: TurnState, activity: AgentNotificationActivity):
        agent = _agent_metadata_from_activity(context.activity)
        service = await factory.create(agent, context)
        try:
            await service.handle_comment_notification(context, state, activity)
        finally:
            await service.aclose()

    @notification.on_agent_notification(
        ChannelId(channel="wxp", sub_channel="excel")
    )
    async def _on_excel(context: TurnContext, state: TurnState, activity: AgentNotificationActivity):
        agent = _agent_metadata_from_activity(context.activity)
        service = await factory.create(agent, context)
        try:
            await service.handle_comment_notification(context, state, activity)
        finally:
            await service.aclose()

    @notification.on_agent_notification(
        ChannelId(channel="wxp", sub_channel="powerpoint")
    )
    async def _on_powerpoint(context: TurnContext, state: TurnState, activity: AgentNotificationActivity):
        agent = _agent_metadata_from_activity(context.activity)
        service = await factory.create(agent, context)
        try:
            await service.handle_comment_notification(context, state, activity)
        finally:
            await service.aclose()

    # ------------------------------------------------------------------
    # Message activity.
    # ------------------------------------------------------------------
    @agent_app.activity("message")
    async def _on_message(context: TurnContext, state: TurnState):
        try:
            logger.info(
                "Received message activity: %s from %s",
                context.activity.id,
                getattr(context.activity.from_property, "id", None),
            )

            agent = _agent_metadata_from_activity(context.activity)
            service = await factory.create(agent, context)
            try:
                # Ignore all other channel IDs to prevent duplicate notifications
                # when messaging is enabled on the agent identity.
                if agent.is_messaging_enabled and context.activity.channel_id != "msteams":
                    return

                # Acknowledge the request so the user sees the agent is working
                # on it. (The C# sample uses StreamingResponse for this; in the
                # Python SDK we send a discrete message.)
                await context.send_activity(
                    MessageFactory.text("Working on your request...")
                )

                await service.new_activity_received(context, state)
            finally:
                await service.aclose()
        except Exception as ex:
            logger.exception("Error processing message activity")
            session_id = os.getenv("FOUNDRY_AGENT_SESSION_ID") or "(not set)"
            error_text = (
                "Sorry, something went wrong while processing your message.\n"
                f"FOUNDRY_AGENT_SESSION_ID: {session_id}\n"
                f"Exception:\n{traceback.format_exc()}"
            )
            try:
                await context.send_activity(MessageFactory.text(error_text))
            except Exception:
                logger.exception("Failed to send error response")

    # ------------------------------------------------------------------
    # Event + installation update.
    # ------------------------------------------------------------------
    @agent_app.activity("event")
    async def _on_event(context: TurnContext, state: TurnState):
        agent = _agent_metadata_from_activity(context.activity)
        service = await factory.create(agent, context)
        try:
            await service.new_activity_received(context, state)
        finally:
            await service.aclose()

    @agent_app.activity("installationUpdate")
    async def _on_installation_update(context: TurnContext, state: TurnState):
        agent = _agent_metadata_from_activity(context.activity)
        service = await factory.create(agent, context)
        try:
            if agent.is_messaging_enabled:
                notification_activity = AgentNotificationActivity(context.activity)
                await service.handle_installation_update(
                    context, state, notification_activity
                )
            else:
                await service.new_activity_received(context, state)
        finally:
            await service.aclose()


# ---------------------------------------------------------------------------
# Activity → AgentMetadata mapping.
# ---------------------------------------------------------------------------
_ZERO_UUID = UUID(int=0)


def _try_parse_uuid(value: Any) -> UUID:
    if not value:
        return _ZERO_UUID
    try:
        return UUID(str(value))
    except (ValueError, TypeError, AttributeError):
        return _ZERO_UUID


def _agent_metadata_from_activity(activity: Activity) -> AgentMetadata:
    if activity is None:
        raise ValueError("Activity cannot be None.")
    recipient = activity.recipient
    conversation = activity.conversation
    if recipient is None or conversation is None:
        raise ValueError("Activity must have a recipient and conversation.")

    tenant_id = _try_parse_uuid(getattr(recipient, "tenant_id", None))
    agent_id = _try_parse_uuid(getattr(recipient, "agentic_app_id", None))

    # Prefer the explicit AgenticUserId, falling back to the AAD object id.
    agentic_user_id = (
        getattr(recipient, "agentic_user_id", None)
        or getattr(recipient, "aad_object_id", None)
    )
    user_id = _try_parse_uuid(agentic_user_id)

    recipient_id = getattr(recipient, "id", "") or ""
    recipient_name = getattr(recipient, "name", "") or ""

    properties = getattr(recipient, "properties", None) or {}
    blueprint_id_raw = (
        properties.get("agenticAppBlueprintId") if isinstance(properties, dict) else None
    )
    if blueprint_id_raw:
        agent_application_id = _try_parse_uuid(blueprint_id_raw)
    else:
        agent_application_id = _try_parse_uuid(recipient_id)

    email_id = recipient_id if "@" in recipient_id else recipient_name

    return AgentMetadata(
        user_id=user_id,
        agent_id=agent_id,
        agent_application_id=agent_application_id,
        tenant_id=tenant_id,
        email_id=email_id,
    )
