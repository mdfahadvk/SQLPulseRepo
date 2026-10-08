using System.Net;
using System.Text.Json;
using SQLPulseAPI.Net8.Controllers;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Responses;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Endpoint behaviour for <c>/api/history/query/{query_hash}</c> and its baseline
/// companion, driven by in-memory storage — no SQL Server and no SQLite file.
/// </summary>
public sealed class HistoryEndpointTests
{
    private const string Hash = "0xABC";
    private const string Database = "AppDb";

    private static QueryMetricRecord Sample(DateTimeOffset collectedAt, double cpuMs = 100) => new()
    {
        SnapshotId = Guid.NewGuid().ToString(),
        CollectedAt = collectedAt,
        Server = "sql01",
        Database = Database,
        QueryHash = Hash,
        SqlTextPreview = "SELECT 1",
        ExecutionCount = 10,
        AvgCpuTimeMs = cpuMs,
        AvgElapsedTimeMs = cpuMs * 2,
        AvgLogicalReads = cpuMs * 10,
        AvgPhysicalReads = cpuMs,
    };

    private static void Seed(SqlPulseApiFactory factory, int count)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            factory.HistoryRepository.Seed(Sample(now.AddMinutes(-i)));
        }
    }

    private static async Task<string> DetailOf(HttpResponseMessage response)
    {
        var body = JsonSerializer.Deserialize<ErrorResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        return body!.Detail;
    }

    [Fact]
    public async Task History_returns_the_stored_samples()
    {
        using var factory = new SqlPulseApiFactory();
        Seed(factory, 3);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/history/query/{Hash}?database={Database}");
        var body = JsonSerializer.Deserialize<QueryHistoryResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Hash, body!.QueryHash);
        Assert.Equal(Database, body.Database);
        Assert.Equal(3, body.Samples.Count);
    }

    [Fact]
    public async Task History_json_uses_the_python_property_names()
    {
        using var factory = new SqlPulseApiFactory();
        Seed(factory, 1);
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync($"/api/history/query/{Hash}?database={Database}");
        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;
        Assert.True(root.TryGetProperty("query_hash", out _));
        Assert.True(root.TryGetProperty("samples", out var samples));

        var sample = samples[0];
        Assert.True(sample.TryGetProperty("collected_at", out _));
        Assert.True(sample.TryGetProperty("sql_text_preview", out _));
        Assert.True(sample.TryGetProperty("avg_cpu_time_ms", out _));
        Assert.True(sample.TryGetProperty("total_logical_writes", out _));
    }

    [Fact]
    public async Task History_requires_a_database()
    {
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/history/query/{Hash}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(HistoryController.DatabaseRequiredMessage, await DetailOf(response));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public async Task History_rejects_an_out_of_range_limit(int limit)
    {
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/history/query/{Hash}?database={Database}&limit={limit}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task History_reports_unavailable_storage_rather_than_an_empty_result()
    {
        // "No history" and "cannot read history" are different answers; conflating them
        // would make a query look unbaselined when it is not.
        using var factory = new SqlPulseApiFactory();
        factory.HistoryRepository.ThrowOnAccess = true;
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/history/query/{Hash}?database={Database}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(HistoryController.StorageUnavailableMessage, await DetailOf(response));
    }

    [Fact]
    public async Task Baseline_reports_insufficient_data_with_no_metrics()
    {
        using var factory = new SqlPulseApiFactory();
        Seed(factory, 2);
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync($"/api/history/query/{Hash}/baseline?database={Database}");
        using var document = JsonDocument.Parse(json);

        Assert.Equal("INSUFFICIENT_DATA", document.RootElement.GetProperty("baseline_status").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("sample_count").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("metrics").EnumerateObject());
    }

    [Fact]
    public async Task Baseline_returns_every_statistic_once_there_is_enough_data()
    {
        using var factory = new SqlPulseApiFactory();
        Seed(factory, 6);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/history/query/{Hash}/baseline?database={Database}");
        var body = JsonSerializer.Deserialize<QueryBaselineResponse>(
            await response.Content.ReadAsStringAsync(), SqlPulseJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BaselineStatus.Ready, body!.BaselineStatus);
        Assert.Equal(6, body.SampleCount);
        Assert.Equal("7d", body.Window);

        var cpu = body.Metrics["avg_cpu_ms"];
        Assert.Equal(6, cpu.SampleCount);
        Assert.Equal(100.0, cpu.Mean);
        Assert.Equal(100.0, cpu.P95);
        Assert.Equal(0.0, cpu.StandardDeviation);

        Assert.Contains("avg_elapsed_ms", body.Metrics.Keys);
        Assert.Contains("avg_logical_reads", body.Metrics.Keys);
        Assert.Contains("avg_physical_reads", body.Metrics.Keys);
        Assert.Contains("execution_count", body.Metrics.Keys);
    }

    [Fact]
    public async Task Baseline_rejects_an_unsupported_window()
    {
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/history/query/{Hash}/baseline?database={Database}&window=90d");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid window '90d'. Use one of: 1h, 24h, 30d, 7d", await DetailOf(response));
    }

    [Fact]
    public async Task Baseline_requires_a_database()
    {
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/history/query/{Hash}/baseline");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Baseline_reports_unavailable_storage()
    {
        using var factory = new SqlPulseApiFactory();
        factory.HistoryRepository.ThrowOnAccess = true;
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/history/query/{Hash}/baseline?database={Database}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(HistoryController.StorageUnavailableMessage, await DetailOf(response));
    }

    [Fact]
    public async Task The_root_document_advertises_the_history_endpoints()
    {
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/");
        using var document = JsonDocument.Parse(json);

        var endpoints = document.RootElement.GetProperty("endpoints");
        Assert.Equal(
            "/api/history/query/{query_hash}?database=...",
            endpoints.GetProperty("query_history").GetString());
        Assert.Equal(
            "/api/history/query/{query_hash}/baseline?database=...",
            endpoints.GetProperty("query_baseline").GetString());
    }
}
