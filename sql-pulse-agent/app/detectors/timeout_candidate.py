"""Slow-query and timeout candidate detector.

Identifies queries from Query Store history whose execution duration exceeded
the slow-query monitoring threshold, and separately determines whether they
also exceeded an explicitly configured application timeout.

Two thresholds, two different claims:

  SLOW_QUERY_CANDIDATE
      max_duration >= slow monitoring threshold.  Means "worth looking at".
      The threshold is chosen by whoever is monitoring and is arbitrary.

  TIMEOUT_CANDIDATE
      max_duration >= an *explicitly configured* application timeout.  Means
      "the client could plausibly have given up".

The second is a much stronger claim and is only made when the application's
timeout is actually known.  A monitoring filter of 5,000 ms says nothing about
whether the application waits 30 seconds before abandoning a request, so the
slow threshold is never reused as a timeout threshold.  When no application
timeout is configured, timeout classification is reported as unavailable
rather than guessed.

Neither label is a confirmed timeout: proving a client actually abandoned a
request requires application telemetry SQLPulse does not have.

When sufficient SQLPulse historical baselines exist for a candidate query,
the detector enriches the result with a baseline comparison showing the
historical mean, median, P95, standard deviation, and regression multipliers.

Aggregation:
  When group_results is True (default), duplicate Query Store intervals for
  the same query/plan are merged into a single candidate.  Grouping key:
  (query_hash, query_id, plan_id, object_id).  Averages are weighted by
  execution_count; max/min are preserved across intervals.

Workload classification:
  See ``classify_workload`` — only strong evidence decides a workload type;
  naming conventions alone are never sufficient.

Ranking:
  Candidates are ranked by a composite score derived from max_duration vs
  threshold ratio, intervals exceeding threshold, baseline regression,
  CPU impact, and workload type.
"""

import logging
import re
import uuid
from collections import defaultdict
from datetime import UTC, datetime

from app.models.findings import (
    Confidence,
    FindingCategory,
    PerformanceFinding,
    Severity,
)
from app.models.history import BaselineStatus
from app.models.slow_history import (
    BaselineComparison,
    CandidateClassification,
    EvidenceStrength,
    SlowQueryRecord,
    TimeoutCandidate,
    WorkloadEvidence,
    WorkloadType,
)
from app.storage.baseline_service import get_baseline_for_regression

logger = logging.getLogger(__name__)


# ─── Workload Classification Patterns ─────────────────────────────────────
#
# Evidence is split by how much it can actually prove.
#
# STRONG evidence identifies *what the connection is* (program_name, SQL Agent
# job context) or *what the statement fundamentally does* (BACKUP, DBCC).
# These cannot reasonably belong to the other category.
#
# WEAK evidence is naming convention and statement shape.  A procedure called
# "uspGetOrders" is probably application code, but "usp" is a convention a DBA
# also uses for maintenance helpers, and a SELECT appears in literally every
# kind of workload.  Weak evidence is recorded but never decides.

# program_name values SQL Server Agent uses for job steps.
_AGENT_PROGRAM_PATTERNS: list[re.Pattern[str]] = [
    re.compile(p, re.IGNORECASE)
    for p in (
        r"SQLAgent",
        r"SQL\s*Server\s*Agent",
        r"SQLAGENT",
    )
]

# program_name values that identify an application or an interactive client.
# These are not SQL Agent, so they establish the connection is application-side.
_APPLICATION_PROGRAM_PATTERNS: list[re.Pattern[str]] = [
    re.compile(p, re.IGNORECASE)
    for p in (
        r"\.Net\s*SqlClient",
        r"Microsoft\.Data\.SqlClient",
        r"EntityFramework",
        r"\bJDBC\b",
        r"jTDS",
        r"go-mssqldb",
        r"python|pyodbc|SQLAlchemy",
        r"node-mssql|tedious",
        r"Microsoft SQL Server Management Studio",
        r"Azure Data Studio",
        r"\bDBeaver\b",
        r"\bsqlcmd\b",
    )
]

