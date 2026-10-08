using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SQLPulseAPI.Net8.Controllers;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Endpoint behaviour for <c>/database/connection-test</c> and <c>/api/databases</c>,
/// driven entirely by a fake executor — no SQL Server required.
/// </summary>
public sealed class DatabaseEndpointTests
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

    [Fact]
    public async Task Connection_test_reports_success()
    {
        var executor = new FakeSqlQueryExecutor { ConnectionOutcome = SqlOperationOutcome.Success() };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/database/connection-test");
        var body = JsonSerializer.Deserialize<ConnectionTestResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body!.Connected);
        Assert.Equal(DatabaseController.ConnectedMessage, body.Message);
    }

    [Fact]
    public async Task Connection_test_reports_failure_without_leaking_details()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ConnectionOutcome = new SqlOperationOutcome(
                SqlOperationStatus.Failed,
                "Login failed for user 'sqlpulse_reader' with password hunter2"),
        };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/database/connection-test");
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ConnectionTestResponse>(raw, SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body!.Connected);
        Assert.Equal(DatabaseController.NotConnectedMessage, body.Message);
        Assert.DoesNotContain("hunter2", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlpulse_reader", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connection_test_returns_200_even_when_the_probe_throws()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnTestConnection = new InvalidOperationException("server=sql01;password=hunter2"),
        };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/database/connection-test");
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ConnectionTestResponse>(raw, SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body!.Connected);
        Assert.Contains("unexpected error", body.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connection_test_payload_matches_the_python_contract()
    {
        var executor = new FakeSqlQueryExecutor();
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/database/connection-test"));

        Assert.Equal(
            new[] { "connected", "message" },
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Databases_endpoint_returns_accessible_databases()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ServerHost = "sql01",
            Rows =
            [
                FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"),
                FakeSqlQueryExecutor.DatabaseRow("Sales_Reporting"),
            ],
        };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var body = JsonSerializer.Deserialize<DatabaseListResponse>(
            await client.GetStringAsync("/api/databases"), SqlPulseJson.Options);

        Assert.Equal("sql01", body!.Server);
        Assert.Equal(["AdventureWorks", "Sales_Reporting"], body.Databases.Select(d => d.Name));
        Assert.All(body.Databases, db => Assert.Equal("ONLINE", db.Status));
    }

    [Fact]
    public async Task Databases_endpoint_excludes_system_and_offline_databases()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows =
            [
                FakeSqlQueryExecutor.DatabaseRow("master"),
                FakeSqlQueryExecutor.DatabaseRow("tempdb"),
                FakeSqlQueryExecutor.DatabaseRow("model"),
                FakeSqlQueryExecutor.DatabaseRow("msdb"),
                FakeSqlQueryExecutor.DatabaseRow("ArchivedDb", "OFFLINE"),
                FakeSqlQueryExecutor.DatabaseRow("AdventureWorks"),
            ],
        };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var body = JsonSerializer.Deserialize<DatabaseListResponse>(
            await client.GetStringAsync("/api/databases"), SqlPulseJson.Options);

        Assert.Equal(["AdventureWorks"], body!.Databases.Select(d => d.Name));
    }

    [Fact]
    public async Task Databases_endpoint_payload_matches_the_python_contract()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [FakeSqlQueryExecutor.DatabaseRow("AdventureWorks")] };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/databases"));
        var root = document.RootElement;

        Assert.Equal(
            new[] { "server", "databases" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(
            new[] { "name", "status" },
            root.GetProperty("databases")[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Databases_endpoint_returns_503_when_discovery_fails()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnExecute = new InvalidOperationException("Server=sql01;User Id=sa;Password=hunter2"),
        };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/databases");
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<ErrorResponse>(raw, SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(DatabaseController.DiscoveryFailedMessage, body!.Detail);
        Assert.DoesNotContain("hunter2", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Databases_endpoint_always_refreshes_the_cache()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [FakeSqlQueryExecutor.DatabaseRow("AdventureWorks")] };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        await client.GetAsync("/api/databases");
        await client.GetAsync("/api/databases");

        Assert.Equal(2, executor.ExecuteCount);
    }

    [Fact]
    public async Task Databases_endpoint_only_issues_the_read_only_discovery_query()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [FakeSqlQueryExecutor.DatabaseRow("AdventureWorks")] };
        using var factory = CreateFactory(executor);
        using var client = factory.CreateClient();

        await client.GetAsync("/api/databases");

        Assert.Equal(SqlQueries.DiscoverDatabases, Assert.Single(executor.ExecutedQueries));
    }
}
