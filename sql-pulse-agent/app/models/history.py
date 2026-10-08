"""Pydantic models for historical storage."""

from datetime import UTC, datetime
from enum import Enum

from pydantic import BaseModel, Field


class BaselineStatus(str, Enum):
    """Status of a baseline calculation."""

    READY = "READY"
    INSUFFICIENT_DATA = "INSUFFICIENT_DATA"


class QueryMetricRecord(BaseModel):
    """A single historical measurement for a query."""

    id: int | None = None
    snapshot_id: str
    collected_at: datetime
    server: str
    database: str
    query_hash: str
    sql_text_preview: str | None = None
    execution_count: int = 0
    total_cpu_time_ms: int = 0
    avg_cpu_time_ms: float = 0.0
    total_elapsed_time_ms: int = 0
    avg_elapsed_time_ms: float = 0.0
    total_logical_reads: int = 0
    avg_logical_reads: float = 0.0
    total_physical_reads: int = 0
    avg_physical_reads: float = 0.0
    total_logical_writes: int = 0
    avg_logical_writes: float = 0.0


class WaitTotalsRecord(BaseModel):
    """A stored observation of instance-wide wait totals.

    Wait counters are cumulative since service start, so a single reading says
    nothing about current behaviour. Persisting each reading lets the next one
    be expressed as a delta. `sqlserver_start_time` identifies the counter
    epoch: if it changes, the server restarted and the counters reset, so the
    two readings cannot be subtracted.
    """

    id: int | None = None
    snapshot_id: str
    collected_at: datetime
    server: str
    sqlserver_start_time: datetime | None = None
    waiting_tasks_count: int = 0
    wait_time_ms: int = 0
    signal_wait_time_ms: int = 0
    resource_wait_time_ms: int = 0


class BaselineStatistic(BaseModel):
    """Aggregate statistics for a single metric."""

    sample_count: int = 0
    mean: float = 0.0
    median: float = 0.0
    minimum: float = 0.0
    maximum: float = 0.0
    standard_deviation: float = 0.0
    p50: float = 0.0
    p95: float = 0.0


class QueryBaseline(BaseModel):
    """Computed baseline for a query over a time window."""

    query_hash: str
    database: str
    server: str
    window: str  # e.g. "1h", "24h", "7d", "30d"
    sample_count: int = 0
    baseline_status: BaselineStatus = BaselineStatus.INSUFFICIENT_DATA
    avg_cpu_ms: BaselineStatistic | None = None
    avg_elapsed_ms: BaselineStatistic | None = None
    avg_logical_reads: BaselineStatistic | None = None
    avg_physical_reads: BaselineStatistic | None = None
    execution_count: BaselineStatistic | None = None


class SnapshotRecord(BaseModel):
    """Record of a stored performance snapshot."""

    snapshot_id: str
    collected_at: datetime = Field(default_factory=lambda: datetime.now(UTC))
    server: str
    scope: str
    database: str | None = None


class QueryHistoryResponse(BaseModel):
    """API response for query history."""

    query_hash: str
    database: str
    server: str
    samples: list[QueryMetricRecord] = []


class QueryBaselineResponse(BaseModel):
    """API response for query baseline."""

    query_hash: str
    database: str
    window: str
    sample_count: int = 0
    baseline_status: BaselineStatus = BaselineStatus.INSUFFICIENT_DATA
    metrics: dict[str, BaselineStatistic] = {}
