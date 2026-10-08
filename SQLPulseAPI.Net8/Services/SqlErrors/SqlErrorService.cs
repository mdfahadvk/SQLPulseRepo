using System.Globalization;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>Reads, filters and summarizes SQL errors from every configured source.</summary>
public interface ISqlErrorService
{
    /// <summary>Returns the matching error events, newest first.</summary>
    Task<SqlErrorListResponse> ListAsync(SqlErrorQuery query, CancellationToken cancellationToken = default);

    /// <summary>Returns aggregate counts for the same filters, computed before <c>top_n</c>.</summary>
    Task<SqlErrorSummaryResponse> SummarizeAsync(SqlErrorQuery query, CancellationToken cancellationToken = default);

    /// <summary>Resolves a previously returned event by id, or null when it is no longer held.</summary>
    SqlErrorEvent? Find(string id);
}

/// <summary>
/// Fans a query out across the registered <see cref="ISqlErrorSource"/> implementations and
/// normalizes everything they return.
/// </summary>
/// <remarks>
/// <para>
/// The hard requirement here is that silence is never mistaken for health. A source that
/// is missing, misconfigured or broken produces
/// <see cref="SqlErrorQueryStatus.ErrorSourceUnavailable"/> or
/// <see cref="SqlErrorQueryStatus.PartialErrorData"/>, never an empty list presented as a
/// clean period. Telling an operator there were no errors when SQLPulse simply could not
/// look would be worse than telling them nothing.
/// </para>
/// <para>
/// One failing source does not lose the others: each is isolated, and its failure is
/// reported as a source state rather than as an exception.
/// </para>
/// </remarks>
public sealed class SqlErrorService(
    IEnumerable<ISqlErrorSource> sources,
    ISqlErrorEventCache cache,
    IOptions<SqlServerOptions> sqlServerOptions,
    IOptions<AnalysisOptions> analysisOptions,
    ILogger<SqlErrorService> logger) : ISqlErrorService
{
    /// <summary>Reported when every source answered and none saw an error.</summary>
    public const string NoErrorsMessage = "No SQL errors were found for this period.";

    /// <summary>Reported when no source could be consulted at all.</summary>
    public const string SourceUnavailableMessage =
        "No SQL error source is currently available, so SQLPulse cannot determine whether "
        + "SQL errors occurred during this period.";

    /// <summary>Reported when some sources answered and others could not.</summary>
    public const string PartialDataMessage =
        "At least one SQL error source could not be consulted, so this result is incomplete "
        + "and an absence of errors cannot be relied on.";

    /// <summary>
    /// Appended whenever no application telemetry source answered.
    /// </summary>
    /// <remarks>
    /// Kept separate from the status message because SQLPulse can hold complete SQL Server
    /// evidence and still be blind to what the application experienced.
    /// </remarks>
    public const string NoApplicationTelemetryMessage =
        "Application error telemetry is not currently connected, so SQLPulse cannot determine "
        + "whether application SQL errors occurred.";

    private readonly IReadOnlyList<ISqlErrorSource> _sources = sources.ToList();
    private readonly SqlServerOptions _sqlServer = sqlServerOptions.Value;
    private readonly AnalysisOptions _analysis = analysisOptions.Value;

    /// <inheritdoc />
    public async Task<SqlErrorListResponse> ListAsync(
        SqlErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        var collected = await CollectAsync(query, cancellationToken);

        var page = collected.Events.Take(Math.Max(query.TopN, 0)).ToList();

        var response = new SqlErrorListResponse
        {
            TotalMatched = collected.Events.Count,
            Returned = page.Count,
            TopN = query.TopN,
            Errors = page,
        };

        Describe(response, query, collected);
        return response;
    }

    /// <inheritdoc />
    public async Task<SqlErrorSummaryResponse> SummarizeAsync(
        SqlErrorQuery query,
        CancellationToken cancellationToken = default)
    {
        var collected = await CollectAsync(query, cancellationToken);
        var events = collected.Events;

        var response = new SqlErrorSummaryResponse
        {
            TotalErrors = events.Count,
            TimeoutCount = events.Count(e => e.ErrorCategory == SqlErrorCategory.Timeout),
            DeadlockCount = events.Count(e => e.ErrorCategory == SqlErrorCategory.Deadlock),
            ConnectionErrorCount = events.Count(e => e.ErrorCategory == SqlErrorCategory.Connection),
            PermissionErrorCount = events.Count(e =>
                e.ErrorCategory is SqlErrorCategory.PermissionDenied or SqlErrorCategory.LoginFailure),
            ConfirmedTimeoutCount = events.Count(e =>
                e.TimeoutClassification == TimeoutClassification.ConfirmedApplicationTimeout),
            TimeoutCandidateCount = events.Count(e =>
                e.TimeoutClassification == TimeoutClassification.TimeoutCandidate),
            AffectedDatabases = Distinct(events.Select(e => e.Database)),
            AffectedApplications = Distinct(events.Select(e => e.Application)),
            CategoryCounts = events
                .GroupBy(e => EnumMemberJsonConverter<SqlErrorCategory>.ToWireValue(e.ErrorCategory))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
        };

        // Derived rather than counted so the five headline buckets and the remainder always
        // add up to the total, whatever categories are added later.
        response.OtherErrorCount = response.TotalErrors
            - response.TimeoutCount
            - response.DeadlockCount
            - response.ConnectionErrorCount
            - response.PermissionErrorCount;

        Describe(response, query, collected);
        return response;
    }

    /// <inheritdoc />
    public SqlErrorEvent? Find(string id) => cache.Get(id);

    /// <summary>Queries every source, normalizes, filters and orders the result.</summary>
    private async Task<CollectedErrors> CollectAsync(SqlErrorQuery query, CancellationToken cancellationToken)
    {
        var states = new List<SqlErrorSourceState>(_sources.Count);
        var events = new List<SqlErrorEvent>();
        ApplicationTelemetrySummary? applicationTelemetry = null;

        foreach (var source in _sources)
        {
            var state = new SqlErrorSourceState { Name = source.Name, Kind = source.Kind };
            states.Add(state);

            SqlErrorSourceResult result;
            try
            {
                result = await source.QueryAsync(query, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "SQL error source {Source} failed", source.Name);

                // Sanitized: a source failure message can carry a connection string.
                result = SqlErrorSourceResult.Failed(
                    $"Source failed: {LlmSanitizer.SanitizeText(exception.Message)}");
            }

            state.Status = result.Status;
            state.Message = result.Message;
            if (result.ApplicationTelemetry is not null)
            {
                // The application source is the only source that supplies this additive
                // registry view; it is copied into the response in Describe below.
                applicationTelemetry = result.ApplicationTelemetry;
            }

            if (result.Status is not (SqlErrorSourceStatus.Available or SqlErrorSourceStatus.Partial
                or SqlErrorSourceStatus.PermissionDenied or SqlErrorSourceStatus.Failed))
            {
                continue;
            }

            var context = new SqlErrorClassificationContext(
                _sqlServer.Server,
                source.Name,
                source.Kind,
                _analysis.ApplicationTimeoutMs,
                _analysis.SlowQueryThresholdMs);

            var normalized = result.Events
                .Select(raw => SqlErrorClassifier.Normalize(raw, context))
                .Where(errorEvent => Matches(errorEvent, query))
                .ToList();

            state.EventCount = normalized.Count;
            events.AddRange(normalized);
        }

        var ordered = Deduplicate(events)
            .OrderByDescending(e => e.Timestamp)
            .ThenBy(e => e.Severity)
            .ToList();

        // Cached before paging so an event the summary counted can still be opened.
        cache.Store(ordered);

        return new CollectedErrors(ordered, states, applicationTelemetry);
    }

    /// <summary>
    /// Removes only strongly identified duplicates reported by multiple telemetry systems.
    /// Close timestamps alone are never enough to merge two events.
    /// </summary>
    private static IEnumerable<SqlErrorEvent> Deduplicate(IEnumerable<SqlErrorEvent> source)
    {
        var selected = new Dictionary<string, SqlErrorEvent>(StringComparer.Ordinal);
        foreach (var error in source)
        {
            var key = DuplicateKey(error);
            if (key is null)
            {
                yield return error;
                continue;
            }

            if (!selected.TryGetValue(key, out var existing)
                || EvidencePriority(error) > EvidencePriority(existing))
            {
                selected[key] = error;
            }
        }

        foreach (var error in selected.Values)
        {
            yield return error;
        }
    }

    private static string? DuplicateKey(SqlErrorEvent error)
    {
        if (!string.IsNullOrWhiteSpace(error.TraceId))
        {
            return $"trace:{error.TraceId}";
        }

        if (!string.IsNullOrWhiteSpace(error.CorrelationId))
        {
            return $"correlation:{error.CorrelationId}";
        }

        if (string.IsNullOrWhiteSpace(error.QueryHash)
            && string.IsNullOrWhiteSpace(error.StoredProcedure))
        {
            return null;
        }

        return string.Join('|',
            error.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            error.Database,
            error.StoredProcedure,
            error.QueryHash,
            error.SqlErrorNumber,
            error.Message);
    }

    private static int EvidencePriority(SqlErrorEvent error) => error.SourceKind switch
    {
        SqlErrorSourceKind.ApplicationTelemetry => 3,
        SqlErrorSourceKind.SqlServer => 2,
        _ => 1,
    };

    /// <summary>
    /// Applies the caller's filters after normalization.
    /// </summary>
    /// <remarks>
    /// Re-applied here even though sources receive the same query, because a source may
    /// filter loosely and because category and severity only exist once the classifier has
    /// run.
    /// </remarks>
    private static bool Matches(SqlErrorEvent errorEvent, SqlErrorQuery query)
    {
        if (errorEvent.Timestamp < query.StartTime || errorEvent.Timestamp >= query.EndTime)
        {
            return false;
        }

        if (query.Database is { Length: > 0 }
            && !string.Equals(errorEvent.Database, query.Database, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Category is { } category && errorEvent.ErrorCategory != category)
        {
            return false;
        }

        if (query.Severity is { } severity && errorEvent.Severity != severity)
        {
            return false;
        }

        if (query.ConfirmedOnly && !errorEvent.Confirmed)
        {
            return false;
        }

        if (query.Application is { Length: > 0 }
            && !string.Equals(errorEvent.Application, query.Application, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.StoredProcedure is { Length: > 0 }
            && !ProcedureMatches(errorEvent.StoredProcedure, query.StoredProcedure))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compares procedure names allowing for schema qualification.
    /// </summary>
    /// <remarks>
    /// Sources disagree about whether to include the schema, so <c>usp_GetOrders</c> has to
    /// match <c>dbo.usp_GetOrders</c>. Only the trailing component is compared, never a
    /// substring, so <c>usp_Get</c> does not match <c>usp_GetOrders</c>.
    /// </remarks>
    private static bool ProcedureMatches(string? actual, string filter)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        return string.Equals(actual, filter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(LastComponent(actual), LastComponent(filter), StringComparison.OrdinalIgnoreCase);

        static string LastComponent(string name)
        {
            var index = name.LastIndexOf('.');
            return index >= 0 && index < name.Length - 1 ? name[(index + 1)..] : name;
        }
    }

    /// <summary>Fills in the fields every SQL error response shares.</summary>
    private void Describe(SqlErrorResponseBase response, SqlErrorQuery query, CollectedErrors collected)
    {
        response.Server = _sqlServer.Server;
        response.Database = query.Database;
        response.StartTime = query.StartTime;
        response.EndTime = query.EndTime;
        response.Sources = collected.Sources;
        response.ApplicationTelemetry = collected.ApplicationTelemetry;

        response.ApplicationTelemetryAvailable = collected.Sources.Any(source =>
            source.Kind == SqlErrorSourceKind.ApplicationTelemetry
            && source.Status is SqlErrorSourceStatus.Available or SqlErrorSourceStatus.Partial);

        if (response.ApplicationTelemetry is { } applicationTelemetry)
        {
            var mapped = applicationTelemetry.Applications;
            var configured = mapped.Count(application => application.Status != ApplicationTelemetryStatus.NotConfigured);
            var usable = mapped.Count(application => application.Status is ApplicationTelemetryStatus.Available or ApplicationTelemetryStatus.NoData);
            var unavailableApplications = mapped.Count(application => application.Status is ApplicationTelemetryStatus.Unavailable or ApplicationTelemetryStatus.PermissionDenied);
            response.ApplicationTelemetryAvailable = usable > 0;
            if (mapped.Count > 0 && unavailableApplications == 0 && mapped.All(application => application.Status == ApplicationTelemetryStatus.NoData))
            {
                response.ApplicationTelemetry.Status = ApplicationTelemetryStatus.NoData;
            }
            else if (unavailableApplications > 0 && usable > 0)
            {
                response.ApplicationTelemetry.Status = ApplicationTelemetryStatus.Partial;
            }
            else if (unavailableApplications > 0)
            {
                response.ApplicationTelemetry.Status = ApplicationTelemetryStatus.Unavailable;
            }
            else if (configured == 0)
            {
                response.ApplicationTelemetry.Status = ApplicationTelemetryStatus.NotConfigured;
            }
            else
            {
                response.ApplicationTelemetry.Status = ApplicationTelemetryStatus.Available;
            }
        }

        var answered = collected.Sources.Count(source =>
            source.Status is SqlErrorSourceStatus.Available or SqlErrorSourceStatus.Partial);

        // NOT_APPLICABLE is an intentional scope decision, not a source failure. The
        // Query Store candidate source returns it for the server-wide SQL Errors query
        // because Query Store evidence is only meaningful when one database is selected.
        // Keep that state visible in Sources, but do not downgrade a working SQL Server
        // source to PARTIAL_ERROR_DATA. A genuine read/configuration failure remains
        // unavailable and continues to affect the overall status.
        var applicationSourceUnavailable = response.ApplicationTelemetry is { Status: ApplicationTelemetryStatus.Unavailable or ApplicationTelemetryStatus.Partial }
            || collected.Sources.Any(source => source.Kind == SqlErrorSourceKind.ApplicationTelemetry
                && source.Status is SqlErrorSourceStatus.NotConfigured or SqlErrorSourceStatus.PermissionDenied
                    or SqlErrorSourceStatus.Failed or SqlErrorSourceStatus.Partial);
        var unavailable = collected.Sources.Count(source =>
            source.Status is SqlErrorSourceStatus.NotConfigured
                or SqlErrorSourceStatus.PermissionDenied
                or SqlErrorSourceStatus.Failed
                or SqlErrorSourceStatus.Partial)
            - (response.ApplicationTelemetry is not null && !applicationSourceUnavailable
                ? collected.Sources.Count(source => source.Name == "signoz"
                    && source.Status is SqlErrorSourceStatus.NotConfigured or SqlErrorSourceStatus.PermissionDenied or SqlErrorSourceStatus.Failed)
                : 0);
        if (applicationSourceUnavailable)
        {
            unavailable++;
        }

        response.Status = answered == 0
            ? SqlErrorQueryStatus.ErrorSourceUnavailable
            : unavailable > 0
                ? SqlErrorQueryStatus.PartialErrorData
                : collected.Events.Count == 0
                    ? SqlErrorQueryStatus.NoErrorsFound
                    : SqlErrorQueryStatus.Ok;

        var message = response.Status switch
        {
            SqlErrorQueryStatus.ErrorSourceUnavailable => SourceUnavailableMessage,
            SqlErrorQueryStatus.PartialErrorData => PartialDataMessage,
            SqlErrorQueryStatus.NoErrorsFound => NoErrorsMessage,
            _ => string.Empty,
        };

        var noMappedApps = response.ApplicationTelemetry?.Status == ApplicationTelemetryStatus.NotConfigured;
        if (!response.ApplicationTelemetryAvailable && !noMappedApps)
        {
            message = message.Length == 0
                ? NoApplicationTelemetryMessage
                : $"{message} {NoApplicationTelemetryMessage}";
        }

        response.Message = message;
    }

    private static List<string> Distinct(IEnumerable<string?> values) =>
    [
        .. values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
    ];

    private sealed record CollectedErrors(
        List<SqlErrorEvent> Events,
        List<SqlErrorSourceState> Sources,
        ApplicationTelemetrySummary? ApplicationTelemetry);
}
