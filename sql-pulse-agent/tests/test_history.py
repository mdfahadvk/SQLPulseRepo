"""Tests for historical storage, baseline calculation, and regression detection."""

import os
import tempfile
from datetime import UTC, datetime, timedelta
from unittest.mock import patch

import pytest

from app.detectors.regression import QueryRegressionDetector
from app.detectors.thresholds import DetectorThresholds
from app.models.agent import InvestigationAuditRecord
from app.models.findings import (
    Confidence,
    FindingCategory,
    HealthSection,
    OverallStatus,
    Severity,
)
from app.models.history import (
    BaselineStatistic,
    BaselineStatus,
    QueryBaseline,
    QueryMetricRecord,
    SnapshotRecord,
)
from app.models.performance import (
    BlockingSessionsResult,
    CollectorMetadata,
    LongRunningQueriesResult,
    PerformanceSnapshot,
    TopCpuQueriesResult,
    TopCpuQuery,
    TopIoQueriesResult,
    TopIoQuery,
)
from app.storage.baseline_service import _compute_statistic, calculate_baseline
from app.storage.history_service import persist_snapshot, set_repository
from app.storage.sqlite_repository import SQLiteHistoryRepository


def _meta() -> CollectorMetadata:
    return CollectorMetadata(collector_name="test", server_host="srv", database="db")


def _make_snapshot(
    snapshot_id: str = "snap-1",
    server: str = "srv",
    database: str | None = None,
    cpu_queries: list[TopCpuQuery] | None = None,
    io_queries: list[TopIoQuery] | None = None,
) -> PerformanceSnapshot:
    return PerformanceSnapshot(
        snapshot_id=snapshot_id,
        server_host=server,
        database=database,
        long_running_queries=LongRunningQueriesResult(
            metadata=_meta(), threshold_ms=5000, queries=[]
        ),
        blocking_sessions=BlockingSessionsResult(metadata=_meta(), sessions=[]),
        top_cpu_queries=TopCpuQueriesResult(
            metadata=_meta(), top_n=20, queries=cpu_queries or []
        ),
        top_io_queries=TopIoQueriesResult(
            metadata=_meta(), top_n=20, queries=io_queries or []
        ),
    )


@pytest.fixture
def sqlite_repo():
    """Create a temporary SQLite repository for testing."""
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


