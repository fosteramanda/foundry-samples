"""
OpenAI Responses API-based implementation of the agent logic service.

Translates ResponsesApiAgentLogicService.cs: invokes the Azure OpenAI
Responses API directly, declares MCP servers as native tools, and persists
``previous_response_id`` per-conversation for continuity.
"""

from __future__ import annotations

import base64
import json
import logging
import os
from pathlib import Path
from typing import Any

import httpx
from azure.identity.aio import DefaultAzureCredential
from microsoft_agents.activity import Activity
from microsoft_agents.hosting.core import MessageFactory, TurnContext, TurnState
from microsoft_agents_a365.notifications import EmailResponse

from ...models import AgentMetadata, McpServerConfig
from ..agent_instructions import get_instructions

logger = logging.getLogger(__name__)

# Azure OpenAI cognitive services scope used when falling back to managed-
# identity auth on the Responses API call.
_COGNITIVE_SERVICES_SCOPE = "https://cognitiveservices.azure.com/.default"


class ResponsesApiAgentLogicService:
    def __init__(
        self,
        agent: AgentMetadata,
        configuration: dict[str, Any],
        access_token: str,
        mcp_servers: list[McpServerConfig],
    ):
        self._agent = agent
        self._configuration = configuration
        self._access_token = access_token
        self._mcp_servers = mcp_servers
        self._http = httpx.AsyncClient(timeout=httpx.Timeout(120.0))

    async def aclose(self) -> None:
        await self._http.aclose()

    # ------------------------------------------------------------------
    # Main entry point used by the message handler.
    # ------------------------------------------------------------------
    async def new_activity_received(
        self, turn_context: TurnContext, _turn_state: TurnState
    ) -> None:
        activity = turn_context.activity
        incoming_text = activity.text or ""
        logger.info("New activity received (Responses API): %s", incoming_text)

        sender = activity.from_property
        channel_id = activity.channel_id

        if channel_id in ("email", "agents:email"):
            subject = ""
            channel_data = getattr(activity, "channel_data", None)
            if isinstance(channel_data, dict):
                subject = channel_data.get("subject", "") or ""
            sender_id = getattr(sender, "id", "") if sender else ""
            incoming_text = (
                f"Please respond to this email From: {sender_id}\n"
                f"Subject: {subject}\nMessage: {incoming_text}"
            )
        elif channel_id == "msteams":
            sender_name = getattr(sender, "name", "") if sender else ""
            sender_id = getattr(sender, "id", "") if sender else ""
            conversation_id = activity.conversation.id if activity.conversation else "default"
            incoming_text = (
                f"Respond to this chat message with chat id {conversation_id} "
                f"From: {sender_name} ({sender_id})\nMessage: {incoming_text}"
            )
        elif activity.type == "installationUpdate":
            sender_id = getattr(sender, "id", "") if sender else ""
            incoming_text = (
                f"You were just added as a digital worker. Please send an email to "
                f"{sender_id} with information on what you can do."
            )

        conversation_id = (
            activity.conversation.id if activity.conversation else "default"
        )
        response = await self._invoke_responses_api(incoming_text, conversation_id)

        if activity.type == "message":
            # Multiple discrete messages is the recommended pattern for agentic
            # identities in Teams (streaming is buffered into a single message).
            final_text = response if response and response.strip() else "Done."
            await turn_context.send_activity(MessageFactory.text(final_text))
        elif response:
            await turn_context.send_activity(MessageFactory.text(response))

    # ------------------------------------------------------------------
    # Alternate entry points (background invocations not using the SDK).
    # ------------------------------------------------------------------
    async def new_email_received(
        self, from_email: str, subject: str, message_body: str
    ) -> str:
        formatted = (
            f"Please respond to this email From: {from_email}\n"
            f"Subject: {subject}\nMessage: {message_body}"
        )
        return await self._invoke_responses_api(
            formatted, f"email:{from_email}:{subject}"
        )

    async def new_chat_received(
        self, chat_id: str, from_user: str, message_body: str
    ) -> str:
        formatted = (
            f"Respond to this chat message with chat id {chat_id} "
            f"From: {from_user}\nMessage: {message_body}"
        )
        return await self._invoke_responses_api(formatted, chat_id)

    # ------------------------------------------------------------------
    # Notification handlers.
    # ------------------------------------------------------------------
    async def handle_email_notification(
        self,
        turn_context: TurnContext,
        _turn_state: TurnState,
        notification_activity: Any,
    ) -> None:
        logger.info(
            "Processing email notification (Responses API) - NotificationType: %s",
            getattr(notification_activity, "notification_type", None),
        )
        conversation_id = (
            turn_context.activity.conversation.id
            if turn_context.activity.conversation
            else "email-notification"
        )
        text = getattr(notification_activity, "text", "") or ""
        response = await self._invoke_responses_api(text, conversation_id)

        response_activity = EmailResponse.create_email_response_activity(response)
        response_activity.text = response
        await turn_context.send_activity(response_activity)

    async def handle_comment_notification(self, *_args, **_kwargs) -> None:
        logger.info("Processing comment notification (Responses API)")

    async def handle_teams_message(self, *_args, **_kwargs) -> None:
        logger.info("Processing Teams message (Responses API)")

    async def handle_installation_update(self, *_args, **_kwargs) -> None:
        logger.info("Processing installation update (Responses API)")

    # ------------------------------------------------------------------
    # Responses API plumbing.
    # ------------------------------------------------------------------
    async def _invoke_responses_api(self, input_text: str, conversation_id: str) -> str:
        endpoint = self._configuration.get("AzureOpenAIEndpoint") or os.getenv(
            "AzureOpenAIEndpoint"
        )
        if not endpoint:
            raise RuntimeError("AzureOpenAIEndpoint not configured")
        deployment = self._configuration.get("ModelDeployment") or os.getenv(
            "ModelDeployment"
        )
        if not deployment:
            raise RuntimeError("ModelDeployment not configured")

        instructions = get_instructions(self._agent)

        mcp_tools = [
            {
                "type": "mcp",
                "server_label": server.mcp_server_name,
                "server_url": server.url,
                "server_description": f"MCP server: {server.mcp_server_name}",
                "require_approval": "never",
                "headers": {"Authorization": f"Bearer {self._access_token}"},
            }
            for server in self._mcp_servers
        ]

        logger.info(
            "Invoking Responses API with %d MCP tool servers", len(mcp_tools)
        )

        previous_response_id = _load_previous_response_id(conversation_id)
        if previous_response_id:
            logger.info(
                "Continuing conversation %s with previous_response_id: %s",
                conversation_id,
                previous_response_id,
            )

        request_body: dict[str, Any] = {
            "model": deployment,
            "instructions": instructions,
            "input": input_text,
            "tools": mcp_tools,
        }
        if previous_response_id:
            request_body["previous_response_id"] = previous_response_id

        request_url = (
            f"{endpoint.rstrip('/')}/openai/responses?api-version=2025-03-01-preview"
        )

        # Acquire a managed-identity token bound to the agent's default
        # instance identity for the Azure OpenAI control plane.
        instance_client_id = os.getenv("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID")
        if not instance_client_id:
            raise RuntimeError(
                "FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID environment variable is not set."
            )

        credential = DefaultAzureCredential(
            managed_identity_client_id=instance_client_id
        )
        try:
            mi_token = await credential.get_token(_COGNITIVE_SERVICES_SCOPE)
        finally:
            await credential.close()

        headers = {
            "Authorization": f"Bearer {mi_token.token}",
            "Content-Type": "application/json",
        }

        response = await self._http.post(
            request_url, json=request_body, headers=headers
        )

        if response.status_code >= 400:
            logger.error(
                "Responses API call failed with status %s: %s",
                response.status_code,
                response.text,
            )
            return (
                f"I encountered an error processing your request. "
                f"Status: {response.status_code}"
            )

        response_json = response.json()
        _save_response_id(conversation_id, response_json)
        return _extract_output_text(response_json)


