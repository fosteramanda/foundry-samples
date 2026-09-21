"""Tests for the LangChain agent adapter and Foundry configuration."""

from __future__ import annotations

import asyncio
from collections.abc import AsyncIterator
from types import SimpleNamespace
from typing import Any

import pytest
from langchain.agents import create_agent
from langchain_core.language_models.fake_chat_models import (
    FakeListChatModel,
    FakeMessagesListChatModel,
)
from langchain_core.messages import AIMessage, AIMessageChunk, ToolMessage
from langchain_core.outputs import ChatGenerationChunk
from langchain_core.tools import tool
from langchain_openai import ChatOpenAI

import langchain_agent
from langchain_agent import (
    LangChainAgentClient,
    TerminalAwareChatOpenAI,
    get_current_utc_time,
    parse_max_output_tokens,
    responses_base_url,
)
from main import create_app
from state import ModelMessage


class FakeGraph:
    def __init__(self, events: list[tuple[Any, dict[str, Any]]]) -> None:
        self._events = events
        self.input: dict[str, Any] | None = None
        self.stream_mode: str | None = None
        self.closed = False

    async def astream(
        self,
        input: dict[str, Any],
        *,
        stream_mode: str,
    ) -> AsyncIterator[tuple[Any, dict[str, Any]]]:
        self.input = input
        self.stream_mode = stream_mode
        try:
            for event in self._events:
                yield event
        finally:
            self.closed = True


class FakeRootAsyncClient:
    def __init__(self, close_error: BaseException | None = None) -> None:
        self.closed = False
        self.close_error = close_error

    async def close(self) -> None:
        self.closed = True
        if self.close_error is not None:
            raise self.close_error


class FakeModel:
    def __init__(self) -> None:
        self.root_async_client = FakeRootAsyncClient()


class FakeCredential:
    def __init__(self) -> None:
        self.closed = False
        self.scopes: list[str] = []

    async def get_token(self, scope: str) -> Any:
        self.scopes.append(scope)
        return SimpleNamespace(token="entra-token")

    async def close(self) -> None:
        self.closed = True


class ToolCallingFakeModel(FakeMessagesListChatModel):
    def bind_tools(self, tools: Any, **kwargs: Any) -> "ToolCallingFakeModel":
        del tools, kwargs
        return self


def test_normalizes_project_responses_url() -> None:
    assert (
        responses_base_url("https://example.test/api/projects/demo")
        == "https://example.test/api/projects/demo/openai/v1/"
    )


def test_app_accepts_injected_backend() -> None:
    model = LangChainAgentClient(
        agent=FakeGraph([]),
        model=None,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )
    host = create_app(model, configure_observability=None)

    assert host.state.voice_runtime._model_client is model


@pytest.mark.parametrize(
    "value",
    [
        "http://example.test/api/projects/demo",
        "https://example.test/openai/v1/",
        "https://user:secret@example.test/api/projects/demo",
        "https://example.test/api/projects/demo?secret=value",
        "https://example.test/prefix/api/projects/demo",
        "https://example.test/api/projects/demo/extra",
    ],
)
def test_rejects_invalid_endpoint(value: str) -> None:
    with pytest.raises(ValueError, match="FOUNDRY_PROJECT_ENDPOINT"):
        responses_base_url(value)


@pytest.mark.parametrize(
    ("value", "expected"),
    [(None, 512), ("", 512), ("1", 1), ("4096", 4096)],
)
def test_parses_output_tokens(value: str | None, expected: int) -> None:
    assert parse_max_output_tokens(value) == expected


@pytest.mark.asyncio
async def test_streams_only_assistant_text_from_tool_loop() -> None:
    graph = FakeGraph(
        [
            (
                AIMessageChunk(
                    content="",
                    tool_call_chunks=[
                        {
                            "name": "get_current_utc_time",
                            "args": "",
                            "id": "call_1",
                            "index": 0,
                        }
                    ],
                ),
                {"langgraph_node": "model"},
            ),
            (
                ToolMessage(
                    content="2026-08-19T00:00:00Z",
                    tool_call_id="call_1",
                ),
                {"langgraph_node": "tools"},
            ),
            (AIMessageChunk(content="It is midnight "), {"langgraph_node": "model"}),
            (AIMessageChunk(content="UTC."), {"langgraph_node": "model"}),
            (
                AIMessageChunk(
                    content="",
                    response_metadata={"status": "completed"},
                    chunk_position="last",
                ),
                {"langgraph_node": "model"},
            ),
        ]
    )
    client = LangChainAgentClient(
        agent=graph,
        model=None,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )

    chunks = [
        chunk
        async for chunk in client.complete(
            [
                ModelMessage("user", "Remember this."),
                ModelMessage("assistant", "I will."),
                ModelMessage("user", "What time is it?"),
            ]
        )
    ]

    assert chunks == ["It is midnight ", "UTC."]
    assert graph.stream_mode == "messages"
    assert graph.input == {
        "messages": [
            {"role": "user", "content": "Remember this."},
            {"role": "assistant", "content": "I will."},
            {"role": "user", "content": "What time is it?"},
        ]
    }


