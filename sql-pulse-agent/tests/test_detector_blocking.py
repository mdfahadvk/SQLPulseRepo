"""Tests for the BlockingDetector."""

from app.detectors.blocking import BlockingDetector
from app.detectors.thresholds import DetectorThresholds
from app.models.findings import Severity, FindingCategory
from app.models.performance import (
    BlockingSession,
    BlockingSessionsResult,
    CollectorMetadata,
    PerformanceSnapshot,
)


def _make_snapshot(sessions: list[BlockingSession], chain_count: int = 0) -> PerformanceSnapshot:
    meta = CollectorMetadata(collector_name="test", server_host="srv", database="db")
    return PerformanceSnapshot(
        snapshot_id="test-snap",
        server_host="srv",
        database="db",
        blocking_sessions=BlockingSessionsResult(
            metadata=meta, sessions=sessions, blocking_chain_count=chain_count
        ),
    )


def _default_thresholds() -> DetectorThresholds:
    return DetectorThresholds()


class TestBlockingDetector:
    def test_no_blocking_no_findings(self):
        snapshot = _make_snapshot([])
        detector = BlockingDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_blocking_below_threshold_no_findings(self):
        """Blocking with wait time below warning threshold → no finding."""
        s = BlockingSession(
            session_id=10, blocking_session_id=5,
            wait_time_ms=2_000, wait_type="LCK_M_X", is_head_blocker=False
        )
        snapshot = _make_snapshot([s])
        detector = BlockingDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_medium_severity_blocking(self):
        """Blocking with wait 10s (between 5s warning and 30s high) → medium."""
        s = BlockingSession(
            session_id=10, blocking_session_id=5,
            wait_time_ms=10_000, wait_type="LCK_M_X", is_head_blocker=False
        )
        snapshot = _make_snapshot([s])
        detector = BlockingDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.MEDIUM
        assert findings[0].category == FindingCategory.BLOCKING

    def test_high_severity_blocking(self):
        """Blocking with wait 40s → high."""
        s = BlockingSession(
            session_id=10, blocking_session_id=5,
            wait_time_ms=40_000, wait_type="LCK_M_X", is_head_blocker=True
        )
        snapshot = _make_snapshot([s])
        detector = BlockingDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.HIGH
        assert findings[0].human_review_required is True

    def test_critical_severity_by_wait_time(self):
        """Blocking with wait 70s → critical."""
        s = BlockingSession(
            session_id=10, blocking_session_id=5,
            wait_time_ms=70_000, wait_type="LCK_M_X", is_head_blocker=True
        )
        snapshot = _make_snapshot([s])
        detector = BlockingDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.CRITICAL

    def test_critical_severity_by_chain_count(self):
        """3+ sessions in blocking chain → critical."""
        sessions = [
            BlockingSession(
                session_id=i, blocking_session_id=i - 1,
                wait_time_ms=10_000, wait_type="LCK_M_X",
                is_head_blocker=(i == 1),
            )
            for i in range(1, 4)
        ]
        snapshot = _make_snapshot(sessions)
        detector = BlockingDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.CRITICAL

    def test_evidence_contains_session_info(self):
        s = BlockingSession(
            session_id=20, blocking_session_id=15,
            wait_time_ms=35_000, wait_type="LCK_M_S",
            sql_text="SELECT * FROM orders", is_head_blocker=False
        )
        snapshot = _make_snapshot([s])
        detector = BlockingDetector(thresholds=_default_thresholds())
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert "Session 20" in findings[0].evidence[0]
        assert "LCK_M_S" in findings[0].evidence[0]
