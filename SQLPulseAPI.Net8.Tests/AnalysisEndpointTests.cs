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
/// Endpoint behaviour for <c>/api/performance/analyze</c>, driven entirely by a fake
/// executor — no SQL Server required.
/// </summary>
public sealed class AnalysisEndpointTests
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

    /// <summary>
    /// Routes rows by query text so the blocking and top-CPU collectors return data
    /// while every other collector returns nothing.
    /// </summary>
    private static FakeSqlQueryExecutor ExecutorWithFindings() => new()
    {
        RowsForQuery = sql =>
        {
            if (sql.Contains("is_head_blocker", StringComparison.Ordinal))
            {
                return
                [
                    FakeSqlQueryExecutor.Row(
                        ("session_id", 51),
                        ("blocking_session_id", 0),
                        ("wait_type", "LCK_M_X"),
                        ("wait_time_ms", 61_000L),
                        ("wait_resource", "KEY: 5:72057594043236352"),
                        ("database_name", "AppDb"),
                        ("sql_text", "UPDATE dbo.Orders SET Status = 1"),
                        ("status", "suspended"),
                        ("login_name", "app"),
                        ("host_name", "web01"),
                        ("program_name", "MyApp"),
                        ("is_head_blocker", 1)),
                    FakeSqlQueryExecutor.Row(
                        ("session_id", 52),
                        ("blocking_session_id", 51),
                        ("wait_type", "LCK_M_S"),
                        ("wait_time_ms", 61_000L),
                        ("wait_resource", "KEY: 5:72057594043236352"),
                        ("database_name", "AppDb"),
                        ("sql_text", "SELECT * FROM dbo.Orders"),
                        ("status", "suspended"),
                        ("login_name", "app"),
                        ("host_name", "web01"),
                        ("program_name", "MyApp"),
                        ("is_head_blocker", 0)),
                ];
            }

            if (sql.Contains("total_worker_time", StringComparison.Ordinal))
            {
                return
                [
                    FakeSqlQueryExecutor.Row(
                        ("sql_text", "SELECT SUM(Total) FROM dbo.Orders"),
                        ("query_hash", "0xAAA"),
                        ("query_plan_hash", "0xPPP"),
                        ("object_name", "dbo.GetOrderTotals"),
                        ("execution_count", 100L),
                        ("total_cpu_time_ms", 4_000_000L),
                        ("avg_cpu_time_ms", 40_000.0),
                        ("total_elapsed_time_ms", 5_000_000L),
                        ("avg_elapsed_time_ms", 50_000.0),
                        ("total_logical_reads", 1_000L),
                        ("avg_logical_reads", 10.0),
                        ("database_name", "AppDb")),
                ];
            }

            return [];
        },
    };

    private static async Task<AnalysisResult> ResultOf(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<AnalysisResult>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options)!;

    private static async Task<string> DetailOf(HttpResponseMessage response)
    {
        var body = JsonSerializer.Deserialize<ErrorResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        return body!.Detail;
    }

    [Fact]
    public async Task Server_scope_returns_an_analysis()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/analyze?scope=server");
        var result = await ResultOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AnalysisScope.Server, result.Scope);
        Assert.Equal(OverallStatus.Healthy, result.OverallStatus);
        Assert.NotEmpty(result.AnalysisId);
    }

    [Fact]
    public async Task Scope_defaults_to_server()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/analyze");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AnalysisScope.Server, (await ResultOf(response)).Scope);
    }

    [Fact]
    public async Task Analysis_json_uses_the_python_property_names()
    {
        using var factory = CreateFactory(ExecutorWithFindings());
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync("/api/performance/analyze?scope=server");

        // The React UI consumes these names verbatim.
        Assert.Contains("\"analysis_id\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"analyzed_at\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"server\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"scope\":\"server\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"database\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"overall_status\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"findings\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"affected_databases\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"total_findings\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"critical_count\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"high_count\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"medium_count\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"low_count\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"current_health\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"optimization_opportunities\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"historical_analysis_status\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"excluded_queries\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finding_json_uses_the_python_property_names()
    {
        using var factory = CreateFactory(ExecutorWithFindings());
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync("/api/performance/analyze?scope=server");

        Assert.Contains("\"finding_id\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"likely_cause\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"validation_steps\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"human_review_required\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"recommended_drilldown\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"query_identity\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"severity_basis\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"category\":\"blocking\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"category\":\"high_cpu\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"critical\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocking_and_cpu_are_reported_in_separate_sections()
    {
        using var factory = CreateFactory(ExecutorWithFindings());
        using var client = factory.CreateClient();

        var result = await ResultOf(await client.GetAsync("/api/performance/analyze?scope=server"));

        // Blocking is happening now; the expensive cached query is only a tuning target.
        Assert.Equal(OverallStatus.Critical, result.OverallStatus);
        Assert.Equal(FindingCategory.Blocking, Assert.Single(result.CurrentHealth!.Findings).Category);
        Assert.Equal(FindingCategory.HighCpu, Assert.Single(result.OptimizationOpportunities).Category);
        Assert.Equal(2, result.TotalFindings);
        Assert.Equal(1, result.CriticalCount);
        Assert.Equal(1, result.HighCount);
    }

    [Fact]
    public async Task Server_scope_findings_offer_a_database_drilldown()
    {
        using var factory = CreateFactory(ExecutorWithFindings());
        using var client = factory.CreateClient();

        var result = await ResultOf(await client.GetAsync("/api/performance/analyze?scope=server"));
        var cpu = Assert.Single(result.OptimizationOpportunities);

        Assert.Equal(AnalysisScope.Database, cpu.RecommendedDrilldown!.Scope);
        Assert.Equal("AppDb", cpu.RecommendedDrilldown.Database);
        Assert.Equal("AppDb", Assert.Single(result.AffectedDatabases).Database);
    }

    [Fact]
    public async Task Database_scope_requires_a_database()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/analyze?scope=database");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PerformanceController.DatabaseRequiredMessage, await DetailOf(response));
    }

    [Fact]
    public async Task An_unknown_database_is_rejected()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows = [FakeSqlQueryExecutor.DatabaseRow("AppDb")],
        };

        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        // Discovery is the allow-list; an unknown name never reaches a query.
        var response = await client.GetAsync("/api/performance/analyze?scope=database&database=Nope");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_invalid_scope_is_rejected()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/analyze?scope=cluster");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(PerformanceController.InvalidScopeMessage, await DetailOf(response));
    }

    [Theory]
    [InlineData("top_n=0")]
    [InlineData("top_n=5000")]
    [InlineData("wait_stats_top_n=0")]
    [InlineData("long_running_threshold_ms=-1")]
    public async Task Out_of_range_limits_are_rejected(string query)
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/performance/analyze?scope=server&{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_collector_still_produces_an_analysis()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnExecute = new InvalidOperationException("permission denied"),
        };

        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/performance/analyze?scope=server");
        var result = await ResultOf(response);

        // A section the login cannot read is not evidence of a healthy server, but it
        // must not fail the whole request either.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task The_root_document_advertises_the_analyze_endpoint()
    {
        using var factory = CreateFactory(new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var raw = await client.GetStringAsync("/");

        Assert.Contains("/api/performance/analyze?scope=server", raw, StringComparison.Ordinal);
    }
}
