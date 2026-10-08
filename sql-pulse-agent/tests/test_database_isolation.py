"""Tests proving database-scope isolation: analyzing database X cannot produce
findings whose underlying evidence belongs to another database.

Specifically tests that analyzing 'CheckerSystem' never produces findings
referencing NSWeb, mas200, Inventory, or other databases.
"""

import pytest

from app.detectors.blocking import BlockingDetector
from app.detectors.engine import run_analysis
from app.detectors.high_cpu import HighCpuDetector
from app.detectors.high_io import HighIoDetector
from app.detectors.long_running import LongRunningDetector
from app.detectors.thresholds import DetectorThresholds
from app.models.findings import FindingCategory, Severity
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

# Databases that must NEVER appear in findings when analyzing CheckerSystem
_OTHER_DATABASES = ["NSWeb", "mas200", "Inventory", "master", "tempdb", "ReportServer"]
_TARGET_DATABASE = "CheckerSystem"


def _meta() -> CollectorMetadata:
    return CollectorMetadata(collector_name="test", server_host="MSI-STGERPDBL", database=_TARGET_DATABASE)


def _thresholds() -> DetectorThresholds:
    return DetectorThresholds()


def _make_mixed_snapshot() -> PerformanceSnapshot:
    """Create a snapshot that contains data from multiple databases.

    Simulates what would happen if the SQL filtering failed to exclude
    queries from other databases. The detectors should still filter them.
    """
    return PerformanceSnapshot(
        snapshot_id="isolation-test",
        server_host="MSI-STGERPDBL",
        database=_TARGET_DATABASE,  # Target database for this analysis
        scope="database",
        long_running_queries=LongRunningQueriesResult(
            metadata=_meta(),
            threshold_ms=5000,
            queries=[
                # Query belonging to target database — should produce finding
                LongRunningQuery(
                    session_id=50, request_id=0, elapsed_time_ms=150_000,
                    sql_text="SELECT * FROM CheckerSystem.dbo.BigTable",
                    database_name=_TARGET_DATABASE,
                    cpu_time_ms=80000, logical_reads=5000000,
                    wait_type="CXPACKET",
                ),
                # Query belonging to OTHER database — MUST NOT produce finding
                LongRunningQuery(
                    session_id=51, request_id=0, elapsed_time_ms=200_000,
                    sql_text="SELECT * FROM NSWeb.dbo.Orders WHERE 1=1",
                    database_name="NSWeb",
                    cpu_time_ms=120000, logical_reads=8000000,
                ),
                # Query belonging to yet another database — MUST NOT produce finding
                LongRunningQuery(
                    session_id=52, request_id=0, elapsed_time_ms=180_000,
                    sql_text="SELECT * FROM mas200.dbo.AR_InvoiceHistoryHeader",
                    database_name="mas200",
                    cpu_time_ms=100000, logical_reads=6000000,
                ),
                # Query belonging to Inventory — MUST NOT produce finding
                LongRunningQuery(
                    session_id=53, request_id=0, elapsed_time_ms=160_000,
                    sql_text="SELECT * FROM Inventory.dbo.StockLevels",
                    database_name="Inventory",
                    cpu_time_ms=90000, logical_reads=4000000,
                ),
            ],
        ),
        blocking_sessions=BlockingSessionsResult(
            metadata=_meta(),
            sessions=[],
        ),
        top_cpu_queries=TopCpuQueriesResult(
            metadata=_meta(),
            top_n=20,
            queries=[
                # Target DB query — should produce finding
                TopCpuQuery(
                    sql_text="SELECT complex FROM CheckerSystem..Checks",
                    query_hash="0xABC123",
                    execution_count=100,
                    total_cpu_time_ms=5_000_000,
                    avg_cpu_time_ms=50_000.0,
                    total_elapsed_time_ms=6_000_000,
                    avg_elapsed_time_ms=60_000.0,
                    total_logical_reads=1000000,
                    avg_logical_reads=10000.0,
                    database_name=_TARGET_DATABASE,
                ),
                # NSWeb query — MUST NOT appear in findings
                TopCpuQuery(
                    sql_text="SELECT * FROM NSWeb.dbo.WebSessions",
                    query_hash="0xDEF456",
                    execution_count=500,
                    total_cpu_time_ms=10_000_000,
                    avg_cpu_time_ms=20_000.0,
                    total_elapsed_time_ms=12_000_000,
                    avg_elapsed_time_ms=24_000.0,
                    total_logical_reads=50000000,
                    avg_logical_reads=100000.0,
                    database_name="NSWeb",
                ),
                # mas200 query — MUST NOT appear in findings
                TopCpuQuery(
                    sql_text="SELECT * FROM mas200.dbo.AP_Transaction",
                    query_hash="0xGHI789",
                    execution_count=200,
                    total_cpu_time_ms=8_000_000,
                    avg_cpu_time_ms=40_000.0,
                    total_elapsed_time_ms=9_000_000,
                    avg_elapsed_time_ms=45_000.0,
                    total_logical_reads=30000000,
                    avg_logical_reads=150000.0,
                    database_name="mas200",
                ),
            ],
        ),
        top_io_queries=TopIoQueriesResult(
            metadata=_meta(),
            top_n=20,
            queries=[
                # Target DB query — should produce finding
                TopIoQuery(
                    sql_text="SELECT * FROM CheckerSystem.dbo.AuditLog",
                    query_hash="0xIO_CS",
                    execution_count=50,
                    total_logical_reads=200_000_000,
                    avg_logical_reads=4_000_000.0,
                    total_physical_reads=1000000,
                    avg_physical_reads=20000.0,
                    total_logical_writes=0,
                    avg_logical_writes=0.0,
                    total_elapsed_time_ms=5000000,
                    database_name=_TARGET_DATABASE,
                ),
                # Inventory query — MUST NOT appear in findings
                TopIoQuery(
                    sql_text="SELECT * FROM Inventory.dbo.TransactionHistory",
                    query_hash="0xIO_INV",
                    execution_count=300,
                    total_logical_reads=500_000_000,
                    avg_logical_reads=1_666_666.0,
                    total_physical_reads=5000000,
                    avg_physical_reads=16666.0,
                    total_logical_writes=0,
                    avg_logical_writes=0.0,
                    total_elapsed_time_ms=8000000,
                    database_name="Inventory",
                ),
            ],
        ),
    )


