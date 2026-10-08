"""Collector for top I/O-consuming queries."""

import logging

from app.collectors.base import BaseCollector
from app.database.performance_queries import TOP_IO_QUERIES, TOP_IO_QUERIES_BY_DATABASE
from app.models.performance import TopIoQueriesResult, TopIoQuery

logger = logging.getLogger(__name__)

DEFAULT_TOP_N = 20


class TopIoQueriesCollector(BaseCollector):
    """Collects top logical-read / I/O-consuming queries from plan cache."""

    def __init__(self, top_n: int = DEFAULT_TOP_N, database: str | None = None) -> None:
        super().__init__(database=database)
        self.top_n = top_n

    @property
    def name(self) -> str:
        return "top_io_queries"

    def collect(self) -> TopIoQueriesResult:
        """Collect top I/O queries.

        Required permission: VIEW SERVER STATE
        """
        try:
            if self._database:
                query = TOP_IO_QUERIES_BY_DATABASE.format(top_n=int(self.top_n))
                rows = self._execute_query(query, params=(self._database,))
            else:
                query = TOP_IO_QUERIES.format(top_n=int(self.top_n))
                rows = self._execute_query(query)
            queries = [TopIoQuery(**row) for row in rows]
            return TopIoQueriesResult(
                metadata=self._build_metadata(),
                top_n=self.top_n,
                queries=queries,
            )
        except Exception as exc:
            permission_issue = self._is_permission_error(exc)
            logger.error(
                "TopIoQueriesCollector failed: %s (permission_issue=%s)",
                str(exc),
                permission_issue,
            )
            return TopIoQueriesResult(
                metadata=self._build_metadata(
                    success=False,
                    error_message=str(exc),
                    permission_issue=permission_issue,
                ),
                top_n=self.top_n,
            )
