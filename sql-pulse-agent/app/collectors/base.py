"""Base collector with shared logic for all performance collectors."""

import logging
from abc import ABC, abstractmethod
from datetime import UTC, datetime
from enum import Enum
from typing import Any

from app.config import get_settings
from app.database.connection import execute_read_query
from app.models.performance import CollectorMetadata

logger = logging.getLogger(__name__)


class CollectorScope(str, Enum):
    """Scope at which a collector operates."""

    SERVER = "server"
    DATABASE = "database"
    BOTH = "both"


class BaseCollector(ABC):
    """Abstract base for all performance data collectors."""

    def __init__(self, database: str | None = None) -> None:
        self._database = database

    @property
    @abstractmethod
    def name(self) -> str:
        """Human-readable collector name."""

    @property
    def scope(self) -> CollectorScope:
        """Override in subclass to declare scope. Defaults to BOTH."""
        return CollectorScope.BOTH

    def _build_metadata(
        self,
        success: bool = True,
        error_message: str | None = None,
        permission_issue: bool = False,
    ) -> CollectorMetadata:
        settings = get_settings()
        return CollectorMetadata(
            collector_name=self.name,
            server_host=settings.sql_server_host,
            database=self._database or settings.sql_server_database,
            collected_at=datetime.now(UTC),
            success=success,
            error_message=error_message,
            permission_issue=permission_issue,
        )

    def _execute_query(
        self, query: str, params: tuple | None = None
    ) -> list[dict[str, Any]]:
        """Execute a read-only query via the shared connection layer."""
        return execute_read_query(query, params, database=self._database)

    def _is_permission_error(self, exc: Exception) -> bool:
        """Check if exception indicates missing permissions."""
        msg = str(exc).lower()
        return any(
            phrase in msg
            for phrase in [
                "permission",
                "view server state",
                "view database state",
                "denied",
                "not have permission",
            ]
        )

    @abstractmethod
    def collect(self) -> Any:
        """Run the collection and return a typed result model."""
    def collect(self) -> Any:
        """Run the collection and return a typed result model."""
