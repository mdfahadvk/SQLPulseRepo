"""Application configuration using Pydantic Settings."""

from pydantic import Field, SecretStr
from pydantic_settings import BaseSettings

# System databases excluded from monitoring by default
SYSTEM_DATABASES = frozenset({"master", "model", "msdb", "tempdb"})


def _split_csv(raw: str) -> tuple[str, ...]:
    """Parse a comma-separated setting into a tuple of non-empty entries."""
    return tuple(part.strip() for part in raw.split(",") if part.strip())


class Settings(BaseSettings):
    """SQLPulse application settings.

    Loaded from environment variables or .env file.
    """

    app_name: str = "SQLPulse"
    app_version: str = "0.1.0"
    debug: bool = False

    # SQL Server connection settings
    sql_server: str = Field(default="", description="SQL Server hostname or instance")
    sql_username: str = Field(default="", description="SQL Server authentication username")
    sql_password: SecretStr = Field(default="", description="SQL Server authentication password")
    sql_driver: str = Field(
        default="{ODBC Driver 18 for SQL Server}",
        description="ODBC driver name",
    )
    sql_encrypt: str = Field(default="yes", description="Encrypt connection")
    sql_trust_server_certificate: str = Field(
        default="yes", description="Trust server certificate"
    )
    sql_default_database: str = Field(
        default="master", description="Default database for server-level queries"
    )
    sql_connection_timeout: int = Field(
        default=30, description="Connection timeout in seconds"
    )
    sql_query_timeout: int = Field(
        default=60, description="Query timeout in seconds"
    )

    # Logging
    log_level: str = Field(default="INFO", description="Logging level")

    # Slow-query monitoring vs application timeout
    #
    # These two numbers answer different questions and must never be conflated:
    #
    #   slow_query_threshold_ms  "which queries are worth looking at?"
    #   application_timeout_ms   "at what duration does the client give up?"
    #
    # Only the application owns the second number. SQLPulse cannot observe it,
    # cannot derive it from a monitoring filter, and must not guess it: calling
    # a 9.7s query a timeout candidate against a 30s client timeout would be a
    # fabricated incident. When it is not configured, timeout classification is
    # simply unavailable.
    slow_query_threshold_ms: int = Field(
        default=5_000,
        ge=1,
        description="Duration above which a query is a SLOW_QUERY_CANDIDATE",
    )
    application_timeout_ms: int | None = Field(
        default=None,
        description=(
            "Explicitly configured application/client command timeout in "
            "milliseconds. Leave unset when unknown — never inferred from "
            "slow-query filters."
        ),
    )

    # Workload classification (see app/detectors/timeout_candidate.py).
    # Comma-separated program/application names. Matching is case-insensitive
    # substring matching against program_name / application_name.
    workload_application_programs: str = Field(
        default="",
        description=(
            "Known application program_name values that identify "
            "APPLICATION_OR_INTERACTIVE workload (comma-separated)"
        ),
    )
    workload_background_programs: str = Field(
        default="",
        description=(
            "Known program_name values that identify BACKGROUND_OR_SCHEDULED "
            "workload, in addition to the built-in SQL Agent patterns "
            "(comma-separated)"
        ),
    )

    # Historical storage
    history_retention_days: int = Field(
        default=30, description="Number of days to retain historical snapshots"
    )
    baseline_min_samples: int = Field(
        default=5, description="Minimum samples required to compute a baseline"
    )
    history_db_path: str = Field(
        default="sqlpulse_history.db", description="Path to SQLite history database"
    )

    # AI Agent
    llm_provider: str = Field(
        default="openai", description="LLM provider name (openai, azure_openai, etc.)"
    )
    llm_api_key: SecretStr = Field(
        default="", description="API key for the LLM provider"
    )
    llm_model: str = Field(
        default="gpt-4o", description="LLM model name"
    )
    llm_base_url: str = Field(
        default="", description="Custom base URL for the LLM API (optional)"
    )
    llm_temperature: float = Field(
        default=0.1, description="LLM temperature (lower = more deterministic)"
    )
    llm_request_timeout_seconds: int = Field(
        default=60, description="Timeout for a single LLM API request in seconds"
    )
    llm_max_tokens: int = Field(
        default=1500,
        description=(
            "Maximum tokens the LLM may generate per call. Providers count this "
            "against per-minute token quotas, so keep it modest on low-tier plans."
        ),
    )
    max_tool_result_chars: int = Field(
        default=4000,
        description=(
            "Maximum characters of a single tool result forwarded to the LLM. "
            "Prevents large snapshots from exhausting the model context or the "
            "provider's tokens-per-minute quota."
        ),
    )
    max_agent_tool_calls: int = Field(
        default=10, description="Maximum tool calls per investigation"
    )
    max_agent_execution_seconds: int = Field(
        default=60, description="Maximum wall-clock seconds per investigation"
    )

    model_config = {
        "env_file": ".env",
        "env_file_encoding": "utf-8",
        "case_sensitive": False,
    }

    @property
    def is_database_configured(self) -> bool:
        """Return True if the minimum SQL Server settings are provided."""
        return bool(
            self.sql_server
            and self.sql_username
            and self.sql_password.get_secret_value()
        )

    @property
    def application_program_names(self) -> tuple[str, ...]:
        """Configured program names that identify application workload."""
        return _split_csv(self.workload_application_programs)

    @property
    def background_program_names(self) -> tuple[str, ...]:
        """Configured program names that identify background/scheduled workload."""
        return _split_csv(self.workload_background_programs)

    def build_connection_string(self, database: str | None = None) -> str:
        """Build pyodbc connection string for a specific database.

        Password is resolved here only — never logged.
        """
        db = database or self.sql_default_database
        return (
            f"DRIVER={self.sql_driver};"
            f"SERVER={self.sql_server};"
            f"DATABASE={db};"
            f"UID={self.sql_username};"
            f"PWD={self.sql_password.get_secret_value()};"
            f"Encrypt={self.sql_encrypt};"
            f"TrustServerCertificate={self.sql_trust_server_certificate};"
            f"Connection Timeout={self.sql_connection_timeout};"
            f"ApplicationIntent=ReadOnly;"
        )

    # Backwards-compatible aliases used by existing code
    @property
    def sql_server_host(self) -> str:
        return self.sql_server

    @property
    def sql_server_database(self) -> str:
        return self.sql_default_database

    @property
    def connection_string(self) -> str:
        return self.build_connection_string()


_settings: Settings | None = None


def get_settings() -> Settings:
    """Return cached application settings singleton."""
    global _settings
    if _settings is None:
        _settings = Settings()  # type: ignore[call-arg]
    return _settings
