using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.QueryStore;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>
/// Derives unconfirmed timeout candidates from SQLPulse's own Query Store evidence.
/// </summary>
/// <remarks>
/// <para>
/// This is not an error feed. It exists because most deployments have no application
/// telemetry connected yet, and SQL Server still knows which statements ran long enough
/// that a client using the configured command timeout would have given up. Every event it
/// produces is reported as <see cref="SqlErrorSourceKind.DerivedPerformanceEvidence"/> and
/// classified as a candidate, never as a confirmed timeout.
/// </para>
/// <para>
/// It refuses to answer rather than guess. Without an explicitly configured application
/// timeout there is no line at which "slow" becomes "long enough to time out", and the
/// monitoring threshold is not that line: it is chosen by whoever set up monitoring and
/// says nothing about when a client stops waiting.
/// </para>
/// <para>
/// Read-only throughout. It reuses the same Query Store service as the slow-history
/// endpoint and issues no SQL of its own.
/// </para>
/// </remarks>
public sealed class QueryStoreTimeoutCandidateSource(
    ISlowHistoryService slowHistoryService,
    IOptions<AnalysisOptions> analysisOptions,
    IOptions<SqlErrorOptions> sqlErrorOptions) : ISqlErrorSource
{
    /// <summary>Identifier reported on every derived event.</summary>
    public const string SourceName = "sqlpulse_query_store";

    /// <summary>Returned when derived candidates have been switched off.</summary>
    public const string DisabledMessage =
        "Derivation of timeout candidates from Query Store is disabled.";

    /// <summary>Returned when the caller did not name a database.</summary>
    public const string DatabaseRequiredMessage =
        "Query Store evidence is per database, so a database filter is required before "
        + "SQLPulse can derive timeout candidates.";

    /// <summary>Returned when no application timeout has been configured.</summary>
    public const string ApplicationTimeoutRequiredMessage =
        "No application timeout is configured, so SQLPulse cannot tell which slow queries "
        + "were long enough to time out. Set Analysis:ApplicationTimeoutMs to enable this source.";

    /// <summary>Upper bound on candidates requested per query, matching the slow-history endpoint.</summary>
    private const int MaxCandidates = 100;

    private readonly AnalysisOptions _analysis = analysisOptions.Value;
    private readonly SqlErrorOptions _sqlErrors = sqlErrorOptions.Value;

    /// <inheritdoc />
    public string Name => SourceName;

    /// <inheritdoc />
    public SqlErrorSourceKind Kind => SqlErrorSourceKind.DerivedPerformanceEvidence;

    /// <inheritdoc />
    public async Task<SqlErrorSourceResult> QueryAsync(
        SqlErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_sqlErrors.EnableQueryStoreTimeoutCandidates)
        {
            return SqlErrorSourceResult.NotConfigured(DisabledMessage);
        }

        if (string.IsNullOrWhiteSpace(query.Database))
        {
            return SqlErrorSourceResult.NotApplicable(DatabaseRequiredMessage);
        }

        if (_analysis.ApplicationTimeoutMs is not { } applicationTimeoutMs)
        {
            return SqlErrorSourceResult.NotApplicable(ApplicationTimeoutRequiredMessage);
        }

        var response = await slowHistoryService.GetSlowHistoryAsync(
            query.Database,
            query.StartTime,
            query.EndTime,
            _analysis.SlowQueryThresholdMs,
            Math.Clamp(query.TopN, 1, MaxCandidates),
            groupResults: true,
            workloadType: null,
            applicationTimeoutMs: applicationTimeoutMs,
            cancellationToken);

        if (!response.QueryStoreStatus.IsAccessible())
        {
            var status = EnumMemberJsonConverter<QueryStoreStatus>.ToWireValue(response.QueryStoreStatus);
            return SqlErrorSourceResult.Failed(
                $"Query Store is not readable for '{query.Database}' ({status}), so timeout "
                + "candidates could not be derived.");
        }

        // Only candidates that reached the application timeout are surfaced. A query that
        // merely exceeded the monitoring threshold is slow, and listing it here would put
        // a performance observation into an error feed.
        var events = response.Candidates
            .Where(candidate => candidate.IsTimeoutCandidate)
            .Select(candidate => ToRawEvent(candidate, query, applicationTimeoutMs))
            .ToList();

        return SqlErrorSourceResult.Available(events);
    }

    private RawSqlErrorEvent ToRawEvent(
        TimeoutCandidate candidate,
        SqlErrorQuery query,
        int applicationTimeoutMs)
    {
        var subject = candidate.ProcedureName is { Length: > 0 } procedure
            ? $"stored procedure {procedure}"
            : "an ad-hoc statement";

        return new RawSqlErrorEvent
        {
            // Query Store aggregates over intervals, so the last execution in the window is
            // the closest thing to an incident time that the evidence actually supports.
            Timestamp = candidate.LastExecutionTime ?? query.EndTime,
            Server = query.Server,
            Database = query.Database,
            StoredProcedure = candidate.ProcedureName,
            QueryHash = candidate.QueryHash,
            DurationMs = candidate.MaxDurationMs,
            CommandTimeoutSeconds = applicationTimeoutMs / 1000.0,
            Message =
                $"Query Store recorded a maximum duration of {candidate.MaxDurationMs:F0} ms for "
                + $"{subject}, at or above the configured application timeout of {applicationTimeoutMs} ms. "
                + "No application error was observed, so this is an unconfirmed timeout candidate.",

            // Never set: this source watches SQL Server, not the client, and therefore
            // cannot know whether anything actually gave up waiting.
            ApplicationTimeoutReported = false,
        };
    }
}