@pytest.mark.asyncio
async def test_real_create_agent_streams_through_adapter() -> None:
    graph = create_agent(
        FakeListChatModel(responses=["Hello"]),
        tools=[],
        system_prompt="Be concise.",
    )
    client = LangChainAgentClient(
        agent=graph,
        model=None,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )

    chunks = [chunk async for chunk in client.complete([ModelMessage("user", "Hello")])]

    assert chunks == list("Hello")


@pytest.mark.asyncio
async def test_responses_model_stops_and_closes_after_terminal_chunk(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    model_calls = 0
    closed_calls: list[int] = []
    consumed_after_terminal = False

    async def fake_astream(
        self: ChatOpenAI,
        *args: Any,
        **kwargs: Any,
    ) -> AsyncIterator[ChatGenerationChunk]:
        del self, args, kwargs
        nonlocal consumed_after_terminal, model_calls
        model_calls += 1
        current_call = model_calls
        try:
            if current_call == 1:
                yield ChatGenerationChunk(
                    message=AIMessageChunk(
                        content="",
                        tool_call_chunks=[
                            {
                                "name": "get_current_utc_time",
                                "args": "{}",
                                "id": "call_1",
                                "index": 0,
                            }
                        ],
                    )
                )
            else:
                yield ChatGenerationChunk(message=AIMessageChunk(content="Tool completed."))
            yield ChatGenerationChunk(
                message=AIMessageChunk(
                    content="",
                    response_metadata={"status": "completed"},
                    chunk_position="last",
                )
            )
            consumed_after_terminal = True
            yield ChatGenerationChunk(message=AIMessageChunk(content="late"))
        finally:
            closed_calls.append(current_call)

    monkeypatch.setattr(ChatOpenAI, "_astream", fake_astream)
    model = TerminalAwareChatOpenAI(
        model="fake",
        api_key="development-key",
        base_url="https://example.test/openai/v1/",
    )
    graph = create_agent(model, tools=[get_current_utc_time])
    client = LangChainAgentClient(
        agent=graph,
        model=model,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )

    try:
        chunks = [
            chunk
            async for chunk in client.complete([ModelMessage("user", "What time is it?")])
        ]

        assert chunks == ["Tool completed."]
        assert model_calls == 2
        assert closed_calls == [1, 2]
        assert not consumed_after_terminal
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_real_agent_cancellation_reaches_async_tool() -> None:
    started = asyncio.Event()
    cancelled = asyncio.Event()

    @tool
    async def wait_for_signal() -> str:
        """Wait until an external signal arrives."""
        started.set()
        try:
            await asyncio.Future()
        except asyncio.CancelledError:
            cancelled.set()
            raise

    model = ToolCallingFakeModel(
        responses=[
            AIMessage(
                content="",
                tool_calls=[
                    {
                        "name": "wait_for_signal",
                        "args": {},
                        "id": "call_1",
                        "type": "tool_call",
                    }
                ],
            )
        ]
    )
    graph = create_agent(model, tools=[wait_for_signal])
    task = asyncio.create_task(graph.ainvoke({"messages": [{"role": "user", "content": "Wait"}]}))
    await asyncio.wait_for(started.wait(), timeout=2)

    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task

    assert cancelled.is_set()


@pytest.mark.asyncio
async def test_incomplete_model_response_fails() -> None:
    graph = FakeGraph(
        [
            (AIMessageChunk(content="partial"), {"langgraph_node": "model"}),
            (
                AIMessageChunk(
                    content="",
                    response_metadata={
                        "status": "incomplete",
                        "incomplete_details": {"reason": "max_output_tokens"},
                    },
                    chunk_position="last",
                ),
                {"langgraph_node": "model"},
            ),
        ]
    )
    client = LangChainAgentClient(
        agent=graph,
        model=None,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )

    with pytest.raises(RuntimeError, match="ended with incomplete"):
        _ = [chunk async for chunk in client.complete([ModelMessage("user", "Continue")])]


@pytest.mark.asyncio
async def test_stream_without_terminal_fails() -> None:
    graph = FakeGraph([(AIMessageChunk(content="partial"), {"langgraph_node": "model"})])
    client = LangChainAgentClient(
        agent=graph,
        model=None,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )

    with pytest.raises(RuntimeError, match="ended before model completion"):
        _ = [chunk async for chunk in client.complete([ModelMessage("user", "Continue")])]


@pytest.mark.asyncio
async def test_closing_consumer_closes_active_agent_stream() -> None:
    graph = FakeGraph(
        [
            (AIMessageChunk(content="first"), {"langgraph_node": "model"}),
            (AIMessageChunk(content="second"), {"langgraph_node": "model"}),
        ]
    )
    client = LangChainAgentClient(
        agent=graph,
        model=None,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )
    stream = client.complete([ModelMessage("user", "Hello")])

    assert await anext(stream) == "first"
    assert not graph.closed
    await stream.aclose()

    assert graph.closed


@pytest.mark.asyncio
async def test_statusless_responses_last_chunk_fails() -> None:
    graph = FakeGraph(
        [
            (
                AIMessageChunk(
                    content="partial",
                    response_metadata={"id": "resp_1"},
                    chunk_position="last",
                ),
                {"langgraph_node": "model"},
            )
        ]
    )
    client = LangChainAgentClient(
        agent=graph,
        model=None,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )

    with pytest.raises(RuntimeError, match="ended before model completion"):
        _ = [chunk async for chunk in client.complete([ModelMessage("user", "Continue")])]


@pytest.mark.asyncio
async def test_metadata_free_generic_last_chunk_succeeds() -> None:
    graph = FakeGraph(
        [
            (
                AIMessageChunk(content="complete", chunk_position="last"),
                {"langgraph_node": "model"},
            )
        ]
    )
    client = LangChainAgentClient(
        agent=graph,
        model=None,
        credential=None,
        model_name="fake",
        server_address="fake.example",
    )

    chunks = [chunk async for chunk in client.complete([ModelMessage("user", "Continue")])]
    assert chunks == ["complete"]


def test_time_tool_returns_utc_iso_timestamp() -> None:
    result = get_current_utc_time.invoke({})
    assert result.endswith("Z")
    assert "T" in result


@pytest.mark.asyncio
async def test_from_environment_builds_responses_agent(monkeypatch: pytest.MonkeyPatch) -> None:
    captured: dict[str, Any] = {}
    graph = FakeGraph([])

    def fake_create_agent(model: Any, tools: list[Any], **kwargs: Any) -> FakeGraph:
        captured.update(model=model, tools=tools, kwargs=kwargs)
        return graph

    monkeypatch.setattr(langchain_agent, "create_agent", fake_create_agent)
    monkeypatch.setenv(
        "FOUNDRY_PROJECT_ENDPOINT",
        "https://example.test/api/projects/demo",
    )
    monkeypatch.setenv("AZURE_AI_MODEL_DEPLOYMENT_NAME", "deployment")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "development-key")
    monkeypatch.setenv("AZURE_OPENAI_MAX_OUTPUT_TOKENS", "64")

    client = LangChainAgentClient.from_environment()

    assert client.model_name == "deployment"
    assert client.server_address == "example.test"
    model = captured["model"]
    assert isinstance(model, TerminalAwareChatOpenAI)
    assert model.use_responses_api is True
    assert model.store is False
    assert model._default_params["max_completion_tokens"] == 64
    assert str(model.root_async_client.base_url) == (
        "https://example.test/api/projects/demo/openai/v1/"
    )
    assert await model.openai_api_key() == "development-key"
    assert [item.name for item in captured["tools"]] == ["get_current_utc_time"]
    assert captured["kwargs"]["name"] == "voice_assistant"
    await client.close()


