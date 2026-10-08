"""Tests for server-level analysis accuracy.

These cover the behaviours that keep a server-scope analysis honest:
health status is not polluted by plan-cache optimization candidates, the same
query is not reported twice, SQLPulse's own monitoring traffic is filtered
without hiding real workload, unresolved databases are reported as null rather
than guessed, severity reflects both aggregate and per-execution impact, and
cumulative wait counters are only turned into a delta when one is meaningful.
"""

from datetime import UTC, datetime, timedelta
from unittest.mock import patch

from app.detectors.engine import run_analysis
from app.detectors.high_cpu import HighCpuDetector
from app.detectors.high_io import HighIoDetector
from app.detectors.query_filters import (
    REASON_DIAGNOSTIC_DMV,
    REASON_SQLPULSE_SELF,
    classify_exclusion,
)
from app.detectors.thresholds import DetectorThresholds
from app.models.findings import Confidence, FindingSignal, OverallStatus, Severity
from app.models.history import WaitTotalsRecord
from app.models.performance import (
    CollectorMetadata,
    PerformanceSnapshot,
    TopCpuQueriesResult,
    TopCpuQuery,
    TopIoQueriesResult,
    TopIoQuery,
    WaitDeltaStatus,
    WaitStatisticsApplicability,
    WaitStatisticsResult,
    WaitStatisticsTotals,
)
from app.storage.wait_stats_service import compute_delta

SERVER = "srv"


def _meta() -> CollectorMetadata:
    return CollectorMetadata(collector_name="test", server_host=SERVER, database="db")


def _snapshot(
    cpu: list[TopCpuQuery] | None = None,
    io: list[TopIoQuery] | None = None,
    database: str | None = None,
) -> PerformanceSnapshot:
    return PerformanceSnapshot(
        snapshot_id="snap-1",
        server_host=SERVER,
        scope="database" if database else "server",
        database=database,
        top_cpu_queries=TopCpuQueriesResult(metadata=_meta(), top_n=20, queries=cpu or []),
        top_io_queries=TopIoQueriesResult(metadata=_meta(), top_n=20, queries=io or []),
    )


def _thresholds() -> DetectorThresholds:
    return DetectorThresholds()


def _expensive_cpu_query(**overrides) -> TopCpuQuery:
    """A query that reliably produces a MEDIUM high-CPU finding."""
    defaults = dict(
        sql_text="SELECT * FROM dbo.Orders WHERE Total > 100",
        query_hash="0xAAAA",
        query_plan_hash="0xPPPP",
        database_name="AppDB",
        execution_count=50,
        total_cpu_time_ms=400_000,
        avg_cpu_time_ms=8_000.0,
    )
    defaults.update(overrides)
    return TopCpuQuery(**defaults)


def _expensive_io_query(**overrides) -> TopIoQuery:
    """A query that reliably produces a MEDIUM high-I/O finding."""
    defaults = dict(
        sql_text="SELECT * FROM dbo.Orders WHERE Total > 100",
        query_hash="0xAAAA",
        query_plan_hash="0xPPPP",
        database_name="AppDB",
        execution_count=50,
        total_logical_reads=3_000_000,
        avg_logical_reads=60_000.0,
    )
    defaults.update(overrides)
    return TopIoQuery(**defaults)


class TestCurrentHealthSeparation:
    def test_current_health_stays_healthy_with_only_optimization_opportunities(self):
        """Plan-cache findings describe past cost, never a live incident.

        A top-CPU query is a tuning candidate. Letting it degrade the health
        status would report an outage that is not happening.
        """
        result = run_analysis(
            _snapshot(cpu=[_expensive_cpu_query()]),
            thresholds=_thresholds(),
            scope="server",
        )

        assert result.optimization_opportunities, "expected a tuning candidate"
        assert result.current_health is not None
        assert result.current_health.findings == []
        assert result.current_health.status == OverallStatus.HEALTHY
        assert result.overall_status == OverallStatus.HEALTHY
        assert result.summary is not None
        assert result.summary.health_finding_count == 0
        assert result.summary.optimization_opportunity_count == len(
            result.optimization_opportunities
        )
        assert result.summary.current_health_status == OverallStatus.HEALTHY


