using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Collectors;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.QueryStore;

/// <summary>
/// The outcome of a Query Store read: availability, the rows, and why not when
/// unavailable.
/// </summary>
/// <param name="Status">Query Store availability for the database.</param>
/// <param name="Records">Matching slow-query rows. Empty unless <paramref name="Status"/> is accessible.</param>
/// <param name="Message">Human-readable detail for a non-available status.</param>
public sealed record QueryStoreReadResult(
    QueryStoreStatus Status,
    IReadOnlyList<SlowQueryRecord> Records,
    string? Message);

/// <summary>Reads slow-query history from SQL Server Query Store.</summary>
public interface IQueryStoreCollector
{
    /// <summary>Configured server host, surfaced on the slow-history response.</summary>
    string ServerHost { get; }

    /// <summary>Reports whether Query Store can be read for a database. Never throws.</summary>
    Task<(QueryStoreStatus Status, string? Message)> CheckStatusAsync(
        string database,
        CancellationToken cancellationToken = default);

    /// <summary>Reads slow queries inside a time window. Never throws.</summary>
    Task<QueryStoreReadResult> CollectSlowHistoryAsync(
        string database,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        double thresholdMs,
        int topN = 20,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Query Store collector.
/// Ported from <c>app/collectors/query_store.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Strictly read-only. SQLPulse reports Query Store's state and never enables, disables
/// or reconfigures it — altering a monitored server's configuration is a write, and
/// SQLPulse does not write to the servers it observes.
/// </para>
/// <para>
/// Every failure is converted into a status, so an unavailable, disabled or
/// permission-denied Query Store degrades this one feature rather than failing the
/// request.
/// </para>
/// </remarks>
public sealed class QueryStoreCollector(
    ISqlQueryExecutor executor,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<QueryStoreCollector> logger)
    : CollectorBase(executor, sqlServerOptions, logger), IQueryStoreCollector
{
    /// <inheritdoc />
    public override string Name => "query_store";

    /// <inheritdoc />
    public override CollectorScope Scope => CollectorScope.Database;

    /// <inheritdoc />
    public string ServerHost => ConfiguredServerHost;

    /// <inheritdoc />
    public async Task<(QueryStoreStatus Status, string? Message)> CheckStatusAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        // The database name is interpolated into a USE statement downstream, so an
        // unvalidated name would be an injection point.
        if (!DatabaseNames.IsValid(database))
        {
            return (QueryStoreStatus.QueryFailed, "Invalid database name");
        }

        try
        {
            var rows = await ExecuteQueryAsync(
                PerformanceQueries.QueryStoreStatus, database, null, cancellationToken);

            if (rows.Count == 0)
            {
                // sys.database_query_store_options always returns a row where Query Store
                // is supported, so no rows means it is unavailable here.
                Logger.LogWarning(
                    "Query Store status query returned no rows for database {Database}", database);

                return (
                    QueryStoreStatus.Disabled,
                    $"Query Store options not found for database '{database}'.");
            }

            var state = ReadString(rows[0], "actual_state_desc").Trim().ToUpperInvariant();

            Logger.LogInformation(
                "Query Store status for {Database}: actual_state_desc={State}", database, state);

            return state switch
            {
                "READ_WRITE" => (QueryStoreStatus.Available, (string?)null),

                // A read-only Query Store still answers historical questions; it just
                // cannot record anything new, which is enough for slow-query history.
                "READ_ONLY" => (QueryStoreStatus.AvailableReadOnly, (string?)null),

                "OFF" => (
                    QueryStoreStatus.Disabled,
                    $"Query Store is disabled (OFF) for database '{database}'."),

                // ERROR and TRANSITIONING are reported as-is rather than worked around,
                // because recovering them is an administrative write.
                _ => (
                    QueryStoreStatus.Disabled,
                    $"Query Store is in state '{state}' for database '{database}'."),
            };
        }
        catch (Exception exception) when (IsPermissionError(exception))
        {
            // Logged with the original exception; the caller only ever sees the fixed
            // message, so server error text never reaches the response.
            Logger.LogWarning(
                exception, "Query Store permission denied for database {Database}", database);

            return (
                QueryStoreStatus.PermissionDenied,
                $"Permission denied when checking Query Store for database '{database}'.");
        }
        catch (Exception exception)
        {
            Logger.LogError(
                exception,
                "Query Store availability check failed for database {Database}", database);

            return (
                QueryStoreStatus.QueryFailed,
                $"Query Store availability check failed for database '{database}'.");
        }
    }

    /// <inheritdoc />
    public async Task<QueryStoreReadResult> CollectSlowHistoryAsync(
        string database,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        double thresholdMs,
        int topN = 20,
        CancellationToken cancellationToken = default)
    {
        var (status, statusMessage) = await CheckStatusAsync(database, cancellationToken);
        if (!status.IsAccessible())
        {
            Logger.LogInformation(
                "Query Store not accessible for {Database} (status={Status})", database, status);

            return new QueryStoreReadResult(status, [], statusMessage);
        }

        try
        {
            var query = PerformanceQueries.WithTopN(PerformanceQueries.QueryStoreSlowHistory, topN);

            var rows = await ExecuteQueryAsync(
                query,
                database,
                [startTime, endTime, thresholdMs],
                cancellationToken);

            var records = rows.Select(Map).ToList();

            Logger.LogInformation(
                "Query Store collection for {Database}: status={Status}, rows={Rows}, threshold_ms={Threshold}",
                database,
                status,
                records.Count,
                thresholdMs);

            return new QueryStoreReadResult(status, records, null);
        }
        catch (Exception exception) when (IsPermissionError(exception))
        {
            Logger.LogWarning(
                exception, "Query Store data access denied for database {Database}", database);

            return new QueryStoreReadResult(
                QueryStoreStatus.PermissionDenied,
                [],
                $"Permission denied when reading Query Store data for database '{database}'.");
        }
        catch (Exception exception)
        {
            Logger.LogError(
                exception,
                "Query Store slow-history query failed for database {Database}", database);

            return new QueryStoreReadResult(
                QueryStoreStatus.QueryFailed,
                [],
                $"Query Store data collection failed for database '{database}'.");
        }
    }

    /// <summary>Maps one Query Store row onto a <see cref="SlowQueryRecord"/>.</summary>
    /// <remarks>
    /// Query Store exposes no session metadata, so <c>program_name</c>, <c>host_name</c>
    /// and <c>is_scheduled_job</c> stay null. That absence is itself evidence, and it is
    /// what keeps workload classification honest about what it cannot know.
    /// </remarks>
    private static SlowQueryRecord Map(SqlRow row) => new()
    {
        QueryId = ReadNullableInt64(row, "query_id"),
        QuerySqlText = ReadNullableString(row, "query_sql_text"),
        QueryHash = ReadNullableString(row, "query_hash"),
        ObjectId = ReadNullableInt64(row, "object_id"),
        ProcedureName = ReadNullableString(row, "procedure_name"),
        PlanId = ReadNullableInt64(row, "plan_id"),
        ExecutionCount = ReadInt64(row, "execution_count"),
        AvgDurationMs = ReadDouble(row, "avg_duration_ms"),
        MaxDurationMs = ReadDouble(row, "max_duration_ms"),
        MinDurationMs = ReadDouble(row, "min_duration_ms"),
        AvgCpuTimeMs = ReadDouble(row, "avg_cpu_time_ms"),
        MaxCpuTimeMs = ReadDouble(row, "max_cpu_time_ms"),
        AvgLogicalReads = ReadDouble(row, "avg_logical_reads"),
        MaxLogicalReads = ReadDouble(row, "max_logical_reads"),
        AvgPhysicalReads = ReadDouble(row, "avg_physical_reads"),
        FirstExecutionTime = ReadNullableTimestamp(row, "first_execution_time"),
        LastExecutionTime = ReadNullableTimestamp(row, "last_execution_time"),
        IntervalStartTime = ReadNullableTimestamp(row, "interval_start_time"),
    };

    private static long? ReadNullableInt64(SqlRow row, string column) =>
        row[column] is { } value
            ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
}
