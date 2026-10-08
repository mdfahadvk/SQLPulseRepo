"""History service — persists snapshot data and manages lifecycle."""

import logging
import re
from datetime import UTC, datetime, timedelta

from app.agent.sanitizer import sanitize_text
from app.models.agent import (
    AuditToolCall,
    InvestigationAuditRecord,
    InvestigationResponse,
    ToolCall,
)
from app.models.history import QueryMetricRecord, SnapshotRecord
from app.models.performance import PerformanceSnapshot
from app.storage.repository import HistoryRepository
from app.storage.sqlite_repository import SQLiteHistoryRepository

logger = logging.getLogger(__name__)

_repository: HistoryRepository | None = None


def get_repository() -> HistoryRepository:
    """Return the global history repository singleton."""
    global _repository
    if _repository is None:
        _repository = SQLiteHistoryRepository()
        _repository.initialize()
    return _repository


def set_repository(repo: HistoryRepository) -> None:
    """Override the repository (for testing)."""
    global _repository
    _repository = repo


def persist_snapshot(snapshot: PerformanceSnapshot) -> None:
    """Extract query metrics from a snapshot and persist to storage.

    Silently handles errors so monitoring is never disrupted.
    """
    try:
        repo = get_repository()

        # Skip if already stored
        if repo.snapshot_exists(snapshot.snapshot_id):
            logger.debug("Snapshot %s already stored, skipping", snapshot.snapshot_id)
            return

        # Store snapshot record
        repo.store_snapshot(SnapshotRecord(
            snapshot_id=snapshot.snapshot_id,
            collected_at=snapshot.collected_at,
            server=snapshot.server_host,
            scope=snapshot.scope,
            database=snapshot.database,
        ))

        # Extract query metrics from top CPU and top I/O collectors
        metrics: list[QueryMetricRecord] = []

        if snapshot.top_cpu_queries and snapshot.top_cpu_queries.metadata.success:
            for q in snapshot.top_cpu_queries.queries:
                if not q.query_hash:
                    continue
                metrics.append(QueryMetricRecord(
                    snapshot_id=snapshot.snapshot_id,
                    collected_at=snapshot.collected_at,
                    server=snapshot.server_host,
                    database=q.database_name or snapshot.database or "unknown",
                    query_hash=q.query_hash,
                    sql_text_preview=(q.sql_text or "")[:200] or None,
                    execution_count=q.execution_count,
                    total_cpu_time_ms=q.total_cpu_time_ms,
                    avg_cpu_time_ms=q.avg_cpu_time_ms,
                    total_elapsed_time_ms=q.total_elapsed_time_ms,
                    avg_elapsed_time_ms=q.avg_elapsed_time_ms,
                    total_logical_reads=q.total_logical_reads,
                    avg_logical_reads=q.avg_logical_reads,
                ))

        if snapshot.top_io_queries and snapshot.top_io_queries.metadata.success:
            for q in snapshot.top_io_queries.queries:
                if not q.query_hash:
                    continue
                # Only add if not already present from CPU collector (same snapshot + hash)
                existing_hashes = {m.query_hash for m in metrics}
                if q.query_hash in existing_hashes:
                    # Merge IO data into existing record
                    for m in metrics:
                        if m.query_hash == q.query_hash:
                            m.total_physical_reads = q.total_physical_reads
                            m.avg_physical_reads = q.avg_physical_reads
                            m.total_logical_writes = q.total_logical_writes
                            m.avg_logical_writes = q.avg_logical_writes
                            # Update reads if IO has better data
                            if q.total_logical_reads > m.total_logical_reads:
                                m.total_logical_reads = q.total_logical_reads
                                m.avg_logical_reads = q.avg_logical_reads
                            break
                else:
                    metrics.append(QueryMetricRecord(
                        snapshot_id=snapshot.snapshot_id,
                        collected_at=snapshot.collected_at,
                        server=snapshot.server_host,
                        database=q.database_name or snapshot.database or "unknown",
                        query_hash=q.query_hash,
                        sql_text_preview=(q.sql_text or "")[:200] or None,
                        execution_count=q.execution_count,
                        total_logical_reads=q.total_logical_reads,
                        avg_logical_reads=q.avg_logical_reads,
                        total_physical_reads=q.total_physical_reads,
                        avg_physical_reads=q.avg_physical_reads,
                        total_logical_writes=q.total_logical_writes,
                        avg_logical_writes=q.avg_logical_writes,
                        total_elapsed_time_ms=q.total_elapsed_time_ms,
                    ))

        if metrics:
            repo.store_query_metrics(metrics)
            logger.info(
                "Persisted %d query metrics from snapshot %s",
                len(metrics), snapshot.snapshot_id,
            )

    except Exception:
        logger.exception("Failed to persist snapshot %s (non-fatal)", snapshot.snapshot_id)


