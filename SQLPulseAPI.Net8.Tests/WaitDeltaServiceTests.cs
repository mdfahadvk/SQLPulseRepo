using Microsoft.Extensions.Logging.Abstractions;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.History;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Wait-counter deltas. <c>sys.dm_os_wait_stats</c> is cumulative since the last restart,
/// so these tests are mostly about refusing to produce a number when one would mislead.
/// </summary>
public class WaitDeltaServiceTests
{
    private const string Server = "sql01";

    private static readonly DateTimeOffset Epoch = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private static WaitStatisticsResult Current(
        long waitMs = 5000,
        long tasks = 500,
        long signalMs = 800,
        long resourceMs = 4200,
        DateTimeOffset? collectedAt = null,
        DateTimeOffset? startTime = null,
        bool success = true,
        bool withTotals = true) => new()
        {
            Metadata = new CollectorMetadata
            {
                CollectorName = "wait_statistics",
                ServerHost = Server,
                Success = success,
            },
            CollectedAt = collectedAt ?? Epoch.AddMinutes(10),
            SqlServerStartTime = startTime,
            Totals = withTotals
                ? new WaitStatisticsTotals
                {
                    WaitingTasksCount = tasks,
                    WaitTimeMs = waitMs,
                    SignalWaitTimeMs = signalMs,
                    ResourceWaitTimeMs = resourceMs,
                }
                : null,
        };

    private static WaitTotalsRecord Previous(
        long waitMs = 1000,
        long tasks = 100,
        long signalMs = 200,
        long resourceMs = 800,
        DateTimeOffset? collectedAt = null,
        DateTimeOffset? startTime = null) => new()
        {
            SnapshotId = "snap-previous",
            CollectedAt = collectedAt ?? Epoch,
            Server = Server,
            SqlServerStartTime = startTime,
            WaitingTasksCount = tasks,
            WaitTimeMs = waitMs,
            SignalWaitTimeMs = signalMs,
            ResourceWaitTimeMs = resourceMs,
        };

    [Fact]
    public void Two_readings_produce_the_change_between_them()
    {
        var (delta, status) = WaitDeltaService.ComputeDelta(Current(), Previous());

        Assert.Equal(WaitDeltaStatus.Available, status);
        Assert.Equal(4000, delta!.WaitTimeMs);
        Assert.Equal(400, delta.WaitingTasksCount);
        Assert.Equal(600, delta.SignalWaitTimeMs);
        Assert.Equal(3400, delta.ResourceWaitTimeMs);
        Assert.Equal(600.0, delta.IntervalSeconds);
        Assert.Equal(Epoch, delta.PreviousCollectedAt);
    }

    [Fact]
    public void The_first_reading_on_a_server_has_nothing_to_compare_against()
    {
        var (delta, status) = WaitDeltaService.ComputeDelta(Current(), previous: null);

        Assert.Null(delta);
        Assert.Equal(WaitDeltaStatus.InsufficientPreviousData, status);
    }

    [Fact]
    public void A_reading_without_totals_cannot_produce_a_delta()
    {
        var (delta, status) = WaitDeltaService.ComputeDelta(Current(withTotals: false), Previous());

        Assert.Null(delta);
        Assert.Equal(WaitDeltaStatus.InsufficientPreviousData, status);
    }

    [Fact]
    public void A_changed_counter_epoch_means_the_server_restarted()
    {
        // Subtracting across a restart would report a large negative or a meaningless
        // positive value, so the reason is reported instead.
        var (delta, status) = WaitDeltaService.ComputeDelta(
            Current(startTime: Epoch.AddDays(-1)),
            Previous(startTime: Epoch.AddDays(-10)));

        Assert.Null(delta);
        Assert.Equal(WaitDeltaStatus.ServerRestarted, status);
    }

    [Fact]
    public void An_unchanged_counter_epoch_still_produces_a_delta()
    {
        var (delta, status) = WaitDeltaService.ComputeDelta(
            Current(startTime: Epoch.AddDays(-10)),
            Previous(startTime: Epoch.AddDays(-10)));

        Assert.Equal(WaitDeltaStatus.Available, status);
        Assert.NotNull(delta);
    }

    [Fact]
    public void Counters_that_went_backwards_void_the_comparison()
    {
        // Cumulative counters can only decrease if they were reset, for example by
        // DBCC SQLPERF, even when the reported start time did not change.
        var (delta, status) = WaitDeltaService.ComputeDelta(Current(waitMs: 10), Previous(waitMs: 1000));

        Assert.Null(delta);
        Assert.Equal(WaitDeltaStatus.ServerRestarted, status);
    }

    [Fact]
    public void The_interval_is_rounded_to_milliseconds()
    {
        var previous = Previous(collectedAt: Epoch);
        var current = Current(collectedAt: Epoch.AddMilliseconds(1234.5678));

        var (delta, _) = WaitDeltaService.ComputeDelta(current, previous);

        Assert.Equal(1.235, delta!.IntervalSeconds);
    }

    [Fact]
    public async Task Applying_a_delta_stores_the_current_reading_for_next_time()
    {
        var repository = new InMemoryHistoryRepository();
        var service = new WaitDeltaService(repository, NullLogger<WaitDeltaService>.Instance);

        var result = await service.ApplyAsync(Current(), Server, "snap-1");

        Assert.Equal(WaitDeltaStatus.InsufficientPreviousData, result.DeltaStatus);
        var stored = Assert.Single(repository.WaitTotals);
        Assert.Equal("snap-1", stored.SnapshotId);
        Assert.Equal(5000, stored.WaitTimeMs);
    }

    [Fact]
    public async Task A_second_reading_is_compared_against_the_first()
    {
        var repository = new InMemoryHistoryRepository();
        var service = new WaitDeltaService(repository, NullLogger<WaitDeltaService>.Instance);

        await service.ApplyAsync(Current(waitMs: 1000, collectedAt: Epoch), Server, "snap-1");
        var second = await service.ApplyAsync(
            Current(waitMs: 5000, collectedAt: Epoch.AddMinutes(10)), Server, "snap-2");

        Assert.Equal(WaitDeltaStatus.Available, second.DeltaStatus);
        Assert.Equal(4000, second.Delta!.WaitTimeMs);
    }

    [Fact]
    public async Task A_storage_failure_reports_missing_history_rather_than_breaking_the_snapshot()
    {
        var repository = new InMemoryHistoryRepository { ThrowOnAccess = true };
        var service = new WaitDeltaService(repository, NullLogger<WaitDeltaService>.Instance);

        var result = await service.ApplyAsync(Current(), Server, "snap-1");

        Assert.Null(result.Delta);
        Assert.Equal(WaitDeltaStatus.InsufficientPreviousData, result.DeltaStatus);
    }

    [Fact]
    public async Task A_failed_collection_is_left_untouched()
    {
        var repository = new InMemoryHistoryRepository();
        var service = new WaitDeltaService(repository, NullLogger<WaitDeltaService>.Instance);

        await service.ApplyAsync(Current(success: false), Server, "snap-1");

        Assert.Empty(repository.WaitTotals);
    }
}
