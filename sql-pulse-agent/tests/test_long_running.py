"""Tests for the LongRunningQueriesCollector."""

from unittest.mock import patch

from app.collectors.long_running import LongRunningQueriesCollector


class TestLongRunningQueriesCollector:
    def test_name(self):
        collector = LongRunningQueriesCollector()
        assert collector.name == "long_running_queries"

    def test_default_threshold(self):
        collector = LongRunningQueriesCollector()
        assert collector.threshold_ms == 5000

    def test_custom_threshold(self):
        collector = LongRunningQueriesCollector(threshold_ms=10000)
        assert collector.threshold_ms == 10000

    @patch("app.collectors.base.execute_read_query")
    def test_collect_returns_results(self, mock_query):
        mock_query.return_value = [
            {
                "session_id": 70,
                "request_id": 0,
                "sql_text": "SELECT * FROM large_table",
                "database_name": "AnalyticsDB",
                "elapsed_time_ms": 15000,
                "cpu_time_ms": 8000,
                "reads": 50000,
                "logical_reads": 120000,
                "writes": 0,
                "wait_type": "CXPACKET",
                "wait_time_ms": 3000,
                "status": "running",
                "login_name": "report_user",
                "host_name": "REPORTSVR",
                "program_name": "SSRS",
            }
        ]
        collector = LongRunningQueriesCollector(threshold_ms=5000)
        result = collector.collect()

        assert result.metadata.success is True
        assert result.threshold_ms == 5000
        assert len(result.queries) == 1
        assert result.queries[0].elapsed_time_ms == 15000
        assert result.queries[0].session_id == 70

    @patch("app.collectors.base.execute_read_query")
    def test_collect_passes_threshold_as_param(self, mock_query):
        mock_query.return_value = []
        collector = LongRunningQueriesCollector(threshold_ms=10000)
        collector.collect()

        mock_query.assert_called_once()
        call_args = mock_query.call_args
        # params passed as positional arg or keyword
        all_args = call_args[0] + tuple(call_args[1].values())
        assert (10000,) in all_args or any(
            (10000,) == a for a in call_args[0] if isinstance(a, tuple)
        )

    @patch("app.collectors.base.execute_read_query")
    def test_collect_handles_permission_error(self, mock_query):
        mock_query.side_effect = Exception("VIEW SERVER STATE permission denied")
        collector = LongRunningQueriesCollector()
        result = collector.collect()

        assert result.metadata.success is False
        assert result.metadata.permission_issue is True
        assert result.queries == []
