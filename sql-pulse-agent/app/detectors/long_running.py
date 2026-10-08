"""Detector for long-running queries."""

import logging

from app.detectors.base import BaseDetector
from app.models.findings import (
    Confidence,
    FindingCategory,
    PerformanceFinding,
    Severity,
)
from app.models.performance import LongRunningQuery, PerformanceSnapshot

logger = logging.getLogger(__name__)

# Known SQL Server system/background procedures and programs that should not
# generate long-running findings. These are always present and not actionable.
_SYSTEM_PROGRAM_NAMES = frozenset({
    "sp_server_diagnostics",
    "SQLAgent - TSQL JobStep",
})

_SYSTEM_SQL_PATTERNS = (
    "sp_server_diagnostics",
    "WAITFOR(RECEIVE",
    "BROKER",
    "xp_sqlagent_notify",
)

# Background wait types that indicate idle/sleeping system tasks, not real queries
_BACKGROUND_WAIT_TYPES = frozenset({
    "SP_SERVER_DIAGNOSTICS_SLEEP",
    "WAITFOR",
    "BROKER_RECEIVE_WAITFOR",
    "LAZYWRITER_SLEEP",
    "REQUEST_FOR_DEADLOCK_SEARCH",
    "XE_TIMER_EVENT",
    "SLEEP_TASK",
    "SLEEP_SYSTEMTASK",
})


def _is_system_background_query(query: LongRunningQuery) -> bool:
    """Return True if the query is a known system/background task."""
    # Check program name
    if query.program_name and query.program_name.strip() in _SYSTEM_PROGRAM_NAMES:
        return True

    # Check wait type (background sleeps)
    if query.wait_type and query.wait_type.upper() in _BACKGROUND_WAIT_TYPES:
        return True

    # Check SQL text for known system patterns
    if query.sql_text:
        sql_upper = query.sql_text.upper()
        for pattern in _SYSTEM_SQL_PATTERNS:
            if pattern.upper() in sql_upper:
                return True

    return False


class LongRunningDetector(BaseDetector):
    """Detects long-running queries based on elapsed time thresholds."""

    @property
    def name(self) -> str:
        return "long_running_detector"

    def detect(self, snapshot: PerformanceSnapshot) -> list[PerformanceFinding]:
        findings: list[PerformanceFinding] = []

        if not snapshot.long_running_queries or not snapshot.long_running_queries.metadata.success:
            return findings

        queries = snapshot.long_running_queries.queries
        if not queries:
            return findings

        # Filter out known system/background tasks
        queries = [q for q in queries if not _is_system_background_query(q)]
        if not queries:
            return findings

        # Database-scope isolation: skip queries not belonging to target database
        target_database = snapshot.database
        if target_database:
            queries = [
                q for q in queries
                if not q.database_name or q.database_name == target_database
            ]
            if not queries:
                return findings

        t = self.thresholds
        now = self._now()

        # Count queries at each severity tier
        critical_queries = [q for q in queries if q.elapsed_time_ms >= t.long_running_critical_ms]
        high_queries = [
            q for q in queries
            if t.long_running_high_ms <= q.elapsed_time_ms < t.long_running_critical_ms
        ]
        medium_queries = [
            q for q in queries
            if t.long_running_warning_ms <= q.elapsed_time_ms < t.long_running_high_ms
        ]

        # Critical: multiple critical-tier queries OR single extremely long query
        if len(critical_queries) >= t.long_running_critical_count:
            findings.append(self._build_finding(
                severity=Severity.CRITICAL,
                confidence=Confidence.HIGH,
                queries=critical_queries,
                snapshot=snapshot,
                now=now,
                summary=(
                    f"{len(critical_queries)} queries running longer than "
                    f"{t.long_running_critical_ms / 1000:.0f}s detected concurrently"
                ),
            ))
        elif critical_queries:
            # Single critical-duration query → high severity
            for q in critical_queries:
                findings.append(self._build_finding(
                    severity=Severity.HIGH,
                    confidence=Confidence.MEDIUM,
                    queries=[q],
                    snapshot=snapshot,
                    now=now,
                    summary=(
                        f"Query on session {q.session_id} running for "
                        f"{q.elapsed_time_ms / 1000:.1f}s "
                        f"(threshold: {t.long_running_critical_ms / 1000:.0f}s)"
                    ),
                ))

        # High-tier queries
        for q in high_queries:
            findings.append(self._build_finding(
                severity=Severity.MEDIUM,
                confidence=Confidence.MEDIUM,
                queries=[q],
                snapshot=snapshot,
                now=now,
                summary=(
                    f"Query on session {q.session_id} running for "
                    f"{q.elapsed_time_ms / 1000:.1f}s "
                    f"(threshold: {t.long_running_high_ms / 1000:.0f}s)"
                ),
            ))

        # Medium-tier queries → low severity individual findings
        if len(medium_queries) >= 3:
            findings.append(self._build_finding(
                severity=Severity.LOW,
                confidence=Confidence.MEDIUM,
                queries=medium_queries,
                snapshot=snapshot,
                now=now,
                summary=(
                    f"{len(medium_queries)} queries running between "
                    f"{t.long_running_warning_ms / 1000:.0f}s and "
                    f"{t.long_running_high_ms / 1000:.0f}s"
                ),
            ))

        return findings

    def _build_finding(self, severity, confidence, queries, snapshot, now, summary):
        evidence = []
        for q in queries:
            text_preview = (q.sql_text or "N/A")[:120]
            evidence.append(
                f"Session {q.session_id}: elapsed={q.elapsed_time_ms}ms, "
                f"cpu={q.cpu_time_ms}ms, reads={q.logical_reads}, "
                f"db={q.database_name or 'N/A'}, "
                f"wait={q.wait_type or 'none'} — {text_preview}"
            )

        # Use the query's database_name for attribution when available
        finding_database = snapshot.database or "unknown"
        if queries and queries[0].database_name:
            finding_database = queries[0].database_name

        # Determine likely cause based on available evidence
        wait_types = {q.wait_type for q in queries if q.wait_type}
        if wait_types:
            likely_cause = (
                f"Query is actively waiting on: {', '.join(sorted(wait_types))}. "
                "The specific root cause requires execution plan analysis."
            )
        else:
            likely_cause = (
                "Query has been executing beyond the expected threshold. "
                "The specific root cause cannot be determined from elapsed time alone; "
                "execution plan analysis is the recommended next diagnostic step."
            )

        return PerformanceFinding(
            finding_id=self._generate_finding_id(),
            category=FindingCategory.LONG_RUNNING_QUERY,
            server=snapshot.server_host,
            database=finding_database,
            severity=severity,
            confidence=confidence,
            first_observed=now,
            last_observed=now,
            summary=summary,
            evidence=evidence,
            likely_cause=likely_cause,
            recommendation=(
                "Retrieve the execution plan for the running session via "
                "sys.dm_exec_query_plan. Identify the most expensive operator "
                "and determine whether it is blocked, scanning, or compute-bound."
            ),
            validation_steps=[
                "Check sys.dm_exec_query_plan for the session's execution plan",
                "Examine the current wait type and resource to understand the bottleneck",
                "Check if the query is waiting on locks, I/O, or other resources",
                "Compare actual vs estimated rows in the plan to detect cardinality issues",
            ],
            human_review_required=(severity in (Severity.CRITICAL, Severity.HIGH)),
        )
