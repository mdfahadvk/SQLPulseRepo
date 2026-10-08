"""Pydantic models for performance detection findings."""

from datetime import UTC, datetime
from enum import Enum

from pydantic import BaseModel, Field


class Severity(str, Enum):
    """Finding severity levels."""

    CRITICAL = "critical"
    HIGH = "high"
    MEDIUM = "medium"
    LOW = "low"


class Confidence(str, Enum):
    """Confidence level of a finding."""

    HIGH = "high"
    MEDIUM = "medium"
    LOW = "low"


class FindingCategory(str, Enum):
    """Categories of performance findings."""

    LONG_RUNNING_QUERY = "long_running_query"
    BLOCKING = "blocking"
    HIGH_CPU = "high_cpu"
    HIGH_IO = "high_io"
    QUERY_REGRESSION = "query_regression"
    # Exceeded the slow-query monitoring threshold only.
    SLOW_QUERY_CANDIDATE = "slow_query_candidate"
    # Additionally exceeded an explicitly configured application timeout.
    TIMEOUT_CANDIDATE = "timeout_candidate"


class OverallStatus(str, Enum):
    """Overall health status of the server or database."""

    HEALTHY = "HEALTHY"
    WARNING = "WARNING"
    DEGRADED = "DEGRADED"
    CRITICAL = "CRITICAL"
    UNKNOWN = "UNKNOWN"


class RecommendedDrilldown(BaseModel):
    """Machine-readable drilldown suggestion for future AI agent use."""

    scope: str  # "database"
    database: str


class FindingSignal(str, Enum):
    """An individual evidence signal contributing to a finding.

    A single query can trigger several detectors (e.g. it is both CPU- and
    IO-expensive).  Signals let one finding carry every observation instead of
    emitting a near-duplicate finding per detector.
    """

    HIGH_CPU = "high_cpu"
    HIGH_IO = "high_io"


class QueryIdentity(BaseModel):
    """Stable identifiers for the query a finding refers to.

    Every field is optional: plan-cache metadata is frequently incomplete and
    SQLPulse never guesses.  Unresolved identifiers stay null.
    """

    query_hash: str | None = None
    query_plan_hash: str | None = None
    object_name: str | None = None


class PerformanceFinding(BaseModel):
    """A single deterministic performance finding."""

    finding_id: str
    category: FindingCategory
    server: str
    # None when plan-cache metadata cannot resolve the database. SQLPulse does
    # not guess, and never emits the literal string "unknown".
    database: str | None = None
    severity: Severity
    confidence: Confidence
    first_observed: datetime = Field(default_factory=lambda: datetime.now(UTC))
    last_observed: datetime = Field(default_factory=lambda: datetime.now(UTC))
    summary: str
    evidence: list[str] = []
    likely_cause: str
    recommendation: str
    validation_steps: list[str] = []
    human_review_required: bool = False
    recommended_drilldown: RecommendedDrilldown | None = None

    # All signals that contributed to this finding. A finding correlated across
    # detectors carries more than one.
    signals: list[FindingSignal] = []
    query_identity: QueryIdentity | None = None
    # Human-readable, deterministic explanation of why this severity was chosen.
    severity_basis: str | None = None


class ExcludedQuery(BaseModel):
    """A query deliberately kept out of the findings, retained for debugging.

    Excluded queries are never user-facing findings, but the reason is surfaced
    so operators can confirm the filter is not hiding real workload.
    """

    reason: str
    query_hash: str | None = None
    database: str | None = None
    sql_preview: str | None = None


class AffectedDatabase(BaseModel):
    """Summary of findings affecting a specific database."""

    database: str
    highest_severity: str
    finding_count: int


class AnalysisSummary(BaseModel):
    """Flat, machine-readable digest of an analysis.

    Exists so the UI and the AI agent can read the headline facts without
    walking the full findings list.
    """

    current_health_status: OverallStatus = OverallStatus.HEALTHY
    health_finding_count: int = 0
    optimization_opportunity_count: int = 0
    highest_severity: str | None = None
    affected_database_count: int = 0
    total_findings: int = 0
    critical_count: int = 0
    high_count: int = 0
    medium_count: int = 0
    low_count: int = 0
    excluded_query_count: int = 0


class AnalysisResult(BaseModel):
    """Full analysis result combining all detector outputs."""

    analysis_id: str
    analyzed_at: datetime = Field(default_factory=lambda: datetime.now(UTC))
    server: str
    scope: str  # "server" or "database"
    database: str | None = None
    overall_status: OverallStatus = OverallStatus.UNKNOWN
    findings: list[PerformanceFinding] = []
    affected_databases: list[AffectedDatabase] = []
    total_findings: int = 0
    critical_count: int = 0
    high_count: int = 0
    medium_count: int = 0
    low_count: int = 0

    # Health vs Optimization separation
    current_health: "HealthSection | None" = None
    optimization_opportunities: list[PerformanceFinding] = []
    historical_analysis_status: str = "available"  # "available" or "unavailable"

    summary: "AnalysisSummary | None" = None
    excluded_queries: list[ExcludedQuery] = []


class HealthSection(BaseModel):
    """Current health status reflecting active problems only.

    `status` reflects ACTIVE problems only. A server with no active incident is
    HEALTHY even when optimization opportunities exist.
    """

    status: OverallStatus = OverallStatus.HEALTHY
    findings: list[PerformanceFinding] = []


# Resolve forward references for AnalysisResult
AnalysisResult.model_rebuild()
