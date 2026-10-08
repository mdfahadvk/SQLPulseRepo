"""Tests for the AI investigation agent.

Covers:
  - Agent chooses server-health tool
  - Agent chooses database-health tool
  - Timeout investigation
  - Regression investigation
  - Insufficient evidence
  - Tool failure
  - Query Store unavailable
  - Maximum tool-call limit
  - No arbitrary SQL execution
  - No remediation
  - No unsupported technical claims
  - Sanitization
  - LLM provider abstraction
  - Structured response parsing
  - API endpoint
"""

import json
import time
from datetime import UTC, datetime
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from app.agent.engine import _parse_structured_answer, investigate, SYSTEM_PROMPT
from app.agent.llm_provider import (
    LLMMessage,
    LLMPayloadTooLargeError,
    LLMProvider,
    LLMRateLimitError,
    LLMResponse,
    OpenAIProvider,
    ToolDefinition,
    _translate_provider_error,
    create_provider,
)
from app.agent.sanitizer import (
    sanitize_dict,
    sanitize_for_llm,
    sanitize_text,
    serialize_for_llm,
)
from app.agent.tools import (
    APPROVED_TOOL_NAMES,
    TOOL_DEFINITIONS,
    TOOL_MAP,
    execute_tool,
)
from app.models.agent import InvestigationRequest, InvestigationResponse, ToolCall


# ─── Mock LLM Provider ───────────────────────────────────────────────────


class MockLLMProvider(LLMProvider):
    """Test-only LLM provider that returns scripted responses."""

    def __init__(self, responses: list[LLMResponse] | None = None):
        self._responses = list(responses or [])
        self._call_count = 0
        self.messages_received: list[list[LLMMessage]] = []

    async def generate(
        self,
        messages: list[LLMMessage],
        tools: list[ToolDefinition] | None = None,
        temperature: float = 0.1,
        max_tokens: int = 4096,
    ) -> LLMResponse:
        self.messages_received.append(messages)
        if self._call_count < len(self._responses):
            resp = self._responses[self._call_count]
        else:
            resp = LLMResponse(content="No more scripted responses.")
        self._call_count += 1
        return resp


# ─── Helpers ──────────────────────────────────────────────────────────────


def _make_tool_call_response(tool_name: str, arguments: dict, tc_id: str = "tc_1") -> LLMResponse:
    """Create an LLMResponse that requests a single tool call."""
    return LLMResponse(
        content=None,
        tool_calls=[{
            "id": tc_id,
            "function": {
                "name": tool_name,
                "arguments": json.dumps(arguments),
            },
        }],
        finish_reason="tool_calls",
    )


def _make_final_response(text: str) -> LLMResponse:
    """Create a final text-only LLMResponse."""
    return LLMResponse(content=text, tool_calls=[], finish_reason="stop")


# ─── Test: LLM Provider Abstraction ──────────────────────────────────────


class TestLLMProviderAbstraction:
    """Verify the LLM provider interface and factory."""

    def test_create_openai_provider(self):
        """OpenAI provider can be created with a valid API key."""
        provider = create_provider("openai", api_key="test-key-123")
        assert isinstance(provider, LLMProvider)

    def test_create_groq_provider(self):
        """Groq provider can be created and uses the Groq base URL."""
        provider = create_provider("groq", api_key="gsk-test-key", model="llama-3.3-70b-versatile")
        assert isinstance(provider, LLMProvider)

    def test_create_unsupported_provider(self):
        with pytest.raises(ValueError, match="Unsupported"):
            create_provider("anthropic", api_key="test-key")

    def test_mock_provider_works(self):
        provider = MockLLMProvider([_make_final_response("hello")])
        assert isinstance(provider, LLMProvider)

    def test_tool_definition_schema(self):
        td = ToolDefinition(
            name="test_tool",
            description="A test tool",
            parameters={"type": "object", "properties": {}},
        )
        schema = td.to_openai_schema()
        assert schema["type"] == "function"
        assert schema["function"]["name"] == "test_tool"

    def test_llm_message_to_dict(self):
        msg = LLMMessage(role="user", content="hello")
        d = msg.to_dict()
        assert d["role"] == "user"
        assert d["content"] == "hello"
        assert "tool_calls" not in d

    def test_llm_message_with_tool_calls(self):
        msg = LLMMessage(role="assistant", content=None, tool_calls=[{"id": "1"}])
        d = msg.to_dict()
        assert d["tool_calls"] == [{"id": "1"}]

    def test_llm_response_has_tool_calls(self):
        resp = LLMResponse(content=None, tool_calls=[{"id": "tc"}])
        assert resp.has_tool_calls is True

    def test_llm_response_no_tool_calls(self):
        resp = LLMResponse(content="done")
        assert resp.has_tool_calls is False

    def test_create_openai_provider_no_key(self):
        with pytest.raises(ValueError, match="LLM_API_KEY is required"):
            OpenAIProvider(api_key="")


class _FakeStatusError(Exception):
    def __init__(self, status_code: int, message: str):
        super().__init__(message)
        self.status_code = status_code


class TestProviderErrorTranslation:
    """Provider failures must surface as actionable SQLPulse errors, not raw 500s."""

    def test_413_becomes_payload_too_large(self):
        exc = _FakeStatusError(413, "Request too large for model")
        assert isinstance(_translate_provider_error(exc), LLMPayloadTooLargeError)

    def test_429_token_quota_becomes_payload_too_large(self):
        exc = _FakeStatusError(429, "Rate limit reached on tokens per minute (TPM)")
        assert isinstance(_translate_provider_error(exc), LLMPayloadTooLargeError)

    def test_plain_429_becomes_rate_limit(self):
        exc = _FakeStatusError(429, "Rate limit reached on requests per minute")
        translated = _translate_provider_error(exc)
        assert isinstance(translated, LLMRateLimitError)
        assert not isinstance(translated, LLMPayloadTooLargeError)

    def test_context_length_becomes_payload_too_large(self):
        exc = _FakeStatusError(400, "This model's maximum context length is 8192 tokens")
        assert isinstance(_translate_provider_error(exc), LLMPayloadTooLargeError)

    def test_unrelated_error_passes_through(self):
        exc = _FakeStatusError(401, "Invalid API key")
        assert _translate_provider_error(exc) is exc


# ─── Test: Sanitization ──────────────────────────────────────────────────


class TestSanitization:
    """Verify sensitive data is stripped before reaching the LLM."""

    def test_sanitize_password(self):
        text = "Server=srv;UID=admin;PWD=secret123;"
        result = sanitize_text(text)
        assert "secret123" not in result
        assert "[REDACTED]" in result

    def test_sanitize_api_key_in_text(self):
        text = "api_key = sk-abc123def"
        result = sanitize_text(text)
        assert "sk-abc123def" not in result

    def test_sanitize_dict_password(self):
        data = {"username": "admin", "password": "s3cret", "host": "srv"}
        result = sanitize_dict(data)
        assert result["password"] == "[REDACTED]"
        assert result["username"] == "admin"

    def test_sanitize_dict_nested(self):
        data = {"config": {"sql_password": "hidden", "host": "srv"}}
        result = sanitize_dict(data)
        assert result["config"]["sql_password"] == "[REDACTED]"

    def test_sanitize_dict_max_depth(self):
        data = {"a": {"b": {"c": {"d": {"e": {"f": {"g": "deep"}}}}}}}
        result = sanitize_dict(data, max_depth=3)
        # Should truncate instead of going deeper
        assert "_truncated" not in result  # depth 3 covers a→b→c

    def test_sanitize_for_llm_trims_sql(self):
        data = {"sql_text": "x" * 500}
        result = sanitize_for_llm(data)
        assert len(result["sql_text"]) <= 300

    def test_sanitize_for_llm_trims_nested_sql(self):
        data = {"records": [{"sql_text": "x" * 5000, "database": "Sales"}]}
        result = sanitize_for_llm(data)
        assert len(result["records"][0]["sql_text"]) <= 300
        assert result["records"][0]["database"] == "Sales"

    def test_serialize_for_llm_leaves_small_payload_intact(self):
        data = {"record_count": 2, "records": [{"a": 1}, {"a": 2}]}
        assert json.loads(serialize_for_llm(data, max_chars=5000)) == data

    def test_serialize_for_llm_shrinks_oversized_payload(self):
        data = {
            "total_findings": 200,
            "findings": [{"summary": "y" * 100} for _ in range(200)],
        }
        payload = serialize_for_llm(data, max_chars=2000)
        assert len(payload) <= 2000
        parsed = json.loads(payload)
        # Scalar context is preserved so the model still knows the real total.
        assert parsed["total_findings"] == 200
        assert parsed["_truncated"] is True
        assert 0 < len(parsed["findings"]) < 200

    def test_sanitize_empty_text(self):
        assert sanitize_text("") == ""
        assert sanitize_text(None) is None

    def test_sanitize_connection_string(self):
        text = "connection string = Server=srv;UID=x;PWD=y;"
        result = sanitize_text(text)
        assert "[REDACTED]" in result


