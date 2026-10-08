"""Configurable thresholds for performance detectors.

All thresholds are tunable via environment variables or .env file.
"""

from pydantic import Field
from pydantic_settings import BaseSettings


class DetectorThresholds(BaseSettings):
    """Thresholds controlling when detectors raise findings.

    Env vars are prefixed with DETECTOR_ for clarity.
    """

    # --- Long-Running Query Thresholds ---
    # Time in ms before a query is considered "long-running"
    long_running_warning_ms: int = Field(
        default=10_000, description="Elapsed ms for medium severity"
    )
    long_running_high_ms: int = Field(
        default=30_000, description="Elapsed ms for high severity"
    )
    long_running_critical_ms: int = Field(
        default=120_000, description="Elapsed ms for critical severity"
    )
    # Minimum number of long-running queries to raise severity
    long_running_critical_count: int = Field(
        default=3, description="Count of concurrent long queries for critical"
    )

    # --- Blocking Thresholds ---
    blocking_warning_wait_ms: int = Field(
        default=5_000, description="Wait ms for medium severity blocking"
    )
    blocking_high_wait_ms: int = Field(
        default=30_000, description="Wait ms for high severity blocking"
    )
    blocking_critical_wait_ms: int = Field(
        default=60_000, description="Wait ms for critical severity blocking"
    )
    blocking_critical_chain_count: int = Field(
        default=3, description="Blocked sessions in a chain for critical"
    )

    # --- High CPU Thresholds ---
    # Average CPU time per execution
    cpu_high_avg_ms: float = Field(
        default=5_000.0, description="Avg CPU ms per exec for high severity"
    )
    cpu_critical_avg_ms: float = Field(
        default=30_000.0, description="Avg CPU ms per exec for critical severity"
    )
    # Total CPU across all executions
    cpu_high_total_ms: int = Field(
        default=600_000, description="Total CPU ms for high severity"
    )
    cpu_critical_total_ms: int = Field(
        default=3_600_000, description="Total CPU ms for critical severity"
    )
    # Minimum executions to avoid flagging one-off queries
    cpu_min_executions: int = Field(
        default=5, description="Min executions before flagging a query"
    )
    # Per-execution floor for total-driven findings. A query that is cheap every
    # time it runs is not a tuning target no matter how often it executes:
    # 1,000,000 executions at 0.1ms each is a workload-volume observation, not a
    # query problem. Without this floor, execution count alone creates findings.
    cpu_min_significant_avg_ms: float = Field(
        default=10.0,
        description="Min avg CPU ms per exec for a total-driven CPU finding",
    )

    # --- High I/O Thresholds ---
    io_high_avg_logical_reads: float = Field(
        default=50_000.0, description="Avg logical reads per exec for high"
    )
    io_critical_avg_logical_reads: float = Field(
        default=500_000.0, description="Avg logical reads per exec for critical"
    )
    io_high_total_logical_reads: int = Field(
        default=10_000_000, description="Total logical reads for high severity"
    )
    io_critical_total_logical_reads: int = Field(
        default=100_000_000, description="Total logical reads for critical"
    )
    io_min_executions: int = Field(
        default=5, description="Min executions before flagging a query"
    )
    # Per-execution floor for total-driven findings — see cpu_min_significant_avg_ms.
    # Deliberately low: a query doing a few hundred reads each time it runs is
    # still a legitimate aggregate-I/O observation. The floor only removes
    # queries whose individual cost is negligible.
    io_min_significant_avg_reads: float = Field(
        default=100.0,
        description="Min avg logical reads per exec for a total-driven I/O finding",
    )

    # --- Regression Thresholds ---
    regression_warning_multiplier: float = Field(
        default=2.0, description="Multiplier vs baseline for warning"
    )
    regression_high_multiplier: float = Field(
        default=4.0, description="Multiplier vs baseline for high severity"
    )
    regression_critical_multiplier: float = Field(
        default=8.0, description="Multiplier vs baseline for critical severity"
    )
    regression_min_samples: int = Field(
        default=5, description="Minimum baseline samples before detecting regression"
    )
    regression_baseline_window: str = Field(
        default="7d", description="Default baseline window for regression detection"
    )

    model_config = {
        "env_file": ".env",
        "env_file_encoding": "utf-8",
        "env_prefix": "DETECTOR_",
        "case_sensitive": False,
        "extra": "ignore",
    }


_thresholds: DetectorThresholds | None = None


def get_thresholds() -> DetectorThresholds:
    """Return cached detector thresholds singleton."""
    global _thresholds
    if _thresholds is None:
        _thresholds = DetectorThresholds()
    return _thresholds
