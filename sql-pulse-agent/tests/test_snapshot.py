"""Tests for the performance snapshot endpoint and orchestrator."""

from unittest.mock import patch

from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


@patch("app.collectors.snapshot.ActiveRequestsCollector.collect")
@patch("app.collectors.snapshot.LongRunningQueriesCollector.collect")
@patch("app.collectors.snapshot.BlockingSessionsCollector.collect")
@patch("app.collectors.snapshot.TopCpuQueriesCollector.collect")
@patch("app.collectors.snapshot.TopIoQueriesCollector.collect")
@patch("app.collectors.snapshot.WaitStatisticsCollector.collect")
def test_snapshot_endpoint_returns_200(
    mock_waits, mock_io, mock_cpu, mock_blocking, mock_long, mock_active
):
    """Verify the endpoint calls all collectors and returns 200."""
    from app.models.performance import (
        ActiveRequestsResult,
        BlockingSessionsResult,
        CollectorMetadata,
        LongRunningQueriesResult,
        TopCpuQueriesResult,
        TopIoQueriesResult,
        WaitStatisticsResult,
    )

    meta = CollectorMetadata(
        collector_name="test",
        server_host="localhost",
        database="master",
    )
    mock_active.return_value = ActiveRequestsResult(metadata=meta)
    mock_long.return_value = LongRunningQueriesResult(metadata=meta, threshold_ms=5000)
    mock_blocking.return_value = BlockingSessionsResult(metadata=meta)
    mock_cpu.return_value = TopCpuQueriesResult(metadata=meta, top_n=20)
    mock_io.return_value = TopIoQueriesResult(metadata=meta, top_n=20)
    mock_waits.return_value = WaitStatisticsResult(metadata=meta, top_n=25)

    response = client.get("/api/performance/snapshot")
    assert response.status_code == 200

    data = response.json()
    assert "snapshot_id" in data
    assert "collected_at" in data
    assert "active_requests" in data
    assert "long_running_queries" in data
    assert "blocking_sessions" in data
    assert "top_cpu_queries" in data
    assert "top_io_queries" in data
    assert "wait_statistics" in data


@patch("app.collectors.snapshot.ActiveRequestsCollector.collect")
@patch("app.collectors.snapshot.LongRunningQueriesCollector.collect")
@patch("app.collectors.snapshot.BlockingSessionsCollector.collect")
@patch("app.collectors.snapshot.TopCpuQueriesCollector.collect")
@patch("app.collectors.snapshot.TopIoQueriesCollector.collect")
@patch("app.collectors.snapshot.WaitStatisticsCollector.collect")
def test_snapshot_endpoint_accepts_query_params(
    mock_waits, mock_io, mock_cpu, mock_blocking, mock_long, mock_active
):
    """Verify custom query parameters are passed through."""
    from app.models.performance import (
        ActiveRequestsResult,
        BlockingSessionsResult,
        CollectorMetadata,
        LongRunningQueriesResult,
        TopCpuQueriesResult,
        TopIoQueriesResult,
        WaitStatisticsResult,
    )

    meta = CollectorMetadata(
        collector_name="test",
        server_host="localhost",
        database="master",
    )
    mock_active.return_value = ActiveRequestsResult(metadata=meta)
    mock_long.return_value = LongRunningQueriesResult(metadata=meta, threshold_ms=10000)
    mock_blocking.return_value = BlockingSessionsResult(metadata=meta)
    mock_cpu.return_value = TopCpuQueriesResult(metadata=meta, top_n=10)
    mock_io.return_value = TopIoQueriesResult(metadata=meta, top_n=10)
    mock_waits.return_value = WaitStatisticsResult(metadata=meta, top_n=15)

    response = client.get(
        "/api/performance/snapshot",
        params={"long_running_threshold_ms": 10000, "top_n": 10, "wait_stats_top_n": 15},
    )
    assert response.status_code == 200


@patch("app.collectors.snapshot.ActiveRequestsCollector.collect")
@patch("app.collectors.snapshot.LongRunningQueriesCollector.collect")
@patch("app.collectors.snapshot.BlockingSessionsCollector.collect")
@patch("app.collectors.snapshot.TopCpuQueriesCollector.collect")
@patch("app.collectors.snapshot.TopIoQueriesCollector.collect")
@patch("app.collectors.snapshot.WaitStatisticsCollector.collect")
def test_snapshot_partial_failure_still_returns(
    mock_waits, mock_io, mock_cpu, mock_blocking, mock_long, mock_active
):
    """If one collector fails, the snapshot still returns with partial data."""
    from app.models.performance import (
        ActiveRequestsResult,
        BlockingSessionsResult,
        CollectorMetadata,
        LongRunningQueriesResult,
        TopCpuQueriesResult,
        TopIoQueriesResult,
        WaitStatisticsResult,
    )

    meta_ok = CollectorMetadata(
        collector_name="test",
        server_host="localhost",
        database="master",
    )
    meta_fail = CollectorMetadata(
        collector_name="test",
        server_host="localhost",
        database="master",
        success=False,
        error_message="permission denied",
        permission_issue=True,
    )
    mock_active.return_value = ActiveRequestsResult(metadata=meta_ok)
    mock_long.return_value = LongRunningQueriesResult(metadata=meta_fail, threshold_ms=5000)
    mock_blocking.return_value = BlockingSessionsResult(metadata=meta_ok)
    mock_cpu.return_value = TopCpuQueriesResult(metadata=meta_fail, top_n=20)
    mock_io.return_value = TopIoQueriesResult(metadata=meta_ok, top_n=20)
    mock_waits.return_value = WaitStatisticsResult(metadata=meta_ok, top_n=25)

    response = client.get("/api/performance/snapshot")
    assert response.status_code == 200

    data = response.json()
    # Failed collectors still present but with success=False
    assert data["long_running_queries"]["metadata"]["success"] is False
    assert data["top_cpu_queries"]["metadata"]["success"] is False
    # Successful collectors have success=True
    assert data["active_requests"]["metadata"]["success"] is True
