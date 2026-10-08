using Microsoft.Extensions.Logging.Abstractions;
using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Analysis;
using SQLPulseAPI.Net8.Services.History;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Correlation, health separation and roll-up behaviour of the analysis engine.
/// </summary>
public sealed class AnalysisEngineTests
{
    private sealed class StubSnapshotService(PerformanceSnapshot snapshot) : IPerformanceSnapshotService
    {
        public PerformanceSnapshotRequest? LastRequest { get; private set; }

        public Task<PerformanceSnapshot> CollectAsync(
            PerformanceSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(snapshot);
        }
    }

    private static AnalysisEngine CreateEngine(
        PerformanceSnapshot? snapshot = null,
        IBaselineService? baselineService = null,
        IHistoryService? historyService = null) => new(
        new StubSnapshotService(snapshot ?? SnapshotBuilder.Snapshot()),
        new LongRunningDetector(SnapshotBuilder.Options),
        new BlockingDetector(SnapshotBuilder.Options),
        new HighCpuDetector(SnapshotBuilder.Options),
        new HighIoDetector(SnapshotBuilder.Options),
        new QueryRegressionDetector(baselineService ?? new StubBaselineService(), SnapshotBuilder.Options),
        historyService ?? new RecordingHistoryService(),
        NullLogger<AnalysisEngine>.Instance);

    private static AnalysisResult Analyze(
        PerformanceSnapshot snapshot,
        AnalysisScope scope = AnalysisScope.Server,
        string? database = null) =>
        CreateEngine().RunAnalysisAsync(snapshot, scope, database).GetAwaiter().GetResult();

    [Fact]
    public void An_empty_snapshot_is_healthy()
    {
        var result = Analyze(SnapshotBuilder.Snapshot());

        Assert.Equal(OverallStatus.Healthy, result.OverallStatus);
        Assert.Empty(result.Findings);
        Assert.Equal(0, result.TotalFindings);
        Assert.Empty(result.AffectedDatabases);
        Assert.Null(result.Summary!.HighestSeverity);
    }

    [Fact]
    public void Result_identifies_the_run()
    {
        var result = Analyze(SnapshotBuilder.Snapshot(), AnalysisScope.Database, "AppDb");

        Assert.NotEmpty(result.AnalysisId);
        Assert.Equal(SnapshotBuilder.Server, result.Server);
        Assert.Equal(AnalysisScope.Database, result.Scope);
        Assert.Equal("AppDb", result.Database);
        Assert.True(result.AnalyzedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public void Cpu_and_io_findings_for_the_same_query_become_one_finding()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 60_000));

        var result = Analyze(snapshot);

