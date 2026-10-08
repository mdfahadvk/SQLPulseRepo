using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;

namespace SQLPulseAPI.Net8.Collectors;

/// <summary>
/// Collects sessions participating in blocking chains.
/// Ported from <c>BlockingSessionsCollector</c>.
/// </summary>
/// <remarks>Required permission: <c>VIEW SERVER STATE</c>.</remarks>
public sealed class BlockingSessionsCollector(
    ISqlQueryExecutor executor,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<BlockingSessionsCollector> logger)
    : CollectorBase(executor, sqlServerOptions, logger)
{
    /// <inheritdoc />
    public override string Name => "blocking_sessions";

    /// <summary>Collects blocking sessions, never throwing.</summary>
    /// <param name="database">Target database, or <see langword="null"/> for instance-wide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<BlockingSessionsResult> CollectAsync(
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = database is null
                ? await ExecuteQueryAsync(PerformanceQueries.BlockingSessions, null, null, cancellationToken)
                : await ExecuteQueryAsync(
                    PerformanceQueries.BlockingSessionsByDatabase, database, [database], cancellationToken);

            var sessions = rows.Select(Map).ToList();

            return new BlockingSessionsResult
            {
                Metadata = BuildMetadata(database),
                Sessions = sessions,
                // Each head blocker is the root of exactly one chain.
                BlockingChainCount = sessions.Count(session => session.IsHeadBlocker),
            };
        }
        catch (Exception exception)
        {
            return new BlockingSessionsResult
            {
                Metadata = BuildFailureMetadata(database, exception),
            };
        }
    }

    private static BlockingSession Map(SqlRow row) => new()
    {
        SessionId = ReadInt32(row, "session_id"),
        BlockingSessionId = ReadNullableInt32(row, "blocking_session_id"),
        WaitType = ReadNullableString(row, "wait_type"),
        WaitTimeMs = ReadInt64(row, "wait_time_ms"),
        WaitResource = ReadNullableString(row, "wait_resource"),
        DatabaseName = ReadNullableString(row, "database_name"),
        SqlText = ReadNullableString(row, "sql_text"),
        Status = ReadString(row, "status"),
        LoginName = ReadNullableString(row, "login_name"),
        HostName = ReadNullableString(row, "host_name"),
        ProgramName = ReadNullableString(row, "program_name"),
        IsHeadBlocker = ReadBoolean(row, "is_head_blocker"),
    };
}
