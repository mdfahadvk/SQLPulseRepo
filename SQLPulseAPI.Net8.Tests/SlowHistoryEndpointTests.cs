using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SQLPulseAPI.Net8.Controllers;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Endpoint behaviour for <c>/api/performance/slow-history</c>, driven entirely by a
/// fake executor — no SQL Server and no Query Store required.
/// </summary>
public sealed class SlowHistoryEndpointTests
{
    private const string Database = "AppDb";
    private const string Window = "start_time=2026-08-24T09:00:00Z&end_time=2026-08-24T11:00:00Z";

    private static SqlPulseApiFactory CreateFactory(FakeSqlQueryExecutor executor) =>
        new()
        {
            ConfigureTestServices = services =>
            {
                services.AddSingleton<ISqlQueryExecutor>(executor);
                services.AddSingleton<IDatabaseDiscoveryService, DatabaseDiscoveryService>();
            },
        };

    /// <summary>An executor that discovers the database and answers Query Store queries.</summary>
    private static FakeSqlQueryExecutor Executor(string actualState = "READ_WRITE", params SqlRow[] slowRows) =>
        new()
        {
            RowsForQuery = sql =>
            {
                if (sql.Contains("sys.databases", StringComparison.Ordinal))
                {
                    return [FakeSqlQueryExecutor.DatabaseRow(Database)];
                }

                if (sql.Contains("database_query_store_options", StringComparison.Ordinal))
                {
                    return
                    [
                        FakeSqlQueryExecutor.Row(
                            ("actual_state_desc", actualState),
                            ("desired_state_desc", actualState),
                            ("readonly_reason", 0),
                            ("current_storage_size_mb", 12L),
                            ("max_storage_size_mb", 1024L)),
                    ];
                }

                return slowRows;
            },
        };