# ---------------------------------------------------------------------------
# Conversation continuity helpers (previous_response_id persistence).
# ---------------------------------------------------------------------------
def _response_store_dir() -> Path:
    return Path.home() / ".a365agent"


def _response_id_file(conversation_id: str) -> Path:
    safe_id = (
        base64.urlsafe_b64encode(conversation_id.encode("utf-8"))
        .decode("ascii")
        .rstrip("=")
    )
    return _response_store_dir() / f"{safe_id}.responseid"


def _load_previous_response_id(conversation_id: str) -> str | None:
    try:
        path = _response_id_file(conversation_id)
        if path.exists():
            value = path.read_text(encoding="utf-8").strip()
            return value or None
    except Exception:
        logger.warning(
            "Failed to load previous_response_id for conversation %s",
            conversation_id,
            exc_info=True,
        )
    return None


def _save_response_id(conversation_id: str, response_json: dict[str, Any]) -> None:
    try:
        response_id = response_json.get("id") if isinstance(response_json, dict) else None
        if not response_id:
            return
        store = _response_store_dir()
        store.mkdir(parents=True, exist_ok=True)
        _response_id_file(conversation_id).write_text(response_id, encoding="utf-8")
    except Exception:
        logger.warning(
            "Failed to save response_id for conversation %s",
            conversation_id,
            exc_info=True,
        )


def _extract_output_text(response_json: dict[str, Any]) -> str:
    try:
        output = response_json.get("output")
        if isinstance(output, list):
            parts: list[str] = []
            for item in output:
                if not isinstance(item, dict) or item.get("type") != "message":
                    continue
                content = item.get("content")
                if not isinstance(content, list):
                    continue
                for content_item in content:
                    if (
                        isinstance(content_item, dict)
                        and content_item.get("type") == "output_text"
                    ):
                        text = content_item.get("text")
                        if isinstance(text, str):
                            parts.append(text)
            return "".join(parts)

        simple = response_json.get("output_text")
        if isinstance(simple, str):
            return simple

        logger.warning("Could not extract output text from Responses API response")
        return ""
    except Exception:
        logger.exception("Error parsing Responses API response")
        return ""
