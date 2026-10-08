"""Tests for database-scope filtering and system/background exclusion."""

from unittest.mock import patch, MagicMock

import pytest

from app.collectors.active_requests import ActiveRequestsCollector
from app.collectors.blocking import BlockingSessionsCollector
from app.collectors.long_running import LongRunningQueriesCollector
from app.collectors.snapshot import collect_performance_snapshot
from app.collectors.top_cpu import TopCpuQueriesCollector
from app.collectors.top_io import TopIoQueriesCollector
from app.detectors.long_running import LongRunningDetector, _is_system_background_query
from app.detectors.thresholds import DetectorThresholds
from app.models.findings import Severity, FindingCategory
from app.models.performance import (
    CollectorMetadata,
    LongRunningQueriesResult,
    LongRunningQuery,
    PerformanceSnapshot,
)


# =============================================================================
# Database-scope collector filtering tests
# =============================================================================


class TestActiveRequestsDatabaseScope:
    @patch("app.collectors.active_requests.ActiveRequestsCollector._execute_query")
    def test_uses_filtered_query_when_database_set(self, mock_exec):
        """When database is set, collector should pass database as parameter."""
        mock_exec.return_value = []
        collector = ActiveRequestsCollector(database="AppDB")
        collector.collect()
        # Should call with params containing the database name
        mock_exec.assert_called_once()
        call_args = mock_exec.call_args
        assert call_args[1].get("params") == ("AppDB",) or call_args[0][1] == ("AppDB",)

    @patch("app.collectors.active_requests.ActiveRequestsCollector._execute_query")
    def test_uses_unfiltered_query_when_no_database(self, mock_exec):
        """When no database is set, collector should use unfiltered query."""
        mock_exec.return_value = []
        collector = ActiveRequestsCollector(database=None)
        collector.collect()
        mock_exec.assert_called_once()
        call_args = mock_exec.call_args
        # No params for unfiltered query
        if len(call_args[0]) > 1:
            assert call_args[0][1] is None or call_args[0][1] == ()
        else:
            assert "params" not in call_args[1] or call_args[1].get("params") is None


class TestLongRunningCollectorDatabaseScope:
    @patch("app.collectors.long_running.LongRunningQueriesCollector._execute_query")
    def test_uses_filtered_query_when_database_set(self, mock_exec):
        """When database is set, should pass (threshold, database) as params."""
        mock_exec.return_value = []
        collector = LongRunningQueriesCollector(threshold_ms=5000, database="AppDB")
        collector.collect()
        mock_exec.assert_called_once()
        call_args = mock_exec.call_args
        params = call_args[1].get("params") or call_args[0][1]
        assert params == (5000, "AppDB")

    @patch("app.collectors.long_running.LongRunningQueriesCollector._execute_query")
    def test_uses_unfiltered_query_when_no_database(self, mock_exec):
        """When no database, should pass only threshold as param."""
        mock_exec.return_value = []
        collector = LongRunningQueriesCollector(threshold_ms=5000, database=None)
        collector.collect()
        mock_exec.assert_called_once()
        call_args = mock_exec.call_args
        params = call_args[1].get("params") or call_args[0][1]
        assert params == (5000,)


class TestBlockingCollectorDatabaseScope:
    @patch("app.collectors.blocking.BlockingSessionsCollector._execute_query")
    def test_uses_filtered_query_when_database_set(self, mock_exec):
        mock_exec.return_value = []
        collector = BlockingSessionsCollector(database="AppDB")
        collector.collect()
        mock_exec.assert_called_once()
        call_args = mock_exec.call_args
        params = call_args[1].get("params") or call_args[0][1]
        assert params == ("AppDB",)


class TestTopCpuCollectorDatabaseScope:
    @patch("app.collectors.top_cpu.TopCpuQueriesCollector._execute_query")
    def test_uses_filtered_query_when_database_set(self, mock_exec):
        mock_exec.return_value = []
        collector = TopCpuQueriesCollector(top_n=20, database="AppDB")
        collector.collect()
        mock_exec.assert_called_once()
        call_args = mock_exec.call_args
        params = call_args[1].get("params") or call_args[0][1]
        assert params == ("AppDB",)

    @patch("app.collectors.top_cpu.TopCpuQueriesCollector._execute_query")
    def test_uses_unfiltered_query_when_no_database(self, mock_exec):
        mock_exec.return_value = []
        collector = TopCpuQueriesCollector(top_n=20, database=None)
        collector.collect()
        mock_exec.assert_called_once()
        call_args = mock_exec.call_args
        # Unfiltered: no params
        if len(call_args[0]) > 1:
            assert call_args[0][1] is None
        else:
            assert call_args[1].get("params") is None


