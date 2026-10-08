using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Controllers;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.SqlErrors;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Endpoint behaviour for <c>/api/sql-errors</c>, driven by stub sources so no SQL Server
/// or application telemetry feed is required.
/// </summary>
/// <remarks>
/// The interesting cases here are the unhappy ones. A SQL Errors screen is read during an
/// incident, so "nothing came back" must never be indistinguishable from "nothing was
/// wrong", and a mistyped filter must fail loudly rather than quietly return an empty list.
/// </remarks>
public sealed class SqlErrorsEndpointTests
{
    private const string Server = "sql01";
    private const string Database = "Merchandising";
    private const string Window = "start_time=2026-09-17T08:00:00Z&end_time=2026-09-17T12:00:00Z";

    private static SqlPulseApiFactory CreateFactory(
        ISqlErrorService? errorService = null,
        ISqlErrorCorrelationService? correlationService = null,
        IEnumerable<ISqlErrorSource>? sources = null) =>
        new()
        {
            ConfigureTestServices = services =>
            {
                services.AddSingleton<IDatabaseDiscoveryService>(
                    StubDatabaseDiscoveryService.Allowing(Database));

                // The factory blanks SqlServer:Server to keep the suite hermetic, so the
                // monitored instance is restored here to exercise the server check.
                services.Configure<SqlServerOptions>(options => options.Server = Server);

                if (errorService is not null)
                {
                    services.AddSingleton(errorService);
                }

                if (correlationService is not null)
                {
                    services.AddSingleton(correlationService);
                }

                if (sources is not null)
                {
                    services.RemoveAll<ISqlErrorSource>();
                    foreach (var source in sources)
                    {
                        services.AddSingleton(source);
                    }
                }
            },
        };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = JsonSerializer.Deserialize<T>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        Assert.NotNull(body);
        return body!;
    }

    private static async Task<string> DetailOf(HttpResponseMessage response)
    {
        var body = JsonSerializer.Deserialize<ErrorResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        return body!.Detail;
    }

    private static SqlErrorEvent SampleEvent(string id = "err_0123456789abcdef") => new()
    {
        Id = id,
        Timestamp = new DateTimeOffset(2026, 9, 17, 10, 42, 16, TimeSpan.Zero),
        Server = Server,
        Database = Database,
        Application = "CatalogApi",
        StoredProcedure = "dbo.usp_GetMerchandiseCatalog",
        SqlErrorNumber = -2,
        ErrorCategory = SqlErrorCategory.Timeout,
        TimeoutClassification = TimeoutClassification.ConfirmedApplicationTimeout,
        IsTimeout = true,
        Confirmed = true,
        Severity = Severity.High,
        Source = "app_telemetry",
        SourceKind = SqlErrorSourceKind.ApplicationTelemetry,
        DurationMs = 30_012,
        Message = "Execution Timeout Expired.",
    };

    // ─── Happy paths ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_endpoint_returns_the_normalized_events()
    {
        var errorService = new StubSqlErrorService
        {
            ListResponse = new SqlErrorListResponse
            {
                Server = Server,
                Status = SqlErrorQueryStatus.Ok,
                ApplicationTelemetryAvailable = true,
                TotalMatched = 1,
                Returned = 1,
                TopN = 100,
                Errors = [SampleEvent()],
            },
        };

        using var factory = CreateFactory(errorService);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/sql-errors?{Window}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync<SqlErrorListResponse>(response);
        Assert.Equal(SqlErrorQueryStatus.Ok, body.Status);
        Assert.Single(body.Errors);
        Assert.Equal(SqlErrorCategory.Timeout, body.Errors[0].ErrorCategory);
    }

    [Fact]
    public async Task The_list_endpoint_serializes_the_documented_snake_case_contract()
    {
        var errorService = new StubSqlErrorService
        {
            ListResponse = new SqlErrorListResponse { Errors = [SampleEvent()] },
        };

        using var factory = CreateFactory(errorService);
        using var client = factory.CreateClient();

        var payload = await client.GetStringAsync($"/api/sql-errors?{Window}");

        foreach (var key in new[]
        {
            "\"application_telemetry_available\"", "\"total_matched\"", "\"error_category\"",
            "\"timeout_classification\"", "\"sql_error_number\"", "\"source_kind\"",
        })
        {
            Assert.Contains(key, payload, StringComparison.Ordinal);
        }

        Assert.Contains("\"CONFIRMED_APPLICATION_TIMEOUT\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_endpoint_returns_the_aggregate_counts()
    {
        var errorService = new StubSqlErrorService
        {
            SummaryResponse = new SqlErrorSummaryResponse
            {
                Server = Server,
                Status = SqlErrorQueryStatus.Ok,
                TotalErrors = 9,
                TimeoutCount = 4,
                DeadlockCount = 2,
                ConfirmedTimeoutCount = 3,
                TimeoutCandidateCount = 1,
                CategoryCounts = new Dictionary<string, int> { ["TIMEOUT"] = 4, ["DEADLOCK"] = 2 },
            },
        };

        using var factory = CreateFactory(errorService);
        using var client = factory.CreateClient();

        var body = await ReadAsync<SqlErrorSummaryResponse>(
            await client.GetAsync($"/api/sql-errors/summary?{Window}"));

        Assert.Equal(9, body.TotalErrors);
        Assert.Equal(3, body.ConfirmedTimeoutCount);
        Assert.Equal(1, body.TimeoutCandidateCount);
        Assert.Equal(4, body.CategoryCounts["TIMEOUT"]);
    }

    [Fact]
    public async Task The_filters_reach_the_service_unchanged()
    {
        var errorService = new StubSqlErrorService();

        using var factory = CreateFactory(errorService);
        using var client = factory.CreateClient();

        await client.GetAsync(
            $"/api/sql-errors?{Window}&database={Database}&category=DEADLOCK&severity=high"
            + "&application=CatalogApi&stored_procedure=dbo.usp_GetMerchandiseCatalog"
            + "&confirmed_only=true&top_n=25");

        var query = Assert.Single(errorService.Queries);
        Assert.Equal(Database, query.Database);
        Assert.Equal(SqlErrorCategory.Deadlock, query.Category);
        Assert.Equal(Severity.High, query.Severity);
        Assert.Equal("CatalogApi", query.Application);
        Assert.Equal("dbo.usp_GetMerchandiseCatalog", query.StoredProcedure);
        Assert.True(query.ConfirmedOnly);
        Assert.Equal(25, query.TopN);
    }

    // ─── Empty, unavailable and partial are three different answers ─────────

    [Fact]
    public async Task With_no_source_configured_the_default_deployment_says_so()
    {
        // No stubs: the application runs with the source it ships with, and the default
        // configuration leaves Analysis:ApplicationTimeoutMs unset.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var body = await ReadAsync<SqlErrorListResponse>(
            await client.GetAsync($"/api/sql-errors?{Window}"));

        Assert.Equal(SqlErrorQueryStatus.ErrorSourceUnavailable, body.Status);
        Assert.Empty(body.Errors);
        Assert.False(body.ApplicationTelemetryAvailable);
        Assert.NotEmpty(body.Message);
        Assert.NotEmpty(body.Sources);
    }

    [Fact]
    public async Task An_unavailable_source_is_never_reported_as_a_quiet_period()
    {
        using var factory = CreateFactory(sources: [new StubSqlErrorSource
        {
            Result = SqlErrorSourceResult.Failed("The telemetry backend refused the connection."),
        }]);

        using var client = factory.CreateClient();

        var body = await ReadAsync<SqlErrorListResponse>(
            await client.GetAsync($"/api/sql-errors?{Window}"));

        Assert.Equal(SqlErrorQueryStatus.ErrorSourceUnavailable, body.Status);
        Assert.NotEqual(SqlErrorQueryStatus.NoErrorsFound, body.Status);
    }

    [Fact]
    public async Task A_source_that_answered_with_nothing_is_reported_as_a_quiet_period()
    {
        using var factory = CreateFactory(sources: [StubSqlErrorSource.Returning()]);
        using var client = factory.CreateClient();

        var body = await ReadAsync<SqlErrorListResponse>(
            await client.GetAsync($"/api/sql-errors?{Window}"));

        Assert.Equal(SqlErrorQueryStatus.NoErrorsFound, body.Status);
        Assert.True(body.ApplicationTelemetryAvailable);
    }

    [Fact]
    public async Task One_broken_source_among_several_yields_a_partial_answer()
    {
        using var factory = CreateFactory(sources:
        [
            StubSqlErrorSource.Returning(new RawSqlErrorEvent
            {
                Timestamp = new DateTimeOffset(2026, 9, 17, 10, 42, 16, TimeSpan.Zero),
                Database = Database,
                Message = "Transaction (Process ID 71) was deadlocked on lock resources.",
            }),
            new StubSqlErrorSource("broken_source")
            {
                Result = SqlErrorSourceResult.Failed("Endpoint unreachable."),
            },
        ]);

        using var client = factory.CreateClient();

        var body = await ReadAsync<SqlErrorListResponse>(
            await client.GetAsync($"/api/sql-errors?{Window}"));

        Assert.Equal(SqlErrorQueryStatus.PartialErrorData, body.Status);
        Assert.Single(body.Errors);
        Assert.Contains(body.Sources, source => source.Status == SqlErrorSourceStatus.Failed);
    }

    // ─── Rejected input ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_rejected()
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/sql-errors?start_time=2026-09-17T12:00:00Z&end_time=2026-09-17T08:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SqlErrorsController.EndTimeMessage, await DetailOf(response));
    }

    [Fact]
    public async Task A_zero_length_window_is_rejected()
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/sql-errors?start_time=2026-09-17T08:00:00Z&end_time=2026-09-17T08:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task An_out_of_range_page_size_is_rejected(int topN)
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/sql-errors?{Window}&top_n={topN}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("top_n", await DetailOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unrecognized_category_is_rejected_with_the_accepted_values()
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/sql-errors?{Window}&category=SLOWNESS");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var detail = await DetailOf(response);
        Assert.Contains("TIMEOUT", detail, StringComparison.Ordinal);
        Assert.Contains("DEADLOCK", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unrecognized_severity_is_rejected()
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/sql-errors?{Window}&severity=catastrophic");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_database_outside_the_allow_list_is_rejected()
    {
        var errorService = new StubSqlErrorService();

        using var factory = CreateFactory(errorService);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/sql-errors?{Window}&database=NotMine");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(StubDatabaseDiscoveryService.RejectionMessage, await DetailOf(response));
        Assert.Empty(errorService.Queries);
    }

    [Fact]
    public async Task A_server_other_than_the_monitored_instance_is_rejected()
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/sql-errors?{Window}&server=someone-elses-server");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SqlErrorsController.UnknownServerMessage, await DetailOf(response));
    }

    [Fact]
    public async Task The_summary_endpoint_validates_the_same_filters()
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/sql-errors/summary?{Window}&category=SLOWNESS");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ─── Detail and correlation ─────────────────────────────────────────────

    [Fact]
    public async Task A_known_event_is_returned_with_its_correlated_evidence()
    {
        var errorEvent = SampleEvent();
        var errorService = new StubSqlErrorService().With(errorEvent);
        var correlation = new StubSqlErrorCorrelationService
        {
            Correlation = new SqlErrorCorrelation
            {
                CorrelationConfidence = CorrelationConfidence.Medium,
                IdentityMatched = true,
                Assessment = "The error coincided with 1 correlated observation.",
                Caveats = [SqlErrorCorrelation.CausationCaveat],
            },
        };

        using var factory = CreateFactory(errorService, correlation);
        using var client = factory.CreateClient();

        var body = await ReadAsync<SqlErrorDetailResponse>(
            await client.GetAsync($"/api/sql-errors/{errorEvent.Id}"));

        Assert.True(body.Found);
        Assert.Equal(errorEvent.Id, body.Error!.Id);
        Assert.NotNull(body.Correlation);
        Assert.Contains(SqlErrorCorrelation.CausationCaveat, body.Correlation!.Caveats);
        Assert.Same(errorEvent, Assert.Single(correlation.Correlated));
    }

    [Fact]
    public async Task Correlation_can_be_skipped()
    {
        var errorEvent = SampleEvent();
        var correlation = new StubSqlErrorCorrelationService();

        using var factory = CreateFactory(new StubSqlErrorService().With(errorEvent), correlation);
        using var client = factory.CreateClient();

        var body = await ReadAsync<SqlErrorDetailResponse>(
            await client.GetAsync($"/api/sql-errors/{errorEvent.Id}?correlate=false"));

        Assert.True(body.Found);
        Assert.Null(body.Correlation);
        Assert.Empty(correlation.Correlated);
    }

    [Fact]
    public async Task An_expired_id_explains_itself_instead_of_inventing_an_event()
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/sql-errors/err_nolongerheld");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync<SqlErrorDetailResponse>(response);
        Assert.False(body.Found);
        Assert.Null(body.Error);
        Assert.Null(body.Correlation);
        Assert.Equal(SqlErrorDetailResponse.NotFoundMessage, body.Message);
    }

    // ─── Read-only and discoverable ─────────────────────────────────────────

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task The_sql_errors_endpoints_accept_nothing_but_reads(string method)
    {
        using var factory = CreateFactory(new StubSqlErrorService());
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/sql-errors");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task The_sql_errors_endpoints_are_advertised_at_the_root()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var payload = await client.GetStringAsync("/");

        Assert.Contains("/api/sql-errors", payload, StringComparison.Ordinal);
        Assert.Contains("/api/sql-errors/summary", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_failure_message_reaches_the_caller_without_credentials()
    {
        using var factory = CreateFactory(sources: [new StubSqlErrorSource
        {
            Throws = new InvalidOperationException(
                "Login failed for Server=sql01;UID=sqlpulse;PWD=SuperSecret123!;"),
        }]);

        using var client = factory.CreateClient();

        var payload = await client.GetStringAsync($"/api/sql-errors?{Window}");

        Assert.DoesNotContain("SuperSecret123!", payload, StringComparison.Ordinal);
    }
}
