"""Pydantic models for performance collector results."""

from datetime import UTC, datetime
from enum import Enum

from pydantic import BaseModel, Field


class CollectorMetadata(BaseModel):
    """Metadata attached to every collector result."""

    collector_name: str
    server_host: str
    database: str
    collected_at: datetime = Field(default_factory=lambda: datetime.now(UTC))
    success: bool = True
    error_message: str | None = None
    permission_issue: bool = False


# --- Active Requests ---


class ActiveRequest(BaseModel):
    """A currently executing SQL request."""

    session_id: int
    request_id: int
    status: str
    command: str
    sql_text: str | None = None
    database_name: str | None = None
    wait_type: str | None = None
    wait_time_ms: int = 0
    cpu_time_ms: int = 0
    total_elapsed_time_ms: int = 0
    reads: int = 0
    writes: int = 0
    logical_reads: int = 0
    blocking_session_id: int | None = None
    open_transaction_count: int = 0
    login_name: str | None = None
    host_name: str | None = None
    program_name: str | None = None


class ActiveRequestsResult(BaseModel):
    """Result of active requests collection."""

    metadata: CollectorMetadata
    requests: list[ActiveRequest] = []


# --- Long-Running Queries ---


class LongRunningQuery(BaseModel):
    """A query running longer than the configured threshold."""

    session_id: int
    request_id: int
    sql_text: str | None = None
    database_name: str | None = None
    elapsed_time_ms: int = 0
    cpu_time_ms: int = 0
    reads: int = 0
    logical_reads: int = 0
    writes: int = 0
    wait_type: str | None = None
    wait_time_ms: int = 0
    status: str = ""
    login_name: str | None = None
    host_name: str | None = None
    program_name: str | None = None


class LongRunningQueriesResult(BaseModel):
    """Result of long-running queries collection."""

    metadata: CollectorMetadata
    threshold_ms: int
    queries: list[LongRunningQuery] = []


# --- Blocking Sessions ---


class BlockingSession(BaseModel):
    """A session involved in a blocking chain."""

    session_id: int
    blocking_session_id: int | None = None
    wait_type: str | None = None
    wait_time_ms: int = 0
    wait_resource: str | None = None
    database_name: str | None = None
    sql_text: str | None = None
    status: str = ""
    login_name: str | None = None
    host_name: str | None = None
    program_name: str | None = None
    is_head_blocker: bool = False


class BlockingSessionsResult(BaseModel):
    """Result of blocking sessions collection."""

    metadata: CollectorMetadata
    sessions: list[BlockingSession] = []
    blocking_chain_count: int = 0


# --- Top CPU Queries ---


class TopCpuQuery(BaseModel):
    """A query ranked by CPU consumption from Query Store or DMVs."""

    sql_text: str | None = None
    query_hash: str | None = None
    query_plan_hash: str | None = None
    object_name: str | None = None
    execution_count: int = 0
    total_cpu_time_ms: int = 0
    avg_cpu_time_ms: float = 0.0
    total_elapsed_time_ms: int = 0
    avg_elapsed_time_ms: float = 0.0
    total_logical_reads: int = 0
    avg_logical_reads: float = 0.0
    database_name: str | None = None


class TopCpuQueriesResult(BaseModel):
    """Result of top CPU queries collection."""

    metadata: CollectorMetadata
    top_n: int
    queries: list[TopCpuQuery] = []


# --- Top I/O Queries ---


class TopIoQuery(BaseModel):
    """A query ranked by logical reads / I/O consumption."""

    sql_text: str | None = None
    query_hash: str | None = None
    query_plan_hash: str | None = None
    object_name: str | None = None
    execution_count: int = 0
    total_logical_reads: int = 0
    avg_logical_reads: float = 0.0
    total_physical_reads: int = 0
    avg_physical_reads: float = 0.0
    total_logical_writes: int = 0
    avg_logical_writes: float = 0.0
    total_elapsed_time_ms: int = 0
    database_name: str | None = None


class TopIoQueriesResult(BaseModel):
    """Result of top I/O queries collection."""

    metadata: CollectorMetadata
    top_n: int
    queries: list[TopIoQuery] = []


# --- Wait Statistics ---


class WaitStatistic(BaseModel):
    """A single wait type statistic."""

    wait_type: str
    waiting_tasks_count: int = 0
    wait_time_ms: int = 0
    max_wait_time_ms: int = 0
    signal_wait_time_ms: int = 0
    resource_wait_time_ms: int = 0


class WaitStatisticsTotals(BaseModel):
    """Server-wide wait totals accumulated since the last SQL Server restart."""

    waiting_tasks_count: int = 0
    wait_time_ms: int = 0
    signal_wait_time_ms: int = 0
    resource_wait_time_ms: int = 0


class WaitStatisticsDelta(BaseModel):
    """Change in wait totals between two snapshots.

    Cumulative DMV counters cannot show what is happening *now*; only the
    change between two observations can. A delta is therefore reported only
    when a previous snapshot exists and the server has not restarted.
    """

    previous_collected_at: datetime
    interval_seconds: float = 0.0
    waiting_tasks_count: int = 0
    wait_time_ms: int = 0
    signal_wait_time_ms: int = 0
    resource_wait_time_ms: int = 0


class WaitDeltaStatus(str, Enum):
    """Why a wait delta is or is not available."""

    AVAILABLE = "available"
    # First observation on this server — nothing to compare against yet.
    INSUFFICIENT_PREVIOUS_DATA = "insufficient_previous_data"
    # Counters reset on restart, so spanning a restart would fabricate a delta.
    SERVER_RESTARTED = "server_restarted"
    NOT_APPLICABLE = "not_applicable"


class WaitStatisticsApplicability(str, Enum):
    """Whether wait statistics apply to the requested scope."""

    # sys.dm_os_wait_stats is instance-wide, so it applies to server scope only.
    SERVER_LEVEL = "server_level"
    NOT_APPLICABLE_DATABASE_SCOPE = "not_applicable_database_scope"


class WaitStatisticsResult(BaseModel):
    """Result of wait statistics collection."""

    metadata: CollectorMetadata
    top_n: int
    statistics: list[WaitStatistic] = []

    applicability: WaitStatisticsApplicability = WaitStatisticsApplicability.SERVER_LEVEL
    collected_at: datetime = Field(default_factory=lambda: datetime.now(UTC))
    # Counters are cumulative since this moment; a restart resets them to zero.
    sqlserver_start_time: datetime | None = None
    totals: WaitStatisticsTotals | None = None
    delta: WaitStatisticsDelta | None = None
    delta_status: WaitDeltaStatus = WaitDeltaStatus.INSUFFICIENT_PREVIOUS_DATA


# --- Performance Snapshot ---


class PerformanceSnapshot(BaseModel):
    """Complete performance snapshot combining all collectors."""

    snapshot_id: str
    collected_at: datetime = Field(default_factory=lambda: datetime.now(UTC))
    server_host: str
    scope: str = "server"  # "server" or "database"
    database: str | None = None
    active_requests: ActiveRequestsResult | None = None
    long_running_queries: LongRunningQueriesResult | None = None
    blocking_sessions: BlockingSessionsResult | None = None
    top_cpu_queries: TopCpuQueriesResult | None = None
    top_io_queries: TopIoQueriesResult | None = None
    wait_statistics: WaitStatisticsResult | None = None
