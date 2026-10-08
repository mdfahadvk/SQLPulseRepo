using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.SqlErrors;

public sealed class ApplicationTelemetryApplication
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("service_name")] public string ServiceName { get; set; } = string.Empty;
    [JsonPropertyName("database")] public string? Database { get; set; }
    [JsonPropertyName("status")] public ApplicationTelemetryStatus Status { get; set; }
    [JsonPropertyName("error_count")] public int ErrorCount { get; set; }
}

public sealed class ApplicationTelemetrySummary
{
    [JsonPropertyName("status")] public ApplicationTelemetryStatus Status { get; set; }
    [JsonPropertyName("applications")] public List<ApplicationTelemetryApplication> Applications { get; set; } = [];
}

/// <summary>Whether one error source could be consulted, and what it said.</summary>
public sealed class SqlErrorSourceState
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public SqlErrorSourceKind Kind { get; set; } = SqlErrorSourceKind.ApplicationTelemetry;

    [JsonPropertyName("status")]
    public SqlErrorSourceStatus Status { get; set; } = SqlErrorSourceStatus.NotConfigured;

    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Fields every SQL error response carries so a caller always knows how much of the
/// picture it is looking at.
/// </summary>
public abstract class SqlErrorResponseBase
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("start_time")]
    public DateTimeOffset StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public DateTimeOffset EndTime { get; set; }

    [JsonPropertyName("status")]
    public SqlErrorQueryStatus Status { get; set; } = SqlErrorQueryStatus.ErrorSourceUnavailable;

    /// <summary>Plain-language explanation of <see cref="Status"/>, safe to show to a user.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Whether any application telemetry source answered.
    /// </summary>
    /// <remarks>
    /// Reported separately from <see cref="Status"/> because SQLPulse can have complete
    /// SQL Server evidence and still be blind to what the application experienced. Without
    /// this flag a caller could read "no errors found" as "the application saw no errors".
    /// </remarks>
    [JsonPropertyName("application_telemetry_available")]
    public bool ApplicationTelemetryAvailable { get; set; }

    [JsonPropertyName("sources")]
    public List<SqlErrorSourceState> Sources { get; set; } = [];

    [JsonPropertyName("application_telemetry")]
    public ApplicationTelemetrySummary? ApplicationTelemetry { get; set; }
}

/// <summary>Result of <c>GET /api/sql-errors</c>.</summary>
public sealed class SqlErrorListResponse : SqlErrorResponseBase
{
    /// <summary>Events matching the filters before <c>top_n</c> was applied.</summary>
    [JsonPropertyName("total_matched")]
    public int TotalMatched { get; set; }

    [JsonPropertyName("returned")]
    public int Returned { get; set; }

    [JsonPropertyName("top_n")]
    public int TopN { get; set; }

    [JsonPropertyName("errors")]
    public List<SqlErrorEvent> Errors { get; set; } = [];
}

/// <summary>Result of <c>GET /api/sql-errors/summary</c>.</summary>
public sealed class SqlErrorSummaryResponse : SqlErrorResponseBase
{
    [JsonPropertyName("total_errors")]
    public int TotalErrors { get; set; }

    [JsonPropertyName("timeout_count")]
    public int TimeoutCount { get; set; }

    [JsonPropertyName("deadlock_count")]
    public int DeadlockCount { get; set; }

    [JsonPropertyName("connection_error_count")]
    public int ConnectionErrorCount { get; set; }

    [JsonPropertyName("permission_error_count")]
    public int PermissionErrorCount { get; set; }

    [JsonPropertyName("other_error_count")]
    public int OtherErrorCount { get; set; }

    /// <summary>Timeouts an application or telemetry source actually recorded.</summary>
    [JsonPropertyName("confirmed_timeout_count")]
    public int ConfirmedTimeoutCount { get; set; }

    /// <summary>Timeouts inferred from performance evidence alone, and therefore unconfirmed.</summary>
    [JsonPropertyName("timeout_candidate_count")]
    public int TimeoutCandidateCount { get; set; }

