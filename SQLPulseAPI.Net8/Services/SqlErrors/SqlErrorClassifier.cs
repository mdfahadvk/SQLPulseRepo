using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Agent;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>
/// What the classifier needs to know that the raw event cannot tell it.
/// </summary>
/// <param name="Server">Monitored server, used when a source does not name one.</param>
/// <param name="SourceName">Name of the reporting source.</param>
/// <param name="SourceKind">What kind of evidence the source contributes.</param>
/// <param name="ApplicationTimeoutMs">
/// The configured application command timeout, or null when none has been configured.
/// Null means timeout candidacy cannot be decided and is never guessed.
/// </param>
/// <param name="SlowQueryThresholdMs">The SQLPulse monitoring threshold for slow queries.</param>
public sealed record SqlErrorClassificationContext(
    string Server,
    string SourceName,
    SqlErrorSourceKind SourceKind,
    int? ApplicationTimeoutMs,
    int SlowQueryThresholdMs);

/// <summary>
/// Turns a raw observation into a normalized <see cref="SqlErrorEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// Classification is deterministic and runs entirely here. The language model is shown the
/// result and never asked to produce it: a category and a timeout classification decide
/// what an operator believes happened during an incident, and that has to be reproducible
/// from the evidence rather than resampled on every investigation.
/// </para>
/// <para>
/// Evidence is ranked. A SQL Server error number is authoritative; message text is
/// consulted only when no number exists, which is the normal case for a client-side
/// command timeout raised by the data provider rather than by SQL Server.
/// </para>
/// </remarks>
public static class SqlErrorClassifier
{
    /// <summary>Upper bound on a stored message, in characters.</summary>
    /// <remarks>
    /// Error text frequently carries the offending row, parameter values or a serialized
    /// payload. The first part identifies the failure; the rest is business data that has
    /// no diagnostic value and must not be forwarded to a model or rendered in a browser.
    /// </remarks>
    public const int MaxMessageLength = 500;

    /// <summary>Suffix appended to a message that was shortened.</summary>
    public const string TruncationSuffix = "…[truncated]";

    /// <summary>Message shown when a source reported no text at all.</summary>
    public const string NoMessageReported = "(no message reported)";

    private const string IdPrefix = "err_";
    private const int IdHexLength = 16;