        // One query that is both CPU- and read-heavy is one problem with two symptoms.
        var finding = Assert.Single(result.Findings);
        Assert.Equal(new[] { FindingSignal.HighCpu, FindingSignal.HighIo }, finding.Signals);
        Assert.Equal(1, result.TotalFindings);
    }

    [Fact]
    public void Merging_keeps_the_evidence_from_both_signals()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 60_000));

        var finding = Assert.Single(Analyze(snapshot).Findings);

        // Absorbed metrics are labelled, so it stays clear which signal each came from.
        Assert.Contains(finding.Evidence, line => line.StartsWith("[high_io]", StringComparison.Ordinal));
        Assert.Contains(finding.Evidence, line => line.Contains("Avg CPU time per execution", StringComparison.Ordinal));
        Assert.Contains(finding.Evidence, line => line.Contains("Avg logical reads", StringComparison.Ordinal));
    }

    [Fact]
    public void Merging_takes_the_more_serious_severity()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 6_000));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000));

        var finding = Assert.Single(Analyze(snapshot).Findings);

        // CPU was MEDIUM, I/O was HIGH — the combined finding inherits HIGH.
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(FindingCategory.HighIo, finding.Category);
    }

    [Fact]
    public void Merging_never_pushes_confidence_above_the_strongest_input()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000));

        var finding = Assert.Single(Analyze(snapshot).Findings);

        // Both readings come from the same cached statistics, so agreement between them
        // is not independent corroboration and must not raise confidence.
        Assert.Equal(Confidence.Medium, finding.Confidence);
    }

    [Fact]
    public void Merging_combines_the_severity_basis_of_both_signals()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000));

        var finding = Assert.Single(Analyze(snapshot).Findings);

        Assert.Contains("avg CPU/exec", finding.SeverityBasis!, StringComparison.Ordinal);
        Assert.Contains("avg logical reads/exec", finding.SeverityBasis!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_plan_hash_is_a_different_query()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, planHash: "0xPLAN1"));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000, planHash: "0xPLAN2"));

        // The same statement under two plans is two different performance stories.
        Assert.Equal(2, Analyze(snapshot).Findings.Count);
    }

    [Fact]
    public void A_different_database_is_a_different_query()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: "AppDb"));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000, database: "OtherDb"));

        Assert.Equal(2, Analyze(snapshot).Findings.Count);
    }

    [Fact]
    public void Findings_without_a_query_hash_are_never_merged()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, queryHash: null));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000, queryHash: null));

        // Merging on weak identity would combine unrelated queries.
        Assert.Equal(2, Analyze(snapshot).Findings.Count);
    }

    [Fact]
    public void Long_running_findings_are_never_merged_into_plan_cache_findings()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(51, 150_000));
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));

        // Live activity and lifetime statistics answer different questions.
        Assert.Equal(2, Analyze(snapshot).Findings.Count);
    }

    [Fact]
    public void Cached_expensive_queries_alone_leave_the_server_healthy()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000));

        var result = Analyze(snapshot);

        // Plan-cache statistics accumulate since the plan was cached. They say nothing
        // about whether the server is struggling right now, so they must not degrade it.
        Assert.Equal(OverallStatus.Healthy, result.OverallStatus);
        Assert.Empty(result.CurrentHealth!.Findings);
        Assert.Equal(OverallStatus.Healthy, result.CurrentHealth.Status);
        Assert.Single(result.OptimizationOpportunities);
        Assert.Equal(1, result.TotalFindings);
        Assert.Equal(1, result.HighCount);
    }

    [Fact]
    public void Blocking_degrades_current_health()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking(SnapshotBuilder.Blocking(51, 61_000, blockedBy: 60));
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));

        var result = Analyze(snapshot);

        Assert.Equal(OverallStatus.Critical, result.OverallStatus);
        Assert.Single(result.CurrentHealth!.Findings);
        Assert.Single(result.OptimizationOpportunities);
        Assert.Equal(2, result.TotalFindings);
    }

    [Theory]
    [InlineData(150_000, OverallStatus.Degraded)]
    [InlineData(45_000, OverallStatus.Warning)]
    public void Live_severity_maps_to_overall_status(long elapsedMs, OverallStatus expected)
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(51, elapsedMs));

        Assert.Equal(expected, Analyze(snapshot).OverallStatus);
    }

    [Fact]
    public void Low_severity_health_findings_leave_the_server_healthy()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(
            SnapshotBuilder.LongRunning(51, 12_000),
            SnapshotBuilder.LongRunning(52, 13_000),
            SnapshotBuilder.LongRunning(53, 14_000));

        var result = Analyze(snapshot);

        // Worth noting, not worth alarming: a handful of mid-tier queries is normal.
        Assert.Equal(OverallStatus.Healthy, result.OverallStatus);
        Assert.Single(result.CurrentHealth!.Findings);
    }

    [Fact]
    public void Findings_are_ordered_by_severity_then_confidence()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking(SnapshotBuilder.Blocking(51, 61_000, blockedBy: 60));
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(52, 45_000));
        snapshot.WithCpu(
            SnapshotBuilder.Cpu(avgCpuMs: 31_000, queryHash: "0x1", planHash: "0x1"),
            SnapshotBuilder.Cpu(
                avgCpuMs: 100,
                executionCount: 50_000,
                queryHash: "0x2",
                planHash: "0x2",
                totalCpuMs: 5_000_000));

        var severities = Analyze(snapshot).Findings.Select(f => f.Severity).ToList();

        Assert.Equal(
            new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low },
            severities);
    }

    [Fact]
    public void Confidence_orders_from_strongest_to_weakest_within_a_severity()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(
            // MEDIUM / MEDIUM confidence.
            SnapshotBuilder.Cpu(avgCpuMs: 6_000, queryHash: "0x1", planHash: "0x1"),
            // MEDIUM severity, weakened to LOW confidence by the unresolved database.
            SnapshotBuilder.Cpu(avgCpuMs: 6_000, queryHash: "0x2", planHash: "0x2", database: null));

        var confidences = Analyze(snapshot).Findings.Select(f => f.Confidence).ToList();

        // Semantic order, strongest first — not the lexical order of the wire values.
        Assert.Equal(new[] { Confidence.Medium, Confidence.Low }, confidences);
    }

    [Fact]
    public void Server_scope_findings_offer_a_database_drilldown()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: "AppDb"));

        var finding = Assert.Single(Analyze(snapshot).Findings);

        Assert.NotNull(finding.RecommendedDrilldown);
        Assert.Equal(AnalysisScope.Database, finding.RecommendedDrilldown.Scope);
        Assert.Equal("AppDb", finding.RecommendedDrilldown.Database);
    }

    [Fact]
    public void An_unresolved_database_gets_no_drilldown()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: null));

        var finding = Assert.Single(Analyze(snapshot).Findings);

        // Pointing an operator at a database SQLPulse could not name is a dead end.
        Assert.Null(finding.RecommendedDrilldown);
        Assert.Null(finding.Database);
    }

    [Fact]
    public void Database_scope_offers_no_drilldown()
    {
        var snapshot = SnapshotBuilder.Snapshot(AnalysisScope.Database, "AppDb");
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: "AppDb"));

        var result = Analyze(snapshot, AnalysisScope.Database, "AppDb");
        var finding = Assert.Single(result.Findings);

        // Already at the finest scope.
        Assert.Null(finding.RecommendedDrilldown);
        Assert.Empty(result.AffectedDatabases);
    }

    [Fact]
    public void Low_severity_findings_get_no_drilldown()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(
            avgCpuMs: 100,
            executionCount: 50_000,
            totalCpuMs: 5_000_000));

        var finding = Assert.Single(Analyze(snapshot).Findings);

        Assert.Null(finding.RecommendedDrilldown);
    }

    [Fact]
    public void Affected_databases_are_ranked_by_their_worst_finding()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(
            SnapshotBuilder.Cpu(avgCpuMs: 6_000, database: "MediumDb", queryHash: "0x1", planHash: "0x1"),
            SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: "HighDb", queryHash: "0x2", planHash: "0x2"),
            SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: "HighDb", queryHash: "0x3", planHash: "0x3"));

        var affected = Analyze(snapshot).AffectedDatabases;

        Assert.Equal(2, affected.Count);
        Assert.Equal("HighDb", affected[0].Database);
        Assert.Equal(Severity.High, affected[0].HighestSeverity);
        Assert.Equal(2, affected[0].FindingCount);
        Assert.Equal("MediumDb", affected[1].Database);
    }

    [Fact]
    public void Affected_databases_skip_findings_with_no_database()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(
            SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: null, queryHash: "0x1", planHash: "0x1"),
            SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: "AppDb", queryHash: "0x2", planHash: "0x2"));

        var result = Analyze(snapshot);

        // Never invent a name to make the roll-up look complete.
        Assert.Equal(2, result.Findings.Count);
        var affected = Assert.Single(result.AffectedDatabases);
        Assert.Equal("AppDb", affected.Database);
    }

    [Fact]
    public void The_same_excluded_query_is_reported_once()
    {
        const string monitoring = "SELECT * FROM sys.dm_exec_query_stats -- SQLPulse:monitoring";
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, sqlText: monitoring));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000, sqlText: monitoring));

        var result = Analyze(snapshot);

        // A monitoring query appears in both lists; double-counting it would misrepresent
        // how much was hidden.
        Assert.Empty(result.Findings);
        Assert.Single(result.ExcludedQueries);
        Assert.Equal(1, result.Summary!.ExcludedQueryCount);
    }

    [Fact]
    public void Summary_counts_match_the_findings()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking(SnapshotBuilder.Blocking(51, 61_000, blockedBy: 60));
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(52, 45_000));
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));

        var result = Analyze(snapshot);
        var summary = result.Summary!;

        Assert.Equal(3, result.TotalFindings);
        Assert.Equal(3, summary.TotalFindings);
        Assert.Equal(1, summary.CriticalCount);
        Assert.Equal(1, summary.HighCount);
        Assert.Equal(1, summary.MediumCount);
        Assert.Equal(0, summary.LowCount);
        Assert.Equal(Severity.Critical, summary.HighestSeverity);
        Assert.Equal(2, summary.HealthFindingCount);
        Assert.Equal(1, summary.OptimizationOpportunityCount);
        Assert.Equal(OverallStatus.Critical, summary.CurrentHealthStatus);
        Assert.Equal(1, summary.AffectedDatabaseCount);
    }

    [Fact]
    public void Historical_analysis_is_reported_as_available_when_baselines_were_consulted()
    {
        // The regression detector ran; it simply found no READY baseline to compare
        // against. That is a different fact from "historical analysis did not happen".
        Assert.Equal(
            HistoricalAnalysisStatus.Available,
            Analyze(SnapshotBuilder.Snapshot()).HistoricalAnalysisStatus);
    }

    [Fact]
    public async Task Historical_analysis_is_reported_as_unavailable_when_storage_fails()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 100));

        var engine = CreateEngine(
            snapshot,
            baselineService: new ThrowingBaselineService());

        var result = await engine.RunAnalysisAsync(snapshot, AnalysisScope.Server, null);

        Assert.Equal(HistoricalAnalysisStatus.Unavailable, result.HistoricalAnalysisStatus);
    }

    /// <summary>Fails on every lookup, unlike the stub which returns no baseline.</summary>
    private sealed class ThrowingBaselineService : IBaselineService
    {
        public Task<Models.History.QueryBaseline> CalculateAsync(
            string queryHash,
            string database,
            string window = BaselineService.DefaultWindow,
            int? minSamples = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated storage failure");

        public Task<Models.History.QueryBaseline?> GetForRegressionAsync(
            string queryHash,
            string database,
            string window = BaselineService.DefaultWindow,
            int? minSamples = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated storage failure");
    }

    [Fact]
    public async Task Analyze_passes_the_request_through_to_the_collector()
    {
        var snapshot = SnapshotBuilder.Snapshot(AnalysisScope.Database, "AppDb");
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000));

        var snapshotService = new StubSnapshotService(snapshot);
        var historyService = new RecordingHistoryService();
        var engine = new AnalysisEngine(
            snapshotService,
            new LongRunningDetector(SnapshotBuilder.Options),
            new BlockingDetector(SnapshotBuilder.Options),
            new HighCpuDetector(SnapshotBuilder.Options),
            new HighIoDetector(SnapshotBuilder.Options),
            new QueryRegressionDetector(new StubBaselineService(), SnapshotBuilder.Options),
            historyService,
            NullLogger<AnalysisEngine>.Instance);

        var result = await engine.AnalyzeAsync(
            new AnalysisRequest(AnalysisScope.Database, "AppDb", 7000, 10, 15));

        Assert.Equal(AnalysisScope.Database, snapshotService.LastRequest!.Scope);
        Assert.Equal("AppDb", snapshotService.LastRequest.Database);
        Assert.Equal(7000, snapshotService.LastRequest.LongRunningThresholdMs);
        Assert.Equal(10, snapshotService.LastRequest.TopN);
        Assert.Equal(15, snapshotService.LastRequest.WaitStatsTopN);
        Assert.Single(result.Findings);
        Assert.Same(snapshot, Assert.Single(historyService.Persisted));
    }
}