class TestSQLiteRepository:
    """Tests for the SQLite repository implementation."""

    def test_initialize_creates_tables(self, sqlite_repo):
        with sqlite_repo._connect() as conn:
            tables = conn.execute(
                "SELECT name FROM sqlite_master WHERE type='table'"
            ).fetchall()
            names = {row["name"] for row in tables}
        assert "snapshots" in names
        assert "query_metrics" in names

    def test_store_and_check_snapshot(self, sqlite_repo):
        record = SnapshotRecord(
            snapshot_id="s1",
            collected_at=datetime.now(UTC),
            server="srv",
            scope="server",
        )
        sqlite_repo.store_snapshot(record)
        assert sqlite_repo.snapshot_exists("s1")
        assert not sqlite_repo.snapshot_exists("s2")

    def test_store_snapshot_duplicate_ignored(self, sqlite_repo):
        record = SnapshotRecord(
            snapshot_id="s1",
            collected_at=datetime.now(UTC),
            server="srv",
            scope="server",
        )
        sqlite_repo.store_snapshot(record)
        # Should not raise on duplicate
        sqlite_repo.store_snapshot(record)
        assert sqlite_repo.snapshot_exists("s1")

    def test_store_and_retrieve_query_metrics(self, sqlite_repo):
        # Store snapshot first
        sqlite_repo.store_snapshot(SnapshotRecord(
            snapshot_id="s1", collected_at=datetime.now(UTC),
            server="srv", scope="server",
        ))
        metrics = [
            QueryMetricRecord(
                snapshot_id="s1",
                collected_at=datetime.now(UTC),
                server="srv",
                database="testdb",
                query_hash="hash123",
                avg_cpu_time_ms=50.0,
                avg_elapsed_time_ms=100.0,
                avg_logical_reads=500.0,
                execution_count=10,
            )
        ]
        sqlite_repo.store_query_metrics(metrics)
        history = sqlite_repo.get_query_history("hash123", "testdb")
        assert len(history) == 1
        assert history[0].query_hash == "hash123"
        assert history[0].avg_cpu_time_ms == 50.0

    def test_get_query_metrics_for_baseline(self, sqlite_repo):
        now = datetime.now(UTC)
        sqlite_repo.store_snapshot(SnapshotRecord(
            snapshot_id="s1", collected_at=now, server="srv", scope="server",
        ))
        sqlite_repo.store_snapshot(SnapshotRecord(
            snapshot_id="s2", collected_at=now - timedelta(hours=2), server="srv", scope="server",
        ))
        sqlite_repo.store_query_metrics([
            QueryMetricRecord(
                snapshot_id="s1", collected_at=now,
                server="srv", database="db1", query_hash="h1",
                avg_cpu_time_ms=50.0, avg_elapsed_time_ms=100.0,
            ),
            QueryMetricRecord(
                snapshot_id="s2", collected_at=now - timedelta(hours=2),
                server="srv", database="db1", query_hash="h1",
                avg_cpu_time_ms=45.0, avg_elapsed_time_ms=90.0,
            ),
        ])
        # Should get both records (within 24h window)
        since = now - timedelta(hours=24)
        records = sqlite_repo.get_query_metrics_for_baseline("h1", "db1", since)
        assert len(records) == 2

        # Narrow window should exclude old one
        since_narrow = now - timedelta(hours=1)
        records = sqlite_repo.get_query_metrics_for_baseline("h1", "db1", since_narrow)
        assert len(records) == 1

    def test_delete_older_than(self, sqlite_repo):
        now = datetime.now(UTC)
        old = now - timedelta(days=40)
        sqlite_repo.store_snapshot(SnapshotRecord(
            snapshot_id="old", collected_at=old, server="srv", scope="server",
        ))
        sqlite_repo.store_snapshot(SnapshotRecord(
            snapshot_id="new", collected_at=now, server="srv", scope="server",
        ))
        sqlite_repo.store_query_metrics([
            QueryMetricRecord(
                snapshot_id="old", collected_at=old,
                server="srv", database="db1", query_hash="h1",
                avg_cpu_time_ms=50.0,
            ),
            QueryMetricRecord(
                snapshot_id="new", collected_at=now,
                server="srv", database="db1", query_hash="h1",
                avg_cpu_time_ms=60.0,
            ),
        ])
        cutoff = now - timedelta(days=30)
        deleted = sqlite_repo.delete_older_than(cutoff)
        assert deleted > 0
        # New record still there
        assert sqlite_repo.snapshot_exists("new")
        assert not sqlite_repo.snapshot_exists("old")

    def test_duplicate_query_metric_ignored(self, sqlite_repo):
        now = datetime.now(UTC)
        sqlite_repo.store_snapshot(SnapshotRecord(
            snapshot_id="s1", collected_at=now, server="srv", scope="server",
        ))
        metric = QueryMetricRecord(
            snapshot_id="s1", collected_at=now,
            server="srv", database="db1", query_hash="h1",
            avg_cpu_time_ms=50.0,
        )
        sqlite_repo.store_query_metrics([metric])
        sqlite_repo.store_query_metrics([metric])  # Should not raise
        history = sqlite_repo.get_query_history("h1", "db1")
        assert len(history) == 1