# Statements whose purpose is maintenance regardless of who issued them.
_MAINTENANCE_STATEMENT_PATTERNS: list[re.Pattern[str]] = [
    re.compile(p, re.IGNORECASE)
    for p in (
        r"\bDBCC\b",
        r"\bBACKUP\s+(DATABASE|LOG)\b",
        r"\bRESTORE\s+(DATABASE|LOG)\b",
        r"\bALTER\s+INDEX\b.*\b(REBUILD|REORGANIZE)\b",
        r"\bUPDATE\s+STATISTICS\b",
        r"\bsp_updatestats\b",
        r"\bsp_recompile\b",
        r"\bDBCC\s+SHRINK",
        r"\bALTER\s+DATABASE\b.*\bSET\b",
    )
]

# Naming conventions — suggestive only.  Never decisive in either direction.
_WEAK_APPLICATION_NAME_PATTERNS: list[re.Pattern[str]] = [
    re.compile(p, re.IGNORECASE)
    for p in (
        r"\busp[A-Z_]",
        r"\bproc[A-Z_]",
        r"\bsp[A-Z][a-z]",
    )
]

_WEAK_BACKGROUND_NAME_PATTERNS: list[re.Pattern[str]] = [
    re.compile(p, re.IGNORECASE)
    for p in (
        r"maintenance",
        r"cleanup",
        r"retention",
        r"purge",
        r"archive",
        r"\bnightly\b",
        r"\bbatch\b",
    )
]

# Statement shapes present in every kind of workload.
_WEAK_STATEMENT_PATTERNS: list[re.Pattern[str]] = [
    re.compile(p, re.IGNORECASE | re.DOTALL)
    for p in (
        r"\bSELECT\b.*\bFROM\b",
        r"\bINSERT\b.*\bINTO\b",
        r"\bUPDATE\b.*\bSET\b",
        r"\bDELETE\b.*\bFROM\b",
        r"\bEXEC(UTE)?\b",
    )
]


# ─── Aggregation ──────────────────────────────────────────────────────────


def aggregate_slow_records(
    records: list[SlowQueryRecord],
    threshold_ms: float,
) -> list[SlowQueryRecord]:
    """Merge duplicate Query Store intervals into a single record per query/plan.

    Grouping key: (query_hash, query_id, plan_id, object_id).
    Averages are weighted by execution_count; max/min are preserved.

    Args:
        records: Raw Query Store records (may contain duplicates across intervals).
        threshold_ms: Threshold used to count intervals_exceeding_threshold.

    Returns:
        De-duplicated list of SlowQueryRecord with interval tracking fields set.
    """
    groups: dict[tuple, list[SlowQueryRecord]] = defaultdict(list)
    for rec in records:
        key = (rec.query_hash, rec.query_id, rec.plan_id, rec.object_id)
        groups[key].append(rec)

    aggregated: list[SlowQueryRecord] = []
    for _key, group in groups.items():
        if len(group) == 1:
            rec = group[0]
            rec.intervals_observed = 1
            rec.intervals_exceeding_threshold = (
                1 if rec.max_duration_ms >= threshold_ms else 0
            )
            aggregated.append(rec)
            continue

        total_exec = sum(r.execution_count for r in group)
        weight = total_exec if total_exec > 0 else 1

        def _weighted_avg(attr: str) -> float:
            return (
                sum(getattr(r, attr) * r.execution_count for r in group) / weight
            )

        first = group[0]
        merged = SlowQueryRecord(
            query_id=first.query_id,
            query_sql_text=first.query_sql_text,
            query_hash=first.query_hash,
            object_id=first.object_id,
            procedure_name=first.procedure_name,
            plan_id=first.plan_id,
            execution_count=total_exec,
            avg_duration_ms=_weighted_avg("avg_duration_ms"),
            max_duration_ms=max(r.max_duration_ms for r in group),
            min_duration_ms=min(r.min_duration_ms for r in group),
            avg_cpu_time_ms=_weighted_avg("avg_cpu_time_ms"),
            max_cpu_time_ms=max(r.max_cpu_time_ms for r in group),
            avg_logical_reads=_weighted_avg("avg_logical_reads"),
            max_logical_reads=max(r.max_logical_reads for r in group),
            avg_physical_reads=_weighted_avg("avg_physical_reads"),
            first_execution_time=min(
                (r.first_execution_time for r in group if r.first_execution_time),
                default=None,
            ),
            last_execution_time=max(
                (r.last_execution_time for r in group if r.last_execution_time),
                default=None,
            ),
            interval_start_time=min(
                (r.interval_start_time for r in group if r.interval_start_time),
                default=None,
            ),
            intervals_observed=len(group),
            intervals_exceeding_threshold=sum(
                1 for r in group if r.max_duration_ms >= threshold_ms
            ),
        )
        aggregated.append(merged)

    return aggregated


