using System.Runtime.Serialization;

namespace SQLPulseAPI.Net8.Models.SqlErrors;

/// <summary>
/// Normalized classification of a SQL-related failure.
/// </summary>
/// <remarks>
/// Assigned deterministically from SQL Server error numbers and, only when no number is
/// available, from evidence-based message rules. It is never inferred by the language
/// model: a category drives what an operator believes happened, so it has to be
/// reproducible from the evidence alone.
/// </remarks>
public enum SqlErrorCategory
{
    [EnumMember(Value = "TIMEOUT")]
    Timeout,

    [EnumMember(Value = "DEADLOCK")]
    Deadlock,

    [EnumMember(Value = "CONNECTION")]
    Connection,

    [EnumMember(Value = "LOGIN_FAILURE")]
    LoginFailure,

    [EnumMember(Value = "PERMISSION_DENIED")]
    PermissionDenied,

    [EnumMember(Value = "CONSTRAINT_VIOLATION")]
    ConstraintViolation,

    [EnumMember(Value = "CONVERSION_ERROR")]
    ConversionError,

    [EnumMember(Value = "ARITHMETIC_ERROR")]
    ArithmeticError,

    [EnumMember(Value = "INVALID_OBJECT")]
    InvalidObject,

    [EnumMember(Value = "INVALID_COLUMN")]
    InvalidColumn,

    [EnumMember(Value = "TRANSACTION_ERROR")]
    TransactionError,

    [EnumMember(Value = "SQL_EXECUTION_ERROR")]
    SqlExecutionError,

    [EnumMember(Value = "UNKNOWN")]
    Unknown,
}

/// <summary>
/// How certain SQLPulse is that a slow or failed statement was actually a timeout.
/// </summary>
/// <remarks>
/// The three states are deliberately not interchangeable. Only an application or
/// telemetry source can confirm that a client gave up waiting; SQL Server performance
/// evidence can at most show that a statement ran long enough for that to have happened.
/// Collapsing the distinction would let SQLPulse report an incident that nobody observed.
/// </remarks>
public enum TimeoutClassification
{
    /// <summary>An application or telemetry source explicitly recorded a SQL command timeout.</summary>
    [EnumMember(Value = "CONFIRMED_APPLICATION_TIMEOUT")]
    ConfirmedApplicationTimeout,

    /// <summary>
    /// Performance evidence reached the configured application timeout, but no application
    /// timeout event was observed.
    /// </summary>
    [EnumMember(Value = "TIMEOUT_CANDIDATE")]
    TimeoutCandidate,

    /// <summary>
    /// The statement exceeded the SQLPulse monitoring threshold but not the configured
    /// application timeout. Slow is not a timeout.
    /// </summary>
    [EnumMember(Value = "SLOW_QUERY")]
    SlowQuery,

    /// <summary>The event is not a timeout question at all, or no duration evidence exists.</summary>
    [EnumMember(Value = "NOT_APPLICABLE")]
    NotApplicable,
}

/// <summary>What kind of evidence a <c>ISqlErrorSource</c> contributes.</summary>
public enum SqlErrorSourceKind
{
    /// <summary>
    /// Errors an application or its telemetry pipeline actually recorded. Only this kind
    /// can confirm an application-side SQL timeout.
    /// </summary>
    [EnumMember(Value = "APPLICATION_TELEMETRY")]
    ApplicationTelemetry,

    /// <summary>Errors and events reported by SQL Server itself.</summary>
    [EnumMember(Value = "SQL_SERVER")]
    SqlServer,

    /// <summary>
    /// Candidates derived from SQLPulse's own performance evidence. Never a confirmation of
    /// anything an application observed.
    /// </summary>
    [EnumMember(Value = "DERIVED_PERFORMANCE_EVIDENCE")]
    DerivedPerformanceEvidence,
}