class TestSignalCorrelation:
    def test_same_query_high_cpu_and_high_io_is_one_finding(self):
        """One query with two symptoms is one problem, not two findings."""
        result = run_analysis(
            _snapshot(cpu=[_expensive_cpu_query()], io=[_expensive_io_query()]),
            thresholds=_thresholds(),
            scope="server",
        )

        assert result.total_findings == 1
        finding = result.findings[0]
        assert set(finding.signals) == {FindingSignal.HIGH_CPU, FindingSignal.HIGH_IO}
        assert finding.query_identity is not None
        assert finding.query_identity.query_hash == "0xAAAA"
        assert finding.query_identity.query_plan_hash == "0xPPPP"
        # Evidence from both signals must survive the merge.
        joined = " ".join(finding.evidence)
        assert "Avg CPU time per execution" in joined
        assert "[high_io]" in joined
        assert "Avg logical reads per execution" in joined

    def test_different_queries_are_not_merged(self):
        """Distinct query hashes stay distinct findings."""
        result = run_analysis(
            _snapshot(
                cpu=[_expensive_cpu_query(query_hash="0xAAAA")],
                io=[_expensive_io_query(query_hash="0xBBBB", query_plan_hash="0xQQQQ")],
            ),
            thresholds=_thresholds(),
            scope="server",
        )
        assert result.total_findings == 2

    def test_correlation_does_not_inflate_confidence(self):
        """Both readings come from the same cached statistics, so they are not
        independent corroboration and must not raise confidence."""
        result = run_analysis(
            _snapshot(cpu=[_expensive_cpu_query()], io=[_expensive_io_query()]),
            thresholds=_thresholds(),
            scope="server",
        )
        assert result.findings[0].confidence == Confidence.MEDIUM


class TestMonitoringQueryExclusion:
    def test_sqlpulse_monitoring_queries_are_excluded(self):
        """SQLPulse reads the plan cache, so it sees itself. It must not report
        its own diagnostics as a tuning opportunity."""
        monitoring = _expensive_cpu_query(
            sql_text=(
                "/* SQLPulse:monitoring */ SELECT TOP 20 * "
                "FROM sys.dm_exec_query_stats"
            ),
        )
        result = run_analysis(
            _snapshot(cpu=[monitoring]), thresholds=_thresholds(), scope="server"
        )

        assert result.findings == []
        assert len(result.excluded_queries) == 1
        assert result.excluded_queries[0].reason == REASON_SQLPULSE_SELF
        assert result.summary is not None
        assert result.summary.excluded_query_count == 1

    def test_third_party_diagnostic_dmv_query_is_excluded(self):
        """Another monitoring tool's DMV read is diagnostic noise too."""
        assert (
            classify_exclusion("SELECT * FROM sys.dm_os_wait_stats")
            == REASON_DIAGNOSTIC_DMV
        )

    def test_legitimate_system_object_queries_are_not_excluded(self):
        """ORMs and migration tools read sys.objects / sys.tables / sys.columns
        constantly. Filtering those would hide real business workload."""
        for sql in (
            "SELECT name FROM sys.objects WHERE type = 'U'",
            "SELECT * FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id",
            "SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Orders')",
            "SELECT * FROM sys.partitions",
        ):
            assert classify_exclusion(sql) is None, sql

        catalog_query = _expensive_cpu_query(
            sql_text="SELECT name FROM sys.objects WHERE type = 'U'",
        )
        result = run_analysis(
            _snapshot(cpu=[catalog_query]), thresholds=_thresholds(), scope="server"
        )
        assert len(result.findings) == 1
        assert result.excluded_queries == []

    def test_same_excluded_query_reported_once_across_detectors(self):
        """A monitoring query appears in both the CPU and I/O lists; the
        exclusion report must not double-count it."""
        sql = "/* SQLPulse:monitoring */ SELECT * FROM sys.dm_exec_query_stats"
        result = run_analysis(
            _snapshot(
                cpu=[_expensive_cpu_query(sql_text=sql)],
                io=[_expensive_io_query(sql_text=sql)],
            ),
            thresholds=_thresholds(),
            scope="server",
        )
        assert len(result.excluded_queries) == 1


