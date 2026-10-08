using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests.Compatibility;

/// <summary>
/// Golden-master tests against the running .NET endpoints.
/// </summary>
/// <remarks>
/// <see cref="PythonContractTests"/> proves the models can reproduce the Python contract.
/// These tests prove the endpoints actually serve it — covering the controller, the
/// serializer configuration and the route table, none of which a model round-trip
/// exercises. Everything is driven by fakes; no SQL Server or LLM provider is contacted.
/// </remarks>
public sealed class PythonEndpointContractTests
{
    private const string Database = "AppDb";

    private static SqlPulseApiFactory CreateFactory(FakeSqlQueryExecutor executor) =>
        new()
        {
            ConfigureTestServices = services =>
            {
                services.AddSingleton<ISqlQueryExecutor>(executor);
                services.AddSingleton<IDatabaseDiscoveryService, DatabaseDiscoveryService>();
            },
        };

    // --- Routes exist and use the Python verbs --------------------------------

    [Theory]
    [InlineData("/health")]
    [InlineData("/database/connection-test")]
    [InlineData("/api/databases")]
    [InlineData("/api/performance/snapshot")]
    [InlineData("/api/performance/analyze")]
    [InlineData("/api/agent/investigations")]
    public async Task Python_routes_are_served(string route)
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(route);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Investigate_is_a_post_only_route()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var get = await client.GetAsync("/api/agent/investigate");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    [Fact]
    public async Task Snapshot_is_a_get_only_route()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var post = await client.PostAsync("/api/performance/snapshot", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    // --- Served payloads match the Python shape -------------------------------

    [Fact]
    public async Task Health_serves_the_python_shape()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        JsonShape.AssertMatches(
            PythonResponseFixtures.Health,
            await client.GetStringAsync("/health"));
    }

    [Fact]
    public async Task Connection_test_serves_the_python_shape()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        JsonShape.AssertMatches(
            PythonResponseFixtures.ConnectionTest,
            await client.GetStringAsync("/database/connection-test"));
    }

    [Fact]
    public async Task Database_list_serves_the_python_shape()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows = [FakeSqlQueryExecutor.DatabaseRow(Database)],
        };

        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        JsonShape.AssertMatches(
            PythonResponseFixtures.DatabaseList,
            await client.GetStringAsync("/api/databases"));
    }

    [Fact]
    public async Task Snapshot_serves_the_python_shape()
    {
        // The fake returns no rows, so collections come back empty; the shape comparison
        // skips empty arrays and still verifies every enclosing object and scalar.
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        JsonShape.AssertMatches(
            PythonResponseFixtures.PerformanceSnapshot,
            await client.GetStringAsync("/api/performance/snapshot?scope=server"),
            "wait_statistics.delta",
            "wait_statistics.sqlserver_start_time");
    }

    [Fact]
    public async Task Analyze_serves_the_python_shape()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        JsonShape.AssertMatches(
            PythonResponseFixtures.AnalysisResult,
            await client.GetStringAsync("/api/performance/analyze?scope=server"));
    }

    [Fact]
    public async Task Query_history_serves_the_python_shape()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        factory.HistoryRepository.Seed(new SQLPulseAPI.Net8.Models.History.QueryMetricRecord
        {
            SnapshotId = Guid.NewGuid().ToString(),
            CollectedAt = DateTimeOffset.UtcNow,
            Server = "sql01",
            Database = Database,
            QueryHash = "0xAB12CD34",
            SqlTextPreview = "SELECT 1",
            ExecutionCount = 10,
            AvgCpuTimeMs = 100,
        });

        using var client = factory.CreateClient();

        JsonShape.AssertMatches(
            PythonResponseFixtures.QueryHistory,
            await client.GetStringAsync($"/api/history/query/0xAB12CD34?database={Database}"));
    }

    [Fact]
    public async Task Query_baseline_serves_the_python_shape()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        // With no samples the baseline reports INSUFFICIENT_DATA and an empty metrics map,
        // which is the shape Python produces in the same situation.
        JsonShape.AssertMatches(
            PythonResponseFixtures.QueryBaselineInsufficient,
            await client.GetStringAsync($"/api/history/query/0xAB12CD34/baseline?database={Database}"));
    }

    [Fact]
    public async Task Investigation_audit_list_serves_the_python_shape()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        JsonShape.AssertMatches(
            PythonResponseFixtures.InvestigationAuditList,
            await client.GetStringAsync("/api/agent/investigations"));
    }

    [Fact]
    public async Task Investigation_audit_detail_serves_the_python_shape()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        JsonShape.AssertMatches(
            PythonResponseFixtures.InvestigationAuditNotFound,
            await client.GetStringAsync("/api/agent/investigations/unknown-id"));
    }

    // --- Error envelope -------------------------------------------------------

    [Fact]
    public async Task Errors_use_the_python_detail_envelope()
    {
        // The React client reads error.response.data.detail and shows nothing useful
        // without it, so the envelope matters as much as the success payloads.
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/snapshot?scope=database");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        JsonShape.AssertMatches(
            PythonResponseFixtures.Error,
            await response.Content.ReadAsStringAsync());
    }

    // --- Defaults match Python ------------------------------------------------

    [Fact]
    public async Task Snapshot_applies_the_python_query_parameter_defaults()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/performance/snapshot");
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("server", root.GetProperty("scope").GetString());

        Assert.Equal(
            5000,
            root.GetProperty("long_running_queries").GetProperty("threshold_ms").GetInt32());

        Assert.Equal(20, root.GetProperty("top_cpu_queries").GetProperty("top_n").GetInt32());
        Assert.Equal(20, root.GetProperty("top_io_queries").GetProperty("top_n").GetInt32());
        Assert.Equal(25, root.GetProperty("wait_statistics").GetProperty("top_n").GetInt32());
    }

    [Fact]
    public async Task Query_baseline_defaults_to_the_seven_day_window()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync($"/api/history/query/0xABC/baseline?database={Database}");
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal("7d", document.RootElement.GetProperty("window").GetString());
    }

    // --- Timestamps -----------------------------------------------------------

    [Fact]
    public async Task Timestamps_carry_an_explicit_offset()
    {
        // The browser parses an offset-less ISO string as local time and an offset-bearing
        // one as the instant it names. Emitting the offset is what keeps a reading from
        // shifting with the viewer's timezone.
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/performance/snapshot?scope=server");
        using var document = System.Text.Json.JsonDocument.Parse(json);

        var collectedAt = document.RootElement.GetProperty("collected_at").GetString();

        Assert.NotNull(collectedAt);
        Assert.True(
            collectedAt.EndsWith("Z", StringComparison.Ordinal)
            || collectedAt.Contains('+', StringComparison.Ordinal)
            || collectedAt.LastIndexOf('-') > 9,
            $"collected_at '{collectedAt}' carries no UTC offset");

        Assert.True(DateTimeOffset.TryParse(collectedAt, out _));
    }
}
