"""Collector for SQL Server wait statistics."""

import logging
from datetime import UTC, datetime

from app.collectors.base import BaseCollector
from app.database.performance_queries import WAIT_STATISTICS, WAIT_STATISTICS_SUMMARY
from app.models.performance import (
    WaitDeltaStatus,
    WaitStatistic,
    WaitStatisticsApplicability,
    WaitStatisticsResult,
    WaitStatisticsTotals,
)

logger = logging.getLogger(__name__)

DEFAULT_TOP_N = 25


class WaitStatisticsCollector(BaseCollector):
    """Collects top wait statistics from sys.dm_os_wait_stats."""

    def __init__(self, top_n: int = DEFAULT_TOP_N, database: str | None = None) -> None:
        super().__init__(database=database)
        self.top_n = top_n

    @property
    def name(self) -> str:
        return "wait_statistics"

    def collect(self) -> WaitStatisticsResult:
        """Collect wait statistics.

        Required permission: VIEW SERVER STATE
        """
        try:
            query = WAIT_STATISTICS.format(top_n=int(self.top_n))
            rows = self._execute_query(query)
            statistics = [WaitStatistic(**row) for row in rows]

            totals, start_time = self._collect_summary()

            return WaitStatisticsResult(
                metadata=self._build_metadata(),
                top_n=self.top_n,
                statistics=statistics,
                applicability=WaitStatisticsApplicability.SERVER_LEVEL,
                collected_at=datetime.now(UTC),
                sqlserver_start_time=start_time,
                totals=totals,
                # A delta needs a previous observation, which only the storage
                # layer can supply; it is filled in after collection.
                delta_status=WaitDeltaStatus.INSUFFICIENT_PREVIOUS_DATA,
            )
        except Exception as exc:
            permission_issue = self._is_permission_error(exc)
            logger.error(
                "WaitStatisticsCollector failed: %s (permission_issue=%s)",
                str(exc),
                permission_issue,
            )
            return WaitStatisticsResult(
                metadata=self._build_metadata(
                    success=False,
                    error_message=str(exc),
                    permission_issue=permission_issue,
                ),
                top_n=self.top_n,
            )

    def _collect_summary(self) -> tuple[WaitStatisticsTotals | None, datetime | None]:
        """Fetch instance-wide totals and the counter epoch.

        Failure here must not lose the per-wait-type detail already collected,
        so the error is logged and the totals are reported as absent.
        """
        try:
            rows = self._execute_query(WAIT_STATISTICS_SUMMARY)
        except Exception as exc:
            logger.warning("Wait statistics summary unavailable: %s", str(exc))
            return None, None

        if not rows:
            return None, None

        row = rows[0]
        totals = WaitStatisticsTotals(
            waiting_tasks_count=row.get("waiting_tasks_count") or 0,
            wait_time_ms=row.get("wait_time_ms") or 0,
            signal_wait_time_ms=row.get("signal_wait_time_ms") or 0,
            resource_wait_time_ms=row.get("resource_wait_time_ms") or 0,
        )
        return totals, row.get("sqlserver_start_time")