class TestTopIoCollectorDatabaseScope:
    @patch("app.collectors.top_io.TopIoQueriesCollector._execute_query")
    def test_uses_filtered_query_when_database_set(self, mock_exec):
        mock_exec.return_value = []
        collector = TopIoQueriesCollector(top_n=20, database="AppDB")
        collector.collect()
        mock_exec.assert_called_once()
        call_args = mock_exec.call_args
        params = call_args[1].get("params") or call_args[0][1]
        assert params == ("AppDB",)


# =============================================================================
# System/background query exclusion tests
# =============================================================================


class TestSystemBackgroundExclusion:
    def test_sp_server_diagnostics_excluded(self):
        q = LongRunningQuery(
            session_id=1, request_id=0, elapsed_time_ms=999999,
            sql_text="EXEC sp_server_diagnostics",
            program_name="sp_server_diagnostics",
        )
        assert _is_system_background_query(q) is True

    def test_diagnostics_sleep_wait_excluded(self):
        q = LongRunningQuery(
            session_id=1, request_id=0, elapsed_time_ms=999999,
            wait_type="SP_SERVER_DIAGNOSTICS_SLEEP",
        )
        assert _is_system_background_query(q) is True

    def test_waitfor_wait_excluded(self):
        q = LongRunningQuery(
            session_id=1, request_id=0, elapsed_time_ms=60000,
            wait_type="WAITFOR",
            sql_text="WAITFOR(RECEIVE ... )",
        )
        assert _is_system_background_query(q) is True

    def test_broker_receive_excluded(self):
        q = LongRunningQuery(
            session_id=1, request_id=0, elapsed_time_ms=999999,
            wait_type="BROKER_RECEIVE_WAITFOR",
            sql_text="WAITFOR(RECEIVE conversation_handle FROM ...",
        )
        assert _is_system_background_query(q) is True

    def test_normal_user_query_not_excluded(self):
        q = LongRunningQuery(
            session_id=50, request_id=0, elapsed_time_ms=30000,
            sql_text="SELECT * FROM Orders WHERE OrderDate > '2024-01-01'",
            program_name="MyApp",
            wait_type="CXPACKET",
        )
        assert _is_system_background_query(q) is False

    def test_long_running_user_query_not_excluded(self):
        q = LongRunningQuery(
            session_id=55, request_id=0, elapsed_time_ms=120000,
            sql_text="SELECT COUNT(*) FROM LargeTable WITH(NOLOCK)",
            program_name=".Net SqlClient Data Provider",
        )
        assert _is_system_background_query(q) is False

    def test_detector_skips_system_queries(self):
        """The detector should produce no findings for system-only queries."""
        meta = CollectorMetadata(collector_name="test", server_host="srv", database="db")
        snapshot = PerformanceSnapshot(
            snapshot_id="sys-test",
            server_host="srv",
            database="db",
            long_running_queries=LongRunningQueriesResult(
                metadata=meta, threshold_ms=5000, queries=[
                    LongRunningQuery(
                        session_id=1, request_id=0, elapsed_time_ms=999999,
                        sql_text="EXEC sp_server_diagnostics",
                        program_name="sp_server_diagnostics",
                        wait_type="SP_SERVER_DIAGNOSTICS_SLEEP",
                    ),
                    LongRunningQuery(
                        session_id=2, request_id=0, elapsed_time_ms=600000,
                        sql_text="WAITFOR(RECEIVE conversation_handle FROM ...",
                        wait_type="BROKER_RECEIVE_WAITFOR",
                    ),
                ],
            ),
        )
        detector = LongRunningDetector(thresholds=DetectorThresholds())
        findings = detector.detect(snapshot)
        assert findings == []

    def test_detector_still_detects_real_long_queries(self):
        """Real user queries should still generate findings."""
        meta = CollectorMetadata(collector_name="test", server_host="srv", database="db")
        snapshot = PerformanceSnapshot(
            snapshot_id="real-test",
            server_host="srv",
            database="db",
            long_running_queries=LongRunningQueriesResult(
                metadata=meta, threshold_ms=5000, queries=[
                    # System query (should be excluded)
                    LongRunningQuery(
                        session_id=1, request_id=0, elapsed_time_ms=999999,
                        program_name="sp_server_diagnostics",
                        wait_type="SP_SERVER_DIAGNOSTICS_SLEEP",
                    ),
                    # Real user query (should generate finding)
                    LongRunningQuery(
                        session_id=50, request_id=0, elapsed_time_ms=150000,
                        sql_text="SELECT * FROM huge_table",
                        program_name="MyApp",
                    ),
                ],
            ),
        )
        detector = LongRunningDetector(thresholds=DetectorThresholds())
        findings = detector.detect(snapshot)
        assert len(findings) > 0
        # The finding should be about the real query, not the system one
        for f in findings:
            assert "sp_server_diagnostics" not in (f.evidence[0] if f.evidence else "")