# ─── Test: Agent Tools ───────────────────────────────────────────────────


class TestAgentTools:
    """Verify the tool registry and execution layer."""

    def test_all_tools_registered(self):
        expected = {
            "get_server_health", "get_database_health", "get_active_requests",
            "get_blocking", "get_long_running_queries", "get_top_cpu_queries",
            "get_top_io_queries", "get_query_history", "get_query_baseline",
            "get_query_regressions", "get_slow_history", "get_timeout_candidates",
        }
        assert APPROVED_TOOL_NAMES == expected

    def test_tool_definitions_have_schemas(self):
        for td in TOOL_DEFINITIONS:
            schema = td.to_openai_schema()
            assert schema["type"] == "function"
            assert "name" in schema["function"]
            assert "parameters" in schema["function"]

    def test_unapproved_tool_rejected(self):
        with pytest.raises(KeyError, match="not an approved"):
            execute_tool("drop_database", {"database": "prod"})

    def test_execute_tool_handles_error(self):
        """Tool execution errors return an error dict, not an exception."""
        with patch.dict(
            "app.agent.tools._EXECUTORS",
            {"get_server_health": MagicMock(side_effect=RuntimeError("connection failed"))},
        ):
            result = execute_tool("get_server_health", {})
            assert "error" in result

    def test_no_sql_generation_tool(self):
        """There must be no tool that accepts raw SQL."""
        for td in TOOL_DEFINITIONS:
            params = td.parameters.get("properties", {})
            for pname, pval in params.items():
                desc = pval.get("description", "").lower()
                assert "sql query" not in desc, (
                    f"Tool {td.name} has parameter '{pname}' that suggests SQL input"
                )


# ─── Test: Investigation Engine ──────────────────────────────────────────


