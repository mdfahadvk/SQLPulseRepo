"""Tests for the HighIoDetector."""

from app.detectors.high_io import HighIoDetector
from app.detectors.thresholds import DetectorThresholds
from app.models.findings import Severity, FindingCategory, Confidence
from app.models.performance import (
    CollectorMetadata,
    PerformanceSnapshot,
    TopIoQueriesResult,
    TopIoQuery,
)


def _make_snapshot(queries: list[TopIoQuery]) -> PerformanceSnapshot:
    meta = CollectorMetadata(collector_name="test", server_host="srv", database="db")
    return PerformanceSnapshot(
        snapshot_id="test-snap",
        server_host="srv",
        database=None,
        top_io_queries=TopIoQueriesResult(metadata=meta, top_n=20, queries=queries),
    )


def _default_thresholds() -> DetectorThresholds:
    return DetectorThresholds()


class TestHighIoDetector:
    def test_no_queries_no_findings(self):
        snapshot = _make_snapshot([])
        detector = HighIoDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_query_below_threshold_no_findings(self):
        """Low reads → no finding."""
        q = TopIoQuery(
            sql_text="SELECT 1",
            execution_count=100,
            total_logical_reads=5000,
            avg_logical_reads=50.0,
        )
        snapshot = _make_snapshot([q])
        detector = HighIoDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_query_too_few_executions_no_findings(self):
        """High reads but only 2 executions → skipped."""
        q = TopIoQuery(
            sql_text="SELECT expensive()",
            execution_count=2,
            total_logical_reads=200_000_000,
            avg_logical_reads=100_000_000.0,
        )
        snapshot = _make_snapshot([q])
        detector = HighIoDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_high_avg_reads_medium_finding(self):
        """High avg reads but low total → medium."""
        q = TopIoQuery(
            sql_text="SELECT * FROM large_table",
            database_name="TestDB",
            execution_count=10,
            total_logical_reads=800_000,      # Below high_total (10M)
            avg_logical_reads=80_000.0,       # Above high_avg (50k)
        )
        snapshot = _make_snapshot([q])
        detector = HighIoDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.MEDIUM
        assert findings[0].confidence == Confidence.MEDIUM

    def test_high_total_reads_low_finding(self):
        """High total reads but low avg → LOW (optimization candidate)."""
        q = TopIoQuery(
            sql_text="SELECT frequent()",
            execution_count=500_000,
            total_logical_reads=150_000_000,  # Above critical_total (100M)
            avg_logical_reads=300.0,          # Below high_avg (50k)
        )
        snapshot = _make_snapshot([q])
        detector = HighIoDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.LOW

    def test_high_avg_reads_medium_with_total(self):
        """Avg above high_avg threshold → MEDIUM."""
        q = TopIoQuery(
            sql_text="SELECT * FROM scan_everything",
            database_name="TestDB",
            execution_count=500,
            total_logical_reads=30_000_000,   # Above high_total (10M)
            avg_logical_reads=60_000.0,       # Above high_avg (50k) but below critical_avg (500k)
        )
        snapshot = _make_snapshot([q])
        detector = HighIoDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.MEDIUM
        assert findings[0].confidence == Confidence.MEDIUM

    def test_critical_avg_io_high_finding(self):
        """Avg above critical_avg → HIGH (not CRITICAL, plan-cache only)."""
        q = TopIoQuery(
            sql_text="SELECT * FROM billion_row_table",
            execution_count=100,
            total_logical_reads=200_000_000,     # Above critical_total (100M)
            avg_logical_reads=2_000_000.0,       # Above critical_avg (500k)
        )
        snapshot = _make_snapshot([q])
        detector = HighIoDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.HIGH
        assert findings[0].category == FindingCategory.HIGH_IO

    def test_evidence_contains_reads_info(self):
        q = TopIoQuery(
            sql_text="SELECT cols FROM t WHERE x > 1",
            query_hash="0xDEAD",
            execution_count=50,
            total_logical_reads=200_000_000,
            avg_logical_reads=4_000_000.0,  # Above critical_avg to ensure finding
            total_physical_reads=5_000,
            avg_physical_reads=100.0,
            database_name="ProdDB",
        )
        snapshot = _make_snapshot([q])
        detector = HighIoDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert any("0xDEAD" in e for e in findings[0].evidence)
        assert any("ProdDB" in e for e in findings[0].evidence)
