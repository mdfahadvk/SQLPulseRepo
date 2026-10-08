using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Repositories;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Round-trip persistence, ordering, duplicate suppression, retention and concurrent
/// access for SQLPulse's own SQLite storage.
/// </summary>
/// <remarks>
/// Each test gets its own database file, so nothing here touches a live SQL Server or a
/// shared fixture.
/// </remarks>
public sealed class HistoryRepositoryTests : IDisposable
{
    private const string Server = "sql01";
    private const string Database = "AppDb";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"sqlpulse-history-{Guid.NewGuid():N}.db");

    private readonly SqliteHistoryRepository _repository;

    public HistoryRepositoryTests()
    {
        _repository = new SqliteHistoryRepository(
            Options.Create(new HistoryOptions { DatabasePath = _path }),
            NullLogger<SqliteHistoryRepository>.Instance);

        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static QueryMetricRecord Metric(
        string snapshotId,
        string queryHash = "0xABC",
        string database = Database,
        DateTimeOffset? collectedAt = null) => new()
    {
        SnapshotId = snapshotId,
        CollectedAt = collectedAt ?? DateTimeOffset.UtcNow,
        Server = Server,
        Database = database,
        QueryHash = queryHash,
        SqlTextPreview = "SELECT 1",
        ExecutionCount = 100,
        TotalCpuTimeMs = 5000,
        AvgCpuTimeMs = 50.0,
        TotalElapsedTimeMs = 9000,
        AvgElapsedTimeMs = 90.0,
        TotalLogicalReads = 20_000,
        AvgLogicalReads = 200.0,
        TotalPhysicalReads = 300,
        AvgPhysicalReads = 3.0,
        TotalLogicalWrites = 40,
        AvgLogicalWrites = 0.4,
    };

    [Fact]
    public async Task Every_persisted_column_survives_a_round_trip()
    {
        var collectedAt = new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero);
        await _repository.SaveQueryMetricsAsync([Metric("snap-1", collectedAt: collectedAt)]);

        var sample = Assert.Single(await _repository.GetQueryHistoryAsync("0xABC", Database, 10));

        Assert.Equal("snap-1", sample.SnapshotId);
        Assert.Equal(collectedAt, sample.CollectedAt);
        Assert.Equal(Server, sample.Server);
        Assert.Equal(Database, sample.Database);
        Assert.Equal("0xABC", sample.QueryHash);
        Assert.Equal("SELECT 1", sample.SqlTextPreview);
        Assert.Equal(100, sample.ExecutionCount);
        Assert.Equal(5000, sample.TotalCpuTimeMs);
        Assert.Equal(50.0, sample.AvgCpuTimeMs);
        Assert.Equal(9000, sample.TotalElapsedTimeMs);
        Assert.Equal(90.0, sample.AvgElapsedTimeMs);
        Assert.Equal(20_000, sample.TotalLogicalReads);
        Assert.Equal(200.0, sample.AvgLogicalReads);
        Assert.Equal(300, sample.TotalPhysicalReads);
        Assert.Equal(3.0, sample.AvgPhysicalReads);
        Assert.Equal(40, sample.TotalLogicalWrites);
        Assert.Equal(0.4, sample.AvgLogicalWrites);
    }

    [Fact]
    public async Task The_same_query_in_the_same_snapshot_is_stored_once()
    {
        // Re-persisting would double-count the sample and make the query look
        // better-established in its own baseline than it actually is.
        await _repository.SaveQueryMetricsAsync([Metric("snap-1")]);
        await _repository.SaveQueryMetricsAsync([Metric("snap-1")]);

        Assert.Single(await _repository.GetQueryHistoryAsync("0xABC", Database, 10));
    }

    [Fact]
    public async Task History_is_returned_newest_first_and_respects_the_limit()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-5);
        for (var i = 0; i < 5; i++)
        {
            await _repository.SaveQueryMetricsAsync([Metric($"snap-{i}", collectedAt: start.AddMinutes(i))]);
        }

        var samples = await _repository.GetQueryHistoryAsync("0xABC", Database, 3);

        Assert.Equal(3, samples.Count);
        Assert.Equal("snap-4", samples[0].SnapshotId);
        Assert.Equal("snap-3", samples[1].SnapshotId);
        Assert.Equal("snap-2", samples[2].SnapshotId);
    }

    [Fact]
    public async Task Baseline_samples_are_returned_oldest_first_and_inside_the_window()
    {
        var now = DateTimeOffset.UtcNow;
        await _repository.SaveQueryMetricsAsync([Metric("old", collectedAt: now.AddDays(-30))]);
        await _repository.SaveQueryMetricsAsync([Metric("recent-2", collectedAt: now.AddHours(-1))]);
        await _repository.SaveQueryMetricsAsync([Metric("recent-1", collectedAt: now.AddHours(-2))]);

        var samples = await _repository.GetQuerySamplesAsync("0xABC", Database, now.AddDays(-7));

        Assert.Equal(2, samples.Count);
        Assert.Equal("recent-1", samples[0].SnapshotId);
        Assert.Equal("recent-2", samples[1].SnapshotId);
    }

    [Fact]
    public async Task History_is_isolated_per_database()
    {
        await _repository.SaveQueryMetricsAsync([Metric("snap-1", database: "AppDb")]);
        await _repository.SaveQueryMetricsAsync([Metric("snap-2", database: "OtherDb")]);

        var samples = await _repository.GetQueryHistoryAsync("0xABC", "AppDb", 10);

        Assert.Equal("AppDb", Assert.Single(samples).Database);
    }

    [Fact]
    public async Task A_snapshot_is_reported_as_existing_only_after_it_is_saved()
    {
        Assert.False(await _repository.SnapshotExistsAsync("snap-1"));

        await _repository.SaveSnapshotAsync(new SnapshotRecord
        {
            SnapshotId = "snap-1",
            CollectedAt = DateTimeOffset.UtcNow,
            Server = Server,
            Scope = "server",
        });

        Assert.True(await _repository.SnapshotExistsAsync("snap-1"));
    }

    [Fact]
    public async Task Wait_totals_lookup_never_returns_the_reading_being_compared()
    {
        var first = DateTimeOffset.UtcNow.AddMinutes(-10);
        var second = DateTimeOffset.UtcNow;

        await _repository.SaveWaitTotalsAsync(new WaitTotalsRecord
        {
            SnapshotId = "snap-1",
            CollectedAt = first,
            Server = Server,
            WaitTimeMs = 100,
        });

        await _repository.SaveWaitTotalsAsync(new WaitTotalsRecord
        {
            SnapshotId = "snap-2",
            CollectedAt = second,
            Server = Server,
            WaitTimeMs = 200,
        });

        var previous = await _repository.GetLatestWaitTotalsAsync(Server, before: second);

        Assert.Equal("snap-1", previous!.SnapshotId);
        Assert.Equal(100, previous.WaitTimeMs);
    }

    [Fact]
    public async Task Wait_totals_are_isolated_per_server()
    {
        await _repository.SaveWaitTotalsAsync(new WaitTotalsRecord
        {
            SnapshotId = "snap-1",
            CollectedAt = DateTimeOffset.UtcNow,
            Server = "other-server",
            WaitTimeMs = 100,
        });

        Assert.Null(await _repository.GetLatestWaitTotalsAsync(Server));
    }

    [Fact]
    public async Task Retention_cleanup_removes_only_expired_samples_and_reports_the_count()
    {
        var now = DateTimeOffset.UtcNow;
        await _repository.SaveQueryMetricsAsync([Metric("old-1", collectedAt: now.AddDays(-40))]);
        await _repository.SaveQueryMetricsAsync([Metric("old-2", collectedAt: now.AddDays(-31))]);
        await _repository.SaveQueryMetricsAsync([Metric("fresh", collectedAt: now.AddDays(-1))]);

        var removed = await _repository.RunRetentionCleanupAsync(30);

        Assert.Equal(2, removed);
        Assert.Equal("fresh", Assert.Single(await _repository.GetQueryHistoryAsync("0xABC", Database, 10)).SnapshotId);
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_all_succeed()
    {
        // The point of WAL plus a busy timeout: several ASP.NET Core requests can be
        // persisting snapshots while others read baselines, without a writer failing
        // outright because another connection held the database.
        const int writers = 32;
        const int metricsPerWriter = 5;

        var tasks = new List<Task>();

        for (var w = 0; w < writers; w++)
        {
            var writer = w;
            tasks.Add(Task.Run(async () =>
            {
                var batch = Enumerable.Range(0, metricsPerWriter)
                    .Select(i => Metric($"snap-{writer}", queryHash: $"0x{i}"))
                    .ToArray();

                await _repository.SaveQueryMetricsAsync(batch);
            }));

            tasks.Add(Task.Run(async () =>
            {
                await _repository.GetQueryHistoryAsync("0x0", Database, 100);
                await _repository.GetQuerySamplesAsync("0x0", Database, DateTimeOffset.UtcNow.AddDays(-7));
            }));
        }

        // Any lock contention surfaces here as a SqliteException rather than being
        // silently swallowed.
        await Task.WhenAll(tasks);

        for (var i = 0; i < metricsPerWriter; i++)
        {
            var samples = await _repository.GetQueryHistoryAsync($"0x{i}", Database, 1000);
            Assert.Equal(writers, samples.Count);
        }
    }
}
