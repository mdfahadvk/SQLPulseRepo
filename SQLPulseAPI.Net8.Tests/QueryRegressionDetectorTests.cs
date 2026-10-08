using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Regression detection: severity bands, confidence, dimension selection and the
/// refusal to compare against a baseline that cannot support a conclusion.
/// </summary>
public class QueryRegressionDetectorTests
{
    private const string Hash = "0xAAA";

    private static BaselineStatistic Stat(double mean, int sampleCount = 10, double stdDev = 1.0) => new()
    {
        SampleCount = sampleCount,
        Mean = mean,
        Median = mean,
        Minimum = mean,
        Maximum = mean,
        StandardDeviation = stdDev,
        P50 = mean,
        P95 = mean,
    };

    private static QueryBaseline Baseline(
        double? elapsedMean = null,
        double? cpuMean = null,
        double? readsMean = null,
        int sampleCount = 10,
        double stdDev = 1.0,
        string database = "AppDb") => new()
        {
            QueryHash = Hash,
            Database = database,
            Window = "7d",
            SampleCount = sampleCount,
            BaselineStatus = BaselineStatus.Ready,
            AvgElapsedMs = elapsedMean is null ? null : Stat(elapsedMean.Value, sampleCount, stdDev),
            AvgCpuMs = cpuMean is null ? null : Stat(cpuMean.Value, sampleCount, stdDev),
            AvgLogicalReads = readsMean is null ? null : Stat(readsMean.Value, sampleCount, stdDev),
        };

    private static async Task<IReadOnlyList<PerformanceFinding>> Detect(
        PerformanceSnapshot snapshot,
        StubBaselineService baselines)
    {
        var detector = new QueryRegressionDetector(baselines, SnapshotBuilder.Options);
        return (await detector.DetectAsync(snapshot)).Findings;
    }

    private static PerformanceSnapshot SnapshotWithCpu(
        double avgCpuMs,
        double avgElapsedMs,
        string? database = "AppDb")
    {
        var snapshot = SnapshotBuilder.Snapshot(AnalysisScope.Server);
        var query = SnapshotBuilder.Cpu(avgCpuMs, database: database, queryHash: Hash);
        query.AvgElapsedTimeMs = avgElapsedMs;
        snapshot.WithCpu(query);
        return snapshot;
    }

    [Fact]
    public void The_detector_is_named_after_the_python_original()
    {
        var detector = new QueryRegressionDetector(new StubBaselineService(), SnapshotBuilder.Options);

        Assert.Equal("query_regression_detector", detector.Name);
    }