/// <summary>Outcome of asking one error source for a time range.</summary>
public enum SqlErrorSourceStatus
{
    /// <summary>The source answered. It may still have returned no events.</summary>
    [EnumMember(Value = "AVAILABLE")]
    Available,

    /// <summary>The source exists but has not been configured, so it cannot be consulted.</summary>
    [EnumMember(Value = "NOT_CONFIGURED")]
    NotConfigured,

    /// <summary>The source cannot answer this particular query, for example a missing database filter.</summary>
    [EnumMember(Value = "NOT_APPLICABLE")]
    NotApplicable,

    /// <summary>The source was consulted and failed.</summary>
    [EnumMember(Value = "FAILED")]
    Failed,

    /// <summary>The source rejected the configured credentials or permissions.</summary>
    [EnumMember(Value = "PERMISSION_DENIED")]
    PermissionDenied,

    [EnumMember(Value = "PARTIAL")]
    Partial,
}

public enum ApplicationTelemetryStatus
{
    [EnumMember(Value = "NOT_CONFIGURED")] NotConfigured,
    [EnumMember(Value = "CONFIGURED")] Configured,
    [EnumMember(Value = "AVAILABLE")] Available,
    [EnumMember(Value = "NO_DATA")] NoData,
    [EnumMember(Value = "UNAVAILABLE")] Unavailable,
    [EnumMember(Value = "PERMISSION_DENIED")] PermissionDenied,
    [EnumMember(Value = "PARTIAL")] Partial,
}

/// <summary>
/// Overall outcome of a SQL error query.
/// </summary>
/// <remarks>
/// "Nothing went wrong" and "SQLPulse cannot tell whether anything went wrong" are
/// different answers and are never merged: reporting an unconnected telemetry pipeline as
/// a clean period would be a false assurance.
/// </remarks>
public enum SqlErrorQueryStatus
{
    /// <summary>Every consulted source answered and at least one error event matched.</summary>
    [EnumMember(Value = "OK")]
    Ok,

    /// <summary>Every consulted source answered and none reported an error for this period.</summary>
    [EnumMember(Value = "NO_ERRORS_FOUND")]
    NoErrorsFound,

    /// <summary>At least one source answered and at least one could not be consulted.</summary>
    [EnumMember(Value = "PARTIAL_ERROR_DATA")]
    PartialErrorData,

    /// <summary>No source could be consulted, so the absence of errors proves nothing.</summary>
    [EnumMember(Value = "ERROR_SOURCE_UNAVAILABLE")]
    ErrorSourceUnavailable,
}

/// <summary>How strongly SQLPulse's performance evidence lines up with an error event.</summary>
/// <remarks>
/// Confidence describes the strength of the <em>correlation</em>, never a causal claim.
/// Even <see cref="High"/> means "these coincided and refer to the same query", not
/// "this caused that".
/// </remarks>
public enum CorrelationConfidence
{
    /// <summary>Same query identity, inside the window, with corroborating evidence.</summary>
    [EnumMember(Value = "HIGH")]
    High,

    /// <summary>Same query identity inside the window, with no corroborating evidence.</summary>
    [EnumMember(Value = "MEDIUM")]
    Medium,

    /// <summary>Activity in the same database inside the window, but no identity match.</summary>
    [EnumMember(Value = "LOW")]
    Low,

    /// <summary>No supporting evidence was found in the window.</summary>
    [EnumMember(Value = "NONE")]
    None,
}

/// <summary>The kind of SQLPulse evidence a correlated observation came from.</summary>
public enum CorrelatedEvidenceKind
{
    [EnumMember(Value = "QUERY_STORE_HISTORY")]
    QueryStoreHistory,

    [EnumMember(Value = "QUERY_BASELINE")]
    QueryBaseline,

    [EnumMember(Value = "BLOCKING")]
    Blocking,

    [EnumMember(Value = "LONG_RUNNING_QUERY")]
    LongRunningQuery,
}