    [JsonPropertyName("affected_databases")]
    public List<string> AffectedDatabases { get; set; } = [];

    [JsonPropertyName("affected_applications")]
    public List<string> AffectedApplications { get; set; } = [];

    /// <summary>Event count per <see cref="SqlErrorCategory"/>, keyed by its wire value.</summary>
    [JsonPropertyName("category_counts")]
    public Dictionary<string, int> CategoryCounts { get; set; } = [];
}

/// <summary>One piece of SQLPulse performance evidence observed near an error.</summary>
public sealed class CorrelatedEvidenceItem
{
    [JsonPropertyName("kind")]
    public CorrelatedEvidenceKind Kind { get; set; }

    /// <summary>The SQLPulse capability the evidence came from, for example <c>query_store</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public List<string> Detail { get; set; } = [];

    /// <summary>
    /// What tied this evidence to the error: <c>query_hash</c>, <c>stored_procedure</c>,
    /// <c>database</c> or <c>time_window</c>.
    /// </summary>
    /// <remarks>
    /// Present so a reader can see whether the link is an identity match or nothing more
    /// than two things happening at once.
    /// </remarks>
    [JsonPropertyName("matched_on")]
    public List<string> MatchedOn { get; set; } = [];

    [JsonPropertyName("observed_at")]
    public DateTimeOffset? ObservedAt { get; set; }
}

/// <summary>
/// SQLPulse performance evidence found around an error's timestamp.
/// </summary>
/// <remarks>
/// This type reports coincidence, not cause. The wording of <see cref="Assessment"/> is
/// built from the evidence rather than written by the language model, so no phrasing can
/// promote "these happened together" into "this caused that".
/// </remarks>
public sealed class SqlErrorCorrelation
{
    /// <summary>Fixed reminder attached to every correlation result.</summary>
    public const string CausationCaveat =
        "Correlated evidence describes observations that coincided in time with the error. "
        + "It does not establish that one caused the other.";

    [JsonPropertyName("window_start")]
    public DateTimeOffset WindowStart { get; set; }

    [JsonPropertyName("window_end")]
    public DateTimeOffset WindowEnd { get; set; }

    [JsonPropertyName("window_minutes")]
    public int WindowMinutes { get; set; }

    [JsonPropertyName("correlation_confidence")]
    public CorrelationConfidence CorrelationConfidence { get; set; } = CorrelationConfidence.None;

    /// <summary>True when evidence was tied to the same query hash or stored procedure.</summary>
    [JsonPropertyName("identity_matched")]
    public bool IdentityMatched { get; set; }

    [JsonPropertyName("correlated_evidence")]
    public List<CorrelatedEvidenceItem> CorrelatedEvidence { get; set; } = [];

    /// <summary>Deterministic, non-causal reading of the correlated evidence.</summary>
    [JsonPropertyName("assessment")]
    public string Assessment { get; set; } = string.Empty;

    /// <summary>Limits of the evidence, always including <see cref="CausationCaveat"/>.</summary>
    [JsonPropertyName("caveats")]
    public List<string> Caveats { get; set; } = [];
}

/// <summary>Result of <c>GET /api/sql-errors/{id}</c>.</summary>
/// <remarks>
/// An unknown id is reported with <see cref="Found"/> false rather than a 404, matching
/// the investigation detail endpoint: the request was well-formed and the answer is
/// simply that SQLPulse is no longer holding that event.
/// </remarks>
public sealed class SqlErrorDetailResponse
{
    /// <summary>Returned when an id is not among the recently retrieved events.</summary>
    public const string NotFoundMessage =
        "This error event is no longer available. SQLPulse does not store error events, so "
        + "re-run the SQL Errors query for the relevant period to retrieve it again.";

    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("error")]
    public SqlErrorEvent? Error { get; set; }

    [JsonPropertyName("correlation")]
    public SqlErrorCorrelation? Correlation { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
