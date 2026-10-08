"""Detection engine — orchestrates all detectors and produces analysis results."""

import logging
import uuid
from collections import defaultdict
from datetime import UTC, datetime

from app.collectors.snapshot import collect_performance_snapshot
from app.detectors.blocking import BlockingDetector
from app.detectors.high_cpu import HighCpuDetector
from app.detectors.high_io import HighIoDetector
from app.detectors.long_running import LongRunningDetector
from app.detectors.thresholds import DetectorThresholds, get_thresholds
from app.models.findings import (
    AffectedDatabase,
    AnalysisResult,
    AnalysisSummary,
    Confidence,
    ExcludedQuery,
    FindingCategory,
    HealthSection,
    OverallStatus,
    PerformanceFinding,
    RecommendedDrilldown,
    Severity,
)
from app.models.performance import PerformanceSnapshot

logger = logging.getLogger(__name__)

# Categories that represent current active health issues
_HEALTH_CATEGORIES = frozenset({
    FindingCategory.LONG_RUNNING_QUERY,
    FindingCategory.BLOCKING,
    FindingCategory.QUERY_REGRESSION,
})

# Categories that are optimization opportunities (historical/plan-cache based)
_OPTIMIZATION_CATEGORIES = frozenset({
    FindingCategory.HIGH_CPU,
    FindingCategory.HIGH_IO,
})

# Plan-cache categories describing the same underlying query, so a query
# appearing in both is one finding with two signals.
_CORRELATABLE_CATEGORIES = frozenset({
    FindingCategory.HIGH_CPU,
    FindingCategory.HIGH_IO,
})

_SEVERITY_ORDER = {
    Severity.CRITICAL: 0,
    Severity.HIGH: 1,
    Severity.MEDIUM: 2,
    Severity.LOW: 3,
}

_CONFIDENCE_ORDER = {
    Confidence.HIGH: 0,
    Confidence.MEDIUM: 1,
    Confidence.LOW: 2,
}


def _sort_findings(findings: list[PerformanceFinding]) -> list[PerformanceFinding]:
    """Sort findings by severity (critical first), then confidence."""
    return sorted(
        findings, key=lambda f: (_SEVERITY_ORDER[f.severity], f.confidence.value)
    )


def determine_overall_status(findings: list[PerformanceFinding]) -> OverallStatus:
    """Deterministically compute overall health from findings."""
    if not findings:
        return OverallStatus.HEALTHY
    severities = {f.severity for f in findings}
    if Severity.CRITICAL in severities:
        return OverallStatus.CRITICAL
    if Severity.HIGH in severities:
        return OverallStatus.DEGRADED
    if Severity.MEDIUM in severities:
        return OverallStatus.WARNING
    return OverallStatus.HEALTHY


def _compute_affected_databases(
    findings: list[PerformanceFinding],
) -> list[AffectedDatabase]:
    """Group findings by database and return summary.

    Findings whose database could not be resolved are skipped rather than
    grouped under a placeholder name.
    """
    db_findings: dict[str, list[PerformanceFinding]] = defaultdict(list)
    for f in findings:
        if f.database:
            db_findings[f.database].append(f)

    result = []
    for db, db_list in db_findings.items():
        highest = min(db_list, key=lambda x: _SEVERITY_ORDER[x.severity])
        result.append(AffectedDatabase(
            database=db,
            highest_severity=highest.severity.value,
            finding_count=len(db_list),
        ))
    return sorted(result, key=lambda a: _SEVERITY_ORDER.get(
        Severity(a.highest_severity), 4
    ))


def _add_drilldown(findings: list[PerformanceFinding], scope: str) -> None:
    """Add recommended_drilldown to server-level findings that identify a database.

    A finding with no resolved database gets no drilldown: pointing the user at
    a database SQLPulse could not name would be a dead end.
    """
    if scope != "server":
        return
    for f in findings:
        if f.database and f.severity in (Severity.CRITICAL, Severity.HIGH, Severity.MEDIUM):
            f.recommended_drilldown = RecommendedDrilldown(
                scope="database", database=f.database
            )


def _correlation_key(finding: PerformanceFinding) -> tuple | None:
    """Identity used to recognise findings about the same query.

    Returns None when the finding cannot be identified confidently — such
    findings are never merged, because merging on weak identity would combine
    unrelated queries.
    """
    identity = finding.query_identity
    if identity is None or not identity.query_hash:
        return None
    # Plan hash is included when present: the same statement running under two
    # different plans is genuinely two different performance stories.
    return (finding.database, identity.query_hash, identity.query_plan_hash)


