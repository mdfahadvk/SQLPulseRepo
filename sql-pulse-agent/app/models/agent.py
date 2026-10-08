"""Pydantic models for the AI investigation agent."""

from datetime import UTC, datetime

from pydantic import BaseModel, Field


class InvestigationRequest(BaseModel):
    """Incoming request to the AI investigation endpoint."""

    question: str = Field(
        ..., min_length=3, max_length=2000,
        description="Natural-language performance question",
    )


class ToolCall(BaseModel):
    """Record of a single tool invocation during investigation."""

    tool_name: str
    arguments: dict = {}
    arguments_sanitized: dict = {}
    result_summary: dict = {}
    success: bool = True
    error: str | None = None
    duration_seconds: float = 0.0


class InvestigationResponse(BaseModel):
    """Structured response from the AI investigation agent."""

    investigation_id: str
    question: str
    answer: str
    severity: str = "unknown"
    confidence: str = "low"
    affected_database: str | None = None
    affected_query: str | None = None
    evidence: list[str] = []
    historical_comparison: str | None = None
    likely_cause: str | None = None
    what_is_known: list[str] = []
    what_is_uncertain: list[str] = []
    recommended_investigation: list[str] = []
    human_review_required: bool = True
    agent_action: str = "No database remediation was performed. Observation and recommendation only."
    tools_used: list[str] = []
    tool_calls_detail: list[ToolCall] = Field(default_factory=list, exclude=True)
    tool_call_count: int = 0
    duration_seconds: float = 0.0
    timestamp: datetime = Field(default_factory=lambda: datetime.now(UTC))


# ─── Audit Trail Models ──────────────────────────────────────────────────


class AuditToolCall(BaseModel):
    """Sanitized record of a single tool call for the audit trail."""

    tool_name: str
    arguments_sanitized: dict = {}
    result_summary: dict = {}
    success: bool = True
    error: str | None = None
    duration_seconds: float = 0.0


class InvestigationAuditRecord(BaseModel):
    """Persisted audit record for an AI investigation."""

    investigation_id: str
    created_at: datetime = Field(default_factory=lambda: datetime.now(UTC))
    question_sanitized: str
    server: str
    database: str | None = None
    time_range_start: str | None = None
    time_range_end: str | None = None
    tools_called: list[AuditToolCall] = []
    finding_references: list[str] = []
    severity: str = "unknown"
    confidence: str = "low"
    answer: str = ""
    human_review_required: bool = True
    remediation_performed: bool = False


class InvestigationAuditListResponse(BaseModel):
    """Response for GET /api/agent/investigations."""

    total: int = 0
    investigations: list[InvestigationAuditRecord] = []


class InvestigationAuditDetailResponse(BaseModel):
    """Response for GET /api/agent/investigations/{investigation_id}."""

    found: bool = True
    investigation: InvestigationAuditRecord | None = None
