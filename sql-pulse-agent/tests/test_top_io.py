"""Tests for the TopIoQueriesCollector."""

from unittest.mock import patch

from app.collectors.top_io import TopIoQueriesCollector


class TestTopIoQueriesCollector:
    def test_name(self):
        collector = TopIoQueriesCollector()
        assert collector.name == "top_io_queries"

    def test_default_top_n(self):
        collector = TopIoQueriesCollector()
        assert collector.top_n == 20

    @patch("app.collectors.base.execute_read_query")
    def test_collect_returns_results(self, mock_query):
        mock_query.return_value = [
            {
                "sql_text": "SELECT * FROM transactions WHERE date > @d",
                "query_hash": "0xAAAA1111",
                "execution_count": 3000,
                "total_logical_reads": 15000000,
                "avg_logical_reads": 5000.0,
                "total_physical_reads": 200000,
                "avg_physical_reads": 66.7,
                "total_logical_writes": 0,
                "avg_logical_writes": 0.0,
                "total_elapsed_time_ms": 900000,
                "database_name": "FinanceDB",
            }
        ]
        collector = TopIoQueriesCollector(top_n=15)
        result = collector.collect()

        assert result.metadata.success is True
        assert result.top_n == 15
        assert len(result.queries) == 1
        assert result.queries[0].total_logical_reads == 15000000
        assert result.queries[0].avg_physical_reads == 66.7

    @patch("app.collectors.base.execute_read_query")
    def test_collect_formats_top_n_in_query(self, mock_query):
        mock_query.return_value = []
        collector = TopIoQueriesCollector(top_n=7)
        collector.collect()

        called_query = mock_query.call_args[0][0]
        assert "TOP (7)" in called_query

    @patch("app.collectors.base.execute_read_query")
    def test_collect_handles_permission_error(self, mock_query):
        mock_query.side_effect = Exception("not have permission to VIEW SERVER STATE")
        collector = TopIoQueriesCollector()
        result = collector.collect()

        assert result.metadata.success is False
        assert result.metadata.permission_issue is True
        assert result.queries == []
