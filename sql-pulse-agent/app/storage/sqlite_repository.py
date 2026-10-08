"""SQLite implementation of the HistoryRepository."""

import contextlib
import json
import logging
import sqlite3
from datetime import UTC, datetime
from pathlib import Path

from app.models.agent import AuditToolCall, InvestigationAuditRecord
from app.models.history import QueryMetricRecord, SnapshotRecord, WaitTotalsRecord
from app.storage.repository import HistoryRepository

logger = logging.getLogger(__name__)

_BUSY_TIMEOUT_MS = 30_000  # 30 seconds

_SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS snapshots (
    snapshot_id TEXT PRIMARY KEY,
    collected_at TEXT NOT NULL,
    server TEXT NOT NULL,
    scope TEXT NOT NULL,
    database_name TEXT
);

CREATE TABLE IF NOT EXISTS query_metrics (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id TEXT NOT NULL,
    collected_at TEXT NOT NULL,
    server TEXT NOT NULL,
    database_name TEXT NOT NULL,
    query_hash TEXT NOT NULL,
    sql_text_preview TEXT,
    execution_count INTEGER DEFAULT 0,
    total_cpu_time_ms INTEGER DEFAULT 0,
    avg_cpu_time_ms REAL DEFAULT 0.0,
    total_elapsed_time_ms INTEGER DEFAULT 0,
    avg_elapsed_time_ms REAL DEFAULT 0.0,
    total_logical_reads INTEGER DEFAULT 0,
    avg_logical_reads REAL DEFAULT 0.0,
    total_physical_reads INTEGER DEFAULT 0,
    avg_physical_reads REAL DEFAULT 0.0,
    total_logical_writes INTEGER DEFAULT 0,
    avg_logical_writes REAL DEFAULT 0.0,
    UNIQUE(snapshot_id, query_hash)
);

CREATE INDEX IF NOT EXISTS idx_qm_hash_db
    ON query_metrics(query_hash, database_name);
CREATE INDEX IF NOT EXISTS idx_qm_collected
    ON query_metrics(collected_at);
CREATE INDEX IF NOT EXISTS idx_snapshots_collected
    ON snapshots(collected_at);

