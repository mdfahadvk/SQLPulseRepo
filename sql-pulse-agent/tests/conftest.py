"""Pytest configuration and shared fixtures."""

import os
from unittest.mock import patch

import pytest


@pytest.fixture(autouse=True)
def _set_test_env(monkeypatch):
    """Ensure required env vars are set for all tests."""
    monkeypatch.setenv("SQL_SERVER", "localhost")
    monkeypatch.setenv("SQL_USERNAME", "test_user")
    monkeypatch.setenv("SQL_PASSWORD", "test_pass")
    monkeypatch.setenv("SQL_DRIVER", "ODBC Driver 18 for SQL Server")
    monkeypatch.setenv("SQL_DEFAULT_DATABASE", "master")
    monkeypatch.setenv("DEBUG", "false")

    # Reset settings singleton between tests
    import app.config
    app.config._settings = None
    yield
    app.config._settings = None
