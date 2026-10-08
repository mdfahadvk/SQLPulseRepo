"""Detector for high CPU-consuming queries."""

import logging

from app.detectors.base import BaseDetector
from app.models.findings import (
    Confidence,
    FindingCategory,
    FindingSignal,
    PerformanceFinding,
    QueryIdentity,
    Severity,
)
from app.models.performance import PerformanceSnapshot

logger = logging.getLogger(__name__)


class HighCpuDetector(BaseDetector):
    """Detects queries with disproportionately high CPU consumption."""

    @property
    def name(self) -> str:
        return "high_cpu_detector"

    def detect(self, snapshot: PerformanceSnapshot) -> list[PerformanceFinding]:
        findings: list[PerformanceFinding] = []
        self.excluded_queries = []

        if not snapshot.top_cpu_queries or not snapshot.top_cpu_queries.metadata.success:
            return findings

        queries = snapshot.top_cpu_queries.queries
        if not queries:
            return findings

        t = self.thresholds
        now = self._now()
        target_database = snapshot.database

        for q in queries:
            # Database-scope isolation: skip queries not belonging to target database
            if target_database and q.database_name and q.database_name != target_database:
                continue

            # SQLPulse's own DMV reads and other monitoring traffic are not
            # tuning targets — reporting them would crowd out real workload.
            if self._is_monitoring_query(q):
                continue

            # Skip queries with too few executions — avoid flagging one-off queries
            if q.execution_count < t.cpu_min_executions:
                continue

            severity, confidence, basis = self._classify(q)
            if severity is None:
                continue

            # Plan-cache metadata does not always carry a database. Without one
            # the observation is weaker: it cannot be attributed or drilled into.
            database = q.database_name or target_database
            if database is None:
                confidence = self._weaken_confidence(confidence)
                basis = f"{basis}; database unresolved, confidence reduced"

            text_preview = (q.sql_text or "N/A")[:150]
            evidence = [
                f"Query hash: {q.query_hash or 'N/A'}",
                f"Query plan hash: {q.query_plan_hash or 'N/A'}",
                f"Object: {q.object_name or 'N/A'}",
                f"Execution count: {q.execution_count}",
                f"Total CPU time: {q.total_cpu_time_ms}ms",
                f"Avg CPU time per execution: {q.avg_cpu_time_ms:.1f}ms",
                f"Total elapsed time: {q.total_elapsed_time_ms}ms",
                f"Avg elapsed time: {q.avg_elapsed_time_ms:.1f}ms",
                f"Database: {q.database_name or 'unresolved'}",
                f"Severity basis: {basis}",
                f"SQL: {text_preview}",
            ]

            findings.append(PerformanceFinding(
                finding_id=self._generate_finding_id(),
                category=FindingCategory.HIGH_CPU,
                server=snapshot.server_host,
                database=database,
                severity=severity,
                confidence=confidence,
                first_observed=now,
                last_observed=now,
                summary=(
                    f"High CPU query: avg {q.avg_cpu_time_ms:.0f}ms/exec "
                    f"over {q.execution_count} executions "
                    f"(total {q.total_cpu_time_ms}ms)"
                ),
                evidence=evidence,
                signals=[FindingSignal.HIGH_CPU],
                query_identity=QueryIdentity(
                    query_hash=q.query_hash,
                    query_plan_hash=q.query_plan_hash,
                    object_name=q.object_name,
                ),
                severity_basis=basis,
                likely_cause=(
                    "This query consumes significant CPU per execution based on "
                    "plan-cache statistics. The specific root cause cannot be "
                    "determined from aggregate metrics alone."
                ),
                recommendation=(
                    "Retrieve the execution plan via sys.dm_exec_query_plan to "
                    "identify expensive operators. Review for optimization opportunities."
                ),
                validation_steps=[
                    "Get the cached execution plan via sys.dm_exec_query_plan",
                    "Identify the most expensive operators in the plan",
                    "Check for missing index suggestions in the plan XML",
                    "Compare actual vs estimated row counts for cardinality issues",
                ],
                human_review_required=(severity == Severity.HIGH),
            ))

        return findings

    def _classify(self, q) -> tuple[Severity | None, Confidence, str]:
        """Assign severity from per-execution cost first, aggregate cost second.

        Per-execution cost is what identifies an inefficient query. Aggregate
        totals only describe how much workload the server ran, so they are
        treated as the weaker signal and are gated behind a per-execution floor.
        """
        t = self.thresholds

        # Expensive every single time it runs — the strongest plan-cache signal.
        # Still HIGH rather than CRITICAL: cached lifetime statistics cannot show
        # that anything is wrong *right now*.
        if q.avg_cpu_time_ms >= t.cpu_critical_avg_ms:
            return (
                Severity.HIGH,
                Confidence.MEDIUM,
                f"avg CPU/exec {q.avg_cpu_time_ms:.0f}ms >= "
                f"{t.cpu_critical_avg_ms:.0f}ms over {q.execution_count} executions",
            )

        if q.avg_cpu_time_ms >= t.cpu_high_avg_ms:
            return (
                Severity.MEDIUM,
                Confidence.MEDIUM,
                f"avg CPU/exec {q.avg_cpu_time_ms:.0f}ms >= "
                f"{t.cpu_high_avg_ms:.0f}ms over {q.execution_count} executions",
            )

        # Large lifetime total. Only meaningful if each execution is itself
        # non-trivial, otherwise this is just a frequently-run cheap query.
        if (
            q.total_cpu_time_ms >= t.cpu_critical_total_ms
            and q.avg_cpu_time_ms >= t.cpu_min_significant_avg_ms
        ):
            return (
                Severity.LOW,
                Confidence.LOW,
                f"total CPU {q.total_cpu_time_ms}ms >= {t.cpu_critical_total_ms}ms "
                f"with avg {q.avg_cpu_time_ms:.0f}ms/exec; aggregate cost only",
            )

        return (None, Confidence.LOW, "")
