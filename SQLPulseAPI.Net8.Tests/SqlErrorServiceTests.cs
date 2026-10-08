using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.SqlErrors;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// How SQLPulse reports SQL errors, and — more importantly — how it reports not knowing.
/// </summary>
/// <remarks>
/// The behaviour under test is the refusal to present silence as health. An error feed
/// that is missing, misconfigured or broken must never produce the same answer as a period
/// in which genuinely nothing failed.
/// </remarks>
public class SqlErrorServiceTests
{
    private const string Database = "Merchandising";

    private static readonly DateTimeOffset WindowStart = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inside = new(2026, 9, 17, 10, 42, 16, TimeSpan.Zero);

    private static SqlErrorService Build(params ISqlErrorSource[] sources) => new(
        sources,
        new SqlErrorEventCache(Options.Create(new SqlErrorOptions())),
        Options.Create(new SqlServerOptions
        {
            Server = "sql01",
            Username = "sqlpulse",
            Password = "irrelevant-for-tests",
        }),
        Options.Create(new AnalysisOptions { ApplicationTimeoutMs = 30_000 }),
        NullLogger<SqlErrorService>.Instance);

    private static SqlErrorQuery Query(
        string? database = Database,
        SqlErrorCategory? category = null,
        Severity? severity = null,
        string? application = null,
        string? storedProcedure = null,
        bool confirmedOnly = false,
        int topN = 100) =>
        new("sql01", database, WindowStart, WindowEnd, category, application, storedProcedure,
            severity, confirmedOnly, topN);

    private static RawSqlErrorEvent Raw(
        string message = "Timeout expired.",
        DateTimeOffset? timestamp = null,
        string? database = Database,
        string? application = "Merchandising.Api",
        string? storedProcedure = null,
        int? errorNumber = null,
        bool applicationTimeoutReported = false) =>
        new()
        {
            Timestamp = timestamp ?? Inside,
            Database = database,
            Application = application,
            StoredProcedure = storedProcedure,
            Message = message,
            SqlErrorNumber = errorNumber,
            ApplicationTimeoutReported = applicationTimeoutReported,
        };

    // ─── Silence is never health ────────────────────────────────────────────

