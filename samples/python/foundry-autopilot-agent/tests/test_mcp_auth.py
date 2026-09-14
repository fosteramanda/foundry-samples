import unittest
from unittest.mock import AsyncMock, Mock, call, patch

import httpx
from openai import APIStatusError

from hello_world_a365_agent.agent import (
    MCP_SCOPE,
    FoundryDigitalWorkerAgent,
    _rate_limit_retry_delay,
)


class McpAuthenticationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.agent = FoundryDigitalWorkerAgent.__new__(FoundryDigitalWorkerAgent)
        self.agent._mcp_servers = [
            {
                "mcpServerName": "configured-tool",
                "url": "https://example.invalid/mcp",
            }
        ]
        self.agent._openai_client = Mock()
        self.agent._openai_client.responses.create = AsyncMock()
        self.agent._acquire_mcp_token = AsyncMock()
        self.agent._deployment = "test-model"
        self.agent._load_previous_response_id = Mock(return_value=None)
        self.agent._save_response_id = Mock()

    @staticmethod
    def _model_error(status, headers=None):
        return APIStatusError(
            "Model request failed",
            response=httpx.Response(
                status,
                headers=headers,
                request=httpx.Request("POST", "https://example.invalid/responses"),
            ),
            body=None,
        )

    async def test_missing_token_stops_before_model_request(self):
        self.agent._acquire_mcp_token.return_value = None

        with self.assertRaisesRegex(RuntimeError, "Cannot authenticate"):
            await self.agent._invoke_responses_api(
                input_text="hello",
                conversation_id="test-conversation",
                instructions="test instructions",
                auth=Mock(),
                auth_handler_name="AGENTIC",
                context=Mock(),
            )

        self.agent._openai_client.responses.create.assert_not_awaited()

    async def test_authenticated_tool_retains_authorization(self):
        self.agent._acquire_mcp_token.return_value = "unit-test-placeholder"

        tools = await self.agent._build_mcp_tools(Mock(), "AGENTIC", Mock())

        self.assertEqual(len(tools), 1)
        self.assertEqual(tools[0]["server_label"], "configured-tool")
        self.assertEqual(tools[0]["server_url"], "https://example.invalid/mcp")
        self.assertEqual(
            tools[0]["headers"], {"Authorization": "Bearer unit-test-placeholder"}
        )
        self.assertEqual(tools[0]["require_approval"], "never")

    async def test_no_tools_does_not_require_token(self):
        self.agent._mcp_servers = []

        tools = await self.agent._build_mcp_tools(Mock(), "AGENTIC", Mock())

        self.assertEqual(tools, [])
        self.agent._acquire_mcp_token.assert_not_awaited()

    async def test_tokens_are_acquired_for_each_distinct_scope(self):
        ado_scope = "https://mcp.dev.azure.com/.default"
        self.agent._mcp_servers.append(
            {
                "mcpServerName": "ado-tool",
                "url": "https://example.invalid/ado",
                "tokenScope": ado_scope,
            }
        )
        self.agent._acquire_mcp_token.side_effect = [
            "unit-test-default",
            "unit-test-ado",
        ]
        auth, context = Mock(), Mock()

        tools = await self.agent._build_mcp_tools(auth, "AGENTIC", context)

        self.agent._acquire_mcp_token.assert_has_awaits(
            [
                call(auth, "AGENTIC", context, scope=MCP_SCOPE),
                call(auth, "AGENTIC", context, scope=ado_scope),
            ]
        )
        self.assertEqual(
            tools[0]["headers"]["Authorization"], "Bearer unit-test-default"
        )
        self.assertEqual(
            tools[1]["headers"]["Authorization"], "Bearer unit-test-ado"
        )

    async def test_missing_second_scope_stops_before_model_request(self):
        self.agent._mcp_servers.append(
            {
                "mcpServerName": "ado-tool",
                "url": "https://example.invalid/ado",
                "tokenScope": "https://mcp.dev.azure.com/.default",
            }
        )
        self.agent._acquire_mcp_token.side_effect = ["unit-test-placeholder", None]

        with self.assertRaisesRegex(RuntimeError, "Cannot authenticate"):
            await self.agent._invoke_responses_api(
                input_text="hello",
                conversation_id="test-conversation",
                instructions="test instructions",
                auth=Mock(),
                auth_handler_name="AGENTIC",
                context=Mock(),
            )

        self.agent._openai_client.responses.create.assert_not_awaited()

    async def test_servers_with_the_same_scope_share_a_token(self):
        self.agent._mcp_servers.append(
            {
                "mcpServerName": "another-tool",
                "url": "https://example.invalid/another",
            }
        )
        self.agent._acquire_mcp_token.return_value = "unit-test-placeholder"
        auth, context = Mock(), Mock()

        tools = await self.agent._build_mcp_tools(auth, "AGENTIC", context)

        self.assertEqual(len(tools), 2)
        self.agent._acquire_mcp_token.assert_awaited_once_with(
            auth, "AGENTIC", context, scope=MCP_SCOPE
        )

    async def test_model_429_retries_after_server_delay(self):
        self.agent._mcp_servers = []
        response = Mock(output_text="ready")
        response.model_dump.return_value = {
            "id": "response-test",
            "output_text": "ready",
        }
        self.agent._openai_client.responses.create.side_effect = [
            self._model_error(429, {"retry-after": "3"}),
            response,
        ]

        with (
            patch(
                "hello_world_a365_agent.agent.asyncio.sleep", new_callable=AsyncMock
            ) as sleep,
            patch("hello_world_a365_agent.agent.set_current_span_response") as trace,
        ):
            result = await self.agent._invoke_responses_api(
                input_text="hello",
                conversation_id="test-conversation",
                instructions="test instructions",
                auth=Mock(),
                auth_handler_name="AGENTIC",
                context=Mock(),
            )

        self.assertEqual(result, "ready")
        self.assertEqual(self.agent._openai_client.responses.create.await_count, 2)
        sleep.assert_awaited_once_with(3.0)
        trace.assert_called_once_with("response-test", "ready")

    async def test_model_retry_limit_is_three_requests(self):
        self.agent._mcp_servers = []
        self.agent._openai_client.responses.create.side_effect = [
            self._model_error(429) for _ in range(3)
        ]

        with patch(
            "hello_world_a365_agent.agent.asyncio.sleep", new_callable=AsyncMock
        ) as sleep:
            result = await self.agent._invoke_responses_api(
                input_text="hello",
                conversation_id="test-conversation",
                instructions="test instructions",
                auth=Mock(),
                auth_handler_name="AGENTIC",
                context=Mock(),
            )

        self.assertIn("Status: 429", result)
        self.assertEqual(self.agent._openai_client.responses.create.await_count, 3)
        self.assertEqual(sleep.await_args_list, [call(2.0), call(4.0)])

    async def test_long_server_delay_does_not_retry(self):
        self.agent._mcp_servers = []
        self.agent._openai_client.responses.create.side_effect = [
            self._model_error(429, {"retry-after": "120"})
        ]

        with patch(
            "hello_world_a365_agent.agent.asyncio.sleep", new_callable=AsyncMock
        ) as sleep:
            result = await self.agent._invoke_responses_api(
                input_text="hello",
                conversation_id="test-conversation",
                instructions="test instructions",
                auth=Mock(),
                auth_handler_name="AGENTIC",
                context=Mock(),
            )

        self.assertIn("Status: 429", result)
        self.agent._openai_client.responses.create.assert_awaited_once()
        sleep.assert_not_awaited()

    async def test_non_rate_limit_errors_are_not_retried(self):
        self.agent._mcp_servers = []
        self.agent._openai_client.responses.create.side_effect = [
            self._model_error(500)
        ]

        with patch(
            "hello_world_a365_agent.agent.asyncio.sleep", new_callable=AsyncMock
        ) as sleep:
            result = await self.agent._invoke_responses_api(
                input_text="hello",
                conversation_id="test-conversation",
                instructions="test instructions",
                auth=Mock(),
                auth_handler_name="AGENTIC",
                context=Mock(),
            )

        self.assertIn("Status: 500", result)
        self.agent._openai_client.responses.create.assert_awaited_once()
        sleep.assert_not_awaited()

    def test_sdk_retries_do_not_multiply_explicit_retries(self):
        with (
            patch.dict(
                "os.environ",
                {
                    "FOUNDRY_PROJECT_ENDPOINT": "https://example.invalid/api/projects/test",
                    "AZURE_OPENAI_DEPLOYMENT": "test-model",
                },
            ),
            patch.object(
                FoundryDigitalWorkerAgent, "_build_credential", return_value=Mock()
            ),
            patch.object(
                FoundryDigitalWorkerAgent, "_load_mcp_servers", return_value=[]
            ),
            patch("hello_world_a365_agent.agent.AIProjectClient") as project,
        ):
            FoundryDigitalWorkerAgent()

        project.return_value.get_openai_client.assert_called_once_with(max_retries=0)

    def test_retry_delay_supports_milliseconds(self):
        response = httpx.Response(429, headers={"retry-after-ms": "2500"})
        self.assertEqual(_rate_limit_retry_delay(response, 0), 2.5)

    def test_retry_does_not_shorten_a_long_server_delay(self):
        response = httpx.Response(429, headers={"retry-after": "120"})
        self.assertIsNone(_rate_limit_retry_delay(response, 0))

    def test_retry_delay_accepts_the_sixty_second_boundary(self):
        response = httpx.Response(429, headers={"retry-after": "60"})
        self.assertEqual(_rate_limit_retry_delay(response, 0), 60.0)

    def test_invalid_retry_delay_uses_explicit_backoff(self):
        response = httpx.Response(429, headers={"retry-after": "invalid"})
        self.assertEqual(_rate_limit_retry_delay(response, 0), 2.0)


if __name__ == "__main__":
    unittest.main()
