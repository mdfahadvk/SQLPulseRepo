"""Tests for the detection engine and analyze endpoint."""

from unittest.mock import patch

from fastapi.testclient import TestClient

from app.detectors.engine import run_analysis, _sort_findings
from app.detectors.thresholds import DetectorThresholds
from app.main import app
from app.models.findings import (
    AnalysisResult,
    Confidence,
    FindingCategory,
    PerformanceFinding,
    Severity,
)
from app.models.performance import (
    BlockingSession,
    BlockingSessionsResult,
    CollectorMetadata,
    LongRunningQueriesResult,
    LongRunningQuery,
    PerformanceSnapshot,
    TopCpuQueriesResult,
    TopCpuQuery,
    TopIoQueriesResult,
    TopIoQuery,
)

client = TestClient(app)


def _meta() -> CollectorMetadata:
    return CollectorMetadata(collector_name="test", server_host="srv", database="db")


class TestSortFindings:
    def test_sorts_by_severity(self):
        findings = [
            PerformanceFinding(
                finding_id="1", category=FindingCategory.HIGH_CPU,
                server="s", database="d", severity=Severity.LOW,
                confidence=Confidence.HIGH, summary="low",
                evidence=[], likely_cause="", recommendation="",
            ),
            PerformanceFinding(
                finding_id="2", category=FindingCategory.BLOCKING,
                server="s", database="d", severity=Severity.CRITICAL,
                confidence=Confidence.HIGH, summary="critical",
                evidence=[], likely_cause="", recommendation="",
            ),
            PerformanceFinding(
                finding_id="3", category=FindingCategory.HIGH_IO,
                server="s", database="d", severity=Severity.MEDIUM,
                confidence=Confidence.HIGH, summary="medium",
                evidence=[], likely_cause="", recommendation="",
            ),
        ]
        sorted_f = _sort_findings(findings)
        assert sorted_f[0].severity == Severity.CRITICAL
        assert sorted_f[1].severity == Severity.MEDIUM
        assert sorted_f[2].severity == Severity.LOW


class TestRunAnalysis:
    def test_normal_conditions_no_findings(self):
        """A healthy server snapshot should produce zero findings."""
        snapshot = PerformanceSnapshot(
            snapshot_id="healthy",
            server_host="srv",
            database="db",
            long_running_queries=LongRunningQueriesResult(
                metadata=_meta(), threshold_ms=5000, queries=[]
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=_meta(), sessions=[]
            ),
            top_cpu_queries=TopCpuQueriesResult(
                metadata=_meta(), top_n=20, queries=[
                    TopCpuQuery(execution_count=100, avg_cpu_time_ms=50.0, total_cpu_time_ms=5000)
                ]
            ),
            top_io_queries=TopIoQueriesResult(
                metadata=_meta(), top_n=20, queries=[
                    TopIoQuery(execution_count=100, avg_logical_reads=100.0, total_logical_reads=10000)
                ]
            ),
        )
        result = run_analysis(snapshot)
        assert result.total_findings == 0
        assert result.findings == []

    def test_multiple_issues_detected(self):
        """Snapshot with long-running + blocking → multiple findings."""
        snapshot = PerformanceSnapshot(
            snapshot_id="bad",
            server_host="srv",
            database="db",
            long_running_queries=LongRunningQueriesResult(
                metadata=_meta(), threshold_ms=5000, queries=[
                    LongRunningQuery(session_id=1, request_id=0, elapsed_time_ms=50_000),
                ]
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=_meta(), sessions=[
                    BlockingSession(
                        session_id=10, blocking_session_id=5,
                        wait_time_ms=40_000, wait_type="LCK_M_X", is_head_blocker=True
                    ),
                ]
            ),
            top_cpu_queries=TopCpuQueriesResult(metadata=_meta(), top_n=20, queries=[]),
            top_io_queries=TopIoQueriesResult(metadata=_meta(), top_n=20, queries=[]),
        )
        result = run_analysis(snapshot)
        assert result.total_findings >= 2
        categories = {f.category for f in result.findings}
        assert FindingCategory.LONG_RUNNING_QUERY in categories
        assert FindingCategory.BLOCKING in categories

    def test_counts_match_findings(self):
        """Severity counts should match actual findings."""
        snapshot = PerformanceSnapshot(
            snapshot_id="test",
            server_host="srv",
            database="db",
            long_running_queries=LongRunningQueriesResult(
                metadata=_meta(), threshold_ms=5000, queries=[
                    LongRunningQuery(session_id=i, request_id=0, elapsed_time_ms=150_000)
                    for i in range(3)
                ]
            ),
            blocking_sessions=BlockingSessionsResult(metadata=_meta(), sessions=[]),
            top_cpu_queries=TopCpuQueriesResult(metadata=_meta(), top_n=20, queries=[]),
            top_io_queries=TopIoQueriesResult(metadata=_meta(), top_n=20, queries=[]),
        )
        result = run_analysis(snapshot)
        assert result.critical_count == sum(
            1 for f in result.findings if f.severity == Severity.CRITICAL
        )
        assert result.total_findings == len(result.findings)


class TestAnalyzeEndpoint:
    @patch("app.detectors.engine.collect_performance_snapshot")
    def test_endpoint_returns_200(self, mock_snapshot):
        mock_snapshot.return_value = PerformanceSnapshot(
            snapshot_id="ep-test",
            server_host="srv",
            database="db",
            long_running_queries=LongRunningQueriesResult(
                metadata=_meta(), threshold_ms=5000, queries=[]
            ),
            blocking_sessions=BlockingSessionsResult(metadata=_meta(), sessions=[]),
            top_cpu_queries=TopCpuQueriesResult(metadata=_meta(), top_n=20, queries=[]),
            top_io_queries=TopIoQueriesResult(metadata=_meta(), top_n=20, queries=[]),
        )
        response = client.get("/api/performance/analyze")
        assert response.status_code == 200
        data = response.json()
        assert "analysis_id" in data
        assert "findings" in data
        assert data["total_findings"] == 0

    @patch("app.detectors.engine.collect_performance_snapshot")
    def test_endpoint_returns_findings_ordered_by_severity(self, mock_snapshot):
        mock_snapshot.return_value = PerformanceSnapshot(
            snapshot_id="ep-test-2",
            server_host="srv",
            database="db",
            long_running_queries=LongRunningQueriesResult(
                metadata=_meta(), threshold_ms=5000, queries=[
                    LongRunningQuery(session_id=1, request_id=0, elapsed_time_ms=50_000),
                ]
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=_meta(), sessions=[
                    BlockingSession(
                        session_id=10, blocking_session_id=5,
                        wait_time_ms=70_000, wait_type="LCK_M_X", is_head_blocker=True
                    ),
                ]
            ),
            top_cpu_queries=TopCpuQueriesResult(metadata=_meta(), top_n=20, queries=[]),
            top_io_queries=TopIoQueriesResult(metadata=_meta(), top_n=20, queries=[]),
        )
        response = client.get("/api/performance/analyze")
        assert response.status_code == 200
        data = response.json()
        assert data["total_findings"] >= 2
        # First finding should be highest severity
        severities = [f["severity"] for f in data["findings"]]
        assert severities[0] == "critical"
