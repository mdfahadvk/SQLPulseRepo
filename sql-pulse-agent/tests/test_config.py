"""Tests for configuration loading."""

import os
from unittest.mock import patch

from app.config import Settings


def test_settings_loads_from_env():
    env = {
        "SQL_SERVER": "localhost",
        "SQL_USERNAME": "monitor_user",
        "SQL_PASSWORD": "secret123",
        "SQL_DRIVER": "{ODBC Driver 18 for SQL Server}",
        "SQL_DEFAULT_DATABASE": "master",
    }
    with patch.dict(os.environ, env, clear=False):
        settings = Settings()  # type: ignore[call-arg]
        assert settings.sql_server == "localhost"
        assert settings.sql_username == "monitor_user"
        assert settings.sql_default_database == "master"
        # Password should not be exposed directly
        assert settings.sql_password.get_secret_value() == "secret123"
        assert "secret123" not in repr(settings)


def test_connection_string_contains_readonly_intent():
    env = {
        "SQL_SERVER": "dbserver",
        "SQL_USERNAME": "reader",
        "SQL_PASSWORD": "pass",
        "SQL_DEFAULT_DATABASE": "proddb",
    }
    with patch.dict(os.environ, env, clear=False):
        settings = Settings()  # type: ignore[call-arg]
        conn_str = settings.connection_string
        assert "ApplicationIntent=ReadOnly" in conn_str
        assert "dbserver" in conn_str
        assert "proddb" in conn_str