    private static SqlRow SlowRow(
        string queryHash = "0xABCD",
        long planId = 7,
        double maxDurationMs = 9749,
        string? procedureName = "dbo.uspGetOrders") => FakeSqlQueryExecutor.Row(
        ("query_id", 42L),
        ("query_sql_text", "SELECT * FROM Orders"),
        ("query_hash", queryHash),
        ("object_id", 991L),
        ("procedure_name", procedureName),
        ("plan_id", planId),
        ("execution_count", 3L),
        ("avg_duration_ms", 6100.0),
        ("max_duration_ms", maxDurationMs),
        ("min_duration_ms", 5200.0),
        ("avg_cpu_time_ms", 1200.0),
        ("max_cpu_time_ms", 2400.0),
        ("avg_logical_reads", 5000.0),
        ("max_logical_reads", 9000.0),
        ("avg_physical_reads", 10.0),
        ("first_execution_time", new DateTime(2026, 8, 24, 9, 5, 0, DateTimeKind.Utc)),
        ("last_execution_time", new DateTime(2026, 8, 24, 10, 55, 0, DateTimeKind.Utc)),
        ("interval_start_time", new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc)));

    private static async Task<string> DetailOf(HttpResponseMessage response)
    {
        var body = JsonSerializer.Deserialize<ErrorResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        return body!.Detail;
    }

    private static async Task<SlowHistoryResponse> BodyOf(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<SlowHistoryResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options)!;

    // --- Validation ----------------------------------------------------------

    [Fact]
    public async Task A_missing_database_is_rejected()
    {
        using var factory = CreateFactory(Executor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/performance/slow-history?{Window}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(DatabaseDiscoveryService.DatabaseRequiredMessage, await DetailOf(response));
    }

    [Fact]
    public async Task A_malformed_database_name_never_reaches_sql_server()
    {
        var executor = Executor();
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/performance/slow-history?database=App%5D%3B%20DROP&{Window}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(DatabaseDiscoveryService.InvalidFormatMessage, await DetailOf(response));
        Assert.Empty(executor.ExecutedQueries);
    }

    [Fact]
    public async Task An_unknown_database_is_rejected_by_the_discovery_allow_list()
    {
        using var factory = CreateFactory(Executor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/performance/slow-history?database=Nope&{Window}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is not accessible or does not exist", await DetailOf(response), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("start_time=2026-08-24T11:00:00Z&end_time=2026-08-24T09:00:00Z")]
    [InlineData("start_time=2026-08-24T09:00:00Z&end_time=2026-08-24T09:00:00Z")]
    public async Task An_end_time_that_is_not_after_the_start_is_rejected(string window)
    {
        using var factory = CreateFactory(Executor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/performance/slow-history?database={Database}&{window}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PerformanceController.EndTimeMessage, await DetailOf(response));
    }

    [Theory]
    [InlineData("threshold_ms=0", "threshold_ms must be 1 or greater")]
    [InlineData("application_timeout_ms=0", "application_timeout_ms must be 1 or greater")]
    [InlineData("top_n=0", "top_n must be between 1 and 100")]
    [InlineData("top_n=101", "top_n must be between 1 and 100")]
    public async Task Out_of_range_parameters_are_rejected(string parameter, string expected)
    {
        using var factory = CreateFactory(Executor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}&{parameter}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expected, await DetailOf(response));
    }

    [Fact]
    public async Task An_unknown_workload_type_lists_the_accepted_values()
    {
        using var factory = CreateFactory(Executor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}&workload_type=BATCH");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "workload_type must be one of: APPLICATION_OR_INTERACTIVE, BACKGROUND_OR_SCHEDULED, UNKNOWN",
            await DetailOf(response));
    }

    // --- Degradation ---------------------------------------------------------

    [Fact]
    public async Task A_disabled_query_store_returns_200_with_a_status_and_message()
    {
        // One unavailable feature must never fail the request.
        using var factory = CreateFactory(Executor("OFF"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/performance/slow-history?database={Database}&{Window}");
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(QueryStoreStatus.Disabled, body.QueryStoreStatus);
        Assert.Equal($"Query Store is disabled (OFF) for database '{Database}'.", body.Message);
        Assert.Empty(body.Candidates);
        Assert.Equal(0, body.TotalCandidates);
        Assert.Equal(0, body.TimeoutCandidateCount);
    }

    [Fact]
    public async Task A_permission_denied_query_store_returns_200_with_a_status()
    {
        var executor = new FakeSqlQueryExecutor
        {
            RowsForQuery = sql => sql.Contains("sys.databases", StringComparison.Ordinal)
                ? [FakeSqlQueryExecutor.DatabaseRow(Database)]
                : throw new InvalidOperationException("VIEW DATABASE STATE permission was denied."),
        };

        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/performance/slow-history?database={Database}&{Window}");
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(QueryStoreStatus.PermissionDenied, body.QueryStoreStatus);
        Assert.Equal(
            $"Permission denied when checking Query Store for database '{Database}'.",
            body.Message);
    }

    // --- Results -------------------------------------------------------------

    [Fact]
    public async Task Candidates_are_returned_with_thresholds_kept_separate()
    {
        using var factory = CreateFactory(Executor("READ_WRITE", SlowRow()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}"
            + "&threshold_ms=5000&application_timeout_ms=30000");

        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(QueryStoreStatus.Available, body.QueryStoreStatus);
        Assert.Equal(Database, body.Database);
        Assert.Equal(5000, body.ThresholdMs);
        Assert.Equal(5000, body.SlowThresholdMs);
        Assert.Equal(30_000, body.ApplicationTimeoutMs);
        Assert.True(body.TimeoutClassificationAvailable);
        Assert.Equal(1, body.TotalCandidates);
        Assert.Equal(1, body.SlowQueryCandidateCount);
        Assert.Equal(0, body.TimeoutCandidateCount);

        var candidate = Assert.Single(body.Candidates);
        Assert.Equal(CandidateClassification.SlowQueryCandidate, candidate.Classification);
        Assert.Equal(1, candidate.Rank);
    }

    [Fact]
    public async Task Without_a_configured_timeout_classification_is_reported_unavailable()
    {
        using var factory = CreateFactory(Executor("READ_WRITE", SlowRow()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}&threshold_ms=5000");

        var body = await BodyOf(response);

        Assert.Null(body.ApplicationTimeoutMs);
        Assert.False(body.TimeoutClassificationAvailable);
        Assert.Equal(0, body.TimeoutCandidateCount);
    }

    [Fact]
    public async Task A_read_only_query_store_still_answers_historical_questions()
    {
        using var factory = CreateFactory(Executor("READ_ONLY", SlowRow()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}&threshold_ms=5000");

        var body = await BodyOf(response);

        Assert.Equal(QueryStoreStatus.AvailableReadOnly, body.QueryStoreStatus);
        Assert.Null(body.Message);
        Assert.Single(body.Candidates);
    }

    [Fact]
    public async Task Grouping_can_be_disabled_per_request()
    {
        using var factory = CreateFactory(Executor("READ_WRITE", SlowRow(), SlowRow()));
        using var client = factory.CreateClient();

        var grouped = await BodyOf(await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}&threshold_ms=5000"));

        var ungrouped = await BodyOf(await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}"
            + "&threshold_ms=5000&group_results=false"));

        Assert.Equal(1, grouped.TotalCandidates);
        Assert.Equal(2, ungrouped.TotalCandidates);
    }

    [Fact]
    public async Task Default_threshold_matches_the_python_contract()
    {
        var executor = Executor("READ_WRITE", SlowRow());
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var body = await BodyOf(await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}"));

        Assert.Equal(30_000, body.ThresholdMs);
        Assert.Equal(20, body.TopN);

        // The 9,749 ms row is below the default 30,000 ms monitoring threshold.
        Assert.Empty(body.Candidates);
    }

    // --- JSON contract -------------------------------------------------------

    [Fact]
    public async Task Slow_history_json_uses_the_python_property_names()
    {
        using var factory = CreateFactory(Executor("READ_WRITE", SlowRow()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/performance/slow-history?database={Database}&{Window}"
            + "&threshold_ms=5000&application_timeout_ms=30000");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("AVAILABLE", root.GetProperty("query_store_status").GetString());
        Assert.Equal(5000, root.GetProperty("slow_threshold_ms").GetInt32());
        Assert.Equal(30_000, root.GetProperty("application_timeout_ms").GetInt32());
        Assert.True(root.GetProperty("timeout_classification_available").GetBoolean());
        Assert.Equal(1, root.GetProperty("slow_query_candidate_count").GetInt32());

        var candidate = root.GetProperty("candidates")[0];
        Assert.Equal("SLOW_QUERY_CANDIDATE", candidate.GetProperty("classification").GetString());
        Assert.Equal("slow_query_candidate", candidate.GetProperty("label").GetString());
        Assert.False(candidate.GetProperty("timeout_candidate").GetBoolean());
        Assert.Equal("UNKNOWN", candidate.GetProperty("workload_type").GetString());
        Assert.Equal("dbo.uspGetOrders", candidate.GetProperty("procedure_name").GetString());
        Assert.Equal(9749, candidate.GetProperty("max_duration_ms").GetDouble());
        Assert.Equal(1, candidate.GetProperty("rank").GetInt32());

        foreach (var name in new[]
        {
            "query_id", "query_hash", "object_id", "plan_id", "sql_text_preview",
            "execution_count", "total_execution_count", "intervals_observed",
            "intervals_exceeding_threshold", "avg_duration_ms", "min_duration_ms",
            "avg_cpu_ms", "max_cpu_ms", "avg_logical_reads", "max_logical_reads",
            "first_execution_time", "last_execution_time", "baseline_comparison",
            "confidence", "severity", "classification_evidence", "classification_reason",
        })
        {
            Assert.True(candidate.TryGetProperty(name, out _), $"missing property: {name}");
        }
    }

    [Fact]
    public async Task The_root_endpoint_map_advertises_slow_history()
    {
        using var factory = CreateFactory(Executor());
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/"));

        Assert.Equal(
            "/api/performance/slow-history?database=...&threshold_ms=30000",
            document.RootElement.GetProperty("endpoints").GetProperty("slow_history").GetString());
    }
}