# ─── Workload Classification ─────────────────────────────────────────────


def classify_workload(
    sql_text: str | None,
    procedure_name: str | None,
    *,
    program_name: str | None = None,
    host_name: str | None = None,
    is_scheduled_job: bool | None = None,
    application_programs: tuple[str, ...] | None = None,
    background_programs: tuple[str, ...] | None = None,
) -> tuple[WorkloadType, list[str]]:
    """Classify a query's workload type from the available evidence.

    Only STRONG evidence decides the outcome.  Naming conventions and generic
    statement shapes are gathered and reported, but they cannot produce a
    classification on their own: a procedure named ``uspRebuildIndexes`` follows
    the "application" convention while doing maintenance, and a plain SELECT
    appears in every workload there is.  Guessing from those would attach a
    confident-looking label to a coin flip.

    When no strong evidence is available the answer is UNKNOWN, with the weak
    signals listed so a reader can see what was considered.

    Args:
        sql_text: The query text, if known.
        procedure_name: The owning procedure, if known.
        program_name: Session ``program_name`` / ``application_name``.
        host_name: Session host, recorded as supporting context.
        is_scheduled_job: True when the caller knows this ran as a scheduled job.
        application_programs: Configured program names meaning "application".
        background_programs: Configured program names meaning "background".

    Returns:
        ``(workload_type, evidence)`` where evidence is a human-readable list
        with each entry prefixed by its strength.
    """
    evidence = _gather_workload_evidence(
        sql_text,
        procedure_name,
        program_name=program_name,
        host_name=host_name,
        is_scheduled_job=is_scheduled_job,
        application_programs=application_programs,
        background_programs=background_programs,
    )
    workload_type = resolve_workload_type(evidence)
    return workload_type, [_format_evidence(e) for e in evidence]


def resolve_workload_type(evidence: list[WorkloadEvidence]) -> WorkloadType:
    """Decide a workload type from gathered evidence.

    Strong background evidence wins over strong application evidence: an
    application connection running BACKUP DATABASE is still doing maintenance,
    so the operation matters more than the caller.
    """
    strong = [e for e in evidence if e.strength is EvidenceStrength.STRONG]

    if any(e.supports is WorkloadType.BACKGROUND_OR_SCHEDULED for e in strong):
        return WorkloadType.BACKGROUND_OR_SCHEDULED

    if any(e.supports is WorkloadType.APPLICATION_OR_INTERACTIVE for e in strong):
        return WorkloadType.APPLICATION_OR_INTERACTIVE

    return WorkloadType.UNKNOWN


def _format_evidence(item: WorkloadEvidence) -> str:
    return f"{item.strength.value}: [{item.source}] {item.detail}"


