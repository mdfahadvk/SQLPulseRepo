"""SQLPulse — Read-only SQL Server Performance Analysis Agent.

FastAPI application entry point.
"""

import asyncio
import logging
from contextlib import asynccontextmanager
from datetime import datetime
from typing import AsyncGenerator

from fastapi import FastAPI, HTTPException, Query
from fastapi.responses import JSONResponse

from app.collectors.snapshot import collect_performance_snapshot
from app.config import get_settings
from app.database.connection import test_connection, validate_database_name
from app.database.discovery import discover_databases, invalidate_cache, validate_target_database
from app.detectors.engine import analyze_performance
from app.logging_config import setup_logging
from app.models.agent import (
    InvestigationAuditDetailResponse,
    InvestigationAuditListResponse,
    InvestigationRequest,
    InvestigationResponse,
)
from app.models.findings import AnalysisResult
from app.models.history import QueryBaselineResponse, QueryHistoryResponse
from app.models.performance import PerformanceSnapshot
from app.models.slow_history import QueryStoreStatus, SlowHistoryResponse, WorkloadType
from app.models.schemas import (
    ConnectionTestResponse,
    DatabaseEntry,
    DatabaseListResponse,
    HealthResponse,
)
from app.storage.baseline_service import calculate_baseline
from app.storage.history_service import (
    get_investigation_audit,
    get_repository,
    list_investigation_audits,
    persist_investigation_audit,
    persist_snapshot,
    run_retention_cleanup,
)
from app.services.slow_history_service import get_slow_history_response

logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncGenerator[None, None]:
    """Application lifespan: startup and shutdown logic."""
    setup_logging()
    settings = get_settings()
    logger.info(
        "%s v%s starting up",
        settings.app_name,
        settings.app_version,
    )
    # Initialize historical storage
    try:
        repo = get_repository()
        logger.info("Historical storage ready")
    except Exception:
        logger.warning("Historical storage unavailable — continuing without it")
    yield
    # Shutdown: run retention cleanup
    try:
        deleted = run_retention_cleanup(settings.history_retention_days)
        if deleted:
            logger.info("Retention cleanup removed %d old records", deleted)
    except Exception:
        pass
    logger.info("%s shutting down", settings.app_name)


app = FastAPI(
    title="SQLPulse",
    description=(
        "Read-only SQL Server Performance Analysis Agent. "
        "Supports server-level and database-level analysis."
    ),
    version="0.1.0",
    lifespan=lifespan,
)


@app.get("/")
async def root():
    """Root endpoint with links to available resources."""
    return {
        "application": "SQLPulse",
        "version": "0.1.0",
        "description": "Read-only SQL Server Performance Analysis Agent",
        "endpoints": {
            "health": "/health",
            "connection_test": "/database/connection-test",
            "databases": "/api/databases",
            "performance_snapshot": "/api/performance/snapshot?scope=server",
            "performance_analyze": "/api/performance/analyze?scope=server",
            "slow_history": "/api/performance/slow-history?database=...&threshold_ms=30000",
            "agent_investigate": "/api/agent/investigate",
            "agent_investigations": "/api/agent/investigations",
            "agent_investigation_detail": "/api/agent/investigations/{investigation_id}",
            "query_history": "/api/history/query/{query_hash}?database=...",
            "query_baseline": "/api/history/query/{query_hash}/baseline?database=...",
            "docs": "/docs",
        },
    }


@app.get("/health", response_model=HealthResponse)
async def health() -> HealthResponse:
    """Report whether SQLPulse itself is running."""
    settings = get_settings()
    return HealthResponse(
        status="healthy",
        application=settings.app_name,
        version=settings.app_version,
    )


@app.get("/database/connection-test", response_model=ConnectionTestResponse)
async def database_connection_test() -> ConnectionTestResponse:
    """Safely verify SQL Server connectivity without exposing credentials."""
    try:
        connected = test_connection()
        if connected:
            return ConnectionTestResponse(
                connected=True,
                message="Successfully connected to SQL Server.",
            )
        return ConnectionTestResponse(
            connected=False,
            message="Connection to SQL Server failed. Check server availability and credentials.",
        )
    except Exception:
        logger.exception("Unexpected error during connection test")
        return ConnectionTestResponse(
            connected=False,
            message="Connection test encountered an unexpected error.",
        )


@app.get("/api/databases", response_model=DatabaseListResponse)
async def list_databases() -> DatabaseListResponse:
    """Discover accessible user databases on the SQL Server instance.

    Excludes system databases (master, model, msdb, tempdb).
    Only returns ONLINE databases accessible to the monitoring account.
    """
    settings = get_settings()
    invalidate_cache()  # Always refresh on explicit call
    try:
        databases = discover_databases(use_cache=False)
    except Exception:
        logger.exception("Failed to discover databases")
        raise HTTPException(
            status_code=503,
            detail="Unable to query SQL Server for database list.",
        )
    return DatabaseListResponse(
        server=settings.sql_server,
        databases=[DatabaseEntry(name=db.name, status=db.status) for db in databases],
    )


