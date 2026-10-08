using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;

namespace SQLPulseAPI.Net8.Collectors;

/// <summary>
/// Collects top wait types from <c>sys.dm_os_wait_stats</c>.
/// Ported from <c>WaitStatisticsCollector</c>.
/// </summary>
/// <remarks>
/// <para>Required permission: <c>VIEW SERVER STATE</c>.</para>
/// <para>
/// <c>sys.dm_os_wait_stats</c> is instance-wide, so this collector only makes sense at
/// server scope. Database-scoped snapshots report the section as not applicable rather
/// than as a failure — see <see cref="Services.Performance.PerformanceSnapshotService"/>.
/// </para>
/// </remarks>
public sealed class WaitStatisticsCollector(
    ISqlQueryExecutor executor,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<WaitStatisticsCollector> logger)
    : CollectorBase(executor, sqlServerOptions, logger)
{
    /// <summary>Default row limit. Python: <c>DEFAULT_TOP_N</c>.</summary>
    public const int DefaultTopN = 25;

    /// <inheritdoc />
    public override string Name => "wait_statistics";

    /// <inheritdoc />
    public override CollectorScope Scope => CollectorScope.Server;

    /// <summary>Collects wait statistics, never throwing.</summary>
    /// <param name="topN">Maximum number of wait types to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<WaitStatisticsResult> CollectAsync(
        int topN = DefaultTopN,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await ExecuteQueryAsync(
                PerformanceQueries.WithTopN(PerformanceQueries.WaitStatistics, topN),
                null,
                null,
                cancellationToken);

            var (totals, startTime) = await CollectSummaryAsync(cancellationToken);

            return new WaitStatisticsResult
            {
                Metadata = BuildMetadata(null),
                TopN = topN,
                Statistics = [.. rows.Select(Map)],
                Applicability = WaitStatisticsApplicability.ServerLevel,
                CollectedAt = DateTimeOffset.UtcNow,
                SqlServerStartTime = startTime,
                Totals = totals,
                // A delta needs a previous observation, which only the storage layer can
                // supply; it is filled in after collection.
                DeltaStatus = WaitDeltaStatus.InsufficientPreviousData,
            };
        }
        catch (Exception exception)
        {
            return new WaitStatisticsResult
            {
                Metadata = BuildFailureMetadata(null, exception),
                TopN = topN,
            };
        }
    }

    /// <summary>
    /// Fetches instance-wide totals and the counter epoch.
    /// </summary>
    /// <remarks>
    /// Failure here must not lose the per-wait-type detail already collected, so the error
    /// is logged and the totals are reported as absent.
    /// </remarks>
    private async Task<(WaitStatisticsTotals? Totals, DateTimeOffset? StartTime)> CollectSummaryAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SqlRow> rows;
        try
        {
            rows = await ExecuteQueryAsync(
                PerformanceQueries.WaitStatisticsSummary, null, null, cancellationToken);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Wait statistics summary unavailable");
            return (null, null);
        }

        if (rows.Count == 0)
        {
            return (null, null);
        }

        var row = rows[0];
        var totals = new WaitStatisticsTotals
        {
            WaitingTasksCount = ReadInt64(row, "waiting_tasks_count"),
            WaitTimeMs = ReadInt64(row, "wait_time_ms"),
            SignalWaitTimeMs = ReadInt64(row, "signal_wait_time_ms"),
            ResourceWaitTimeMs = ReadInt64(row, "resource_wait_time_ms"),
        };

        return (totals, ReadNullableTimestamp(row, "sqlserver_start_time"));
    }

    private static WaitStatistic Map(SqlRow row) => new()
    {
        WaitType = ReadString(row, "wait_type"),
        WaitingTasksCount = ReadInt64(row, "waiting_tasks_count"),
        WaitTimeMs = ReadInt64(row, "wait_time_ms"),
        MaxWaitTimeMs = ReadInt64(row, "max_wait_time_ms"),
        SignalWaitTimeMs = ReadInt64(row, "signal_wait_time_ms"),
        ResourceWaitTimeMs = ReadInt64(row, "resource_wait_time_ms"),
    };
}
