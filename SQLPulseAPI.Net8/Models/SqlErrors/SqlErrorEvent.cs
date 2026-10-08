using System.Text.Json.Serialization;
using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Models.SqlErrors;

/// <summary>
/// A single SQL-related failure, normalized so events from different sources can be
/// compared, filtered and counted together.
/// </summary>
/// <remarks>
/// <para>
/// Parameter values are present only when an approved execution-time telemetry source
/// supplied them, capture was enabled, and the values survived sensitive-value masking.
/// Query Store and cached plans never populate them.
/// </para>
/// <para>
/// <see cref="ErrorCategory"/>, <see cref="Severity"/> and <see cref="TimeoutClassification"/>
/// are assigned by <c>SqlErrorClassifier</c>, not by the source, so two sources reporting
/// the same failure classify identically.
/// </para>
/// </remarks>
public sealed class SqlErrorEvent
{
    /// <summary>
    /// Stable identifier derived from the event's own content.
    /// </summary>
    /// <remarks>
    /// Derived rather than random so that re-running the same query yields the same id and
    /// a detail link stays valid, even though SQLPulse does not store error events.
    /// </remarks>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("application")]
    public string? Application { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    /// <summary>User or session the failure was observed on, as reported by the source.</summary>
    [JsonPropertyName("session_identifier")]
    public string? SessionIdentifier { get; set; }

    [JsonPropertyName("trace_id")]
    public string? TraceId { get; set; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }

    /// <summary>Application request or endpoint the failure occurred under.</summary>
    [JsonPropertyName("request_endpoint")]
    public string? RequestEndpoint { get; set; }

    [JsonPropertyName("stored_procedure")]
    public string? StoredProcedure { get; set; }

    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    /// <summary>
    /// SQL Server error number, when the failure came from SQL Server.
    /// </summary>
    /// <remarks>
    /// Null is meaningful and common: a client-side command timeout is raised by the data
    /// access library, not by SQL Server, so it has no server error number at all.
    /// </remarks>
    [JsonPropertyName("sql_error_number")]
    public int? SqlErrorNumber { get; set; }

    [JsonPropertyName("sql_error_state")]
    public int? SqlErrorState { get; set; }

    /// <summary>SQL Server error severity class. 20 and above are fatal.</summary>
    [JsonPropertyName("sql_error_class")]
    public int? SqlErrorClass { get; set; }

    [JsonPropertyName("error_category")]
    public SqlErrorCategory ErrorCategory { get; set; } = SqlErrorCategory.Unknown;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Application exception type, when telemetry reported one.</summary>
    [JsonPropertyName("exception_type")]
    public string? ExceptionType { get; set; }

    /// <summary>The command timeout the client was configured with, when reported.</summary>
    [JsonPropertyName("command_timeout_seconds")]
    public double? CommandTimeoutSeconds { get; set; }

    [JsonPropertyName("duration_ms")]
    public double? DurationMs { get; set; }

    /// <summary>Sanitized execution-time parameter values, when the source supplied them.</summary>
    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string?> Parameters { get; set; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Name of the <c>ISqlErrorSource</c> that reported the event.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("source_kind")]
    public SqlErrorSourceKind SourceKind { get; set; } = SqlErrorSourceKind.ApplicationTelemetry;

    [JsonPropertyName("is_timeout")]
    public bool IsTimeout { get; set; }

    [JsonPropertyName("is_deadlock")]
    public bool IsDeadlock { get; set; }

    [JsonPropertyName("severity")]
    public Severity Severity { get; set; } = Severity.Medium;

    /// <summary>
    /// Whether the timeout is confirmed, merely a candidate, or only a slow query.
    /// </summary>
    [JsonPropertyName("timeout_classification")]
    public TimeoutClassification TimeoutClassification { get; set; } = TimeoutClassification.NotApplicable;

    /// <summary>Why the category and timeout classification were chosen.</summary>
    /// <remarks>
    /// Recorded so a reader can check the reasoning instead of trusting the label, and so a
    /// misclassification can be traced to the exact rule that produced it.
    /// </remarks>
    [JsonPropertyName("classification_evidence")]
    public List<string> ClassificationEvidence { get; set; } = [];

    /// <summary>True when an application or telemetry source recorded the failure itself.</summary>
    [JsonPropertyName("confirmed")]
    public bool Confirmed { get; set; }
}