def run_retention_cleanup(retention_days: int = 30) -> int:
    """Delete historical records older than retention_days. Returns deleted count."""
    try:
        repo = get_repository()
        cutoff = datetime.now(UTC) - timedelta(days=retention_days)
        return repo.delete_older_than(cutoff)
    except Exception:
        logger.exception("Retention cleanup failed (non-fatal)")
        return 0


# ─── Audit trail ──────────────────────────────────────────────────────────

# Regex to extract ISO 8601 timestamps from the question text
_ISO_DATETIME_RE = re.compile(
    r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2})?"
)

# Regex to extract query hashes (0x...) from text
_QUERY_HASH_RE = re.compile(r"0x[0-9A-Fa-f]{4,}")


def _extract_time_range(text: str) -> tuple[str | None, str | None]:
    """Best-effort extraction of ISO timestamps from question text."""
    matches = _ISO_DATETIME_RE.findall(text)
    # findall with groups returns the group, not the full match
    full_matches = [m.group() for m in _ISO_DATETIME_RE.finditer(text)]
    if len(full_matches) >= 2:
        return full_matches[0], full_matches[1]
    if len(full_matches) == 1:
        return full_matches[0], None
    return None, None


def _extract_finding_references(
    response: InvestigationResponse,
) -> list[str]:
    """Extract query hashes and procedure names from the investigation response."""
    refs: list[str] = []
    if response.affected_query:
        refs.append(response.affected_query)
    # Scan evidence for query hashes
    for item in response.evidence:
        for m in _QUERY_HASH_RE.finditer(item):
            if m.group() not in refs:
                refs.append(m.group())
    return refs


def build_audit_record(
    response: InvestigationResponse,
    tool_calls: list[ToolCall],
    server: str,
) -> InvestigationAuditRecord:
    """Build a sanitized audit record from an investigation response.

    Strips credentials from the question and answer before storage.
    Never stores SQL credentials, API keys, or connection strings.
    """
    question_safe = sanitize_text(response.question)
    answer_safe = sanitize_text(response.answer)

    time_start, time_end = _extract_time_range(response.question)

    audit_tools = [
        AuditToolCall(
            tool_name=tc.tool_name,
            arguments_sanitized=tc.arguments_sanitized,
            result_summary=tc.result_summary,
            success=tc.success,
            error=tc.error,
            duration_seconds=tc.duration_seconds,
        )
        for tc in tool_calls
    ]

    finding_refs = _extract_finding_references(response)

    return InvestigationAuditRecord(
        investigation_id=response.investigation_id,
        created_at=response.timestamp,
        question_sanitized=question_safe,
        server=server,
        database=response.affected_database,
        time_range_start=time_start,
        time_range_end=time_end,
        tools_called=audit_tools,
        finding_references=finding_refs,
        severity=response.severity,
        confidence=response.confidence,
        answer=answer_safe,
        human_review_required=response.human_review_required,
        remediation_performed=False,
    )


def persist_investigation_audit(
    response: InvestigationResponse,
    tool_calls: list[ToolCall],
    server: str,
) -> None:
    """Persist an investigation audit record. Never disrupts the caller."""
    try:
        repo = get_repository()
        record = build_audit_record(response, tool_calls, server)
        repo.store_investigation_audit(record)
        logger.info(
            "Persisted audit for investigation %s", response.investigation_id,
        )
    except Exception:
        logger.exception(
            "Failed to persist investigation audit %s (non-fatal)",
            response.investigation_id,
        )


def get_investigation_audit(investigation_id: str) -> InvestigationAuditRecord | None:
    """Retrieve a single investigation audit by ID."""
    repo = get_repository()
    return repo.get_investigation_audit(investigation_id)


def list_investigation_audits(limit: int = 50) -> list[InvestigationAuditRecord]:
    """List recent investigation audits, newest first."""
    repo = get_repository()
    return repo.list_investigation_audits(limit=limit)