class TestHistoryService:
    """Tests for persist_snapshot service."""

    def test_persist_snapshot_extracts_metrics(self, sqlite_repo):
        snapshot = _make_snapshot(
            snapshot_id="persist-1",
            cpu_queries=[
                TopCpuQuery(
                    query_hash="qh1", database_name="mydb",
                    execution_count=100, avg_cpu_time_ms=50.0,
                    total_cpu_time_ms=5000, avg_elapsed_time_ms=100.0,
                    total_elapsed_time_ms=10000, avg_logical_reads=200.0,
                    total_logical_reads=20000,
                ),
            ],
        )
        persist_snapshot(snapshot)
        assert sqlite_repo.snapshot_exists("persist-1")
        history = sqlite_repo.get_query_history("qh1", "mydb")
        assert len(history) == 1
        assert history[0].avg_cpu_time_ms == 50.0

    def test_persist_snapshot_skips_duplicate(self, sqlite_repo):
        snapshot = _make_snapshot(
            snapshot_id="dup-1",
            cpu_queries=[
                TopCpuQuery(
                    query_hash="qh2", database_name="mydb",
                    execution_count=10, avg_cpu_time_ms=20.0,
                    total_cpu_time_ms=200,
                ),
            ],
        )
        persist_snapshot(snapshot)
        persist_snapshot(snapshot)  # Should be silently skipped
        # Only one record
        history = sqlite_repo.get_query_history("qh2", "mydb")
        assert len(history) == 1

    def test_persist_snapshot_handles_no_queries(self, sqlite_repo):
        snapshot = _make_snapshot(snapshot_id="empty-1")
        persist_snapshot(snapshot)
        assert sqlite_repo.snapshot_exists("empty-1")

    def test_persist_snapshot_merges_io_data(self, sqlite_repo):
        snapshot = _make_snapshot(
            snapshot_id="merge-1",
            cpu_queries=[
                TopCpuQuery(
                    query_hash="qm1", database_name="db",
                    execution_count=50, avg_cpu_time_ms=30.0,
                    total_cpu_time_ms=1500, avg_logical_reads=100.0,
                    total_logical_reads=5000,
                ),
            ],
            io_queries=[
                TopIoQuery(
                    query_hash="qm1", database_name="db",
                    execution_count=50, avg_logical_reads=150.0,
                    total_logical_reads=7500, avg_physical_reads=25.0,
                    total_physical_reads=1250,
                ),
            ],
        )
        persist_snapshot(snapshot)
        history = sqlite_repo.get_query_history("qm1", "db")
        assert len(history) == 1
        # IO data should be merged (higher reads value taken)
        assert history[0].avg_logical_reads == 150.0
        assert history[0].avg_physical_reads == 25.0


class TestBaselineService:
    """Tests for baseline calculation."""

    def test_compute_statistic_basic(self):
        values = [10.0, 20.0, 30.0, 40.0, 50.0]
        stat = _compute_statistic(values)
        assert stat.sample_count == 5
        assert stat.mean == 30.0
        assert stat.median == 30.0
        assert stat.minimum == 10.0
        assert stat.maximum == 50.0
        assert stat.p50 == 30.0
        assert stat.p95 == 50.0
        assert stat.standard_deviation > 0

    def test_compute_statistic_single_value(self):
        stat = _compute_statistic([42.0])
        assert stat.sample_count == 1
        assert stat.mean == 42.0
        assert stat.standard_deviation == 0.0

    def test_compute_statistic_empty(self):
        stat = _compute_statistic([])
        assert stat.sample_count == 0

    def test_calculate_baseline_insufficient_data(self, sqlite_repo):
        # No data stored yet
        baseline = calculate_baseline("nonexistent", "db", "7d", min_samples=5)
        assert baseline.baseline_status == BaselineStatus.INSUFFICIENT_DATA
        assert baseline.sample_count == 0

    def test_calculate_baseline_ready(self, sqlite_repo):
        now = datetime.now(UTC)
        # Store enough samples
        for i in range(10):
            snap_id = f"base-{i}"
            t = now - timedelta(hours=i)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="bh1",
                    avg_cpu_time_ms=50.0 + i,
                    avg_elapsed_time_ms=100.0 + i * 2,
                    avg_logical_reads=500.0 + i * 10,
                    execution_count=20 + i,
                ),
            ])

        baseline = calculate_baseline("bh1", "testdb", "7d", min_samples=5)
        assert baseline.baseline_status == BaselineStatus.READY
        assert baseline.sample_count == 10
        assert baseline.avg_cpu_ms is not None
        assert baseline.avg_cpu_ms.mean > 0
        assert baseline.avg_elapsed_ms is not None
        assert baseline.avg_logical_reads is not None

    def test_calculate_baseline_invalid_window(self, sqlite_repo):
        with pytest.raises(ValueError, match="Unsupported window"):
            calculate_baseline("h1", "db", "99x")