    [Fact]
    public async Task With_no_source_at_all_the_answer_is_unavailable_not_empty()
    {
        var result = await Build().ListAsync(Query());

        Assert.Equal(SqlErrorQueryStatus.ErrorSourceUnavailable, result.Status);
        Assert.Contains(SqlErrorService.SourceUnavailableMessage, result.Message, StringComparison.Ordinal);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task A_source_that_answered_with_nothing_reports_no_errors_found()
    {
        var result = await Build(new StubSqlErrorSource()).ListAsync(Query());

        Assert.Equal(SqlErrorQueryStatus.NoErrorsFound, result.Status);
        Assert.Contains(SqlErrorService.NoErrorsMessage, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_and_no_errors_found_are_different_answers()
    {
        var unavailable = await Build().ListAsync(Query());
        var empty = await Build(new StubSqlErrorSource()).ListAsync(Query());

        Assert.NotEqual(unavailable.Status, empty.Status);
        Assert.NotEqual(unavailable.Message, empty.Message);
    }

    [Fact]
    public async Task One_silent_source_alongside_a_working_one_is_partial_data()
    {
        var working = StubSqlErrorSource.Returning(Raw());
        var silent = new StubSqlErrorSource("missing_feed")
        {
            Result = SqlErrorSourceResult.NotConfigured("Not wired up yet."),
        };

        var result = await Build(working, silent).ListAsync(Query());

        Assert.Equal(SqlErrorQueryStatus.PartialErrorData, result.Status);
        Assert.Contains(SqlErrorService.PartialDataMessage, result.Message, StringComparison.Ordinal);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Partial_data_also_explains_when_application_telemetry_is_missing()
    {
        var sqlServer = new StubSqlErrorSource("sql_server", SqlErrorSourceKind.SqlServer)
        {
            Result = SqlErrorSourceResult.Available([Raw()]),
        };
        var application = new StubSqlErrorSource("application_feed")
        {
            Result = SqlErrorSourceResult.NotConfigured("Not connected."),
        };

        var result = await Build(sqlServer, application).ListAsync(Query());

        Assert.Equal(SqlErrorQueryStatus.PartialErrorData, result.Status);
        Assert.False(result.ApplicationTelemetryAvailable);
        Assert.Equal(
            $"{SqlErrorService.PartialDataMessage} {SqlErrorService.NoApplicationTelemetryMessage}",
            result.Message);
    }

    [Fact]
    public async Task A_source_that_throws_does_not_lose_the_others()
    {
        var broken = new StubSqlErrorSource("broken_feed")
        {
            Throws = new InvalidOperationException("feed exploded"),
        };

        var result = await Build(broken, StubSqlErrorSource.Returning(Raw())).ListAsync(Query());

        Assert.Equal(SqlErrorQueryStatus.PartialErrorData, result.Status);
        Assert.Single(result.Errors);
        Assert.Equal(
            SqlErrorSourceStatus.Failed,
            result.Sources.Single(source => source.Name == "broken_feed").Status);
    }

    [Fact]
    public async Task A_source_failure_message_cannot_leak_a_connection_string()
    {
        var broken = new StubSqlErrorSource("broken_feed")
        {
            Throws = new InvalidOperationException("Server=sql01;UID=sa;PWD=hunter2"),
        };

        var result = await Build(broken).ListAsync(Query());
        var state = Assert.Single(result.Sources);

        Assert.DoesNotContain("hunter2", state.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_application_telemetry_is_always_stated()
    {
        var derived = new StubSqlErrorSource("derived", SqlErrorSourceKind.DerivedPerformanceEvidence)
        {
            Result = SqlErrorSourceResult.Available([Raw()]),
        };

        var result = await Build(derived).ListAsync(Query());

        Assert.False(result.ApplicationTelemetryAvailable);
        Assert.Contains(SqlErrorService.NoApplicationTelemetryMessage, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_store_style_derived_evidence_never_receives_parameters()
    {
        var derived = new StubSqlErrorSource("query_store", SqlErrorSourceKind.DerivedPerformanceEvidence)
        {
            Result = SqlErrorSourceResult.Available([Raw() with
            {
                QueryHash = "0x123",
                Parameters = new Dictionary<string, string?>(),
            }]),
        };

        var result = await Build(derived).ListAsync(Query());

        Assert.Empty(Assert.Single(result.Errors).Parameters);
    }

    [Fact]
    public async Task Strongly_correlated_cross_source_duplicates_are_returned_once()
    {
        var sqlServer = new StubSqlErrorSource("sql_server", SqlErrorSourceKind.SqlServer)
        {
            Result = SqlErrorSourceResult.Available([Raw() with
            {
                TraceId = "trace-duplicate",
                Message = "SQL Server observed the failure.",
            }]),
        };
        var signoz = new StubSqlErrorSource("signoz", SqlErrorSourceKind.ApplicationTelemetry)
        {
            Result = SqlErrorSourceResult.Available([Raw() with
            {
                TraceId = "trace-duplicate",
                Message = "Application observed the SQL failure.",
                ApplicationTimeoutReported = true,
            }]),
        };

        var result = await Build(sqlServer, signoz).ListAsync(Query());

        var error = Assert.Single(result.Errors);
        Assert.Equal("signoz", error.Source);
        Assert.True(error.Confirmed);
    }

    [Fact]
    public async Task A_not_applicable_derived_source_does_not_downgrade_a_working_sql_server_source()
    {
        var sqlServer = new StubSqlErrorSource("sql_server", SqlErrorSourceKind.SqlServer)
        {
            Result = SqlErrorSourceResult.Available([]),
        };
        var queryStore = new StubSqlErrorSource(
            "sqlpulse_query_store",
            SqlErrorSourceKind.DerivedPerformanceEvidence)
        {
            Result = SqlErrorSourceResult.NotApplicable("A database filter is required."),
        };

        var result = await Build(sqlServer, queryStore).ListAsync(Query(database: null));

        Assert.Equal(SqlErrorQueryStatus.NoErrorsFound, result.Status);
        Assert.Equal(SqlErrorSourceStatus.Available, result.Sources.Single(s => s.Name == "sql_server").Status);
        Assert.Equal(SqlErrorSourceStatus.NotApplicable, result.Sources.Single(s => s.Name == "sqlpulse_query_store").Status);
    }

    [Fact]
    public async Task Connected_application_telemetry_is_reported_as_available()
    {
        var result = await Build(StubSqlErrorSource.Returning(Raw())).ListAsync(Query());

        Assert.True(result.ApplicationTelemetryAvailable);
        Assert.DoesNotContain(
            SqlErrorService.NoApplicationTelemetryMessage,
            result.Message,
            StringComparison.Ordinal);
    }

    // ─── Filtering ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Events_outside_the_window_are_excluded()
    {
        var source = StubSqlErrorSource.Returning(
            Raw(timestamp: WindowStart.AddHours(-1)),
            Raw(timestamp: Inside),
            Raw(timestamp: WindowEnd.AddHours(1)));

        var result = await Build(source).ListAsync(Query());

        Assert.Single(result.Errors);
        Assert.Equal(Inside, result.Errors[0].Timestamp);
    }

    [Fact]
    public async Task The_window_end_is_exclusive_so_adjacent_ranges_cannot_double_count()
    {
        var source = StubSqlErrorSource.Returning(Raw(timestamp: WindowEnd));

        var result = await Build(source).ListAsync(Query());

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task A_database_filter_excludes_other_databases()
    {
        var source = StubSqlErrorSource.Returning(
            Raw(database: Database),
            Raw(database: "Sales"));

        var result = await Build(source).ListAsync(Query(database: Database));

        Assert.Single(result.Errors);
        Assert.Equal(Database, result.Errors[0].Database);
    }

    [Fact]
    public async Task A_category_filter_is_applied_after_classification()
    {
        var source = StubSqlErrorSource.Returning(
            Raw("Timeout expired.", applicationTimeoutReported: true),
            Raw("Transaction was deadlocked on lock resources."));

        var result = await Build(source).ListAsync(Query(category: SqlErrorCategory.Deadlock));

        Assert.Single(result.Errors);
        Assert.Equal(SqlErrorCategory.Deadlock, result.Errors[0].ErrorCategory);
    }

    [Fact]
    public async Task A_severity_filter_is_applied_after_classification()
    {
        var source = StubSqlErrorSource.Returning(
            Raw("Transaction was deadlocked on lock resources."),
            Raw("Something went wrong."));

        var result = await Build(source).ListAsync(Query(severity: Severity.Low));

        Assert.Single(result.Errors);
        Assert.Equal(SqlErrorCategory.Unknown, result.Errors[0].ErrorCategory);
    }

    [Fact]
    public async Task An_application_filter_is_case_insensitive()
    {
        var source = StubSqlErrorSource.Returning(
            Raw(application: "Merchandising.Api"),
            Raw(application: "Sales.Worker"));

        var result = await Build(source).ListAsync(Query(application: "merchandising.api"));

        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task A_stored_procedure_filter_ignores_the_schema_prefix()
    {
        var source = StubSqlErrorSource.Returning(
            Raw(storedProcedure: "dbo.usp_GetOrders"),
            Raw(storedProcedure: "dbo.usp_GetCustomers"));

        var result = await Build(source).ListAsync(Query(storedProcedure: "usp_GetOrders"));

        Assert.Single(result.Errors);
        Assert.Equal("dbo.usp_GetOrders", result.Errors[0].StoredProcedure);
    }

    [Fact]
    public async Task A_stored_procedure_filter_does_not_match_on_a_prefix()
    {
        var source = StubSqlErrorSource.Returning(Raw(storedProcedure: "dbo.usp_GetOrders"));

        var result = await Build(source).ListAsync(Query(storedProcedure: "usp_Get"));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Confirmed_only_drops_everything_SQLPulse_merely_inferred()
    {
        var telemetry = StubSqlErrorSource.Returning(Raw("Timeout expired.", applicationTimeoutReported: true));
        var derived = new StubSqlErrorSource("derived", SqlErrorSourceKind.DerivedPerformanceEvidence)
        {
            Result = SqlErrorSourceResult.Available([Raw("A long statement.")]),
        };

        var result = await Build(telemetry, derived).ListAsync(Query(confirmedOnly: true));

        Assert.Single(result.Errors);
        Assert.True(result.Errors[0].Confirmed);
    }

    [Fact]
    public async Task Top_n_limits_the_page_without_hiding_how_many_matched()
    {
        var source = StubSqlErrorSource.Returning(
            Raw(timestamp: Inside),
            Raw(timestamp: Inside.AddMinutes(1)),
            Raw(timestamp: Inside.AddMinutes(2)));

        var result = await Build(source).ListAsync(Query(topN: 2));

        Assert.Equal(3, result.TotalMatched);
        Assert.Equal(2, result.Returned);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task Errors_are_returned_newest_first()
    {
        var source = StubSqlErrorSource.Returning(
            Raw(timestamp: Inside),
            Raw(timestamp: Inside.AddMinutes(30)));

        var result = await Build(source).ListAsync(Query());

        Assert.Equal(Inside.AddMinutes(30), result.Errors[0].Timestamp);
    }

    // ─── Summary ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_summary_counts_each_category_and_balances_to_the_total()
    {
        var source = StubSqlErrorSource.Returning(
            Raw("Timeout expired.", applicationTimeoutReported: true),
            Raw("Transaction was deadlocked on lock resources."),
            Raw("A transport-level error has occurred."),
            Raw("Login failed for user 'app'."),
            Raw("Conversion failed when converting the varchar value to int."),
            Raw("Something went wrong."));

        var summary = await Build(source).SummarizeAsync(Query());

        Assert.Equal(6, summary.TotalErrors);
        Assert.Equal(1, summary.TimeoutCount);
        Assert.Equal(1, summary.DeadlockCount);
        Assert.Equal(1, summary.ConnectionErrorCount);
        Assert.Equal(1, summary.PermissionErrorCount);
        Assert.Equal(2, summary.OtherErrorCount);

        Assert.Equal(
            summary.TotalErrors,
            summary.TimeoutCount + summary.DeadlockCount + summary.ConnectionErrorCount
                + summary.PermissionErrorCount + summary.OtherErrorCount);
    }

    [Fact]
    public async Task The_summary_separates_confirmed_timeouts_from_candidates()
    {
        var telemetry = StubSqlErrorSource.Returning(
            Raw("Timeout expired.", applicationTimeoutReported: true));

        var derived = new StubSqlErrorSource("derived", SqlErrorSourceKind.DerivedPerformanceEvidence)
        {
            Result = SqlErrorSourceResult.Available(
            [
                Raw("A long statement.") with { DurationMs = 45_000 },
            ]),
        };

        var summary = await Build(telemetry, derived).SummarizeAsync(Query());

        Assert.Equal(1, summary.ConfirmedTimeoutCount);
        Assert.Equal(1, summary.TimeoutCandidateCount);
    }

    [Fact]
    public async Task The_summary_lists_the_affected_databases_and_applications()
    {
        var source = StubSqlErrorSource.Returning(
            Raw(database: Database, application: "Merchandising.Api"),
            Raw(database: "Sales", application: "Sales.Worker"),
            Raw(database: "Sales", application: "Sales.Worker"));

        var summary = await Build(source).SummarizeAsync(Query(database: null));

        Assert.Equal(["Merchandising", "Sales"], summary.AffectedDatabases);
        Assert.Equal(["Merchandising.Api", "Sales.Worker"], summary.AffectedApplications);
    }

    [Fact]
    public async Task A_summary_of_nothing_still_reports_why_nothing_was_seen()
    {
        var summary = await Build().SummarizeAsync(Query());

        Assert.Equal(0, summary.TotalErrors);
        Assert.Equal(SqlErrorQueryStatus.ErrorSourceUnavailable, summary.Status);
    }

    // ─── Detail lookup ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_listed_error_can_be_resolved_by_its_id()
    {
        var service = Build(StubSqlErrorSource.Returning(Raw()));
        var listed = await service.ListAsync(Query());

        var found = service.Find(listed.Errors[0].Id);

        Assert.NotNull(found);
        Assert.Equal(listed.Errors[0].Id, found.Id);
    }

    [Fact]
    public void An_unknown_id_resolves_to_nothing_rather_than_a_reconstruction()
    {
        Assert.Null(Build().Find("err_does_not_exist"));
    }

    [Fact]
    public async Task An_event_only_the_summary_counted_can_still_be_opened()
    {
        var service = Build(StubSqlErrorSource.Returning(Raw()));
        var summary = await service.SummarizeAsync(Query());
        var listed = await service.ListAsync(Query());

        Assert.Equal(1, summary.TotalErrors);
        Assert.NotNull(service.Find(listed.Errors[0].Id));
    }

    [Fact]
    public async Task Every_source_is_reported_whether_it_answered_or_not()
    {
        var working = StubSqlErrorSource.Returning(Raw());
        var silent = new StubSqlErrorSource("missing_feed")
        {
            Result = SqlErrorSourceResult.NotApplicable("Needs a database filter."),
        };

        var result = await Build(working, silent).ListAsync(Query());

        Assert.Equal(2, result.Sources.Count);
        Assert.Equal(1, result.Sources.Single(s => s.Name == "stub_source").EventCount);
        Assert.Equal(
            "Needs a database filter.",
            result.Sources.Single(s => s.Name == "missing_feed").Message);
    }
}