@pytest.mark.asyncio
async def test_managed_identity_provider_uses_foundry_scope(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    credential = FakeCredential()
    monkeypatch.setattr(langchain_agent, "DefaultAzureCredential", lambda: credential)
    monkeypatch.setenv(
        "FOUNDRY_PROJECT_ENDPOINT",
        "https://example.test/api/projects/demo",
    )
    monkeypatch.setenv("AZURE_AI_MODEL_DEPLOYMENT_NAME", "deployment")
    monkeypatch.delenv("AZURE_OPENAI_API_KEY", raising=False)

    client = LangChainAgentClient.from_environment()
    assert client._model is not None
    assert await client._model.openai_api_key() == "entra-token"
    assert credential.scopes == ["https://ai.azure.com/.default"]

    await client.close()
    assert credential.closed


@pytest.mark.asyncio
async def test_close_releases_transport_and_credential() -> None:
    model = FakeModel()
    credential = FakeCredential()
    client = LangChainAgentClient(
        agent=FakeGraph([]),
        model=model,  # type: ignore[arg-type]
        credential=credential,  # type: ignore[arg-type]
        model_name="fake",
        server_address="fake.example",
    )

    await client.close()

    assert model.root_async_client.closed
    assert credential.closed


@pytest.mark.asyncio
async def test_close_attempts_credential_after_transport_failure() -> None:
    model = FakeModel()
    model.root_async_client = FakeRootAsyncClient(RuntimeError("transport close failed"))
    credential = FakeCredential()
    client = LangChainAgentClient(
        agent=FakeGraph([]),
        model=model,  # type: ignore[arg-type]
        credential=credential,  # type: ignore[arg-type]
        model_name="fake",
        server_address="fake.example",
    )

    with pytest.raises(RuntimeError, match="transport close failed"):
        await client.close()

    assert model.root_async_client.closed
    assert credential.closed
