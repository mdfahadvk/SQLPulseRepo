using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>
/// The filters a caller applies when asking for SQL errors.
/// </summary>
/// <param name="Server">Monitored server the errors must belong to.</param>
/// <param name="Database">Optional database filter.</param>
/// <param name="StartTime">Inclusive start of the period.</param>
/// <param name="EndTime">Exclusive end of the period.</param>
/// <param name="Category">Optional normalized category filter.</param>
/// <param name="Application">Optional application filter, matched case-insensitively.</param>
/// <param name="StoredProcedure">Optional stored procedure filter, matched case-insensitively.</param>
/// <param name="Severity">Optional severity filter.</param>
/// <param name="ConfirmedOnly">Exclude anything an application or telemetry source did not record itself.</param>
/// <param name="TopN">Maximum number of events to return.</param>
public sealed record SqlErrorQuery(
    string Server,
    string? Database,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    SqlErrorCategory? Category = null,
    string? Application = null,
    string? StoredProcedure = null,
    Severity? Severity = null,
    bool ConfirmedOnly = false,
    int TopN = 100);

/// <summary>
/// An unclassified failure exactly as an error source observed it.
/// </summary>
/// <remarks>
/// Sources report evidence and nothing else. Category, severity and timeout
/// classification are withheld from this type on purpose, so that every source is
/// classified by the same rules in <see cref="SqlErrorClassifier"/> and two feeds
/// describing the same failure cannot disagree about what it was.
/// </remarks>
public sealed record RawSqlErrorEvent
{
    public required DateTimeOffset Timestamp { get; init; }

    public string? Server { get; init; }

    public string? Database { get; init; }

    public string? Application { get; init; }

    public string? Environment { get; init; }

    public string? Host { get; init; }

    public string? SessionIdentifier { get; init; }

    public string? TraceId { get; init; }

    public string? CorrelationId { get; init; }

    public string? RequestEndpoint { get; init; }

    public string? StoredProcedure { get; init; }

    public string? QueryHash { get; init; }

    public int? SqlErrorNumber { get; init; }

    public int? SqlErrorState { get; init; }

    public int? SqlErrorClass { get; init; }

    public string? Message { get; init; }

    public string? ExceptionType { get; init; }

    public double? CommandTimeoutSeconds { get; init; }

    public double? DurationMs { get; init; }

    /// <summary>
    /// Set only when the source itself recorded that the client abandoned the command.
    /// </summary>
    /// <remarks>
    /// This single flag is what separates a confirmed application timeout from a
    /// candidate, so it must never be set from duration evidence. A source that merely
    /// knows a statement ran for a long time leaves it false.
    /// </remarks>
    public bool ApplicationTimeoutReported { get; init; }
    public string? SourceName { get; init; }

    public string? EventName { get; init; }

    public string? SqlText { get; init; }

    public int? SessionId { get; init; }

    /// <summary>
    /// Parameter values explicitly supplied by the execution-time telemetry, after masking.
    /// Empty means that the source did not provide values or capture was disabled.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What one source returned for a query.</summary>
/// <param name="Status">Whether the source could be consulted.</param>
/// <param name="Events">Observed failures, empty when the source saw none.</param>
/// <param name="Message">Explanation shown to the caller, required for anything but <c>Available</c>.</param>
/// <param name="ApplicationTelemetry">Optional per-application telemetry registry outcome.</param>
public sealed record SqlErrorSourceResult(
    SqlErrorSourceStatus Status,
    IReadOnlyList<RawSqlErrorEvent> Events,
    string? Message = null,
    ApplicationTelemetrySummary? ApplicationTelemetry = null)
{
    /// <summary>The source answered with the events it observed.</summary>
    public static SqlErrorSourceResult Available(IReadOnlyList<RawSqlErrorEvent> events) =>
        new(SqlErrorSourceStatus.Available, events);

    /// <summary>The source exists but has not been configured.</summary>
    public static SqlErrorSourceResult NotConfigured(string message) =>
        new(SqlErrorSourceStatus.NotConfigured, [], message);

    /// <summary>The source cannot answer this particular query.</summary>
    public static SqlErrorSourceResult NotApplicable(string message) =>
        new(SqlErrorSourceStatus.NotApplicable, [], message);

    /// <summary>The source was consulted and failed.</summary>
    public static SqlErrorSourceResult Failed(string message) =>
        new(SqlErrorSourceStatus.Failed, [], message);

    /// <summary>The source rejected its configured credentials or permissions.</summary>
    public static SqlErrorSourceResult PermissionDenied(string message) =>
        new(SqlErrorSourceStatus.PermissionDenied, [], message);
}

/// <summary>
/// A feed of SQL-related failures SQLPulse can read.
/// </summary>
/// <remarks>
/// <para>
/// SQLPulse has no error feed of its own, so this abstraction exists to let one be
/// attached — application structured logs, an observability backend such as SigNoz, an
/// Extended Events session an operator has already approved and enabled, or SQL Server's
/// own error surfaces. Adding a feed is a registration, not a change to the API, the
/// summary maths or the React page.
/// </para>
/// <para>
/// Implementations are strictly read-only. In particular, an Extended Events source reads
/// a session somebody else created: SQLPulse never creates, alters, starts or stops one,
/// because changing the server it is diagnosing would break the guarantee the rest of the
/// product is built on.
/// </para>
/// <para>
/// An implementation must never invent an event. When it cannot answer it says so through
/// <see cref="SqlErrorSourceResult"/>, which surfaces as
/// <see cref="SqlErrorQueryStatus.ErrorSourceUnavailable"/> or
/// <see cref="SqlErrorQueryStatus.PartialErrorData"/> rather than as a clean period.
/// </para>
/// </remarks>
public interface ISqlErrorSource
{
    /// <summary>Stable identifier reported in the response and on every event.</summary>
    string Name { get; }

    /// <summary>What kind of evidence this source contributes.</summary>
    SqlErrorSourceKind Kind { get; }

    /// <summary>
    /// Returns the failures this source observed in the requested period.
    /// </summary>
    /// <remarks>
    /// May throw: <c>SqlErrorService</c> isolates a failing source so one broken feed
    /// degrades the answer to partial data instead of losing the others.
    /// </remarks>
    Task<SqlErrorSourceResult> QueryAsync(SqlErrorQuery query, CancellationToken cancellationToken = default);
}
