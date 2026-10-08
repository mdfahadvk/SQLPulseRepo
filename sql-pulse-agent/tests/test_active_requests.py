"""Tests for the ActiveRequestsCollector."""

from unittest.mock import patch

from app.collectors.active_requests import ActiveRequestsCollector


class TestActiveRequestsCollector:
    def test_name(self):
        collector = ActiveRequestsCollector()
        assert collector.name == "active_requests"

    @patch("app.collectors.base.execute_read_query")
    def test_collect_returns_results(self, mock_query):
        mock_query.return_value = [
            {
                "session_id": 55,
                "request_id": 0,
                "status": "running",
                "command": "SELECT",
                "sql_text": "SELECT * FROM orders",
                "database_name": "SalesDB",
                "wait_type": None,
                "wait_time_ms": 0,
                "cpu_time_ms": 120,
                "total_elapsed_time_ms": 5000,
                "reads": 100,
                "writes": 0,
                "logical_reads": 500,
                "blocking_session_id": None,
                "open_transaction_count": 0,
                "login_name": "app_user",
                "host_name": "WEBSERVER1",
                "program_name": "MyApp",
            }
        ]
        collector = ActiveRequestsCollector()
        result = collector.collect()

        assert result.metadata.success is True
        assert result.metadata.collector_name == "active_requests"
        assert len(result.requests) == 1
        assert result.requests[0].session_id == 55
        assert result.requests[0].cpu_time_ms == 120
        assert result.requests[0].sql_text == "SELECT * FROM orders"

    @patch("app.collectors.base.execute_read_query")
    def test_collect_empty_results(self, mock_query):
        mock_query.return_value = []
        collector = ActiveRequestsCollector()
        result = collector.collect()

        assert result.metadata.success is True
        assert result.requests == []

    @patch("app.collectors.base.execute_read_query")
    def test_collect_handles_permission_error(self, mock_query):
        mock_query.side_effect = Exception(
            "The user does not have permission to perform this action. "
            "VIEW SERVER STATE permission denied."
        )
        collector = ActiveRequestsCollector()
        result = collector.collect()

        assert result.metadata.success is False
        assert result.metadata.permission_issue is True
        assert "permission" in result.metadata.error_message.lower()
        assert result.requests == []

    @patch("app.collectors.base.execute_read_query")
    def test_collect_handles_generic_error(self, mock_query):
        mock_query.side_effect = Exception("Connection timeout")
        collector = ActiveRequestsCollector()
        result = collector.collect()

        assert result.metadata.success is False
        assert result.metadata.permission_issue is False
        assert "timeout" in result.metadata.error_message.lower()
