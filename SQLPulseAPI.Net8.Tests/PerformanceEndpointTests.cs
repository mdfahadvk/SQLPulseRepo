using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SQLPulseAPI.Net8.Controllers;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Endpoint behaviour for <c>/api/performance/snapshot</c>, driven entirely by a fake
/// executor — no SQL Server required.
/// </summary>
public sealed class PerformanceEndpointTests
{
    private static SqlPulseApiFactory CreateFactory(FakeSqlQueryExecutor executor) =>
        new()
        {
            ConfigureTestServices = services =>
            {
                services.AddSingleton<ISqlQueryExecutor>(executor);
                services.AddSingleton<IDatabaseDiscoveryService, DatabaseDiscoveryService>();
            },
        };

    private static async Task<string> DetailOf(HttpResponseMessage response)
    {
        var body = JsonSerializer.Deserialize<ErrorResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        return body!.Detail;
    }

    [Fact]
    public async Task Server_scope_returns_a_snapshot()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/snapshot?scope=server");
        var snapshot = JsonSerializer.Deserialize<PerformanceSnapshot>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AnalysisScope.Server, snapshot!.Scope);
        Assert.NotNull(snapshot.ActiveRequests);
        Assert.NotNull(snapshot.WaitStatistics);
    }

    [Fact]
    public async Task Scope_defaults_to_server()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/snapshot");
        var snapshot = JsonSerializer.Deserialize<PerformanceSnapshot>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AnalysisScope.Server, snapshot!.Scope);
    }

    [Fact]
    public async Task Snapshot_json_uses_the_python_property_names()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync("/api/performance/snapshot?scope=server");

        // The React UI consumes these names verbatim.
        Assert.Contains("\"snapshot_id\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"server_host\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"active_requests\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"long_running_queries\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"blocking_sessions\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"top_cpu_queries\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"top_io_queries\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"wait_statistics\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"collector_name\":\"active_requests\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"permission_issue\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"scope\":\"server\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"applicability\":\"server_level\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_scope_requires_a_database_name()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/snapshot?scope=database");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PerformanceController.DatabaseRequiredMessage, await DetailOf(response));
    }

    [Fact]
    public async Task Database_scope_rejects_a_database_that_was_not_discovered()
    {
        // Discovery is the allow-list; an unknown name must never reach a query.
        var executor = new FakeSqlQueryExecutor { Rows = [FakeSqlQueryExecutor.DatabaseRow("OrdersDb")] };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/snapshot?scope=database&database=PayrollDb");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PayrollDb", await DetailOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_scope_rejects_a_malformed_database_name()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [FakeSqlQueryExecutor.DatabaseRow("OrdersDb")] };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/snapshot?scope=database&database=Orders%3B+DROP");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(DatabaseDiscoveryService.InvalidFormatMessage, await DetailOf(response));
    }

    [Fact]
    public async Task Database_scope_collects_against_the_discovered_database()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [FakeSqlQueryExecutor.DatabaseRow("OrdersDb")] };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/snapshot?scope=database&database=OrdersDb");
        var snapshot = JsonSerializer.Deserialize<PerformanceSnapshot>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AnalysisScope.Database, snapshot!.Scope);
        Assert.Equal("OrdersDb", snapshot.Database);
        Assert.Equal(
            WaitStatisticsApplicability.NotApplicableDatabaseScope,
            snapshot.WaitStatistics!.Applicability);
    }

    [Theory]
    [InlineData("scope=instance")]
    [InlineData("scope=SERVER")]
    [InlineData("scope=all")]
    public async Task An_unsupported_scope_is_rejected(string query)
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/performance/snapshot?{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(PerformanceController.InvalidScopeMessage, await DetailOf(response));
    }

    [Theory]
    [InlineData("top_n=0")]
    [InlineData("top_n=100000")]
    [InlineData("wait_stats_top_n=0")]
    [InlineData("long_running_threshold_ms=-1")]
    public async Task Out_of_range_limits_are_rejected(string query)
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/performance/snapshot?scope=server&{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_failing_collector_still_returns_200_with_a_sanitized_message()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnExecute = new TimeoutException("Timeout expired connecting to MSI-STGERPDBL as sqlpulse_svc."),
        };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/snapshot?scope=server");
        var raw = await response.Content.ReadAsStringAsync();
        var snapshot = JsonSerializer.Deserialize<PerformanceSnapshot>(raw, SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(snapshot!.ActiveRequests!.Metadata.Success);
        Assert.DoesNotContain("MSI-STGERPDBL", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlpulse_svc", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Root_endpoint_advertises_the_snapshot_endpoint()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var body = JsonSerializer.Deserialize<RootResponse>(
            await client.GetStringAsync("/"), SqlPulseJson.Options);

        Assert.Equal("/api/performance/snapshot?scope=server", body!.Endpoints["performance_snapshot"]);
    }
}