class TestRegressionDetector:
    """Tests for the QueryRegressionDetector."""

    def _make_thresholds(self) -> DetectorThresholds:
        return DetectorThresholds(
            regression_warning_multiplier=2.0,
            regression_high_multiplier=4.0,
            regression_critical_multiplier=8.0,
            regression_min_samples=5,
            regression_baseline_window="7d",
        )

    def test_no_findings_without_baseline(self, sqlite_repo):
        """No baseline data → no regression findings."""
        detector = QueryRegressionDetector(thresholds=self._make_thresholds())
        snapshot = _make_snapshot(
            cpu_queries=[
                TopCpuQuery(
                    query_hash="no_baseline_q", database_name="db",
                    execution_count=10, avg_cpu_time_ms=500.0,
                    total_cpu_time_ms=5000,
                ),
            ]
        )
        findings = detector.detect(snapshot)
        assert len(findings) == 0

    def test_detects_regression_above_threshold(self, sqlite_repo):
        """Query 4x slower than baseline should trigger HIGH finding."""
        now = datetime.now(UTC)
        # Create baseline data: avg_elapsed_ms ≈ 100
        for i in range(10):
            snap_id = f"reg-{i}"
            t = now - timedelta(hours=i + 1)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="regq1",
                    avg_cpu_time_ms=50.0,
                    avg_elapsed_time_ms=100.0,
                    avg_logical_reads=500.0,
                    execution_count=20,
                ),
            ])

        # Current snapshot: avg_elapsed_ms = 450 (4.5x baseline)
        detector = QueryRegressionDetector(thresholds=self._make_thresholds())
        snapshot = _make_snapshot(
            cpu_queries=[
                TopCpuQuery(
                    query_hash="regq1", database_name="testdb",
                    execution_count=20, avg_cpu_time_ms=50.0,
                    total_cpu_time_ms=1000, avg_elapsed_time_ms=450.0,
                    total_elapsed_time_ms=9000, avg_logical_reads=500.0,
                    total_logical_reads=10000,
                ),
            ]
        )
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].category == FindingCategory.QUERY_REGRESSION
        assert findings[0].severity == Severity.HIGH
        assert "4.5x" in findings[0].summary

    def test_no_regression_when_within_baseline(self, sqlite_repo):
        """Query performing within normal range → no findings."""
        now = datetime.now(UTC)
        for i in range(10):
            snap_id = f"ok-{i}"
            t = now - timedelta(hours=i + 1)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="okq1",
                    avg_cpu_time_ms=50.0,
                    avg_elapsed_time_ms=100.0,
                    avg_logical_reads=500.0,
                    execution_count=20,
                ),
            ])

        # Current: only 1.5x baseline (below 2x warning threshold)
        detector = QueryRegressionDetector(thresholds=self._make_thresholds())
        snapshot = _make_snapshot(
            cpu_queries=[
                TopCpuQuery(
                    query_hash="okq1", database_name="testdb",
                    execution_count=20, avg_cpu_time_ms=75.0,
                    total_cpu_time_ms=1500, avg_elapsed_time_ms=150.0,
                    total_elapsed_time_ms=3000, avg_logical_reads=750.0,
                    total_logical_reads=15000,
                ),
            ]
        )
        findings = detector.detect(snapshot)
        assert len(findings) == 0

    def test_critical_regression(self, sqlite_repo):
        """Query 10x slower → CRITICAL severity."""
        now = datetime.now(UTC)
        for i in range(10):
            snap_id = f"crit-{i}"
            t = now - timedelta(hours=i + 1)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="critq1",
                    avg_cpu_time_ms=50.0,
                    avg_elapsed_time_ms=100.0,
                    avg_logical_reads=500.0,
                    execution_count=20,
                ),
            ])

        detector = QueryRegressionDetector(thresholds=self._make_thresholds())
        snapshot = _make_snapshot(
            cpu_queries=[
                TopCpuQuery(
                    query_hash="critq1", database_name="testdb",
                    execution_count=20, avg_cpu_time_ms=50.0,
                    total_cpu_time_ms=1000, avg_elapsed_time_ms=1000.0,
                    total_elapsed_time_ms=20000, avg_logical_reads=500.0,
                    total_logical_reads=10000,
                ),
            ]
        )
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].severity == Severity.CRITICAL

    def test_confidence_high_with_stable_baseline(self, sqlite_repo):
        """Many samples with low variance → HIGH confidence."""
        now = datetime.now(UTC)
        # Stable baseline: all values exactly 100
        for i in range(15):
            snap_id = f"stable-{i}"
            t = now - timedelta(hours=i + 1)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="stableq",
                    avg_cpu_time_ms=100.0,
                    avg_elapsed_time_ms=100.0,
                    avg_logical_reads=1000.0,
                    execution_count=50,
                ),
            ])

        detector = QueryRegressionDetector(thresholds=self._make_thresholds())
        snapshot = _make_snapshot(
            cpu_queries=[
                TopCpuQuery(
                    query_hash="stableq", database_name="testdb",
                    execution_count=50, avg_cpu_time_ms=100.0,
                    total_cpu_time_ms=5000, avg_elapsed_time_ms=500.0,
                    total_elapsed_time_ms=25000, avg_logical_reads=1000.0,
                    total_logical_reads=50000,
                ),
            ]
        )
        findings = detector.detect(snapshot)
        assert len(findings) == 1
        assert findings[0].confidence == Confidence.HIGH


