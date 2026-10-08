using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Repositories;

namespace SQLPulseAPI.Net8.Services.History;

/// <summary>
/// Calculates statistical baselines for a query from stored history.
/// </summary>
public interface IBaselineService
{
    /// <summary>
    /// Calculates the baseline for a query over a time window.
    /// </summary>
    /// <param name="queryHash">Query hash to baseline.</param>
    /// <param name="database">Database the samples were recorded against.</param>
    /// <param name="window">Time window: <c>1h</c>, <c>24h</c>, <c>7d</c> or <c>30d</c>.</param>
    /// <param name="minSamples">
    /// Overrides the configured minimum; below it the baseline is reported as
    /// <see cref="BaselineStatus.InsufficientData"/> with no metrics.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The window is not supported.</exception>
    Task<QueryBaseline> CalculateAsync(
        string queryHash,
        string database,
        string window = BaselineService.DefaultWindow,
        int? minSamples = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a baseline only when it is <see cref="BaselineStatus.Ready"/>, otherwise
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Regression detection must never compare against an unusable baseline, so the
    /// "not enough data" case is expressed as the absence of a baseline rather than as a
    /// baseline the caller has to remember to check.
    /// </remarks>
    Task<QueryBaseline?> GetForRegressionAsync(
        string queryHash,
        string database,
        string window = BaselineService.DefaultWindow,
        int? minSamples = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic baseline calculation.
/// Ported from <c>BaselineService</c> in <c>app/storage/baseline_service.py</c>.
/// </summary>
/// <remarks>
/// Every number here is computed arithmetically from stored samples. No LLM is involved:
/// a baseline is evidence, and evidence has to be reproducible.
/// </remarks>
public sealed class BaselineService(
    IHistoryRepository repository,
    IOptions<HistoryOptions> historyOptions,
    ILogger<BaselineService> logger) : IBaselineService
{
    /// <summary>Window used when the caller does not specify one.</summary>
    public const string DefaultWindow = "7d";

    /// <summary>Supported windows and their length in hours. Python: <c>WINDOW_HOURS</c>.</summary>
    private static readonly Dictionary<string, int> WindowHours = new(StringComparer.Ordinal)
    {
        ["1h"] = 1,
        ["24h"] = 24,
        ["7d"] = 168,
        ["30d"] = 720,
    };

    private readonly HistoryOptions _history = historyOptions.Value;

    /// <summary>Window names accepted by <see cref="CalculateAsync"/>, sorted for display.</summary>
    public static IReadOnlyList<string> SupportedWindows { get; } =
        WindowHours.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>Returns whether <paramref name="window"/> is a supported window name.</summary>
    public static bool IsSupportedWindow(string? window) =>
        window is not null && WindowHours.ContainsKey(window);

    /// <inheritdoc />
    public async Task<QueryBaseline> CalculateAsync(
        string queryHash,
        string database,
        string window = DefaultWindow,
        int? minSamples = null,
        CancellationToken cancellationToken = default)
    {
        if (!WindowHours.TryGetValue(window, out var hours))
        {
            throw new ArgumentException(
                $"Unsupported window '{window}'. Use one of: {string.Join(", ", SupportedWindows)}",
                nameof(window));
        }

        var required = minSamples ?? _history.BaselineMinSamples;
        var since = DateTimeOffset.UtcNow.AddHours(-hours);

        var records = await repository.GetQuerySamplesAsync(queryHash, database, since, cancellationToken);

        var baseline = new QueryBaseline
        {
            QueryHash = queryHash,
            Database = database,
            Server = records.Count > 0 ? records[0].Server : string.Empty,
            Window = window,
            SampleCount = records.Count,
        };

        if (records.Count < required)
        {
            // Deliberately leaves every statistic null. Zero-filled statistics would be
            // indistinguishable from a query that genuinely measured zero, and any
            // current value would then look like an infinite regression.
            baseline.BaselineStatus = BaselineStatus.InsufficientData;
            return baseline;
        }

        baseline.BaselineStatus = BaselineStatus.Ready;
        baseline.AvgCpuMs = BaselineStatistics.Compute(records.Select(r => r.AvgCpuTimeMs).ToArray());
        baseline.AvgElapsedMs = BaselineStatistics.Compute(records.Select(r => r.AvgElapsedTimeMs).ToArray());
        baseline.AvgLogicalReads = BaselineStatistics.Compute(records.Select(r => r.AvgLogicalReads).ToArray());
        baseline.AvgPhysicalReads = BaselineStatistics.Compute(records.Select(r => r.AvgPhysicalReads).ToArray());
        baseline.ExecutionCount = BaselineStatistics.Compute(records.Select(r => (double)r.ExecutionCount).ToArray());

        return baseline;
    }

    /// <inheritdoc />
    public async Task<QueryBaseline?> GetForRegressionAsync(
        string queryHash,
        string database,
        string window = DefaultWindow,
        int? minSamples = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var baseline = await CalculateAsync(queryHash, database, window, minSamples, cancellationToken);
            return baseline.BaselineStatus == BaselineStatus.Ready ? baseline : null;
        }
        catch (Exception exception)
        {
            // Missing history is the normal state on a new install. Failing detection
            // because of it would remove findings that do not depend on history at all.
            logger.LogDebug(
                exception,
                "Baseline lookup failed for {QueryHash} in {Database}",
                queryHash,
                database);
            return null;
        }
    }
}
