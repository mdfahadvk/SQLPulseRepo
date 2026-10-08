using SQLPulseAPI.Net8.Services.History;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// The arithmetic behind every baseline. These values are hand-calculable on purpose:
/// a baseline is evidence a DBA may be asked to act on, so the numbers must be
/// verifiable without running the code.
/// </summary>
public class BaselineStatisticsTests
{
    [Fact]
    public void Mean_is_the_arithmetic_average()
    {
        var stat = BaselineStatistics.Compute([10, 20, 30, 40, 50])!;

        Assert.Equal(30.0, stat.Mean);
    }

    [Fact]
    public void Median_of_an_odd_sample_count_is_the_middle_value()
    {
        // Deliberately unsorted: the median must not depend on input order.
        var stat = BaselineStatistics.Compute([50, 10, 30, 40, 20])!;

        Assert.Equal(30.0, stat.Median);
    }

    [Fact]
    public void Median_of_an_even_sample_count_averages_the_two_middle_values()
    {
        var stat = BaselineStatistics.Compute([10, 20, 30, 40])!;

        Assert.Equal(25.0, stat.Median);
    }

    [Fact]
    public void P50_equals_the_median()
    {
        var stat = BaselineStatistics.Compute([1, 2, 3, 4, 5, 6])!;

        Assert.Equal(stat.Median, stat.P50);
    }

    [Fact]
    public void P95_uses_nearest_rank()
    {
        // n = 20, ceil(0.95 * 20) - 1 = 18 → the 19th smallest value.
        var values = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();

        var stat = BaselineStatistics.Compute(values)!;

        Assert.Equal(19.0, stat.P95);
    }

    [Fact]
    public void P95_never_indexes_past_the_last_sample()
    {
        // n = 3, ceil(0.95 * 3) - 1 = 2, which is already the last index.
        var stat = BaselineStatistics.Compute([5, 10, 15])!;

        Assert.Equal(15.0, stat.P95);
        Assert.Equal(stat.Maximum, stat.P95);
    }

    [Fact]
    public void Standard_deviation_uses_the_sample_formula()
    {
        // Values 2,4,4,4,5,5,7,9: mean 5, sum of squared deviations 32.
        // Sample variance divides by n-1 = 7 → 4.571..., sqrt → 2.14 (2 dp).
        var stat = BaselineStatistics.Compute([2, 4, 4, 4, 5, 5, 7, 9])!;

        Assert.Equal(5.0, stat.Mean);
        Assert.Equal(2.14, stat.StandardDeviation);
    }

    [Fact]
    public void A_single_sample_has_no_spread()
    {
        // Bessel's correction divides by n-1, which is zero here. Reporting 0 is the
        // only honest answer: one observation says nothing about variability.
        var stat = BaselineStatistics.Compute([42])!;

        Assert.Equal(1, stat.SampleCount);
        Assert.Equal(0.0, stat.StandardDeviation);
        Assert.Equal(42.0, stat.Mean);
        Assert.Equal(42.0, stat.Minimum);
        Assert.Equal(42.0, stat.Maximum);
    }

    [Fact]
    public void Minimum_and_maximum_span_the_sample()
    {
        var stat = BaselineStatistics.Compute([7, 3, 99, 12])!;

        Assert.Equal(3.0, stat.Minimum);
        Assert.Equal(99.0, stat.Maximum);
    }

    [Fact]
    public void Sample_count_is_the_number_of_values()
    {
        var stat = BaselineStatistics.Compute([1, 1, 1])!;

        Assert.Equal(3, stat.SampleCount);
    }

    [Fact]
    public void Results_are_rounded_to_two_decimals()
    {
        var stat = BaselineStatistics.Compute([1, 2])!;

        // Mean of 1 and 2 is exactly 1.5; use a value that actually needs rounding.
        Assert.Equal(1.5, stat.Mean);

        var third = BaselineStatistics.Compute([1, 1, 2])!;
        Assert.Equal(1.33, third.Mean);
    }

    [Fact]
    public void An_empty_sample_produces_no_statistic_rather_than_zeros()
    {
        // A zero-filled statistic would be indistinguishable from a query that genuinely
        // measured zero, and dividing by a zero baseline would manufacture an infinite
        // regression multiplier out of no evidence at all.
        Assert.Null(BaselineStatistics.Compute([]));
    }
}
