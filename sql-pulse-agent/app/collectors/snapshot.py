"""Performance snapshot orchestrator — runs all collectors and assembles results."""

import logging
import uuid
from datetime import UTC, datetime

from app.collectors.active_requests import ActiveRequestsCollector
from app.collectors.blocking import BlockingSessionsCollector
from app.collectors.long_running import LongRunningQueriesCollector
from app.collectors.top_cpu import TopCpuQueriesCollector
from app.collectors.top_io import TopIoQueriesCollector
from app.collectors.wait_stats import WaitStatisticsCollector
from app.config import get_settings
from app.models.performance import (
    CollectorMetadata,
    PerformanceSnapshot,
    WaitDeltaStatus,
    WaitStatisticsApplicability,
    WaitStatisticsResult,
)

logger = logging.getLogger(__name__)


def collect_performance_snapshot(
    scope: str = "server",
    database: str | None = None,
    long_running_threshold_ms: int = 5000,
    top_n: int = 20,
    wait_stats_top_n: int = 25,
) -> PerformanceSnapshot:
    """Run collectors and return a unified performance snapshot.

    Args:
        scope: "server" for instance-wide, "database" for single-database.
        database: Required when scope="database".
        long_running_threshold_ms: Threshold for long-running query detection.
        top_n: Number of top queries to retrieve.
        wait_stats_top_n: Number of top wait types.
    """
    settings = get_settings()
    snapshot_id = str(uuid.uuid4())
    db_target = database if scope == "database" else None

    logger.info("Starting %s snapshot %s (db=%s)", scope, snapshot_id, db_target or "all")

    # Collectors use database-filtered queries when db_target is set
    active = ActiveRequestsCollector(database=db_target).collect()
    long_running = LongRunningQueriesCollector(
        threshold_ms=long_running_threshold_ms, database=db_target
    ).collect()
    blocking = BlockingSessionsCollector(database=db_target).collect()
    top_cpu = TopCpuQueriesCollector(top_n=top_n, database=db_target).collect()
    top_io = TopIoQueriesCollector(top_n=top_n, database=db_target).collect()

    # Wait stats are server-level only (sys.dm_os_wait_stats is instance-wide)
    if scope == "server":
        waits = WaitStatisticsCollector(top_n=wait_stats_top_n).collect()
        # Cumulative counters mean nothing on their own — enrich with the change
        # since the previous snapshot where one exists.
        try:
            from app.storage.wait_stats_service import apply_wait_delta
            waits = apply_wait_delta(
                waits, server=settings.sql_server_host, snapshot_id=snapshot_id
            )
        except Exception as exc:
            logger.warning("Could not compute wait statistics delta: %s", str(exc))
    else:
        # Explicitly indicate not applicable rather than returning null
        waits = WaitStatisticsResult(
            metadata=CollectorMetadata(
                collector_name="wait_statistics",
                server_host=settings.sql_server_host,
                database=database or settings.sql_server_database,
                collected_at=datetime.now(UTC),
                success=True,
                error_message="Wait statistics are server-level only (sys.dm_os_wait_stats). Not applicable for database scope.",
            ),
            top_n=wait_stats_top_n,
            applicability=WaitStatisticsApplicability.NOT_APPLICABLE_DATABASE_SCOPE,
            delta_status=WaitDeltaStatus.NOT_APPLICABLE,
        )

    snapshot = PerformanceSnapshot(
        snapshot_id=snapshot_id,
        collected_at=datetime.now(UTC),
        server_host=settings.sql_server_host,
        scope=scope,
        database=database,
        active_requests=active,
        long_running_queries=long_running,
        blocking_sessions=blocking,
        top_cpu_queries=top_cpu,
        top_io_queries=top_io,
        wait_statistics=waits,
    )

    logger.info("Performance snapshot %s complete", snapshot_id)
    return snapshot
