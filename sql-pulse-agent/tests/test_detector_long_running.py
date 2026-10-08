"""Tests for the LongRunningDetector."""

from app.detectors.long_running import LongRunningDetector
from app.detectors.thresholds import DetectorThresholds
from app.models.findings import Severity, FindingCategory
from app.models.performance import (
    CollectorMetadata,
    LongRunningQueriesResult,
    LongRunningQuery,
    PerformanceSnapshot,
)


def _make_snapshot(queries: list[LongRunningQuery], threshold_ms: int = 5000) -> PerformanceSnapshot:
    meta = CollectorMetadata(collector_name="test", server_host="srv", database="db")
    return PerformanceSnapshot(
        snapshot_id="test-snap",
        server_host="srv",
        database="db",
        long_running_queries=LongRunningQueriesResult(
            metadata=meta, threshold_ms=threshold_ms, queries=queries
        ),
    )


def _default_thresholds() -> DetectorThresholds:
    return DetectorThresholds()


class TestLongRunningDetector:
    def test_no_queries_no_findings(self):
        snapshot = _make_snapshot([])
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_query_below_threshold_no_findings(self):
        """A query running 8s with default warning at 10s → no finding."""
        q = LongRunningQuery(session_id=1, request_id=0, elapsed_time_ms=8_000)
        snapshot = _make_snapshot([q])
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_single_medium_tier_query_no_finding(self):
        """A single query at 15s (medium tier) → no finding alone (needs 3+)."""
        q = LongRunningQuery(session_id=1, request_id=0, elapsed_time_ms=15_000)
        snapshot = _make_snapshot([q])
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_three_medium_tier_queries_low_finding(self):
        """3 queries between 10s-30s → low severity finding."""
        queries = [
            LongRunningQuery(session_id=i, request_id=0, elapsed_time_ms=15_000)
            for i in range(3)
        ]
        snapshot = _make_snapshot(queries)
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.LOW

    def test_single_high_tier_query_medium_finding(self):
        """A query running 45s (between high 30s and critical 120s) → medium."""
        q = LongRunningQuery(
            session_id=10, request_id=0, elapsed_time_ms=45_000,
            sql_text="SELECT * FROM big_table", cpu_time_ms=20_000
        )
        snapshot = _make_snapshot([q])
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.MEDIUM
        assert findings[0].category == FindingCategory.LONG_RUNNING_QUERY

    def test_single_critical_tier_query_high_finding(self):
        """One query at 150s → HIGH (not critical for single query)."""
        q = LongRunningQuery(session_id=5, request_id=0, elapsed_time_ms=150_000)
        snapshot = _make_snapshot([q])
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.HIGH
        assert findings[0].human_review_required is True

    def test_multiple_critical_tier_queries_critical_finding(self):
        """3+ queries at 150s → CRITICAL."""
        queries = [
            LongRunningQuery(session_id=i, request_id=0, elapsed_time_ms=150_000)
            for i in range(3)
        ]
        snapshot = _make_snapshot(queries)
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        critical = [f for f in findings if f.severity == Severity.CRITICAL]
        assert len(critical) == 1
        assert critical[0].human_review_required is True

    def test_evidence_contains_session_info(self):
        q = LongRunningQuery(
            session_id=99, request_id=0, elapsed_time_ms=50_000,
            sql_text="SELECT 1", cpu_time_ms=1000, logical_reads=500
        )
        snapshot = _make_snapshot([q])
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert "Session 99" in findings[0].evidence[0]

    def test_failed_collector_no_findings(self):
        """If the collector failed, no findings should be produced."""
        meta = CollectorMetadata(
            collector_name="test", server_host="srv", database="db",
            success=False, error_message="permission denied"
        )
        snapshot = PerformanceSnapshot(
            snapshot_id="test",
            server_host="srv",
            database="db",
            long_running_queries=LongRunningQueriesResult(
                metadata=meta, threshold_ms=5000
            ),
        )
        detector = LongRunningDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []
