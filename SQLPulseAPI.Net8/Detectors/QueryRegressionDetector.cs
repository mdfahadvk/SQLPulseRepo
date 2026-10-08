using System.Globalization;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.History;

namespace SQLPulseAPI.Net8.Detectors;

/// <summary>
/// Detects queries whose current metrics significantly exceed their historical baseline.
/// Ported from <c>QueryRegressionDetector</c> in <c>app/detectors/regression.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// This detector does not derive from <see cref="DetectorBase"/> because it must read
/// stored history, which is asynchronous. Forcing it into the synchronous
/// <see cref="DetectorBase.Detect"/> signature would mean blocking on I/O inside a
/// request thread, so it exposes <see cref="DetectAsync"/> and is invoked separately.
/// </para>
/// <para>
/// Every comparison is arithmetic against stored samples. No LLM is involved: a
/// regression claim is evidence and must be reproducible.
/// </para>
/// </remarks>
public sealed class QueryRegressionDetector(
    IBaselineService baselineService,
    IOptions<DetectorThresholdOptions> thresholds)
{
    private readonly DetectorThresholdOptions _thresholds = thresholds.Value;

    /// <summary>Detector name, used in diagnostics.</summary>
    public string Name => "query_regression_detector";

    /// <summary>
    /// Current metrics for one query, assembled from the CPU and I/O sections.
    /// </summary>
    private sealed class CurrentMetrics
    {
        public string Database { get; init; } = string.Empty;
        public double AvgCpuMs { get; set; }
        public double AvgElapsedMs { get; set; }
        public double AvgLogicalReads { get; set; }
        public string? SqlText { get; init; }
    }

    /// <summary>
    /// Compares each query in the snapshot against its baseline and raises findings for
    /// significant degradation.
    /// </summary>
    public async Task<DetectionResult> DetectAsync(
        PerformanceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var targetDatabase = snapshot.Database;
        var currentQueries = CollectCurrentMetrics(snapshot, targetDatabase);
        if (currentQueries.Count == 0)
        {
            return DetectionResult.Empty;
        }

        var t = _thresholds;
        var now = DateTimeOffset.UtcNow;
        var window = t.RegressionBaselineWindow;
        var minSamples = t.RegressionMinSamples;
        var findings = new List<PerformanceFinding>();

        foreach (var (queryHash, metrics) in currentQueries)
        {
            var database = metrics.Database;

            // Returns null unless the baseline is READY, so an under-sampled baseline can
            // never be compared against.
            var baseline = await baselineService.GetForRegressionAsync(
                queryHash,
                database,
                window,
                minSamples,
                cancellationToken);

            if (baseline is null)
            {
                continue;
            }

            var regression = CheckRegression(metrics, baseline);
            if (regression is null)
            {
                continue;
            }

            var (dimension, multiplier, currentValue, baselineMean, sampleCount, stdDev) = regression.Value;

            Severity severity;
            if (multiplier >= t.RegressionCriticalMultiplier)
            {
                severity = Severity.Critical;
            }
            else if (multiplier >= t.RegressionHighMultiplier)
            {
                severity = Severity.High;
            }
            else if (multiplier >= t.RegressionWarningMultiplier)
            {
                severity = Severity.Medium;
            }
            else
            {
                continue;
            }

            var confidence = ComputeConfidence(sampleCount, stdDev, baselineMean);
            var preview = Truncate(metrics.SqlText, 120);

            var evidence = new List<string>
            {
                $"Query hash: {queryHash}",
                $"Database: {database}",
                $"Regression dimension: {dimension}",
                $"Current value: {Fixed(currentValue, 1)}",
                $"Baseline mean ({window}): {Fixed(baselineMean, 1)}",
                $"Regression multiplier: {Fixed(multiplier, 1)}x",
                $"Baseline samples: {sampleCount}",
                $"Baseline std dev: {Fixed(stdDev, 1)}",
            };

            if (preview.Length > 0)
            {
                evidence.Add($"SQL: {preview}");
            }

            findings.Add(new PerformanceFinding
            {
                FindingId = Guid.NewGuid().ToString(),
                Category = FindingCategory.QueryRegression,
                Server = snapshot.ServerHost,
                Database = database,
                Severity = severity,
                Confidence = confidence,
                FirstObserved = now,
                LastObserved = now,
                Summary = $"Query {dimension} is {Fixed(multiplier, 1)}x worse than its {window} baseline.",
                Evidence = evidence,
                LikelyCause =
                    $"Query performance has regressed: current {dimension} "
                    + $"({Fixed(currentValue, 0)}) significantly exceeds the historical "
                    + $"baseline ({Fixed(baselineMean, 0)}). This indicates a recent "
                    + "change in query behavior.",
                Recommendation =
                    "Compare the current execution plan against a known-good plan. "
                    + "Check for recent schema changes, statistics updates, parameter "
                    + "changes, or data volume growth that could explain the regression.",
                ValidationSteps =
                [
                    "Retrieve the current execution plan",
                    "Check if statistics were recently updated",
                    "Look for recent schema or index changes",
                    "Verify data volume hasn't changed dramatically",
                ],
                QueryIdentity = new QueryIdentity { QueryHash = queryHash },
                HumanReviewRequired = severity is Severity.Critical or Severity.High,
            });
        }

        return new DetectionResult(findings, []);
    }

    /// <summary>
    /// Builds the current per-query metrics from the CPU section first, then merges the
    /// I/O section into it.
    /// </summary>
    private static Dictionary<string, CurrentMetrics> CollectCurrentMetrics(
        PerformanceSnapshot snapshot,
        string? targetDatabase)
    {
        var current = new Dictionary<string, CurrentMetrics>(StringComparer.Ordinal);

        var cpuSection = snapshot.TopCpuQueries;
        if (cpuSection is not null && cpuSection.Metadata.Success)
        {
            foreach (var query in cpuSection.Queries)
            {
                if (string.IsNullOrEmpty(query.QueryHash) || !InScope(query.DatabaseName, targetDatabase))
                {
                    continue;
                }

                current[query.QueryHash] = new CurrentMetrics
                {
                    Database = HistoryService.ResolveDatabase(query.DatabaseName, targetDatabase),
                    AvgCpuMs = query.AvgCpuTimeMs,
                    AvgElapsedMs = query.AvgElapsedTimeMs,
                    AvgLogicalReads = query.AvgLogicalReads,
                    SqlText = query.SqlText,
                };
            }
        }

        var ioSection = snapshot.TopIoQueries;
        if (ioSection is not null && ioSection.Metadata.Success)
        {
            foreach (var query in ioSection.Queries)
            {
                if (string.IsNullOrEmpty(query.QueryHash) || !InScope(query.DatabaseName, targetDatabase))
                {
                    continue;
                }

                if (current.TryGetValue(query.QueryHash, out var existing))
                {
                    // The I/O collector orders by reads, so a larger figure there is the
                    // more complete measurement for the same query.
                    if (query.AvgLogicalReads > existing.AvgLogicalReads)
                    {
                        existing.AvgLogicalReads = query.AvgLogicalReads;
                    }

                    continue;
                }

                // I/O-only query: CPU and elapsed are unmeasured here and stay at zero,
                // which excludes them from comparison rather than inventing a value.
                current[query.QueryHash] = new CurrentMetrics
                {
                    Database = HistoryService.ResolveDatabase(query.DatabaseName, targetDatabase),
                    AvgLogicalReads = query.AvgLogicalReads,
                    SqlText = query.SqlText,
                };
            }
        }

        return current;
    }

    /// <summary>Database-scope isolation: never report another database's query.</summary>
    private static bool InScope(string? queryDatabase, string? targetDatabase) =>
        string.IsNullOrEmpty(targetDatabase)
        || string.IsNullOrEmpty(queryDatabase)
        || string.Equals(queryDatabase, targetDatabase, StringComparison.Ordinal);

    /// <summary>
    /// Returns the worst regression across the compared dimensions, or
    /// <see langword="null"/> when none reaches the warning multiplier.
    /// </summary>
    /// <remarks>
    /// A dimension is skipped when its baseline statistic is missing, its baseline mean
    /// is not positive, or the current value is not positive. Dividing by a zero or
    /// absent baseline would manufacture an unbounded multiplier out of no evidence.
    /// </remarks>
    private (string Dimension, double Multiplier, double CurrentValue, double BaselineMean, int SampleCount, double StdDev)?
        CheckRegression(CurrentMetrics metrics, QueryBaseline baseline)
    {
        (string Dimension, double Current, BaselineStatistic? Stat)[] checks =
        [
            ("avg_elapsed_ms", metrics.AvgElapsedMs, baseline.AvgElapsedMs),
            ("avg_cpu_ms", metrics.AvgCpuMs, baseline.AvgCpuMs),
            ("avg_logical_reads", metrics.AvgLogicalReads, baseline.AvgLogicalReads),
        ];

        (string, double, double, double, int, double)? worst = null;

        foreach (var (dimension, currentValue, stat) in checks)
        {
            if (stat is null || stat.Mean <= 0 || currentValue <= 0)
            {
                continue;
            }

            var multiplier = currentValue / stat.Mean;
            if (multiplier < _thresholds.RegressionWarningMultiplier)
            {
                continue;
            }

            if (worst is null || multiplier > worst.Value.Item2)
            {
                worst = (dimension, multiplier, currentValue, stat.Mean, stat.SampleCount, stat.StandardDeviation);
            }
        }

        return worst;
    }

    /// <summary>
    /// Derives confidence from how much history exists and how stable it is.
    /// </summary>
    /// <remarks>
    /// A baseline that swings wildly cannot support a strong claim even when the
    /// multiplier is large, so relative variance caps confidence independently of
    /// severity.
    /// </remarks>
    internal static Confidence ComputeConfidence(int sampleCount, double stdDev, double baselineMean)
    {
        if (sampleCount < 5)
        {
            return Confidence.Low;
        }

        var cv = baselineMean > 0 ? stdDev / baselineMean : 0.0;

        if (sampleCount >= 10 && cv < 0.5)
        {
            return Confidence.High;
        }

        return cv < 1.0 ? Confidence.Medium : Confidence.Low;
    }

    private static string Truncate(string? value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= length ? value : value[..length];
    }

    private static string Fixed(double value, int decimals) =>
        value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
}
