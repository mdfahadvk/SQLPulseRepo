"""Tests for the WaitStatisticsCollector."""

from unittest.mock import patch

from app.collectors.wait_stats import WaitStatisticsCollector


class TestWaitStatisticsCollector:
    def test_name(self):
        collector = WaitStatisticsCollector()
        assert collector.name == "wait_statistics"

    def test_default_top_n(self):
        collector = WaitStatisticsCollector()
        assert collector.top_n == 25

    def test_custom_top_n(self):
        collector = WaitStatisticsCollector(top_n=10)
        assert collector.top_n == 10

    @patch("app.collectors.base.execute_read_query")
    def test_collect_returns_results(self, mock_query):
        mock_query.return_value = [
            {
                "wait_type": "PAGEIOLATCH_SH",
                "waiting_tasks_count": 50000,
                "wait_time_ms": 300000,
                "max_wait_time_ms": 1200,
                "signal_wait_time_ms": 5000,
                "resource_wait_time_ms": 295000,
            },
            {
                "wait_type": "LCK_M_X",
                "waiting_tasks_count": 2000,
                "wait_time_ms": 150000,
                "max_wait_time_ms": 30000,
                "signal_wait_time_ms": 100,
                "resource_wait_time_ms": 149900,
            },
        ]
        collector = WaitStatisticsCollector(top_n=10)
        result = collector.collect()

        assert result.metadata.success is True
        assert result.top_n == 10
        assert len(result.statistics) == 2
        assert result.statistics[0].wait_type == "PAGEIOLATCH_SH"
        assert result.statistics[0].resource_wait_time_ms == 295000
        assert result.statistics[1].wait_type == "LCK_M_X"

    @patch("app.collectors.base.execute_read_query")
    def test_collect_empty_results(self, mock_query):
        mock_query.return_value = []
        collector = WaitStatisticsCollector()
        result = collector.collect()

        assert result.metadata.success is True
        assert result.statistics == []

    @patch("app.collectors.base.execute_read_query")
    def test_collect_handles_error(self, mock_query):
        mock_query.side_effect = Exception("Server unavailable")
        collector = WaitStatisticsCollector()
        result = collector.collect()

        assert result.metadata.success is False
        assert result.metadata.permission_issue is False
        assert "unavailable" in result.metadata.error_message.lower()
