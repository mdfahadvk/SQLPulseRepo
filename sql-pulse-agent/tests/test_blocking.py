"""Tests for the BlockingSessionsCollector."""

from unittest.mock import patch

from app.collectors.blocking import BlockingSessionsCollector


class TestBlockingSessionsCollector:
    def test_name(self):
        collector = BlockingSessionsCollector()
        assert collector.name == "blocking_sessions"

    @patch("app.collectors.base.execute_read_query")
    def test_collect_returns_blocking_chain(self, mock_query):
        mock_query.return_value = [
            {
                "session_id": 60,
                "blocking_session_id": 55,
                "wait_type": "LCK_M_X",
                "wait_time_ms": 8000,
                "wait_resource": "KEY: 5:72057594039:1",
                "database_name": "SalesDB",
                "sql_text": "UPDATE orders SET status = 1",
                "status": "suspended",
                "login_name": "app_user",
                "host_name": "APPSERVER",
                "program_name": "OrderSvc",
                "is_head_blocker": 1,
            },
            {
                "session_id": 65,
                "blocking_session_id": 60,
                "wait_type": "LCK_M_S",
                "wait_time_ms": 5000,
                "wait_resource": "KEY: 5:72057594039:1",
                "database_name": "SalesDB",
                "sql_text": "SELECT * FROM orders WHERE id = 1",
                "status": "suspended",
                "login_name": "read_user",
                "host_name": "WEBSERVER",
                "program_name": "WebApp",
                "is_head_blocker": 0,
            },
        ]
        collector = BlockingSessionsCollector()
        result = collector.collect()

        assert result.metadata.success is True
        assert len(result.sessions) == 2
        assert result.blocking_chain_count == 1
        assert result.sessions[0].is_head_blocker is True
        assert result.sessions[1].is_head_blocker is False

    @patch("app.collectors.base.execute_read_query")
    def test_collect_no_blocking(self, mock_query):
        mock_query.return_value = []
        collector = BlockingSessionsCollector()
        result = collector.collect()

        assert result.metadata.success is True
        assert result.sessions == []
        assert result.blocking_chain_count == 0

    @patch("app.collectors.base.execute_read_query")
    def test_collect_handles_permission_error(self, mock_query):
        mock_query.side_effect = Exception("permission denied on view server state")
        collector = BlockingSessionsCollector()
        result = collector.collect()

        assert result.metadata.success is False
        assert result.metadata.permission_issue is True