class TestHealthOptimizationSeparation:
    """Tests for health vs optimization separation in engine output."""

    def test_empty_snapshot_has_healthy_status(self, sqlite_repo):
        from app.detectors.engine import run_analysis

        snapshot = _make_snapshot()
        result = run_analysis(snapshot)
        assert result.overall_status == OverallStatus.HEALTHY
        assert result.current_health is not None
        assert result.current_health.status == OverallStatus.HEALTHY
        assert result.current_health.findings == []
        assert result.optimization_opportunities == []

    def test_high_cpu_goes_to_optimization(self, sqlite_repo):
        from app.detectors.engine import run_analysis

        snapshot = _make_snapshot(
            cpu_queries=[
                TopCpuQuery(
                    query_hash="cpu1", database_name="db",
                    execution_count=10, avg_cpu_time_ms=50000.0,
                    total_cpu_time_ms=500000,
                ),
            ]
        )
        result = run_analysis(snapshot)
        # HIGH_CPU findings go to optimization, not health
        if result.optimization_opportunities:
            for f in result.optimization_opportunities:
                assert f.category == FindingCategory.HIGH_CPU
        # Health should still be HEALTHY since HIGH_CPU is optimization-only
        assert result.current_health is not None
        assert result.current_health.status == OverallStatus.HEALTHY

    def test_historical_analysis_status_in_result(self, sqlite_repo):
        from app.detectors.engine import run_analysis

        snapshot = _make_snapshot()
        result = run_analysis(snapshot)
        assert result.historical_analysis_status in ("available", "unavailable")


