# Copyright (c) Microsoft. All rights reserved.

"""Foundry Responses adapter used by the Voice Live Bridge sample."""

from __future__ import annotations

import asyncio
import os
from collections.abc import AsyncIterator, Sequence
from urllib.parse import urlsplit, urlunsplit

from azure.identity.aio import DefaultAzureCredential
from openai import AsyncOpenAI
from opentelemetry import trace
from opentelemetry.trace import SpanKind, Status, StatusCode

from model_contract import StreamingModelClient
from state import ModelMessage

FOUNDRY_TOKEN_SCOPE = "https://ai.azure.com/.default"
DEFAULT_SYSTEM_PROMPT = (
    "You are a concise voice assistant. Answer naturally in plain text without markdown."
)
DEFAULT_MAX_OUTPUT_TOKENS = 512
MAX_OUTPUT_TOKENS = 4096

_tracer = trace.get_tracer("VoiceHostedAgent.Model")


def responses_base_url(project_endpoint: str) -> str:
    """Convert an HTTPS Foundry project endpoint to its OpenAI-compatible base URL."""
    parsed = urlsplit(project_endpoint.strip())
    if (
        parsed.scheme != "https"
        or not parsed.netloc
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
    ):
        raise ValueError("FOUNDRY_PROJECT_ENDPOINT must be an absolute HTTPS URL")

    path = parsed.path.rstrip("/")
    segments = [segment for segment in path.split("/") if segment]
    if (
        len(segments) != 3
        or segments[0].lower() != "api"
        or segments[1].lower() != "projects"
        or not segments[2].strip()
    ):
        raise ValueError("FOUNDRY_PROJECT_ENDPOINT must identify a Foundry project")
    return urlunsplit((parsed.scheme, parsed.netloc, f"{path}/openai/v1/", "", ""))


def parse_max_output_tokens(value: str | None) -> int:
    """Parse and bound the configured output-token limit."""
    if value is None or not value.strip():
        return DEFAULT_MAX_OUTPUT_TOKENS
    try:
        parsed = int(value)
    except ValueError as exc:
        raise ValueError(
            f"AZURE_OPENAI_MAX_OUTPUT_TOKENS must be between 1 and {MAX_OUTPUT_TOKENS}"
        ) from exc
    if not 1 <= parsed <= MAX_OUTPUT_TOKENS:
        raise ValueError(
            f"AZURE_OPENAI_MAX_OUTPUT_TOKENS must be between 1 and {MAX_OUTPUT_TOKENS}"
        )
    return parsed


class AzureOpenAIResponsesClient(StreamingModelClient):
    """Streaming Responses client using an API key or managed identity."""

    def __init__(
        self,
        *,
        client: AsyncOpenAI,
        credential: DefaultAzureCredential | None,
        model_name: str,
        server_address: str,
        system_prompt: str,
        max_output_tokens: int,
    ) -> None:
        self._client = client
        self._credential = credential
        self._system_prompt = system_prompt
        self._max_output_tokens = max_output_tokens
        self.model_name = model_name
        self.server_address = server_address

    @classmethod
    def from_environment(cls) -> "AzureOpenAIResponsesClient":
        """Create a client from standard Foundry hosted-agent environment variables."""
        endpoint = os.getenv("FOUNDRY_PROJECT_ENDPOINT", "").strip()
        deployment = os.getenv("AZURE_AI_MODEL_DEPLOYMENT_NAME", "").strip()
        if not endpoint or not deployment:
            raise ValueError(
                "FOUNDRY_PROJECT_ENDPOINT and AZURE_AI_MODEL_DEPLOYMENT_NAME are required"
            )

        base_url = responses_base_url(endpoint)
        api_key = os.getenv("AZURE_OPENAI_API_KEY", "").strip()
        credential: DefaultAzureCredential | None = None
        if api_key:
            client = AsyncOpenAI(api_key=api_key, base_url=base_url)
        else:
            credential = DefaultAzureCredential()

            async def token() -> str:
                return (await credential.get_token(FOUNDRY_TOKEN_SCOPE)).token

            client = AsyncOpenAI(api_key=token, base_url=base_url)

        return cls(
            client=client,
            credential=credential,
            model_name=deployment,
            server_address=urlsplit(base_url).hostname or "",
            system_prompt=(
                os.getenv("AZURE_OPENAI_SYSTEM_PROMPT", "").strip()
                or DEFAULT_SYSTEM_PROMPT
            ),
            max_output_tokens=parse_max_output_tokens(
                os.getenv("AZURE_OPENAI_MAX_OUTPUT_TOKENS")
            ),
        )

    async def complete(self, messages: Sequence[ModelMessage]) -> AsyncIterator[str]:
        """Stream response text and require an explicit successful terminal event."""
        completed = False
        with _tracer.start_as_current_span(
            "chat",
            kind=SpanKind.CLIENT,
            attributes={
                "gen_ai.operation.name": "chat",
                "gen_ai.provider.name": "Azure OpenAI",
                "gen_ai.request.model": self.model_name,
                "server.address": self.server_address,
            },
        ) as span:
            try:
                stream = await self._client.responses.create(
                    model=self.model_name,
                    instructions=self._system_prompt,
                    input=[
                        {"role": message.role, "content": message.content}
                        for message in messages
                    ],
                    max_output_tokens=self._max_output_tokens,
                    store=False,
                    stream=True,
                )
                async with stream:
                    async for event in stream:
                        if event.type == "response.output_text.delta" and event.delta:
                            yield event.delta
                        elif event.type == "response.completed":
                            completed = True
                            break
                        elif event.type in {"error", "response.failed", "response.incomplete"}:
                            raise RuntimeError("Foundry model response did not complete")
                if not completed:
                    raise RuntimeError("Foundry model response stream ended before completion")
            except asyncio.CancelledError:
                raise
            except BaseException as exc:
                span.set_status(Status(StatusCode.ERROR))
                span.set_attribute(
                    "error.type", f"{type(exc).__module__}.{type(exc).__qualname__}"
                )
                raise

    async def close(self) -> None:
        """Close the model transport and managed credential."""
        errors: list[BaseException] = []
        try:
            await self._client.close()
        except BaseException as exc:
            errors.append(exc)
        if self._credential is not None:
            try:
                await self._credential.close()
            except BaseException as exc:
                errors.append(exc)
        if len(errors) == 1:
            raise errors[0]
        if errors:
            raise BaseExceptionGroup("Failed to close model resources", errors)