# =============================================================================
# Wait statistics not-applicable metadata for database scope
# =============================================================================


class TestWaitStatsNotApplicable:
    @patch("app.collectors.snapshot.ActiveRequestsCollector.collect")
    @patch("app.collectors.snapshot.LongRunningQueriesCollector.collect")
    @patch("app.collectors.snapshot.BlockingSessionsCollector.collect")
    @patch("app.collectors.snapshot.TopCpuQueriesCollector.collect")
    @patch("app.collectors.snapshot.TopIoQueriesCollector.collect")
    def test_database_scope_wait_stats_not_null(
        self, mock_io, mock_cpu, mock_blocking, mock_long, mock_active
    ):
        """Database-scope snapshot should have wait_statistics with metadata, not null."""
        from app.models.performance import (
            ActiveRequestsResult,
            BlockingSessionsResult,
            LongRunningQueriesResult,
            TopCpuQueriesResult,
            TopIoQueriesResult,
        )

        meta = CollectorMetadata(collector_name="test", server_host="localhost", database="AppDB")
        mock_active.return_value = ActiveRequestsResult(metadata=meta)
        mock_long.return_value = LongRunningQueriesResult(metadata=meta, threshold_ms=5000)
        mock_blocking.return_value = BlockingSessionsResult(metadata=meta)
        mock_cpu.return_value = TopCpuQueriesResult(metadata=meta, top_n=20)
        mock_io.return_value = TopIoQueriesResult(metadata=meta, top_n=20)

        snapshot = collect_performance_snapshot(scope="database", database="AppDB")

        # wait_statistics should NOT be None
        assert snapshot.wait_statistics is not None
        # It should have success=True but an explanatory message
        assert snapshot.wait_statistics.metadata.success is True
        assert "not applicable" in snapshot.wait_statistics.metadata.error_message.lower() or \
               "server-level only" in snapshot.wait_statistics.metadata.error_message.lower()
        # No actual wait stats data
        assert snapshot.wait_statistics.statistics == []

    @patch("app.collectors.snapshot.ActiveRequestsCollector.collect")
    @patch("app.collectors.snapshot.LongRunningQueriesCollector.collect")
    @patch("app.collectors.snapshot.BlockingSessionsCollector.collect")
    @patch("app.collectors.snapshot.TopCpuQueriesCollector.collect")
    @patch("app.collectors.snapshot.TopIoQueriesCollector.collect")
    @patch("app.collectors.snapshot.WaitStatisticsCollector.collect")
    def test_server_scope_wait_stats_collected(
        self, mock_waits, mock_io, mock_cpu, mock_blocking, mock_long, mock_active
    ):
        """Server-scope snapshot should collect wait_statistics normally."""
        from app.models.performance import (
            ActiveRequestsResult,
            BlockingSessionsResult,
            LongRunningQueriesResult,
            TopCpuQueriesResult,
            TopIoQueriesResult,
            WaitStatisticsResult,
        )

        meta = CollectorMetadata(collector_name="test", server_host="localhost", database="master")
        mock_active.return_value = ActiveRequestsResult(metadata=meta)
        mock_long.return_value = LongRunningQueriesResult(metadata=meta, threshold_ms=5000)
        mock_blocking.return_value = BlockingSessionsResult(metadata=meta)
        mock_cpu.return_value = TopCpuQueriesResult(metadata=meta, top_n=20)
        mock_io.return_value = TopIoQueriesResult(metadata=meta, top_n=20)
        mock_waits.return_value = WaitStatisticsResult(metadata=meta, top_n=25)

        snapshot = collect_performance_snapshot(scope="server")

        assert snapshot.wait_statistics is not None
        mock_waits.assert_called_once()
