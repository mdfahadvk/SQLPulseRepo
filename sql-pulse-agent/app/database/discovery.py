"""Database discovery — list accessible databases on the SQL Server instance."""

import logging
from typing import Any

from app.config import SYSTEM_DATABASES, get_settings
from app.database.connection import execute_read_query, validate_database_name
from app.database.queries import DISCOVER_DATABASES

logger = logging.getLogger(__name__)


class DatabaseInfo:
    """Lightweight DTO for a discovered database."""

    def __init__(self, name: str, status: str) -> None:
        self.name = name
        self.status = status


_cached_databases: list[DatabaseInfo] | None = None


def discover_databases(use_cache: bool = True) -> list[DatabaseInfo]:
    """Query SQL Server for accessible user databases.

    Returns only ONLINE databases that are not system databases.
    """
    global _cached_databases
    if use_cache and _cached_databases is not None:
        return _cached_databases

    rows = execute_read_query(DISCOVER_DATABASES)
    databases = [
        DatabaseInfo(name=row["name"], status=row["status"])
        for row in rows
        if row["name"].lower() not in {db.lower() for db in SYSTEM_DATABASES}
    ]
    _cached_databases = databases
    logger.info("Discovered %d accessible databases", len(databases))
    return databases


def get_accessible_database_names() -> set[str]:
    """Return the set of database names accessible for monitoring."""
    return {db.name for db in discover_databases()}


def validate_target_database(database: str) -> str | None:
    """Validate a user-supplied database name.

    Returns None if valid, otherwise an error message.
    """
    if not database:
        return "Database name is required."
    if not validate_database_name(database):
        return "Invalid database name format."
    accessible = get_accessible_database_names()
    if database not in accessible:
        return f"Database '{database}' is not accessible or does not exist."
    return None


def invalidate_cache() -> None:
    """Clear the cached database list (useful for tests)."""
    global _cached_databases
    _cached_databases = None