class TestUnresolvedDatabase:
    def test_unknown_database_is_null_not_a_placeholder(self):
        """Plan-cache rows often carry no database. Reporting "unknown" invents
        an object name that does not exist on the server."""
        detector = HighCpuDetector(thresholds=_thresholds())
        findings = detector.detect(_snapshot(cpu=[_expensive_cpu_query(database_name=None)]))

        assert len(findings) == 1
        assert findings[0].database is None

    def test_unresolved_database_reduces_confidence(self):
        """A finding that cannot be attributed to a database is weaker evidence
        than one that can."""
        resolved = HighCpuDetector(thresholds=_thresholds()).detect(
            _snapshot(cpu=[_expensive_cpu_query(database_name="AppDB")])
        )[0]
        unresolved = HighCpuDetector(thresholds=_thresholds()).detect(
            _snapshot(cpu=[_expensive_cpu_query(database_name=None)])
        )[0]

        assert resolved.confidence == Confidence.MEDIUM
        assert unresolved.confidence == Confidence.LOW
        assert "database unresolved" in (unresolved.severity_basis or "")

    def test_unresolved_database_is_not_counted_as_affected(self):
        result = run_analysis(
            _snapshot(cpu=[_expensive_cpu_query(database_name=None)]),
            thresholds=_thresholds(),
            scope="server",
        )
        assert result.affected_databases == []
        assert result.summary is not None
        assert result.summary.affected_database_count == 0


class TestDrilldown:
    def test_valid_database_gets_drilldown(self):
        result = run_analysis(
            _snapshot(cpu=[_expensive_cpu_query(database_name="AppDB")]),
            thresholds=_thresholds(),
            scope="server",
        )
        finding = result.findings[0]
        assert finding.recommended_drilldown is not None
        assert finding.recommended_drilldown.scope == "database"
        assert finding.recommended_drilldown.database == "AppDB"

    def test_unresolved_database_gets_no_drilldown(self):
        """Pointing the user at a database SQLPulse could not name is a dead end."""
        result = run_analysis(
            _snapshot(cpu=[_expensive_cpu_query(database_name=None)]),
            thresholds=_thresholds(),
            scope="server",
        )
        assert result.findings[0].recommended_drilldown is None


class TestWaitStatisticsApplicability:
    @patch("app.collectors.wait_stats.WaitStatisticsCollector._execute_query")
    def test_server_scope_reports_wait_statistics_with_totals(self, mock_query):
        from app.collectors.wait_stats import WaitStatisticsCollector

        started = datetime(2024, 1, 1, tzinfo=UTC)
        mock_query.side_effect = [
            [{
                "wait_type": "PAGEIOLATCH_SH",
                "waiting_tasks_count": 100,
                "wait_time_ms": 5_000,
                "max_wait_time_ms": 250,
                "signal_wait_time_ms": 500,
                "resource_wait_time_ms": 4_500,
            }],
            [{
                "waiting_tasks_count": 1_000,
                "wait_time_ms": 50_000,
                "signal_wait_time_ms": 5_000,
                "resource_wait_time_ms": 45_000,
                "sqlserver_start_time": started,
            }],
        ]

        result = WaitStatisticsCollector(top_n=25).collect()

        assert result.applicability == WaitStatisticsApplicability.SERVER_LEVEL
        assert result.statistics[0].wait_type == "PAGEIOLATCH_SH"
        assert result.totals is not None
        assert result.totals.wait_time_ms == 50_000
        assert result.totals.waiting_tasks_count == 1_000
        assert result.sqlserver_start_time == started

    @patch("app.collectors.snapshot.TopIoQueriesCollector.collect")
    @patch("app.collectors.snapshot.TopCpuQueriesCollector.collect")
    @patch("app.collectors.snapshot.BlockingSessionsCollector.collect")
    @patch("app.collectors.snapshot.LongRunningQueriesCollector.collect")
    @patch("app.collectors.snapshot.ActiveRequestsCollector.collect")
    def test_database_scope_reports_wait_statistics_not_applicable(
        self, mock_active, mock_long, mock_blocking, mock_cpu, mock_io
    ):
        """sys.dm_os_wait_stats is instance-wide, so a database-scope answer
        must say "not applicable" rather than return instance numbers as if
        they belonged to the database."""
        from app.collectors.snapshot import collect_performance_snapshot
        from app.models.performance import (
            ActiveRequestsResult,
            BlockingSessionsResult,
            LongRunningQueriesResult,
        )

        meta = _meta()
        mock_active.return_value = ActiveRequestsResult(metadata=meta)
        mock_long.return_value = LongRunningQueriesResult(metadata=meta, threshold_ms=5000)
        mock_blocking.return_value = BlockingSessionsResult(metadata=meta)
        mock_cpu.return_value = TopCpuQueriesResult(metadata=meta, top_n=20)
        mock_io.return_value = TopIoQueriesResult(metadata=meta, top_n=20)

        snapshot = collect_performance_snapshot(scope="database", database="AppDB")

        waits = snapshot.wait_statistics
        assert waits is not None
        assert (
            waits.applicability
            == WaitStatisticsApplicability.NOT_APPLICABLE_DATABASE_SCOPE
        )
        assert waits.delta_status == WaitDeltaStatus.NOT_APPLICABLE
        assert waits.statistics == []
        assert waits.totals is None


