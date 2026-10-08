"""Read-only SQL Server connection management."""

import logging
import re
from contextlib import contextmanager
from typing import Generator

import pyodbc

from app.config import get_settings

logger = logging.getLogger(__name__)

# Statements that could modify data or configuration — blocked as a safety net.
_BLOCKED_PREFIXES = (
    "INSERT",
    "UPDATE",
    "DELETE",
    "DROP",
    "ALTER",
    "CREATE",
    "TRUNCATE",
    "EXEC",
    "EXECUTE",
    "GRANT",
    "REVOKE",
    "DENY",
    "MERGE",
    "BULK",
    "DBCC",
    "SHUTDOWN",
    "KILL",
    "BACKUP",
    "RESTORE",
    "sp_",
    "xp_",
)

# Allowed characters in a database name (prevents injection)
_VALID_DB_NAME_RE = re.compile(r"^[A-Za-z0-9_\-]+$")


def validate_database_name(name: str) -> bool:
    """Return True if name is a safe SQL Server database identifier."""
    return bool(name and _VALID_DB_NAME_RE.match(name) and len(name) <= 128)


def _validate_read_only(query: str) -> None:
    """Raise ValueError if query appears to be a write operation."""
    normalized = query.strip().upper()
    for prefix in _BLOCKED_PREFIXES:
        if normalized.startswith(prefix):
            raise ValueError(
                f"Blocked potentially mutating SQL statement starting with '{prefix}'. "
                "SQLPulse only permits read-only queries."
            )


def get_connection(database: str | None = None) -> pyodbc.Connection:
    """Create a new pyodbc connection to SQL Server (read-only intent).

    Args:
        database: Target database. Defaults to sql_default_database from settings.
    """
    settings = get_settings()
    if not settings.is_database_configured:
        raise ConnectionError(
            "SQL Server connection is not configured. "
            "Set SQL_SERVER, SQL_USERNAME, and SQL_PASSWORD in your .env file."
        )
    logger.debug("Opening SQL Server connection to %s", settings.sql_server)
    conn = pyodbc.connect(
        settings.build_connection_string(database=database),
        timeout=settings.sql_connection_timeout,
        autocommit=True,  # No implicit transactions — read-only usage
    )
    conn.timeout = settings.sql_query_timeout
    return conn


@contextmanager
def get_cursor(database: str | None = None) -> Generator[pyodbc.Cursor, None, None]:
    """Context manager that yields a cursor and ensures connection cleanup."""
    conn = get_connection(database=database)
    try:
        cursor = conn.cursor()
        yield cursor
    finally:
        conn.close()
        logger.debug("SQL Server connection closed")


def execute_read_query(
    query: str, params: tuple | None = None, database: str | None = None
) -> list[dict]:
    """Execute a read-only query and return results as a list of dicts.

    Args:
        query: SQL SELECT query.
        params: Optional query parameters.
        database: Target database (None = default from settings).

    Raises:
        ValueError: If the query is detected as a write operation.
        pyodbc.Error: On database connectivity or query errors.
    """
    _validate_read_only(query)

    with get_cursor(database=database) as cursor:
        if params:
            cursor.execute(query, params)
        else:
            cursor.execute(query)

        columns = [desc[0] for desc in cursor.description] if cursor.description else []
        rows = cursor.fetchall()
        return [dict(zip(columns, row)) for row in rows]


def test_connection() -> bool:
    """Test SQL Server connectivity with a minimal read-only query.

    Returns True if connection is successful, False otherwise.
    """
    try:
        with get_cursor() as cursor:
            cursor.execute("SELECT 1 AS connected")
            result = cursor.fetchone()
            return result is not None and result[0] == 1
    except (pyodbc.Error, Exception) as exc:
        logger.error("SQL Server connection test failed: %s", str(exc))
        return False
