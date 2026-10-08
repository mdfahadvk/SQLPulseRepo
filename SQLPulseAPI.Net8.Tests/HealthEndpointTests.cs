using System.Net;
using System.Text.Json;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Responses;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

public sealed class HealthEndpointTests(SqlPulseApiFactory factory) : IClassFixture<SqlPulseApiFactory>
{
    [Fact]
    public async Task Health_returns_200()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Health_returns_the_python_payload_shape()
    {
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/health");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            new[] { "status", "application", "version" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("healthy", root.GetProperty("status").GetString());
        Assert.Equal("SQLPulse", root.GetProperty("application").GetString());
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Health_deserializes_into_the_response_contract()
    {
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/health");
        var health = JsonSerializer.Deserialize<HealthResponse>(json, SqlPulseJson.Options);

        Assert.NotNull(health);
        Assert.Equal("healthy", health!.Status);
        Assert.Equal("SQLPulse", health.Application);
        Assert.Equal("0.1.0", health.Version);
    }

    [Fact]
    public async Task Root_lists_available_endpoints()
    {
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/");
        var root = JsonSerializer.Deserialize<RootResponse>(json, SqlPulseJson.Options);

        Assert.NotNull(root);
        Assert.Equal("SQLPulse", root!.Application);
        Assert.Equal("Read-only SQL Server Performance Analysis Agent", root.Description);
        Assert.Equal("/health", root.Endpoints["health"]);
    }

    [Fact]
    public async Task Swagger_document_is_served()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
