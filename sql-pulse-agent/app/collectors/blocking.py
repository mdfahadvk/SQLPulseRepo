"""Collector for blocking sessions and blocking chains."""

import logging

from app.collectors.base import BaseCollector
from app.database.performance_queries import BLOCKING_SESSIONS, BLOCKING_SESSIONS_BY_DATABASE
from app.models.performance import BlockingSession, BlockingSessionsResult

logger = logging.getLogger(__name__)


class BlockingSessionsCollector(BaseCollector):
    """Collects sessions involved in blocking chains."""

    def __init__(self, database: str | None = None) -> None:
        super().__init__(database=database)

    @property
    def name(self) -> str:
        return "blocking_sessions"

    def collect(self) -> BlockingSessionsResult:
        """Collect blocking sessions.

        Required permission: VIEW SERVER STATE
        """
        try:
            if self._database:
                rows = self._execute_query(BLOCKING_SESSIONS_BY_DATABASE, params=(self._database,))
            else:
                rows = self._execute_query(BLOCKING_SESSIONS)
            sessions = [
                BlockingSession(**{**row, "is_head_blocker": bool(row.get("is_head_blocker", 0))})
                for row in rows
            ]
            # Count distinct head blockers as chain roots
            chain_count = sum(1 for s in sessions if s.is_head_blocker)
            return BlockingSessionsResult(
                metadata=self._build_metadata(),
                sessions=sessions,
                blocking_chain_count=chain_count,
            )
        except Exception as exc:
            permission_issue = self._is_permission_error(exc)
            logger.error(
                "BlockingSessionsCollector failed: %s (permission_issue=%s)",
                str(exc),
                permission_issue,
            )
            return BlockingSessionsResult(
                metadata=self._build_metadata(
                    success=False,
                    error_message=str(exc),
                    permission_issue=permission_issue,
                ),
            )
