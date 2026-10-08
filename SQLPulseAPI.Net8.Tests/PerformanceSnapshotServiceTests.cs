using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Collectors;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Snapshot orchestration: scope handling, database attribution, and the guarantee that
/// one failing collector cannot take down the whole snapshot.
/// </summary>
public class PerformanceSnapshotServiceTests
{
    private const string Server = "sql01";

    private static PerformanceSnapshotService Build(ISqlQueryExecutor executor)
    {
        var options = Options.Create(new SqlServerOptions
        {
            Server = Server,
            Username = "sqlpulse",
            Password = "irrelevant-for-tests",
            DefaultDatabase = "master",
        });

        return new PerformanceSnapshotService(
            new ActiveRequestsCollector(executor, options, NullLogger<ActiveRequestsCollector>.Instance),
            new LongRunningQueriesCollector(executor, options, NullLogger<LongRunningQueriesCollector>.Instance),
            new BlockingSessionsCollector(executor, options, NullLogger<BlockingSessionsCollector>.Instance),
            new TopCpuQueriesCollector(executor, options, NullLogger<TopCpuQueriesCollector>.Instance),
            new TopIoQueriesCollector(executor, options, NullLogger<TopIoQueriesCollector>.Instance),
            new WaitStatisticsCollector(executor, options, NullLogger<WaitStatisticsCollector>.Instance),
            new PassThroughWaitDeltaService(),
            options,
            NullLogger<PerformanceSnapshotService>.Instance);
    }

    [Fact]
    public async Task Server_scope_populates_every_section()
    {
        var executor = new FakeSqlQueryExecutor();

        var snapshot = await Build(executor).CollectAsync(new PerformanceSnapshotRequest());

        Assert.False(string.IsNullOrWhiteSpace(snapshot.SnapshotId));
        Assert.True(Guid.TryParse(snapshot.SnapshotId, out _));
        Assert.Equal(Server, snapshot.ServerHost);
        Assert.Equal(AnalysisScope.Server, snapshot.Scope);
        Assert.Null(snapshot.Database);

        Assert.NotNull(snapshot.ActiveRequests);
        Assert.NotNull(snapshot.LongRunningQueries);
        Assert.NotNull(snapshot.BlockingSessions);
        Assert.NotNull(snapshot.TopCpuQueries);
        Assert.NotNull(snapshot.TopIoQueries);
        Assert.NotNull(snapshot.WaitStatistics);

        Assert.True(snapshot.ActiveRequests!.Metadata.Success);
        Assert.True(snapshot.WaitStatistics!.Metadata.Success);
        Assert.Equal(WaitStatisticsApplicability.ServerLevel, snapshot.WaitStatistics.Applicability);
    }

    [Fact]
    public void A_default_request_carries_the_python_defaults()
    {
        // Guards against the defaults being bypassed by a parameterless construction,
        // which would silently collapse the row limits to zero.
        var request = new PerformanceSnapshotRequest();

        Assert.Equal(AnalysisScope.Server, request.Scope);
        Assert.Null(request.Database);
        Assert.Equal(5000, request.LongRunningThresholdMs);
        Assert.Equal(20, request.TopN);
        Assert.Equal(25, request.WaitStatsTopN);
    }

    [Fact]
    public async Task Server_scope_applies_the_requested_thresholds()
    {
        var executor = new FakeSqlQueryExecutor();

        var snapshot = await Build(executor).CollectAsync(new PerformanceSnapshotRequest(
            LongRunningThresholdMs: 15000,
            TopN: 5,
            WaitStatsTopN: 7));

        Assert.Equal(15000, snapshot.LongRunningQueries!.ThresholdMs);
        Assert.Equal(5, snapshot.TopCpuQueries!.TopN);
        Assert.Equal(5, snapshot.TopIoQueries!.TopN);
        Assert.Equal(7, snapshot.WaitStatistics!.TopN);
    }

