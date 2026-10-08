"""Tests for database discovery, scope support, health status, and drilldown."""

from unittest.mock import patch, MagicMock

import pytest
from fastapi.testclient import TestClient

from app.database.connection import validate_database_name
from app.database.discovery import (
    DatabaseInfo,
    discover_databases,
    get_accessible_database_names,
    invalidate_cache,
    validate_target_database,
)
from app.detectors.engine import (
    _compute_affected_databases,
    _sort_findings,
    determine_overall_status,
    run_analysis,
)
from app.main import app
from app.models.findings import (
    AffectedDatabase,
    AnalysisResult,
    Confidence,
    FindingCategory,
    OverallStatus,
    PerformanceFinding,
    RecommendedDrilldown,
    Severity,
)
from app.models.performance import (
    BlockingSession,
    BlockingSessionsResult,
    CollectorMetadata,
    LongRunningQueriesResult,
    LongRunningQuery,
    PerformanceSnapshot,
    TopCpuQueriesResult,
    TopCpuQuery,
    TopIoQueriesResult,
    TopIoQuery,
)

client = TestClient(app)


# --- Database name validation ---


class TestDatabaseNameValidation:
    def test_valid_simple_name(self):
        assert validate_database_name("MyDatabase") is True

    def test_valid_name_with_underscore(self):
        assert validate_database_name("My_Database_01") is True

    def test_valid_name_with_hyphen(self):
        assert validate_database_name("My-Database") is True

    def test_empty_name(self):
        assert validate_database_name("") is False

    def test_name_with_spaces(self):
        assert validate_database_name("My Database") is False

    def test_name_with_semicolons(self):
        assert validate_database_name("db;DROP TABLE x") is False

    def test_name_with_quotes(self):
        assert validate_database_name("db'--") is False

    def test_name_exceeds_max_length(self):
        assert validate_database_name("a" * 129) is False

    def test_name_at_max_length(self):
        assert validate_database_name("a" * 128) is True

    def test_sql_injection_attempt(self):
        assert validate_database_name("master;EXEC xp_cmdshell") is False

    def test_unicode_characters(self):
        assert validate_database_name("données") is False


# --- Discovery service ---


class TestDiscoverDatabases:
    @patch("app.database.discovery.execute_read_query")
    def test_returns_user_databases(self, mock_query):
        mock_query.return_value = [
            {"name": "AppDB", "status": "ONLINE"},
            {"name": "ReportDB", "status": "ONLINE"},
            {"name": "master", "status": "ONLINE"},
            {"name": "tempdb", "status": "ONLINE"},
        ]
        invalidate_cache()
        result = discover_databases(use_cache=False)
        names = [db.name for db in result]
        assert "AppDB" in names
        assert "ReportDB" in names
        assert "master" not in names
        assert "tempdb" not in names

    @patch("app.database.discovery.execute_read_query")
    def test_excludes_all_system_databases(self, mock_query):
        mock_query.return_value = [
            {"name": "master", "status": "ONLINE"},
            {"name": "model", "status": "ONLINE"},
            {"name": "msdb", "status": "ONLINE"},
            {"name": "tempdb", "status": "ONLINE"},
        ]
        invalidate_cache()
        result = discover_databases(use_cache=False)
        assert result == []

    @patch("app.database.discovery.execute_read_query")
    def test_caching_avoids_repeated_queries(self, mock_query):
        mock_query.return_value = [{"name": "CachedDB", "status": "ONLINE"}]
        invalidate_cache()
        result1 = discover_databases(use_cache=True)
        result2 = discover_databases(use_cache=True)
        assert mock_query.call_count == 1
        assert result1[0].name == result2[0].name


class TestValidateTargetDatabase:
    @patch("app.database.discovery.execute_read_query")
    def test_valid_database_returns_none(self, mock_query):
        mock_query.return_value = [{"name": "ValidDB", "status": "ONLINE"}]
        invalidate_cache()
        assert validate_target_database("ValidDB") is None

    @patch("app.database.discovery.execute_read_query")
    def test_inaccessible_database_returns_error(self, mock_query):
        mock_query.return_value = [{"name": "OnlyThis", "status": "ONLINE"}]
        invalidate_cache()
        error = validate_target_database("OtherDB")
        assert error is not None
        assert "not accessible" in error

    def test_empty_database_returns_error(self):
        error = validate_target_database("")
        assert error is not None
        assert "required" in error

    def test_invalid_format_returns_error(self):
        error = validate_target_database("db;DROP TABLE x")
        assert error is not None
        assert "Invalid" in error


