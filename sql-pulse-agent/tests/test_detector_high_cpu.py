"""Tests for the HighCpuDetector."""

from app.detectors.high_cpu import HighCpuDetector
from app.detectors.thresholds import DetectorThresholds
from app.models.findings import Severity, FindingCategory, Confidence
from app.models.performance import (
    CollectorMetadata,
    PerformanceSnapshot,
    TopCpuQueriesResult,
    TopCpuQuery,
)


def _make_snapshot(queries: list[TopCpuQuery]) -> PerformanceSnapshot:
    meta = CollectorMetadata(collector_name="test", server_host="srv", database="db")
    return PerformanceSnapshot(
        snapshot_id="test-snap",
        server_host="srv",
        database=None,
        top_cpu_queries=TopCpuQueriesResult(metadata=meta, top_n=20, queries=queries),
    )


def _default_thresholds() -> DetectorThresholds:
    return DetectorThresholds()


class TestHighCpuDetector:
    def test_no_queries_no_findings(self):
        snapshot = _make_snapshot([])
        detector = HighCpuDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_query_below_threshold_no_findings(self):
        """Query with low avg CPU → no finding."""
        q = TopCpuQuery(
            sql_text="SELECT 1",
            execution_count=100,
            total_cpu_time_ms=1000,
            avg_cpu_time_ms=10.0,
        )
        snapshot = _make_snapshot([q])
        detector = HighCpuDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_query_too_few_executions_no_findings(self):
        """High avg CPU but only 2 executions → skipped."""
        q = TopCpuQuery(
            sql_text="SELECT expensive()",
            execution_count=2,
            total_cpu_time_ms=100_000,
            avg_cpu_time_ms=50_000.0,
        )
        snapshot = _make_snapshot([q])
        detector = HighCpuDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_high_avg_cpu_medium_finding(self):
        """High avg CPU but low total → medium severity."""
        q = TopCpuQuery(
            sql_text="SELECT * FROM big",
            database_name="TestDB",
            execution_count=10,
            total_cpu_time_ms=80_000,  # Below high_total (600k)
            avg_cpu_time_ms=8_000.0,   # Above high_avg (5k)
        )
        snapshot = _make_snapshot([q])
        detector = HighCpuDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.MEDIUM
        assert findings[0].confidence == Confidence.MEDIUM
        assert findings[0].category == FindingCategory.HIGH_CPU

    def test_high_total_cpu_low_finding(self):
        """High total CPU but low avg → LOW severity (optimization candidate)."""
        q = TopCpuQuery(
            sql_text="SELECT frequent()",
            execution_count=200_000,
            total_cpu_time_ms=4_000_000,  # Above critical_total (3.6M)
            avg_cpu_time_ms=20.0,         # Below high_avg (5k)
        )
        snapshot = _make_snapshot([q])
        detector = HighCpuDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.LOW

    def test_high_avg_and_total_medium_finding(self):
        """Avg above high_avg threshold → MEDIUM severity."""
        q = TopCpuQuery(
            sql_text="SELECT * FROM massive_join",
            database_name="TestDB",
            execution_count=200,
            total_cpu_time_ms=1_200_000,  # Above high_total (600k)
            avg_cpu_time_ms=6_000.0,      # Above high_avg (5k) but below critical_avg (30k)
        )
        snapshot = _make_snapshot([q])
        detector = HighCpuDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.MEDIUM
        assert findings[0].confidence == Confidence.MEDIUM

    def test_critical_avg_cpu_high_finding(self):
        """Avg above critical_avg → HIGH (not CRITICAL, plan-cache only)."""
        q = TopCpuQuery(
            sql_text="SELECT * FROM enormous_cross_join",
            execution_count=100,
            total_cpu_time_ms=5_000_000,   # Above critical_total (3.6M)
            avg_cpu_time_ms=50_000.0,      # Above critical_avg (30k)
        )
        snapshot = _make_snapshot([q])
        detector = HighCpuDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.HIGH
        assert findings[0].human_review_required is True

    def test_evidence_contains_query_info(self):
        q = TopCpuQuery(
            sql_text="SELECT complex_query FROM t",
            query_hash="0xABCD",
            execution_count=50,
            total_cpu_time_ms=5_000_000,
            avg_cpu_time_ms=100_000.0,  # Above critical_avg to ensure finding
            database_name="TestDB",
        )
        snapshot = _make_snapshot([q])
        detector = HighCpuDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert any("0xABCD" in e for e in findings[0].evidence)
        assert any("TestDB" in e for e in findings[0].evidence)
