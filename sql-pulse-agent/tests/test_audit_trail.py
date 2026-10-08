"""Tests for the AI investigation audit trail.

Covers:
  - Audit record persistence (store + retrieve)
  - Audit record listing
  - Sanitization of questions and answers before storage
  - Failed tool calls are recorded in audit
  - No-remediation confirmation in every audit record
  - Credential stripping verified
  - Time range extraction from questions
  - Finding reference extraction
  - GET /api/agent/investigations endpoint
  - GET /api/agent/investigations/{investigation_id} endpoint
  - Investigation not found
"""

import os
import tempfile
from datetime import UTC, datetime, timedelta
from unittest.mock import patch

import pytest

from app.models.agent import (
    AuditToolCall,
    InvestigationAuditRecord,
    InvestigationResponse,
    ToolCall,
)
from app.storage.history_service import (
    build_audit_record,
    persist_investigation_audit,
    get_investigation_audit,
    list_investigation_audits,
    set_repository,
    _extract_time_range,
    _extract_finding_references,
)
from app.storage.sqlite_repository import SQLiteHistoryRepository


@pytest.fixture
def audit_repo():
    """Create a temporary SQLite repository for audit trail testing."""
    fd, path = tempfile.mkstemp(suffix=".db")
    os.close(fd)
    repo = SQLiteHistoryRepository(db_path=path)
    repo.initialize()
    set_repository(repo)
    yield repo
    repo.close()
    try:
        os.unlink(path)
    except OSError:
        pass


def _make_response(
    investigation_id: str = "inv-001",
    question: str = "Why is Merchandising slow?",
    answer: str = "Summary: Slow query detected.\nSeverity: high\nConfidence: medium",
    severity: str = "high",
    confidence: str = "medium",
    affected_database: str | None = "Merchandising",
    affected_query: str | None = "dbo.uspGetOrders",
    evidence: list[str] | None = None,
    human_review_required: bool = True,
) -> InvestigationResponse:
    return InvestigationResponse(
        investigation_id=investigation_id,
        question=question,
        answer=answer,
        severity=severity,
        confidence=confidence,
        affected_database=affected_database,
        affected_query=affected_query,
        evidence=["Max duration: 47.2 seconds", "Query hash: 0xABC123"] if evidence is None else evidence,
        human_review_required=human_review_required,
        tools_used=["get_timeout_candidates"],
        tool_call_count=1,
        duration_seconds=2.5,
    )


def _make_tool_calls() -> list[ToolCall]:
    return [
        ToolCall(
            tool_name="get_timeout_candidates",
            arguments={"database": "Merchandising", "start_time": "2026-08-24T09:00:00Z", "end_time": "2026-08-24T11:00:00Z"},
            arguments_sanitized={"database": "Merchandising", "start_time": "2026-08-24T09:00:00Z", "end_time": "2026-08-24T11:00:00Z"},
            result_summary={"database": "Merchandising", "candidate_count": 2, "candidates_count": 2},
            success=True,
            duration_seconds=1.5,
        ),
    ]


# ─── Persistence Tests ───────────────────────────────────────────────────


