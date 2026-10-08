"""Approved read-only tools exposed to the AI agent.

Each tool wraps an existing SQLPulse collector / service.  The AI chooses
which tools to call — it never generates SQL directly.

Every tool result is sanitised before being forwarded to the LLM.
"""

from __future__ import annotations

import json
import logging
import re
from datetime import datetime, timedelta

from app.agent.llm_provider import ToolDefinition
from app.agent.sanitizer import sanitize_for_llm

logger = logging.getLogger(__name__)

# ─── Tool definitions (OpenAI function-calling schema) ────────────────────


TOOL_DEFINITIONS: list[ToolDefinition] = [
    ToolDefinition(
        name="get_server_health",
        description=(
            "Collect a server-wide performance snapshot and run all detectors. "
            "Returns overall health status, findings ordered by severity, "
            "affected databases, and recommended drilldowns. "
            "Use as the FIRST tool for server-wide questions or when no specific database is mentioned."
        ),
        parameters={"type": "object", "properties": {}, "required": []},
    ),
    ToolDefinition(
        name="get_database_health",
        description=(
            "Collect performance data scoped to a specific database and run detectors. "
            "Returns health status and findings for that database only. "
            "Use as the FIRST tool when a specific database is mentioned in a current-performance question."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Target database name",
                },
            },
            "required": ["database"],
        },
    ),
    ToolDefinition(
        name="get_active_requests",
        description=(
            "List currently executing SQL requests on the server, "
            "optionally filtered to a specific database. "
            "Use AFTER get_database_health/get_server_health to see what is running right now."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Optional database filter",
                },
            },
            "required": [],
        },
    ),
    ToolDefinition(
        name="get_blocking",
        description=(
            "Detect blocking chains. Returns sessions that are blocking "
            "other sessions, with head-blocker identification. "
            "Use when current-performance evidence suggests contention or waits."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Optional database filter",
                },
            },
            "required": [],
        },
    ),
    ToolDefinition(
        name="get_long_running_queries",
        description=(
            "Find queries running longer than the threshold (default 5 seconds). "
            "Returns elapsed time, CPU, reads, and wait info."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Optional database filter",
                },
            },
            "required": [],
        },
    ),
    ToolDefinition(
        name="get_top_cpu_queries",
        description=(
            "Return top queries ranked by CPU consumption from plan cache."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Optional database filter",
                },
            },
            "required": [],
        },
    ),
    ToolDefinition(
        name="get_top_io_queries",
        description=(
            "Return top queries ranked by logical reads (I/O consumption) from plan cache."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Optional database filter",
                },
            },
            "required": [],
        },
    ),
    ToolDefinition(
        name="get_query_history",
        description=(
            "Retrieve historical performance measurements for a specific query "
            "identified by query_hash. Shows how the query performed over time."
        ),
        parameters={
            "type": "object",
            "properties": {
                "query_hash": {
                    "type": "string",
                    "description": "Query hash identifier",
                },
                "database": {
                    "type": "string",
                    "description": "Database name",
                },
            },
            "required": ["query_hash", "database"],
        },
    ),
    ToolDefinition(
        name="get_query_baseline",
        description=(
            "Compute the performance baseline for a query over a time window. "
            "Returns historical mean, median, P95, and standard deviation."
        ),
        parameters={
            "type": "object",
            "properties": {
                "query_hash": {
                    "type": "string",
                    "description": "Query hash identifier",
                },
                "database": {
                    "type": "string",
                    "description": "Database name",
                },
            },
            "required": ["query_hash", "database"],
        },
    ),
    ToolDefinition(
        name="get_query_regressions",
        description=(
            "Detect queries that have regressed compared to their baseline. "
            "Runs the full analysis pipeline and filters for regression findings."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Database name",
                },
            },
            "required": ["database"],
        },
    ),
    ToolDefinition(
        name="get_slow_history",
        description=(
            "Retrieve slow query history from Query Store for a time range. "
            "Returns queries whose duration exceeded the slow-query monitoring "
            "threshold, with interval aggregation and workload classification. "
            "Use for HISTORICAL questions about past performance, NOT current issues."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Target database name",
                },
                "start_time": {
                    "type": "string",
                    "description": "Start of time range (ISO 8601)",
                },
                "end_time": {
                    "type": "string",
                    "description": "End of time range (ISO 8601)",
                },
                "threshold_ms": {
                    "type": "integer",
                    "description": (
                        "Slow-query monitoring threshold in milliseconds "
                        "(default 30000). This selects which queries are "
                        "returned. It is NOT the application timeout."
                    ),
                },
                "application_timeout_ms": {
                    "type": "integer",
                    "description": (
                        "The application's configured command timeout in "
                        "milliseconds. Supply ONLY if the user stated it. Do "
                        "not guess and do not copy threshold_ms into this."
                    ),
                },
                "top_n": {
                    "type": "integer",
                    "description": "Maximum number of candidates to return (default 20)",
                },
                "workload_type": {
                    "type": "string",
                    "description": "Filter by workload type: APPLICATION_OR_INTERACTIVE, BACKGROUND_OR_SCHEDULED, UNKNOWN, or omit for all",
                },
            },
            "required": ["database", "start_time", "end_time"],
        },
    ),
    ToolDefinition(
        name="get_timeout_candidates",
        description=(
            "Identify slow-query and timeout candidates from Query Store history. "
            "Every result exceeded the slow-query monitoring threshold and is a "
            "'slow_query_candidate'. A result is only a 'timeout_candidate' when "
            "it also reached an explicitly configured application timeout — never "
            "a confirmed timeout. Read each candidate's 'classification' field "
            "rather than assuming. "
            "Includes confidence, severity, baseline comparison, workload "
            "classification with evidence, interval aggregation, and ranking. "
            "Use for timeout/slow-query investigations with a specific time range."
        ),
        parameters={
            "type": "object",
            "properties": {
                "database": {
                    "type": "string",
                    "description": "Target database name",
                },
                "start_time": {
                    "type": "string",
                    "description": "Start of time range (ISO 8601)",
                },
                "end_time": {
                    "type": "string",
                    "description": "End of time range (ISO 8601)",
                },
                "threshold_ms": {
                    "type": "integer",
                    "description": (
                        "Slow-query monitoring threshold in milliseconds "
                        "(default 30000). Selects which queries are returned. "
                        "It is NOT the application timeout."
                    ),
                },
                "application_timeout_ms": {
                    "type": "integer",
                    "description": (
                        "The application's configured command timeout in "
                        "milliseconds. Supply ONLY if the user stated it. Do "
                        "not guess and do not copy threshold_ms into this."
                    ),
                },
                "workload_type": {
                    "type": "string",
                    "description": "Filter by workload type: APPLICATION_OR_INTERACTIVE, BACKGROUND_OR_SCHEDULED, or omit for all",
                },
            },
            "required": ["database", "start_time", "end_time"],
        },
    ),
]

