using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.SqlErrors;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// The deterministic rules that decide what a SQL failure was.
/// </summary>
/// <remarks>
/// These tests exist because the classification is what an operator acts on. The
/// distinction that matters most is between a timeout somebody observed, a timeout
/// SQLPulse merely inferred from a duration, and a query that was only slow: collapsing
/// those three would let SQLPulse report incidents that never happened.
/// </remarks>
public class SqlErrorClassifierTests
{
    private const int ApplicationTimeoutMs = 30_000;
    private const int SlowQueryThresholdMs = 5_000;

    private static readonly DateTimeOffset Observed = new(2026, 9, 17, 10, 42, 16, TimeSpan.Zero);

    private static SqlErrorClassificationContext Context(
        SqlErrorSourceKind kind = SqlErrorSourceKind.ApplicationTelemetry,
        int? applicationTimeoutMs = ApplicationTimeoutMs) =>
        new("sql01", "test_source", kind, applicationTimeoutMs, SlowQueryThresholdMs);

    private static RawSqlErrorEvent Raw(
        string? message = null,
        int? errorNumber = null,
        double? durationMs = null,
        bool applicationTimeoutReported = false) =>
        new()
        {
            Timestamp = Observed,
            Database = "Merchandising",
            Application = "Merchandising.Api",
            Message = message,
            SqlErrorNumber = errorNumber,
            DurationMs = durationMs,
            ApplicationTimeoutReported = applicationTimeoutReported,
        };

    // ─── Timeout: the three claims must stay apart ──────────────────────────