class TestHistoryEndpoints:
    """Tests for the /api/history/ endpoints."""

    def test_query_history_returns_data(self, sqlite_repo):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        # Store some data first
        now = datetime.now(UTC)
        sqlite_repo.store_snapshot(SnapshotRecord(
            snapshot_id="ep-s1", collected_at=now, server="srv", scope="server",
        ))
        sqlite_repo.store_query_metrics([
            QueryMetricRecord(
                snapshot_id="ep-s1", collected_at=now,
                server="srv", database="mydb", query_hash="ep_hash1",
                avg_cpu_time_ms=50.0, avg_elapsed_time_ms=100.0,
            ),
        ])

        response = client.get("/api/history/query/ep_hash1?database=mydb")
        assert response.status_code == 200
        data = response.json()
        assert data["query_hash"] == "ep_hash1"
        assert data["database"] == "mydb"
        assert len(data["samples"]) == 1

    def test_query_history_empty(self, sqlite_repo):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        response = client.get("/api/history/query/nonexistent?database=db")
        assert response.status_code == 200
        data = response.json()
        assert data["samples"] == []

    def test_query_baseline_insufficient_data(self, sqlite_repo):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        response = client.get("/api/history/query/nodata/baseline?database=db")
        assert response.status_code == 200
        data = response.json()
        assert data["baseline_status"] == "INSUFFICIENT_DATA"
        assert data["sample_count"] == 0

    def test_query_baseline_invalid_window(self, sqlite_repo):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        response = client.get("/api/history/query/h1/baseline?database=db&window=99x")
        assert response.status_code == 400

    def test_query_baseline_with_data(self, sqlite_repo):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        for i in range(10):
            snap_id = f"bep-{i}"
            t = now - timedelta(hours=i)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="bep_hash",
                    avg_cpu_time_ms=50.0 + i,
                    avg_elapsed_time_ms=100.0 + i,
                    avg_logical_reads=500.0,
                    execution_count=20,
                ),
            ])

        response = client.get("/api/history/query/bep_hash/baseline?database=testdb&window=7d")
        assert response.status_code == 200
        data = response.json()
        assert data["baseline_status"] == "READY"
        assert data["sample_count"] == 10
        assert "avg_cpu_ms" in data["metrics"]
        assert data["metrics"]["avg_cpu_ms"]["mean"] > 0

    def test_query_history_requires_database_param(self, sqlite_repo):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        response = client.get("/api/history/query/hash1")
        assert response.status_code == 422  # Missing required query param


# ─── Concurrency Tests ───────────────────────────────────────────────────