    [Theory]
    [InlineData(2.0, Severity.Medium)]
    [InlineData(3.9, Severity.Medium)]
    [InlineData(4.0, Severity.High)]
    [InlineData(7.9, Severity.High)]
    [InlineData(8.0, Severity.Critical)]
    [InlineData(20.0, Severity.Critical)]
    public async Task Severity_follows_the_regression_multiplier(double multiplier, Severity expected)
    {
        var snapshot = SnapshotWithCpu(avgCpuMs: 10, avgElapsedMs: 100 * multiplier);
        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 100));

        var finding = Assert.Single(await Detect(snapshot, baselines));

        Assert.Equal(expected, finding.Severity);
        Assert.Equal(FindingCategory.QueryRegression, finding.Category);
    }

    [Fact]
    public async Task A_query_below_the_warning_multiplier_is_not_reported()
    {
        var snapshot = SnapshotWithCpu(avgCpuMs: 10, avgElapsedMs: 199);
        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 100));

        Assert.Empty(await Detect(snapshot, baselines));
    }

    [Fact]
    public async Task A_query_with_no_ready_baseline_is_not_reported()
    {
        // Absence of history is not evidence of a regression.
        var snapshot = SnapshotWithCpu(avgCpuMs: 10, avgElapsedMs: 10_000);

        Assert.Empty(await Detect(snapshot, new StubBaselineService()));
    }

    [Fact]
    public async Task A_baseline_mean_of_zero_is_never_used_as_a_divisor()
    {
        // Dividing by zero would manufacture an unbounded multiplier from no evidence.
        var snapshot = SnapshotWithCpu(avgCpuMs: 10, avgElapsedMs: 10_000);
        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 0));

        Assert.Empty(await Detect(snapshot, baselines));
    }

    [Fact]
    public async Task An_unmeasured_current_value_is_skipped()
    {
        var snapshot = SnapshotWithCpu(avgCpuMs: 0, avgElapsedMs: 0);
        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 100, cpuMean: 50));

        Assert.Empty(await Detect(snapshot, baselines));
    }

    [Fact]
    public async Task The_worst_dimension_is_the_one_reported()
    {
        // Elapsed is checked first but CPU regressed further, so CPU must win.
        var snapshot = SnapshotWithCpu(avgCpuMs: 500, avgElapsedMs: 300);
        var baselines = new StubBaselineService()
            .WithBaseline(Hash, Baseline(elapsedMean: 100, cpuMean: 50));

        var finding = Assert.Single(await Detect(snapshot, baselines));

        Assert.Contains("Regression dimension: avg_cpu_ms", finding.Evidence);
        Assert.Contains("avg_cpu_ms", finding.Summary);
        Assert.Contains("10.0x", finding.Summary);
    }

    [Fact]
    public async Task Evidence_carries_the_numbers_behind_the_claim()
    {
        var snapshot = SnapshotWithCpu(avgCpuMs: 10, avgElapsedMs: 450);
        var baselines = new StubBaselineService()
            .WithBaseline(Hash, Baseline(elapsedMean: 100, sampleCount: 12, stdDev: 20));

        var finding = Assert.Single(await Detect(snapshot, baselines));

        Assert.Contains($"Query hash: {Hash}", finding.Evidence);
        Assert.Contains("Database: AppDb", finding.Evidence);
        Assert.Contains("Regression dimension: avg_elapsed_ms", finding.Evidence);
        Assert.Contains("Current value: 450.0", finding.Evidence);
        Assert.Contains("Baseline mean (7d): 100.0", finding.Evidence);
        Assert.Contains("Regression multiplier: 4.5x", finding.Evidence);
        Assert.Contains("Baseline samples: 12", finding.Evidence);
        Assert.Contains("Baseline std dev: 20.0", finding.Evidence);
        Assert.Contains(finding.Evidence, e => e.StartsWith("SQL: ", StringComparison.Ordinal));
        Assert.NotEmpty(finding.LikelyCause);
        Assert.NotEmpty(finding.Recommendation);
        Assert.Equal(4, finding.ValidationSteps.Count);
    }

    [Theory]
    [InlineData(4, 1.0, Confidence.Low)]
    [InlineData(10, 10.0, Confidence.High)]
    [InlineData(10, 60.0, Confidence.Medium)]
    [InlineData(10, 150.0, Confidence.Low)]
    [InlineData(5, 10.0, Confidence.Medium)]
    [InlineData(5, 120.0, Confidence.Low)]
    public void Confidence_reflects_sample_count_and_relative_variance(
        int sampleCount,
        double stdDev,
        Confidence expected)
    {
        // A baseline that swings wildly cannot support a strong claim, however large the
        // multiplier is.
        Assert.Equal(
            expected,
            QueryRegressionDetector.ComputeConfidence(sampleCount, stdDev, baselineMean: 100));
    }

    [Fact]
    public void A_zero_baseline_mean_is_treated_as_no_variance_rather_than_a_division()
    {
        Assert.Equal(
            Confidence.High,
            QueryRegressionDetector.ComputeConfidence(sampleCount: 10, stdDev: 5, baselineMean: 0));
    }

    [Fact]
    public async Task Critical_and_high_regressions_are_flagged_for_human_review()
    {
        var snapshot = SnapshotWithCpu(avgCpuMs: 10, avgElapsedMs: 1000);
        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 100));

        Assert.True(Assert.Single(await Detect(snapshot, baselines)).HumanReviewRequired);
    }

    [Fact]
    public async Task A_medium_regression_does_not_demand_human_review()
    {
        var snapshot = SnapshotWithCpu(avgCpuMs: 10, avgElapsedMs: 250);
        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 100));

        Assert.False(Assert.Single(await Detect(snapshot, baselines)).HumanReviewRequired);
    }

    [Fact]
    public async Task A_query_from_another_database_is_never_reported_at_database_scope()
    {
        var snapshot = SnapshotBuilder.Snapshot(AnalysisScope.Database, "AppDb");
        var query = SnapshotBuilder.Cpu(10, database: "OtherDb", queryHash: Hash);
        query.AvgElapsedTimeMs = 10_000;
        snapshot.WithCpu(query);

        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 100));

        Assert.Empty(await Detect(snapshot, baselines));
    }

    [Fact]
    public async Task An_io_only_query_is_compared_on_reads_alone()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 50_000, queryHash: Hash));

        var baselines = new StubBaselineService()
            .WithBaseline(Hash, Baseline(elapsedMean: 100, cpuMean: 50, readsMean: 5000));

        var finding = Assert.Single(await Detect(snapshot, baselines));

        Assert.Contains("Regression dimension: avg_logical_reads", finding.Evidence);
    }

    [Fact]
    public async Task The_io_section_upgrades_reads_when_it_reports_more()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        var cpu = SnapshotBuilder.Cpu(10, queryHash: Hash);
        cpu.AvgElapsedTimeMs = 1;
        cpu.AvgLogicalReads = 100;
        snapshot.WithCpu(cpu);
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 50_000, queryHash: Hash));

        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(readsMean: 5000));

        var finding = Assert.Single(await Detect(snapshot, baselines));

        Assert.Contains("Current value: 50000.0", finding.Evidence);
    }

    [Fact]
    public async Task A_query_without_a_hash_cannot_be_matched_to_history()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        var cpu = SnapshotBuilder.Cpu(10, queryHash: null);
        cpu.AvgElapsedTimeMs = 10_000;
        snapshot.WithCpu(cpu);

        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 100));

        Assert.Empty(await Detect(snapshot, baselines));
    }

    [Fact]
    public async Task A_failed_collector_section_contributes_nothing()
    {
        var snapshot = SnapshotWithCpu(avgCpuMs: 10, avgElapsedMs: 10_000);
        snapshot.TopCpuQueries!.Metadata.Success = false;

        var baselines = new StubBaselineService().WithBaseline(Hash, Baseline(elapsedMean: 100));

        Assert.Empty(await Detect(snapshot, baselines));
    }
}
