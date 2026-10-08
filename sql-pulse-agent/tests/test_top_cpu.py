"""Tests for the TopCpuQueriesCollector."""

from unittest.mock import patch

from app.collectors.top_cpu import TopCpuQueriesCollector


class TestTopCpuQueriesCollector:
    def test_name(self):
        collector = TopCpuQueriesCollector()
        assert collector.name == "top_cpu_queries"

    def test_default_top_n(self):
        collector = TopCpuQueriesCollector()
        assert collector.top_n == 20

    def test_custom_top_n(self):
        collector = TopCpuQueriesCollector(top_n=10)
        assert collector.top_n == 10

    @patch("app.collectors.base.execute_read_query")
    def test_collect_returns_results(self, mock_query):
        mock_query.return_value = [
            {
                "sql_text": "SELECT * FROM products WHERE category_id = @p1",
                "query_hash": "0x1234ABCD",
                "execution_count": 5000,
                "total_cpu_time_ms": 900000,
                "avg_cpu_time_ms": 180.0,
                "total_elapsed_time_ms": 1200000,
                "avg_elapsed_time_ms": 240.0,
                "total_logical_reads": 2500000,
                "avg_logical_reads": 500.0,
                "database_name": "ProductDB",
            },
            {
                "sql_text": "SELECT COUNT(*) FROM audit_log",
                "query_hash": "0x5678EFGH",
                "execution_count": 200,
                "total_cpu_time_ms": 500000,
                "avg_cpu_time_ms": 2500.0,
                "total_elapsed_time_ms": 600000,
                "avg_elapsed_time_ms": 3000.0,
                "total_logical_reads": 8000000,
                "avg_logical_reads": 40000.0,
                "database_name": "AuditDB",
            },
        ]
        collector = TopCpuQueriesCollector(top_n=10)
        result = collector.collect()

        assert result.metadata.success is True
        assert result.top_n == 10
        assert len(result.queries) == 2
        assert result.queries[0].total_cpu_time_ms == 900000
        assert result.queries[1].avg_cpu_time_ms == 2500.0

    @patch("app.collectors.base.execute_read_query")
    def test_collect_formats_top_n_in_query(self, mock_query):
        mock_query.return_value = []
        collector = TopCpuQueriesCollector(top_n=5)
        collector.collect()

        called_query = mock_query.call_args[0][0]
        assert "TOP (5)" in called_query

    @patch("app.collectors.base.execute_read_query")
    def test_collect_handles_error(self, mock_query):
        mock_query.side_effect = Exception("Connection reset")
        collector = TopCpuQueriesCollector()
        result = collector.collect()

        assert result.metadata.success is False
        assert result.metadata.permission_issue is False
        assert result.queries == []