class TestAuditPersistence:
    """Tests for storing and retrieving audit records."""

    def test_store_and_retrieve_audit(self, audit_repo):
        """Audit record survives a store → retrieve cycle."""
        resp = _make_response()
        tool_calls = _make_tool_calls()
        persist_investigation_audit(resp, tool_calls, server="DCIR-ERPDBL")

        audit = get_investigation_audit("inv-001")
        assert audit is not None
        assert audit.investigation_id == "inv-001"
        assert audit.server == "DCIR-ERPDBL"
        assert audit.database == "Merchandising"
        assert audit.severity == "high"
        assert audit.confidence == "medium"
        assert audit.human_review_required is True
        assert audit.remediation_performed is False

    def test_audit_has_tools_called(self, audit_repo):
        """Audit record includes tool call details."""
        resp = _make_response()
        tool_calls = _make_tool_calls()
        persist_investigation_audit(resp, tool_calls, server="srv")

        audit = get_investigation_audit("inv-001")
        assert len(audit.tools_called) == 1
        assert audit.tools_called[0].tool_name == "get_timeout_candidates"
        assert audit.tools_called[0].success is True
        assert audit.tools_called[0].duration_seconds == 1.5

    def test_audit_has_finding_references(self, audit_repo):
        """Audit record extracts query hashes and procedure names."""
        resp = _make_response(
            affected_query="dbo.uspGetOrders",
            evidence=["Query hash: 0xABC123", "Another hash 0xDEF456"],
        )
        tool_calls = _make_tool_calls()
        persist_investigation_audit(resp, tool_calls, server="srv")

        audit = get_investigation_audit("inv-001")
        assert "dbo.uspGetOrders" in audit.finding_references
        assert "0xABC123" in audit.finding_references
        assert "0xDEF456" in audit.finding_references

    def test_audit_not_found_returns_none(self, audit_repo):
        """Non-existent investigation_id returns None."""
        audit = get_investigation_audit("nonexistent")
        assert audit is None

    def test_duplicate_audit_ignored(self, audit_repo):
        """Storing the same investigation_id twice does not fail."""
        resp = _make_response()
        tool_calls = _make_tool_calls()
        persist_investigation_audit(resp, tool_calls, server="srv")
        # Second insert should silently be ignored
        persist_investigation_audit(resp, tool_calls, server="srv")

        audit = get_investigation_audit("inv-001")
        assert audit is not None

    def test_list_audits_ordered_newest_first(self, audit_repo):
        """List returns audits ordered by created_at descending."""
        now = datetime.now(UTC)
        for i in range(5):
            resp = _make_response(investigation_id=f"inv-{i:03d}")
            resp.timestamp = now - timedelta(hours=5 - i)
            persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audits = list_investigation_audits(limit=10)
        assert len(audits) == 5
        # newest first (inv-004 has the latest timestamp)
        assert audits[0].investigation_id == "inv-004"
        assert audits[-1].investigation_id == "inv-000"

    def test_list_audits_respects_limit(self, audit_repo):
        """List returns at most `limit` records."""
        for i in range(10):
            resp = _make_response(investigation_id=f"inv-{i:03d}")
            persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audits = list_investigation_audits(limit=3)
        assert len(audits) == 3

    def test_list_audits_empty(self, audit_repo):
        """List with no records returns empty list."""
        audits = list_investigation_audits()
        assert audits == []

    def test_audit_preserves_answer(self, audit_repo):
        """The full answer text is persisted."""
        resp = _make_response(answer="This is the full analysis text.")
        persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audit = get_investigation_audit("inv-001")
        assert audit.answer == "This is the full analysis text."


# ─── Sanitization Tests ──────────────────────────────────────────────────


class TestAuditSanitization:
    """Verify credentials are stripped before audit storage."""

    def test_password_stripped_from_question(self, audit_repo):
        """Passwords in the question are redacted before storage."""
        resp = _make_response(
            question="Why is slow? Server=srv;UID=admin;PWD=secret123;"
        )
        persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audit = get_investigation_audit("inv-001")
        assert "secret123" not in audit.question_sanitized
        assert "[REDACTED]" in audit.question_sanitized

    def test_api_key_stripped_from_question(self, audit_repo):
        """API keys in the question are redacted."""
        resp = _make_response(
            question="Check api_key = sk-abc123def performance"
        )
        persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audit = get_investigation_audit("inv-001")
        assert "sk-abc123def" not in audit.question_sanitized

    def test_connection_string_stripped_from_answer(self, audit_repo):
        """Connection strings in the answer are redacted."""
        resp = _make_response(
            answer="Found issue. connection string = Server=srv;UID=x;PWD=y;"
        )
        persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audit = get_investigation_audit("inv-001")
        assert "PWD=y" not in audit.answer
        assert "[REDACTED]" in audit.answer

    def test_tool_arguments_sanitized_in_audit(self, audit_repo):
        """Tool call arguments are sanitized before storage (credentials stripped)."""
        tool_calls = [
            ToolCall(
                tool_name="get_database_health",
                arguments={"database": "Merchandising", "password": "secret"},
                arguments_sanitized={"database": "Merchandising", "password": "[REDACTED]"},
                result_summary={"overall_status": "HEALTHY"},
                success=True,
                duration_seconds=0.5,
            ),
        ]
        resp = _make_response()
        persist_investigation_audit(resp, tool_calls, server="srv")

        audit = get_investigation_audit("inv-001")
        tc = audit.tools_called[0]
        assert tc.arguments_sanitized["database"] == "Merchandising"
        assert tc.arguments_sanitized["password"] == "[REDACTED]"
        assert tc.result_summary["overall_status"] == "HEALTHY"

    def test_clean_question_unchanged(self, audit_repo):
        """Questions without sensitive data pass through unchanged."""
        resp = _make_response(question="Why is Merchandising slow today?")
        persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audit = get_investigation_audit("inv-001")
        assert audit.question_sanitized == "Why is Merchandising slow today?"


# ─── Failed Tool Calls ───────────────────────────────────────────────────


