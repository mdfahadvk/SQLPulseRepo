"""Detector for high I/O-consuming queries."""

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


class HighIoDetector(BaseDetector):
    """Detects queries with excessive logical reads / I/O consumption."""

    @property
    def name(self) -> str:
        return "high_io_detector"

    def detect(self, snapshot: PerformanceSnapshot) -> list[PerformanceFinding]:
        findings: list[PerformanceFinding] = []
        self.excluded_queries = []

        if not snapshot.top_io_queries or not snapshot.top_io_queries.metadata.success:
            return findings

        queries = snapshot.top_io_queries.queries
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

            # Skip queries with too few executions
            if q.execution_count < t.io_min_executions:
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
                f"Total logical reads: {q.total_logical_reads:,}",
                f"Avg logical reads per execution: {q.avg_logical_reads:,.0f}",
                f"Total physical reads: {q.total_physical_reads:,}",
                f"Avg physical reads: {q.avg_physical_reads:,.1f}",
                f"Database: {q.database_name or 'unresolved'}",
                f"Severity basis: {basis}",
                f"SQL: {text_preview}",
            ]

            findings.append(PerformanceFinding(
                finding_id=self._generate_finding_id(),
                category=FindingCategory.HIGH_IO,
                server=snapshot.server_host,
                database=database,
                severity=severity,
                confidence=confidence,
                first_observed=now,
                last_observed=now,
                summary=(
                    f"High I/O query: avg {q.avg_logical_reads:,.0f} logical reads/exec "
                    f"over {q.execution_count} executions "
                    f"(total {q.total_logical_reads:,} reads)"
                ),
                evidence=evidence,
                signals=[FindingSignal.HIGH_IO],
                query_identity=QueryIdentity(
                    query_hash=q.query_hash,
                    query_plan_hash=q.query_plan_hash,
                    object_name=q.object_name,
                ),
                severity_basis=basis,
                likely_cause=(
                    "This query performs a high number of logical reads per execution "
                    "based on plan-cache statistics. The specific root cause cannot be "
                    "determined from aggregate metrics alone."
                ),
                recommendation=(
                    "Retrieve the execution plan via sys.dm_exec_query_plan to "
                    "identify operators with highest I/O cost. Look for scan operators "
                    "that could benefit from index changes."
                ),
                validation_steps=[
                    "Get the execution plan and find operators with highest I/O cost",
                    "Check for Table Scan or Clustered Index Scan operators",
                    "Look for missing index DMV recommendations",
                    "Compare actual vs estimated row counts",
                ],
                human_review_required=(severity == Severity.HIGH),
            ))

        return findings

    def _classify(self, q) -> tuple[Severity | None, Confidence, str]:
        """Assign severity from per-execution reads first, aggregate reads second.

        Mirrors the CPU detector: reads-per-execution identify an inefficient
        access path, whereas lifetime totals mostly reflect how often the query
        ran. Totals are therefore gated behind a per-execution floor.
        """
        t = self.thresholds

        # Extremely read-heavy on every execution. HIGH rather than CRITICAL:
        # cached lifetime statistics cannot demonstrate current resource pressure.
        if q.avg_logical_reads >= t.io_critical_avg_logical_reads:
            return (
                Severity.HIGH,
                Confidence.MEDIUM,
                f"avg logical reads/exec {q.avg_logical_reads:,.0f} >= "
                f"{t.io_critical_avg_logical_reads:,.0f} over "
                f"{q.execution_count} executions",
            )

        if q.avg_logical_reads >= t.io_high_avg_logical_reads:
            return (
                Severity.MEDIUM,
                Confidence.MEDIUM,
                f"avg logical reads/exec {q.avg_logical_reads:,.0f} >= "
                f"{t.io_high_avg_logical_reads:,.0f} over "
                f"{q.execution_count} executions",
            )

        # Large lifetime total. Only meaningful if each execution is itself
        # non-trivial, otherwise this is just a frequently-run cheap query.
        if (
            q.total_logical_reads >= t.io_critical_total_logical_reads
            and q.avg_logical_reads >= t.io_min_significant_avg_reads
        ):
            return (
                Severity.LOW,
                Confidence.LOW,
                f"total logical reads {q.total_logical_reads:,} >= "
                f"{t.io_critical_total_logical_reads:,} with avg "
                f"{q.avg_logical_reads:,.0f}/exec; aggregate cost only",
            )

        return (None, Confidence.LOW, "")
