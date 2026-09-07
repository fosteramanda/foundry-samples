"""LangGraph model adapter for the Voice Live Bridge hosted-agent sample."""

from __future__ import annotations

import os
from collections.abc import AsyncIterator, Awaitable, Callable, Sequence
from contextlib import aclosing
from datetime import UTC, datetime
from typing import Annotated, Any, Protocol, TypedDict
from urllib.parse import urlsplit, urlunsplit

from azure.identity.aio import DefaultAzureCredential
from langchain_core.language_models.chat_models import BaseChatModel
from langchain_core.messages import AIMessageChunk, AnyMessage, SystemMessage
from langchain_core.outputs import ChatGenerationChunk
from langchain_core.tools import BaseTool, tool
from langchain_openai import ChatOpenAI
from langgraph.graph import END, START, StateGraph
from langgraph.graph.message import add_messages
from langgraph.prebuilt import ToolNode, tools_condition

from model_contract import StreamingModelClient
from state import ModelMessage

FOUNDRY_TOKEN_SCOPE = "https://ai.azure.com/.default"
DEFAULT_SYSTEM_PROMPT = (
    "You are a concise voice assistant. Answer naturally in plain text without markdown. "
    "Use tools when they help, and never read tool protocol details aloud."
)
DEFAULT_MAX_OUTPUT_TOKENS = 512
MAX_OUTPUT_TOKENS = 4096


class VoiceGraphState(TypedDict):
    """Conversation state passed between the model and tool nodes."""

    messages: Annotated[list[AnyMessage], add_messages]


class CompiledVoiceGraph(Protocol):
    """Subset of a compiled LangGraph used by the Voice runtime adapter."""

    def astream(
        self,
        input: dict[str, Any],
        *,
        stream_mode: str,
    ) -> AsyncIterator[tuple[Any, dict[str, Any]]]: ...


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


@tool
def get_current_utc_time() -> str:
    """Return the current UTC date and time in ISO 8601 format."""
    return datetime.now(UTC).isoformat().replace("+00:00", "Z")


class TerminalAwareChatOpenAI(ChatOpenAI):
    """End each Responses HTTP stream at its explicit terminal chunk."""

    async def _astream(self, *args: Any, **kwargs: Any) -> AsyncIterator[ChatGenerationChunk]:
        async with aclosing(super()._astream(*args, **kwargs)) as stream:
            async for chunk in stream:
                yield chunk
                if chunk.message.response_metadata.get("status") is not None:
                    break


def build_voice_graph(
    model: BaseChatModel,
    *,
    tools: Sequence[BaseTool],
    system_prompt: str,
) -> CompiledVoiceGraph:
    """Compile an explicit model -> tools -> model LangGraph workflow."""
    model_with_tools = model.bind_tools(tools)

    async def call_model(state: VoiceGraphState) -> dict[str, list[AnyMessage]]:
        response = await model_with_tools.ainvoke(
            [SystemMessage(content=system_prompt), *state["messages"]]
        )
        return {"messages": [response]}

    graph = StateGraph(VoiceGraphState)
    graph.add_node("model", call_model)
    graph.add_node("tools", ToolNode(tools))
    graph.add_edge(START, "model")
    graph.add_conditional_edges("model", tools_condition, {"tools": "tools", "__end__": END})
    graph.add_edge("tools", "model")
    return graph.compile()


class LangGraphAgentClient(StreamingModelClient):
    """Streaming LangGraph agent backed by the Foundry Responses endpoint."""

    def __init__(
        self,
        *,
        graph: CompiledVoiceGraph,
        model: ChatOpenAI | None,
        credential: DefaultAzureCredential | None,
        model_name: str,
        server_address: str,
    ) -> None:
        self._graph = graph
        self._model = model
        self._credential = credential
        self.model_name = model_name
        self.server_address = server_address

    @classmethod
    def from_environment(cls) -> LangGraphAgentClient:
        """Create an explicit LangGraph workflow from Hosted Agent settings."""
        endpoint = os.getenv("FOUNDRY_PROJECT_ENDPOINT", "").strip()
        deployment = os.getenv("AZURE_AI_MODEL_DEPLOYMENT_NAME", "").strip()
        if not endpoint or not deployment:
            raise ValueError(
                "FOUNDRY_PROJECT_ENDPOINT and AZURE_AI_MODEL_DEPLOYMENT_NAME are required"
            )

        base_url = responses_base_url(endpoint)
        api_key = os.getenv("AZURE_OPENAI_API_KEY", "").strip()
        credential: DefaultAzureCredential | None = None
        token_provider: Callable[[], Awaitable[str]]
        if api_key:

            async def configured_api_key() -> str:
                return api_key

            token_provider = configured_api_key
        else:
            credential = DefaultAzureCredential()

            async def entra_token() -> str:
                return (await credential.get_token(FOUNDRY_TOKEN_SCOPE)).token

            token_provider = entra_token

        model = TerminalAwareChatOpenAI(
            model=deployment,
            base_url=base_url,
            api_key=token_provider,
            use_responses_api=True,
            streaming=True,
            store=False,
            max_completion_tokens=parse_max_output_tokens(
                os.getenv("AZURE_OPENAI_MAX_OUTPUT_TOKENS")
            ),
        )
        graph = build_voice_graph(
            model,
            tools=[get_current_utc_time],
            system_prompt=(
                os.getenv("AZURE_OPENAI_SYSTEM_PROMPT", "").strip() or DEFAULT_SYSTEM_PROMPT
            ),
        )
        return cls(
            graph=graph,
            model=model,
            credential=credential,
            model_name=deployment,
            server_address=urlsplit(base_url).hostname or "",
        )

    async def complete(self, messages: Sequence[ModelMessage]) -> AsyncIterator[str]:
        """Stream assistant text and require the final model response to complete."""
        completed = False
        stream = self._graph.astream(
            {"messages": [{"role": item.role, "content": item.content} for item in messages]},
            stream_mode="messages",
        )
        async with aclosing(stream):
            async for message, metadata in stream:
                if metadata.get("langgraph_node") != "model" or not isinstance(
                    message, AIMessageChunk
                ):
                    continue
                status = message.response_metadata.get("status")
                if status is not None:
                    if status != "completed":
                        raise RuntimeError(f"LangGraph model response ended with {status}")
                    completed = True
                elif message.response_metadata.get("id"):
                    completed = False
                elif message.chunk_position == "last":
                    completed = True
                elif message.text:
                    completed = False
                if message.text:
                    yield message.text
        if not completed:
            raise RuntimeError("LangGraph agent stream ended before model completion")

    async def close(self) -> None:
        """Close the OpenAI transport and managed credential."""
        errors: list[BaseException] = []
        if self._model is not None and self._model.root_async_client is not None:
            try:
                await self._model.root_async_client.close()
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
            raise BaseExceptionGroup("Failed to close LangGraph agent resources", errors)