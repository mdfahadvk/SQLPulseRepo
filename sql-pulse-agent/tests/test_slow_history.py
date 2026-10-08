"""Tests for slow-history endpoint, Query Store collector, and timeout candidate detector."""

import os
import tempfile
from datetime import UTC, datetime, timedelta
from unittest.mock import patch, call

import pytest

from app.collectors.query_store import check_query_store_status, collect_slow_history
from app.detectors.timeout_candidate import (
    aggregate_slow_records,
    build_timeout_candidates,
    build_timeout_findings,
    classify_workload,
    rank_candidates,
    _compare_with_baseline,
    _compute_confidence,
    _compute_rank_score,
    _compute_severity,
)
from app.models.findings import Confidence, FindingCategory, Severity
from app.models.history import (
    BaselineStatistic,
    BaselineStatus,
    QueryBaseline,
    QueryMetricRecord,
    SnapshotRecord,
)
from app.models.slow_history import (
    BaselineComparison,
    QueryStoreStatus,
    SlowHistoryResponse,
    SlowQueryRecord,
    TimeoutCandidate,
    WorkloadType,
)
from app.storage.history_service import set_repository
from app.storage.sqlite_repository import SQLiteHistoryRepository


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


def _make_slow_record(
    query_hash: str = "0xABC123",
    procedure_name: str | None = None,
    object_id: int | None = None,
    avg_duration_ms: float = 35_000.0,
    max_duration_ms: float = 60_000.0,
    min_duration_ms: float = 5_000.0,
    execution_count: int = 5,
    query_id: int = 101,
    plan_id: int = 201,
    query_sql_text: str = "SELECT * FROM Orders WHERE ...",
    avg_cpu_time_ms: float = 10_000.0,
    max_cpu_time_ms: float = 20_000.0,
    avg_logical_reads: float = 50_000.0,
    max_logical_reads: float = 80_000.0,
    avg_physical_reads: float = 100.0,
    program_name: str | None = None,
    host_name: str | None = None,
    is_scheduled_job: bool | None = None,
) -> SlowQueryRecord:
    now = datetime.now(UTC)
    return SlowQueryRecord(
        query_id=query_id,
        query_sql_text=query_sql_text,
        query_hash=query_hash,
        object_id=object_id,
        procedure_name=procedure_name,
        plan_id=plan_id,
        execution_count=execution_count,
        avg_duration_ms=avg_duration_ms,
        max_duration_ms=max_duration_ms,
        min_duration_ms=min_duration_ms,
        avg_cpu_time_ms=avg_cpu_time_ms,
        max_cpu_time_ms=max_cpu_time_ms,
        avg_logical_reads=avg_logical_reads,
        max_logical_reads=max_logical_reads,
        avg_physical_reads=avg_physical_reads,
        first_execution_time=now - timedelta(hours=2),
        last_execution_time=now,
        interval_start_time=now - timedelta(hours=3),
        program_name=program_name,
        host_name=host_name,
        is_scheduled_job=is_scheduled_job,
    )


# ─── Query Store Availability Tests ──────────────────────────────────────


class TestQueryStoreAvailability:
    """Tests for Query Store availability checking."""

    @patch("app.collectors.query_store.execute_read_query")
    def test_read_write_returns_available(self, mock_exec):
        mock_exec.return_value = [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}]
        status, msg = check_query_store_status("Merchandising")
        assert status == QueryStoreStatus.AVAILABLE
        assert msg is None

    @patch("app.collectors.query_store.execute_read_query")
    def test_read_only_returns_available_read_only(self, mock_exec):
        mock_exec.return_value = [{"actual_state_desc": "READ_ONLY", "desired_state_desc": "READ_WRITE", "readonly_reason": 2, "current_storage_size_mb": 10, "max_storage_size_mb": 100}]
        status, msg = check_query_store_status("testdb")
        assert status == QueryStoreStatus.AVAILABLE_READ_ONLY
        assert msg is None

    @patch("app.collectors.query_store.execute_read_query")
    def test_off_returns_disabled(self, mock_exec):
        mock_exec.return_value = [{"actual_state_desc": "OFF", "desired_state_desc": "OFF", "readonly_reason": 0, "current_storage_size_mb": 0, "max_storage_size_mb": 0}]
        status, msg = check_query_store_status("testdb")
        assert status == QueryStoreStatus.DISABLED
        assert "disabled" in msg.lower() or "OFF" in msg

    @patch("app.collectors.query_store.execute_read_query")
    def test_error_state_returns_disabled(self, mock_exec):
        mock_exec.return_value = [{"actual_state_desc": "ERROR", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 0, "max_storage_size_mb": 0}]
        status, msg = check_query_store_status("testdb")
        assert status == QueryStoreStatus.DISABLED
        assert "ERROR" in msg

    @patch("app.collectors.query_store.execute_read_query")
    def test_empty_result_returns_disabled(self, mock_exec):
        mock_exec.return_value = []
        status, msg = check_query_store_status("testdb")
        assert status == QueryStoreStatus.DISABLED
        assert msg is not None

    @patch("app.collectors.query_store.execute_read_query")
    def test_permission_denied_returns_permission_denied(self, mock_exec):
        mock_exec.side_effect = Exception("The user does not have permission denied to perform this action")
        status, msg = check_query_store_status("testdb")
        assert status == QueryStoreStatus.PERMISSION_DENIED
        assert "permission" in msg.lower() or "Permission" in msg

    @patch("app.collectors.query_store.execute_read_query")
    def test_connection_error_returns_query_failed(self, mock_exec):
        mock_exec.side_effect = Exception("connection reset by peer")
        status, msg = check_query_store_status("testdb")
        assert status == QueryStoreStatus.QUERY_FAILED
        assert msg is not None

    @patch("app.collectors.query_store.execute_read_query")
    def test_database_parameter_is_passed(self, mock_exec):
        """Verify that the database parameter is forwarded to execute_read_query."""
        mock_exec.return_value = [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}]
        check_query_store_status("Merchandising")
        mock_exec.assert_called_once()
        _, kwargs = mock_exec.call_args
        assert kwargs.get("database") == "Merchandising"

    @patch("app.collectors.query_store.execute_read_query")
    def test_default_master_does_not_affect_target_database(self, mock_exec, monkeypatch):
        """Even if SQL_DEFAULT_DATABASE=master, queries run against specified database."""
        monkeypatch.setenv("SQL_DEFAULT_DATABASE", "master")
        import app.config
        app.config._settings = None

        mock_exec.return_value = [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}]
        check_query_store_status("Merchandising")
        _, kwargs = mock_exec.call_args
        assert kwargs.get("database") == "Merchandising"
        app.config._settings = None

    def test_invalid_database_name_returns_query_failed(self):
        """Invalid database name returns QUERY_FAILED without querying SQL."""
        status, msg = check_query_store_status("'; DROP TABLE--")
        assert status == QueryStoreStatus.QUERY_FAILED
        assert "Invalid" in msg


# ─── Query Store Collector Tests ──────────────────────────────────────────


