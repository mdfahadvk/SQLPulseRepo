using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>Result of a read-only SigNoz query, with provider details kept internal.</summary>
public sealed record SigNozQueryResult(
    SigNozQueryResultStatus Status,
    JsonDocument? Payload = null);

public enum SigNozQueryResultStatus
{
    Available,
    PermissionDenied,
    Failed,
}

/// <summary>
/// Typed HTTP client for the documented SigNoz v5 query endpoint.
/// </summary>
/// <remarks>
/// The API key is sent only as a request header. Provider response bodies are deliberately
/// not copied into exceptions or logs because they can contain telemetry payloads.
/// </remarks>
public sealed class SigNozTelemetryClient(
    HttpClient httpClient,
    IOptions<SqlErrorOptions> options,
    ILogger<SigNozTelemetryClient> logger)
{
    private const string QueryPath = "api/v5/query_range";
    private readonly SqlErrorOptions _options = options.Value;

    public async Task<SigNozQueryResult> QueryAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? database,
        CancellationToken cancellationToken = default)
        => await QueryForServiceAsync(start, end, database, null, cancellationToken);

    public async Task<SigNozQueryResult> QueryForServiceAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        string? database,
        string? serviceName,
        CancellationToken cancellationToken = default)
    {
        var serviceFilter = serviceName ?? _options.EffectiveSigNozServiceName;
        var body = BuildQuery(start, end, serviceFilter, _options.EffectiveSigNozSignal);
        using var request = new HttpRequestMessage(HttpMethod.Post, QueryPath)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation(_options.EffectiveSigNozAuthHeader, _options.EffectiveSigNozApiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogWarning(
                    "SigNoz query {Endpoint} for service {ServiceFilter} and database {DatabaseFilter} returned HTTP {StatusCode}.",
                    QueryPath, serviceFilter, database, (int)response.StatusCode);
                return new(SigNozQueryResultStatus.PermissionDenied);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "SigNoz query {Endpoint} for service {ServiceFilter} and database {DatabaseFilter} returned HTTP {StatusCode}; telemetry will be marked unavailable.",
                    QueryPath, serviceFilter, database, (int)response.StatusCode);
                return new(SigNozQueryResultStatus.Failed);
            }

            logger.LogInformation(
                "SigNoz query {Endpoint} for service {ServiceFilter} and database {DatabaseFilter} returned HTTP {StatusCode}.",
                QueryPath, serviceFilter, database, (int)response.StatusCode);

            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                return new(SigNozQueryResultStatus.Available, await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false));
            }
            catch (JsonException)
            {
                logger.LogWarning(
                    "SigNoz query {Endpoint} for service {ServiceFilter} and database {DatabaseFilter} returned invalid JSON; telemetry will be marked unavailable.",
                    QueryPath, serviceFilter, database);
                return new(SigNozQueryResultStatus.Failed);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "SigNoz query {Endpoint} for service {ServiceFilter} and database {DatabaseFilter} timed out; telemetry will be marked unavailable.",
                QueryPath, serviceFilter, database);
            return new(SigNozQueryResultStatus.Failed);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning(
                "SigNoz query {Endpoint} for service {ServiceFilter} and database {DatabaseFilter} could not be reached; telemetry will be marked unavailable.",
                QueryPath, serviceFilter, database);
            return new(SigNozQueryResultStatus.Failed);
        }
    }

    private static JsonObject BuildQuery(
        DateTimeOffset start,
        DateTimeOffset end,
        string? serviceName,
        string signal)
    {
        var filters = new List<string>();
        if (signal == "traces")
        {
            filters.Add("has_error = true");
        }
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            filters.Add($"service.name = '{EscapeFilterValue(serviceName)}'");
        }

        // The configured environment is applied to returned spans only when the actual
        // attribute is present. A missing optional resource attribute must not hide data.

        var result = new JsonObject
        {
            ["start"] = start.ToUnixTimeMilliseconds(),
            ["end"] = end.ToUnixTimeMilliseconds(),
            ["requestType"] = "raw",
            ["variables"] = new JsonObject(),
            ["compositeQuery"] = new JsonObject
            {
                ["queries"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "builder_query",
                        ["spec"] = new JsonObject
                        {
                            ["name"] = "A",
                            ["signal"] = signal,
                            ["filter"] = new JsonObject
                            {
                                ["expression"] = string.Join(" AND ", filters),
                            },
                            ["order"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["key"] = new JsonObject { ["name"] = "timestamp" },
                                    ["direction"] = "desc",
                                },
                            },
                            ["limit"] = 500,
                            ["offset"] = 0,
                            ["disabled"] = false,
                        },
                    },
                },
            },
        };

        if (signal == "traces")
        {
            ((JsonObject)((JsonObject)((JsonArray)((JsonObject)result["compositeQuery"]!)["queries"]!)[0]!)!["spec"]!)!["selectFields"] = new JsonArray
            {
                Field("service.name", "resource"),
                Field("deployment.environment", "resource"),
                Field("host.name", "resource"),
                Field("exception.type", "span"),
                Field("exception.message", "span"),
                Field("trace_id", "span"),
                Field("span_id", "span"),
                Field("http.route", "span"),
                Field("db.system", "span"),
                Field("db.namespace", "span"),
                Field("db.operation.name", "span"),
                Field("db.query.summary", "span"),
                Field("server.address", "span"),
            };
        }

        return result;

        static JsonObject Field(string name, string context) => new()
        {
            ["name"] = name,
            ["fieldContext"] = context,
        };
    }

    private static string EscapeFilterValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