# Quick lookup map
TOOL_MAP: dict[str, ToolDefinition] = {t.name: t for t in TOOL_DEFINITIONS}

# Names of all approved tools
APPROVED_TOOL_NAMES: frozenset[str] = frozenset(TOOL_MAP.keys())


# ─── Tool execution ──────────────────────────────────────────────────────


def execute_tool(name: str, arguments: dict) -> dict:
    """Execute an approved tool and return sanitised results.

    Raises KeyError if the tool name is not in the approved set.
    """
    if name not in APPROVED_TOOL_NAMES:
        raise KeyError(f"Tool '{name}' is not an approved SQLPulse tool")

    executor = _EXECUTORS.get(name)
    if executor is None:
        return {"error": f"Tool '{name}' has no executor registered"}

    try:
        raw = executor(arguments)
        return sanitize_for_llm(raw)
    except Exception as exc:
        logger.error("Tool %s failed: %s", name, str(exc))
        return {"error": f"Tool execution failed: {str(exc)}"}


# ─── Individual tool executors ────────────────────────────────────────────


def _exec_server_health(_args: dict) -> dict:
    from app.detectors.engine import analyze_performance
    result = analyze_performance(scope="server")
    return _analysis_to_dict(result)


def _exec_database_health(args: dict) -> dict:
    from app.detectors.engine import analyze_performance
    db = args.get("database", "")
    if not db:
        return {"error": "database parameter is required"}
    from app.database.connection import validate_database_name
    if not validate_database_name(db):
        return {"error": "Invalid database name"}
    result = analyze_performance(scope="database", database=db)
    return _analysis_to_dict(result)


def _exec_active_requests(args: dict) -> dict:
    from app.collectors.active_requests import ActiveRequestsCollector
    db = args.get("database") or None
    result = ActiveRequestsCollector(database=db).collect()
    return {
        "request_count": len(result.requests),
        "requests": [r.model_dump(mode="json") for r in result.requests[:50]],
    }