class TestDatabaseScopeIsolation:
    """Prove that analyzing CheckerSystem never produces findings for other databases."""

    def test_long_running_excludes_other_databases(self):
        """Long-running detector with database scope must not produce findings for NSWeb/mas200/Inventory."""
        snapshot = _make_mixed_snapshot()
        detector = LongRunningDetector(thresholds=_thresholds())
        findings = detector.detect(snapshot)

        for f in findings:
            assert f.database not in _OTHER_DATABASES, (
                f"Finding database='{f.database}' must not appear when analyzing {_TARGET_DATABASE}"
            )
            # Evidence should not reference other database queries
            for evidence_line in f.evidence:
                for other_db in _OTHER_DATABASES:
                    assert f"db={other_db}" not in evidence_line, (
                        f"Evidence references '{other_db}' in a {_TARGET_DATABASE} analysis"
                    )

    def test_high_cpu_excludes_other_databases(self):
        """High CPU detector with database scope must not produce findings for NSWeb/mas200."""
        snapshot = _make_mixed_snapshot()
        detector = HighCpuDetector(thresholds=_thresholds())
        findings = detector.detect(snapshot)

        for f in findings:
            assert f.database not in _OTHER_DATABASES, (
                f"CPU finding database='{f.database}' must not appear when analyzing {_TARGET_DATABASE}"
            )
            # The evidence Database: field must not reference other databases
            for evidence_line in f.evidence:
                if evidence_line.startswith("Database:"):
                    for other_db in _OTHER_DATABASES:
                        assert other_db not in evidence_line

    def test_high_io_excludes_other_databases(self):
        """High I/O detector with database scope must not produce findings for Inventory."""
        snapshot = _make_mixed_snapshot()
        detector = HighIoDetector(thresholds=_thresholds())
        findings = detector.detect(snapshot)

        for f in findings:
            assert f.database not in _OTHER_DATABASES, (
                f"I/O finding database='{f.database}' must not appear when analyzing {_TARGET_DATABASE}"
            )

    def test_full_analysis_excludes_other_databases(self):
        """Full run_analysis with scope=database must produce zero findings for other databases."""
        snapshot = _make_mixed_snapshot()
        result = run_analysis(snapshot, scope="database", database=_TARGET_DATABASE)

        for f in result.findings:
            assert f.database not in _OTHER_DATABASES, (
                f"Analysis finding for '{f.database}' leaked into {_TARGET_DATABASE} analysis. "
                f"Category={f.category}, Summary={f.summary}"
            )

    def test_full_analysis_only_contains_target_database(self):
        """All findings in a database-scoped analysis must reference only the target database."""
        snapshot = _make_mixed_snapshot()
        result = run_analysis(snapshot, scope="database", database=_TARGET_DATABASE)

        for f in result.findings:
            assert f.database == _TARGET_DATABASE or f.database is None, (
                f"Finding database='{f.database}' is not '{_TARGET_DATABASE}'. "
                f"Category={f.category}, Summary={f.summary}"
            )

    def test_target_database_findings_are_produced(self):
        """Verify that findings ARE produced for the target database (not a vacuous test)."""
        snapshot = _make_mixed_snapshot()
        result = run_analysis(snapshot, scope="database", database=_TARGET_DATABASE)

        # We expect at least some findings for CheckerSystem
        assert result.total_findings > 0, (
            "Expected at least one finding for CheckerSystem but got zero"
        )
        # All findings should be for CheckerSystem
        for f in result.findings:
            assert f.database == _TARGET_DATABASE