    [Fact]
    public void A_timeout_the_application_recorded_is_confirmed()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Execution Timeout Expired.", durationMs: 42_800, applicationTimeoutReported: true),
            Context());

        Assert.Equal(SqlErrorCategory.Timeout, normalized.ErrorCategory);
        Assert.Equal(TimeoutClassification.ConfirmedApplicationTimeout, normalized.TimeoutClassification);
        Assert.True(normalized.IsTimeout);
        Assert.True(normalized.Confirmed);
    }

    [Fact]
    public void The_client_timeout_error_number_confirms_a_timeout_on_its_own()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Timeout expired.", errorNumber: SqlErrorNumbers.ClientCommandTimeout),
            Context());

        Assert.Equal(SqlErrorCategory.Timeout, normalized.ErrorCategory);
        Assert.Equal(TimeoutClassification.ConfirmedApplicationTimeout, normalized.TimeoutClassification);
    }

    [Fact]
    public void Duration_alone_produces_a_candidate_not_a_confirmation()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Query Store recorded a long duration.", durationMs: 42_800),
            Context(SqlErrorSourceKind.DerivedPerformanceEvidence));

        Assert.Equal(TimeoutClassification.TimeoutCandidate, normalized.TimeoutClassification);
        Assert.NotEqual(TimeoutClassification.ConfirmedApplicationTimeout, normalized.TimeoutClassification);
    }

    [Fact]
    public void A_derived_event_is_never_reported_as_confirmed_however_long_it_ran()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("An extremely long statement.", durationMs: 600_000, applicationTimeoutReported: true),
            Context(SqlErrorSourceKind.DerivedPerformanceEvidence));

        Assert.False(normalized.Confirmed);
        Assert.Equal(TimeoutClassification.TimeoutCandidate, normalized.TimeoutClassification);
    }

    [Fact]
    public void A_slow_query_below_the_application_timeout_is_not_a_timeout()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Statement completed slowly.", durationMs: 9_000),
            Context(SqlErrorSourceKind.DerivedPerformanceEvidence));

        Assert.Equal(TimeoutClassification.SlowQuery, normalized.TimeoutClassification);
        Assert.False(normalized.IsTimeout);
    }

    [Fact]
    public void Without_a_configured_application_timeout_candidacy_is_not_guessed()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Statement completed slowly.", durationMs: 600_000),
            Context(SqlErrorSourceKind.DerivedPerformanceEvidence, applicationTimeoutMs: null));

        Assert.Equal(TimeoutClassification.SlowQuery, normalized.TimeoutClassification);
        Assert.Contains(
            normalized.ClassificationEvidence,
            line => line.Contains("no application timeout is configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_per_command_timeout_overrides_the_configured_default()
    {
        var raw = Raw("Statement completed slowly.", durationMs: 9_000) with
        {
            CommandTimeoutSeconds = 5,
        };

        var normalized = SqlErrorClassifier.Normalize(
            raw,
            Context(SqlErrorSourceKind.DerivedPerformanceEvidence));

        Assert.Equal(TimeoutClassification.TimeoutCandidate, normalized.TimeoutClassification);
    }

    [Fact]
    public void A_sql_server_lock_timeout_is_not_an_application_timeout()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Lock request time out period exceeded.", errorNumber: SqlErrorNumbers.LockRequestTimeout),
            Context(SqlErrorSourceKind.SqlServer));

        Assert.Equal(SqlErrorCategory.Timeout, normalized.ErrorCategory);
        Assert.Equal(TimeoutClassification.NotApplicable, normalized.TimeoutClassification);
    }

    // ─── Category mapping ───────────────────────────────────────────────────

    [Theory]
    [InlineData(1205, SqlErrorCategory.Deadlock)]
    [InlineData(18456, SqlErrorCategory.LoginFailure)]
    [InlineData(229, SqlErrorCategory.PermissionDenied)]
    [InlineData(245, SqlErrorCategory.ConversionError)]
    [InlineData(8134, SqlErrorCategory.ArithmeticError)]
    [InlineData(547, SqlErrorCategory.ConstraintViolation)]
    [InlineData(208, SqlErrorCategory.InvalidObject)]
    [InlineData(207, SqlErrorCategory.InvalidColumn)]
    [InlineData(3903, SqlErrorCategory.TransactionError)]
    [InlineData(10054, SqlErrorCategory.Connection)]
    public void A_known_error_number_decides_the_category(int errorNumber, SqlErrorCategory expected)
    {
        var normalized = SqlErrorClassifier.Normalize(Raw(errorNumber: errorNumber), Context());

        Assert.Equal(expected, normalized.ErrorCategory);
    }

    [Fact]
    public void A_deadlock_is_flagged_as_one()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Transaction was deadlocked on lock resources.", errorNumber: SqlErrorNumbers.DeadlockVictim),
            Context());

        Assert.True(normalized.IsDeadlock);
        Assert.Equal(Severity.High, normalized.Severity);
    }

    [Fact]
    public void An_unmapped_error_number_is_a_sql_execution_error_not_a_guess()
    {
        var normalized = SqlErrorClassifier.Normalize(Raw(errorNumber: 50_001), Context());

        Assert.Equal(SqlErrorCategory.SqlExecutionError, normalized.ErrorCategory);
    }

    [Fact]
    public void An_error_number_outranks_the_message_text()
    {
        // A login failure message on a deadlock error number: the number is the stronger
        // evidence and has to win, or a source with sloppy text could rewrite the category.
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Login failed for user 'app'.", errorNumber: SqlErrorNumbers.DeadlockVictim),
            Context());

        Assert.Equal(SqlErrorCategory.Deadlock, normalized.ErrorCategory);
    }

    [Theory]
    [InlineData("Timeout expired. The timeout period elapsed prior to completion.", SqlErrorCategory.Timeout)]
    [InlineData("Transaction (Process ID 71) was deadlocked on lock resources.", SqlErrorCategory.Deadlock)]
    [InlineData("Login failed for user 'reporting'.", SqlErrorCategory.LoginFailure)]
    [InlineData("The SELECT permission was denied on object 'Orders'.", SqlErrorCategory.PermissionDenied)]
    [InlineData("A transport-level error has occurred when receiving results.", SqlErrorCategory.Connection)]
    [InlineData("Conversion failed when converting the varchar value to int.", SqlErrorCategory.ConversionError)]
    [InlineData("Invalid object name 'dbo.Missing'.", SqlErrorCategory.InvalidObject)]
    public void Without_an_error_number_the_message_is_matched_deterministically(
        string message,
        SqlErrorCategory expected)
    {
        var normalized = SqlErrorClassifier.Normalize(Raw(message), Context());

        Assert.Equal(expected, normalized.ErrorCategory);
    }

    [Fact]
    public void An_unrecognisable_failure_is_reported_as_unknown()
    {
        var normalized = SqlErrorClassifier.Normalize(Raw("Something went wrong."), Context());

        Assert.Equal(SqlErrorCategory.Unknown, normalized.ErrorCategory);
        Assert.Equal(Severity.Low, normalized.Severity);
        Assert.Equal(TimeoutClassification.NotApplicable, normalized.TimeoutClassification);
    }

    [Fact]
    public void A_fatal_sql_severity_class_outranks_the_category_default()
    {
        var raw = Raw("Invalid column name 'Missing'.", errorNumber: 207) with { SqlErrorClass = 20 };

        var normalized = SqlErrorClassifier.Normalize(raw, Context());

        Assert.Equal(Severity.Critical, normalized.Severity);
    }

    // ─── Sensitive data ─────────────────────────────────────────────────────

    [Fact]
    public void Credentials_in_an_error_message_never_survive_normalization()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Login failed. Server=sql01;UID=sa;PWD=hunter2;Database=Merchandising"),
            Context());

        Assert.DoesNotContain("hunter2", normalized.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PWD=", normalized.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_api_key_in_an_error_message_is_redacted()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw("Call failed, api_key=sk-live-should-not-appear"),
            Context());

        Assert.DoesNotContain("sk-live-should-not-appear", normalized.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_message_is_capped_so_a_payload_cannot_ride_along()
    {
        var normalized = SqlErrorClassifier.Normalize(
            Raw(new string('x', SqlErrorClassifier.MaxMessageLength * 3)),
            Context());

        Assert.EndsWith(SqlErrorClassifier.TruncationSuffix, normalized.Message, StringComparison.Ordinal);
        Assert.True(normalized.Message.Length
            <= SqlErrorClassifier.MaxMessageLength + SqlErrorClassifier.TruncationSuffix.Length);
    }

    [Fact]
    public void A_missing_message_is_stated_rather_than_left_blank()
    {
        var normalized = SqlErrorClassifier.Normalize(Raw(message: null), Context());

        Assert.Equal(SqlErrorClassifier.NoMessageReported, normalized.Message);
    }

    // ─── Identity ───────────────────────────────────────────────────────────

    [Fact]
    public void The_same_observation_always_gets_the_same_id()
    {
        var raw = Raw("Timeout expired.", errorNumber: SqlErrorNumbers.ClientCommandTimeout);

        var first = SqlErrorClassifier.Normalize(raw, Context());
        var second = SqlErrorClassifier.Normalize(raw, Context());

        Assert.Equal(first.Id, second.Id);
        Assert.NotEmpty(first.Id);
    }

    [Fact]
    public void Two_different_failures_do_not_share_an_id()
    {
        var first = SqlErrorClassifier.Normalize(Raw("Timeout expired."), Context());
        var second = SqlErrorClassifier.Normalize(Raw("Login failed for user 'app'."), Context());

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void The_monitored_server_fills_in_when_a_source_does_not_name_one()
    {
        var normalized = SqlErrorClassifier.Normalize(Raw("Timeout expired."), Context());

        Assert.Equal("sql01", normalized.Server);
    }
}