def _gather_workload_evidence(
    sql_text: str | None,
    procedure_name: str | None,
    *,
    program_name: str | None = None,
    host_name: str | None = None,
    is_scheduled_job: bool | None = None,
    application_programs: tuple[str, ...] | None = None,
    background_programs: tuple[str, ...] | None = None,
) -> list[WorkloadEvidence]:
    """Collect every workload signal available, tagged by strength."""
    evidence: list[WorkloadEvidence] = []

    # ── Strong: explicit scheduled-job knowledge ──
    if is_scheduled_job is True:
        evidence.append(WorkloadEvidence(
            strength=EvidenceStrength.STRONG,
            source="session",
            detail="Caller reported this execution ran as a scheduled job",
            supports=WorkloadType.BACKGROUND_OR_SCHEDULED,
        ))
    elif is_scheduled_job is False:
        evidence.append(WorkloadEvidence(
            strength=EvidenceStrength.STRONG,
            source="session",
            detail="Caller reported this execution did not run as a scheduled job",
            supports=WorkloadType.APPLICATION_OR_INTERACTIVE,
        ))

    # ── Strong: program_name / application_name ──
    if program_name:
        evidence.extend(_classify_program_name(
            program_name,
            application_programs or (),
            background_programs or (),
        ))
    else:
        evidence.append(WorkloadEvidence(
            strength=EvidenceStrength.WEAK,
            source="session",
            detail="No program_name/application_name available for this query",
            supports=WorkloadType.UNKNOWN,
        ))

    if host_name:
        evidence.append(WorkloadEvidence(
            strength=EvidenceStrength.WEAK,
            source="session",
            detail=f"Executed from host '{host_name}'",
            supports=WorkloadType.UNKNOWN,
        ))

    # ── Strong: the statement's own purpose ──
    combined = " ".join(filter(None, [sql_text, procedure_name]))
    if sql_text:
        for pat in _MAINTENANCE_STATEMENT_PATTERNS:
            if pat.search(sql_text):
                evidence.append(WorkloadEvidence(
                    strength=EvidenceStrength.STRONG,
                    source="sql_text",
                    detail=f"Maintenance statement matched /{pat.pattern}/",
                    supports=WorkloadType.BACKGROUND_OR_SCHEDULED,
                ))

    # ── Weak: naming conventions ──
    if procedure_name:
        for pat in _WEAK_APPLICATION_NAME_PATTERNS:
            if pat.search(procedure_name):
                evidence.append(WorkloadEvidence(
                    strength=EvidenceStrength.WEAK,
                    source="procedure_name",
                    detail=(
                        f"Name matches application naming convention "
                        f"/{pat.pattern}/ — a convention, not proof of caller"
                    ),
                    supports=WorkloadType.APPLICATION_OR_INTERACTIVE,
                ))
        for pat in _WEAK_BACKGROUND_NAME_PATTERNS:
            if pat.search(procedure_name):
                evidence.append(WorkloadEvidence(
                    strength=EvidenceStrength.WEAK,
                    source="procedure_name",
                    detail=(
                        f"Name suggests maintenance work /{pat.pattern}/ — "
                        f"a naming hint, not proof of caller"
                    ),
                    supports=WorkloadType.BACKGROUND_OR_SCHEDULED,
                ))

    # ── Weak: generic statement shape ──
    if sql_text:
        for pat in _WEAK_STATEMENT_PATTERNS:
            if pat.search(sql_text):
                evidence.append(WorkloadEvidence(
                    strength=EvidenceStrength.WEAK,
                    source="sql_text",
                    detail=(
                        f"Generic statement shape /{pat.pattern}/ — occurs in "
                        f"application and background workloads alike"
                    ),
                    supports=WorkloadType.UNKNOWN,
                ))
                break

    if not combined.strip() and not program_name:
        evidence.append(WorkloadEvidence(
            strength=EvidenceStrength.WEAK,
            source="none",
            detail="No SQL text, procedure name, or session metadata available",
            supports=WorkloadType.UNKNOWN,
        ))

    return evidence


def _classify_program_name(
    program_name: str,
    application_programs: tuple[str, ...],
    background_programs: tuple[str, ...],
) -> list[WorkloadEvidence]:
    """Derive workload evidence from a session program/application name."""
    # Operator-configured mappings are the most authoritative signal available:
    # someone who knows the environment stated what this program is.
    for configured in background_programs:
        if configured.lower() in program_name.lower():
            return [WorkloadEvidence(
                strength=EvidenceStrength.STRONG,
                source="program_name",
                detail=(
                    f"program_name '{program_name}' matches configured "
                    f"background program '{configured}'"
                ),
                supports=WorkloadType.BACKGROUND_OR_SCHEDULED,
            )]

    for configured in application_programs:
        if configured.lower() in program_name.lower():
            return [WorkloadEvidence(
                strength=EvidenceStrength.STRONG,
                source="program_name",
                detail=(
                    f"program_name '{program_name}' matches configured "
                    f"application program '{configured}'"
                ),
                supports=WorkloadType.APPLICATION_OR_INTERACTIVE,
            )]

    for pat in _AGENT_PROGRAM_PATTERNS:
        if pat.search(program_name):
            return [WorkloadEvidence(
                strength=EvidenceStrength.STRONG,
                source="program_name",
                detail=(
                    f"program_name '{program_name}' identifies a SQL Server "
                    f"Agent job step"
                ),
                supports=WorkloadType.BACKGROUND_OR_SCHEDULED,
            )]

    for pat in _APPLICATION_PROGRAM_PATTERNS:
        if pat.search(program_name):
            return [WorkloadEvidence(
                strength=EvidenceStrength.STRONG,
                source="program_name",
                detail=(
                    f"program_name '{program_name}' identifies an application "
                    f"or interactive client"
                ),
                supports=WorkloadType.APPLICATION_OR_INTERACTIVE,
            )]

    return [WorkloadEvidence(
        strength=EvidenceStrength.WEAK,
        source="program_name",
        detail=(
            f"program_name '{program_name}' is not a recognised application "
            f"or agent program"
        ),
        supports=WorkloadType.UNKNOWN,
    )]