@app.get("/api/performance/snapshot", response_model=PerformanceSnapshot)
async def performance_snapshot(
    scope: str = Query(
        default="server",
        description="Analysis scope: 'server' for instance-wide, 'database' for single database",
        pattern="^(server|database)$",
    ),
    database: str | None = Query(
        default=None,
        description="Target database name (required when scope=database)",
    ),
    long_running_threshold_ms: int = 5000,
    top_n: int = 20,
    wait_stats_top_n: int = 25,
) -> PerformanceSnapshot:
    """Collect raw performance measurements from SQL Server.

    - **scope=server**: Instance-wide snapshot (active requests, blocking, waits, top queries)
    - **scope=database**: Single database snapshot (queries scoped to that database)
    """
    if scope == "database":
        if not database:
            raise HTTPException(status_code=400, detail="database parameter is required when scope=database")
        error = validate_target_database(database)
        if error:
            raise HTTPException(status_code=400, detail=error)

    snapshot = collect_performance_snapshot(
        scope=scope,
        database=database if scope == "database" else None,
        long_running_threshold_ms=long_running_threshold_ms,
        top_n=top_n,
        wait_stats_top_n=wait_stats_top_n,
    )

    # Persist for historical tracking
    try:
        persist_snapshot(snapshot)
    except Exception:
        logger.warning("Failed to persist snapshot (non-fatal)")

    return snapshot


@app.get("/api/performance/analyze", response_model=AnalysisResult)
async def performance_analyze(
    scope: str = Query(
        default="server",
        description="Analysis scope: 'server' for instance-wide, 'database' for single database",
        pattern="^(server|database)$",
    ),
    database: str | None = Query(
        default=None,
        description="Target database name (required when scope=database)",
    ),
    long_running_threshold_ms: int = 5000,
    top_n: int = 20,
    wait_stats_top_n: int = 25,
) -> AnalysisResult:
    """Collect performance data, run deterministic detectors, return findings.

    - **scope=server**: Answers 'What is affecting this SQL Server's performance?'
      Returns findings ordered by severity with affected_databases summary.
    - **scope=database**: Answers 'Why is this database experiencing performance problems?'
      Returns deeper per-database findings.

    No LLM is involved — all detection is deterministic Python logic.
    The system remains strictly read-only.
    """
    if scope == "database":
        if not database:
            raise HTTPException(status_code=400, detail="database parameter is required when scope=database")
        error = validate_target_database(database)
        if error:
            raise HTTPException(status_code=400, detail=error)

    return analyze_performance(
        scope=scope,
        database=database if scope == "database" else None,
        long_running_threshold_ms=long_running_threshold_ms,
        top_n=top_n,
        wait_stats_top_n=wait_stats_top_n,
    )


# ─── Slow-History / Timeout Candidate Endpoint ────────────────────────────