CREATE TABLE IF NOT EXISTS investigation_audits (
    investigation_id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL,
    question_sanitized TEXT NOT NULL,
    server TEXT NOT NULL,
    database_name TEXT,
    time_range_start TEXT,
    time_range_end TEXT,
    tools_called TEXT NOT NULL DEFAULT '[]',
    finding_references TEXT NOT NULL DEFAULT '[]',
    severity TEXT NOT NULL DEFAULT 'unknown',
    confidence TEXT NOT NULL DEFAULT 'low',
    answer TEXT NOT NULL DEFAULT '',
    human_review_required INTEGER NOT NULL DEFAULT 1,
    remediation_performed INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_audit_created
    ON investigation_audits(created_at);

CREATE TABLE IF NOT EXISTS wait_totals (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id TEXT NOT NULL UNIQUE,
    collected_at TEXT NOT NULL,
    server TEXT NOT NULL,
    sqlserver_start_time TEXT,
    waiting_tasks_count INTEGER DEFAULT 0,
    wait_time_ms INTEGER DEFAULT 0,
    signal_wait_time_ms INTEGER DEFAULT 0,
    resource_wait_time_ms INTEGER DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_wait_totals_server_collected
    ON wait_totals(server, collected_at);
"""


class SQLiteHistoryRepository(HistoryRepository):
    """SQLite-backed history repository for POC use.

    Each public method opens a short-lived connection, executes its
    SQL inside an implicit or explicit transaction, and closes the
    connection before returning.  This avoids holding write-locks
    across long-running operations (LLM calls, SQL Server queries).
    """

    def __init__(self, db_path: str = "sqlpulse_history.db") -> None:
        self._db_path = db_path

    @contextlib.contextmanager
    def _connect(self):
        """Yield a short-lived SQLite connection with WAL and busy_timeout."""
        conn = sqlite3.connect(
            self._db_path,
            timeout=_BUSY_TIMEOUT_MS / 1000,  # sqlite3.connect timeout in seconds
            detect_types=sqlite3.PARSE_DECLTYPES,
        )
        conn.row_factory = sqlite3.Row
        conn.execute(f"PRAGMA busy_timeout={_BUSY_TIMEOUT_MS}")
        conn.execute("PRAGMA journal_mode=WAL")
        try:
            yield conn
        finally:
            conn.close()

    def initialize(self) -> None:
        """Create tables if they don't exist."""
        with self._connect() as conn:
            conn.executescript(_SCHEMA_SQL)
            conn.commit()
        logger.info("SQLite history storage initialized at %s", self._db_path)

    def store_snapshot(self, record: SnapshotRecord) -> None:
        """Persist a snapshot record. Ignore duplicates."""
        with self._connect() as conn:
            conn.execute(
                """INSERT OR IGNORE INTO snapshots
                   (snapshot_id, collected_at, server, scope, database_name)
                   VALUES (?, ?, ?, ?, ?)""",
                (
                    record.snapshot_id,
                    record.collected_at.isoformat(),
                    record.server,
                    record.scope,
                    record.database,
                ),
            )
            conn.commit()

    def store_query_metrics(self, records: list[QueryMetricRecord]) -> None:
        """Persist query metric records. Skip duplicates."""
        if not records:
            return
        with self._connect() as conn:
            conn.executemany(
                """INSERT OR IGNORE INTO query_metrics
                   (snapshot_id, collected_at, server, database_name, query_hash,
                    sql_text_preview, execution_count,
                    total_cpu_time_ms, avg_cpu_time_ms,
                    total_elapsed_time_ms, avg_elapsed_time_ms,
                    total_logical_reads, avg_logical_reads,
                    total_physical_reads, avg_physical_reads,
                    total_logical_writes, avg_logical_writes)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                [
                    (
                        r.snapshot_id,
                        r.collected_at.isoformat(),
                        r.server,
                        r.database,
                        r.query_hash,
                        r.sql_text_preview,
                        r.execution_count,
                        r.total_cpu_time_ms,
                        r.avg_cpu_time_ms,
                        r.total_elapsed_time_ms,
                        r.avg_elapsed_time_ms,
                        r.total_logical_reads,
                        r.avg_logical_reads,
                        r.total_physical_reads,
                        r.avg_physical_reads,
                        r.total_logical_writes,
                        r.avg_logical_writes,
                    )
                    for r in records
                ],
            )
            conn.commit()

    def get_query_history(
        self,
        query_hash: str,
        database: str,
        since: datetime | None = None,
        limit: int = 500,
    ) -> list[QueryMetricRecord]:
        """Retrieve historical measurements for a query."""
        with self._connect() as conn:
            if since:
                cursor = conn.execute(
                    """SELECT * FROM query_metrics
                       WHERE query_hash = ? AND database_name = ? AND collected_at >= ?
                       ORDER BY collected_at DESC LIMIT ?""",
                    (query_hash, database, since.isoformat(), limit),
                )
            else:
                cursor = conn.execute(
                    """SELECT * FROM query_metrics
                       WHERE query_hash = ? AND database_name = ?
                       ORDER BY collected_at DESC LIMIT ?""",
                    (query_hash, database, limit),
                )
            return [self._row_to_metric(row) for row in cursor.fetchall()]

    def get_query_metrics_for_baseline(
        self,
        query_hash: str,
        database: str,
        since: datetime,
    ) -> list[QueryMetricRecord]:
        """Retrieve metrics within a time window for baseline calculation."""
        with self._connect() as conn:
            cursor = conn.execute(
                """SELECT * FROM query_metrics
                   WHERE query_hash = ? AND database_name = ? AND collected_at >= ?
                   ORDER BY collected_at ASC""",
                (query_hash, database, since.isoformat()),
            )
            return [self._row_to_metric(row) for row in cursor.fetchall()]

    def snapshot_exists(self, snapshot_id: str) -> bool:
        """Check if a snapshot has already been stored."""
        with self._connect() as conn:
            cursor = conn.execute(
                "SELECT 1 FROM snapshots WHERE snapshot_id = ?", (snapshot_id,)
            )
            return cursor.fetchone() is not None

    def delete_older_than(self, cutoff: datetime) -> int:
        """Delete records older than the cutoff."""
        with self._connect() as conn:
            cutoff_str = cutoff.isoformat()
            cursor = conn.execute(
                "DELETE FROM query_metrics WHERE collected_at < ?", (cutoff_str,)
            )
            metrics_deleted = cursor.rowcount
            conn.execute(
                "DELETE FROM snapshots WHERE collected_at < ?", (cutoff_str,)
            )
            conn.execute(
                "DELETE FROM wait_totals WHERE collected_at < ?", (cutoff_str,)
            )
            conn.commit()
        logger.info("Retention cleanup: deleted %d metric records older than %s", metrics_deleted, cutoff_str)
        return metrics_deleted

    def close(self) -> None:
        """No-op — connections are short-lived and closed automatically."""

    # ─── Wait statistics ──────────────────────────────────────────────

    def store_wait_totals(self, record: WaitTotalsRecord) -> None:
        """Persist a wait-totals observation. Ignore duplicates."""
        with self._connect() as conn:
            conn.execute(
                """INSERT OR IGNORE INTO wait_totals
                   (snapshot_id, collected_at, server, sqlserver_start_time,
                    waiting_tasks_count, wait_time_ms, signal_wait_time_ms,
                    resource_wait_time_ms)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                (
                    record.snapshot_id,
                    record.collected_at.isoformat(),
                    record.server,
                    record.sqlserver_start_time.isoformat()
                    if record.sqlserver_start_time
                    else None,
                    record.waiting_tasks_count,
                    record.wait_time_ms,
                    record.signal_wait_time_ms,
                    record.resource_wait_time_ms,
                ),
            )
            conn.commit()

    def get_latest_wait_totals(
        self, server: str, before: datetime | None = None
    ) -> WaitTotalsRecord | None:
        """Return the most recent wait-totals observation for *server*."""
        sql = "SELECT * FROM wait_totals WHERE server = ?"
        params: list = [server]
        if before is not None:
            sql += " AND collected_at < ?"
            params.append(before.isoformat())
        sql += " ORDER BY collected_at DESC LIMIT 1"

        with self._connect() as conn:
            row = conn.execute(sql, tuple(params)).fetchone()

        if row is None:
            return None
        return WaitTotalsRecord(
            id=row["id"],
            snapshot_id=row["snapshot_id"],
            collected_at=datetime.fromisoformat(row["collected_at"]),
            server=row["server"],
            sqlserver_start_time=(
                datetime.fromisoformat(row["sqlserver_start_time"])
                if row["sqlserver_start_time"]
                else None
            ),
            waiting_tasks_count=row["waiting_tasks_count"],
            wait_time_ms=row["wait_time_ms"],
            signal_wait_time_ms=row["signal_wait_time_ms"],
            resource_wait_time_ms=row["resource_wait_time_ms"],
        )

    # ─── Audit trail ──────────────────────────────────────────────────

    def store_investigation_audit(self, record: InvestigationAuditRecord) -> None:
        """Persist an investigation audit record. Ignore duplicates."""
        with self._connect() as conn:
            conn.execute(
                """INSERT OR IGNORE INTO investigation_audits
                   (investigation_id, created_at, question_sanitized, server,
                    database_name, time_range_start, time_range_end,
                    tools_called, finding_references,
                    severity, confidence, answer,
                    human_review_required, remediation_performed)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (
                    record.investigation_id,
                    record.created_at.isoformat(),
                    record.question_sanitized,
                    record.server,
                    record.database,
                    record.time_range_start,
                    record.time_range_end,
                    json.dumps([tc.model_dump() for tc in record.tools_called]),
                    json.dumps(record.finding_references),
                    record.severity,
                    record.confidence,
                    record.answer,
                    1 if record.human_review_required else 0,
                    1 if record.remediation_performed else 0,
                ),
            )
            conn.commit()

    def get_investigation_audit(self, investigation_id: str) -> InvestigationAuditRecord | None:
        """Retrieve a single investigation audit by ID."""
        with self._connect() as conn:
            cursor = conn.execute(
                "SELECT * FROM investigation_audits WHERE investigation_id = ?",
                (investigation_id,),
            )
            row = cursor.fetchone()
            if row is None:
                return None
            return self._row_to_audit(row)

    def list_investigation_audits(self, limit: int = 50) -> list[InvestigationAuditRecord]:
        """List recent investigation audits, newest first."""
        with self._connect() as conn:
            cursor = conn.execute(
                "SELECT * FROM investigation_audits ORDER BY created_at DESC LIMIT ?",
                (limit,),
            )
            return [self._row_to_audit(row) for row in cursor.fetchall()]

    @staticmethod
    def _row_to_audit(row: sqlite3.Row) -> InvestigationAuditRecord:
        """Convert a SQLite row to an InvestigationAuditRecord."""
        tools_called_raw = json.loads(row["tools_called"]) if row["tools_called"] else []
        tools_called = [AuditToolCall(**tc) for tc in tools_called_raw]
        finding_refs = json.loads(row["finding_references"]) if row["finding_references"] else []
        return InvestigationAuditRecord(
            investigation_id=row["investigation_id"],
            created_at=datetime.fromisoformat(row["created_at"]),
            question_sanitized=row["question_sanitized"],
            server=row["server"],
            database=row["database_name"],
            time_range_start=row["time_range_start"],
            time_range_end=row["time_range_end"],
            tools_called=tools_called,
            finding_references=finding_refs,
            severity=row["severity"],
            confidence=row["confidence"],
            answer=row["answer"],
            human_review_required=bool(row["human_review_required"]),
            remediation_performed=bool(row["remediation_performed"]),
        )

    @staticmethod
    def _row_to_metric(row: sqlite3.Row) -> QueryMetricRecord:
        """Convert a SQLite row to a QueryMetricRecord."""
        return QueryMetricRecord(
            id=row["id"],
            snapshot_id=row["snapshot_id"],
            collected_at=datetime.fromisoformat(row["collected_at"]),
            server=row["server"],
            database=row["database_name"],
            query_hash=row["query_hash"],
            sql_text_preview=row["sql_text_preview"],
            execution_count=row["execution_count"],
            total_cpu_time_ms=row["total_cpu_time_ms"],
            avg_cpu_time_ms=row["avg_cpu_time_ms"],
            total_elapsed_time_ms=row["total_elapsed_time_ms"],
            avg_elapsed_time_ms=row["avg_elapsed_time_ms"],
            total_logical_reads=row["total_logical_reads"],
            avg_logical_reads=row["avg_logical_reads"],
            total_physical_reads=row["total_physical_reads"],
            avg_physical_reads=row["avg_physical_reads"],
            total_logical_writes=row["total_logical_writes"],
            avg_logical_writes=row["avg_logical_writes"],
        )