def _exec_blocking(args: dict) -> dict:
    from app.collectors.blocking import BlockingSessionsCollector
    db = args.get("database") or None
    result = BlockingSessionsCollector(database=db).collect()
    return {
        "blocking_chain_count": result.blocking_chain_count,
        "sessions": [s.model_dump(mode="json") for s in result.sessions[:50]],
    }


def _exec_long_running(args: dict) -> dict:
    from app.collectors.long_running import LongRunningQueriesCollector
    db = args.get("database") or None
    result = LongRunningQueriesCollector(database=db).collect()
    return {
        "query_count": len(result.queries),
        "threshold_ms": result.threshold_ms,
        "queries": [q.model_dump(mode="json") for q in result.queries[:50]],
    }


def _exec_top_cpu(args: dict) -> dict:
    from app.collectors.top_cpu import TopCpuQueriesCollector
    db = args.get("database") or None
    result = TopCpuQueriesCollector(database=db).collect()
    return {
        "query_count": len(result.queries),
        "queries": [q.model_dump(mode="json") for q in result.queries[:20]],
    }


def _exec_top_io(args: dict) -> dict:
    from app.collectors.top_io import TopIoQueriesCollector
    db = args.get("database") or None
    result = TopIoQueriesCollector(database=db).collect()
    return {
        "query_count": len(result.queries),
        "queries": [q.model_dump(mode="json") for q in result.queries[:20]],
    }


def _exec_query_history(args: dict) -> dict:
    from app.storage.history_service import get_repository
    qh = args.get("query_hash", "")
    db = args.get("database", "")
    if not qh or not db:
        return {"error": "query_hash and database are required"}
    repo = get_repository()
    records = repo.get_query_history(qh, db, limit=100)
    return {
        "query_hash": qh,
        "database": db,
        "sample_count": len(records),
        "samples": [r.model_dump(mode="json") for r in records[:100]],
    }


def _exec_query_baseline(args: dict) -> dict:
    from app.storage.baseline_service import calculate_baseline
    qh = args.get("query_hash", "")
    db = args.get("database", "")
    if not qh or not db:
        return {"error": "query_hash and database are required"}
    baseline = calculate_baseline(qh, db, window="7d")
    return baseline.model_dump(mode="json")


def _exec_query_regressions(args: dict) -> dict:
    from app.detectors.engine import analyze_performance
    from app.models.findings import FindingCategory
    db = args.get("database", "")
    if not db:
        return {"error": "database parameter is required"}
    result = analyze_performance(scope="database", database=db)
    regressions = [
        f.model_dump(mode="json")
        for f in result.findings
        if f.category == FindingCategory.QUERY_REGRESSION
    ]
    return {
        "database": db,
        "regression_count": len(regressions),
        "regressions": regressions[:20],
    }


def _parse_datetime(value: str, *, _now: datetime | None = None) -> datetime:
    """Parse a datetime string from LLM output into a naive datetime.

    Handles:
      - ISO 8601 with optional timezone (stripped to naive)
      - Date-only YYYY-MM-DD
      - Relative terms: "today", "yesterday"

    When the LLM provides a year that is more than 1 year before the current
    year, but the month/day are plausible for the current year, the year is
    corrected to the most recent past occurrence.  This prevents the LLM from
    inventing years like 2024 when the application is running in 2026.

    Args:
        value: Raw datetime string from LLM tool arguments.
        _now: Override for current time (testing only).

    Returns:
        A naive datetime matching the direct API behaviour.
    """
    now = _now or datetime.now()
    v = value.strip()

    # ── Relative terms ────────────────────────────────────────────────
    lower = v.lower()
    if lower == "today":
        return datetime(now.year, now.month, now.day)
    if lower == "yesterday":
        yesterday = now - timedelta(days=1)
        return datetime(yesterday.year, yesterday.month, yesterday.day)

    # ── Trailing Z → offset ───────────────────────────────────────────
    if v.endswith("Z"):
        v = v[:-1] + "+00:00"

    # ── ISO 8601 full datetime ────────────────────────────────────────
    try:
        dt = datetime.fromisoformat(v)
        dt = dt.replace(tzinfo=None)  # strip timezone
        dt = _correct_year(dt, now)
        return _normalize_end_of_day(dt)
    except ValueError:
        pass

    # ── Date-only: YYYY-MM-DD ─────────────────────────────────────────
    m = re.match(r"^(\d{4}-\d{2}-\d{2})$", v)
    if m:
        dt = datetime.fromisoformat(m.group(1) + "T00:00:00")
        dt = _correct_year(dt, now)
        return dt

    raise ValueError(f"Cannot parse datetime: {value!r}")


