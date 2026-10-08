"""Collector for currently active SQL Server requests."""

import logging
from typing import Any

from app.collectors.base import BaseCollector
from app.database.performance_queries import ACTIVE_REQUESTS, ACTIVE_REQUESTS_BY_DATABASE
from app.models.performance import ActiveRequest, ActiveRequestsResult

logger = logging.getLogger(__name__)


class ActiveRequestsCollector(BaseCollector):
    """Collects currently executing requests from sys.dm_exec_requests."""

    def __init__(self, database: str | None = None) -> None:
        super().__init__(database=database)

    @property
    def name(self) -> str:
        return "active_requests"

    def collect(self) -> ActiveRequestsResult:
        """Collect active requests.

        Required permission: VIEW SERVER STATE
        """
        try:
            if self._database:
                rows = self._execute_query(ACTIVE_REQUESTS_BY_DATABASE, params=(self._database,))
            else:
                rows = self._execute_query(ACTIVE_REQUESTS)
            requests = [ActiveRequest(**row) for row in rows]
            return ActiveRequestsResult(
                metadata=self._build_metadata(),
                requests=requests,
            )
        except Exception as exc:
            permission_issue = self._is_permission_error(exc)
            logger.error(
                "ActiveRequestsCollector failed: %s (permission_issue=%s)",
                str(exc),
                permission_issue,
            )
            return ActiveRequestsResult(
                metadata=self._build_metadata(
                    success=False,
                    error_message=str(exc),
                    permission_issue=permission_issue,
                ),
            )
