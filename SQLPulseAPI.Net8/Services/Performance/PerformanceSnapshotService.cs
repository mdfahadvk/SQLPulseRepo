using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Collectors;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.History;

namespace SQLPulseAPI.Net8.Services.Performance;

/// <summary>
/// Runs the collectors and assembles a <see cref="PerformanceSnapshot"/>.
/// </summary>
/// <remarks>
/// Collectors run sequentially, as in the Python implementation, so a snapshot never
/// opens several concurrent connections against a server that is already under
/// investigation. Each collector absorbs its own failures, so a permission-denied or
/// unavailable section degrades that section alone.
/// </remarks>
public sealed class PerformanceSnapshotService(
    ActiveRequestsCollector activeRequests,
    LongRunningQueriesCollector longRunningQueries,
    BlockingSessionsCollector blockingSessions,
    TopCpuQueriesCollector topCpuQueries,
    TopIoQueriesCollector topIoQueries,
    WaitStatisticsCollector waitStatistics,
    IWaitDeltaService waitDeltaService,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<PerformanceSnapshotService> logger) : IPerformanceSnapshotService
{
    /// <summary>
    /// Reported instead of a failure when wait statistics are requested at database scope.
    /// </summary>
    public const string WaitStatisticsNotApplicableMessage =
        "Wait statistics are server-level only (sys.dm_os_wait_stats). Not applicable for database scope.";

    private readonly SqlServerOptions _sqlServer = sqlServerOptions.Value;

    /// <inheritdoc />
    public async Task<PerformanceSnapshot> CollectAsync(
        PerformanceSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var snapshotId = Guid.NewGuid().ToString();

        // Only a database-scoped snapshot targets a specific database; at server scope the
        // collectors deliberately query the instance as a whole.
        var databaseTarget = request.Scope == AnalysisScope.Database ? request.Database : null;

        logger.LogInformation(
            "Starting {Scope} snapshot {SnapshotId} (db={Database})",
            request.Scope,
            snapshotId,
            databaseTarget ?? "all");

        var active = await activeRequests.CollectAsync(databaseTarget, cancellationToken);
        var longRunning = await longRunningQueries.CollectAsync(
            request.LongRunningThresholdMs, databaseTarget, cancellationToken);
        var blocking = await blockingSessions.CollectAsync(databaseTarget, cancellationToken);
        var topCpu = await topCpuQueries.CollectAsync(request.TopN, databaseTarget, cancellationToken);
        var topIo = await topIoQueries.CollectAsync(request.TopN, databaseTarget, cancellationToken);

        WaitStatisticsResult waits;
        if (request.Scope == AnalysisScope.Server)
        {
            waits = await waitStatistics.CollectAsync(request.WaitStatsTopN, cancellationToken);

            // sys.dm_os_wait_stats is cumulative since the last restart, so a single
            // reading describes the server's whole uptime. Only the change since the
            // previous reading says anything about the recent interval.
            waits = await waitDeltaService.ApplyAsync(waits, _sqlServer.Server, snapshotId, cancellationToken);
        }
        else
        {
            waits = BuildNotApplicableWaitStatistics(request);
        }

        logger.LogInformation("Performance snapshot {SnapshotId} complete", snapshotId);

        return new PerformanceSnapshot
        {
            SnapshotId = snapshotId,
            CollectedAt = DateTimeOffset.UtcNow,
            ServerHost = _sqlServer.Server,
            Scope = request.Scope,
            Database = request.Database,
            ActiveRequests = active,
            LongRunningQueries = longRunning,
            BlockingSessions = blocking,
            TopCpuQueries = topCpu,
            TopIoQueries = topIo,
            WaitStatistics = waits,
        };
    }

    /// <summary>
    /// Builds the database-scope wait statistics placeholder.
    /// </summary>
    /// <remarks>
    /// Reported as a successful, explicitly not-applicable section rather than as a null or
    /// a failure, so the UI can distinguish "does not apply here" from "could not be read".
    /// </remarks>
    private WaitStatisticsResult BuildNotApplicableWaitStatistics(PerformanceSnapshotRequest request) => new()
    {
        Metadata = new CollectorMetadata
        {
            CollectorName = "wait_statistics",
            ServerHost = _sqlServer.Server,
            Database = string.IsNullOrWhiteSpace(request.Database)
                ? _sqlServer.DefaultDatabase
                : request.Database,
            CollectedAt = DateTimeOffset.UtcNow,
            Success = true,
            ErrorMessage = WaitStatisticsNotApplicableMessage,
        },
        TopN = request.WaitStatsTopN,
        Applicability = WaitStatisticsApplicability.NotApplicableDatabaseScope,
        DeltaStatus = WaitDeltaStatus.NotApplicable,
    };
}