@pytest.mark.asyncio
class TestInvestigationEngine:
    """Test the agent loop with mocked LLM and tools."""

    async def test_agent_chooses_server_health(self):
        """Agent calls get_server_health when asked about server performance."""
        mock_tool_result = {
            "overall_status": "HEALTHY",
            "total_findings": 0,
            "findings": [],
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_server_health", {}),
            _make_final_response(
                "Summary: The server is healthy.\n"
                "Severity: low\n"
                "Confidence: high\n"
                "Affected database: N/A\n"
                "Human review required: no\n"
                "Agent action: Observation and recommendation only."
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {"get_server_health": lambda _: mock_tool_result}):
            result = await investigate("Why is the server slow?", provider)

        assert "get_server_health" in result.tools_used
        assert result.tool_call_count >= 1
        assert result.severity == "low"

    async def test_agent_chooses_database_health(self):
        """Agent calls get_database_health for a database-specific question."""
        mock_tool_result = {
            "overall_status": "DEGRADED",
            "total_findings": 1,
            "findings": [{
                "category": "long_running_query",
                "database": "Sales",
                "severity": "high",
                "confidence": "high",
                "summary": "Query running for 45 seconds",
                "evidence": ["Elapsed: 45s"],
                "likely_cause": "Missing index",
                "recommendation": "Review plan",
            }],
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_database_health", {"database": "Sales"}),
            _make_final_response(
                "Summary: Sales database has a long-running query.\n"
                "Severity: high\n"
                "Confidence: high\n"
                "Affected database: Sales\n"
                "Human review required: yes\n"
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {"get_database_health": lambda _: mock_tool_result}):
            result = await investigate("What is affecting Sales?", provider)

        assert "get_database_health" in result.tools_used
        assert result.severity == "high"
        assert result.affected_database == "Sales"

    async def test_timeout_investigation(self):
        """Agent investigates timeout candidates with proper tool sequence."""
        slow_result = {
            "database": "Merchandising",
            "query_store_status": "AVAILABLE",
            "record_count": 1,
            "records": [],
        }
        candidate_result = {
            "database": "Merchandising",
            "query_store_status": "AVAILABLE",
            "candidate_count": 1,
            "candidates": [{
                "label": "timeout_candidate",
                "procedure_name": "dbo.uspExample",
                "max_duration_ms": 47200,
                "avg_duration_ms": 35000,
                "confidence": "high",
                "severity": "critical",
                "baseline_comparison": {
                    "baseline_status": "READY",
                    "historical_mean_ms": 6100,
                    "historical_p95_ms": 9400,
                    "regression_multiplier_vs_mean": 7.8,
                },
            }],
        }
        baseline_result = {
            "query_hash": "abc123",
            "database": "Merchandising",
            "baseline_status": "READY",
            "sample_count": 50,
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_slow_history", {
                "database": "Merchandising",
                "start_time": "2026-08-24T09:00:00Z",
                "end_time": "2026-08-24T11:00:00Z",
            }),
            _make_tool_call_response("get_timeout_candidates", {
                "database": "Merchandising",
                "start_time": "2026-08-24T09:00:00Z",
                "end_time": "2026-08-24T11:00:00Z",
            }),
            _make_final_response(
                "Summary: Merchandising experienced a significant query slowdown.\n"
                "Severity: critical\n"
                "Confidence: high\n"
                "Affected database: Merchandising\n"
                "Affected query/procedure: dbo.uspExample\n"
                "Evidence:\n"
                "- Max duration: 47.2 seconds\n"
                "- Timeout threshold: 30 seconds\n"
                "- Historical mean: 6.1 seconds\n"
                "- Regression: 7.8x slower than historical mean\n"
                "Historical comparison: 7.8x regression vs mean\n"
                "Likely cause: Abnormal performance regression\n"
                "What is known:\n"
                "- dbo.uspExample reached 47.2 seconds\n"
                "- Historical mean is 6.1 seconds\n"
                "What is uncertain:\n"
                "- Whether blocking caused the slowdown\n"
                "- Whether plan regression is involved\n"
                "Recommended investigation:\n"
                "- Review execution plan\n"
                "- Check blocking history\n"
                "Human review required: yes\n"
                "Agent action: Observation and recommendation only."
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {
            "get_slow_history": lambda _: slow_result,
            "get_timeout_candidates": lambda _: candidate_result,
        }):
            result = await investigate(
                "Why was Merchandising slow between 9 AM and 11 AM?", provider
            )

        assert result.severity == "critical"
        assert result.confidence == "high"
        assert result.affected_database == "Merchandising"
        assert "dbo.uspExample" in result.affected_query
        assert len(result.evidence) > 0
        assert result.human_review_required is True
        assert "get_slow_history" in result.tools_used or "get_timeout_candidates" in result.tools_used

    async def test_regression_investigation(self):
        """Agent investigates query regressions."""
        regression_result = {
            "database": "Sales",
            "regression_count": 1,
            "regressions": [{
                "category": "query_regression",
                "severity": "high",
                "summary": "Query xyz regressed 5x",
            }],
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_query_regressions", {"database": "Sales"}),
            _make_final_response(
                "Summary: One query regression detected in Sales.\n"
                "Severity: high\n"
                "Confidence: medium\n"
                "Affected database: Sales\n"
                "Human review required: yes\n"
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {"get_query_regressions": lambda _: regression_result}):
            result = await investigate("Has anything regressed in Sales?", provider)

        assert "get_query_regressions" in result.tools_used
        assert result.severity == "high"

    async def test_insufficient_evidence(self):
        """Agent reports insufficient evidence when tools return empty results."""
        provider = MockLLMProvider([
            _make_tool_call_response("get_server_health", {}),
            _make_final_response(
                "Summary: Insufficient evidence to determine the root cause.\n"
                "Severity: unknown\n"
                "Confidence: low\n"
                "Human review required: yes\n"
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {"get_server_health": lambda _: {
            "overall_status": "HEALTHY", "total_findings": 0, "findings": [],
        }}):
            result = await investigate("Why is something slow?", provider)

        assert result.confidence == "low"
        assert "insufficient" in result.answer.lower() or result.severity == "unknown"

    async def test_tool_failure_handled(self):
        """Agent handles tool execution failure gracefully."""
        provider = MockLLMProvider([
            _make_tool_call_response("get_server_health", {}),
            _make_final_response(
                "Summary: Unable to gather server health data due to a tool error.\n"
                "Severity: unknown\n"
                "Confidence: low\n"
                "Human review required: yes\n"
            ),
        ])

        def _raise(_):
            raise Exception("conn error")

        with patch.dict("app.agent.tools._EXECUTORS", {"get_server_health": _raise}):
            result = await investigate("How is the server?", provider)

        assert result.tool_call_count >= 1
        # Should still produce a response, not crash
        assert result.answer

    async def test_query_store_unavailable(self):
        """Agent handles Query Store being disabled."""
        provider = MockLLMProvider([
            _make_tool_call_response("get_timeout_candidates", {
                "database": "TestDB",
                "start_time": "2026-08-24T09:00:00Z",
                "end_time": "2026-08-24T11:00:00Z",
            }),
            _make_final_response(
                "Summary: Query Store is disabled for TestDB.\n"
                "Severity: unknown\n"
                "Confidence: low\n"
                "Human review required: yes\n"
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {"get_timeout_candidates": lambda _: {
            "database": "TestDB",
            "query_store_status": "DISABLED",
            "candidates": [],
        }}):
            result = await investigate(
                "Are there timeout candidates in TestDB?", provider
            )

        assert result.answer
        assert "disabled" in result.answer.lower() or result.confidence == "low"

    async def test_max_tool_call_limit(self, monkeypatch):
        """Agent stops after hitting the maximum tool-call limit."""
        monkeypatch.setenv("MAX_AGENT_TOOL_CALLS", "2")
        import app.config
        app.config._settings = None

        # Return tool calls indefinitely
        responses = [
            _make_tool_call_response("get_server_health", {}, f"tc_{i}")
            for i in range(20)
        ]
        responses.append(_make_final_response(
            "Summary: Analysis based on limited data.\n"
            "Severity: medium\n"
            "Confidence: low\n"
        ))
        provider = MockLLMProvider(responses)

        with patch.dict("app.agent.tools._EXECUTORS", {"get_server_health": lambda _: {
            "overall_status": "HEALTHY", "total_findings": 0, "findings": [],
        }}):
            result = await investigate("Keep investigating", provider)

        assert result.tool_call_count <= 3  # max_calls=2, each response has 1 call
        app.config._settings = None

    async def test_no_arbitrary_sql_execution(self):
        """Agent must not be able to execute raw SQL."""
        # Try to call a non-existent SQL execution tool
        provider = MockLLMProvider([
            LLMResponse(
                content=None,
                tool_calls=[{
                    "id": "tc_sql",
                    "function": {
                        "name": "execute_sql",
                        "arguments": json.dumps({"query": "DROP TABLE users"}),
                    },
                }],
                finish_reason="tool_calls",
            ),
            _make_final_response(
                "Summary: Cannot execute arbitrary SQL.\n"
                "Severity: unknown\n"
                "Confidence: low\n"
            ),
        ])

        result = await investigate("Run a query", provider)
        # The tool should be rejected
        assert "execute_sql" not in result.tools_used

    async def test_no_remediation(self):
        """System prompt prevents the agent from modifying the database."""
        assert "NEVER modify" in SYSTEM_PROMPT
        assert "read-only" in SYSTEM_PROMPT.lower()
        assert "Do NOT generate SQL" in SYSTEM_PROMPT
        assert "No database remediation was performed" in SYSTEM_PROMPT

    async def test_no_unsupported_claims(self):
        """System prompt instructs the agent not to invent technical facts."""
        assert "Never invent" in SYSTEM_PROMPT
        assert "UNKNOWN / REQUIRES MORE EVIDENCE" in SYSTEM_PROMPT
        assert "TIMEOUT_CANDIDATE" in SYSTEM_PROMPT

    async def test_investigation_response_structure(self):
        """Verify the response model has all required fields."""
        provider = MockLLMProvider([
            _make_final_response(
                "Summary: Nothing found.\nSeverity: low\nConfidence: low\n"
                "Human review required: no\n"
            ),
        ])

        result = await investigate("Test", provider)

        assert result.investigation_id
        assert result.question == "Test"
        assert result.answer
        assert "No database remediation was performed" in result.agent_action
        assert isinstance(result.tools_used, list)
        assert isinstance(result.duration_seconds, float)

    async def test_system_prompt_contains_current_date(self):
        """System prompt sent to LLM must contain a formatted date, not the placeholder."""
        provider = MockLLMProvider([
            _make_final_response("Summary: ok\nSeverity: low\nConfidence: low\n"),
        ])
        await investigate("Test", provider)

        system_msg = provider.messages_received[0][0]
        assert system_msg.role == "system"
        # Placeholder must be resolved
        assert "{current_datetime}" not in system_msg.content
        # Should contain an ISO-like date from the current year
        import re
        assert re.search(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", system_msg.content)


# ─── Test: Structured Response Parsing ────────────────────────────────────


class TestStructuredParsing:
    """Test the extraction of structured fields from LLM free-text."""

    def test_parse_severity(self):
        text = "Severity: critical\nConfidence: high\n"
        result = _parse_structured_answer(text)
        assert result["severity"] == "critical"
        assert result["confidence"] == "high"

    def test_parse_affected_database(self):
        text = "Affected database: Merchandising\n"
        result = _parse_structured_answer(text)
        assert result["affected_database"] == "Merchandising"

    def test_parse_na_ignored(self):
        text = "Affected database: N/A\nAffected query: None\n"
        result = _parse_structured_answer(text)
        assert result.get("affected_database") is None
        assert result.get("affected_query") is None

    def test_parse_evidence_bullets(self):
        text = (
            "Evidence:\n"
            "- Max duration: 47.2 seconds\n"
            "- Threshold: 30 seconds\n"
            "Likely cause: regression\n"
        )
        result = _parse_structured_answer(text)
        assert len(result["evidence"]) == 2
        assert "47.2 seconds" in result["evidence"][0]

    def test_parse_human_review_no(self):
        text = "Human review required: no\n"
        result = _parse_structured_answer(text)
        assert result["human_review_required"] is False

    def test_parse_human_review_yes(self):
        text = "Human review required: yes\n"
        result = _parse_structured_answer(text)
        assert result["human_review_required"] is True


# ─── Test: API Endpoint ──────────────────────────────────────────────────


class TestAgentEndpoint:
    """Test the POST /api/agent/investigate endpoint."""

    @pytest.mark.asyncio
    async def test_endpoint_rejects_without_api_key(self, monkeypatch):
        """Endpoint returns 503 when LLM_API_KEY is not set."""
        monkeypatch.setenv("LLM_API_KEY", "")
        import app.config
        app.config._settings = None

        from httpx import ASGITransport, AsyncClient
        from app.main import app as fastapi_app

        transport = ASGITransport(app=fastapi_app)
        async with AsyncClient(transport=transport, base_url="http://test") as client:
            resp = await client.post(
                "/api/agent/investigate",
                json={"question": "Why is the server slow?"},
            )
        assert resp.status_code == 503
        assert "not configured" in resp.json()["detail"]
        app.config._settings = None

    @pytest.mark.asyncio
    async def test_endpoint_validates_question(self, monkeypatch):
        """Endpoint rejects empty questions."""
        monkeypatch.setenv("LLM_API_KEY", "test-key-123")
        import app.config
        app.config._settings = None

        from httpx import ASGITransport, AsyncClient
        from app.main import app as fastapi_app

        transport = ASGITransport(app=fastapi_app)
        async with AsyncClient(transport=transport, base_url="http://test") as client:
            resp = await client.post(
                "/api/agent/investigate",
                json={"question": "ab"},  # too short (min_length=3)
            )
        assert resp.status_code == 422
        app.config._settings = None

    @pytest.mark.asyncio
    async def test_endpoint_maps_token_budget_failure_to_429(self, monkeypatch):
        """A blown provider token budget is actionable, so it must not be a bare 500."""
        monkeypatch.setenv("LLM_API_KEY", "test-key-123")
        import app.config
        app.config._settings = None

        from httpx import ASGITransport, AsyncClient
        from app.main import app as fastapi_app

        async def _raise(*_args, **_kwargs):
            raise LLMPayloadTooLargeError("Request too large for model")

        with patch("app.agent.engine.investigate", _raise):
            transport = ASGITransport(app=fastapi_app)
            async with AsyncClient(transport=transport, base_url="http://test") as client:
                resp = await client.post(
                    "/api/agent/investigate",
                    json={"question": "Why is the server slow?"},
                )

        assert resp.status_code == 429
        assert "token limit" in resp.json()["detail"]
        app.config._settings = None

    @pytest.mark.asyncio
    async def test_endpoint_maps_rate_limit_to_429(self, monkeypatch):
        monkeypatch.setenv("LLM_API_KEY", "test-key-123")
        import app.config
        app.config._settings = None

        from httpx import ASGITransport, AsyncClient
        from app.main import app as fastapi_app

        async def _raise(*_args, **_kwargs):
            raise LLMRateLimitError("Rate limit reached")

        with patch("app.agent.engine.investigate", _raise):
            transport = ASGITransport(app=fastapi_app)
            async with AsyncClient(transport=transport, base_url="http://test") as client:
                resp = await client.post(
                    "/api/agent/investigate",
                    json={"question": "Why is the server slow?"},
                )

        assert resp.status_code == 429
        assert "rate limiting" in resp.json()["detail"]
        app.config._settings = None

    def test_investigation_request_model(self):
        req = InvestigationRequest(question="Why is the server slow?")
        assert req.question == "Why is the server slow?"

    def test_investigation_response_model(self):
        resp = InvestigationResponse(
            investigation_id="test-id",
            question="test",
            answer="test answer",
            severity="high",
            confidence="medium",
            tools_used=["get_server_health"],
        )
        assert resp.severity == "high"
        assert "No database remediation was performed" in resp.agent_action

    def test_tool_call_model(self):
        tc = ToolCall(
            tool_name="get_server_health",
            arguments={},
            success=True,
            duration_seconds=0.5,
        )
        assert tc.tool_name == "get_server_health"


# ─── Test: Security Boundaries ───────────────────────────────────────────


class TestSecurityBoundaries:
    """Verify the AI agent's security constraints."""

    def test_no_sql_in_approved_tools(self):
        """No approved tool accepts raw SQL as a parameter."""
        for tool_name in APPROVED_TOOL_NAMES:
            td = TOOL_MAP[tool_name]
            params = td.parameters.get("properties", {})
            for pname in params:
                assert pname not in ("sql", "query", "statement", "command"), (
                    f"Tool {tool_name} has a parameter '{pname}' that could accept SQL"
                )

    def test_unapproved_tool_raises(self):
        with pytest.raises(KeyError):
            execute_tool("exec_sql", {"sql": "SELECT 1"})

    def test_system_prompt_no_sql_generation(self):
        assert "Do NOT generate SQL" in SYSTEM_PROMPT

    def test_system_prompt_no_modification(self):
        assert "NEVER modify" in SYSTEM_PROMPT

    def test_credentials_not_in_system_prompt(self):
        prompt_lower = SYSTEM_PROMPT.lower()
        assert "password" not in prompt_lower
        assert "api_key" not in prompt_lower
        assert "connection_string" not in prompt_lower

    def test_sanitizer_blocks_credentials(self):
        data = {
            "password": "secret",
            "api_key": "sk-123",
            "token": "bearer-xyz",
            "host": "safe-host",
        }
        result = sanitize_dict(data)
        assert result["password"] == "[REDACTED]"
        assert result["api_key"] == "[REDACTED]"
        assert result["token"] == "[REDACTED]"
        assert result["host"] == "safe-host"


# ─── Test: Timeout Candidate Wording ─────────────────────────────────────


class TestTimeoutCandidateWording:
    """Ensure the system never claims confirmed timeouts."""

    def test_system_prompt_uses_candidate_language(self):
        assert "TIMEOUT_CANDIDATE" in SYSTEM_PROMPT
        assert "NEVER" in SYSTEM_PROMPT and "confirmed" in SYSTEM_PROMPT.lower()

    def test_tool_definition_uses_candidate(self):
        td = TOOL_MAP["get_timeout_candidates"]
        assert "timeout_candidate" in td.description or "timeout candidate" in td.description
        assert "confirmed timeout" in td.description.lower()


# ─── Test: Config ─────────────────────────────────────────────────────────


class TestAgentConfig:
    """Verify agent configuration settings."""

    def test_default_config(self, monkeypatch):
        import app.config
        app.config._settings = None
        monkeypatch.setenv("LLM_API_KEY", "")
        monkeypatch.setenv("LLM_MODEL", "gpt-4o")

        settings = app.config.get_settings()
        assert settings.max_agent_tool_calls == 10
        assert settings.max_agent_execution_seconds == 60
        assert settings.llm_temperature == 0.1
        assert settings.llm_model == "gpt-4o"
        app.config._settings = None

    def test_custom_limits(self, monkeypatch):
        import app.config
        app.config._settings = None
        monkeypatch.setenv("MAX_AGENT_TOOL_CALLS", "5")
        monkeypatch.setenv("MAX_AGENT_EXECUTION_SECONDS", "30")

        settings = app.config.get_settings()
        assert settings.max_agent_tool_calls == 5
        assert settings.max_agent_execution_seconds == 30
        app.config._settings = None

    def test_api_key_not_logged(self, monkeypatch):
        """API key uses SecretStr — never exposed in repr/str."""
        import app.config
        app.config._settings = None
        monkeypatch.setenv("LLM_API_KEY", "sk-super-secret")

        settings = app.config.get_settings()
        settings_repr = repr(settings)
        assert "sk-super-secret" not in settings_repr
        app.config._settings = None


# ─── Test: Investigation Strategy ─────────────────────────────────────────


class TestInvestigationStrategy:
    """Verify the system prompt contains investigation strategy guidance."""

    def test_current_performance_strategy(self):
        """System prompt contains current-performance tool path."""
        assert "CURRENT PERFORMANCE QUESTION" in SYSTEM_PROMPT
        assert "get_database_health" in SYSTEM_PROMPT
        assert "get_active_requests" in SYSTEM_PROMPT
        assert "get_blocking" in SYSTEM_PROMPT

    def test_historical_strategy(self):
        """System prompt contains historical tool path."""
        assert "HISTORICAL PERFORMANCE QUESTION" in SYSTEM_PROMPT
        assert "get_slow_history" in SYSTEM_PROMPT
        assert "get_timeout_candidates" in SYSTEM_PROMPT
        assert "get_query_baseline" in SYSTEM_PROMPT

    def test_timeout_strategy(self):
        """System prompt contains timeout investigation tool path."""
        assert "TIMEOUT / SLOW QUERY QUESTION" in SYSTEM_PROMPT
        assert "APPLICATION_OR_INTERACTIVE" in SYSTEM_PROMPT
        assert "BACKGROUND_OR_SCHEDULED" in SYSTEM_PROMPT

    def test_regression_strategy(self):
        """System prompt contains regression tool path."""
        assert "REGRESSION QUESTION" in SYSTEM_PROMPT
        assert "get_query_regressions" in SYSTEM_PROMPT

    def test_server_wide_strategy(self):
        """System prompt contains server-wide tool path."""
        assert "SERVER-WIDE QUESTION" in SYSTEM_PROMPT
        assert "get_server_health" in SYSTEM_PROMPT

    def test_evidence_rules(self):
        """System prompt contains evidence classification rules."""
        assert "CONFIRMED FACT" in SYSTEM_PROMPT
        assert "SUPPORTED INFERENCE" in SYSTEM_PROMPT
        assert "UNKNOWN / REQUIRES MORE EVIDENCE" in SYSTEM_PROMPT

    def test_timeout_language_rules(self):
        """System prompt separates slow, timeout candidate, and confirmed timeout."""
        assert "SLOW_QUERY_CANDIDATE" in SYSTEM_PROMPT
        assert "TIMEOUT_CANDIDATE" in SYSTEM_PROMPT
        assert "CONFIRMED APPLICATION TIMEOUT" in SYSTEM_PROMPT
        assert "NEVER" in SYSTEM_PROMPT

    def test_no_remediation_statement(self):
        """System prompt requires 'No database remediation was performed.'"""
        assert "No database remediation was performed" in SYSTEM_PROMPT

    def test_smallest_tool_path_guidance(self):
        """Tool usage is scoped to the question rather than blanket-minimised.

        The path must be small enough to stay efficient but large enough to
        actually answer a broad question, so the guidance is conditional.
        """
        assert "SMALLEST tool path" in SYSTEM_PROMPT
        assert "narrow question" in SYSTEM_PROMPT.lower()

    def test_no_hallucinated_index_possible(self):
        """System prompt explicitly forbids inventing index recommendations."""
        lower = SYSTEM_PROMPT.lower()
        assert "index recommendation" in lower or "missing indexes" in lower
        assert "never invent" in lower

    def test_no_unsupported_root_cause(self):
        """System prompt requires evidence for every technical claim."""
        assert "Every technical statement must originate from tool evidence" in SYSTEM_PROMPT

    def test_date_context_placeholder_in_prompt(self):
        """System prompt contains a placeholder for current date injection."""
        assert "{current_datetime}" in SYSTEM_PROMPT

    def test_half_open_interval_guidance(self):
        """System prompt instructs half-open intervals, not 23:59:59."""
        assert "Do NOT use 23:59:59" in SYSTEM_PROMPT
        assert "half-open interval" in SYSTEM_PROMPT


# ─── Test: Background vs Application Workload ─────────────────────────────


@pytest.mark.asyncio
class TestWorkloadStrategy:
    """Verify agent differentiates application vs background workloads."""

    async def test_application_timeout_prefers_interactive(self):
        """Timeout question about application should prefer APPLICATION_OR_INTERACTIVE."""
        candidate_result = {
            "database": "Merchandising",
            "query_store_status": "AVAILABLE",
            "candidate_count": 2,
            "candidates": [
                {
                    "label": "timeout_candidate",
                    "procedure_name": "dbo.uspGetOrders",
                    "max_duration_ms": 47200,
                    "avg_duration_ms": 35000,
                    "confidence": "high",
                    "severity": "critical",
                    "workload_type": "APPLICATION_OR_INTERACTIVE",
                    "rank": 1,
                    "intervals_observed": 3,
                    "intervals_exceeding_threshold": 2,
                },
                {
                    "label": "timeout_candidate",
                    "procedure_name": None,
                    "query_hash": "0xMaint",
                    "max_duration_ms": 55000,
                    "avg_duration_ms": 50000,
                    "confidence": "medium",
                    "severity": "high",
                    "workload_type": "BACKGROUND_OR_SCHEDULED",
                    "rank": 2,
                    "intervals_observed": 1,
                    "intervals_exceeding_threshold": 1,
                },
            ],
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_timeout_candidates", {
                "database": "Merchandising",
                "start_time": "2026-08-24T09:00:00Z",
                "end_time": "2026-08-24T11:00:00Z",
                "workload_type": "APPLICATION_OR_INTERACTIVE",
            }),
            _make_final_response(
                "Summary: Application timeout candidate identified in Merchandising.\n"
                "Severity: critical\n"
                "Confidence: high\n"
                "Affected database: Merchandising\n"
                "Affected query/procedure: dbo.uspGetOrders\n"
                "Evidence:\n"
                "- dbo.uspGetOrders reached 47.2 seconds (timeout threshold: 30s)\n"
                "- Classified as APPLICATION_OR_INTERACTIVE workload\n"
                "- Exceeded threshold in 2 of 3 observed intervals\n"
                "What is known:\n"
                "- Application query exceeded threshold multiple times\n"
                "What is uncertain:\n"
                "- Whether application telemetry confirms a SqlException\n"
                "Human review required: yes\n"
                "Agent action: No database remediation was performed.\n"
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {
            "get_timeout_candidates": lambda _: candidate_result,
        }):
            result = await investigate(
                "Which query caused the Merchandising application timeout?", provider,
            )

        assert result.severity == "critical"
        assert result.affected_database == "Merchandising"
        assert "get_timeout_candidates" in result.tools_used

    async def test_background_workload_mentioned_for_pressure(self):
        """Background workloads should be mentioned when they contribute to pressure."""
        health_result = {
            "overall_status": "DEGRADED",
            "total_findings": 2,
            "findings": [
                {
                    "category": "long_running_query",
                    "database": "Merchandising",
                    "severity": "high",
                    "confidence": "high",
                    "summary": "DBCC CHECKDB running for 120 seconds",
                    "evidence": ["Elapsed: 120s", "Type: DBCC"],
                    "likely_cause": "Background maintenance competing for resources",
                    "recommendation": "Schedule during off-peak",
                },
            ],
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_database_health", {"database": "Merchandising"}),
            _make_final_response(
                "Summary: Background maintenance task (DBCC CHECKDB) consuming resources.\n"
                "Severity: high\n"
                "Confidence: high\n"
                "Affected database: Merchandising\n"
                "Evidence:\n"
                "- DBCC CHECKDB running for 120 seconds\n"
                "- Background workload competing for I/O\n"
                "What is known:\n"
                "- Background maintenance is active\n"
                "What is uncertain:\n"
                "- Whether this directly caused application slowness\n"
                "Human review required: yes\n"
                "Agent action: No database remediation was performed.\n"
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {
            "get_database_health": lambda _: health_result,
        }):
            result = await investigate(
                "Why is Merchandising slow right now?", provider,
            )

        assert result.severity == "high"
        assert "get_database_health" in result.tools_used


# ─── Test: Baseline Unavailable ───────────────────────────────────────────


@pytest.mark.asyncio
class TestBaselineUnavailable:
    """Agent handles missing baseline data gracefully."""

    async def test_baseline_unavailable(self):
        """Agent reports when baseline data is insufficient."""
        candidate_result = {
            "database": "Merchandising",
            "query_store_status": "AVAILABLE",
            "candidate_count": 1,
            "candidates": [{
                "label": "timeout_candidate",
                "procedure_name": "dbo.uspNewProc",
                "max_duration_ms": 45000,
                "avg_duration_ms": 40000,
                "confidence": "medium",
                "severity": "high",
                "workload_type": "APPLICATION_OR_INTERACTIVE",
                "rank": 1,
                "baseline_comparison": {
                    "baseline_status": "INSUFFICIENT_DATA",
                    "sample_count": 0,
                },
            }],
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_timeout_candidates", {
                "database": "Merchandising",
                "start_time": "2026-08-24T09:00:00Z",
                "end_time": "2026-08-24T11:00:00Z",
            }),
            _make_final_response(
                "Summary: Timeout candidate identified but no baseline available.\n"
                "Severity: high\n"
                "Confidence: medium\n"
                "Affected database: Merchandising\n"
                "Affected query/procedure: dbo.uspNewProc\n"
                "Evidence:\n"
                "- dbo.uspNewProc reached 45 seconds\n"
                "- Baseline status: INSUFFICIENT_DATA\n"
                "Historical comparison: N/A\n"
                "What is known:\n"
                "- Query exceeded 30-second threshold\n"
                "What is uncertain:\n"
                "- Whether this represents a regression (no baseline history)\n"
                "Human review required: yes\n"
                "Agent action: No database remediation was performed.\n"
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {
            "get_timeout_candidates": lambda _: candidate_result,
        }):
            result = await investigate(
                "Was there a timeout in Merchandising?", provider,
            )

        assert result.confidence == "medium"
        assert result.historical_comparison is None or result.historical_comparison == ""
        assert result.human_review_required is True


# ─── Test: Date Parsing ───────────────────────────────────────────────────


class TestToolDateParsing:
    """Verify that _parse_datetime handles all LLM-produced date formats."""

    def test_iso_with_t(self):
        from app.agent.tools import _parse_datetime
        dt = _parse_datetime("2026-08-21T00:00:00")
        assert dt == datetime(2026, 8, 21, 0, 0, 0)

    def test_iso_with_z(self):
        from app.agent.tools import _parse_datetime
        dt = _parse_datetime("2026-08-21T00:00:00Z")
        assert dt == datetime(2026, 8, 21, 0, 0, 0)
        assert dt.tzinfo is None  # naive to match direct API

    def test_date_only(self):
        from app.agent.tools import _parse_datetime
        dt = _parse_datetime("2026-08-21")
        assert dt == datetime(2026, 8, 21, 0, 0, 0)

    def test_iso_with_offset(self):
        from app.agent.tools import _parse_datetime
        dt = _parse_datetime("2026-08-21T09:00:00+05:00")
        assert dt == datetime(2026, 8, 21, 9, 0, 0)
        assert dt.tzinfo is None  # stripped to naive

    def test_invalid_raises(self):
        from app.agent.tools import _parse_datetime
        with pytest.raises(ValueError, match="Cannot parse datetime"):
            _parse_datetime("August twenty-first")

    def test_august_21_resolves_to_current_year(self):
        """'August 21' when current date is 2026-08-26 → 2026-08-21."""
        from app.agent.tools import _parse_datetime
        now = datetime(2026, 8, 26, 14, 30, 0)
        # LLM wrongly sends 2024-08-21 — year correction fixes it
        dt = _parse_datetime("2024-08-21T00:00:00", _now=now)
        assert dt == datetime(2026, 8, 21, 0, 0, 0)

    def test_date_only_wrong_year_corrected(self):
        """Date-only with wrong year is corrected."""
        from app.agent.tools import _parse_datetime
        now = datetime(2026, 8, 26)
        dt = _parse_datetime("2024-08-21", _now=now)
        assert dt == datetime(2026, 8, 21, 0, 0, 0)

    def test_yesterday(self):
        """'yesterday' resolves to the day before current date."""
        from app.agent.tools import _parse_datetime
        now = datetime(2026, 8, 26, 14, 30, 0)
        dt = _parse_datetime("yesterday", _now=now)
        assert dt == datetime(2026, 8, 25, 0, 0, 0)

    def test_today(self):
        """'today' resolves to the current date at midnight."""
        from app.agent.tools import _parse_datetime
        now = datetime(2026, 8, 26, 14, 30, 0)
        dt = _parse_datetime("today", _now=now)
        assert dt == datetime(2026, 8, 26, 0, 0, 0)

    def test_explicit_2025_year_trusted(self):
        """Explicit year within 1 year of current is trusted as-is."""
        from app.agent.tools import _parse_datetime
        now = datetime(2026, 8, 26)
        dt = _parse_datetime("2025-08-21T00:00:00", _now=now)
        assert dt == datetime(2025, 8, 21, 0, 0, 0)

    def test_future_month_without_year_uses_last_year(self):
        """If month/day is in the future for current year, use previous year.
        E.g., asking about 'December 15' on 2026-08-26 → 2025-12-15."""
        from app.agent.tools import _parse_datetime
        now = datetime(2026, 8, 26)
        # LLM passes 2024-12-15 (wrong year, future month/day)
        dt = _parse_datetime("2024-12-15T00:00:00", _now=now)
        assert dt == datetime(2025, 12, 15, 0, 0, 0)

    def test_end_of_day_23_59_59_normalized(self):
        """23:59:59 end-times are normalized to next-day 00:00:00."""
        from app.agent.tools import _parse_datetime
        dt = _parse_datetime("2026-08-21T23:59:59")
        assert dt == datetime(2026, 8, 22, 0, 0, 0)

    def test_half_open_interval_not_23_59(self):
        """Midnight-to-midnight is the correct half-open range for a full day."""
        from app.agent.tools import _parse_datetime
        start = _parse_datetime("2026-08-21T00:00:00")
        end = _parse_datetime("2026-08-22T00:00:00")
        assert start == datetime(2026, 8, 21, 0, 0, 0)
        assert end == datetime(2026, 8, 22, 0, 0, 0)
        assert (end - start).total_seconds() == 86400

    def test_current_year_not_corrected(self):
        """Date in current year is not modified."""
        from app.agent.tools import _parse_datetime
        now = datetime(2026, 8, 26)
        dt = _parse_datetime("2026-03-15T10:00:00", _now=now)
        assert dt == datetime(2026, 3, 15, 10, 0, 0)

    def test_wrong_year_with_z_suffix(self):
        """Wrong year + Z suffix both handled."""
        from app.agent.tools import _parse_datetime
        now = datetime(2026, 8, 26)
        dt = _parse_datetime("2024-08-21T00:00:00Z", _now=now)
        assert dt == datetime(2026, 8, 21, 0, 0, 0)
        assert dt.tzinfo is None


# ─── Test: Structured Parsing with Markdown ───────────────────────────────


class TestStructuredParsingMarkdown:
    """Verify _parse_structured_answer handles markdown-formatted LLM output."""

    def test_bold_severity(self):
        text = "**Severity:** low (no performance issue detected)\n**Confidence:** high\n"
        result = _parse_structured_answer(text)
        assert result["severity"] == "low"
        assert result["confidence"] == "high"

    def test_bold_human_review_no(self):
        text = "**Human review required:** no\n"
        result = _parse_structured_answer(text)
        assert result["human_review_required"] is False

    def test_bold_affected_database(self):
        text = "**Affected database:** Merchandising\n"
        result = _parse_structured_answer(text)
        assert result["affected_database"] == "Merchandising"

    def test_bold_affected_query_na(self):
        text = "**Affected query/procedure:** N/A\n"
        result = _parse_structured_answer(text)
        assert result.get("affected_query") is None

    def test_bold_evidence_bullets(self):
        text = (
            "**Evidence:**\n"
            "- `get_timeout_candidates` returned candidate_count: 0\n"
            "- No procedures exceeded 30s\n"
            "**Likely cause:** No performance issue\n"
        )
        result = _parse_structured_answer(text)
        assert len(result["evidence"]) == 2

    def test_bold_severity_with_qualifier(self):
        """Severity: low (no performance issue) should parse as 'low'."""
        text = "**Severity:** low (no performance issue detected for the requested period)\n"
        result = _parse_structured_answer(text)
        assert result["severity"] == "low"

    def test_consistency_text_matches_structured(self):
        """Parsed structured fields must match what the text says."""
        text = (
            "Summary: No stored procedures exceeded 30 seconds.\n"
            "**Severity:** low (no performance issue detected)\n"
            "**Confidence:** high (direct evidence from Query Store)\n"
            "**Affected database:** Merchandising\n"
            "**Affected query/procedure:** N/A\n"
            "**Evidence**\n"
            "- get_timeout_candidates returned candidate_count: 0\n"
            "**Human review required:** no\n"
            "**Agent action:** No database remediation was performed.\n"
        )
        result = _parse_structured_answer(text)
        assert result["severity"] == "low"
        assert result["confidence"] == "high"
        assert result["affected_database"] == "Merchandising"
        assert result.get("affected_query") is None
        assert result["human_review_required"] is False


# ─── Test: AI Tool vs Direct API Equivalence ──────────────────────────────


class TestToolApiEquivalence:
    """AI tool executors and direct API must use the same service layer."""

    def test_slow_history_calls_same_collector(self):
        """_exec_slow_history calls the same collect_slow_history as the API endpoint."""
        from app.models.slow_history import QueryStoreStatus
        captured_args = {}

        def fake_collect(database, start_time, end_time, threshold_ms, top_n=20):
            captured_args["database"] = database
            captured_args["start_time"] = start_time
            captured_args["end_time"] = end_time
            captured_args["threshold_ms"] = threshold_ms
            captured_args["top_n"] = top_n
            return QueryStoreStatus.AVAILABLE, [], None

        with patch("app.services.slow_history_service.collect_slow_history", fake_collect):
            from app.agent.tools import _exec_slow_history
            result = _exec_slow_history({
                "database": "Merchandising",
                "start_time": "2026-08-21T00:00:00",
                "end_time": "2026-08-22T00:00:00",
                "threshold_ms": 30000,
            })

        assert captured_args["database"] == "Merchandising"
        assert captured_args["start_time"] == datetime(2026, 8, 21, 0, 0, 0)
        assert captured_args["end_time"] == datetime(2026, 8, 22, 0, 0, 0)
        assert captured_args["threshold_ms"] == 30000.0
        assert captured_args["top_n"] == 20
        assert result["candidate_count"] == 0
        assert result["query_store_status"] == "AVAILABLE"

    def test_timeout_candidates_calls_same_collector(self):
        """_exec_timeout_candidates calls the same collect_slow_history."""
        from app.models.slow_history import QueryStoreStatus
        captured_args = {}

        def fake_collect(database, start_time, end_time, threshold_ms, top_n=20):
            captured_args["database"] = database
            captured_args["start_time"] = start_time
            captured_args["end_time"] = end_time
            captured_args["threshold_ms"] = threshold_ms
            return QueryStoreStatus.AVAILABLE, [], None

        with patch("app.collectors.query_store.collect_slow_history", fake_collect):
            from app.agent.tools import _exec_timeout_candidates
            result = _exec_timeout_candidates({
                "database": "Merchandising",
                "start_time": "2026-08-21T00:00:00",
                "end_time": "2026-08-22T00:00:00",
                "threshold_ms": 30000,
            })

        assert captured_args["database"] == "Merchandising"
        assert captured_args["start_time"] == datetime(2026, 8, 21, 0, 0, 0)
        assert captured_args["end_time"] == datetime(2026, 8, 22, 0, 0, 0)
        assert captured_args["threshold_ms"] == 30000.0
        assert result["candidate_count"] == 0

    def test_identical_args_produce_identical_results(self):
        """Direct slow-history service and AI tool return equivalent candidates."""
        from app.models.slow_history import QueryStoreStatus, SlowQueryRecord
        from app.services.slow_history_service import get_slow_history_response

        fake_record = SlowQueryRecord(
            query_id=101,
            query_hash="0xABC123",
            query_sql_text="EXEC dbo.uspMerch_GetSearchOpenOrders",
            procedure_name="dbo.uspMerch_GetSearchOpenOrders",
            plan_id=42,
            execution_count=5,
            avg_duration_ms=38500.0,
            max_duration_ms=42614.0,
            min_duration_ms=31200.0,
            first_execution_time=datetime(2026, 8, 21, 9, 0, 0),
            last_execution_time=datetime(2026, 8, 21, 16, 30, 0),
            avg_cpu_time_ms=12000.0,
            avg_logical_reads=50000.0,
        )

        def fake_collect(database, start_time, end_time, threshold_ms, top_n=20):
            return QueryStoreStatus.AVAILABLE, [fake_record], None

        args = {
            "database": "Merchandising",
            "start_time": "2026-08-21T00:00:00",
            "end_time": "2026-08-22T00:00:00",
            "threshold_ms": 30000,
        }

        with patch("app.services.slow_history_service.collect_slow_history", fake_collect):
            from app.agent.tools import _exec_slow_history
            direct_result = get_slow_history_response(
                database=args["database"],
                start_time=datetime(2026, 8, 21, 0, 0, 0),
                end_time=datetime(2026, 8, 22, 0, 0, 0),
                threshold_ms=args["threshold_ms"],
                top_n=20,
            )
            tool_result = _exec_slow_history(args)

        direct_keys = [
            (c.query_hash, c.query_id, c.procedure_name)
            for c in direct_result.candidates
        ]
        tool_keys = [
            (c["query_hash"], c["query_id"], c["procedure_name"])
            for c in tool_result["candidates"]
        ]

        assert tool_result["candidate_count"] == direct_result.total_candidates == 1
        assert tool_keys == direct_keys == [
            ("0xABC123", 101, "dbo.uspMerch_GetSearchOpenOrders")
        ]

    def test_date_only_produces_half_open_range(self):
        """Date-only inputs produce midnight-to-midnight range."""
        from app.agent.tools import _parse_datetime

        start = _parse_datetime("2026-08-21")
        end = _parse_datetime("2026-08-22")
        assert start == datetime(2026, 8, 21, 0, 0, 0)
        assert end == datetime(2026, 8, 22, 0, 0, 0)
        assert (end - start).total_seconds() == 86400

    def test_timeout_candidates_and_direct_api_return_same_hashes(self):
        """AI tool get_timeout_candidates produces the same candidate query hashes
        as the direct /api/performance/slow-history endpoint logic."""
        from app.models.slow_history import QueryStoreStatus, SlowQueryRecord
        from app.detectors.timeout_candidate import build_timeout_candidates

        # Simulated records identical to what collect_slow_history would return
        records = [
            SlowQueryRecord(
                query_id=101,
                query_hash="0xABC123",
                query_sql_text="EXEC dbo.uspMerch_GetSearchOpenOrders @Param=1",
                procedure_name="dbo.uspMerch_GetSearchOpenOrders",
                object_id=12345,
                plan_id=42,
                execution_count=5,
                avg_duration_ms=38500.0,
                max_duration_ms=42614.0,
                min_duration_ms=31200.0,
                avg_cpu_time_ms=12000.0,
                max_cpu_time_ms=18000.0,
                avg_logical_reads=50000.0,
                max_logical_reads=80000.0,
                avg_physical_reads=100.0,
                first_execution_time=datetime(2026, 8, 21, 9, 0, 0),
                last_execution_time=datetime(2026, 8, 21, 16, 30, 0),
                interval_start_time=datetime(2026, 8, 21, 9, 0, 0),
            ),
            SlowQueryRecord(
                query_id=202,
                query_hash="0xDEF456",
                query_sql_text="EXEC dbo.uspMerch_DTS_UpdateSOOrderStatus",
                procedure_name="dbo.uspMerch_DTS_UpdateSOOrderStatus",
                object_id=67890,
                plan_id=99,
                execution_count=2,
                avg_duration_ms=35000.0,
                max_duration_ms=38000.0,
                min_duration_ms=32000.0,
                avg_cpu_time_ms=8000.0,
                max_cpu_time_ms=10000.0,
                avg_logical_reads=20000.0,
                max_logical_reads=30000.0,
                avg_physical_reads=50.0,
                first_execution_time=datetime(2026, 8, 21, 2, 0, 0),
                last_execution_time=datetime(2026, 8, 21, 3, 0, 0),
                interval_start_time=datetime(2026, 8, 21, 2, 0, 0),
            ),
        ]

        def fake_collect(database, start_time, end_time, threshold_ms, top_n=20):
            return QueryStoreStatus.AVAILABLE, records, None

        # Path 1: Direct API logic (build_timeout_candidates with group_results=True)
        direct_candidates = build_timeout_candidates(
            records, 30000.0, "Merchandising",
            group_results=True, workload_type_filter=None,
        )
        direct_hashes = {c.query_hash for c in direct_candidates}

        # Path 2: AI tool _exec_timeout_candidates
        with patch("app.collectors.query_store.collect_slow_history", fake_collect):
            from app.agent.tools import _exec_timeout_candidates
            tool_result = _exec_timeout_candidates({
                "database": "Merchandising",
                "start_time": "2026-08-21T00:00:00",
                "end_time": "2026-08-22T00:00:00",
                "threshold_ms": 30000,
            })

        tool_hashes = {c["query_hash"] for c in tool_result["candidates"]}

        # Both paths MUST return the same candidates
        assert tool_result["candidate_count"] == len(direct_candidates)
        assert tool_hashes == direct_hashes
        assert "0xABC123" in tool_hashes
        assert "0xDEF456" in tool_hashes

    def test_timezone_z_suffix_does_not_shift_date(self):
        """UTC Z suffix must not shift the date when passed to the collector."""
        from app.models.slow_history import QueryStoreStatus
        captured_args = {}

        def fake_collect(database, start_time, end_time, threshold_ms, top_n=20):
            captured_args["start_time"] = start_time
            captured_args["end_time"] = end_time
            return QueryStoreStatus.AVAILABLE, [], None

        with patch("app.collectors.query_store.collect_slow_history", fake_collect):
            from app.agent.tools import _exec_timeout_candidates
            _exec_timeout_candidates({
                "database": "Merchandising",
                "start_time": "2026-08-21T00:00:00Z",
                "end_time": "2026-08-22T00:00:00Z",
                "threshold_ms": 30000,
            })

        # Timezone must be stripped — naive datetime at midnight
        assert captured_args["start_time"] == datetime(2026, 8, 21, 0, 0, 0)
        assert captured_args["end_time"] == datetime(2026, 8, 22, 0, 0, 0)
        assert captured_args["start_time"].tzinfo is None
        assert captured_args["end_time"].tzinfo is None

    def test_tool_result_includes_all_debug_fields(self):
        """get_timeout_candidates result includes all required debug fields."""
        from app.models.slow_history import QueryStoreStatus

        def fake_collect(database, start_time, end_time, threshold_ms, top_n=20):
            return QueryStoreStatus.AVAILABLE, [], None

        with patch("app.collectors.query_store.collect_slow_history", fake_collect):
            from app.agent.tools import _exec_timeout_candidates
            result = _exec_timeout_candidates({
                "database": "Merchandising",
                "start_time": "2026-08-21T00:00:00",
                "end_time": "2026-08-22T00:00:00",
                "threshold_ms": 30000,
                "workload_type": "ALL",
            })

        # Verify all required logging/debug fields are present
        assert result["database"] == "Merchandising"
        assert result["query_store_status"] == "AVAILABLE"
        assert result["candidate_count"] == 0
        assert "candidates" in result

# ─── Test: Tool Call Logging ──────────────────────────────────────────────


@pytest.mark.asyncio
class TestToolCallLogging:
    """Verify that tool calls capture sanitized arguments and result summaries."""

    async def test_tool_calls_include_sanitized_args(self):
        """ToolCall records include sanitized arguments."""
        slow_result = {
            "database": "Merchandising",
            "query_store_status": "AVAILABLE",
            "record_count": 0,
            "records": [],
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_slow_history", {
                "database": "Merchandising",
                "start_time": "2026-08-21T00:00:00",
                "end_time": "2026-08-22T00:00:00",
                "threshold_ms": 30000,
            }),
            _make_final_response(
                "Summary: No slow queries found.\n"
                "Severity: low\nConfidence: high\n"
                "Human review required: no\n"
            ),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {
            "get_slow_history": lambda _: slow_result,
        }):
            result = await investigate("Test question", provider)

        assert len(result.tool_calls_detail) == 1
        tc = result.tool_calls_detail[0]
        assert tc.tool_name == "get_slow_history"
        assert tc.arguments_sanitized["database"] == "Merchandising"
        assert tc.arguments_sanitized["start_time"] == "2026-08-21T00:00:00"

    async def test_tool_calls_include_result_summary(self):
        """ToolCall records include a result summary with counts."""
        slow_result = {
            "database": "Merchandising",
            "query_store_status": "AVAILABLE",
            "record_count": 3,
            "records": [{"q": "a"}, {"q": "b"}, {"q": "c"}],
        }

        provider = MockLLMProvider([
            _make_tool_call_response("get_slow_history", {
                "database": "Merchandising",
                "start_time": "2026-08-21T00:00:00",
                "end_time": "2026-08-22T00:00:00",
            }),
            _make_final_response("Summary: Found 3 queries.\nSeverity: medium\nConfidence: high\nHuman review required: yes\n"),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {
            "get_slow_history": lambda _: slow_result,
        }):
            result = await investigate("Test question", provider)

        tc = result.tool_calls_detail[0]
        assert tc.result_summary["database"] == "Merchandising"
        assert tc.result_summary["record_count"] == 3
        assert tc.result_summary["records_count"] == 3

    async def test_failed_tool_has_error_in_summary(self):
        """Failed tool calls capture the error in result_summary."""
        provider = MockLLMProvider([
            _make_tool_call_response("get_slow_history", {
                "database": "Merchandising",
                "start_time": "2026-08-21T00:00:00",
                "end_time": "2026-08-22T00:00:00",
            }),
            _make_final_response("Summary: Tool failed.\nSeverity: unknown\nConfidence: low\nHuman review required: yes\n"),
        ])

        with patch.dict("app.agent.tools._EXECUTORS", {
            "get_slow_history": lambda _: {"error": "Connection failed"},
        }):
            result = await investigate("Test question", provider)

        tc = result.tool_calls_detail[0]
        assert tc.success is False
        assert tc.result_summary["error"] == "Connection failed"


# ─── Test: Result Summary Builder ─────────────────────────────────────────


class TestBuildResultSummary:
    """Verify _build_result_summary extracts the right metadata."""

    def test_slow_history_summary(self):
        from app.agent.engine import _build_result_summary
        data = {
            "database": "Merchandising",
            "query_store_status": "AVAILABLE",
            "record_count": 5,
            "records": [{"a": 1}, {"a": 2}, {"a": 3}, {"a": 4}, {"a": 5}],
        }
        summary = _build_result_summary("get_slow_history", data)
        assert summary["database"] == "Merchandising"
        assert summary["record_count"] == 5
        assert summary["records_count"] == 5
        assert "records" not in summary  # full payload not stored

    def test_error_summary(self):
        from app.agent.engine import _build_result_summary
        data = {"error": "Permission denied"}
        summary = _build_result_summary("get_slow_history", data)
        assert summary == {"error": "Permission denied"}

    def test_candidate_summary(self):
        from app.agent.engine import _build_result_summary
        data = {
            "database": "Sales",
            "query_store_status": "AVAILABLE",
            "candidate_count": 2,
            "candidates": [{"x": 1}, {"x": 2}],
        }
        summary = _build_result_summary("get_timeout_candidates", data)
        assert summary["candidate_count"] == 2
        assert summary["candidates_count"] == 2


# ─── Test: Token-budget resilience ───────────────────────────────────────


class _OversizedThenOkProvider(LLMProvider):
    """Requests a tool, then rejects the follow-up as too large, then answers."""

    def __init__(self):
        self.calls = 0
        self.last_messages: list[LLMMessage] = []
        self.last_tools: list[ToolDefinition] | None = None

    async def generate(self, messages, tools=None, temperature=0.1, max_tokens=4096):
        self.calls += 1
        self.last_messages = list(messages)
        self.last_tools = tools
        if self.calls == 1:
            return _make_tool_call_response("get_server_health", {})
        if self.calls == 2:
            raise LLMPayloadTooLargeError("Request too large for model")
        return _make_final_response("Summary: recovered\nSeverity: low\nConfidence: low")


class TestTokenBudgetResilience:
    """An oversized conversation must degrade into an answer, not a 500."""

    @pytest.mark.asyncio
    async def test_oversized_request_recovers_with_compacted_evidence(self):
        provider = _OversizedThenOkProvider()
        big_result = {"overall_status": "DEGRADED", "findings": [{"summary": "z" * 200}] * 50}

        with patch("app.agent.engine.execute_tool", return_value=big_result):
            result = await investigate("How is the server?", provider)

        assert provider.calls == 3
        assert "recovered" in result.answer
        assert result.severity == "low"

        # The retry must have shrunk the evidence rather than resending it whole.
        evidence = [
            m for m in provider.last_messages
            if m.role == "user" and (m.content or "").startswith("Evidence from")
        ]
        assert evidence
        assert all(len(m.content) <= 2200 for m in evidence)

    @pytest.mark.asyncio
    async def test_summary_call_carries_no_tool_plumbing(self):
        """Providers reject tool calls in the history when no tools are offered."""
        provider = _OversizedThenOkProvider()

        with patch("app.agent.engine.execute_tool", return_value={"overall_status": "OK"}):
            await investigate("How is the server?", provider)

        assert provider.last_tools is None
        assert all(m.role != "tool" for m in provider.last_messages)
        assert all(not m.tool_calls for m in provider.last_messages)
        # The evidence itself survives the flattening.
        assert any("get_server_health" in (m.content or "") for m in provider.last_messages)

    @pytest.mark.asyncio
    async def test_tool_result_is_size_bounded(self):
        provider = MockLLMProvider([
            _make_tool_call_response("get_server_health", {}),
            _make_final_response("Summary: done\nSeverity: low\nConfidence: low"),
        ])
        huge_result = {"overall_status": "DEGRADED", "findings": [{"summary": "z" * 500}] * 200}

        with patch("app.agent.engine.execute_tool", return_value=huge_result):
            await investigate("How is the server?", provider)

        from app.config import get_settings
        limit = get_settings().max_tool_result_chars
        tool_messages = [
            m for msgs in provider.messages_received for m in msgs if m.role == "tool"
        ]
        assert tool_messages
        assert all(len(m.content) <= limit for m in tool_messages)