# --- Overall health status ---


class TestOverallStatus:
    def test_no_findings_is_healthy(self):
        assert determine_overall_status([]) == OverallStatus.HEALTHY

    def test_critical_finding_makes_critical(self):
        findings = [
            PerformanceFinding(
                finding_id="1", category=FindingCategory.BLOCKING,
                server="s", database="d", severity=Severity.CRITICAL,
                confidence=Confidence.HIGH, summary="x",
                evidence=[], likely_cause="", recommendation="",
            )
        ]
        assert determine_overall_status(findings) == OverallStatus.CRITICAL

    def test_high_finding_makes_degraded(self):
        findings = [
            PerformanceFinding(
                finding_id="1", category=FindingCategory.BLOCKING,
                server="s", database="d", severity=Severity.HIGH,
                confidence=Confidence.HIGH, summary="x",
                evidence=[], likely_cause="", recommendation="",
            )
        ]
        assert determine_overall_status(findings) == OverallStatus.DEGRADED

    def test_medium_finding_makes_warning(self):
        findings = [
            PerformanceFinding(
                finding_id="1", category=FindingCategory.HIGH_CPU,
                server="s", database="d", severity=Severity.MEDIUM,
                confidence=Confidence.MEDIUM, summary="x",
                evidence=[], likely_cause="", recommendation="",
            )
        ]
        assert determine_overall_status(findings) == OverallStatus.WARNING

    def test_low_finding_is_healthy(self):
        findings = [
            PerformanceFinding(
                finding_id="1", category=FindingCategory.HIGH_IO,
                server="s", database="d", severity=Severity.LOW,
                confidence=Confidence.LOW, summary="x",
                evidence=[], likely_cause="", recommendation="",
            )
        ]
        assert determine_overall_status(findings) == OverallStatus.HEALTHY


# --- Affected databases computation ---


class TestAffectedDatabases:
    def test_groups_findings_by_database(self):
        findings = [
            PerformanceFinding(
                finding_id="1", category=FindingCategory.HIGH_CPU,
                server="s", database="db1", severity=Severity.HIGH,
                confidence=Confidence.HIGH, summary="x",
                evidence=[], likely_cause="", recommendation="",
            ),
            PerformanceFinding(
                finding_id="2", category=FindingCategory.BLOCKING,
                server="s", database="db1", severity=Severity.CRITICAL,
                confidence=Confidence.HIGH, summary="x",
                evidence=[], likely_cause="", recommendation="",
            ),
            PerformanceFinding(
                finding_id="3", category=FindingCategory.HIGH_IO,
                server="s", database="db2", severity=Severity.MEDIUM,
                confidence=Confidence.MEDIUM, summary="x",
                evidence=[], likely_cause="", recommendation="",
            ),
        ]
        result = _compute_affected_databases(findings)
        assert len(result) == 2
        db1 = next(a for a in result if a.database == "db1")
        assert db1.highest_severity == "critical"
        assert db1.finding_count == 2
        db2 = next(a for a in result if a.database == "db2")
        assert db2.highest_severity == "medium"
        assert db2.finding_count == 1

    def test_empty_findings_returns_empty(self):
        assert _compute_affected_databases([]) == []


# --- Drilldown recommendation ---


