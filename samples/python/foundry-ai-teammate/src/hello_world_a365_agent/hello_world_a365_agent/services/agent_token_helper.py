"""
Three-step agentic user identity token acquisition using managed identity,
mirroring the C# AgentTokenHelper.

  1. Acquire a "blueprint" token via the managed identity associated with the
     agent application (client_id = agentAppId).
  2. Use that blueprint token as a client assertion to acquire an instance
     token for the agent app instance (client_id = agentAppInstanceId), audience
     "api://AzureAdTokenExchange".
  3. Exchange the two tokens for a user-federated identity token via the
     login.microsoftonline.com /oauth2/v2.0/token endpoint with
     grant_type=user_fic.
"""

from __future__ import annotations

import logging
from typing import Sequence

import httpx
from azure.identity.aio import DefaultAzureCredential
from msal import ConfidentialClientApplication

logger = logging.getLogger(__name__)


class AgentTokenHelper:
    """Acquires agentic user identity tokens using managed identity."""

    async def get_agentic_user_token(
        self,
        agent_app_id: str,
        agent_app_instance_id: str,
        user_upn: str,
        tenant_id: str,
        scopes: Sequence[str],
    ) -> str:
        try:
            # FIRST: Get blueprint token via managed identity.
            blueprint_token = await self._get_blueprint_token(agent_app_id)

            # SECOND: Get AAD token for AgentAppInstanceId using the blueprint token
            # as a client assertion (federated credential).
            instance_app = ConfidentialClientApplication(
                client_id=agent_app_instance_id,
                client_credential={"client_assertion": blueprint_token},
                authority=f"https://login.microsoftonline.com/{tenant_id}",
            )
            instance_result = instance_app.acquire_token_for_client(
                scopes=["api://AzureAdTokenExchange/.default"]
            )
            instance_token = instance_result.get("access_token") if instance_result else None
            if not instance_token:
                raise RuntimeError(
                    f"Failed to acquire instance token: {instance_result}"
                )

            # THIRD: Get combined user token.
            return await self._get_user_federated_identity_token(
                client_id=agent_app_instance_id,
                tenant_id=tenant_id,
                client_assertion=blueprint_token,
                user_federated_identity_credential=instance_token,
                username=user_upn,
                scopes=scopes,
            )
        except Exception:
            logger.exception("Error acquiring agentic user token")
            raise

    async def _get_blueprint_token(self, client_id: str) -> str:
        # Managed identity with the blueprint app's client id.
        credential = DefaultAzureCredential(managed_identity_client_id=client_id)
        try:
            access_token = await credential.get_token(
                "api://AzureADTokenExchange/.default"
            )
            return access_token.token
        finally:
            await credential.close()

    async def _get_user_federated_identity_token(
        self,
        client_id: str,
        tenant_id: str,
        client_assertion: str,
        user_federated_identity_credential: str,
        username: str,
        scopes: Sequence[str],
    ) -> str:
        token_endpoint = (
            f"https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token"
        )

        params: dict[str, str] = {
            "client_id": client_id,
            "scope": " ".join(scopes),
            "client_assertion_type": "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            "client_assertion": client_assertion,
            "user_federated_identity_credential": user_federated_identity_credential,
            "grant_type": "user_fic",
        }

        if "@" in username:
            params["username"] = username
        else:
            params["user_id"] = username

        async with httpx.AsyncClient() as client:
            response = await client.post(token_endpoint, data=params)
            if response.status_code >= 400:
                raise RuntimeError(
                    f"Failed to acquire user federated identity token: {response.text}"
                )

            body = response.json()
            access_token = body.get("access_token")
            if not access_token:
                raise RuntimeError("Failed to parse access token from response")
            return access_token