    /// <summary>
    /// Message rules, applied in order, and only when no SQL Server error number exists.
    /// </summary>
    /// <remarks>
    /// Ordered from most specific to least: a deadlock message also mentions a lock, and a
    /// login failure also mentions a connection, so the narrower rule has to match first.
    /// </remarks>
    private static readonly (Regex Pattern, SqlErrorCategory Category, string Evidence)[] MessageRules =
    [
        (new Regex(@"deadlock", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.Deadlock, "message reports a deadlock"),

        (new Regex(@"(execution\s+)?timeout\s+expired|command\s+timeout|operation\s+timed\s+out|timeout\s+period\s+elapsed",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.Timeout, "message reports an expired timeout"),

        (new Regex(@"login\s+failed|password\s+(has\s+)?expired|account\s+is\s+disabled",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.LoginFailure, "message reports a failed login"),

        (new Regex(@"permission\s+(was\s+)?denied|do(es)?\s+not\s+have\s+permission",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.PermissionDenied, "message reports denied permission"),

        (new Regex(@"transport-level\s+error|network-related\s+or\s+instance-specific|forcibly\s+closed|server\s+was\s+not\s+found|no\s+such\s+host|connection\s+refused",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.Connection, "message reports a connection failure"),

        (new Regex(@"violation\s+of\s+\w+\s+constraint|cannot\s+insert\s+the\s+value\s+null|duplicate\s+key|foreign\s+key\s+constraint",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.ConstraintViolation, "message reports a constraint violation"),

        (new Regex(@"conversion\s+failed|error\s+converting\s+data\s+type|would\s+be\s+truncated",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.ConversionError, "message reports a conversion failure"),

        (new Regex(@"divide\s+by\s+zero|arithmetic\s+overflow",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.ArithmeticError, "message reports an arithmetic failure"),

        (new Regex(@"invalid\s+object\s+name|could\s+not\s+find\s+stored\s+procedure",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.InvalidObject, "message reports a missing object"),

        (new Regex(@"invalid\s+column\s+name|ambiguous\s+column\s+name",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.InvalidColumn, "message reports a missing or ambiguous column"),

        (new Regex(@"transaction\s+(count|was\s+doomed|cannot\s+be\s+committed)|rollback\s+transaction\s+request|commit\s+transaction\s+request",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            SqlErrorCategory.TransactionError, "message reports a transaction failure"),
    ];

    /// <summary>
    /// Normalizes and classifies one raw observation.
    /// </summary>
    public static SqlErrorEvent Normalize(RawSqlErrorEvent raw, SqlErrorClassificationContext context)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(context);

        var evidence = new List<string>();
        var message = NormalizeMessage(raw.Message);
        var category = Categorize(raw, message, evidence);
        var timeoutClassification = ClassifyTimeout(raw, category, context, evidence);
        var confirmed = context.SourceKind is not SqlErrorSourceKind.DerivedPerformanceEvidence;

        var normalized = new SqlErrorEvent
        {
            Timestamp = raw.Timestamp,
            Server = string.IsNullOrWhiteSpace(raw.Server) ? context.Server : raw.Server,
            Database = Trim(raw.Database),
            Application = Trim(raw.Application),
            Environment = Trim(raw.Environment),
            Host = Trim(raw.Host),
            SessionIdentifier = Trim(raw.SessionIdentifier),
            TraceId = Trim(raw.TraceId),
            CorrelationId = Trim(raw.CorrelationId),
            RequestEndpoint = Trim(raw.RequestEndpoint),
            StoredProcedure = Trim(raw.StoredProcedure),
            QueryHash = Trim(raw.QueryHash),
            SqlErrorNumber = raw.SqlErrorNumber,
            SqlErrorState = raw.SqlErrorState,
            SqlErrorClass = raw.SqlErrorClass,
            ErrorCategory = category,
            Message = message,
            ExceptionType = Trim(raw.ExceptionType),
            CommandTimeoutSeconds = raw.CommandTimeoutSeconds,
            DurationMs = raw.DurationMs,
            Parameters = new Dictionary<string, string?>(raw.Parameters, StringComparer.OrdinalIgnoreCase),
            Source = context.SourceName,
            SourceKind = context.SourceKind,
            IsTimeout = category == SqlErrorCategory.Timeout,
            IsDeadlock = category == SqlErrorCategory.Deadlock,
            Severity = DetermineSeverity(raw, category, timeoutClassification),
            TimeoutClassification = timeoutClassification,
            ClassificationEvidence = evidence,
            Confirmed = confirmed,
        };

        normalized.Id = BuildId(normalized);
        return normalized;
    }

    /// <summary>
    /// Chooses a category from the strongest evidence available.
    /// </summary>
    private static SqlErrorCategory Categorize(RawSqlErrorEvent raw, string message, List<string> evidence)
    {
        if (SqlErrorNumbers.Categorize(raw.SqlErrorNumber) is { } fromNumber)
        {
            evidence.Add(SqlErrorNumbers.IsKnown(raw.SqlErrorNumber)
                ? $"SQL Server error {raw.SqlErrorNumber} maps to this category"
                : $"SQL Server error {raw.SqlErrorNumber} has no specific mapping");

            return fromNumber;
        }

        // A client-side command timeout never reaches SQL Server, so it arrives with no
        // error number. Message rules are the only deterministic evidence left.
        foreach (var (pattern, category, reason) in MessageRules)
        {
            if (pattern.IsMatch(message))
            {
                evidence.Add($"No SQL Server error number was reported; {reason}");
                return category;
            }
        }

        if (raw.ApplicationTimeoutReported)
        {
            evidence.Add("The source recorded an application command timeout");
            return SqlErrorCategory.Timeout;
        }

        evidence.Add("No SQL Server error number and no recognised message pattern");
        return SqlErrorCategory.Unknown;
    }

    /// <summary>
    /// Decides whether a timeout is confirmed, a candidate, merely slow, or not a timeout
    /// question at all.
    /// </summary>
    /// <remarks>
    /// Confirmation requires a source that watched the client give up. Duration evidence,
    /// however extreme, can only produce a candidate: a statement that ran for a minute
    /// tells us nothing about whether anyone was still waiting for it.
    /// </remarks>
    private static TimeoutClassification ClassifyTimeout(
        RawSqlErrorEvent raw,
        SqlErrorCategory category,
        SqlErrorClassificationContext context,
        List<string> evidence)
    {
        var observedByClient = context.SourceKind is not SqlErrorSourceKind.DerivedPerformanceEvidence;

        if (observedByClient && raw.ApplicationTimeoutReported)
        {
            evidence.Add($"'{context.SourceName}' explicitly recorded a SQL command timeout");
            return TimeoutClassification.ConfirmedApplicationTimeout;
        }

        if (observedByClient && raw.SqlErrorNumber == SqlErrorNumbers.ClientCommandTimeout)
        {
            evidence.Add(
                $"Error {SqlErrorNumbers.ClientCommandTimeout} is raised by the client when it "
                + "stops waiting, which confirms an application command timeout");

            return TimeoutClassification.ConfirmedApplicationTimeout;
        }

        if (raw.SqlErrorNumber == SqlErrorNumbers.LockRequestTimeout)
        {
            evidence.Add(
                $"Error {SqlErrorNumbers.LockRequestTimeout} is a lock request timeout inside "
                + "SQL Server, not an application command timeout");

            return TimeoutClassification.NotApplicable;
        }

        if (raw.DurationMs is not { } duration)
        {
            if (category == SqlErrorCategory.Timeout)
            {
                evidence.Add("Reported as a timeout, but without duration evidence it stays unconfirmed");
                return TimeoutClassification.TimeoutCandidate;
            }

            return TimeoutClassification.NotApplicable;
        }

        // A timeout the source reported for this specific command beats the global
        // setting: different call sites legitimately use different timeouts.
        double? applicationTimeoutMs = raw.CommandTimeoutSeconds is { } seconds and > 0
            ? seconds * 1000.0
            : context.ApplicationTimeoutMs;

        if (applicationTimeoutMs is { } limit && duration >= limit)
        {
            evidence.Add(
                $"Duration {duration:F0} ms reached the configured application timeout "
                + $"{limit:F0} ms, but no application timeout event was observed");

            return TimeoutClassification.TimeoutCandidate;
        }

        if (duration >= context.SlowQueryThresholdMs)
        {
            evidence.Add(applicationTimeoutMs is { } configured
                ? $"Duration {duration:F0} ms exceeded the {context.SlowQueryThresholdMs} ms monitoring "
                  + $"threshold but stayed below the {configured:F0} ms application timeout"
                : $"Duration {duration:F0} ms exceeded the {context.SlowQueryThresholdMs} ms monitoring "
                  + "threshold; no application timeout is configured, so timeout candidacy cannot be decided");

            return TimeoutClassification.SlowQuery;
        }

        return TimeoutClassification.NotApplicable;
    }

    /// <summary>
    /// Assigns severity from the evidence rather than from opinion.
    /// </summary>
    private static Severity DetermineSeverity(
        RawSqlErrorEvent raw,
        SqlErrorCategory category,
        TimeoutClassification timeoutClassification)
    {
        // SQL Server severity classes of 20 and above terminate the connection, so the
        // server has already told us this is the most serious kind of failure.
        if (raw.SqlErrorClass is >= 20)
        {
            return Severity.Critical;
        }

        return category switch
        {
            SqlErrorCategory.Timeout => timeoutClassification == TimeoutClassification.ConfirmedApplicationTimeout
                ? Severity.High
                : Severity.Medium,
            SqlErrorCategory.Deadlock => Severity.High,
            SqlErrorCategory.Connection => Severity.High,
            SqlErrorCategory.LoginFailure => Severity.High,
            SqlErrorCategory.Unknown => Severity.Low,
            _ => Severity.Medium,
        };
    }

    /// <summary>
    /// Strips credentials, collapses whitespace and caps the length.
    /// </summary>
    /// <remarks>
    /// Runs before the message is stored anywhere, so a connection string pasted into an
    /// application's exception text cannot reach the API response, the browser or the
    /// model. Reusing the agent's sanitizer keeps one definition of "sensitive".
    /// </remarks>
    internal static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return NoMessageReported;
        }

        var sanitized = LlmSanitizer.SanitizeText(message) ?? string.Empty;
        var collapsed = Regex.Replace(sanitized, @"\s+", " ").Trim();

        if (collapsed.Length == 0)
        {
            return NoMessageReported;
        }

        return collapsed.Length <= MaxMessageLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, MaxMessageLength), TruncationSuffix);
    }

    /// <summary>
    /// Derives the identifier from the event's own content.
    /// </summary>
    /// <remarks>
    /// SQLPulse does not store error events, so a random id would stop resolving the moment
    /// a query was re-run. Hashing the identifying fields means the same observation always
    /// produces the same id, and two genuinely different failures do not collide.
    /// </remarks>
    internal static string BuildId(SqlErrorEvent normalized)
    {
        var material = string.Join(
            '|',
            normalized.Source,
            normalized.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            normalized.Server,
            normalized.Database ?? string.Empty,
            normalized.Application ?? string.Empty,
            normalized.StoredProcedure ?? string.Empty,
            normalized.QueryHash ?? string.Empty,
            normalized.SqlErrorNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            normalized.CorrelationId ?? string.Empty,
            normalized.Message);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return IdPrefix + Convert.ToHexString(digest)[..IdHexLength].ToLowerInvariant();
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
