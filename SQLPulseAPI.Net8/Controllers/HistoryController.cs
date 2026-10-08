using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Repositories;
using SQLPulseAPI.Net8.Services.History;

namespace SQLPulseAPI.Net8.Controllers;

/// <summary>
/// Historical query measurements and baselines.
/// Mirrors <c>GET /api/history/query/{query_hash}</c> and
/// <c>GET /api/history/query/{query_hash}/baseline</c> in the Python implementation.
/// </summary>
/// <remarks>
/// These endpoints read SQLPulse's own storage, not the monitored SQL Server. When that
/// storage is unreachable they return 503 rather than an empty result: "no history" and
/// "cannot read history" are different answers, and reporting the second as the first
/// would make a query look unbaselined when it is not.
/// </remarks>
[ApiController]
public sealed class HistoryController(
    IHistoryRepository repository,
    IBaselineService baselineService,
    IOptions<SqlServerOptions> sqlServerOptions,
    ILogger<HistoryController> logger) : ControllerBase
{
    /// <summary>Returned when historical storage cannot be read.</summary>
    public const string StorageUnavailableMessage = "Historical storage unavailable";

    /// <summary>Returned when the required <c>database</c> parameter is absent.</summary>
    public const string DatabaseRequiredMessage = "database parameter is required";

    private const int DefaultLimit = 500;
    private const int MaxLimit = 5000;

    /// <summary>
    /// Returns historical performance measurements for a single query, newest first.
    /// </summary>
    /// <param name="queryHash">Query hash to look up.</param>
    /// <param name="database">Database the samples were recorded against.</param>
    /// <param name="limit">Maximum number of samples to return (1–5000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/history/query/{query_hash}")]
    [ProducesResponseType(typeof(QueryHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<QueryHistoryResponse>> QueryHistory(
        [FromRoute(Name = "query_hash")] string queryHash,
        [FromQuery(Name = "database")] string? database = null,
        [FromQuery(Name = "limit")] int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return Unprocessable(DatabaseRequiredMessage);
        }

        if (limit < 1 || limit > MaxLimit)
        {
            return Unprocessable($"limit must be between 1 and {MaxLimit}");
        }

        IReadOnlyList<QueryMetricRecord> samples;
        try
        {
            samples = await repository.GetQueryHistoryAsync(queryHash, database, limit, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to retrieve query history");
            return StorageUnavailable();
        }

        return Ok(new QueryHistoryResponse
        {
            QueryHash = queryHash,
            Database = database,
            Server = sqlServerOptions.Value.Server,
            Samples = [.. samples],
        });
    }

    /// <summary>
    /// Returns the statistical baseline for a single query over a time window.
    /// </summary>
    /// <remarks>
    /// When fewer samples exist than the configured minimum, the response reports
    /// <c>baseline_status = INSUFFICIENT_DATA</c> with an empty <c>metrics</c> object.
    /// Zero-valued statistics are never emitted in that case: they would be
    /// indistinguishable from a query that genuinely measured zero.
    /// </remarks>
    /// <param name="queryHash">Query hash to look up.</param>
    /// <param name="database">Database the samples were recorded against.</param>
    /// <param name="window">Time window: <c>1h</c>, <c>24h</c>, <c>7d</c> or <c>30d</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/history/query/{query_hash}/baseline")]
    [ProducesResponseType(typeof(QueryBaselineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<QueryBaselineResponse>> QueryBaseline(
        [FromRoute(Name = "query_hash")] string queryHash,
        [FromQuery(Name = "database")] string? database = null,
        [FromQuery(Name = "window")] string window = BaselineService.DefaultWindow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return Unprocessable(DatabaseRequiredMessage);
        }

        if (!BaselineService.IsSupportedWindow(window))
        {
            return BadRequest(new ErrorResponse
            {
                Detail = $"Invalid window '{window}'. Use one of: {string.Join(", ", BaselineService.SupportedWindows)}",
            });
        }

        QueryBaseline baseline;
        try
        {
            baseline = await baselineService.CalculateAsync(
                queryHash, database, window, minSamples: null, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to calculate baseline");
            return StorageUnavailable();
        }

        var metrics = new Dictionary<string, BaselineStatistic>(StringComparer.Ordinal);
        Add(metrics, "avg_cpu_ms", baseline.AvgCpuMs);
        Add(metrics, "avg_elapsed_ms", baseline.AvgElapsedMs);
        Add(metrics, "avg_logical_reads", baseline.AvgLogicalReads);
        Add(metrics, "avg_physical_reads", baseline.AvgPhysicalReads);
        Add(metrics, "execution_count", baseline.ExecutionCount);

        return Ok(new QueryBaselineResponse
        {
            QueryHash = queryHash,
            Database = database,
            Window = window,
            SampleCount = baseline.SampleCount,
            BaselineStatus = baseline.BaselineStatus,
            Metrics = metrics,
        });
    }

    private static void Add(
        Dictionary<string, BaselineStatistic> metrics,
        string name,
        BaselineStatistic? statistic)
    {
        if (statistic is not null)
        {
            metrics[name] = statistic;
        }
    }

    private ObjectResult Unprocessable(string detail) => StatusCode(
        StatusCodes.Status422UnprocessableEntity,
        new ErrorResponse { Detail = detail });

    private ObjectResult StorageUnavailable() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ErrorResponse { Detail = StorageUnavailableMessage });
}