class TestQueryStoreCollector:
    """Tests for the Query Store collector."""

    @patch("app.collectors.query_store.execute_read_query")
    def test_collect_disabled_returns_disabled(self, mock_exec):
        """When QS is off, return DISABLED without querying slow history."""
        mock_exec.return_value = [{"actual_state_desc": "OFF", "desired_state_desc": "OFF", "readonly_reason": 0, "current_storage_size_mb": 0, "max_storage_size_mb": 0}]
        status, records, msg = collect_slow_history(
            "testdb",
            datetime.now(UTC) - timedelta(hours=24),
            datetime.now(UTC),
            30_000.0,
        )
        assert status == QueryStoreStatus.DISABLED
        assert records == []
        assert msg is not None
        assert mock_exec.call_count == 1

    @patch("app.collectors.query_store.execute_read_query")
    def test_collect_returns_records(self, mock_exec):
        """When QS is available, return matching records with all fields."""
        now = datetime.now(UTC)
        mock_exec.side_effect = [
            [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}],
            [
                {
                    "query_id": 1,
                    "query_sql_text": "SELECT * FROM Orders",
                    "query_hash": "0xABC",
                    "object_id": 789,
                    "procedure_name": "dbo.uspGetOrders",
                    "plan_id": 10,
                    "execution_count": 12,
                    "avg_duration_ms": 8500.0,
                    "max_duration_ms": 47200.0,
                    "min_duration_ms": 3000.0,
                    "avg_cpu_time_ms": 4200.0,
                    "max_cpu_time_ms": 17000.0,
                    "avg_logical_reads": 180000.0,
                    "max_logical_reads": 920000.0,
                    "avg_physical_reads": 50.0,
                    "first_execution_time": now - timedelta(hours=1),
                    "last_execution_time": now,
                    "interval_start_time": now - timedelta(hours=2),
                },
            ],
        ]
        status, records, msg = collect_slow_history(
            "testdb", now - timedelta(hours=24), now, 30_000.0, top_n=20,
        )
        assert status == QueryStoreStatus.AVAILABLE
        assert msg is None
        assert len(records) == 1
        r = records[0]
        assert r.query_hash == "0xABC"
        assert r.object_id == 789
        assert r.procedure_name == "dbo.uspGetOrders"
        assert r.max_duration_ms == 47200.0
        assert r.min_duration_ms == 3000.0
        assert r.interval_start_time is not None

    @patch("app.collectors.query_store.execute_read_query")
    def test_collect_query_exception_returns_query_failed(self, mock_exec):
        """If the slow-query SQL fails after status check, return QUERY_FAILED."""
        mock_exec.side_effect = [
            [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}],
            Exception("some odbc error"),
        ]
        status, records, msg = collect_slow_history(
            "testdb", datetime.now(UTC) - timedelta(hours=24),
            datetime.now(UTC), 30_000.0,
        )
        assert status == QueryStoreStatus.QUERY_FAILED
        assert records == []
        assert msg is not None

    @patch("app.collectors.query_store.execute_read_query")
    def test_collect_permission_error_returns_permission_denied(self, mock_exec):
        """If data query gets permission denied, return PERMISSION_DENIED."""
        mock_exec.side_effect = [
            [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}],
            Exception("permission denied for user"),
        ]
        status, records, msg = collect_slow_history(
            "testdb", datetime.now(UTC) - timedelta(hours=24),
            datetime.now(UTC), 30_000.0,
        )
        assert status == QueryStoreStatus.PERMISSION_DENIED
        assert records == []

    @patch("app.collectors.query_store.execute_read_query")
    def test_top_n_passed_to_query(self, mock_exec):
        """top_n parameter formats into the SQL TOP clause."""
        now = datetime.now(UTC)
        mock_exec.side_effect = [
            [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}],
            [],
        ]
        collect_slow_history("testdb", now - timedelta(hours=1), now, 30_000.0, top_n=5)
        # The second call should have TOP (5) in the query
        actual_query = mock_exec.call_args_list[1][0][0]
        assert "TOP (5)" in actual_query

    @patch("app.collectors.query_store.execute_read_query")
    def test_database_parameter_forwarded(self, mock_exec):
        """Both status check and data query use the specified database."""
        now = datetime.now(UTC)
        mock_exec.side_effect = [
            [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}],
            [],
        ]
        collect_slow_history("Merchandising", now - timedelta(hours=1), now, 30_000.0)
        # Both calls should pass database="Merchandising"
        for c in mock_exec.call_args_list:
            assert c.kwargs.get("database") == "Merchandising" or \
                   (len(c.args) >= 3 and False) or \
                   c[1].get("database") == "Merchandising"

    @patch("app.collectors.query_store.execute_read_query")
    def test_available_with_zero_candidates(self, mock_exec):
        """Query Store accessible but no slow queries → AVAILABLE + empty list."""
        now = datetime.now(UTC)
        mock_exec.side_effect = [
            [{"actual_state_desc": "READ_WRITE", "desired_state_desc": "READ_WRITE", "readonly_reason": 0, "current_storage_size_mb": 10, "max_storage_size_mb": 100}],
            [],  # No rows match threshold
        ]
        status, records, msg = collect_slow_history(
            "Merchandising", now - timedelta(hours=2), now, 30_000.0,
        )
        assert status == QueryStoreStatus.AVAILABLE
        assert records == []
        assert msg is None


# ─── Duration Unit Conversion Tests ──────────────────────────────────────


