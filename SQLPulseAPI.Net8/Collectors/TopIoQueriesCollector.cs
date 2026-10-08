using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;

namespace SQLPulseAPI.Net8.Collectors;

/// <summary>
/// Collects the heaviest logical-read consumers from the plan cache.
/// Ported from <c>TopIoQueriesCollector</c>.
/// </summary>
/// <remarks>Required permission: <c>VIEW SERVER STATE</c>.</remarks>
public sealed class TopIoQueriesCollector(
    ISqlQueryExecutor executor,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<TopIoQueriesCollector> logger)
    : CollectorBase(executor, sqlServerOptions, logger)
{
    /// <summary>Default row limit. Python: <c>DEFAULT_TOP_N</c>.</summary>
    public const int DefaultTopN = 20;

    /// <inheritdoc />
    public override string Name => "top_io_queries";

    /// <summary>Collects top I/O queries, never throwing.</summary>
    /// <param name="topN">Maximum number of rows to return.</param>
    /// <param name="database">Target database, or <see langword="null"/> for instance-wide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<TopIoQueriesResult> CollectAsync(
        int topN = DefaultTopN,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = database is null
                ? await ExecuteQueryAsync(
                    PerformanceQueries.WithTopN(PerformanceQueries.TopIoQueries, topN),
                    null,
                    null,
                    cancellationToken)
                : await ExecuteQueryAsync(
                    PerformanceQueries.WithTopN(PerformanceQueries.TopIoQueriesByDatabase, topN),
                    database,
                    [database],
                    cancellationToken);

            return new TopIoQueriesResult
            {
                Metadata = BuildMetadata(database),
                TopN = topN,
                Queries = [.. rows.Select(Map)],
            };
        }
        catch (Exception exception)
        {
            return new TopIoQueriesResult
            {
                Metadata = BuildFailureMetadata(database, exception),
                TopN = topN,
            };
        }
    }

    private static TopIoQuery Map(SqlRow row) => new()
    {
        SqlText = ReadNullableString(row, "sql_text"),
        QueryHash = ReadNullableString(row, "query_hash"),
        QueryPlanHash = ReadNullableString(row, "query_plan_hash"),
        ObjectName = ReadNullableString(row, "object_name"),
        ExecutionCount = ReadInt64(row, "execution_count"),
        TotalLogicalReads = ReadInt64(row, "total_logical_reads"),
        AvgLogicalReads = ReadDouble(row, "avg_logical_reads"),
        TotalPhysicalReads = ReadInt64(row, "total_physical_reads"),
        AvgPhysicalReads = ReadDouble(row, "avg_physical_reads"),
        TotalLogicalWrites = ReadInt64(row, "total_logical_writes"),
        AvgLogicalWrites = ReadDouble(row, "avg_logical_writes"),
        TotalElapsedTimeMs = ReadInt64(row, "total_elapsed_time_ms"),
        DatabaseName = ReadNullableString(row, "database_name"),
    };
}