# ─── Ranking ──────────────────────────────────────────────────────────────


def _compute_rank_score(candidate: TimeoutCandidate, threshold_ms: float) -> float:
    """Compute a composite ranking score (higher = worse / more urgent).

    Signals:
      - max_duration vs threshold ratio (weight 40%)
      - intervals exceeding threshold (weight 20%)
      - baseline regression multiplier (weight 20%)
      - CPU impact ratio (weight 10%)
      - workload type boost (weight 10%) — application queries rank higher
    """
    # Duration ratio: how many times above threshold
    duration_ratio = (
        candidate.max_duration_ms / threshold_ms if threshold_ms > 0 else 1.0
    )

    # Interval exceedance ratio
    interval_ratio = (
        candidate.intervals_exceeding_threshold / candidate.intervals_observed
        if candidate.intervals_observed > 0
        else 0.0
    )

    # Baseline regression
    regression = 0.0
    if (
        candidate.baseline_comparison
        and candidate.baseline_comparison.baseline_status == "READY"
    ):
        regression = candidate.baseline_comparison.regression_multiplier_vs_mean

    # CPU impact: cpu / duration (high CPU relative to wall clock = CPU bound)
    cpu_ratio = (
        candidate.max_cpu_ms / candidate.max_duration_ms
        if candidate.max_duration_ms > 0
        else 0.0
    )

    # Workload boost: application queries are more impactful
    workload_boost = {
        WorkloadType.APPLICATION_OR_INTERACTIVE: 1.2,
        WorkloadType.BACKGROUND_OR_SCHEDULED: 0.8,
        WorkloadType.UNKNOWN: 1.0,
    }.get(candidate.workload_type, 1.0)

    score = (
        duration_ratio * 0.4
        + interval_ratio * 0.2
        + regression * 0.2
        + cpu_ratio * 0.1
    ) * workload_boost

    return round(score, 4)


def rank_candidates(
    candidates: list[TimeoutCandidate], threshold_ms: float
) -> list[TimeoutCandidate]:
    """Assign rank (1 = worst) to candidates by composite score and return sorted list."""
    scored = [
        (_compute_rank_score(c, threshold_ms), c) for c in candidates
    ]
    scored.sort(key=lambda t: t[0], reverse=True)
    for idx, (_score, cand) in enumerate(scored, start=1):
        cand.rank = idx
    return [c for _, c in scored]


# ─── Timeout Candidate Builder ────────────────────────────────────────────

def _classification_settings() -> tuple[tuple[str, ...], tuple[str, ...]]:
    """Return configured (application_programs, background_programs).

    Configuration problems must not break detection, so an unreadable settings
    object degrades to "no configured mappings" rather than raising.
    """
    try:
        from app.config import get_settings
        settings = get_settings()
        return settings.application_program_names, settings.background_program_names
    except Exception:
        logger.debug("Workload program mappings unavailable", exc_info=True)
        return (), ()


