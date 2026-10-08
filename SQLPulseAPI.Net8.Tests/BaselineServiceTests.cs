using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Services.History;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Window handling and the insufficient-data contract of the baseline service.
/// </summary>
public class BaselineServiceTests
{
    private const string QueryHash = "0xABC";
    private const string Database = "AppDb";

    private static (BaselineService Service, InMemoryHistoryRepository Repository) Build(int minSamples = 5)
    {
        var repository = new InMemoryHistoryRepository();
        var service = new BaselineService(
            repository,
            Options.Create(new HistoryOptions { BaselineMinSamples = minSamples }),
            NullLogger<BaselineService>.Instance);

        return (service, repository);
    }

    private static QueryMetricRecord Sample(double cpuMs, DateTimeOffset collectedAt) => new()
    {
        SnapshotId = Guid.NewGuid().ToString(),
        CollectedAt = collectedAt,
        Server = "sql01",
        Database = Database,
        QueryHash = QueryHash,
        ExecutionCount = 10,
        AvgCpuTimeMs = cpuMs,
        AvgElapsedTimeMs = cpuMs * 2,
        AvgLogicalReads = cpuMs * 10,
        AvgPhysicalReads = cpuMs,
    };

    private static void Seed(InMemoryHistoryRepository repository, int count, double cpuMs = 100)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            repository.Seed(Sample(cpuMs, now.AddMinutes(-i)));
        }
    }

    [Fact]
    public async Task Fewer_samples_than_the_minimum_reports_insufficient_data()
    {
        var (service, repository) = Build(minSamples: 5);
        Seed(repository, 4);

        var baseline = await service.CalculateAsync(QueryHash, Database);

        Assert.Equal(BaselineStatus.InsufficientData, baseline.BaselineStatus);
        Assert.Equal(4, baseline.SampleCount);
    }

    [Fact]
    public async Task Insufficient_data_carries_no_statistics_at_all()
    {
        // Zero-valued statistics would be read as a real measurement of zero, and the
        // regression detector would then divide by that zero-mean baseline.
        var (service, repository) = Build(minSamples: 5);
        Seed(repository, 2);

        var baseline = await service.CalculateAsync(QueryHash, Database);

        Assert.Null(baseline.AvgCpuMs);
        Assert.Null(baseline.AvgElapsedMs);
        Assert.Null(baseline.AvgLogicalReads);
        Assert.Null(baseline.AvgPhysicalReads);
        Assert.Null(baseline.ExecutionCount);
    }

    [Fact]
    public async Task Reaching_the_minimum_produces_a_ready_baseline_with_every_metric()
    {
        var (service, repository) = Build(minSamples: 5);
        Seed(repository, 5, cpuMs: 100);

        var baseline = await service.CalculateAsync(QueryHash, Database);

        Assert.Equal(BaselineStatus.Ready, baseline.BaselineStatus);
        Assert.Equal(5, baseline.SampleCount);
        Assert.Equal(100.0, baseline.AvgCpuMs!.Mean);
        Assert.Equal(200.0, baseline.AvgElapsedMs!.Mean);
        Assert.Equal(1000.0, baseline.AvgLogicalReads!.Mean);
        Assert.Equal(100.0, baseline.AvgPhysicalReads!.Mean);
        Assert.Equal(10.0, baseline.ExecutionCount!.Mean);
    }

    [Fact]
    public async Task An_explicit_minimum_overrides_configuration()
    {
        var (service, repository) = Build(minSamples: 5);
        Seed(repository, 3);

        var baseline = await service.CalculateAsync(QueryHash, Database, minSamples: 3);

        Assert.Equal(BaselineStatus.Ready, baseline.BaselineStatus);
    }

    [Fact]
    public async Task Samples_outside_the_window_are_not_counted()
    {
        var (service, repository) = Build(minSamples: 1);
        repository.Seed(Sample(100, DateTimeOffset.UtcNow.AddHours(-5)));

        var baseline = await service.CalculateAsync(QueryHash, Database, window: "1h");

        Assert.Equal(0, baseline.SampleCount);
        Assert.Equal(BaselineStatus.InsufficientData, baseline.BaselineStatus);
    }

    [Theory]
    [InlineData("1h")]
    [InlineData("24h")]
    [InlineData("7d")]
    [InlineData("30d")]
    public async Task Supported_windows_are_accepted(string window)
    {
        var (service, _) = Build();

        var baseline = await service.CalculateAsync(QueryHash, Database, window);

        Assert.Equal(window, baseline.Window);
    }

    [Fact]
    public async Task An_unsupported_window_is_rejected_rather_than_silently_defaulted()
    {
        var (service, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CalculateAsync(QueryHash, Database, "90d"));
    }

    [Fact]
    public async Task Regression_lookup_returns_nothing_unless_the_baseline_is_ready()
    {
        var (service, repository) = Build(minSamples: 5);
        Seed(repository, 4);

        Assert.Null(await service.GetForRegressionAsync(QueryHash, Database));
    }

    [Fact]
    public async Task Regression_lookup_returns_a_ready_baseline()
    {
        var (service, repository) = Build(minSamples: 5);
        Seed(repository, 6);

        var baseline = await service.GetForRegressionAsync(QueryHash, Database);

        Assert.NotNull(baseline);
        Assert.Equal(BaselineStatus.Ready, baseline.BaselineStatus);
    }

    [Fact]
    public async Task Regression_lookup_survives_a_storage_failure()
    {
        // A missing or broken history store must not remove the findings that do not
        // depend on history at all.
        var (service, repository) = Build();
        repository.ThrowOnAccess = true;

        Assert.Null(await service.GetForRegressionAsync(QueryHash, Database));
    }
}