class TestWaitStatisticsDelta:
    @staticmethod
    def _result(
        collected_at: datetime,
        started: datetime | None,
        totals: WaitStatisticsTotals,
    ) -> WaitStatisticsResult:
        return WaitStatisticsResult(
            metadata=_meta(),
            top_n=25,
            collected_at=collected_at,
            sqlserver_start_time=started,
            totals=totals,
        )

    def test_first_snapshot_produces_no_fabricated_delta(self):
        """With nothing to compare against, the only honest answer is "none"."""
        current = self._result(
            datetime(2024, 6, 1, 12, 0, tzinfo=UTC),
            datetime(2024, 1, 1, tzinfo=UTC),
            WaitStatisticsTotals(
                waiting_tasks_count=1_000,
                wait_time_ms=50_000,
                signal_wait_time_ms=5_000,
                resource_wait_time_ms=45_000,
            ),
        )

        delta, status = compute_delta(current, previous=None)

        assert delta is None
        assert status == WaitDeltaStatus.INSUFFICIENT_PREVIOUS_DATA

    def test_second_snapshot_produces_correct_delta(self):
        started = datetime(2024, 1, 1, tzinfo=UTC)
        previous = WaitTotalsRecord(
            snapshot_id="snap-0",
            collected_at=datetime(2024, 6, 1, 12, 0, tzinfo=UTC),
            server=SERVER,
            sqlserver_start_time=started,
            waiting_tasks_count=1_000,
            wait_time_ms=50_000,
            signal_wait_time_ms=5_000,
            resource_wait_time_ms=45_000,
        )
        current = self._result(
            previous.collected_at + timedelta(seconds=60),
            started,
            WaitStatisticsTotals(
                waiting_tasks_count=1_250,
                wait_time_ms=62_000,
                signal_wait_time_ms=6_500,
                resource_wait_time_ms=55_500,
            ),
        )

        delta, status = compute_delta(current, previous)

        assert status == WaitDeltaStatus.AVAILABLE
        assert delta is not None
        assert delta.previous_collected_at == previous.collected_at
        assert delta.interval_seconds == 60.0
        assert delta.waiting_tasks_count == 250
        assert delta.wait_time_ms == 12_000
        assert delta.signal_wait_time_ms == 1_500
        assert delta.resource_wait_time_ms == 10_500

    def test_restart_between_snapshots_produces_no_delta(self):
        """A restart zeroes the counters, so subtracting across one is invalid."""
        previous = WaitTotalsRecord(
            snapshot_id="snap-0",
            collected_at=datetime(2024, 6, 1, 12, 0, tzinfo=UTC),
            server=SERVER,
            sqlserver_start_time=datetime(2024, 1, 1, tzinfo=UTC),
            waiting_tasks_count=1_000,
            wait_time_ms=50_000,
        )
        current = self._result(
            datetime(2024, 6, 1, 12, 1, tzinfo=UTC),
            datetime(2024, 6, 1, 11, 59, tzinfo=UTC),
            WaitStatisticsTotals(waiting_tasks_count=10, wait_time_ms=100),
        )

        delta, status = compute_delta(current, previous)

        assert delta is None
        assert status == WaitDeltaStatus.SERVER_RESTARTED