def classify_candidate(
    max_duration_ms: float,
    slow_threshold_ms: float,
    application_timeout_ms: float | None,
) -> tuple[CandidateClassification, str]:
    """Decide whether an observation is merely slow or a timeout candidate.

    ``application_timeout_ms`` of None means the application's timeout is not
    configured.  In that case the query stays a SLOW_QUERY_CANDIDATE: the
    monitoring threshold describes what an operator asked to see, not what the
    client tolerates, so promoting on it would invent a timeout.
    """
    slow_sec = slow_threshold_ms / 1000.0
    observed_sec = max_duration_ms / 1000.0

    if application_timeout_ms is None:
        return (
            CandidateClassification.SLOW_QUERY_CANDIDATE,
            f"Exceeded the {slow_sec:.3g}s slow-query monitoring threshold "
            f"({observed_sec:.3g}s observed). No application timeout is "
            f"configured, so timeout classification is unavailable.",
        )

    timeout_sec = application_timeout_ms / 1000.0

    if max_duration_ms >= application_timeout_ms:
        return (
            CandidateClassification.TIMEOUT_CANDIDATE,
            f"Reached {observed_sec:.3g}s, at or above the configured "
            f"{timeout_sec:.3g}s application timeout.",
        )

    return (
        CandidateClassification.SLOW_QUERY_CANDIDATE,
        f"Exceeded the {slow_sec:.3g}s slow-query monitoring threshold "
        f"({observed_sec:.3g}s observed) but stayed below the configured "
        f"{timeout_sec:.3g}s application timeout.",
    )


def build_timeout_candidates(
    slow_records: list[SlowQueryRecord],
    threshold_ms: float,
    database: str,
    *,
    group_results: bool = True,
    workload_type_filter: WorkloadType | None = None,
    application_timeout_ms: float | None = None,
) -> list[TimeoutCandidate]:
    """Convert slow query records into classified candidates.

    Args:
        slow_records: Query Store records from the time window.
        threshold_ms: Slow-query monitoring threshold in milliseconds. Controls
            which records are returned at all.
        database: Target database name for baseline lookups.
        group_results: When True (default), aggregate duplicate intervals.
        workload_type_filter: When set, only return candidates of this type.
        application_timeout_ms: The application's configured command timeout.
            None (default) means unknown — candidates are then never promoted
            to TIMEOUT_CANDIDATE, because the monitoring threshold is not a
            substitute for knowing what the client tolerates.

    Returns:
        Candidates whose max_duration >= threshold_ms, enriched with baseline
        data, confidence/severity, workload classification, and rank.
    """
    # Step 1: optionally aggregate duplicate intervals
    if group_results:
        records = aggregate_slow_records(slow_records, threshold_ms)
    else:
        records = slow_records

    candidates: list[TimeoutCandidate] = []
    settings = _classification_settings()

    for rec in records:
        if rec.max_duration_ms < threshold_ms:
            continue

        baseline = _compare_with_baseline(rec, database)
        confidence = _compute_confidence(rec, threshold_ms, baseline)
        severity = _compute_severity(rec, threshold_ms, baseline)

        workload_evidence = _gather_workload_evidence(
            rec.query_sql_text,
            rec.procedure_name,
            program_name=rec.program_name,
            host_name=rec.host_name,
            is_scheduled_job=rec.is_scheduled_job,
            application_programs=settings[0],
            background_programs=settings[1],
        )
        wl_type = resolve_workload_type(workload_evidence)

        classification, reason = classify_candidate(
            rec.max_duration_ms, threshold_ms, application_timeout_ms,
        )

        candidate = TimeoutCandidate(
            label=classification.value.lower(),
            classification=classification,
            slow_query_candidate=True,
            timeout_candidate=(
                classification is CandidateClassification.TIMEOUT_CANDIDATE
            ),
            slow_threshold_ms=threshold_ms,
            application_timeout_ms=application_timeout_ms,
            classification_reason=reason,
            query_id=rec.query_id,
            query_hash=rec.query_hash,
            object_id=rec.object_id,
            procedure_name=rec.procedure_name,
            plan_id=rec.plan_id,
            sql_text_preview=(rec.query_sql_text or "")[:200] or None,
            execution_count=rec.execution_count,
            total_execution_count=rec.execution_count,
            intervals_observed=rec.intervals_observed,
            intervals_exceeding_threshold=rec.intervals_exceeding_threshold,
            avg_duration_ms=round(rec.avg_duration_ms, 1),
            max_duration_ms=round(rec.max_duration_ms, 1),
            min_duration_ms=round(rec.min_duration_ms, 1),
            avg_cpu_ms=round(rec.avg_cpu_time_ms, 1),
            max_cpu_ms=round(rec.max_cpu_time_ms, 1),
            avg_logical_reads=round(rec.avg_logical_reads, 1),
            max_logical_reads=round(rec.max_logical_reads, 1),
            first_execution_time=rec.first_execution_time,
            last_execution_time=rec.last_execution_time,
            baseline_comparison=baseline,
            confidence=confidence.value,
            severity=severity.value,
            workload_type=wl_type,
            classification_evidence=[
                _format_evidence(e) for e in workload_evidence
            ],
            workload_evidence=workload_evidence,
        )
        candidates.append(candidate)

    # Step 2: filter by workload type if requested
    if workload_type_filter is not None:
        candidates = [
            c for c in candidates if c.workload_type == workload_type_filter
        ]

    # Step 3: rank and sort
    candidates = rank_candidates(candidates, threshold_ms)
    return candidates


