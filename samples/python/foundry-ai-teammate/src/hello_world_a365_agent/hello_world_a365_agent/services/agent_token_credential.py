"""
Token helper that caches tokens and acquires them lazily on demand for a
specific AgentMetadata, mirroring the C# AgentTokenCredential.
"""

from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Sequence

import jwt

from ..models import AgentMetadata
from .agent_token_helper import AgentTokenHelper

logger = logging.getLogger(__name__)


@dataclass
class AccessToken:
    token: str
    expires_on: datetime


class AgentTokenCredential:
    """Caches an agentic user token per-agent with thread-safe refresh."""

    _DEFAULT_SCOPE = "https://canary.graph.microsoft.com/.default"

    def __init__(self, token_helper: AgentTokenHelper, agent: AgentMetadata):
        self._token_helper = token_helper
        self._agent = agent
        self._cached: AccessToken | None = None
        self._lock = asyncio.Lock()

    async def get_token(self, scopes: Sequence[str] | None = None) -> AccessToken:
        # Fast path: return the cached token if it isn't about to expire.
        if self._cached and datetime.now(timezone.utc) + timedelta(minutes=5) < self._cached.expires_on:
            return self._cached

        async with self._lock:
            if self._cached and datetime.now(timezone.utc) + timedelta(minutes=5) < self._cached.expires_on:
                return self._cached

            effective_scopes = list(scopes) if scopes else [self._DEFAULT_SCOPE]
            user_identity = self._agent.email_id or str(self._agent.user_id)
            token = await self._token_helper.get_agentic_user_token(
                agent_app_id=str(self._agent.agent_application_id),
                agent_app_instance_id=str(self._agent.agent_id),
                user_upn=user_identity,
                tenant_id=str(self._agent.tenant_id),
                scopes=effective_scopes,
            )

            access_token = AccessToken(token=token, expires_on=_get_token_expiry(token))
            self._cached = access_token
            return access_token


def _get_token_expiry(token: str) -> datetime:
    try:
        decoded = jwt.decode(token, options={"verify_signature": False})
        exp = decoded.get("exp")
        if exp:
            return datetime.fromtimestamp(int(exp), tz=timezone.utc)
    except Exception:
        # Fall through to default below if the JWT can't be read.
        pass
    return datetime.now(timezone.utc) + timedelta(hours=1)