class TestCpuSeverityBasis:
    def test_high_per_execution_cost_drives_severity(self):
        findings = HighCpuDetector(thresholds=_thresholds()).detect(
            _snapshot(cpu=[_expensive_cpu_query(
                execution_count=10,
                total_cpu_time_ms=80_000,
                avg_cpu_time_ms=8_000.0,
            )])
        )
        assert findings[0].severity == Severity.MEDIUM
        assert "avg CPU/exec" in (findings[0].severity_basis or "")

    def test_high_total_with_trivial_per_execution_cost_is_not_a_finding(self):
        """A query costing 0.5ms per run is not a tuning target no matter how
        often it runs — that is a workload-volume fact, not a query problem."""
        findings = HighCpuDetector(thresholds=_thresholds()).detect(
            _snapshot(cpu=[_expensive_cpu_query(
                execution_count=8_000_000,
                total_cpu_time_ms=4_000_000,
                avg_cpu_time_ms=0.5,
            )])
        )
        assert findings == []

    def test_high_total_with_significant_per_execution_cost_is_low_severity(self):
        findings = HighCpuDetector(thresholds=_thresholds()).detect(
            _snapshot(cpu=[_expensive_cpu_query(
                execution_count=200_000,
                total_cpu_time_ms=4_000_000,
                avg_cpu_time_ms=20.0,
            )])
        )
        assert len(findings) == 1
        assert findings[0].severity == Severity.LOW
        assert "aggregate cost only" in (findings[0].severity_basis or "")


class TestIoSeverityBasis:
    def test_high_per_execution_reads_drive_severity(self):
        findings = HighIoDetector(thresholds=_thresholds()).detect(
            _snapshot(io=[_expensive_io_query(
                execution_count=20,
                total_logical_reads=1_200_000,
                avg_logical_reads=60_000.0,
            )])
        )
        assert findings[0].severity == Severity.MEDIUM
        assert "avg logical reads/exec" in (findings[0].severity_basis or "")

    def test_high_total_with_trivial_per_execution_reads_is_not_a_finding(self):
        findings = HighIoDetector(thresholds=_thresholds()).detect(
            _snapshot(io=[_expensive_io_query(
                execution_count=20_000_000,
                total_logical_reads=200_000_000,
                avg_logical_reads=10.0,
            )])
        )
        assert findings == []

    def test_high_total_with_significant_per_execution_reads_is_low_severity(self):
        findings = HighIoDetector(thresholds=_thresholds()).detect(
            _snapshot(io=[_expensive_io_query(
                execution_count=400_000,
                total_logical_reads=120_000_000,
                avg_logical_reads=300.0,
            )])
        )
        assert len(findings) == 1
        assert findings[0].severity == Severity.LOW
        assert "aggregate cost only" in (findings[0].severity_basis or "")


class TestConfidenceReflectsEvidenceQuality:
    def test_single_cached_plan_observation_never_reaches_high_confidence(self):
        """One reading of lifetime plan-cache statistics cannot prove a current
        problem, so it can never be HIGH confidence."""
        cpu = HighCpuDetector(thresholds=_thresholds()).detect(
            _snapshot(cpu=[_expensive_cpu_query(avg_cpu_time_ms=120_000.0)])
        )
        io = HighIoDetector(thresholds=_thresholds()).detect(
            _snapshot(io=[_expensive_io_query(avg_logical_reads=2_000_000.0)])
        )

        assert cpu[0].severity == Severity.HIGH
        assert cpu[0].confidence != Confidence.HIGH
        assert io[0].severity == Severity.HIGH
        assert io[0].confidence != Confidence.HIGH

    def test_aggregate_only_finding_is_low_confidence(self):
        """Aggregate totals describe workload volume, which is the weakest
        basis for claiming a query is a problem."""
        findings = HighCpuDetector(thresholds=_thresholds()).detect(
            _snapshot(cpu=[_expensive_cpu_query(
                execution_count=200_000,
                total_cpu_time_ms=4_000_000,
                avg_cpu_time_ms=20.0,
            )])
        )
        assert findings[0].confidence == Confidence.LOW

    def test_every_finding_records_its_severity_basis(self):
        result = run_analysis(
            _snapshot(cpu=[_expensive_cpu_query()], io=[
                _expensive_io_query(query_hash="0xBBBB", query_plan_hash="0xQQQQ")
            ]),
            thresholds=_thresholds(),
            scope="server",
        )
        assert result.findings
        for finding in result.findings:
            assert finding.severity_basis
