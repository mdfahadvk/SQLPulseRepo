"""Wait-statistics delta calculation against the previous stored observation.

``sys.dm_os_wait_stats`` is cumulative since the last service restart. A single
reading therefore describes the server's entire uptime, not its current state,
and cannot support a diagnosis on its own. Only the *change* between two
readings says anything about the recent interval.

This module deliberately refuses to produce a delta when one would be
misleading:

  * no previous observation exists (first snapshot on this server)
  * the counter epoch changed, meaning SQL Server restarted and reset to zero
  * counters went backwards for any other reason

In each case the reason is reported instead of a fabricated number.
"""

from __future__ import annotations

import logging

from app.models.history import WaitTotalsRecord
from app.models.performance import (
    WaitDeltaStatus,
    WaitStatisticsDelta,
    WaitStatisticsResult,
)

logger = logging.getLogger(__name__)


def compute_delta(
    current: WaitStatisticsResult,
    previous: WaitTotalsRecord | None,
) -> tuple[WaitStatisticsDelta | None, WaitDeltaStatus]:
    """Return the delta between *current* and *previous*, or why there is none."""
    if current.totals is None or previous is None:
        return None, WaitDeltaStatus.INSUFFICIENT_PREVIOUS_DATA

    # A restart zeroes the counters, so subtracting across one would report a
    # large negative or a meaningless positive value.
    if (
        current.sqlserver_start_time is not None
        and previous.sqlserver_start_time is not None
        and current.sqlserver_start_time != previous.sqlserver_start_time
    ):
        logger.info(
            "Wait delta skipped: SQL Server restarted (%s -> %s)",
            previous.sqlserver_start_time,
            current.sqlserver_start_time,
        )
        return None, WaitDeltaStatus.SERVER_RESTARTED

    wait_delta = current.totals.wait_time_ms - previous.wait_time_ms
    tasks_delta = current.totals.waiting_tasks_count - previous.waiting_tasks_count
    signal_delta = current.totals.signal_wait_time_ms - previous.signal_wait_time_ms
    resource_delta = current.totals.resource_wait_time_ms - previous.resource_wait_time_ms

    # Cumulative counters must never decrease. If they did, the epoch changed
    # without us seeing it (e.g. DBCC SQLPERF clear), so the comparison is void.
    if min(wait_delta, tasks_delta, signal_delta, resource_delta) < 0:
        logger.info("Wait delta skipped: counters decreased since last snapshot")
        return None, WaitDeltaStatus.SERVER_RESTARTED

    interval = (current.collected_at - previous.collected_at).total_seconds()

    return (
        WaitStatisticsDelta(
            previous_collected_at=previous.collected_at,
            interval_seconds=round(interval, 3),
            waiting_tasks_count=tasks_delta,
            wait_time_ms=wait_delta,
            signal_wait_time_ms=signal_delta,
            resource_wait_time_ms=resource_delta,
        ),
        WaitDeltaStatus.AVAILABLE,
    )


def apply_wait_delta(
    result: WaitStatisticsResult,
    server: str,
    snapshot_id: str,
) -> WaitStatisticsResult:
    """Populate *result* with a delta and persist the current reading.

    Storage problems must never break a snapshot, so any failure leaves the
    result reporting insufficient previous data rather than raising.
    """
    if result.totals is None or not result.metadata.success:
        return result

    try:
        from app.storage.history_service import get_repository

        repo = get_repository()
        previous = repo.get_latest_wait_totals(server, before=result.collected_at)
        delta, status = compute_delta(result, previous)
        result.delta = delta
        result.delta_status = status

        repo.store_wait_totals(WaitTotalsRecord(
            snapshot_id=snapshot_id,
            collected_at=result.collected_at,
            server=server,
            sqlserver_start_time=result.sqlserver_start_time,
            waiting_tasks_count=result.totals.waiting_tasks_count,
            wait_time_ms=result.totals.wait_time_ms,
            signal_wait_time_ms=result.totals.signal_wait_time_ms,
            resource_wait_time_ms=result.totals.resource_wait_time_ms,
        ))
    except Exception as exc:
        logger.warning("Wait delta unavailable (storage error): %s", str(exc))
        result.delta = None
        result.delta_status = WaitDeltaStatus.INSUFFICIENT_PREVIOUS_DATA

    return result