class TestSQLiteConcurrency:
    """Verify SQLite handles concurrent reads and writes without locking errors."""

    def test_concurrent_audit_writes(self, sqlite_repo):
        """Multiple threads writing audit records simultaneously should not deadlock."""
        import threading

        errors: list[Exception] = []
        barrier = threading.Barrier(4)

        def _write_audit(idx: int) -> None:
            try:
                barrier.wait(timeout=5)
                record = InvestigationAuditRecord(
                    investigation_id=f"conc-{idx}",
                    created_at=datetime.now(UTC),
                    question_sanitized=f"question {idx}",
                    server="srv",
                    severity="low",
                    confidence="low",
                    answer=f"answer {idx}",
                    human_review_required=False,
                    remediation_performed=False,
                )
                sqlite_repo.store_investigation_audit(record)
            except Exception as exc:
                errors.append(exc)

        threads = [threading.Thread(target=_write_audit, args=(i,)) for i in range(4)]
        for t in threads:
            t.start()
        for t in threads:
            t.join(timeout=60)

        assert errors == [], f"Concurrent audit writes failed: {errors}"
        audits = sqlite_repo.list_investigation_audits(limit=10)
        assert len(audits) == 4

    def test_concurrent_reads_during_write(self, sqlite_repo):
        """Reads should succeed while another thread is writing."""
        import threading

        # Seed some data so reads return something
        now = datetime.now(UTC)
        sqlite_repo.store_snapshot(SnapshotRecord(
            snapshot_id="seed", collected_at=now, server="srv", scope="server",
        ))
        sqlite_repo.store_query_metrics([
            QueryMetricRecord(
                snapshot_id="seed", collected_at=now,
                server="srv", database="db1", query_hash="rw_hash",
                avg_cpu_time_ms=10.0,
            ),
        ])

        errors: list[Exception] = []
        read_results: list[int] = []
        barrier = threading.Barrier(3)

        def _bulk_write() -> None:
            try:
                barrier.wait(timeout=5)
                for i in range(20):
                    snap_id = f"bw-{i}"
                    sqlite_repo.store_snapshot(SnapshotRecord(
                        snapshot_id=snap_id, collected_at=now,
                        server="srv", scope="server",
                    ))
                    sqlite_repo.store_query_metrics([
                        QueryMetricRecord(
                            snapshot_id=snap_id, collected_at=now,
                            server="srv", database="db1",
                            query_hash=f"bwh-{i}",
                            avg_cpu_time_ms=float(i),
                        ),
                    ])
            except Exception as exc:
                errors.append(exc)

        def _repeated_read() -> None:
            try:
                barrier.wait(timeout=5)
                for _ in range(20):
                    history = sqlite_repo.get_query_history("rw_hash", "db1")
                    read_results.append(len(history))
            except Exception as exc:
                errors.append(exc)

        writer = threading.Thread(target=_bulk_write)
        reader1 = threading.Thread(target=_repeated_read)
        reader2 = threading.Thread(target=_repeated_read)

        writer.start()
        reader1.start()
        reader2.start()
        writer.join(timeout=60)
        reader1.join(timeout=60)
        reader2.join(timeout=60)

        assert errors == [], f"Concurrent read/write failed: {errors}"
        # Every read should have seen at least the seed record
        assert all(r >= 1 for r in read_results)

    def test_concurrent_audit_write_and_list(self, sqlite_repo):
        """Listing audits while another thread writes should not raise."""
        import threading

        errors: list[Exception] = []
        barrier = threading.Barrier(2)

        def _write_audits() -> None:
            try:
                barrier.wait(timeout=5)
                for i in range(10):
                    record = InvestigationAuditRecord(
                        investigation_id=f"wl-{i}",
                        created_at=datetime.now(UTC),
                        question_sanitized=f"q {i}",
                        server="srv",
                        severity="low",
                        confidence="low",
                        answer=f"a {i}",
                        human_review_required=False,
                        remediation_performed=False,
                    )
                    sqlite_repo.store_investigation_audit(record)
            except Exception as exc:
                errors.append(exc)

        def _list_audits() -> None:
            try:
                barrier.wait(timeout=5)
                for _ in range(10):
                    sqlite_repo.list_investigation_audits(limit=50)
            except Exception as exc:
                errors.append(exc)

        writer = threading.Thread(target=_write_audits)
        reader = threading.Thread(target=_list_audits)
        writer.start()
        reader.start()
        writer.join(timeout=60)
        reader.join(timeout=60)

        assert errors == [], f"Concurrent audit write+list failed: {errors}"

    def test_wal_mode_enabled(self, sqlite_repo):
        """Verify WAL journal mode is active on new connections."""
        with sqlite_repo._connect() as conn:
            mode = conn.execute("PRAGMA journal_mode").fetchone()[0]
        assert mode.lower() == "wal"

    def test_busy_timeout_configured(self, sqlite_repo):
        """Verify busy_timeout is set to 30 seconds."""
        with sqlite_repo._connect() as conn:
            timeout = conn.execute("PRAGMA busy_timeout").fetchone()[0]
        assert timeout == 30_000