class TestDrilldown:
    def test_server_scope_adds_drilldown_for_critical_findings(self):
        """Server-scope findings with database should get drilldown."""
        snapshot = PerformanceSnapshot(
            snapshot_id="drill",
            server_host="srv",
            database=None,
            long_running_queries=LongRunningQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="srv", database="master"
                ),
                threshold_ms=5000,
                queries=[
                    LongRunningQuery(
                        session_id=1, request_id=0, elapsed_time_ms=150_000,
                        database_name="ProblemDB",
                    ),
                ],
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="srv", database="master"
                ),
                sessions=[],
            ),
            top_cpu_queries=TopCpuQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="srv", database="master"
                ),
                top_n=20,
                queries=[],
            ),
            top_io_queries=TopIoQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="srv", database="master"
                ),
                top_n=20,
                queries=[],
            ),
        )
        result = run_analysis(snapshot, scope="server")
        # Should have findings from long-running detector
        for f in result.findings:
            if f.database and f.severity in (Severity.CRITICAL, Severity.HIGH, Severity.MEDIUM):
                assert f.recommended_drilldown is not None
                assert f.recommended_drilldown.scope == "database"

    def test_database_scope_no_drilldown(self):
        """Database-scope findings should not get drilldown."""
        snapshot = PerformanceSnapshot(
            snapshot_id="drill-db",
            server_host="srv",
            database="AppDB",
            scope="database",
            long_running_queries=LongRunningQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="srv", database="AppDB"
                ),
                threshold_ms=5000,
                queries=[
                    LongRunningQuery(
                        session_id=1, request_id=0, elapsed_time_ms=150_000,
                        database_name="AppDB",
                    ),
                ],
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="srv", database="AppDB"
                ),
                sessions=[],
            ),
            top_cpu_queries=TopCpuQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="srv", database="AppDB"
                ),
                top_n=20,
                queries=[],
            ),
            top_io_queries=TopIoQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="srv", database="AppDB"
                ),
                top_n=20,
                queries=[],
            ),
        )
        result = run_analysis(snapshot, scope="database", database="AppDB")
        for f in result.findings:
            assert f.recommended_drilldown is None


# --- Scope-aware endpoints ---


class TestScopeEndpoints:
    @patch("app.main.validate_target_database")
    @patch("app.main.collect_performance_snapshot")
    def test_snapshot_server_scope(self, mock_snapshot, mock_validate):
        meta = CollectorMetadata(collector_name="test", server_host="srv", database="master")
        mock_snapshot.return_value = PerformanceSnapshot(
            snapshot_id="scope-test",
            server_host="srv",
            scope="server",
            active_requests=None,
            long_running_queries=LongRunningQueriesResult(metadata=meta, threshold_ms=5000),
            blocking_sessions=BlockingSessionsResult(metadata=meta),
            top_cpu_queries=TopCpuQueriesResult(metadata=meta, top_n=20),
            top_io_queries=TopIoQueriesResult(metadata=meta, top_n=20),
        )
        response = client.get("/api/performance/snapshot?scope=server")
        assert response.status_code == 200
        data = response.json()
        assert data["scope"] == "server"
        mock_validate.assert_not_called()

    @patch("app.main.validate_target_database")
    @patch("app.main.collect_performance_snapshot")
    def test_snapshot_database_scope(self, mock_snapshot, mock_validate):
        mock_validate.return_value = None  # Valid database
        meta = CollectorMetadata(collector_name="test", server_host="srv", database="AppDB")
        mock_snapshot.return_value = PerformanceSnapshot(
            snapshot_id="scope-db",
            server_host="srv",
            scope="database",
            database="AppDB",
            active_requests=None,
            long_running_queries=LongRunningQueriesResult(metadata=meta, threshold_ms=5000),
            blocking_sessions=BlockingSessionsResult(metadata=meta),
            top_cpu_queries=TopCpuQueriesResult(metadata=meta, top_n=20),
            top_io_queries=TopIoQueriesResult(metadata=meta, top_n=20),
        )
        response = client.get("/api/performance/snapshot?scope=database&database=AppDB")
        assert response.status_code == 200
        data = response.json()
        assert data["scope"] == "database"
        assert data["database"] == "AppDB"

    def test_snapshot_database_scope_missing_db_param(self):
        response = client.get("/api/performance/snapshot?scope=database")
        assert response.status_code == 400
        assert "required" in response.json()["detail"]

    @patch("app.main.validate_target_database")
    def test_snapshot_database_scope_invalid_db(self, mock_validate):
        mock_validate.return_value = "Database 'bad;db' is not accessible or does not exist."
        response = client.get("/api/performance/snapshot?scope=database&database=bad;db")
        # The regex pattern on the scope param only validates scope, not database name
        # The database validation happens inside the endpoint logic
        assert response.status_code == 400

    def test_snapshot_invalid_scope(self):
        response = client.get("/api/performance/snapshot?scope=invalid")
        assert response.status_code == 422  # Pydantic/FastAPI validation error

    @patch("app.main.validate_target_database")
    @patch("app.main.analyze_performance")
    def test_analyze_server_scope(self, mock_analyze, mock_validate):
        mock_analyze.return_value = AnalysisResult(
            analysis_id="test-id",
            server="srv",
            scope="server",
            overall_status=OverallStatus.HEALTHY,
            findings=[],
            total_findings=0,
            critical_count=0,
            high_count=0,
            medium_count=0,
            low_count=0,
        )
        response = client.get("/api/performance/analyze?scope=server")
        assert response.status_code == 200
        data = response.json()
        assert data["scope"] == "server"
        assert data["overall_status"] == "HEALTHY"
        mock_validate.assert_not_called()

    @patch("app.main.validate_target_database")
    @patch("app.main.analyze_performance")
    def test_analyze_database_scope(self, mock_analyze, mock_validate):
        mock_validate.return_value = None
        mock_analyze.return_value = AnalysisResult(
            analysis_id="test-db",
            server="srv",
            scope="database",
            database="AppDB",
            overall_status=OverallStatus.WARNING,
            findings=[],
            total_findings=0,
            critical_count=0,
            high_count=0,
            medium_count=0,
            low_count=0,
        )
        response = client.get("/api/performance/analyze?scope=database&database=AppDB")
        assert response.status_code == 200
        data = response.json()
        assert data["scope"] == "database"
        assert data["database"] == "AppDB"

    def test_analyze_database_scope_missing_db(self):
        response = client.get("/api/performance/analyze?scope=database")
        assert response.status_code == 400
        assert "required" in response.json()["detail"]


