"""Pydantic response models for SQLPulse API."""

from pydantic import BaseModel


class HealthResponse(BaseModel):
    """Response model for the /health endpoint."""

    status: str
    application: str
    version: str


class ConnectionTestResponse(BaseModel):
    """Response model for the /database/connection-test endpoint."""

    connected: bool
    message: str


class DatabaseEntry(BaseModel):
    """A single discovered database."""

    name: str
    status: str


class DatabaseListResponse(BaseModel):
    """Response model for the /api/databases endpoint."""

    server: str
    databases: list[DatabaseEntry] = []
