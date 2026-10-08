using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;

namespace SQLPulseAPI.Net8.Collectors;

/// <summary>
/// Collects the heaviest CPU consumers from the plan cache.
/// Ported from <c>TopCpuQueriesCollector</c>.
/// </summary>
/// <remarks>Required permission: <c>VIEW SERVER STATE</c>.</remarks>
public sealed class TopCpuQueriesCollector(
    ISqlQueryExecutor executor,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<TopCpuQueriesCollector> logger)
    : CollectorBase(executor, sqlServerOptions, logger)
{
    /// <summary>Default row limit. Python: <c>DEFAULT_TOP_N</c>.</summary>
    public const int DefaultTopN = 20;

    /// <inheritdoc />
    public override string Name => "top_cpu_queries";

    /// <summary>Collects top CPU queries, never throwing.</summary>
    /// <param name="topN">Maximum number of rows to return.</param>
    /// <param name="database">Target database, or <see langword="null"/> for instance-wide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<TopCpuQueriesResult> CollectAsync(
        int topN = DefaultTopN,
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = database is null
                ? await ExecuteQueryAsync(
                    PerformanceQueries.WithTopN(PerformanceQueries.TopCpuQueries, topN),
                    null,
                    null,
                    cancellationToken)
                : await ExecuteQueryAsync(
                    PerformanceQueries.WithTopN(PerformanceQueries.TopCpuQueriesByDatabase, topN),
                    database,
                    [database],
                    cancellationToken);

            return new TopCpuQueriesResult
            {
                Metadata = BuildMetadata(database),
                TopN = topN,
                Queries = [.. rows.Select(Map)],
            };
        }
        catch (Exception exception)
        {
            return new TopCpuQueriesResult
            {
                Metadata = BuildFailureMetadata(database, exception),
                TopN = topN,
            };
        }
    }

    private static TopCpuQuery Map(SqlRow row) => new()
    {
        SqlText = ReadNullableString(row, "sql_text"),
        QueryHash = ReadNullableString(row, "query_hash"),
        QueryPlanHash = ReadNullableString(row, "query_plan_hash"),
        ObjectName = ReadNullableString(row, "object_name"),
        ExecutionCount = ReadInt64(row, "execution_count"),
        TotalCpuTimeMs = ReadInt64(row, "total_cpu_time_ms"),
        AvgCpuTimeMs = ReadDouble(row, "avg_cpu_time_ms"),
        TotalElapsedTimeMs = ReadInt64(row, "total_elapsed_time_ms"),
        AvgElapsedTimeMs = ReadDouble(row, "avg_elapsed_time_ms"),
        TotalLogicalReads = ReadInt64(row, "total_logical_reads"),
        AvgLogicalReads = ReadDouble(row, "avg_logical_reads"),
        DatabaseName = ReadNullableString(row, "database_name"),
    };
}