# --- Databases endpoint ---


class TestDatabasesEndpoint:
    @patch("app.main.discover_databases")
    def test_list_databases_success(self, mock_discover):
        mock_discover.return_value = [
            DatabaseInfo("AppDB", "ONLINE"),
            DatabaseInfo("ReportDB", "ONLINE"),
        ]
        response = client.get("/api/databases")
        assert response.status_code == 200
        data = response.json()
        assert data["server"] == "localhost"
        assert len(data["databases"]) == 2
        assert data["databases"][0]["name"] == "AppDB"
        assert data["databases"][0]["status"] == "ONLINE"

    @patch("app.main.discover_databases")
    def test_list_databases_empty(self, mock_discover):
        mock_discover.return_value = []
        response = client.get("/api/databases")
        assert response.status_code == 200
        assert response.json()["databases"] == []

    @patch("app.main.discover_databases")
    def test_list_databases_connection_failure(self, mock_discover):
        mock_discover.side_effect = Exception("Connection failed")
        response = client.get("/api/databases")
        assert response.status_code == 503


# --- Analysis result model ---


class TestAnalysisResultModel:
    def test_analysis_result_includes_overall_status(self):
        result = AnalysisResult(
            analysis_id="model-test",
            server="srv",
            scope="server",
            overall_status=OverallStatus.DEGRADED,
            total_findings=1,
            critical_count=0,
            high_count=1,
            medium_count=0,
            low_count=0,
        )
        assert result.overall_status == OverallStatus.DEGRADED
        assert result.scope == "server"
        assert result.database is None

    def test_analysis_result_with_database_scope(self):
        result = AnalysisResult(
            analysis_id="model-db",
            server="srv",
            scope="database",
            database="TestDB",
            overall_status=OverallStatus.HEALTHY,
            total_findings=0,
            critical_count=0,
            high_count=0,
            medium_count=0,
            low_count=0,
        )
        assert result.scope == "database"
        assert result.database == "TestDB"

    def test_analysis_result_affected_databases(self):
        result = AnalysisResult(
            analysis_id="aff-db",
            server="srv",
            scope="server",
            overall_status=OverallStatus.WARNING,
            affected_databases=[
                AffectedDatabase(database="db1", highest_severity="critical", finding_count=2),
                AffectedDatabase(database="db2", highest_severity="medium", finding_count=1),
            ],
            total_findings=3,
            critical_count=1,
            high_count=0,
            medium_count=1,
            low_count=1,
        )
        assert len(result.affected_databases) == 2
        assert result.affected_databases[0].database == "db1"


# --- Root endpoint ---


class TestRootEndpoint:
    def test_root_includes_databases_endpoint(self):
        response = client.get("/")
        assert response.status_code == 200
        data = response.json()
        assert "databases" in data["endpoints"]
        assert data["endpoints"]["databases"] == "/api/databases"