def build_timeout_findings(
    candidates: list[TimeoutCandidate],
    threshold_ms: float,
    database: str,
    server: str,
) -> list[PerformanceFinding]:
    """Generate PerformanceFinding objects from slow/timeout candidates.

    The finding category and wording follow each candidate's own
    classification, so a merely-slow query is never described as having
    exceeded a timeout it was never measured against.
    """
    findings: list[PerformanceFinding] = []
    now = datetime.now(UTC)

    for cand in candidates:
        max_sec = cand.max_duration_ms / 1000.0
        slow_sec = (cand.slow_threshold_ms or threshold_ms) / 1000.0
        is_timeout = cand.classification is CandidateClassification.TIMEOUT_CANDIDATE

        evidence = [
            f"Maximum duration: {max_sec:.1f} seconds",
            f"Slow-query monitoring threshold: {slow_sec:.3g} seconds",
            f"Average duration: {cand.avg_duration_ms / 1000.0:.1f} seconds",
            f"Execution count: {cand.execution_count}",
        ]
        if cand.application_timeout_ms is not None:
            evidence.append(
                f"Configured application timeout: "
                f"{cand.application_timeout_ms / 1000.0:.3g} seconds"
            )
        else:
            evidence.append(
                "Configured application timeout: not configured — timeout "
                "classification unavailable"
            )
        evidence.append(f"Classification: {cand.classification.value}")
        if cand.classification_reason:
            evidence.append(cand.classification_reason)
        evidence.append(f"Workload type: {cand.workload_type.value}")

        if cand.baseline_comparison and cand.baseline_comparison.baseline_status == "READY":
            bc = cand.baseline_comparison
            evidence.append(f"Historical mean: {bc.historical_mean_ms / 1000.0:.1f} seconds")
            evidence.append(f"Historical P95: {bc.historical_p95_ms / 1000.0:.1f} seconds")
            evidence.append(f"Regression vs mean: {bc.regression_multiplier_vs_mean:.1f}x")

        if cand.procedure_name:
            identity = cand.procedure_name
        elif cand.query_hash:
            identity = f"Query (hash {cand.query_hash})"
        else:
            identity = f"Query (id {cand.query_id})"

        if is_timeout:
            timeout_sec = (cand.application_timeout_ms or 0) / 1000.0
            summary = (
                f"{identity} reached {max_sec:.1f} seconds, at or above the "
                f"configured {timeout_sec:.3g}-second application timeout."
            )
        else:
            summary = (
                f"{identity} reached {max_sec:.1f} seconds, exceeding the "
                f"{slow_sec:.3g}-second slow-query monitoring threshold."
            )

        findings.append(PerformanceFinding(
            finding_id=str(uuid.uuid4()),
            category=(
                FindingCategory.TIMEOUT_CANDIDATE
                if is_timeout
                else FindingCategory.SLOW_QUERY_CANDIDATE
            ),
            server=server,
            database=database,
            severity=Severity(cand.severity),
            confidence=Confidence(cand.confidence),
            first_observed=cand.first_execution_time or now,
            last_observed=cand.last_execution_time or now,
            summary=summary,
            evidence=evidence,
            likely_cause=(
                "The query experienced a significant performance regression or encountered "
                "resource contention. The specific root cause requires further investigation."
            ),
            recommendation=(
                "Review the execution plan, blocking history, CPU, I/O, and parameter/input "
                "patterns during the affected period."
            ),
            validation_steps=[
                "Check execution plan for regressions",
                "Review blocking and wait statistics during the period",
                "Check for parameter sniffing or data skew",
                "Verify index maintenance status",
            ] + ([] if cand.application_timeout_ms is not None else [
                "Configure the application's command timeout so timeout "
                "candidates can be distinguished from merely slow queries",
            ]),
            human_review_required=True,
        ))

    return findings


