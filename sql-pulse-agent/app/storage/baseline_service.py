"""Baseline calculation service.

Computes historical baselines for query metrics over configurable time windows.
"""

import logging
import math
from datetime import UTC, datetime, timedelta

from app.models.history import (
    BaselineStatistic,
    BaselineStatus,
    QueryBaseline,
    QueryMetricRecord,
)
from app.storage.history_service import get_repository

logger = logging.getLogger(__name__)

# Configurable via environment (defaults)
DEFAULT_MIN_SAMPLES = 5

# Supported windows
WINDOW_HOURS = {
    "1h": 1,
    "24h": 24,
    "7d": 168,
    "30d": 720,
}


def _compute_statistic(values: list[float]) -> BaselineStatistic:
    """Compute aggregate statistics for a list of values."""
    if not values:
        return BaselineStatistic()

    n = len(values)
    sorted_vals = sorted(values)
    mean = sum(values) / n
    median = sorted_vals[n // 2] if n % 2 else (sorted_vals[n // 2 - 1] + sorted_vals[n // 2]) / 2
    minimum = sorted_vals[0]
    maximum = sorted_vals[-1]

    # Standard deviation
    if n > 1:
        variance = sum((v - mean) ** 2 for v in values) / (n - 1)
        std_dev = math.sqrt(variance)
    else:
        std_dev = 0.0

    # Percentiles
    p50 = median
    p95_idx = min(int(math.ceil(0.95 * n)) - 1, n - 1)
    p95 = sorted_vals[p95_idx]

    return BaselineStatistic(
        sample_count=n,
        mean=round(mean, 2),
        median=round(median, 2),
        minimum=round(minimum, 2),
        maximum=round(maximum, 2),
        standard_deviation=round(std_dev, 2),
        p50=round(p50, 2),
        p95=round(p95, 2),
    )


def calculate_baseline(
    query_hash: str,
    database: str,
    window: str = "7d",
    min_samples: int = DEFAULT_MIN_SAMPLES,
) -> QueryBaseline:
    """Calculate baseline for a query over the specified time window.

    Args:
        query_hash: The query identity hash.
        database: Target database name.
        window: Time window string (1h, 24h, 7d, 30d).
        min_samples: Minimum samples before baseline is READY.

    Returns:
        QueryBaseline with metrics or INSUFFICIENT_DATA status.
    """
    hours = WINDOW_HOURS.get(window)
    if hours is None:
        raise ValueError(f"Unsupported window: {window}. Use one of: {list(WINDOW_HOURS.keys())}")

    since = datetime.now(UTC) - timedelta(hours=hours)
    repo = get_repository()
    records = repo.get_query_metrics_for_baseline(query_hash, database, since)

    server = records[0].server if records else ""

    if len(records) < min_samples:
        return QueryBaseline(
            query_hash=query_hash,
            database=database,
            server=server,
            window=window,
            sample_count=len(records),
            baseline_status=BaselineStatus.INSUFFICIENT_DATA,
        )

    # Compute statistics for each metric
    avg_cpu_values = [r.avg_cpu_time_ms for r in records]
    avg_elapsed_values = [r.avg_elapsed_time_ms for r in records]
    avg_reads_values = [r.avg_logical_reads for r in records]
    avg_phys_values = [r.avg_physical_reads for r in records]
    exec_count_values = [float(r.execution_count) for r in records]

    return QueryBaseline(
        query_hash=query_hash,
        database=database,
        server=server,
        window=window,
        sample_count=len(records),
        baseline_status=BaselineStatus.READY,
        avg_cpu_ms=_compute_statistic(avg_cpu_values),
        avg_elapsed_ms=_compute_statistic(avg_elapsed_values),
        avg_logical_reads=_compute_statistic(avg_reads_values),
        avg_physical_reads=_compute_statistic(avg_phys_values),
        execution_count=_compute_statistic(exec_count_values),
    )


def get_baseline_for_regression(
    query_hash: str,
    database: str,
    window: str = "7d",
    min_samples: int = DEFAULT_MIN_SAMPLES,
) -> QueryBaseline | None:
    """Get baseline if sufficient data exists, else None."""
    try:
        baseline = calculate_baseline(query_hash, database, window, min_samples)
        if baseline.baseline_status == BaselineStatus.READY:
            return baseline
        return None
    except Exception:
        logger.debug("Baseline unavailable for %s/%s", query_hash, database)
        return None