    [Fact]
    public async Task Database_scope_targets_only_that_database()
    {
        var executor = new FakeSqlQueryExecutor();

        var snapshot = await Build(executor).CollectAsync(
            new PerformanceSnapshotRequest(AnalysisScope.Database, "OrdersDb"));

        Assert.Equal(AnalysisScope.Database, snapshot.Scope);
        Assert.Equal("OrdersDb", snapshot.Database);
        Assert.Equal("OrdersDb", snapshot.ActiveRequests!.Metadata.Database);
        Assert.Equal("OrdersDb", snapshot.TopCpuQueries!.Metadata.Database);

        // Every executed query was routed at the target database.
        Assert.All(executor.ExecutedDatabases, database => Assert.Equal("OrdersDb", database));
    }

    [Fact]
    public async Task Database_scope_reports_wait_statistics_as_not_applicable_not_failed()
    {
        var executor = new FakeSqlQueryExecutor();

        var snapshot = await Build(executor).CollectAsync(
            new PerformanceSnapshotRequest(AnalysisScope.Database, "OrdersDb"));

        var waits = snapshot.WaitStatistics!;

        // sys.dm_os_wait_stats is instance-wide: "does not apply here" must not read as
        // "could not be collected".
        Assert.True(waits.Metadata.Success);
        Assert.False(waits.Metadata.PermissionIssue);
        Assert.Equal(WaitStatisticsApplicability.NotApplicableDatabaseScope, waits.Applicability);
        Assert.Equal(WaitDeltaStatus.NotApplicable, waits.DeltaStatus);
        Assert.Equal(PerformanceSnapshotService.WaitStatisticsNotApplicableMessage, waits.Metadata.ErrorMessage);
        Assert.Empty(waits.Statistics);
    }

    [Fact]
    public async Task Database_scope_does_not_query_wait_statistics()
    {
        var executor = new FakeSqlQueryExecutor();

        await Build(executor).CollectAsync(new PerformanceSnapshotRequest(AnalysisScope.Database, "OrdersDb"));

        Assert.DoesNotContain(
            executor.ExecutedQueries,
            sql => sql.Contains("sys.dm_os_wait_stats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_failing_collector_does_not_fail_the_snapshot()
    {
        // Only the blocking query fails; every other section must still be produced.
        var executor = new FakeSqlQueryExecutor
        {
            RowsForQuery = sql => sql.Contains("blocking_info", StringComparison.Ordinal)
                ? throw new InvalidOperationException("VIEW SERVER STATE permission was denied.")
                : [],
        };

        var snapshot = await Build(executor).CollectAsync(new PerformanceSnapshotRequest());

        Assert.False(snapshot.BlockingSessions!.Metadata.Success);
        Assert.True(snapshot.BlockingSessions.Metadata.PermissionIssue);
        Assert.Equal(SqlErrorSanitizer.PermissionDeniedMessage, snapshot.BlockingSessions.Metadata.ErrorMessage);

        Assert.True(snapshot.ActiveRequests!.Metadata.Success);
        Assert.True(snapshot.LongRunningQueries!.Metadata.Success);
        Assert.True(snapshot.TopCpuQueries!.Metadata.Success);
        Assert.True(snapshot.TopIoQueries!.Metadata.Success);
        Assert.True(snapshot.WaitStatistics!.Metadata.Success);
    }

    [Fact]
    public async Task A_total_outage_still_returns_a_snapshot_with_every_section_marked_failed()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnExecute = new TimeoutException("Timeout expired connecting to MSI-STGERPDBL."),
        };

        var snapshot = await Build(executor).CollectAsync(new PerformanceSnapshotRequest());

        Assert.False(snapshot.ActiveRequests!.Metadata.Success);
        Assert.False(snapshot.LongRunningQueries!.Metadata.Success);
        Assert.False(snapshot.BlockingSessions!.Metadata.Success);
        Assert.False(snapshot.TopCpuQueries!.Metadata.Success);
        Assert.False(snapshot.TopIoQueries!.Metadata.Success);
        Assert.False(snapshot.WaitStatistics!.Metadata.Success);

        Assert.Equal(SqlErrorSanitizer.GenericFailureMessage, snapshot.ActiveRequests.Metadata.ErrorMessage);
    }
}