def _compare_with_baseline(
    record: SlowQueryRecord, database: str
) -> BaselineComparison | None:
    """Attempt to compare a query against its SQLPulse historical baseline.

    Returns None when the query has no hash (cannot look up baseline).
    Returns a BaselineComparison with INSUFFICIENT_DATA if not enough history.
    Returns a full comparison with READY status when baseline is available.
    """
    if not record.query_hash:
        return None

    try:
        baseline = get_baseline_for_regression(
            record.query_hash, database, window="7d", min_samples=5
        )
        if baseline is None:
            return BaselineComparison(baseline_status="INSUFFICIENT_DATA")

        elapsed = baseline.avg_elapsed_ms
        if elapsed is None:
            return BaselineComparison(baseline_status="INSUFFICIENT_DATA")

        mean = elapsed.mean
        median = elapsed.median
        p95 = elapsed.p95
        std_dev = elapsed.standard_deviation

        mult_mean = round(record.max_duration_ms / mean, 1) if mean > 0 else 0.0
        mult_p95 = round(record.max_duration_ms / p95, 1) if p95 > 0 else 0.0

        return BaselineComparison(
            baseline_status=BaselineStatus.READY.value,
            sample_count=baseline.sample_count,
            historical_mean_ms=round(mean, 1),
            historical_median_ms=round(median, 1),
            historical_p95_ms=round(p95, 1),
            standard_deviation_ms=round(std_dev, 1),
            regression_multiplier_vs_mean=mult_mean,
            regression_multiplier_vs_p95=mult_p95,
        )
    except Exception:
        logger.debug("Baseline comparison unavailable for %s", record.query_hash)
        return None


def _compute_confidence(
    record: SlowQueryRecord,
    threshold_ms: float,
    baseline: BaselineComparison | None,
) -> Confidence:
    """Compute confidence deterministically from measurable evidence.

    HIGH: Exceeded threshold repeatedly AND baseline shows normally fast
    MEDIUM: Exceeded threshold once OR no baseline available
    LOW: Duration near threshold AND noisy baseline (high std dev)
    """
    exceeded_margin = record.max_duration_ms / threshold_ms if threshold_ms > 0 else 1.0

    has_baseline = (
        baseline is not None
        and baseline.baseline_status == "READY"
    )
    baseline_shows_fast = (
        has_baseline
        and baseline.historical_mean_ms > 0
        and baseline.regression_multiplier_vs_mean >= 3.0
    )
    baseline_noisy = (
        has_baseline
        and baseline.historical_mean_ms > 0
        and baseline.standard_deviation_ms / baseline.historical_mean_ms > 1.0
    )

    # Repeated executions that exceeded threshold
    repeated = record.execution_count >= 3

    # HIGH: repeated + baseline shows normally fast
    if repeated and baseline_shows_fast:
        return Confidence.HIGH

    # LOW: barely exceeded threshold + noisy baseline
    if exceeded_margin < 1.5 and baseline_noisy:
        return Confidence.LOW

    # LOW: barely exceeded threshold + no baseline
    if exceeded_margin < 1.2:
        return Confidence.LOW

    # MEDIUM: default — exceeded once or no baseline
    return Confidence.MEDIUM


def _compute_severity(
    record: SlowQueryRecord,
    threshold_ms: float,
    baseline: BaselineComparison | None,
) -> Severity:
    """Compute severity from how far the query exceeded the threshold.

    CRITICAL: >3x threshold OR regression >8x baseline mean
    HIGH: >1.5x threshold OR regression >4x baseline mean
    MEDIUM: exceeded threshold but within 1.5x
    """
    multiplier = record.max_duration_ms / threshold_ms if threshold_ms > 0 else 1.0

    baseline_regression = 0.0
    if baseline and baseline.baseline_status == "READY" and baseline.regression_multiplier_vs_mean > 0:
        baseline_regression = baseline.regression_multiplier_vs_mean

    if multiplier >= 3.0 or baseline_regression >= 8.0:
        return Severity.CRITICAL
    if multiplier >= 1.5 or baseline_regression >= 4.0:
        return Severity.HIGH
    return Severity.MEDIUM