def _merge_correlated(findings: list[PerformanceFinding]) -> list[PerformanceFinding]:
    """Combine CPU and I/O findings that describe the same query.

    A query that is both CPU- and read-heavy is one problem with two symptoms,
    not two problems. Emitting it twice doubles the noise and makes the counts
    misleading. The merged finding keeps every metric from both sides, so no
    evidence is lost.
    """
    merged: dict[tuple, PerformanceFinding] = {}
    result: list[PerformanceFinding] = []

    for finding in findings:
        key = _correlation_key(finding)
        if key is None or finding.category not in _CORRELATABLE_CATEGORIES:
            result.append(finding)
            continue

        existing = merged.get(key)
        if existing is None:
            merged[key] = finding
            result.append(finding)
            continue

        _absorb(existing, finding)

    return result


def _absorb(target: PerformanceFinding, other: PerformanceFinding) -> None:
    """Fold *other* into *target* in place, preserving all evidence."""
    for signal in other.signals:
        if signal not in target.signals:
            target.signals.append(signal)

    # Keep both metric sets, labelled so it stays clear which signal each
    # measurement came from.
    label = other.category.value
    target.evidence.extend(
        line for line in (f"[{label}] {e}" for e in other.evidence)
        if line not in target.evidence
    )

    # The finding inherits the more serious severity of the two symptoms.
    if _SEVERITY_ORDER[other.severity] < _SEVERITY_ORDER[target.severity]:
        target.severity = other.severity
        target.category = other.category
        target.summary = other.summary
        target.likely_cause = other.likely_cause
        target.recommendation = other.recommendation
        target.human_review_required = other.human_review_required

    target.severity_basis = "; ".join(
        b for b in (target.severity_basis, other.severity_basis) if b
    ) or None

    for step in other.validation_steps:
        if step not in target.validation_steps:
            target.validation_steps.append(step)

    # Confidence stays at the strongest of the two. Corroboration across
    # detectors is NOT treated as grounds to exceed it: both readings come from
    # the same cached statistics, so they are not independent evidence.
    if _CONFIDENCE_ORDER[other.confidence] < _CONFIDENCE_ORDER[target.confidence]:
        target.confidence = other.confidence


def _deduplicate_exclusions(exclusions: list[ExcludedQuery]) -> list[ExcludedQuery]:
    """Collapse the same excluded query reported by several detectors.

    A monitoring query typically appears in both the CPU and I/O lists, so
    without this the exclusion report would double-count it.
    """
    seen: set[tuple] = set()
    unique: list[ExcludedQuery] = []
    for item in exclusions:
        key = (item.reason, item.query_hash, item.database, item.sql_preview)
        if key in seen:
            continue
        seen.add(key)
        unique.append(item)
    return unique


def _build_summary(
    result_findings: list[PerformanceFinding],
    health_findings: list[PerformanceFinding],
    optimization_findings: list[PerformanceFinding],
    overall_status: OverallStatus,
    affected_databases: list[AffectedDatabase],
    excluded_count: int,
) -> AnalysisSummary:
    """Build the flat digest the UI and AI agent read first."""
    highest = None
    if result_findings:
        highest = min(
            result_findings, key=lambda f: _SEVERITY_ORDER[f.severity]
        ).severity.value

    return AnalysisSummary(
        current_health_status=overall_status,
        health_finding_count=len(health_findings),
        optimization_opportunity_count=len(optimization_findings),
        highest_severity=highest,
        affected_database_count=len(affected_databases),
        total_findings=len(result_findings),
        critical_count=sum(1 for f in result_findings if f.severity == Severity.CRITICAL),
        high_count=sum(1 for f in result_findings if f.severity == Severity.HIGH),
        medium_count=sum(1 for f in result_findings if f.severity == Severity.MEDIUM),
        low_count=sum(1 for f in result_findings if f.severity == Severity.LOW),
        excluded_query_count=excluded_count,
    )