class TestAuditFailedToolCalls:
    """Verify failed tool calls are recorded in the audit."""

    def test_failed_tool_call_recorded(self, audit_repo):
        """A failed tool call is persisted with success=False and error."""
        tool_calls = [
            ToolCall(
                tool_name="get_server_health",
                arguments={},
                success=False,
                error="Tool execution failed: connection error",
                duration_seconds=0.1,
            ),
        ]
        resp = _make_response()
        persist_investigation_audit(resp, tool_calls, server="srv")

        audit = get_investigation_audit("inv-001")
        assert len(audit.tools_called) == 1
        assert audit.tools_called[0].success is False
        assert "connection error" in audit.tools_called[0].error

    def test_mixed_success_and_failure(self, audit_repo):
        """Both successful and failed tool calls are recorded."""
        tool_calls = [
            ToolCall(tool_name="get_database_health", arguments={}, success=True, duration_seconds=1.0),
            ToolCall(tool_name="get_blocking", arguments={}, success=False, error="timeout", duration_seconds=5.0),
            ToolCall(tool_name="get_active_requests", arguments={}, success=True, duration_seconds=0.5),
        ]
        resp = _make_response()
        persist_investigation_audit(resp, tool_calls, server="srv")

        audit = get_investigation_audit("inv-001")
        assert len(audit.tools_called) == 3
        successes = [tc for tc in audit.tools_called if tc.success]
        failures = [tc for tc in audit.tools_called if not tc.success]
        assert len(successes) == 2
        assert len(failures) == 1
        assert failures[0].tool_name == "get_blocking"


# ─── No-Remediation Audit ────────────────────────────────────────────────


class TestNoRemediationAudit:
    """Verify every audit record confirms no remediation occurred."""

    def test_remediation_performed_is_false(self, audit_repo):
        """remediation_performed is always False in audit records."""
        resp = _make_response()
        persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audit = get_investigation_audit("inv-001")
        assert audit.remediation_performed is False

    def test_multiple_audits_all_no_remediation(self, audit_repo):
        """Every audit record in a batch confirms no remediation."""
        for i in range(5):
            resp = _make_response(investigation_id=f"inv-{i:03d}")
            persist_investigation_audit(resp, _make_tool_calls(), server="srv")

        audits = list_investigation_audits()
        for audit in audits:
            assert audit.remediation_performed is False

    def test_audit_record_model_default(self):
        """The model defaults remediation_performed to False."""
        record = InvestigationAuditRecord(
            investigation_id="test",
            question_sanitized="test",
            server="srv",
        )
        assert record.remediation_performed is False


# ─── Time Range Extraction ────────────────────────────────────────────────


class TestTimeRangeExtraction:
    """Tests for best-effort time range extraction from question text."""

    def test_extract_two_timestamps(self):
        text = "What was slow between 2026-08-24T09:00:00 and 2026-08-24T11:00:00?"
        start, end = _extract_time_range(text)
        assert start == "2026-08-24T09:00:00"
        assert end == "2026-08-24T11:00:00"

    def test_extract_single_timestamp(self):
        text = "What happened at 2026-08-24T14:30?"
        start, end = _extract_time_range(text)
        assert start == "2026-08-24T14:30"
        assert end is None

    def test_no_timestamps(self):
        text = "Why is the server slow?"
        start, end = _extract_time_range(text)
        assert start is None
        assert end is None


# ─── Finding Reference Extraction ─────────────────────────────────────────


class TestFindingReferenceExtraction:
    """Tests for extracting query hashes and procedure names."""

    def test_extract_affected_query(self):
        resp = _make_response(affected_query="dbo.uspExample", evidence=[])
        refs = _extract_finding_references(resp)
        assert "dbo.uspExample" in refs

    def test_extract_query_hash_from_evidence(self):
        resp = _make_response(
            affected_query=None,
            evidence=["Query hash: 0xABCDEF", "Duration: 47s"],
        )
        refs = _extract_finding_references(resp)
        assert "0xABCDEF" in refs

    def test_no_duplicates(self):
        resp = _make_response(
            affected_query="0xABC123",
            evidence=["Hash: 0xABC123"],
        )
        refs = _extract_finding_references(resp)
        assert refs.count("0xABC123") == 1

    def test_no_references(self):
        resp = _make_response(affected_query=None, evidence=[])
        refs = _extract_finding_references(resp)
        assert refs == []


# ─── Endpoint Tests ───────────────────────────────────────────────────────


