"""Tests for the /database/connection-test endpoint."""

from unittest.mock import patch

from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


@patch("app.main.test_connection")
def test_connection_test_success(mock_test_conn):
    mock_test_conn.return_value = True
    response = client.get("/database/connection-test")
    assert response.status_code == 200
    data = response.json()
    assert data["connected"] is True
    assert "Successfully" in data["message"]


@patch("app.main.test_connection")
def test_connection_test_failure(mock_test_conn):
    mock_test_conn.return_value = False
    response = client.get("/database/connection-test")
    assert response.status_code == 200
    data = response.json()
    assert data["connected"] is False


@patch("app.main.test_connection")
def test_connection_test_exception(mock_test_conn):
    mock_test_conn.side_effect = RuntimeError("unexpected")
    response = client.get("/database/connection-test")
    assert response.status_code == 200
    data = response.json()
    assert data["connected"] is False
    assert "unexpected error" in data["message"].lower()