@app.get("/api/performance/slow-history", response_model=SlowHistoryResponse)
async def slow_history(
    database: str = Query(
        ..., description="Target database name (Query Store must be enabled)"
    ),
    start_time: datetime = Query(
        ..., description="Start of time range (ISO 8601, e.g. 2026-08-24T09:00:00Z)"
    ),
    end_time: datetime = Query(
        ..., description="End of time range (ISO 8601, e.g. 2026-08-24T11:00:00Z)"
    ),
    threshold_ms: int = Query(
        default=30_000, ge=1,
        description=(
            "Slow-query monitoring threshold in milliseconds. Selects which "
            "queries are reported. This is NOT the application timeout."
        ),
    ),
    application_timeout_ms: int | None = Query(
        default=None, ge=1,
        description=(
            "The application's configured command timeout in milliseconds. "
            "Only when a query reaches this value is it a TIMEOUT_CANDIDATE. "
            "Omit to use the configured default; when neither is set, timeout "
            "classification is reported as unavailable rather than guessed."
        ),
    ),
    top_n: int = Query(
        default=20, ge=1, le=100,
        description="Maximum number of candidates to return",
    ),
    group_results: bool = Query(
        default=True,
        description="Aggregate duplicate Query Store intervals per query/plan",
    ),
    workload_type: WorkloadType | None = Query(
        default=None,
        description="Filter candidates by workload type (APPLICATION_OR_INTERACTIVE, BACKGROUND_OR_SCHEDULED, UNKNOWN)",
    ),
) -> SlowHistoryResponse:
    """Identify queries that exceeded the slow-query monitoring threshold.

    Reads from SQL Server Query Store (read-only) to find historical slow
    queries and stored procedures.

    Every returned query is a **SLOW_QUERY_CANDIDATE**.  It is additionally a
    **TIMEOUT_CANDIDATE** only when its duration reached an explicitly
    configured **application_timeout_ms** — the monitoring threshold is chosen
    by whoever is monitoring and says nothing about when the client gives up,
    so it is never reused for that decision.  Even a TIMEOUT_CANDIDATE is not a
    confirmed timeout: that requires application telemetry SQLPulse does not
    have.

    When **group_results** is True (default), duplicate Query Store intervals
    for the same query/plan are merged into a single candidate with weighted
    averages, preserved max/min values, and interval tracking counts.

    When **workload_type** is provided, only candidates matching that
    classification are returned.

    Each candidate is assigned a **rank** (1 = most urgent) based on a
    composite score of duration vs threshold, interval exceedance,
    baseline regression, CPU impact, and workload type.

    Returns `query_store_status: "UNAVAILABLE"` when Query Store is disabled
    or inaccessible for the requested database — the API does not fail.
    """
    if not validate_database_name(database):
        raise HTTPException(status_code=400, detail="Invalid database name")

    if end_time <= start_time:
        raise HTTPException(
            status_code=400, detail="end_time must be after start_time"
        )

    return get_slow_history_response(
        database=database,
        start_time=start_time,
        end_time=end_time,
        threshold_ms=threshold_ms,
        top_n=top_n,
        group_results=group_results,
        workload_type=workload_type,
        application_timeout_ms=application_timeout_ms,
    )


# ─── Historical API Endpoints ────────────────────────────────────────────


@app.get("/api/history/query/{query_hash}", response_model=QueryHistoryResponse)
async def query_history(
    query_hash: str,
    database: str = Query(..., description="Database name to scope history"),
    limit: int = Query(default=500, ge=1, le=5000, description="Max records to return"),
) -> QueryHistoryResponse:
    """Retrieve historical performance measurements for a specific query.

    Returns time-series data showing how the query has performed over time.
    """
    settings = get_settings()
    try:
        repo = get_repository()
        records = repo.get_query_history(query_hash, database, limit=limit)
    except Exception:
        logger.exception("Failed to retrieve query history")
        raise HTTPException(status_code=503, detail="Historical storage unavailable")

    return QueryHistoryResponse(
        query_hash=query_hash,
        database=database,
        server=settings.sql_server,
        samples=records,
    )


@app.get("/api/history/query/{query_hash}/baseline", response_model=QueryBaselineResponse)
async def query_baseline(
    query_hash: str,
    database: str = Query(..., description="Database name to scope baseline"),
    window: str = Query(default="7d", description="Time window: 1h, 24h, 7d, 30d"),
) -> QueryBaselineResponse:
    """Compute or retrieve the performance baseline for a query.

    The baseline represents expected performance based on historical data.
    Used by the regression detector to identify queries that have degraded.
    """
    valid_windows = {"1h", "24h", "7d", "30d"}
    if window not in valid_windows:
        raise HTTPException(
            status_code=400,
            detail=f"Invalid window '{window}'. Use one of: {sorted(valid_windows)}",
        )

    try:
        baseline = calculate_baseline(query_hash, database, window=window)
    except Exception:
        logger.exception("Failed to calculate baseline")
        raise HTTPException(status_code=503, detail="Historical storage unavailable")

    metrics = {}
    if baseline.avg_cpu_ms:
        metrics["avg_cpu_ms"] = baseline.avg_cpu_ms
    if baseline.avg_elapsed_ms:
        metrics["avg_elapsed_ms"] = baseline.avg_elapsed_ms
    if baseline.avg_logical_reads:
        metrics["avg_logical_reads"] = baseline.avg_logical_reads
    if baseline.avg_physical_reads:
        metrics["avg_physical_reads"] = baseline.avg_physical_reads
    if baseline.execution_count:
        metrics["execution_count"] = baseline.execution_count

    return QueryBaselineResponse(
        query_hash=query_hash,
        database=database,
        window=window,
        sample_count=baseline.sample_count,
        baseline_status=baseline.baseline_status,
        metrics=metrics,
    )


# ─── AI Agent Endpoint ───────────────────────────────────────────────────


