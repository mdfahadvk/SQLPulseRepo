using SQLPulseAPI.Net8.Models.History;

namespace SQLPulseAPI.Net8.Services.History;

/// <summary>
/// Descriptive statistics for baseline calculation.
/// Ported from <c>_compute_statistic</c> in <c>app/storage/baseline_service.py</c>.
/// </summary>
/// <remarks>
/// Purely arithmetic and deterministic. No LLM is involved in any calculation here or
/// anywhere else in the history layer.
/// </remarks>
public static class BaselineStatistics
{
    /// <summary>Results are rounded to this many decimals, matching the Python reference.</summary>
    private const int Decimals = 2;

    /// <summary>
    /// Computes statistics over <paramref name="values"/>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when there are no values. A caller must not receive a
    /// zero-filled statistic for an empty set, because "no data" and "measured zero" are
    /// different facts and a zero baseline would make any current value look like an
    /// infinite regression.
    /// </returns>
    public static BaselineStatistic? Compute(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var n = values.Count;
        var sorted = values.OrderBy(v => v).ToArray();

        var mean = values.Sum() / n;
        var median = n % 2 != 0
            ? sorted[n / 2]
            : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;

        // Sample standard deviation (n-1). These readings are a sample of the query's
        // behaviour, not its complete population, so Bessel's correction applies.
        // A single sample has no spread to measure, so it reports 0 rather than dividing
        // by zero.
        var standardDeviation = 0.0;
        if (n > 1)
        {
            var variance = values.Sum(v => (v - mean) * (v - mean)) / (n - 1);
            standardDeviation = Math.Sqrt(variance);
        }

        // Nearest-rank percentile: index ceil(0.95 * n) - 1, clamped to the last element.
        var p95Index = Math.Min((int)Math.Ceiling(0.95 * n) - 1, n - 1);

        return new BaselineStatistic
        {
            SampleCount = n,
            Mean = Round(mean),
            Median = Round(median),
            Minimum = Round(sorted[0]),
            Maximum = Round(sorted[^1]),
            StandardDeviation = Round(standardDeviation),
            // P50 is the median by definition; both are exposed because the API contract
            // carries both names.
            P50 = Round(median),
            P95 = Round(sorted[p95Index]),
        };
    }

    /// <summary>
    /// Rounds to <see cref="Decimals"/> places using banker's rounding, matching Python's
    /// built-in <c>round()</c> so the two implementations agree on exact midpoints.
    /// </summary>
    private static double Round(double value) => Math.Round(value, Decimals, MidpointRounding.ToEven);
}
