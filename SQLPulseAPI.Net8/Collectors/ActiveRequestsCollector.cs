using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;

namespace SQLPulseAPI.Net8.Collectors;

/// <summary>
/// Collects currently executing requests from <c>sys.dm_exec_requests</c>.
/// Ported from <c>ActiveRequestsCollector</c>.
/// </summary>
/// <remarks>Required permission: <c>VIEW SERVER STATE</c>.</remarks>
public sealed class ActiveRequestsCollector(
    ISqlQueryExecutor executor,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<ActiveRequestsCollector> logger)
    : CollectorBase(executor, sqlServerOptions, logger)
{
    /// <inheritdoc />
    public override string Name => "active_requests";

    /// <summary>Collects active requests, never throwing.</summary>
    /// <param name="database">Target database, or <see langword="null"/> for instance-wide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ActiveRequestsResult> CollectAsync(
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = database is null
                ? await ExecuteQueryAsync(PerformanceQueries.ActiveRequests, null, null, cancellationToken)
                : await ExecuteQueryAsync(
                    PerformanceQueries.ActiveRequestsByDatabase, database, [database], cancellationToken);

            return new ActiveRequestsResult
            {
                Metadata = BuildMetadata(database),
                Requests = [.. rows.Select(Map)],
            };
        }
        catch (Exception exception)
        {
            return new ActiveRequestsResult
            {
                Metadata = BuildFailureMetadata(database, exception),
            };
        }
    }

    private static ActiveRequest Map(SqlRow row) => new()
    {
        SessionId = ReadInt32(row, "session_id"),
        RequestId = ReadInt32(row, "request_id"),
        Status = ReadString(row, "status"),
        Command = ReadString(row, "command"),
        SqlText = ReadNullableString(row, "sql_text"),
        DatabaseName = ReadNullableString(row, "database_name"),
        WaitType = ReadNullableString(row, "wait_type"),
        WaitTimeMs = ReadInt64(row, "wait_time_ms"),
        CpuTimeMs = ReadInt64(row, "cpu_time_ms"),
        TotalElapsedTimeMs = ReadInt64(row, "total_elapsed_time_ms"),
        Reads = ReadInt64(row, "reads"),
        Writes = ReadInt64(row, "writes"),
        LogicalReads = ReadInt64(row, "logical_reads"),
        BlockingSessionId = ReadNullableInt32(row, "blocking_session_id"),
        OpenTransactionCount = ReadInt32(row, "open_transaction_count"),
        LoginName = ReadNullableString(row, "login_name"),
        HostName = ReadNullableString(row, "host_name"),
        ProgramName = ReadNullableString(row, "program_name"),
    };
}