class TestDurationConversion:
    """Verify microsecond → millisecond conversion is correct in SQL and logic."""

    def test_query_converts_microseconds_to_milliseconds(self):
        """The SQL divides Query Store microseconds by 1000 to produce ms."""
        from app.database.performance_queries import QUERY_STORE_SLOW_HISTORY
        # Verify the conversion formula is present
        assert "/ 1000.0" in QUERY_STORE_SLOW_HISTORY
        # Check that the threshold comparison uses the converted value
        assert "rs.max_duration / 1000.0 >= ?" in QUERY_STORE_SLOW_HISTORY

    def test_29_second_query_does_not_exceed_30_second_threshold(self):
        """A 29-second query (29000 ms) should NOT be a timeout candidate."""
        records = [_make_slow_record(max_duration_ms=29_000.0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 0

    def test_31_second_query_exceeds_30_second_threshold(self):
        """A 31-second query (31000 ms) should be a timeout candidate."""
        records = [_make_slow_record(max_duration_ms=31_000.0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1

    def test_exactly_30_seconds_is_included(self):
        """A query exactly at 30000 ms (>= comparison) is included."""
        records = [_make_slow_record(max_duration_ms=30_000.0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1

    def test_microseconds_in_sql_comment(self):
        """The SQL query comments mention microsecond conversion."""
        from app.database.performance_queries import QUERY_STORE_SLOW_HISTORY
        assert "avg_duration / 1000.0 AS avg_duration_ms" in QUERY_STORE_SLOW_HISTORY
        assert "max_duration / 1000.0 AS max_duration_ms" in QUERY_STORE_SLOW_HISTORY
        assert "min_duration / 1000.0 AS min_duration_ms" in QUERY_STORE_SLOW_HISTORY

    def test_datetimeoffset_columns_cast_to_datetime2(self):
        """Regression: Query Store datetimeoffset columns must be CAST to datetime2.

        pyodbc raises HY106 'ODBC SQL type -155 is not yet supported' when
        reading datetimeoffset columns directly.  The fix is to CAST them
        to datetime2 in the SELECT list.
        """
        from app.database.performance_queries import QUERY_STORE_SLOW_HISTORY
        # first_execution_time must be cast
        assert "CAST(rs.first_execution_time AS datetime2) AS first_execution_time" in QUERY_STORE_SLOW_HISTORY
        # last_execution_time must be cast
        assert "CAST(rs.last_execution_time AS datetime2) AS last_execution_time" in QUERY_STORE_SLOW_HISTORY
        # interval start_time must be cast
        assert "CAST(rsi.start_time AS datetime2) AS interval_start_time" in QUERY_STORE_SLOW_HISTORY
        # Raw datetimeoffset columns must NOT appear uncast in SELECT list
        # (they can still appear in WHERE for comparison)
        select_section = QUERY_STORE_SLOW_HISTORY.split("FROM")[0]
        # These patterns indicate a raw (uncast) column in the select list
        assert "\n    rs.first_execution_time," not in select_section
        assert "\n    rs.last_execution_time," not in select_section
        # Verify the CAST wrappers are present (not raw column references)
        assert "CAST(rs.first_execution_time AS datetime2)" in select_section
        assert "CAST(rs.last_execution_time AS datetime2)" in select_section
        assert "CAST(rsi.start_time AS datetime2)" in select_section

    @patch("app.collectors.query_store.execute_read_query")
    def test_collector_handles_datetime2_timestamps(self, mock_exec):
        """Regression: timestamps returned as datetime (not datetimeoffset) parse correctly."""
        now = datetime.now(UTC)
        mock_exec.side_effect = [
            [{"actual_state_desc": "READ_ONLY", "desired_state_desc": "READ_WRITE",
              "readonly_reason": 8, "current_storage_size_mb": 50, "max_storage_size_mb": 100}],
            [
                {
                    "query_id": 42,
                    "query_sql_text": "SELECT 1",
                    "query_hash": "0xDEAD",
                    "object_id": 0,
                    "procedure_name": None,
                    "plan_id": 99,
                    "execution_count": 3,
                    "avg_duration_ms": 35000.0,
                    "max_duration_ms": 47200.0,
                    "min_duration_ms": 28000.0,
                    "avg_cpu_time_ms": 12000.0,
                    "max_cpu_time_ms": 18000.0,
                    "avg_logical_reads": 5000.0,
                    "max_logical_reads": 8000.0,
                    "avg_physical_reads": 10.0,
                    # Simulates datetime2 values (no timezone offset)
                    "first_execution_time": now - timedelta(hours=2),
                    "last_execution_time": now - timedelta(minutes=5),
                    "interval_start_time": now - timedelta(hours=3),
                },
            ],
        ]
        status, records, msg = collect_slow_history(
            "Merchandising",
            now - timedelta(hours=24),
            now,
            30_000.0,
            top_n=20,
        )
        # READ_ONLY is accessible
        assert status == QueryStoreStatus.AVAILABLE_READ_ONLY
        assert len(records) == 1
        r = records[0]
        assert r.first_execution_time is not None
        assert r.last_execution_time is not None
        assert r.interval_start_time is not None
        assert r.max_duration_ms == 47200.0


# ─── Timeout Candidate Detector Tests ─────────────────────────────────────


class TestTimeoutCandidateDetector:
    """Tests for building timeout candidates from slow records."""

    def test_above_threshold_becomes_candidate(self):
        records = [_make_slow_record(max_duration_ms=60_000.0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1
        # No application timeout configured, so this is slow — not a timeout.
        assert candidates[0].label == "slow_query_candidate"
        assert candidates[0].slow_query_candidate is True
        assert candidates[0].timeout_candidate is False
        assert candidates[0].max_duration_ms == 60_000.0

    def test_below_threshold_excluded(self):
        records = [_make_slow_record(max_duration_ms=25_000.0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 0

    def test_exact_threshold_included(self):
        records = [_make_slow_record(max_duration_ms=30_000.0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1

    def test_procedure_name_resolved(self):
        """Stored procedure name from object_id resolution is preserved."""
        records = [_make_slow_record(
            procedure_name="dbo.uspMerch_GetOrders",
            object_id=12345,
        )]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1
        assert candidates[0].procedure_name == "dbo.uspMerch_GetOrders"
        assert candidates[0].object_id == 12345

    def test_null_procedure_name_when_object_id_zero(self):
        """When object_id=0, procedure_name should be null."""
        records = [_make_slow_record(procedure_name=None, object_id=0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1
        assert candidates[0].procedure_name is None

    def test_null_procedure_name_when_object_id_none(self):
        """When procedure cannot be resolved, return null."""
        records = [_make_slow_record(procedure_name=None, object_id=None)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1
        assert candidates[0].procedure_name is None
        # query_hash is still preserved
        assert candidates[0].query_hash == "0xABC123"

    def test_sql_text_truncated_to_200(self):
        long_sql = "SELECT " + "x" * 300
        records = [_make_slow_record(query_sql_text=long_sql)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1
        assert len(candidates[0].sql_text_preview) == 200

    def test_multiple_candidates_sorted_by_worst(self):
        records = [
            _make_slow_record(query_hash="q1", max_duration_ms=40_000.0),
            _make_slow_record(query_hash="q2", max_duration_ms=90_000.0),
            _make_slow_record(query_hash="q3", max_duration_ms=60_000.0),
        ]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 3
        assert candidates[0].max_duration_ms == 90_000.0
        assert candidates[1].max_duration_ms == 60_000.0
        assert candidates[2].max_duration_ms == 40_000.0

    def test_repeated_timeout_candidates(self):
        """Multiple executions of the same query exceed threshold."""
        records = [_make_slow_record(execution_count=15, max_duration_ms=50_000.0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1
        assert candidates[0].execution_count == 15

    def test_query_ids_and_plan_ids_preserved(self):
        records = [_make_slow_record(query_id=42, plan_id=99)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert candidates[0].query_id == 42
        assert candidates[0].plan_id == 99

    def test_no_candidates_found(self):
        """No records exceed threshold → empty list."""
        records = [
            _make_slow_record(max_duration_ms=10_000.0),
            _make_slow_record(max_duration_ms=20_000.0),
        ]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert candidates == []
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 3
        assert candidates[0].max_duration_ms == 90_000.0
        assert candidates[1].max_duration_ms == 60_000.0
        assert candidates[2].max_duration_ms == 40_000.0

    def test_repeated_timeout_candidates(self):
        """Multiple executions of the same query exceed threshold."""
        records = [_make_slow_record(execution_count=15, max_duration_ms=50_000.0)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert len(candidates) == 1
        assert candidates[0].execution_count == 15

    def test_query_ids_and_plan_ids_preserved(self):
        records = [_make_slow_record(query_id=42, plan_id=99)]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert candidates[0].query_id == 42
        assert candidates[0].plan_id == 99

    def test_no_candidates_found(self):
        """No records exceed threshold → empty list."""
        records = [
            _make_slow_record(max_duration_ms=10_000.0),
            _make_slow_record(max_duration_ms=20_000.0),
        ]
        candidates = build_timeout_candidates(records, threshold_ms=30_000.0, database="db")
        assert candidates == []


# ─── Confidence Tests ─────────────────────────────────────────────────────


class TestConfidence:
    """Tests for deterministic confidence computation."""

    def test_high_confidence_repeated_with_fast_baseline(self):
        """Repeated exceedance + baseline shows normally fast → HIGH."""
        rec = _make_slow_record(execution_count=5, max_duration_ms=60_000.0)
        baseline = BaselineComparison(
            baseline_status="READY",
            sample_count=10,
            historical_mean_ms=6000.0,
            historical_p95_ms=9000.0,
            standard_deviation_ms=1000.0,
            regression_multiplier_vs_mean=10.0,
            regression_multiplier_vs_p95=6.7,
        )
        conf = _compute_confidence(rec, 30_000.0, baseline)
        assert conf == Confidence.HIGH

    def test_medium_confidence_single_exceedance_no_baseline(self):
        """Single exceedance + no baseline → MEDIUM."""
        rec = _make_slow_record(execution_count=1, max_duration_ms=60_000.0)
        conf = _compute_confidence(rec, 30_000.0, None)
        assert conf == Confidence.MEDIUM

    def test_low_confidence_near_threshold_noisy_baseline(self):
        """Duration near threshold + noisy baseline → LOW."""
        rec = _make_slow_record(execution_count=1, max_duration_ms=35_000.0)
        baseline = BaselineComparison(
            baseline_status="READY",
            sample_count=10,
            historical_mean_ms=20_000.0,
            historical_p95_ms=28_000.0,
            standard_deviation_ms=25_000.0,  # CV > 1.0 (noisy)
            regression_multiplier_vs_mean=1.75,
            regression_multiplier_vs_p95=1.25,
        )
        conf = _compute_confidence(rec, 30_000.0, baseline)
        assert conf == Confidence.LOW

    def test_low_confidence_barely_exceeded(self):
        """Duration barely exceeds threshold (< 1.2x) → LOW."""
        rec = _make_slow_record(execution_count=1, max_duration_ms=31_000.0)
        conf = _compute_confidence(rec, 30_000.0, None)
        assert conf == Confidence.LOW

    def test_medium_confidence_exceeded_no_repetition(self):
        """Exceeded clearly but only once → MEDIUM."""
        rec = _make_slow_record(execution_count=1, max_duration_ms=60_000.0)
        baseline = BaselineComparison(
            baseline_status="INSUFFICIENT_DATA",
        )
        conf = _compute_confidence(rec, 30_000.0, baseline)
        assert conf == Confidence.MEDIUM


# ─── Severity Tests ───────────────────────────────────────────────────────


class TestSeverity:
    """Tests for severity computation."""

    def test_critical_when_far_above_threshold(self):
        """3x threshold → CRITICAL."""
        rec = _make_slow_record(max_duration_ms=100_000.0)
        sev = _compute_severity(rec, 30_000.0, None)
        assert sev == Severity.CRITICAL

    def test_high_when_above_1_5x(self):
        """1.5x–3x threshold → HIGH."""
        rec = _make_slow_record(max_duration_ms=50_000.0)
        sev = _compute_severity(rec, 30_000.0, None)
        assert sev == Severity.HIGH

    def test_medium_when_at_threshold(self):
        """At threshold (1x–1.5x) → MEDIUM."""
        rec = _make_slow_record(max_duration_ms=35_000.0)
        sev = _compute_severity(rec, 30_000.0, None)
        assert sev == Severity.MEDIUM

    def test_critical_from_baseline_regression(self):
        """8x baseline regression → CRITICAL even if near threshold."""
        rec = _make_slow_record(max_duration_ms=35_000.0)
        baseline = BaselineComparison(
            baseline_status="READY",
            historical_mean_ms=4000.0,
            regression_multiplier_vs_mean=8.75,
        )
        sev = _compute_severity(rec, 30_000.0, baseline)
        assert sev == Severity.CRITICAL


# ─── Baseline Comparison Tests ────────────────────────────────────────────


class TestBaselineComparison:
    """Tests for baseline comparison enrichment."""

    def test_no_query_hash_returns_none(self):
        record = _make_slow_record(query_hash=None)
        result = _compare_with_baseline(record, "db")
        assert result is None

    def test_insufficient_data(self, sqlite_repo):
        """No baseline history → INSUFFICIENT_DATA."""
        record = _make_slow_record(query_hash="unknown_hash")
        result = _compare_with_baseline(record, "db")
        assert result is not None
        assert result.baseline_status == "INSUFFICIENT_DATA"

    def test_ready_baseline_comparison(self, sqlite_repo):
        """Sufficient history → full comparison with regression multipliers."""
        now = datetime.now(UTC)
        for i in range(10):
            snap_id = f"bl-{i}"
            t = now - timedelta(hours=i + 1)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="known_hash",
                    avg_cpu_time_ms=50.0,
                    avg_elapsed_time_ms=6000.0,
                    avg_logical_reads=500.0,
                    execution_count=20,
                ),
            ])

        # Query has max_duration = 47200 — far above baseline mean of ~6000
        record = _make_slow_record(query_hash="known_hash", max_duration_ms=47_200.0)
        result = _compare_with_baseline(record, "testdb")
        assert result is not None
        assert result.baseline_status == "READY"
        assert result.sample_count == 10
        assert result.historical_mean_ms == 6000.0
        assert result.historical_p95_ms > 0
        assert result.standard_deviation_ms >= 0
        # 47200 / 6000 ≈ 7.9x
        assert result.regression_multiplier_vs_mean == pytest.approx(7.9, abs=0.1)

    def test_noisy_baseline(self, sqlite_repo):
        """Baseline with high variance still returns comparison."""
        now = datetime.now(UTC)
        for i in range(10):
            snap_id = f"noisy-{i}"
            t = now - timedelta(hours=i + 1)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            # Large variance: values alternate between 2000 and 20000
            elapsed = 2000.0 if i % 2 == 0 else 20000.0
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="noisy_hash",
                    avg_elapsed_time_ms=elapsed,
                    execution_count=10,
                ),
            ])

        record = _make_slow_record(query_hash="noisy_hash", max_duration_ms=40_000.0)
        result = _compare_with_baseline(record, "testdb")
        assert result is not None
        assert result.baseline_status == "READY"
        assert result.standard_deviation_ms > 5000  # High std dev confirms noise


# ─── Finding Generation Tests ─────────────────────────────────────────────


class TestFindingGeneration:
    """Tests for building PerformanceFinding objects from candidates."""

    def test_finding_has_correct_category(self):
        """Without a configured application timeout the finding is slow-query."""
        candidates = build_timeout_candidates(
            [_make_slow_record(max_duration_ms=60_000.0)],
            threshold_ms=30_000.0,
            database="Merchandising",
        )
        findings = build_timeout_findings(candidates, 30_000.0, "Merchandising", "DCIR-ERPDBL")
        assert len(findings) == 1
        assert findings[0].category == FindingCategory.SLOW_QUERY_CANDIDATE

    def test_finding_category_is_timeout_when_timeout_configured(self):
        candidates = build_timeout_candidates(
            [_make_slow_record(max_duration_ms=60_000.0)],
            threshold_ms=30_000.0,
            database="Merchandising",
            application_timeout_ms=45_000.0,
        )
        findings = build_timeout_findings(candidates, 30_000.0, "Merchandising", "DCIR-ERPDBL")
        assert findings[0].category == FindingCategory.TIMEOUT_CANDIDATE

    def test_finding_uses_procedure_name_in_summary(self):
        candidates = build_timeout_candidates(
            [_make_slow_record(procedure_name="dbo.uspExample", max_duration_ms=47_200.0)],
            threshold_ms=30_000.0,
            database="Merchandising",
        )
        findings = build_timeout_findings(candidates, 30_000.0, "Merchandising", "DCIR-ERPDBL")
        assert "dbo.uspExample" in findings[0].summary
        assert "47.2 seconds" in findings[0].summary
        assert "30-second" in findings[0].summary

    def test_finding_uses_query_hash_when_no_proc(self):
        candidates = build_timeout_candidates(
            [_make_slow_record(procedure_name=None, query_hash="0xDEAD", max_duration_ms=50_000.0)],
            threshold_ms=30_000.0,
            database="db",
        )
        findings = build_timeout_findings(candidates, 30_000.0, "db", "srv")
        assert "0xDEAD" in findings[0].summary

    def test_finding_evidence_includes_baseline(self, sqlite_repo):
        now = datetime.now(UTC)
        for i in range(10):
            snap_id = f"fe-{i}"
            t = now - timedelta(hours=i + 1)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="fe_hash",
                    avg_elapsed_time_ms=6100.0, execution_count=10,
                ),
            ])

        candidates = build_timeout_candidates(
            [_make_slow_record(query_hash="fe_hash", max_duration_ms=47_200.0)],
            threshold_ms=30_000.0,
            database="testdb",
        )
        findings = build_timeout_findings(candidates, 30_000.0, "testdb", "srv")
        evidence_text = " ".join(findings[0].evidence)
        assert "Historical mean" in evidence_text
        assert "Historical P95" in evidence_text

    def test_finding_human_review_required(self):
        candidates = build_timeout_candidates(
            [_make_slow_record(max_duration_ms=60_000.0)],
            threshold_ms=30_000.0,
            database="db",
        )
        findings = build_timeout_findings(candidates, 30_000.0, "db", "srv")
        assert findings[0].human_review_required is True


# ─── Endpoint Tests ───────────────────────────────────────────────────────


class TestSlowHistoryEndpoint:
    """Tests for GET /api/performance/slow-history."""

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_query_store_disabled(self, mock_collect):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        mock_collect.return_value = (QueryStoreStatus.DISABLED, [], "Query Store is disabled (OFF) for database 'testdb'.")
        now = datetime.now(UTC)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["query_store_status"] == "DISABLED"
        assert data["candidates"] == []
        assert data["message"] is not None

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_query_store_permission_denied(self, mock_collect):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        mock_collect.return_value = (QueryStoreStatus.PERMISSION_DENIED, [], "Permission denied.")
        now = datetime.now(UTC)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["query_store_status"] == "PERMISSION_DENIED"

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_query_store_query_failed(self, mock_collect):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        mock_collect.return_value = (QueryStoreStatus.QUERY_FAILED, [], "Query failed.")
        now = datetime.now(UTC)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["query_store_status"] == "QUERY_FAILED"

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_returns_candidates(self, mock_collect):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_collect.return_value = (
            QueryStoreStatus.AVAILABLE,
            [
                SlowQueryRecord(
                    query_id=123,
                    query_sql_text="EXEC dbo.uspExample",
                    query_hash="0xABC",
                    object_id=789,
                    procedure_name="dbo.uspExample",
                    plan_id=456,
                    execution_count=12,
                    avg_duration_ms=8500.0,
                    max_duration_ms=47200.0,
                    min_duration_ms=3200.0,
                    avg_cpu_time_ms=4200.0,
                    max_cpu_time_ms=17000.0,
                    avg_logical_reads=180000.0,
                    max_logical_reads=920000.0,
                    avg_physical_reads=50.0,
                    first_execution_time=now - timedelta(hours=2),
                    last_execution_time=now,
                    interval_start_time=now - timedelta(hours=3),
                ),
            ],
            None,
        )
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "Merchandising",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["query_store_status"] == "AVAILABLE"
        assert data["total_candidates"] == 1
        cand = data["candidates"][0]
        assert cand["label"] == "slow_query_candidate"
        assert cand["procedure_name"] == "dbo.uspExample"
        assert cand["query_hash"] == "0xABC"
        assert cand["query_id"] == 123
        assert cand["plan_id"] == 456
        assert cand["object_id"] == 789
        assert cand["max_duration_ms"] == 47200.0
        assert cand["avg_cpu_ms"] == 4200.0
        assert cand["max_cpu_ms"] == 17000.0
        assert cand["avg_logical_reads"] == 180000.0
        assert cand["max_logical_reads"] == 920000.0

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_available_with_no_candidates(self, mock_collect):
        """AVAILABLE + zero candidates is valid (does NOT become unavailable)."""
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        # All records below threshold
        mock_collect.return_value = (
            QueryStoreStatus.AVAILABLE,
            [
                SlowQueryRecord(
                    query_id=1, query_hash="0xLOW",
                    execution_count=10,
                    avg_duration_ms=20_000.0,
                    max_duration_ms=25_000.0,
                ),
            ],
            None,
        )
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["query_store_status"] == "AVAILABLE"
        assert data["total_candidates"] == 0
        assert data["candidates"] == []
        # No message for successful access
        assert data.get("message") is None

    def test_missing_database_param(self):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
            },
        )
        assert response.status_code == 422

    def test_invalid_time_range(self):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": now.isoformat(),
                "end_time": (now - timedelta(hours=1)).isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 400
        assert "end_time" in response.json()["detail"]

    def test_database_validation_rejects_injection(self):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "'; DROP TABLE--",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 400

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_top_n_limit(self, mock_collect):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_collect.return_value = (QueryStoreStatus.AVAILABLE, [], None)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
                "top_n": 5,
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["top_n"] == 5
        # Verify top_n was passed to collector
        mock_collect.assert_called_once()

    def test_top_n_exceeds_max_rejected(self):
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
                "top_n": 500,
            },
        )
        assert response.status_code == 422  # Exceeds le=100

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_response_structure_matches_spec(self, mock_collect):
        """Response has exact structure from spec: server, database, candidates, etc."""
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_collect.return_value = (QueryStoreStatus.AVAILABLE, [], None)
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "Merchandising",
                "start_time": (now - timedelta(hours=2)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        data = response.json()
        assert "server" in data
        assert "database" in data
        assert data["database"] == "Merchandising"
        assert "start_time" in data
        assert "end_time" in data
        assert "threshold_ms" in data
        assert data["threshold_ms"] == 30000
        assert "query_store_status" in data
        assert data["query_store_status"] == "AVAILABLE"
        assert "candidates" in data
        assert "total_candidates" in data
        assert data["total_candidates"] == 0


# ─── Aggregation Tests ────────────────────────────────────────────────────


class TestAggregation:
    """Tests for merging duplicate Query Store intervals."""

    def test_single_record_unchanged(self):
        """A single record passes through with intervals_observed=1."""
        records = [_make_slow_record(max_duration_ms=60_000.0)]
        result = aggregate_slow_records(records, threshold_ms=30_000.0)
        assert len(result) == 1
        assert result[0].intervals_observed == 1

    def test_single_record_exceeding_threshold(self):
        """A single record above threshold has intervals_exceeding=1."""
        records = [_make_slow_record(max_duration_ms=60_000.0)]
        result = aggregate_slow_records(records, threshold_ms=30_000.0)
        assert result[0].intervals_exceeding_threshold == 1

    def test_single_record_below_threshold(self):
        """A single record below threshold has intervals_exceeding=0."""
        records = [_make_slow_record(max_duration_ms=20_000.0)]
        result = aggregate_slow_records(records, threshold_ms=30_000.0)
        assert result[0].intervals_exceeding_threshold == 0

    def test_two_intervals_same_query_merged(self):
        """Two intervals for the same (query_hash, query_id, plan_id, object_id) merge."""
        now = datetime.now(UTC)
        r1 = SlowQueryRecord(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            query_sql_text="SELECT 1",
            execution_count=5,
            avg_duration_ms=10_000.0, max_duration_ms=40_000.0, min_duration_ms=5_000.0,
            avg_cpu_time_ms=3_000.0, max_cpu_time_ms=8_000.0,
            avg_logical_reads=1_000.0, max_logical_reads=3_000.0,
            avg_physical_reads=10.0,
            first_execution_time=now - timedelta(hours=4),
            last_execution_time=now - timedelta(hours=2),
            interval_start_time=now - timedelta(hours=5),
        )
        r2 = SlowQueryRecord(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            query_sql_text="SELECT 1",
            execution_count=15,
            avg_duration_ms=20_000.0, max_duration_ms=50_000.0, min_duration_ms=8_000.0,
            avg_cpu_time_ms=7_000.0, max_cpu_time_ms=12_000.0,
            avg_logical_reads=2_000.0, max_logical_reads=5_000.0,
            avg_physical_reads=20.0,
            first_execution_time=now - timedelta(hours=2),
            last_execution_time=now,
            interval_start_time=now - timedelta(hours=3),
        )
        result = aggregate_slow_records([r1, r2], threshold_ms=30_000.0)
        assert len(result) == 1
        m = result[0]
        assert m.intervals_observed == 2
        assert m.execution_count == 20  # 5 + 15
        assert m.max_duration_ms == 50_000.0  # max(40k, 50k)
        assert m.min_duration_ms == 5_000.0  # min(5k, 8k)
        assert m.max_cpu_time_ms == 12_000.0  # max(8k, 12k)
        assert m.max_logical_reads == 5_000.0

    def test_weighted_average_duration(self):
        """Weighted avg = sum(avg_i * exec_i) / sum(exec_i)."""
        now = datetime.now(UTC)
        r1 = SlowQueryRecord(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            execution_count=10, avg_duration_ms=10_000.0,
            max_duration_ms=40_000.0, min_duration_ms=5_000.0,
            avg_cpu_time_ms=0.0, max_cpu_time_ms=0.0,
            avg_logical_reads=0.0, max_logical_reads=0.0,
            first_execution_time=now - timedelta(hours=2),
            last_execution_time=now,
        )
        r2 = SlowQueryRecord(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            execution_count=10, avg_duration_ms=30_000.0,
            max_duration_ms=50_000.0, min_duration_ms=8_000.0,
            avg_cpu_time_ms=0.0, max_cpu_time_ms=0.0,
            avg_logical_reads=0.0, max_logical_reads=0.0,
            first_execution_time=now - timedelta(hours=4),
            last_execution_time=now - timedelta(hours=1),
        )
        result = aggregate_slow_records([r1, r2], threshold_ms=30_000.0)
        # weighted avg = (10*10000 + 10*30000) / 20 = 20000
        assert result[0].avg_duration_ms == pytest.approx(20_000.0)

    def test_first_last_execution_time_span(self):
        """first_execution_time = earliest, last_execution_time = latest across intervals."""
        now = datetime.now(UTC)
        r1 = SlowQueryRecord(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            execution_count=5,
            avg_duration_ms=10_000.0, max_duration_ms=40_000.0, min_duration_ms=5_000.0,
            avg_cpu_time_ms=0.0, max_cpu_time_ms=0.0,
            avg_logical_reads=0.0, max_logical_reads=0.0,
            first_execution_time=now - timedelta(hours=6),
            last_execution_time=now - timedelta(hours=4),
        )
        r2 = SlowQueryRecord(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            execution_count=5,
            avg_duration_ms=10_000.0, max_duration_ms=50_000.0, min_duration_ms=8_000.0,
            avg_cpu_time_ms=0.0, max_cpu_time_ms=0.0,
            avg_logical_reads=0.0, max_logical_reads=0.0,
            first_execution_time=now - timedelta(hours=2),
            last_execution_time=now,
        )
        result = aggregate_slow_records([r1, r2], threshold_ms=30_000.0)
        assert result[0].first_execution_time == now - timedelta(hours=6)
        assert result[0].last_execution_time == now

    def test_different_plan_ids_not_merged(self):
        """Different plan_ids produce separate groups."""
        records = [
            _make_slow_record(query_hash="0xABC", query_id=1, plan_id=10),
            _make_slow_record(query_hash="0xABC", query_id=1, plan_id=20),
        ]
        result = aggregate_slow_records(records, threshold_ms=30_000.0)
        assert len(result) == 2

    def test_different_query_hashes_not_merged(self):
        """Different query_hashes produce separate groups."""
        records = [
            _make_slow_record(query_hash="0xAAA", query_id=1),
            _make_slow_record(query_hash="0xBBB", query_id=2),
        ]
        result = aggregate_slow_records(records, threshold_ms=30_000.0)
        assert len(result) == 2

    def test_intervals_exceeding_threshold_counted(self):
        """Only intervals where max_duration >= threshold are counted."""
        now = datetime.now(UTC)
        r1 = SlowQueryRecord(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            execution_count=5,
            avg_duration_ms=10_000.0, max_duration_ms=25_000.0, min_duration_ms=5_000.0,
            avg_cpu_time_ms=0.0, max_cpu_time_ms=0.0,
            avg_logical_reads=0.0, max_logical_reads=0.0,
            first_execution_time=now - timedelta(hours=4),
            last_execution_time=now - timedelta(hours=2),
        )
        r2 = SlowQueryRecord(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            execution_count=5,
            avg_duration_ms=35_000.0, max_duration_ms=50_000.0, min_duration_ms=30_000.0,
            avg_cpu_time_ms=0.0, max_cpu_time_ms=0.0,
            avg_logical_reads=0.0, max_logical_reads=0.0,
            first_execution_time=now - timedelta(hours=2),
            last_execution_time=now,
        )
        result = aggregate_slow_records([r1, r2], threshold_ms=30_000.0)
        assert result[0].intervals_observed == 2
        assert result[0].intervals_exceeding_threshold == 1  # only r2

    def test_three_intervals_mixed_threshold(self):
        """Three intervals, two exceeding threshold."""
        now = datetime.now(UTC)
        base = dict(
            query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
            execution_count=3,
            avg_duration_ms=10_000.0, avg_cpu_time_ms=0.0, max_cpu_time_ms=0.0,
            avg_logical_reads=0.0, max_logical_reads=0.0,
            first_execution_time=now - timedelta(hours=6),
            last_execution_time=now,
        )
        r1 = SlowQueryRecord(**base, max_duration_ms=20_000.0, min_duration_ms=5_000.0)
        r2 = SlowQueryRecord(**base, max_duration_ms=40_000.0, min_duration_ms=10_000.0)
        r3 = SlowQueryRecord(**base, max_duration_ms=35_000.0, min_duration_ms=8_000.0)
        result = aggregate_slow_records([r1, r2, r3], threshold_ms=30_000.0)
        assert result[0].intervals_observed == 3
        assert result[0].intervals_exceeding_threshold == 2
        assert result[0].execution_count == 9
        assert result[0].max_duration_ms == 40_000.0
        assert result[0].min_duration_ms == 5_000.0

    def test_empty_records(self):
        """Empty input returns empty output."""
        assert aggregate_slow_records([], threshold_ms=30_000.0) == []

    def test_aggregation_preserves_procedure_name(self):
        """Procedure name from the first record is preserved."""
        records = [
            _make_slow_record(query_hash="0xABC", query_id=1, plan_id=10,
                              procedure_name="dbo.uspGetOrders", object_id=123),
            _make_slow_record(query_hash="0xABC", query_id=1, plan_id=10,
                              procedure_name="dbo.uspGetOrders", object_id=123),
        ]
        result = aggregate_slow_records(records, threshold_ms=30_000.0)
        assert len(result) == 1
        assert result[0].procedure_name == "dbo.uspGetOrders"

    def test_build_candidates_with_grouping(self):
        """build_timeout_candidates with group_results=True aggregates intervals."""
        records = [
            _make_slow_record(query_hash="0xABC", query_id=1, plan_id=10,
                              max_duration_ms=40_000.0),
            _make_slow_record(query_hash="0xABC", query_id=1, plan_id=10,
                              max_duration_ms=50_000.0),
        ]
        candidates = build_timeout_candidates(
            records, threshold_ms=30_000.0, database="db", group_results=True,
        )
        assert len(candidates) == 1
        assert candidates[0].intervals_observed == 2

    def test_build_candidates_without_grouping(self):
        """build_timeout_candidates with group_results=False keeps intervals separate."""
        records = [
            _make_slow_record(query_hash="0xABC", query_id=1, plan_id=10,
                              max_duration_ms=40_000.0),
            _make_slow_record(query_hash="0xABC", query_id=1, plan_id=10,
                              max_duration_ms=50_000.0),
        ]
        candidates = build_timeout_candidates(
            records, threshold_ms=30_000.0, database="db", group_results=False,
        )
        assert len(candidates) == 2


# ─── Workload Classification Tests ────────────────────────────────────────


class TestWorkloadClassification:
    """Tests for workload type classification."""

    def test_dbcc_classified_as_background(self):
        wl, evidence = classify_workload("DBCC CHECKDB", None)
        assert wl == WorkloadType.BACKGROUND_OR_SCHEDULED
        assert any("DBCC" in e for e in evidence)

    def test_backup_classified_as_background(self):
        wl, _ = classify_workload("BACKUP DATABASE [mydb] TO DISK", None)
        assert wl == WorkloadType.BACKGROUND_OR_SCHEDULED

    def test_rebuild_classified_as_background(self):
        wl, _ = classify_workload("ALTER INDEX ALL ON dbo.Orders REBUILD", None)
        assert wl == WorkloadType.BACKGROUND_OR_SCHEDULED

    def test_update_statistics_classified_as_background(self):
        wl, _ = classify_workload("UPDATE STATISTICS dbo.Orders", None)
        assert wl == WorkloadType.BACKGROUND_OR_SCHEDULED

    def test_maintenance_procedure_name_alone_is_not_enough(self):
        """A maintenance-sounding NAME is a convention, not proof of a caller.

        Naming is weak evidence in both directions: treating it as decisive
        here while rejecting it for 'usp' would just be an inconsistent bias.
        """
        wl, evidence = classify_workload(None, "dbo.maintenance_job")
        assert wl == WorkloadType.UNKNOWN
        assert any("weak" in e for e in evidence)

    def test_select_alone_is_not_enough_for_application(self):
        """A SELECT appears in every kind of workload, so it proves nothing."""
        wl, evidence = classify_workload("SELECT * FROM Orders WHERE id = 1", None)
        assert wl == WorkloadType.UNKNOWN
        assert len(evidence) > 0

    def test_exec_of_usp_procedure_alone_is_not_enough(self):
        wl, _ = classify_workload("EXEC dbo.uspGetOrders @id=1", None)
        assert wl == WorkloadType.UNKNOWN

    def test_usp_procedure_name_alone_does_not_prove_application(self):
        """Explicitly: the 'usp' prefix must not decide the classification.

        DBAs name maintenance helpers 'usp...' too, so the prefix carries no
        information about who called the procedure.
        """
        wl, evidence = classify_workload(None, "dbo.uspMerch_GetOrders")
        assert wl == WorkloadType.UNKNOWN
        assert any("convention" in e.lower() for e in evidence)

    def test_application_program_name_proves_application(self):
        wl, evidence = classify_workload(
            "SELECT * FROM Orders",
            "dbo.uspMerch_GetOrders",
            program_name=".Net SqlClient Data Provider",
        )
        assert wl == WorkloadType.APPLICATION_OR_INTERACTIVE
        assert any("strong" in e for e in evidence)

    def test_sql_agent_program_name_proves_background(self):
        wl, evidence = classify_workload(
            "EXEC dbo.uspNightlyRollup",
            "dbo.uspNightlyRollup",
            program_name="SQLAgent - TSQL JobStep (Job 0x1234 : Step 1)",
        )
        assert wl == WorkloadType.BACKGROUND_OR_SCHEDULED
        assert any("strong" in e for e in evidence)

    def test_unknown_when_no_text(self):
        wl, evidence = classify_workload(None, None)
        assert wl == WorkloadType.UNKNOWN
        assert len(evidence) > 0

    def test_unknown_for_ambiguous_text(self):
        wl, _ = classify_workload("WAITFOR DELAY '00:00:05'", None)
        assert wl == WorkloadType.UNKNOWN

    def test_background_takes_precedence(self):
        """A maintenance statement outranks an application caller.

        An application connection running BACKUP DATABASE is still performing
        maintenance, so the operation decides, not the caller.
        """
        wl, _ = classify_workload(
            "BACKUP DATABASE [mydb] TO DISK = 'x.bak'",
            None,
            program_name=".Net SqlClient Data Provider",
        )
        assert wl == WorkloadType.BACKGROUND_OR_SCHEDULED

    def test_candidate_has_workload_type(self):
        """build_timeout_candidates assigns workload_type to each candidate."""
        records = [_make_slow_record(
            max_duration_ms=60_000.0,
            query_sql_text="SELECT * FROM Orders WHERE id = 1",
            program_name=".Net SqlClient Data Provider",
        )]
        candidates = build_timeout_candidates(records, 30_000.0, "db")
        assert candidates[0].workload_type == WorkloadType.APPLICATION_OR_INTERACTIVE
        assert len(candidates[0].classification_evidence) > 0

    def test_candidate_background_workload(self):
        """DBCC query classified as background in candidate."""
        records = [_make_slow_record(
            max_duration_ms=60_000.0,
            query_sql_text="DBCC CHECKDB",
        )]
        candidates = build_timeout_candidates(records, 30_000.0, "db")
        assert candidates[0].workload_type == WorkloadType.BACKGROUND_OR_SCHEDULED


# ─── Ranking Tests ─────────────────────────────────────────────────────────


class TestRanking:
    """Tests for candidate ranking by composite score."""

    def test_candidates_ranked_by_score(self):
        """Higher max_duration candidate gets rank 1."""
        records = [
            _make_slow_record(query_hash="q1", max_duration_ms=40_000.0, query_id=1),
            _make_slow_record(query_hash="q2", max_duration_ms=90_000.0, query_id=2),
            _make_slow_record(query_hash="q3", max_duration_ms=60_000.0, query_id=3),
        ]
        candidates = build_timeout_candidates(records, 30_000.0, "db")
        assert candidates[0].rank == 1
        assert candidates[0].max_duration_ms == 90_000.0
        assert candidates[1].rank == 2
        assert candidates[2].rank == 3

    def test_single_candidate_rank_1(self):
        """A single candidate is always rank 1."""
        records = [_make_slow_record(max_duration_ms=60_000.0)]
        candidates = build_timeout_candidates(records, 30_000.0, "db")
        assert candidates[0].rank == 1

    def test_application_query_ranks_higher_than_background(self):
        """Application workload type gets a ranking boost."""
        app_rec = _make_slow_record(
            query_hash="q1", max_duration_ms=50_000.0, query_id=1,
            query_sql_text="SELECT * FROM Orders WHERE id = 1",
        )
        bg_rec = _make_slow_record(
            query_hash="q2", max_duration_ms=50_000.0, query_id=2,
            query_sql_text="DBCC CHECKDB",
        )
        candidates = build_timeout_candidates(
            [app_rec, bg_rec], 30_000.0, "db",
        )
        # Both have same max_duration, but app query gets workload boost
        app_cand = next(c for c in candidates if c.query_hash == "q1")
        bg_cand = next(c for c in candidates if c.query_hash == "q2")
        assert app_cand.rank < bg_cand.rank  # lower rank = more urgent

    def test_rank_score_includes_regression(self, sqlite_repo):
        """Baseline regression increases rank score."""
        now = datetime.now(UTC)
        for i in range(10):
            snap_id = f"rk-{i}"
            t = now - timedelta(hours=i + 1)
            sqlite_repo.store_snapshot(SnapshotRecord(
                snapshot_id=snap_id, collected_at=t, server="srv", scope="server",
            ))
            sqlite_repo.store_query_metrics([
                QueryMetricRecord(
                    snapshot_id=snap_id, collected_at=t,
                    server="srv", database="testdb", query_hash="rank_hash",
                    avg_elapsed_time_ms=5000.0, execution_count=10,
                ),
            ])
        records = [_make_slow_record(
            query_hash="rank_hash", max_duration_ms=50_000.0, query_id=1,
        )]
        candidates = build_timeout_candidates(records, 30_000.0, "testdb")
        assert candidates[0].rank == 1
        # Regression should boost the score
        assert candidates[0].baseline_comparison is not None
        assert candidates[0].baseline_comparison.baseline_status == "READY"

    def test_rank_candidates_empty_list(self):
        """Empty list returns empty list."""
        result = rank_candidates([], threshold_ms=30_000.0)
        assert result == []


# ─── Filtering Tests ───────────────────────────────────────────────────────


class TestFiltering:
    """Tests for workload_type filtering in build_timeout_candidates."""

    def test_filter_application_only(self):
        """Filter returns only APPLICATION_OR_INTERACTIVE candidates."""
        records = [
            _make_slow_record(query_hash="q1", max_duration_ms=60_000.0, query_id=1,
                              query_sql_text="SELECT * FROM Orders",
                              program_name=".Net SqlClient Data Provider"),
            _make_slow_record(query_hash="q2", max_duration_ms=60_000.0, query_id=2,
                              query_sql_text="DBCC CHECKDB"),
        ]
        candidates = build_timeout_candidates(
            records, 30_000.0, "db",
            workload_type_filter=WorkloadType.APPLICATION_OR_INTERACTIVE,
        )
        assert len(candidates) == 1
        assert candidates[0].workload_type == WorkloadType.APPLICATION_OR_INTERACTIVE

    def test_filter_background_only(self):
        """Filter returns only BACKGROUND_OR_SCHEDULED candidates."""
        records = [
            _make_slow_record(query_hash="q1", max_duration_ms=60_000.0, query_id=1,
                              query_sql_text="SELECT * FROM Orders"),
            _make_slow_record(query_hash="q2", max_duration_ms=60_000.0, query_id=2,
                              query_sql_text="DBCC CHECKDB"),
        ]
        candidates = build_timeout_candidates(
            records, 30_000.0, "db",
            workload_type_filter=WorkloadType.BACKGROUND_OR_SCHEDULED,
        )
        assert len(candidates) == 1
        assert candidates[0].workload_type == WorkloadType.BACKGROUND_OR_SCHEDULED

    def test_no_filter_returns_all(self):
        """No workload_type filter returns all candidates."""
        records = [
            _make_slow_record(query_hash="q1", max_duration_ms=60_000.0, query_id=1,
                              query_sql_text="SELECT * FROM Orders"),
            _make_slow_record(query_hash="q2", max_duration_ms=60_000.0, query_id=2,
                              query_sql_text="DBCC CHECKDB"),
        ]
        candidates = build_timeout_candidates(records, 30_000.0, "db")
        assert len(candidates) == 2

    def test_filter_with_no_matches_returns_empty(self):
        """Filter with no matching candidates returns empty list."""
        records = [
            _make_slow_record(query_hash="q1", max_duration_ms=60_000.0, query_id=1,
                              query_sql_text="SELECT * FROM Orders"),
        ]
        candidates = build_timeout_candidates(
            records, 30_000.0, "db",
            workload_type_filter=WorkloadType.BACKGROUND_OR_SCHEDULED,
        )
        assert len(candidates) == 0


# ─── Endpoint Aggregation/Filter/Rank Tests ───────────────────────────────


class TestSlowHistoryEndpointExtended:
    """Tests for new group_results and workload_type parameters."""

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_group_results_default_true(self, mock_collect):
        """By default, group_results=True aggregates intervals."""
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_collect.return_value = (
            QueryStoreStatus.AVAILABLE,
            [
                SlowQueryRecord(
                    query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
                    query_sql_text="SELECT 1",
                    execution_count=5,
                    avg_duration_ms=35_000.0, max_duration_ms=50_000.0,
                    min_duration_ms=5_000.0,
                    avg_cpu_time_ms=3_000.0, max_cpu_time_ms=8_000.0,
                    avg_logical_reads=1_000.0, max_logical_reads=3_000.0,
                    first_execution_time=now - timedelta(hours=4),
                    last_execution_time=now - timedelta(hours=2),
                ),
                SlowQueryRecord(
                    query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
                    query_sql_text="SELECT 1",
                    execution_count=15,
                    avg_duration_ms=40_000.0, max_duration_ms=60_000.0,
                    min_duration_ms=8_000.0,
                    avg_cpu_time_ms=7_000.0, max_cpu_time_ms=12_000.0,
                    avg_logical_reads=2_000.0, max_logical_reads=5_000.0,
                    first_execution_time=now - timedelta(hours=2),
                    last_execution_time=now,
                ),
            ],
            None,
        )
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["total_candidates"] == 1
        cand = data["candidates"][0]
        assert cand["intervals_observed"] == 2
        assert cand["total_execution_count"] == 20
        assert cand["rank"] == 1

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_group_results_false_keeps_separate(self, mock_collect):
        """group_results=false keeps intervals as separate candidates."""
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_collect.return_value = (
            QueryStoreStatus.AVAILABLE,
            [
                SlowQueryRecord(
                    query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
                    query_sql_text="SELECT 1",
                    execution_count=5,
                    avg_duration_ms=35_000.0, max_duration_ms=50_000.0,
                    min_duration_ms=5_000.0,
                    avg_cpu_time_ms=3_000.0, max_cpu_time_ms=8_000.0,
                    avg_logical_reads=1_000.0, max_logical_reads=3_000.0,
                    first_execution_time=now - timedelta(hours=4),
                    last_execution_time=now - timedelta(hours=2),
                ),
                SlowQueryRecord(
                    query_id=1, query_hash="0xABC", plan_id=10, object_id=0,
                    query_sql_text="SELECT 1",
                    execution_count=15,
                    avg_duration_ms=40_000.0, max_duration_ms=60_000.0,
                    min_duration_ms=8_000.0,
                    avg_cpu_time_ms=7_000.0, max_cpu_time_ms=12_000.0,
                    avg_logical_reads=2_000.0, max_logical_reads=5_000.0,
                    first_execution_time=now - timedelta(hours=2),
                    last_execution_time=now,
                ),
            ],
            None,
        )
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
                "group_results": "false",
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["total_candidates"] == 2

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_workload_type_filter_endpoint(self, mock_collect):
        """workload_type parameter filters candidates at the endpoint."""
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_collect.return_value = (
            QueryStoreStatus.AVAILABLE,
            [
                SlowQueryRecord(
                    query_id=1, query_hash="0xAPP", plan_id=10, object_id=0,
                    query_sql_text="SELECT * FROM Orders WHERE id = 1",
                    program_name=".Net SqlClient Data Provider",
                    execution_count=5,
                    avg_duration_ms=35_000.0, max_duration_ms=50_000.0,
                    min_duration_ms=5_000.0,
                    avg_cpu_time_ms=3_000.0, max_cpu_time_ms=8_000.0,
                    avg_logical_reads=1_000.0, max_logical_reads=3_000.0,
                    first_execution_time=now - timedelta(hours=2),
                    last_execution_time=now,
                ),
                SlowQueryRecord(
                    query_id=2, query_hash="0xBG", plan_id=20, object_id=0,
                    query_sql_text="DBCC CHECKDB",
                    execution_count=1,
                    avg_duration_ms=45_000.0, max_duration_ms=45_000.0,
                    min_duration_ms=45_000.0,
                    avg_cpu_time_ms=10_000.0, max_cpu_time_ms=10_000.0,
                    avg_logical_reads=5_000.0, max_logical_reads=5_000.0,
                    first_execution_time=now - timedelta(hours=1),
                    last_execution_time=now,
                ),
            ],
            None,
        )
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
                "workload_type": "APPLICATION_OR_INTERACTIVE",
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert data["total_candidates"] == 1
        assert data["candidates"][0]["workload_type"] == "APPLICATION_OR_INTERACTIVE"

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_response_has_rank_field(self, mock_collect):
        """Every candidate in the response has a rank field."""
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_collect.return_value = (
            QueryStoreStatus.AVAILABLE,
            [
                SlowQueryRecord(
                    query_id=1, query_hash="0xA", plan_id=10, object_id=0,
                    query_sql_text="SELECT 1",
                    execution_count=5,
                    avg_duration_ms=35_000.0, max_duration_ms=50_000.0,
                    min_duration_ms=5_000.0,
                    avg_cpu_time_ms=3_000.0, max_cpu_time_ms=8_000.0,
                    avg_logical_reads=1_000.0, max_logical_reads=3_000.0,
                    first_execution_time=now - timedelta(hours=2),
                    last_execution_time=now,
                ),
                SlowQueryRecord(
                    query_id=2, query_hash="0xB", plan_id=20, object_id=0,
                    query_sql_text="SELECT 2",
                    execution_count=10,
                    avg_duration_ms=45_000.0, max_duration_ms=80_000.0,
                    min_duration_ms=10_000.0,
                    avg_cpu_time_ms=5_000.0, max_cpu_time_ms=15_000.0,
                    avg_logical_reads=2_000.0, max_logical_reads=6_000.0,
                    first_execution_time=now - timedelta(hours=3),
                    last_execution_time=now,
                ),
            ],
            None,
        )
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 200
        data = response.json()
        for cand in data["candidates"]:
            assert "rank" in cand
            assert cand["rank"] is not None
        # Rank 1 should have highest max_duration
        assert data["candidates"][0]["rank"] == 1

    @patch("app.services.slow_history_service.collect_slow_history")
    def test_response_has_workload_type(self, mock_collect):
        """Every candidate has workload_type and classification_evidence."""
        from fastapi.testclient import TestClient
        from app.main import app

        client = TestClient(app)
        now = datetime.now(UTC)
        mock_collect.return_value = (
            QueryStoreStatus.AVAILABLE,
            [
                SlowQueryRecord(
                    query_id=1, query_hash="0xA", plan_id=10, object_id=0,
                    query_sql_text="SELECT * FROM Orders",
                    program_name=".Net SqlClient Data Provider",
                    execution_count=5,
                    avg_duration_ms=35_000.0, max_duration_ms=50_000.0,
                    min_duration_ms=5_000.0,
                    avg_cpu_time_ms=3_000.0, max_cpu_time_ms=8_000.0,
                    avg_logical_reads=1_000.0, max_logical_reads=3_000.0,
                    first_execution_time=now - timedelta(hours=2),
                    last_execution_time=now,
                ),
            ],
            None,
        )
        response = client.get(
            "/api/performance/slow-history",
            params={
                "database": "testdb",
                "start_time": (now - timedelta(hours=24)).isoformat(),
                "end_time": now.isoformat(),
                "threshold_ms": 30000,
            },
        )
        assert response.status_code == 200
        data = response.json()
        cand = data["candidates"][0]
        assert "workload_type" in cand
        assert cand["workload_type"] == "APPLICATION_OR_INTERACTIVE"
        assert "classification_evidence" in cand
        assert len(cand["classification_evidence"]) > 0
