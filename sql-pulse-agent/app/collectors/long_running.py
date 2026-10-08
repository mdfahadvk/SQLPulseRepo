"""Collector for long-running SQL Server queries."""

import logging

from app.collectors.base import BaseCollector
from app.database.performance_queries import LONG_RUNNING_QUERIES, LONG_RUNNING_QUERIES_BY_DATABASE
from app.models.performance import LongRunningQueriesResult, LongRunningQuery

logger = logging.getLogger(__name__)

# Default threshold: 5 seconds
DEFAULT_THRESHOLD_MS = 5000


class LongRunningQueriesCollector(BaseCollector):
    """Collects queries exceeding an elapsed-time threshold."""

    def __init__(self, threshold_ms: int = DEFAULT_THRESHOLD_MS, database: str | None = None) -> None:
        super().__init__(database=database)
        self.threshold_ms = threshold_ms

    @property
    def name(self) -> str:
        return "long_running_queries"

    def collect(self) -> LongRunningQueriesResult:
        """Collect long-running queries.

        Required permission: VIEW SERVER STATE
        """
        try:
            if self._database:
                rows = self._execute_query(
                    LONG_RUNNING_QUERIES_BY_DATABASE, params=(self.threshold_ms, self._database)
                )
            else:
                rows = self._execute_query(
                    LONG_RUNNING_QUERIES, params=(self.threshold_ms,)
                )
            queries = [LongRunningQuery(**row) for row in rows]
            return LongRunningQueriesResult(
                metadata=self._build_metadata(),
                threshold_ms=self.threshold_ms,
                queries=queries,
            )
        except Exception as exc:
            permission_issue = self._is_permission_error(exc)
            logger.error(
                "LongRunningQueriesCollector failed: %s (permission_issue=%s)",
                str(exc),
                permission_issue,
            )
            return LongRunningQueriesResult(
                metadata=self._build_metadata(
                    success=False,
                    error_message=str(exc),
                    permission_issue=permission_issue,
                ),
                threshold_ms=self.threshold_ms,
            )
