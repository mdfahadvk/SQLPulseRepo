"""Collector for Query Store slow-query history.

Reads from Query Store catalog views (read-only) to find queries whose
execution duration exceeded a configurable threshold within a time range.

Returns granular status when Query Store is not accessible:
  AVAILABLE          — READ_WRITE state, full access
  AVAILABLE_READ_ONLY — READ_ONLY state, can still read history
  DISABLED           — Query Store is OFF for the database
  PERMISSION_DENIED  — Monitoring account lacks required permission
  QUERY_FAILED       — An unexpected SQL/connection error occurred
"""

import logging
import time
from datetime import datetime

from app.database.connection import execute_read_query, validate_database_name
from app.database.performance_queries import (
    QUERY_STORE_SLOW_HISTORY,
    QUERY_STORE_STATUS,
)
from app.models.slow_history import (
    QueryStoreStatus,
    SlowQueryRecord,
)

logger = logging.getLogger(__name__)


def check_query_store_status(database: str) -> tuple[QueryStoreStatus, str | None]:
    """Check Query Store availability for a specific database.

    The query is executed in the context of the specified database
    (not the default connection database).

    Args:
        database: Target database name (must be pre-validated).

    Returns:
        Tuple of (status, message) where message provides human-readable
        detail for non-available statuses.
    """
    if not validate_database_name(database):
        return QueryStoreStatus.QUERY_FAILED, "Invalid database name"

    try:
        rows = execute_read_query(QUERY_STORE_STATUS, database=database)
        if not rows:
            logger.warning(
                "Query Store status query returned no rows for database '%s'",
                database,
            )
            return QueryStoreStatus.DISABLED, (
                f"Query Store options not found for database '{database}'."
            )

        row = rows[0]
        state = (row.get("actual_state_desc") or "").upper().strip()

        logger.info(
            "Query Store status for '%s': actual_state_desc=%s, "
            "desired_state_desc=%s, readonly_reason=%s",
            database,
            state,
            row.get("desired_state_desc"),
            row.get("readonly_reason"),
        )

        if state == "READ_WRITE":
            return QueryStoreStatus.AVAILABLE, None
        elif state == "READ_ONLY":
            return QueryStoreStatus.AVAILABLE_READ_ONLY, None
        elif state == "OFF":
            return QueryStoreStatus.DISABLED, (
                f"Query Store is disabled (OFF) for database '{database}'."
            )
        else:
            # ERROR, TRANSITIONING, or unknown states
            return QueryStoreStatus.DISABLED, (
                f"Query Store is in state '{state}' for database '{database}'."
            )

    except Exception as exc:
        msg = str(exc)
        msg_lower = msg.lower()

        if "permission" in msg_lower or "denied" in msg_lower:
            logger.warning(
                "Query Store permission denied for database '%s': %s",
                database,
                _sanitize_error(msg),
            )
            return QueryStoreStatus.PERMISSION_DENIED, (
                f"Permission denied when checking Query Store for database '{database}'."
            )
        else:
            logger.error(
                "Query Store availability check failed for database '%s': %s",
                database,
                _sanitize_error(msg),
            )
            return QueryStoreStatus.QUERY_FAILED, (
                f"Query Store availability check failed for database '{database}'."
            )


def collect_slow_history(
    database: str,
    start_time: datetime,
    end_time: datetime,
    threshold_ms: float,
    top_n: int = 20,
) -> tuple[QueryStoreStatus, list[SlowQueryRecord], str | None]:
    """Collect slow queries from Query Store for the given time range.

    All queries execute in the context of the specified database to ensure
    correct Query Store access (not the default connection database).

    Args:
        database: Target database name.
        start_time: Beginning of the time window.
        end_time: End of the time window.
        threshold_ms: Minimum max_duration to include (milliseconds).
            Query Store stores durations in microseconds internally;
            the SQL query converts to milliseconds before comparison.
        top_n: Maximum number of records to return.

    Returns:
        (status, records, message) — status indicates Query Store availability;
        records are the matching slow queries; message provides detail for
        non-available statuses.
    """
    collection_start = time.monotonic()

    status, status_msg = check_query_store_status(database)
    if not status.is_accessible:
        logger.info(
            "Query Store not accessible for '%s' (status=%s)",
            database, status.value,
        )
        return status, [], status_msg

    try:
        query = QUERY_STORE_SLOW_HISTORY.format(top_n=int(top_n))
        rows = execute_read_query(
            query,
            params=(start_time, end_time, threshold_ms),
            database=database,
        )
        records = [SlowQueryRecord(**row) for row in rows]

        duration = time.monotonic() - collection_start
        logger.info(
            "Query Store collection for '%s': status=%s, rows_evaluated=%d, "
            "candidates=%d, threshold_ms=%.0f, duration=%.2fs",
            database,
            status.value,
            len(rows),
            len(records),
            threshold_ms,
            duration,
        )
        return status, records, None

    except Exception as exc:
        msg = str(exc)
        msg_lower = msg.lower()
        duration = time.monotonic() - collection_start

        if "permission" in msg_lower or "denied" in msg_lower:
            logger.warning(
                "Query Store data access denied for '%s' after %.2fs: %s",
                database, duration, _sanitize_error(msg),
            )
            return QueryStoreStatus.PERMISSION_DENIED, [], (
                f"Permission denied when reading Query Store data for database '{database}'."
            )
        else:
            logger.error(
                "Query Store slow-history query failed for '%s' after %.2fs: %s",
                database, duration, _sanitize_error(msg),
            )
            return QueryStoreStatus.QUERY_FAILED, [], (
                f"Query Store data collection failed for database '{database}'."
            )


def _sanitize_error(msg: str) -> str:
    """Remove credentials and connection details from error messages before logging."""
    import re
    # Remove password fragments
    sanitized = re.sub(r"(?i)(PWD|PASSWORD)\s*=\s*[^\s;]+", "PWD=***", msg)
    # Remove full connection strings
    sanitized = re.sub(r"(?i)(SERVER|UID|DRIVER)\s*=\s*[^\s;]+", r"\1=***", sanitized)
    return sanitized
