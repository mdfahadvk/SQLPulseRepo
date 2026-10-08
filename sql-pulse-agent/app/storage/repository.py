"""Abstract repository interface for historical storage.

This abstraction allows SQLite to be replaced by SQL Server or PostgreSQL
without changing collector/detector logic.
"""

from abc import ABC, abstractmethod
from datetime import datetime

from app.models.agent import InvestigationAuditRecord
from app.models.history import QueryMetricRecord, SnapshotRecord, WaitTotalsRecord


class HistoryRepository(ABC):
    """Abstract interface for persisting and querying performance history."""

    @abstractmethod
    def initialize(self) -> None:
        """Create tables/schema if they don't exist."""

    @abstractmethod
    def store_snapshot(self, record: SnapshotRecord) -> None:
        """Persist a snapshot record. Ignore duplicates by snapshot_id."""

    @abstractmethod
    def store_query_metrics(self, records: list[QueryMetricRecord]) -> None:
        """Persist query metric records. Skip duplicates by (snapshot_id, query_hash)."""

    @abstractmethod
    def get_query_history(
        self,
        query_hash: str,
        database: str,
        since: datetime | None = None,
        limit: int = 500,
    ) -> list[QueryMetricRecord]:
        """Retrieve historical measurements for a query."""

    @abstractmethod
    def get_query_metrics_for_baseline(
        self,
        query_hash: str,
        database: str,
        since: datetime,
    ) -> list[QueryMetricRecord]:
        """Retrieve metrics within a time window for baseline calculation."""

    @abstractmethod
    def snapshot_exists(self, snapshot_id: str) -> bool:
        """Check if a snapshot has already been stored."""

    @abstractmethod
    def delete_older_than(self, cutoff: datetime) -> int:
        """Delete records older than the cutoff. Returns count of deleted rows."""

    @abstractmethod
    def close(self) -> None:
        """Close any open connections."""

    # ─── Wait statistics ────────────────────────────────────────

    @abstractmethod
    def store_wait_totals(self, record: WaitTotalsRecord) -> None:
        """Persist a wait-totals observation. Ignore duplicates by snapshot_id."""

    @abstractmethod
    def get_latest_wait_totals(
        self, server: str, before: datetime | None = None
    ) -> WaitTotalsRecord | None:
        """Return the most recent wait-totals observation for *server*.

        When *before* is given, only observations strictly earlier than it are
        considered, so a reading is never compared against itself.
        """

    # ─── Audit trail ──────────────────────────────────────────────────

    @abstractmethod
    def store_investigation_audit(self, record: InvestigationAuditRecord) -> None:
        """Persist an investigation audit record. Ignore duplicates by investigation_id."""

    @abstractmethod
    def get_investigation_audit(self, investigation_id: str) -> InvestigationAuditRecord | None:
        """Retrieve a single investigation audit by ID. Returns None if not found."""

    @abstractmethod
    def list_investigation_audits(self, limit: int = 50) -> list[InvestigationAuditRecord]:
        """List recent investigation audits, newest first."""
