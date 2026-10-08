"""Pydantic models for Query Store slow-query history and timeout candidates."""

from datetime import UTC, datetime
from enum import Enum

from pydantic import BaseModel, Field


class WorkloadType(str, Enum):
    """Workload classification for a query or procedure."""

    APPLICATION_OR_INTERACTIVE = "APPLICATION_OR_INTERACTIVE"
    BACKGROUND_OR_SCHEDULED = "BACKGROUND_OR_SCHEDULED"
    UNKNOWN = "UNKNOWN"


class EvidenceStrength(str, Enum):
    """How much a single piece of workload evidence can be relied upon.

    Only STRONG evidence may decide a classification. WEAK evidence is still
    reported, because an operator reviewing an UNKNOWN result needs to see what
    was considered and why it was not enough.
    """

    STRONG = "strong"
    WEAK = "weak"


class WorkloadEvidence(BaseModel):
    """One observation supporting (or failing to support) a workload type."""

    strength: EvidenceStrength
    source: str
    detail: str
    supports: WorkloadType


class CandidateClassification(str, Enum):
    """What a slow observation actually demonstrates.

    SLOW_QUERY_CANDIDATE
        Exceeded the monitoring threshold. Says the query is worth attention.

    TIMEOUT_CANDIDATE
        Exceeded an *explicitly configured* application timeout, so the client
        could plausibly have abandoned the request. This is a strictly stronger
        claim and requires knowing the application's timeout.
    """

    SLOW_QUERY_CANDIDATE = "SLOW_QUERY_CANDIDATE"
    TIMEOUT_CANDIDATE = "TIMEOUT_CANDIDATE"


class QueryStoreStatus(str, Enum):
    """Availability status of Query Store for a database."""

    AVAILABLE = "AVAILABLE"
    AVAILABLE_READ_ONLY = "AVAILABLE_READ_ONLY"
    DISABLED = "DISABLED"
    PERMISSION_DENIED = "PERMISSION_DENIED"
    QUERY_FAILED = "QUERY_FAILED"

    @property
    def is_accessible(self) -> bool:
        """Return True if Query Store data can be read."""
        return self in (QueryStoreStatus.AVAILABLE, QueryStoreStatus.AVAILABLE_READ_ONLY)


class SlowQueryRecord(BaseModel):
    """A single slow query from Query Store history."""

    query_id: int | None = None
    query_sql_text: str | None = None
    query_hash: str | None = None
    object_id: int | None = None
    procedure_name: str | None = None
    plan_id: int | None = None
    execution_count: int = 0
    avg_duration_ms: float = 0.0
    max_duration_ms: float = 0.0
    min_duration_ms: float = 0.0
    avg_cpu_time_ms: float = 0.0
    max_cpu_time_ms: float = 0.0
    avg_logical_reads: float = 0.0
    max_logical_reads: float = 0.0
    avg_physical_reads: float = 0.0
    first_execution_time: datetime | None = None
    last_execution_time: datetime | None = None
    interval_start_time: datetime | None = None
    intervals_observed: int = 1
    intervals_exceeding_threshold: int = 0

    # Session/connection metadata. Query Store does not record these, so they
    # are normally None; they are populated only when a caller has observed the
    # query on a live session. They are the strongest available workload
    # evidence, which is why the model carries them even when usually empty.
    program_name: str | None = None
    host_name: str | None = None
    is_scheduled_job: bool | None = None


class BaselineComparison(BaseModel):
    """Comparison of a timeout candidate against its SQLPulse historical baseline."""

    baseline_status: str = "INSUFFICIENT_DATA"
    sample_count: int = 0
    historical_mean_ms: float = 0.0
    historical_median_ms: float = 0.0
    historical_p95_ms: float = 0.0
    standard_deviation_ms: float = 0.0
    regression_multiplier_vs_mean: float = 0.0
    regression_multiplier_vs_p95: float = 0.0


class TimeoutCandidate(BaseModel):
    """A query/procedure that exceeded the slow-query monitoring threshold.

    Exceeding the monitoring threshold makes this a SLOW_QUERY_CANDIDATE. It
    becomes a TIMEOUT_CANDIDATE only when it also exceeds an explicitly
    configured application timeout — SQLPulse cannot observe client timeouts,
    so without that configuration the timeout question stays unanswered rather
    than being answered by assumption.

    Even a TIMEOUT_CANDIDATE is not a confirmed timeout: proving the client
    actually gave up requires application telemetry SQLPulse does not have.
    """

    label: str = "slow_query_candidate"
    classification: CandidateClassification = (
        CandidateClassification.SLOW_QUERY_CANDIDATE
    )
    slow_query_candidate: bool = True
    timeout_candidate: bool = False
    # The thresholds this candidate was judged against, so a reader never has
    # to guess which number produced the classification.
    slow_threshold_ms: float = 0.0
    application_timeout_ms: float | None = None
    classification_reason: str = ""
    query_id: int | None = None
    query_hash: str | None = None
    object_id: int | None = None
    procedure_name: str | None = None
    plan_id: int | None = None
    sql_text_preview: str | None = None
    execution_count: int = 0
    total_execution_count: int = 0
    intervals_observed: int = 1
    intervals_exceeding_threshold: int = 0
    avg_duration_ms: float = 0.0
    max_duration_ms: float = 0.0
    min_duration_ms: float = 0.0
    avg_cpu_ms: float = 0.0
    max_cpu_ms: float = 0.0
    avg_logical_reads: float = 0.0
    max_logical_reads: float = 0.0
    first_execution_time: datetime | None = None
    last_execution_time: datetime | None = None
    baseline_comparison: BaselineComparison | None = None
    confidence: str = "medium"
    severity: str = "high"
    workload_type: WorkloadType = WorkloadType.UNKNOWN
    classification_evidence: list[str] = []
    workload_evidence: list[WorkloadEvidence] = []
    rank: int | None = None


class SlowHistoryResponse(BaseModel):
    """API response for /api/performance/slow-history."""

    server: str
    database: str
    start_time: datetime
    end_time: datetime
    threshold_ms: int
    # Repeated explicitly so a consumer can tell which number drove which
    # classification without inspecting individual candidates.
    slow_threshold_ms: int = 0
    application_timeout_ms: int | None = None
    timeout_classification_available: bool = False
    query_store_status: QueryStoreStatus = QueryStoreStatus.AVAILABLE
    top_n: int = 20
    total_candidates: int = 0
    slow_query_candidate_count: int = 0
    timeout_candidate_count: int = 0
    candidates: list[TimeoutCandidate] = []
    message: str | None = None