def _correct_year(dt: datetime, now: datetime) -> datetime:
    """Correct an LLM-provided year if it's implausibly old.

    If the date's year is more than 1 year before the current year AND the
    month/day would be valid for the current year (or previous year), replace
    the year with the most recent past occurrence of that month/day.

    An explicit historical year within 1 year of current is trusted as-is.
    """
    year_gap = now.year - dt.year
    if year_gap <= 1:
        # Current year or last year — trust the LLM's explicit choice
        return dt

    # Year is ≥ 2 years in the past — likely an LLM hallucination
    # Pick the most recent past occurrence of this month/day
    try:
        candidate_this_year = dt.replace(year=now.year)
    except ValueError:
        # Feb 29 in a non-leap year
        candidate_this_year = dt.replace(year=now.year, day=28)

    if candidate_this_year <= now:
        return candidate_this_year

    # Month/day is in the future for this year — use last year
    try:
        return dt.replace(year=now.year - 1)
    except ValueError:
        return dt.replace(year=now.year - 1, day=28)


def _normalize_end_of_day(dt: datetime) -> datetime:
    """Convert 23:59:59 end-times to next-day 00:00:00 (half-open interval)."""
    if dt.hour == 23 and dt.minute == 59 and dt.second >= 59:
        return (dt + timedelta(days=1)).replace(
            hour=0, minute=0, second=0, microsecond=0,
        )
    return dt


def _optional_int(args: dict, key: str) -> int | None:
    """Read an optional integer tool argument, treating junk as absent.

    Absent means "unknown", which downstream is meaningfully different from any
    number we could invent, so a malformed value is dropped rather than coerced.
    """
    raw = args.get(key)
    if raw is None or raw == "":
        return None
    try:
        value = int(raw)
    except (TypeError, ValueError):
        return None
    return value if value > 0 else None


def _exec_slow_history(args: dict) -> dict:
    from app.models.slow_history import WorkloadType
    from app.services.slow_history_service import get_slow_history_response
    db = args.get("database", "")
    st = args.get("start_time", "")
    et = args.get("end_time", "")
    threshold = int(args.get("threshold_ms", 30000))
    app_timeout = _optional_int(args, "application_timeout_ms")
    top_n = int(args.get("top_n", 20))
    wl_type_str = args.get("workload_type", "")
    if not db or not st or not et:
        return {"error": "database, start_time, and end_time are required"}
    start = _parse_datetime(st)
    end = _parse_datetime(et)

    wl_filter = None
    if wl_type_str and wl_type_str.upper() != "ALL":
        try:
            wl_filter = WorkloadType(wl_type_str.upper())
        except ValueError:
            pass

    response = get_slow_history_response(
        database=db,
        start_time=start,
        end_time=end,
        threshold_ms=threshold,
        top_n=top_n,
        group_results=True,
        workload_type=wl_filter,
        application_timeout_ms=app_timeout,
    )
    data = response.model_dump(mode="json")

    return {
        "database": data["database"],
        "start_time": data["start_time"],
        "end_time": data["end_time"],
        "slow_threshold_ms": data["slow_threshold_ms"],
        "application_timeout_ms": data["application_timeout_ms"],
        "timeout_classification_available": data["timeout_classification_available"],
        "threshold_ms": data["threshold_ms"],
        "top_n": data["top_n"],
        "query_store_status": data["query_store_status"],
        "candidate_count": data["total_candidates"],
        "slow_query_candidate_count": data["slow_query_candidate_count"],
        "timeout_candidate_count": data["timeout_candidate_count"],
        "candidates": data["candidates"],
        "message": data.get("message"),
    }


