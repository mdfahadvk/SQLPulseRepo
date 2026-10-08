using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;

namespace SQLPulseAPI.Net8.Collectors;

/// <summary>
/// Collects requests whose elapsed time exceeds a threshold.
/// Ported from <c>LongRunningQueriesCollector</c>.
/// </summary>
/// <remarks>Required permission: <c>VIEW SERVER STATE</c>.</remarks>
public sealed class LongRunningQueriesCollector(
    ISqlQueryExecutor executor,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<LongRunningQueriesCollector> logger)
    : CollectorBase(executor, sqlServerOptions, logger)
{
    /// <summary>Default threshold: 5 seconds. Python: <c>DEFAULT_THRESHOLD_MS</c>.</summary>
    public const int DefaultThresholdMs = 5000;

    /// <inheritdoc />
    public override string Name => "long_running_queries";

    /// <summary>Collects long-running queries, never throwing.</summary>
    /// <param name="thresholdMs">Minimum elapsed time, in milliseconds.</param>
    /// <param name="database">Target database, or <see langword="null"/> for instance-wide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<LongRunningQueriesResult> CollectAsync(
        int thresholdMs = DefaultThresholdMs,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = database is null
                ? await ExecuteQueryAsync(
                    PerformanceQueries.LongRunningQueries, null, [thresholdMs], cancellationToken)
                : await ExecuteQueryAsync(
                    PerformanceQueries.LongRunningQueriesByDatabase,
                    database,
                    [thresholdMs, database],
                    cancellationToken);

            return new LongRunningQueriesResult
            {
                Metadata = BuildMetadata(database),
                ThresholdMs = thresholdMs,
                Queries = [.. rows.Select(Map)],
            };
        }
        catch (Exception exception)
        {
            return new LongRunningQueriesResult
            {
                Metadata = BuildFailureMetadata(database, exception),
                ThresholdMs = thresholdMs,
            };
        }
    }

    private static LongRunningQuery Map(SqlRow row) => new()
    {
        SessionId = ReadInt32(row, "session_id"),
        RequestId = ReadInt32(row, "request_id"),
        SqlText = ReadNullableString(row, "sql_text"),
        DatabaseName = ReadNullableString(row, "database_name"),
        ElapsedTimeMs = ReadInt64(row, "elapsed_time_ms"),
        CpuTimeMs = ReadInt64(row, "cpu_time_ms"),
        Reads = ReadInt64(row, "reads"),
        LogicalReads = ReadInt64(row, "logical_reads"),
        Writes = ReadInt64(row, "writes"),
        WaitType = ReadNullableString(row, "wait_type"),
        WaitTimeMs = ReadInt64(row, "wait_time_ms"),
        Status = ReadString(row, "status"),
        LoginName = ReadNullableString(row, "login_name"),
        HostName = ReadNullableString(row, "host_name"),
        ProgramName = ReadNullableString(row, "program_name"),
    };
}