@app.post("/api/agent/investigate", response_model=InvestigationResponse)
async def agent_investigate(
    request: InvestigationRequest,
) -> InvestigationResponse:
    """Ask the AI agent a natural-language performance question.

    The agent uses approved read-only SQLPulse tools to gather evidence,
    then produces a structured explanation with severity, confidence,
    and recommendations.

    The AI never executes arbitrary SQL and never modifies the database.
    Every technical claim is supported by tool output.
    """
    settings = get_settings()

    if not settings.llm_api_key.get_secret_value():
        raise HTTPException(
            status_code=503,
            detail=(
                "AI agent is not configured. "
                "Set LLM_API_KEY in your environment to enable it."
            ),
        )

    # Hard cap: the agent loop only checks its own budget between steps, so
    # allow headroom for the final summarisation call, then fail cleanly.
    hard_timeout = settings.max_agent_execution_seconds + settings.llm_request_timeout_seconds

    try:
        from app.agent.llm_provider import (
            LLMPayloadTooLargeError,
            LLMRateLimitError,
            create_provider,
        )
        from app.agent.engine import investigate

        provider = create_provider(
            provider_name=settings.llm_provider,
            api_key=settings.llm_api_key.get_secret_value(),
            model=settings.llm_model,
            base_url=settings.llm_base_url or None,
            timeout=float(settings.llm_request_timeout_seconds),
        )

        result = await asyncio.wait_for(
            investigate(question=request.question, provider=provider),
            timeout=hard_timeout,
        )

        # Persist audit trail (non-fatal on failure)
        try:
            persist_investigation_audit(
                response=result,
                tool_calls=result.tool_calls_detail,
                server=settings.sql_server,
            )
        except Exception:
            logger.warning("Failed to persist investigation audit (non-fatal)")

        return result

    except asyncio.TimeoutError:
        logger.warning("AI investigation timed out after %ds", hard_timeout)
        raise HTTPException(
            status_code=504,
            detail=(
                "The investigation timed out before it could complete. "
                "Try a narrower question or a shorter time range."
            ),
        )
    except ImportError as exc:
        logger.error("AI agent dependency missing: %s", str(exc))
        raise HTTPException(
            status_code=503,
            detail="AI agent dependency not installed. Run: pip install openai",
        )
    except LLMPayloadTooLargeError as exc:
        logger.warning("AI investigation exceeded the LLM token budget: %s", str(exc))
        raise HTTPException(
            status_code=429,
            detail=(
                "The AI provider's token limit was exceeded. Try a narrower "
                "question, such as a single database or a shorter time range."
            ),
        )
    except LLMRateLimitError as exc:
        logger.warning("AI provider rate limit hit: %s", str(exc))
        raise HTTPException(
            status_code=429,
            detail=(
                "The AI provider is rate limiting requests. "
                "Please wait a moment and try again."
            ),
        )
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc))
    except Exception:
        logger.exception("AI investigation failed")
        raise HTTPException(
            status_code=500,
            detail="AI investigation encountered an error. See server logs.",
        )


# ─── Investigation Audit Trail Endpoints ──────────────────────────────────


@app.get("/api/agent/investigations", response_model=InvestigationAuditListResponse)
async def list_investigations(
    limit: int = Query(
        default=50, ge=1, le=500,
        description="Maximum number of audit records to return",
    ),
) -> InvestigationAuditListResponse:
    """List recent AI investigation audit records, newest first.

    Provides a trail of what SQLPulse investigated, which tools it used,
    what evidence it referenced, and what conclusion it reached.
    No credentials, API keys, or connection strings are ever stored.
    """
    try:
        audits = list_investigation_audits(limit=limit)
        return InvestigationAuditListResponse(
            total=len(audits),
            investigations=audits,
        )
    except Exception:
        logger.exception("Failed to list investigation audits")
        raise HTTPException(
            status_code=500,
            detail="Unable to retrieve investigation audit records.",
        )


@app.get(
    "/api/agent/investigations/{investigation_id}",
    response_model=InvestigationAuditDetailResponse,
)
async def get_investigation(investigation_id: str) -> InvestigationAuditDetailResponse:
    """Retrieve a single AI investigation audit by ID.

    Returns the sanitized question, tools called, findings referenced,
    severity, confidence, answer, and confirmation that no remediation occurred.
    """
    try:
        audit = get_investigation_audit(investigation_id)
        if audit is None:
            return InvestigationAuditDetailResponse(found=False, investigation=None)
        return InvestigationAuditDetailResponse(found=True, investigation=audit)
    except Exception:
        logger.exception("Failed to retrieve investigation audit %s", investigation_id)
        raise HTTPException(
            status_code=500,
            detail="Unable to retrieve investigation audit record.",
        )


@app.exception_handler(Exception)
async def global_exception_handler(request, exc: Exception) -> JSONResponse:
    """Catch-all exception handler — never expose internal details."""
    logger.exception("Unhandled exception on %s %s", request.method, request.url.path)
    return JSONResponse(
        status_code=500,
        content={"detail": "An internal error occurred. Please try again later."},
    )
