"""Tests for the database connection layer."""

from unittest.mock import MagicMock, patch

import pytest

from app.database.connection import _validate_read_only, execute_read_query


class TestValidateReadOnly:
    """Tests for the SQL statement safety validator."""

    def test_allows_select(self):
        _validate_read_only("SELECT * FROM sys.dm_exec_requests")

    def test_allows_select_with_whitespace(self):
        _validate_read_only("  SELECT 1")

    def test_blocks_insert(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("INSERT INTO table1 VALUES (1)")

    def test_blocks_update(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("UPDATE table1 SET col = 1")

    def test_blocks_delete(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("DELETE FROM table1")

    def test_blocks_drop(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("DROP TABLE table1")

    def test_blocks_alter(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("ALTER TABLE table1 ADD col INT")

    def test_blocks_exec(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("EXEC sp_who2")

    def test_blocks_create(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("CREATE INDEX idx ON t(c)")

    def test_blocks_truncate(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("TRUNCATE TABLE table1")

    def test_blocks_kill(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("KILL 55")

    def test_case_insensitive_blocking(self):
        with pytest.raises(ValueError, match="Blocked"):
            _validate_read_only("insert into table1 values (1)")


class TestExecuteReadQuery:
    """Tests for execute_read_query with mocked connection."""

    @patch("app.database.connection.get_cursor")
    def test_executes_select_and_returns_dicts(self, mock_get_cursor):
        mock_cursor = MagicMock()
        mock_cursor.description = [("id",), ("name",)]
        mock_cursor.fetchall.return_value = [(1, "db1"), (2, "db2")]
        mock_get_cursor.return_value.__enter__ = MagicMock(return_value=mock_cursor)
        mock_get_cursor.return_value.__exit__ = MagicMock(return_value=False)

        result = execute_read_query("SELECT id, name FROM sys.databases")

        assert result == [{"id": 1, "name": "db1"}, {"id": 2, "name": "db2"}]

    @patch("app.database.connection.get_cursor")
    def test_blocks_mutating_query(self, mock_get_cursor):
        with pytest.raises(ValueError, match="Blocked"):
            execute_read_query("DROP DATABASE test")

        # Cursor should never be opened for a blocked query
        mock_get_cursor.assert_not_called()
