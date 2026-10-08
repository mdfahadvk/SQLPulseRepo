using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Detectors;

/// <summary>
/// What a detector produced from one snapshot.
/// </summary>
/// <param name="Findings">Findings raised by the detector.</param>
/// <param name="ExcludedQueries">
/// Queries the detector deliberately skipped, retained so an operator can confirm the
/// filter is not hiding real workload.
/// </param>
public sealed record DetectionResult(
    IReadOnlyList<PerformanceFinding> Findings,
    IReadOnlyList<ExcludedQuery> ExcludedQueries)
{
    /// <summary>An empty result.</summary>
    public static DetectionResult Empty { get; } = new([], []);
}

/// <summary>
/// Base class for the deterministic performance detectors.
/// Ported from <c>BaseDetector</c> in <c>app/detectors/base.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Detection is deterministic: the same snapshot and thresholds always produce the same
/// findings. No LLM is involved at this layer.
/// </para>
/// <para>
/// Unlike the Python original, exclusions are returned from <see cref="Detect"/> rather
/// than accumulated on the instance. Detectors are registered as singletons and can run
/// for several requests at once, so per-instance mutable state would leak one caller's
/// exclusions into another's response.
/// </para>
/// </remarks>
public abstract class DetectorBase(DetectorThresholdOptions thresholds)
{
    /// <summary>Ordered weakest-first so confidence can be stepped down by index.</summary>
    private static readonly Confidence[] ConfidenceLadder =
    [
        Confidence.Low,
        Confidence.Medium,
        Confidence.High,
    ];

    /// <summary>Thresholds controlling when this detector raises findings.</summary>
    protected DetectorThresholdOptions Thresholds { get; } = thresholds;

    /// <summary>Detector name, used in diagnostics.</summary>
    public abstract string Name { get; }

    /// <summary>Analyses a snapshot. Must be deterministic and must not throw.</summary>
    public abstract DetectionResult Detect(PerformanceSnapshot snapshot);

    /// <summary>Creates a new finding identifier.</summary>
    protected static string NewFindingId() => Guid.NewGuid().ToString();

    /// <summary>Current UTC timestamp, isolated so tests can reason about ordering.</summary>
    protected static DateTimeOffset Now() => DateTimeOffset.UtcNow;

    /// <summary>
    /// Steps <paramref name="confidence"/> down, never below <see cref="Confidence.Low"/>.
    /// </summary>
    protected static Confidence WeakenConfidence(Confidence confidence, int steps = 1)
    {
        var index = Array.IndexOf(ConfidenceLadder, confidence);
        return ConfidenceLadder[Math.Max(0, index - steps)];
    }

    /// <summary>
    /// Returns an <see cref="ExcludedQuery"/> when <paramref name="sqlText"/> is diagnostic
    /// noise, otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Monitoring queries are recorded rather than silently dropped, so an operator can
    /// confirm the filter is not hiding real workload.
    /// </remarks>
    protected static ExcludedQuery? ClassifyMonitoringQuery(
        string? sqlText,
        string? queryHash,
        string? databaseName)
    {
        var reason = QueryFilters.ClassifyExclusion(sqlText);
        if (reason is null)
        {
            return null;
        }

        var preview = Truncate(sqlText, 150);

        return new ExcludedQuery
        {
            Reason = reason,
            QueryHash = queryHash,
            Database = databaseName,
            SqlPreview = string.IsNullOrEmpty(preview) ? null : preview,
        };
    }

    /// <summary>Truncates <paramref name="value"/> to <paramref name="length"/> characters.</summary>
    protected static string Truncate(string? value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= length ? value : value[..length];
    }
}
