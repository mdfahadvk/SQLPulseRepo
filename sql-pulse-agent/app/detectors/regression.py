"""Detector for query performance regressions.

Compares current query metrics against historical baselines to detect
significant performance degradation.
"""

import logging

from app.detectors.base import BaseDetector
from app.models.findings import (
    Confidence,
    FindingCategory,
    PerformanceFinding,
    Severity,
)
from app.models.history import BaselineStatistic, QueryBaseline
from app.models.performance import PerformanceSnapshot, TopCpuQuery, TopIoQuery
from app.storage.baseline_service import get_baseline_for_regression

logger = logging.getLogger(__name__)


class QueryRegressionDetector(BaseDetector):
    """Detects queries whose current metrics significantly exceed their historical baseline."""

    @property
    def name(self) -> str:
        return "query_regression_detector"

    def detect(self, snapshot: PerformanceSnapshot) -> list[PerformanceFinding]:
        """Compare current query metrics against baselines."""
        findings: list[PerformanceFinding] = []

        target_database = snapshot.database

        # Collect current query metrics from CPU and IO collectors
        current_queries: dict[str, dict] = {}

        if snapshot.top_cpu_queries and snapshot.top_cpu_queries.metadata.success:
            for q in snapshot.top_cpu_queries.queries:
                if not q.query_hash:
                    continue
                if target_database and q.database_name and q.database_name != target_database:
                    continue
                db = q.database_name or target_database or "unknown"
                current_queries[q.query_hash] = {
                    "database": db,
                    "avg_cpu_ms": q.avg_cpu_time_ms,
                    "avg_elapsed_ms": q.avg_elapsed_time_ms,
                    "avg_logical_reads": q.avg_logical_reads,
                    "execution_count": q.execution_count,
                    "sql_text": q.sql_text,
                }

        if snapshot.top_io_queries and snapshot.top_io_queries.metadata.success:
            for q in snapshot.top_io_queries.queries:
                if not q.query_hash:
                    continue
                if target_database and q.database_name and q.database_name != target_database:
                    continue
                db = q.database_name or target_database or "unknown"
                if q.query_hash not in current_queries:
                    current_queries[q.query_hash] = {
                        "database": db,
                        "avg_cpu_ms": 0.0,
                        "avg_elapsed_ms": 0.0,
                        "avg_logical_reads": q.avg_logical_reads,
                        "avg_physical_reads": q.avg_physical_reads,
                        "execution_count": q.execution_count,
                        "sql_text": q.sql_text,
                    }
                else:
                    # Merge reads data
                    existing = current_queries[q.query_hash]
                    if q.avg_logical_reads > existing.get("avg_logical_reads", 0):
                        existing["avg_logical_reads"] = q.avg_logical_reads
                    existing["avg_physical_reads"] = q.avg_physical_reads

        # Check each query against its baseline
        t = self.thresholds
        now = self._now()
        window = t.regression_baseline_window
        min_samples = t.regression_min_samples

        for query_hash, metrics in current_queries.items():
            db = metrics["database"]
            baseline = get_baseline_for_regression(query_hash, db, window, min_samples)
            if baseline is None:
                continue

            # Check for regression across multiple dimensions
            regression = self._check_regression(metrics, baseline)
            if regression is None:
                continue

            dimension, multiplier, current_val, baseline_mean, sample_count, std_dev = regression

            # Determine severity from multiplier
            if multiplier >= t.regression_critical_multiplier:
                severity = Severity.CRITICAL
            elif multiplier >= t.regression_high_multiplier:
                severity = Severity.HIGH
            elif multiplier >= t.regression_warning_multiplier:
                severity = Severity.MEDIUM
            else:
                continue

            # Determine confidence based on sample count and variance
            confidence = self._compute_confidence(sample_count, std_dev, baseline_mean, current_val)

            text_preview = (metrics.get("sql_text") or "")[:120]
            evidence = [
                f"Query hash: {query_hash}",
                f"Database: {db}",
                f"Regression dimension: {dimension}",
                f"Current value: {current_val:.1f}",
                f"Baseline mean ({window}): {baseline_mean:.1f}",
                f"Regression multiplier: {multiplier:.1f}x",
                f"Baseline samples: {sample_count}",
                f"Baseline std dev: {std_dev:.1f}",
            ]
            if text_preview:
                evidence.append(f"SQL: {text_preview}")

            findings.append(PerformanceFinding(
                finding_id=self._generate_finding_id(),
                category=FindingCategory.QUERY_REGRESSION,
                server=snapshot.server_host,
                database=db,
                severity=severity,
                confidence=confidence,
                first_observed=now,
                last_observed=now,
                summary=(
                    f"Query {dimension} is {multiplier:.1f}x worse than its "
                    f"{window} baseline."
                ),
                evidence=evidence,
                likely_cause=(
                    f"Query performance has regressed: current {dimension} "
                    f"({current_val:.0f}) significantly exceeds the historical "
                    f"baseline ({baseline_mean:.0f}). This indicates a recent "
                    f"change in query behavior."
                ),
                recommendation=(
                    "Compare the current execution plan against a known-good plan. "
                    "Check for recent schema changes, statistics updates, parameter "
                    "changes, or data volume growth that could explain the regression."
                ),
                validation_steps=[
                    "Retrieve the current execution plan",
                    "Check if statistics were recently updated",
                    "Look for recent schema or index changes",
                    "Verify data volume hasn't changed dramatically",
                ],
                human_review_required=(severity in (Severity.CRITICAL, Severity.HIGH)),
            ))

        return findings

    def _check_regression(
        self, metrics: dict, baseline: QueryBaseline
    ) -> tuple[str, float, float, float, int, float] | None:
        """Check for regression across multiple metrics.

        Returns (dimension, multiplier, current, baseline_mean, samples, std_dev)
        for the worst regression, or None if no regression.
        """
        worst: tuple[str, float, float, float, int, float] | None = None
        t = self.thresholds

        checks = [
            ("avg_elapsed_ms", metrics.get("avg_elapsed_ms", 0), baseline.avg_elapsed_ms),
            ("avg_cpu_ms", metrics.get("avg_cpu_ms", 0), baseline.avg_cpu_ms),
            ("avg_logical_reads", metrics.get("avg_logical_reads", 0), baseline.avg_logical_reads),
        ]

        for dimension, current_val, stat in checks:
            if stat is None or stat.mean <= 0 or current_val <= 0:
                continue
            multiplier = current_val / stat.mean
            if multiplier >= t.regression_warning_multiplier:
                if worst is None or multiplier > worst[1]:
                    worst = (
                        dimension,
                        multiplier,
                        current_val,
                        stat.mean,
                        stat.sample_count,
                        stat.standard_deviation,
                    )

        return worst

    def _compute_confidence(
        self, sample_count: int, std_dev: float, baseline_mean: float, current_val: float
    ) -> Confidence:
        """Compute confidence based on sample count and variance.

        Stable baselines with many samples → high confidence.
        Noisy baselines or few samples → lower confidence.
        """
        if sample_count < 5:
            return Confidence.LOW

        # Coefficient of variation (relative standard deviation)
        if baseline_mean > 0:
            cv = std_dev / baseline_mean
        else:
            cv = 0.0

        # High confidence: many samples AND low variance
        if sample_count >= 10 and cv < 0.5:
            return Confidence.HIGH

        # Medium confidence: enough samples but some variance
        if sample_count >= 5:
            if cv < 1.0:
                return Confidence.MEDIUM
            else:
                return Confidence.LOW

        return Confidence.LOW
