# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""Generic Agent Host Server — hosts agents implementing :class:`AgentInterface`.

Python port of the C# ``Program.cs`` + ``A365AgentApplication``. Wires up:

* Azure Key Vault as a configuration source when ``KEY_VAULT_NAME`` is set.
* Application Insights telemetry when
  ``APPLICATIONINSIGHTS_CONNECTION_STRING`` (or the legacy
  ``ApplicationInsights__ConnectionString`` binding from ``appsettings.json``)
  is set.
* The Microsoft Agents SDK ``AgentApplication`` (the Python equivalent of the
  C# ``builder.AddAgent<A365AgentApplication>``), with all four agentic
  notification handlers (Email, Word, Excel, PowerPoint) routed through the
  agent's ``handle_agent_notification_activity``.
* The HTTP server endpoints ``/api/messages``, ``/``, ``/liveness``, and
  ``/readiness`` to match the original C# minimal-API routes.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import socket
from collections.abc import Mapping
from os import environ
from typing import Optional
from uuid import UUID

from aiohttp.web import Application, Request, Response, json_response, run_app
from aiohttp.web_middlewares import middleware as web_middleware
from microsoft_agents.activity import Activity, load_configuration_from_env
from microsoft_agents.authentication.msal import MsalConnectionManager
from microsoft_agents.hosting.aiohttp import (
    CloudAdapter,
    jwt_authorization_middleware,
    start_agent_process,
)
from microsoft_agents.hosting.core import (
    AgentApplication,
    AgentAuthConfiguration,
    AuthenticationConstants,
    Authorization,
    ClaimsIdentity,
    MemoryStorage,
    TurnContext,
    TurnState,
)
from microsoft_agents_a365.notifications import EmailResponse
from microsoft_agents_a365.notifications.agent_notification import (
    AgentNotification,
    AgentNotificationActivity,
    ChannelId,
    NotificationTypes,
)

from .agent_interface import AgentInterface, check_agent_inheritance
from .msal_auth_patches import apply_msal_auth_patches
from .token_cache import cache_agentic_token, get_cached_agentic_token

# Apply runtime patches to microsoft_agents.authentication.msal before any
# MsalConnectionManager / MsalAuth instance is constructed.
apply_msal_auth_patches()

_LOG_LEVEL_NAME = os.getenv("LOG_LEVEL", "INFO").upper()
_LOG_LEVEL = getattr(logging, _LOG_LEVEL_NAME, logging.INFO)
logging.basicConfig(
    level=_LOG_LEVEL,
    format="%(asctime)s %(levelname)s %(name)s | %(message)s",
)

ms_agents_logger = logging.getLogger("microsoft_agents")
ms_agents_logger.addHandler(logging.StreamHandler())
ms_agents_logger.setLevel(logging.INFO)

observability_logger = logging.getLogger("microsoft_agents_a365.observability")
observability_logger.setLevel(logging.ERROR)

logger = logging.getLogger(__name__)
logger.info("📝 Logging configured at level %s (from LOG_LEVEL env)", _LOG_LEVEL_NAME)


def _is_email_channel_id(channel_id: str) -> bool:
    return channel_id.lower() in {"email", "agents:email"}


def _email_responses_enabled() -> bool:
    value = (os.getenv("ENABLE_EMAIL_RESPONSES") or "").strip().lower()
    return value in {"1", "true", "yes", "on"}


def _normalize_tenant_id(value: str) -> str:
    tenant_id = value.strip()
    if not tenant_id:
        return ""
    try:
        return str(UUID(tenant_id))
    except ValueError:
        return tenant_id.lower()


def _add_tenant_candidate(candidates: set[str], value: object) -> None:
    if not isinstance(value, str):
        return
    normalized = _normalize_tenant_id(value)
    if normalized:
        candidates.add(normalized)


def _add_tenant_candidate_value(candidates: set[str], value: object) -> None:
    if isinstance(value, str):
        _add_tenant_candidate(candidates, value)
        return
    if isinstance(value, Mapping):
        _add_tenant_candidate(candidates, value.get("id"))


def _get_tenant_id_candidates(account: object | None) -> list[str]:
    candidates: set[str] = set()
    if account is None:
        return []

    _add_tenant_candidate(candidates, getattr(account, "tenant_id", None))
    _add_tenant_candidate(candidates, getattr(account, "tenantId", None))
    _add_tenant_candidate_value(candidates, getattr(account, "tenant", None))

    for attribute_name in ("properties", "additional_properties", "additionalProperties"):
        payload = getattr(account, attribute_name, None)
        if isinstance(payload, Mapping):
            _add_tenant_candidate_value(candidates, payload.get("tenantId"))
            _add_tenant_candidate_value(candidates, payload.get("tenant"))
            _add_tenant_candidate_value(candidates, payload.get("tenant_id"))

    if isinstance(account, Mapping):
        _add_tenant_candidate_value(candidates, account.get("tenantId"))
        _add_tenant_candidate_value(candidates, account.get("tenant"))
        _add_tenant_candidate_value(candidates, account.get("tenant_id"))

    return sorted(candidates)


def _configured_tenant_id() -> str:
    return (
        os.getenv("TENANT_ID")
        or os.getenv("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID")
        or ""
    )


def _get_agent_tenant_id_candidates_from_activity(
    activity_payload: Mapping[str, object],
) -> list[str]:
    candidates: set[str] = set()

    for candidate in _get_tenant_id_candidates(activity_payload.get("recipient")):
        candidates.add(candidate)

    conversation = activity_payload.get("conversation")
    if isinstance(conversation, Mapping):
        _add_tenant_candidate_value(candidates, conversation.get("tenantId"))
        _add_tenant_candidate_value(candidates, conversation.get("tenant"))
        _add_tenant_candidate_value(candidates, conversation.get("tenant_id"))

    channel_data = activity_payload.get("channelData")
    if isinstance(channel_data, Mapping):
        _add_tenant_candidate_value(candidates, channel_data.get("tenantId"))
        _add_tenant_candidate_value(candidates, channel_data.get("tenant"))
        _add_tenant_candidate_value(candidates, channel_data.get("tenant_id"))

    _add_tenant_candidate(candidates, _configured_tenant_id())
    return sorted(candidates)


def _is_provably_internal_activity(
    sender_tenant_candidates: list[str], agent_tenant_candidates: list[str]
) -> bool:
    if not sender_tenant_candidates or not agent_tenant_candidates:
        return False
    return any(
        sender_tenant == agent_tenant
        for sender_tenant in sender_tenant_candidates
        for agent_tenant in agent_tenant_candidates
    )


def _should_block_activity_payload(activity_payload: Mapping[str, object]) -> bool:
    sender = activity_payload.get("from")
    sender_tenant_candidates = _get_tenant_id_candidates(sender)

    agent_tenant_candidates = _get_agent_tenant_id_candidates_from_activity(activity_payload)

    if _is_provably_internal_activity(sender_tenant_candidates, agent_tenant_candidates):
        return False

    sender_id = sender.get("id") if isinstance(sender, Mapping) else None
    sender_name = sender.get("name") if isinstance(sender, Mapping) else None
    logger.info(
        "AP tenant guard: blocked activity because it is not provably internal. activityType=%s senderId=%s senderName=%s senderTenantCandidates=%s agentTenantCandidates=%s",
        activity_payload.get("type"),
        sender_id,
        sender_name,
        ",".join(sender_tenant_candidates),
        ",".join(agent_tenant_candidates),
    )
    return True


# ---------------------------------------------------------------------------
# Key Vault + Application Insights bootstrap (matches Program.cs)
# ---------------------------------------------------------------------------


def _configure_key_vault() -> None:
    key_vault_name = os.getenv("KeyVaultName") or os.getenv("KEY_VAULT_NAME")
    if not key_vault_name:
        print("KeyVaultName not configured. Key Vault integration skipped.")
        return

    key_vault_uri = f"https://{key_vault_name}.vault.azure.net/"
    try:
        from azure.identity import DefaultAzureCredential
        from azure.keyvault.secrets import SecretClient

        client = SecretClient(vault_url=key_vault_uri, credential=DefaultAzureCredential())
        for secret_properties in client.list_properties_of_secrets():
            name = secret_properties.name
            secret = client.get_secret(name)
            env_name = name.replace("--", "__")
            os.environ.setdefault(env_name, secret.value or "")
        print(f"Azure Key Vault configured: {key_vault_uri}")
    except Exception as ex:
        logger.warning("Failed to load secrets from %s: %s", key_vault_uri, ex)


def _configure_application_insights() -> None:
    conn = (
        os.getenv("APPLICATIONINSIGHTS_CONNECTION_STRING")
        or os.getenv("ApplicationInsights__ConnectionString")
    )
    if not conn:
        return
    print(f"AI ConnectionString: {conn}")
    try:
        from azure.monitor.opentelemetry import configure_azure_monitor

        configure_azure_monitor(connection_string=conn)
    except Exception as ex:
        logger.warning("Failed to configure Application Insights: %s", ex)


_configure_key_vault()
_configure_application_insights()


agents_sdk_config = load_configuration_from_env(environ)


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------


def create_and_run_host(agent_class: type[AgentInterface], *agent_args, **agent_kwargs) -> None:
    """Create and run a generic agent host."""

    if not check_agent_inheritance(agent_class):
        raise TypeError(
            f"Agent class {agent_class.__name__} must inherit from AgentInterface"
        )

    try:
        from microsoft.opentelemetry import use_microsoft_opentelemetry

        use_microsoft_opentelemetry(
            enable_a365=True,
            enable_azure_monitor=False,
            a365_token_resolver=lambda agent_id, tenant_id: (
                get_cached_agentic_token(tenant_id, agent_id) or ""
            ),
        )
    except Exception as ex:
        logger.warning("Microsoft OpenTelemetry distro not initialized: %s", ex)

    host = GenericAgentHost(agent_class, *agent_args, **agent_kwargs)
    auth_config = host.create_auth_configuration()
    host.start_server(auth_config)


# ---------------------------------------------------------------------------
# GenericAgentHost
# ---------------------------------------------------------------------------


class GenericAgentHost:
    """Generic host for agents implementing :class:`AgentInterface`."""

    def __init__(
        self,
        agent_class: type[AgentInterface],
        *agent_args,
        **agent_kwargs,
    ) -> None:
        if not check_agent_inheritance(agent_class):
            raise TypeError(
                f"Agent class {agent_class.__name__} must inherit from AgentInterface"
            )

        # The handler key inside AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS
        # comes through verbatim from the env-var split. Set AUTH_HANDLER_NAME
        # to match (uppercase) — empty disables agentic auth entirely.
        self.auth_handler_name = os.getenv("AUTH_HANDLER_NAME", "AGENTIC") or None
        if self.auth_handler_name:
            logger.info("🔐 Using auth handler: %s", self.auth_handler_name)
        else:
            logger.info("🔓 No auth handler configured (AUTH_HANDLER_NAME not set)")
        self.email_responses_enabled = _email_responses_enabled()
        logger.info(
            "📧 Email responses are %s (ENABLE_EMAIL_RESPONSES=%s)",
            "enabled" if self.email_responses_enabled else "disabled",
            os.getenv("ENABLE_EMAIL_RESPONSES", "(unset)"),
        )

        self.agent_class = agent_class
        self.agent_args = agent_args
        self.agent_kwargs = agent_kwargs
        self.agent_instance: Optional[AgentInterface] = None

        self.storage = MemoryStorage()
        self.connection_manager = MsalConnectionManager(**agents_sdk_config)
        self.adapter = CloudAdapter(connection_manager=self.connection_manager)
        self.authorization = Authorization(
            self.storage, self.connection_manager, **agents_sdk_config
        )

        # Diagnostic: dump what the SDK actually loaded from env so we can tell
        # whether AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__<name>__... env
        # vars reached the container and were parsed as expected.
        _agent_app_cfg = agents_sdk_config.get("AGENTAPPLICATION", {})
        _user_auth_cfg = _agent_app_cfg.get("USERAUTHORIZATION", {})
        _handlers_cfg = _user_auth_cfg.get("HANDLERS", {})
        logger.info(
            "🔎 Auth handlers loaded from config: env_keys=%s | "
            "registered=%s | default=%s",
            list(_handlers_cfg.keys()),
            list(self.authorization._handlers.keys()),
            getattr(self.authorization, "_default_handler_id", None),
        )
        if self.auth_handler_name and self.auth_handler_name not in self.authorization._handlers:
            logger.error(
                "❌ AUTH_HANDLER_NAME=%s is NOT in registered handlers %s. "
                "Check that AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__%s__SETTINGS__TYPE "
                "is set in the container env.",
                self.auth_handler_name,
                list(self.authorization._handlers.keys()),
                self.auth_handler_name,
            )

        self.agent_app = AgentApplication[TurnState](
            storage=self.storage,
            adapter=self.adapter,
            authorization=self.authorization,
            **agents_sdk_config,
        )
        self.agent_notification = AgentNotification(self.agent_app)
        self._setup_handlers()
        logger.info("✅ Notification handlers registered successfully")

    # ------------------------------------------------------------------
    # Observability
    # ------------------------------------------------------------------

    async def _setup_observability_token(
        self, context: TurnContext, tenant_id: str, agent_id: str
    ) -> None:
        if not self.auth_handler_name:
            logger.debug("Skipping observability token exchange (no auth handler)")
            return
        try:
            from microsoft_agents_a365.runtime.environment_utils import (
                get_observability_authentication_scope,
            )

            exaau_token = await self.agent_app.auth.exchange_token(
                context,
                scopes=get_observability_authentication_scope(),
                auth_handler_id=self.auth_handler_name,
            )
            cache_agentic_token(tenant_id, agent_id, exaau_token.token)
            logger.info(
                "✅ Token exchange successful (tenant_id=%s, agent_id=%s)",
                tenant_id,
                agent_id,
            )
        except Exception as ex:
            logger.warning("⚠️ Failed to cache observability token: %s", ex)

    async def _validate_agent_and_setup_context(self, context: TurnContext):
        recipient = context.activity.recipient
        tenant_id = getattr(recipient, "tenant_id", "") or ""
        agent_id = getattr(recipient, "agentic_app_id", "") or ""
        logger.info("🔍 tenant_id=%s, agent_id=%s", tenant_id, agent_id)

        if not self.agent_instance:
            logger.error("Agent not available")
            await context.send_activity("❌ Sorry, the agent is not available.")
            return None

        await self._setup_observability_token(context, tenant_id, agent_id)
        return tenant_id, agent_id

    async def _should_block_cross_tenant_activity(
        self, context: TurnContext, agent_tenant_id: str
    ) -> bool:
        sender = getattr(context.activity, "from_property", None)
        sender_tenant_candidates = _get_tenant_id_candidates(sender)

        agent_tenant_candidates = set()
        normalized_agent_tenant_id = _normalize_tenant_id(agent_tenant_id or "")
        if normalized_agent_tenant_id:
            agent_tenant_candidates.add(normalized_agent_tenant_id)
        configured_tenant_id = _normalize_tenant_id(_configured_tenant_id())
        if configured_tenant_id:
            agent_tenant_candidates.add(configured_tenant_id)
        sorted_agent_tenant_candidates = sorted(agent_tenant_candidates)

        if _is_provably_internal_activity(
            sender_tenant_candidates, sorted_agent_tenant_candidates
        ):
            return False

        logger.info(
            "AP tenant guard: blocked activity because it is not provably internal. senderId=%s senderName=%s senderTenantCandidates=%s agentTenantCandidates=%s",
            getattr(sender, "id", None),
            getattr(sender, "name", None),
            ",".join(sender_tenant_candidates),
            ",".join(sorted_agent_tenant_candidates),
        )
        return True

    # ------------------------------------------------------------------
    # Handlers
    # ------------------------------------------------------------------

    def _setup_handlers(self) -> None:
        handler_config = (
            {"auth_handlers": [self.auth_handler_name]} if self.auth_handler_name else {}
        )

        async def help_handler(context: TurnContext, _: TurnState) -> None:
            recipient = getattr(context.activity, "recipient", None)
            agent_tenant_id = getattr(recipient, "tenant_id", "") if recipient else ""
            if await self._should_block_cross_tenant_activity(context, agent_tenant_id):
                return

            channel_id = getattr(context.activity, "channel_id", "") or ""
            if _is_email_channel_id(channel_id) and not self.email_responses_enabled:
                logger.info(
                    "Email responses disabled by config; ignoring help message on channel_id=%s",
                    channel_id,
                )
                return

            await context.send_activity(
                f"👋 **Hi there!** I'm **{self.agent_class.__name__}**, your AI assistant.\n\n"
                "How can I help you today?"
            )

        self.agent_app.conversation_update("membersAdded", **handler_config)(help_handler)
        self.agent_app.message("/help", **handler_config)(help_handler)

        @self.agent_app.activity("installationUpdate")
        async def on_installation_update(context: TurnContext, _: TurnState) -> None:
            recipient = getattr(context.activity, "recipient", None)
            agent_tenant_id = getattr(recipient, "tenant_id", "") if recipient else ""
            if await self._should_block_cross_tenant_activity(context, agent_tenant_id):
                return

            action = getattr(context.activity, "action", None)
            from_prop = context.activity.from_property
            logger.info(
                "InstallationUpdate received — Action: '%s', DisplayName: '%s', UserId: '%s'",
                action or "(none)",
                getattr(from_prop, "name", "(unknown)") if from_prop else "(unknown)",
                getattr(from_prop, "id", "(unknown)") if from_prop else "(unknown)",
            )
            if action == "add":
                await context.send_activity(
                    "Thank you for hiring me! Looking forward to assisting you in "
                    "your professional journey!"
                )
            elif action == "remove":
                await context.send_activity(
                    "Thank you for your time, I enjoyed working with you."
                )

        @self.agent_app.activity("message", **handler_config)
        async def on_message(context: TurnContext, _: TurnState) -> None:
            try:
                result = await self._validate_agent_and_setup_context(context)
                if result is None:
                    return
                tenant_id, agent_id = result

                if await self._should_block_cross_tenant_activity(context, tenant_id):
                    return

                channel_id = getattr(context.activity, "channel_id", "") or ""
                if _is_email_channel_id(channel_id) and not self.email_responses_enabled:
                    logger.info(
                        "Email responses disabled by config; ignoring message on channel_id=%s",
                        channel_id,
                    )
                    return

                from microsoft_agents_a365.observability.core.middleware.baggage_builder import (
                    BaggageBuilder,
                )

                with BaggageBuilder().tenant_id(tenant_id).agent_id(agent_id).build():
                    user_message = context.activity.text or ""
                    if not user_message.strip() or user_message.strip() == "/help":
                        return

                    logger.info("📨 %s", user_message)

                    # Multi-message pattern: typing indicator loop, then the
                    # final LLM response. Mirrors the C# StreamingResponse flow
                    # (QueueInformativeUpdateAsync + QueueTextChunk).
                    #
                    # Optional "Working on your request..." informative message.
                    # Uncomment if you want the explicit text bubble in addition
                    # to Teams' built-in typing animation.
                    # await context.send_activity("Working on your request...")
                    await context.send_activity(Activity(type="typing"))

                    async def _typing_loop() -> None:
                        try:
                            while True:
                                await asyncio.sleep(4)
                                await context.send_activity(Activity(type="typing"))
                        except asyncio.CancelledError:
                            pass

                    typing_task = asyncio.create_task(_typing_loop())
                    try:
                        response = await self.agent_instance.process_user_message(
                            user_message,
                            self.agent_app.auth,
                            self.auth_handler_name,
                            context,
                        )
                        await context.send_activity(response)
                    finally:
                        typing_task.cancel()
                        try:
                            await typing_task
                        except asyncio.CancelledError:
                            pass

            except Exception as ex:
                logger.exception("Error processing message")
                session_id = os.getenv("FOUNDRY_AGENT_SESSION_ID") or "(not set)"
                await context.send_activity(
                    "Sorry, something went wrong while processing your message.\n"
                    f"FOUNDRY_AGENT_SESSION_ID: {session_id}\n"
                    f"Exception: {ex}"
                )

        @self.agent_notification.on_agent_notification(
            channel_id=ChannelId(channel="agents", sub_channel="*"),
            **handler_config,
        )
        async def on_notification(
            context: TurnContext,
            state: TurnState,
            notification_activity: AgentNotificationActivity,
        ) -> None:
            try:
                result = await self._validate_agent_and_setup_context(context)
                if result is None:
                    return
                tenant_id, agent_id = result

                if await self._should_block_cross_tenant_activity(context, tenant_id):
                    return

                from microsoft_agents_a365.observability.core.middleware.baggage_builder import (
                    BaggageBuilder,
                )

                with BaggageBuilder().tenant_id(tenant_id).agent_id(agent_id).build():
                    logger.info("📬 %s", notification_activity.notification_type)

                    if (
                        not self.email_responses_enabled
                        and notification_activity.notification_type
                        == NotificationTypes.EMAIL_NOTIFICATION
                    ):
                        logger.info(
                            "Email responses disabled by config; ignoring email notification."
                        )
                        return

                    if not hasattr(
                        self.agent_instance, "handle_agent_notification_activity"
                    ):
                        logger.warning("⚠️ Agent doesn't support notifications")
                        await context.send_activity(
                            "This agent doesn't support notification handling yet."
                        )
                        return

                    response = await self.agent_instance.handle_agent_notification_activity(
                        notification_activity,
                        self.agent_app.auth,
                        self.auth_handler_name,
                        context,
                    )

                    if (
                        notification_activity.notification_type
                        == NotificationTypes.EMAIL_NOTIFICATION
                    ):
                        response_activity = EmailResponse.create_email_response_activity(
                            response
                        )
                        await context.send_activity(response_activity)
                        return

                    await context.send_activity(response)
            except Exception as ex:
                logger.exception("Notification error")
                await context.send_activity(
                    f"Sorry, I encountered an error processing the notification: {ex}"
                )

    # ------------------------------------------------------------------
    # Agent lifecycle
    # ------------------------------------------------------------------

    async def initialize_agent(self) -> None:
        if self.agent_instance is None:
            logger.info("🤖 Initializing %s...", self.agent_class.__name__)
            self.agent_instance = self.agent_class(*self.agent_args, **self.agent_kwargs)
            await self.agent_instance.initialize()

    async def cleanup(self) -> None:
        if self.agent_instance:
            try:
                await self.agent_instance.cleanup()
            except Exception:
                logger.exception("Cleanup error")

    # ------------------------------------------------------------------
    # Authentication
    # ------------------------------------------------------------------

    def create_auth_configuration(self) -> AgentAuthConfiguration | None:
        client_id = environ.get("CLIENT_ID") or environ.get(
            "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID"
        )
        tenant_id = environ.get("TENANT_ID") or environ.get(
            "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID"
        )
        client_secret = environ.get("CLIENT_SECRET") or environ.get(
            "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET"
        )

        if client_id and tenant_id and client_secret:
            logger.info("🔒 Using Client Credentials authentication")
            return AgentAuthConfiguration(
                client_id=client_id,
                tenant_id=tenant_id,
                client_secret=client_secret,
                scopes=["5a807f24-c9de-44ee-a3a7-329e88a00ffc/.default"],
            )

        if environ.get("BEARER_TOKEN"):
            logger.info("🔑 Anonymous dev mode")
        else:
            logger.warning("⚠️ No auth env vars; running anonymous")
        return None

    # ------------------------------------------------------------------
    # HTTP server
    # ------------------------------------------------------------------

    def start_server(
        self, auth_configuration: AgentAuthConfiguration | None = None
    ) -> None:
        async def entry_point(req: Request) -> Response:
            activity_payload: Mapping[str, object] | None = None
            parse_error: str | None = None
            try:
                body_bytes = await req.read()
                body_text = ""
                try:
                    body_text = body_bytes.decode("utf-8")
                except UnicodeDecodeError:
                    parse_error = "request body is not valid UTF-8 text"
                body_repr = body_text if body_text else repr(body_bytes)
                logger.info(
                    "📥 /api/messages request | method=%s | content-type=%s | size=%d bytes | body=%s",
                    req.method,
                    req.headers.get("Content-Type", ""),
                    len(body_bytes),
                    body_repr,
                )
                if body_text:
                    try:
                        parsed_payload = json.loads(body_text)
                    except Exception as ex:
                        parse_error = f"invalid JSON payload: {ex}"
                    else:
                        if isinstance(parsed_payload, Mapping):
                            activity_payload = parsed_payload
                        else:
                            parse_error = "top-level JSON value is not an object"
                elif parse_error is None:
                    parse_error = "request body is empty"
            except Exception as ex:
                logger.warning("Failed to log incoming request body: %s", ex)
                parse_error = f"failed to read request body: {ex}"

            if activity_payload is None:
                logger.info(
                    "AP tenant guard: blocked activity because it is not provably internal. reason=%s",
                    parse_error or "activity payload unavailable",
                )
                return Response(status=200)

            if _should_block_activity_payload(activity_payload):
                return Response(status=200)

            return await start_agent_process(
                req, req.app["agent_app"], req.app["adapter"]
            )

        async def root(_req: Request) -> Response:
            return Response(text="Hello World from HelloWorldA365Agent!")

        async def health(_req: Request) -> Response:
            return json_response(
                {
                    "status": "ok",
                    "agent_type": self.agent_class.__name__,
                    "agent_initialized": self.agent_instance is not None,
                }
            )

        middlewares = []
        if auth_configuration:

            @web_middleware
            async def jwt_with_health_bypass(request, handler):
                # Skip JWT validation for health/liveness/readiness/root endpoints
                # so that container orchestrators can reach them without a bearer token.
                if request.path in {"/", "/liveness", "/readiness", "/api/health"}:
                    return await handler(request)
                return await jwt_authorization_middleware(request, handler)

            middlewares.append(jwt_with_health_bypass)

        @web_middleware
        async def anonymous_claims(request, handler):
            if not auth_configuration:
                request["claims_identity"] = ClaimsIdentity(
                    {
                        AuthenticationConstants.AUDIENCE_CLAIM: "anonymous",
                        AuthenticationConstants.APP_ID_CLAIM: "anonymous-app",
                    },
                    False,
                    "Anonymous",
                )
            return await handler(request)

        middlewares.append(anonymous_claims)
        app = Application(middlewares=middlewares)

        app.router.add_post("/api/messages", entry_point)
        app.router.add_get("/api/messages", lambda _: Response(status=200))
        app.router.add_get("/", root)
        app.router.add_get("/liveness", root)
        app.router.add_get("/readiness", root)
        app.router.add_get("/api/health", health)

        app["agent_configuration"] = auth_configuration
        app["agent_app"] = self.agent_app
        app["adapter"] = self.agent_app.adapter

        app.on_startup.append(lambda app: self.initialize_agent())
        app.on_shutdown.append(lambda app: self.cleanup())

        desired_port = int(environ.get("PORT", 8088))
        port = desired_port
        host_addr = environ.get("HOST", "0.0.0.0")

        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.settimeout(0.5)
            if s.connect_ex(("127.0.0.1", desired_port)) == 0:
                port = desired_port + 1

        print("=" * 80)
        print(f"🏢 {self.agent_class.__name__}")
        print("=" * 80)
        print(f"🔒 Auth: {'Enabled' if auth_configuration else 'Anonymous'}")
        print(f"🚀 Server: {host_addr}:{port}")
        print(f"📚 Endpoint: http://{host_addr}:{port}/api/messages")
        print(f"❤️  Health: http://{host_addr}:{port}/api/health\n")

        try:
            run_app(app, host=host_addr, port=port, handle_signals=True)
        except KeyboardInterrupt:
            print("\n👋 Server stopped")