def run_analysis(
    snapshot: PerformanceSnapshot,
    thresholds: DetectorThresholds | None = None,
    scope: str = "server",
    database: str | None = None,
) -> AnalysisResult:
    """Run all detectors against a snapshot and return ordered findings."""
    t = thresholds or get_thresholds()

    detectors = [
        LongRunningDetector(thresholds=t),
        BlockingDetector(thresholds=t),
        HighCpuDetector(thresholds=t),
        HighIoDetector(thresholds=t),
    ]

    # Try to include regression detection
    historical_status = "available"
    try:
        from app.detectors.regression import QueryRegressionDetector
        detectors.append(QueryRegressionDetector(thresholds=t))
    except Exception:
        historical_status = "unavailable"
        logger.debug("Regression detector unavailable")

    all_findings: list[PerformanceFinding] = []
    excluded_queries: list[ExcludedQuery] = []
    for detector in detectors:
        try:
            findings = detector.detect(snapshot)
            all_findings.extend(findings)
            excluded_queries.extend(getattr(detector, "excluded_queries", []))
            logger.debug(
                "Detector %s produced %d finding(s)", detector.name, len(findings)
            )
        except Exception as exc:
            logger.error("Detector %s failed: %s", detector.name, str(exc))
            if "regression" in detector.name:
                historical_status = "unavailable"

    # Fold multi-signal findings together before sorting, so counts and ordering
    # reflect distinct problems rather than distinct detectors.
    correlated = _merge_correlated(all_findings)
    sorted_findings = _sort_findings(correlated)
    _add_drilldown(sorted_findings, scope)

    # Separate health findings from optimization opportunities.
    # An expensive cached query is an optimization opportunity, NOT evidence of
    # an active incident — it must never drive the health status on its own.
    health_findings = [f for f in sorted_findings if f.category in _HEALTH_CATEGORIES]
    optimization_findings = [f for f in sorted_findings if f.category in _OPTIMIZATION_CATEGORIES]

    # Overall status is driven by health findings only
    overall_status = determine_overall_status(health_findings)
    affected_dbs = _compute_affected_databases(sorted_findings) if scope == "server" else []

    deduplicated_exclusions = _deduplicate_exclusions(excluded_queries)
    if deduplicated_exclusions:
        logger.info(
            "Excluded %d monitoring/diagnostic quer(ies) from findings",
            len(deduplicated_exclusions),
        )

    return AnalysisResult(
        analysis_id=str(uuid.uuid4()),
        analyzed_at=datetime.now(UTC),
        server=snapshot.server_host,
        scope=scope,
        database=database,
        overall_status=overall_status,
        findings=sorted_findings,
        affected_databases=affected_dbs,
        total_findings=len(sorted_findings),
        critical_count=sum(1 for f in sorted_findings if f.severity == Severity.CRITICAL),
        high_count=sum(1 for f in sorted_findings if f.severity == Severity.HIGH),
        medium_count=sum(1 for f in sorted_findings if f.severity == Severity.MEDIUM),
        low_count=sum(1 for f in sorted_findings if f.severity == Severity.LOW),
        current_health=HealthSection(
            status=overall_status,
            findings=health_findings,
        ),
        optimization_opportunities=optimization_findings,
        historical_analysis_status=historical_status,
        summary=_build_summary(
            sorted_findings,
            health_findings,
            optimization_findings,
            overall_status,
            affected_dbs,
            len(deduplicated_exclusions),
        ),
        excluded_queries=deduplicated_exclusions,
    )


def analyze_performance(
    scope: str = "server",
    database: str | None = None,
    long_running_threshold_ms: int = 5000,
    top_n: int = 20,
    wait_stats_top_n: int = 25,
) -> AnalysisResult:
    """Collect a snapshot and run the full analysis pipeline."""
    logger.info("Starting performance analysis (scope=%s, db=%s)", scope, database)

    snapshot = collect_performance_snapshot(
        scope=scope,
        database=database,
        long_running_threshold_ms=long_running_threshold_ms,
        top_n=top_n,
        wait_stats_top_n=wait_stats_top_n,
    )

    # Persist snapshot for historical tracking
    try:
        from app.storage.history_service import persist_snapshot
        persist_snapshot(snapshot)
    except Exception as exc:
        logger.warning("Failed to persist snapshot: %s", str(exc))

    result = run_analysis(snapshot, scope=scope, database=database)
    logger.info(
        "Analysis complete: %s — %d findings (%d critical, %d high, %d medium, %d low)",
        result.overall_status.value,
        result.total_findings,
        result.critical_count,
        result.high_count,
        result.medium_count,
        result.low_count,
    )
    return result