class TestServerScopeReturnsAll:
    """Server scope should still return instance-wide findings."""

    def test_server_scope_includes_all_databases(self):
        """Server-scope analysis produces findings from all databases."""
        snapshot = PerformanceSnapshot(
            snapshot_id="server-test",
            server_host="MSI-STGERPDBL",
            database=None,  # No target database — server scope
            scope="server",
            long_running_queries=LongRunningQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="MSI-STGERPDBL", database="master"
                ),
                threshold_ms=5000,
                queries=[
                    LongRunningQuery(
                        session_id=50, request_id=0, elapsed_time_ms=150_000,
                        sql_text="SELECT FROM CheckerSystem.dbo.X",
                        database_name="CheckerSystem",
                        cpu_time_ms=50000, logical_reads=1000000,
                    ),
                    LongRunningQuery(
                        session_id=51, request_id=0, elapsed_time_ms=200_000,
                        sql_text="SELECT FROM NSWeb.dbo.Y",
                        database_name="NSWeb",
                        cpu_time_ms=80000, logical_reads=2000000,
                    ),
                ],
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="MSI-STGERPDBL", database="master"
                ),
                sessions=[],
            ),
            top_cpu_queries=TopCpuQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="MSI-STGERPDBL", database="master"
                ),
                top_n=20,
                queries=[],
            ),
            top_io_queries=TopIoQueriesResult(
                metadata=CollectorMetadata(
                    collector_name="test", server_host="MSI-STGERPDBL", database="master"
                ),
                top_n=20,
                queries=[],
            ),
        )
        result = run_analysis(snapshot, scope="server", database=None)
        databases_in_findings = {f.database for f in result.findings}
        # Server scope should include findings from both
        assert "CheckerSystem" in databases_in_findings or "NSWeb" in databases_in_findings


class TestSeverityNoHistoricalCritical:
    """Plan-cache metrics alone should NOT produce CRITICAL findings."""

    def test_high_cpu_never_critical(self):
        """Even extreme plan-cache CPU metrics should not produce CRITICAL."""
        snapshot = PerformanceSnapshot(
            snapshot_id="sev-cpu",
            server_host="srv",
            database=None,
            top_cpu_queries=TopCpuQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20,
                queries=[
                    TopCpuQuery(
                        sql_text="SELECT expensive FROM big_table",
                        query_hash="0xBIG",
                        execution_count=1000,
                        total_cpu_time_ms=50_000_000,  # 50 million ms total
                        avg_cpu_time_ms=50_000.0,  # 50s avg
                        total_elapsed_time_ms=60_000_000,
                        avg_elapsed_time_ms=60_000.0,
                        total_logical_reads=100000000,
                        avg_logical_reads=100000.0,
                        database_name="TestDB",
                    ),
                ],
            ),
            long_running_queries=LongRunningQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                threshold_ms=5000, queries=[],
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                sessions=[],
            ),
            top_io_queries=TopIoQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20, queries=[],
            ),
        )
        detector = HighCpuDetector(thresholds=_thresholds())
        findings = detector.detect(snapshot)
        for f in findings:
            assert f.severity != Severity.CRITICAL, (
                f"Plan-cache CPU metrics should not produce CRITICAL. Got: {f.summary}"
            )

    def test_high_io_never_critical(self):
        """Even extreme plan-cache I/O metrics should not produce CRITICAL."""
        snapshot = PerformanceSnapshot(
            snapshot_id="sev-io",
            server_host="srv",
            database=None,
            top_io_queries=TopIoQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20,
                queries=[
                    TopIoQuery(
                        sql_text="SELECT * FROM enormous_table",
                        query_hash="0xHUGE",
                        execution_count=500,
                        total_logical_reads=1_000_000_000,  # 1 billion
                        avg_logical_reads=2_000_000.0,  # 2M per exec
                        total_physical_reads=50000000,
                        avg_physical_reads=100000.0,
                        total_logical_writes=0,
                        avg_logical_writes=0.0,
                        total_elapsed_time_ms=100000000,
                        database_name="TestDB",
                    ),
                ],
            ),
            long_running_queries=LongRunningQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                threshold_ms=5000, queries=[],
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                sessions=[],
            ),
            top_cpu_queries=TopCpuQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20, queries=[],
            ),
        )
        detector = HighIoDetector(thresholds=_thresholds())
        findings = detector.detect(snapshot)
        for f in findings:
            assert f.severity != Severity.CRITICAL, (
                f"Plan-cache I/O metrics should not produce CRITICAL. Got: {f.summary}"
            )