class TestAuditEndpoints:
    """Tests for the audit trail GET endpoints."""

    @patch("app.main.list_investigation_audits")
    def test_list_investigations_empty(self, mock_list):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        mock_list.return_value = []
        response = client.get("/api/agent/investigations")
        assert response.status_code == 200
        data = response.json()
        assert data["total"] == 0
        assert data["investigations"] == []

    @patch("app.main.list_investigation_audits")
    def test_list_investigations_with_records(self, mock_list):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_list.return_value = [
            InvestigationAuditRecord(
                investigation_id="inv-001",
                created_at=now,
                question_sanitized="Why is Merchandising slow?",
                server="DCIR-ERPDBL",
                database="Merchandising",
                severity="high",
                confidence="medium",
                answer="Slow query found.",
                tools_called=[
                    AuditToolCall(tool_name="get_timeout_candidates", success=True, duration_seconds=1.5),
                ],
                finding_references=["dbo.uspGetOrders", "0xABC123"],
                human_review_required=True,
                remediation_performed=False,
            ),
        ]
        response = client.get("/api/agent/investigations")
        assert response.status_code == 200
        data = response.json()
        assert data["total"] == 1
        inv = data["investigations"][0]
        assert inv["investigation_id"] == "inv-001"
        assert inv["server"] == "DCIR-ERPDBL"
        assert inv["database"] == "Merchandising"
        assert inv["severity"] == "high"
        assert inv["remediation_performed"] is False
        assert len(inv["tools_called"]) == 1
        assert inv["tools_called"][0]["tool_name"] == "get_timeout_candidates"

    @patch("app.main.list_investigation_audits")
    def test_list_investigations_limit_param(self, mock_list):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        mock_list.return_value = []
        response = client.get("/api/agent/investigations", params={"limit": 5})
        assert response.status_code == 200
        mock_list.assert_called_once_with(limit=5)

    @patch("app.main.get_investigation_audit")
    def test_get_investigation_found(self, mock_get):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_get.return_value = InvestigationAuditRecord(
            investigation_id="inv-001",
            created_at=now,
            question_sanitized="Why slow?",
            server="srv",
            severity="high",
            confidence="medium",
            answer="Found issue.",
            remediation_performed=False,
        )
        response = client.get("/api/agent/investigations/inv-001")
        assert response.status_code == 200
        data = response.json()
        assert data["found"] is True
        assert data["investigation"]["investigation_id"] == "inv-001"
        assert data["investigation"]["remediation_performed"] is False

    @patch("app.main.get_investigation_audit")
    def test_get_investigation_not_found(self, mock_get):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        mock_get.return_value = None
        response = client.get("/api/agent/investigations/nonexistent")
        assert response.status_code == 200
        data = response.json()
        assert data["found"] is False
        assert data["investigation"] is None

    def test_list_investigations_limit_validation(self):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        response = client.get("/api/agent/investigations", params={"limit": 0})
        assert response.status_code == 422  # ge=1 validation

    def test_list_investigations_limit_max_validation(self):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        response = client.get("/api/agent/investigations", params={"limit": 1000})
        assert response.status_code == 422  # le=500 validation


# ─── Build Audit Record Tests ────────────────────────────────────────────


class TestBuildAuditRecord:
    """Tests for building the audit record from response + tool calls."""

    def test_build_basic_audit_record(self):
        """Build audit record from a standard response."""
        resp = _make_response()
        tool_calls = _make_tool_calls()
        record = build_audit_record(resp, tool_calls, server="srv")

        assert record.investigation_id == "inv-001"
        assert record.server == "srv"
        assert record.database == "Merchandising"
        assert record.severity == "high"
        assert record.confidence == "medium"
        assert record.remediation_performed is False
        assert len(record.tools_called) == 1
        assert record.tools_called[0].tool_name == "get_timeout_candidates"

    def test_build_includes_sanitized_args_and_summary(self):
        """Audit tool calls include sanitized arguments and result summary."""
        resp = _make_response()
        tool_calls = [
            ToolCall(
                tool_name="get_database_health",
                arguments={"database": "Sales", "password": "secret"},
                arguments_sanitized={"database": "Sales", "password": "[REDACTED]"},
                result_summary={"overall_status": "HEALTHY", "total_findings": 0},
                success=True,
                duration_seconds=1.0,
            ),
        ]
        record = build_audit_record(resp, tool_calls, server="srv")
        audit_tc = record.tools_called[0]
        assert audit_tc.arguments_sanitized["database"] == "Sales"
        assert audit_tc.arguments_sanitized["password"] == "[REDACTED]"
        assert audit_tc.result_summary["overall_status"] == "HEALTHY"

    def test_build_sanitizes_question(self):
        """Question with credentials is sanitized in audit record."""
        resp = _make_response(question="Check PWD=secret123 connection")
        record = build_audit_record(resp, _make_tool_calls(), server="srv")
        assert "secret123" not in record.question_sanitized
        assert "[REDACTED]" in record.question_sanitized

    def test_build_with_no_database(self):
        """Audit record handles None database."""
        resp = _make_response(affected_database=None)
        record = build_audit_record(resp, _make_tool_calls(), server="srv")
        assert record.database is None

    def test_build_with_time_range_in_question(self):
        """Time range is extracted from the question."""
        resp = _make_response(
            question="What was slow between 2026-08-24T09:00:00 and 2026-08-24T11:00:00?"
        )
        record = build_audit_record(resp, _make_tool_calls(), server="srv")
        assert record.time_range_start == "2026-08-24T09:00:00"
        assert record.time_range_end == "2026-08-24T11:00:00"
