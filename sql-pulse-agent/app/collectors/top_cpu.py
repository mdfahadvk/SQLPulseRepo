"""Collector for top CPU-consuming queries."""

import logging

from app.collectors.base import BaseCollector
from app.database.performance_queries import TOP_CPU_QUERIES, TOP_CPU_QUERIES_BY_DATABASE
from app.models.performance import TopCpuQueriesResult, TopCpuQuery

logger = logging.getLogger(__name__)

DEFAULT_TOP_N = 20


class TopCpuQueriesCollector(BaseCollector):
    """Collects top CPU-consuming queries from plan cache statistics."""

    def __init__(self, top_n: int = DEFAULT_TOP_N, database: str | None = None) -> None:
        super().__init__(database=database)
        self.top_n = top_n

    @property
    def name(self) -> str:
        return "top_cpu_queries"

    def collect(self) -> TopCpuQueriesResult:
        """Collect top CPU queries.

        Required permission: VIEW SERVER STATE
        """
        try:
            if self._database:
                query = TOP_CPU_QUERIES_BY_DATABASE.format(top_n=int(self.top_n))
                rows = self._execute_query(query, params=(self._database,))
            else:
                query = TOP_CPU_QUERIES.format(top_n=int(self.top_n))
                rows = self._execute_query(query)
            queries = [TopCpuQuery(**row) for row in rows]
            return TopCpuQueriesResult(
                metadata=self._build_metadata(),
                top_n=self.top_n,
                queries=queries,
            )
        except Exception as exc:
            permission_issue = self._is_permission_error(exc)
            logger.error(
                "TopCpuQueriesCollector failed: %s (permission_issue=%s)",
                str(exc),
                permission_issue,
            )
            return TopCpuQueriesResult(
                metadata=self._build_metadata(
                    success=False,
                    error_message=str(exc),
                    permission_issue=permission_issue,
                ),
                top_n=self.top_n,
            )