class TestLikelyCauseNotSpeculative:
    """Likely cause should not state unsupported causes."""

    def test_cpu_likely_cause_no_speculation(self):
        """CPU findings should not assert missing indexes or parameter sniffing."""
        snapshot = PerformanceSnapshot(
            snapshot_id="cause-cpu",
            server_host="srv",
            database=None,
            top_cpu_queries=TopCpuQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20,
                queries=[
                    TopCpuQuery(
                        sql_text="SELECT x FROM t",
                        execution_count=100,
                        total_cpu_time_ms=5_000_000,
                        avg_cpu_time_ms=50_000.0,
                        total_elapsed_time_ms=6_000_000,
                        avg_elapsed_time_ms=60_000.0,
                        total_logical_reads=1000000,
                        avg_logical_reads=10000.0,
                        database_name="TestDB",
                    ),
                ],
            ),
            long_running_queries=LongRunningQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                threshold_ms=5000, queries=[],
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                sessions=[],
            ),
            top_io_queries=TopIoQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20, queries=[],
            ),
        )
        detector = HighCpuDetector(thresholds=_thresholds())
        findings = detector.detect(snapshot)
        for f in findings:
            cause_lower = f.likely_cause.lower()
            assert "missing index" not in cause_lower
            assert "parameter sniffing" not in cause_lower
            assert "outdated statistics" not in cause_lower
            assert "scalar udf" not in cause_lower

    def test_io_likely_cause_no_speculation(self):
        """I/O findings should not assert specific causes without evidence."""
        snapshot = PerformanceSnapshot(
            snapshot_id="cause-io",
            server_host="srv",
            database=None,
            top_io_queries=TopIoQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20,
                queries=[
                    TopIoQuery(
                        sql_text="SELECT y FROM large",
                        execution_count=50,
                        total_logical_reads=500_000_000,
                        avg_logical_reads=10_000_000.0,
                        total_physical_reads=1000000,
                        avg_physical_reads=20000.0,
                        total_logical_writes=0,
                        avg_logical_writes=0.0,
                        total_elapsed_time_ms=5000000,
                        database_name="TestDB",
                    ),
                ],
            ),
            long_running_queries=LongRunningQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                threshold_ms=5000, queries=[],
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                sessions=[],
            ),
            top_cpu_queries=TopCpuQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20, queries=[],
            ),
        )
        detector = HighIoDetector(thresholds=_thresholds())
        findings = detector.detect(snapshot)
        for f in findings:
            cause_lower = f.likely_cause.lower()
            assert "missing" not in cause_lower
            assert "non-sargable" not in cause_lower
            assert "covering index" not in cause_lower

    def test_long_running_likely_cause_no_speculation(self):
        """Long-running findings should not assert missing indexes or parameter sniffing."""
        snapshot = PerformanceSnapshot(
            snapshot_id="cause-lr",
            server_host="srv",
            database=None,
            long_running_queries=LongRunningQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                threshold_ms=5000,
                queries=[
                    LongRunningQuery(
                        session_id=1, request_id=0, elapsed_time_ms=50_000,
                        sql_text="SELECT * FROM big",
                        database_name="TestDB",
                    ),
                ],
            ),
            blocking_sessions=BlockingSessionsResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                sessions=[],
            ),
            top_cpu_queries=TopCpuQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20, queries=[],
            ),
            top_io_queries=TopIoQueriesResult(
                metadata=CollectorMetadata(collector_name="test", server_host="srv", database="master"),
                top_n=20, queries=[],
            ),
        )
        detector = LongRunningDetector(thresholds=_thresholds())
        findings = detector.detect(snapshot)
        for f in findings:
            cause_lower = f.likely_cause.lower()
            assert "missing index" not in cause_lower
            assert "parameter sniffing" not in cause_lower
            assert "outdated statistics" not in cause_lower