def _exec_timeout_candidates(args: dict) -> dict:
    from app.collectors.query_store import collect_slow_history
    from app.config import get_settings
    from app.detectors.timeout_candidate import build_timeout_candidates
    from app.models.slow_history import WorkloadType
    db = args.get("database", "")
    st = args.get("start_time", "")
    et = args.get("end_time", "")
    threshold = int(args.get("threshold_ms", 30000))
    app_timeout = _optional_int(args, "application_timeout_ms")
    if app_timeout is None:
        app_timeout = get_settings().application_timeout_ms
    wl_type_str = args.get("workload_type", "")
    if not db or not st or not et:
        return {"error": "database, start_time, and end_time are required"}
    start = _parse_datetime(st)
    end = _parse_datetime(et)

    # Parse optional workload_type filter
    wl_filter = None
    if wl_type_str and wl_type_str.upper() != "ALL":
        try:
            wl_filter = WorkloadType(wl_type_str.upper())
        except ValueError:
            pass

    logger.info(
        "get_timeout_candidates: database=%s start=%s end=%s threshold_ms=%d "
        "application_timeout_ms=%s workload_type=%s",
        db, start.isoformat(), end.isoformat(), threshold,
        app_timeout if app_timeout is not None else "not_configured",
        wl_type_str or "ALL",
    )

    status, records, _msg = collect_slow_history(
        database=db, start_time=start, end_time=end,
        threshold_ms=float(threshold), top_n=20,
    )
    if not status.is_accessible:
        return {
            "database": db,
            "query_store_status": status.value,
            "candidate_count": 0,
            "candidates": [],
        }
    candidates = build_timeout_candidates(
        records, float(threshold), db,
        group_results=True,
        workload_type_filter=wl_filter,
        application_timeout_ms=float(app_timeout) if app_timeout is not None else None,
    )

    timeout_count = sum(1 for c in candidates if c.timeout_candidate)
    logger.info(
        "get_timeout_candidates result: query_store_status=%s records=%d "
        "candidates=%d timeout_candidates=%d",
        status.value, len(records), len(candidates), timeout_count,
    )

    return {
        "database": db,
        "query_store_status": status.value,
        "slow_threshold_ms": threshold,
        "application_timeout_ms": app_timeout,
        # Stated explicitly so the model reports "not determinable" instead of
        # treating the monitoring threshold as the application's timeout.
        "timeout_classification_available": app_timeout is not None,
        "candidate_count": len(candidates),
        "slow_query_candidate_count": sum(
            1 for c in candidates if c.slow_query_candidate
        ),
        "timeout_candidate_count": timeout_count,
        "candidates": [c.model_dump(mode="json") for c in candidates[:20]],
    }


def _analysis_to_dict(result) -> dict:
    """Convert an AnalysisResult to a sanitised dict for the LLM."""
    d = result.model_dump(mode="json")
    # Keep only the most relevant fields to limit token usage
    return {
        "overall_status": d.get("overall_status"),
        # Health and optimization are separate concerns: a server can be
        # HEALTHY while still having queries worth tuning. Stating this
        # explicitly stops the model reporting tuning candidates as an incident.
        "current_health_status": (d.get("current_health") or {}).get("status"),
        "health_finding_count": len((d.get("current_health") or {}).get("findings", [])),
        "optimization_opportunity_count": len(d.get("optimization_opportunities", [])),
        "summary": d.get("summary"),
        "total_findings": d.get("total_findings"),
        "critical_count": d.get("critical_count"),
        "high_count": d.get("high_count"),
        "medium_count": d.get("medium_count"),
        "low_count": d.get("low_count"),
        "affected_databases": d.get("affected_databases", []),
        "findings": [
            {
                "category": f.get("category"),
                # May be null when plan-cache metadata cannot resolve it.
                "database": f.get("database"),
                "severity": f.get("severity"),
                "confidence": f.get("confidence"),
                "signals": f.get("signals", []),
                "query_identity": f.get("query_identity"),
                "severity_basis": f.get("severity_basis"),
                "summary": f.get("summary"),
                "evidence": f.get("evidence", []),
                "likely_cause": f.get("likely_cause"),
                "recommendation": f.get("recommendation"),
            }
            for f in d.get("findings", [])[:20]
        ],
        "historical_analysis_status": d.get("historical_analysis_status"),
    }


# ─── Executor registry ───────────────────────────────────────────────────

_EXECUTORS: dict[str, callable] = {
    "get_server_health": _exec_server_health,
    "get_database_health": _exec_database_health,
    "get_active_requests": _exec_active_requests,
    "get_blocking": _exec_blocking,
    "get_long_running_queries": _exec_long_running,
    "get_top_cpu_queries": _exec_top_cpu,
    "get_top_io_queries": _exec_top_io,
    "get_query_history": _exec_query_history,
    "get_query_baseline": _exec_query_baseline,
    "get_query_regressions": _exec_query_regressions,
    "get_slow_history": _exec_slow_history,
    "get_timeout_candidates": _exec_timeout_candidates,
}
