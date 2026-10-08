using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.History;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// How a collected snapshot becomes historical samples.
/// </summary>
public class HistoryServiceTests
{
    private static (HistoryService Service, InMemoryHistoryRepository Repository) Build()
    {
        var repository = new InMemoryHistoryRepository();
        var service = new HistoryService(
            repository,
            Options.Create(new HistoryOptions()),
            NullLogger<HistoryService>.Instance);

        return (service, repository);
    }

    [Fact]
    public async Task A_snapshot_header_and_its_samples_are_persisted()
    {
        var snapshot = SnapshotBuilder.Snapshot(AnalysisScope.Database, "AppDb");
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120));

        var (service, repository) = Build();
        await service.PersistSnapshotAsync(snapshot);

        var header = Assert.Single(repository.Snapshots);
        Assert.Equal(snapshot.SnapshotId, header.SnapshotId);
        Assert.Equal("database", header.Scope);
        Assert.Equal("AppDb", header.Database);
        Assert.Equal(SnapshotBuilder.Server, header.Server);

        var sample = Assert.Single(repository.Metrics);
        Assert.Equal("0xAAA", sample.QueryHash);
        Assert.Equal(120.0, sample.AvgCpuTimeMs);
    }

    [Fact]
    public async Task A_snapshot_that_was_already_stored_is_not_stored_again()
    {
        // Re-persisting inflates sample counts, which would make a query's baseline look
        // better-established than the evidence supports.
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120));

        var (service, repository) = Build();
        await service.PersistSnapshotAsync(snapshot);
        await service.PersistSnapshotAsync(snapshot);

        Assert.Single(repository.Snapshots);
        Assert.Single(repository.Metrics);
    }

    [Fact]
    public async Task A_storage_failure_never_breaks_the_caller()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120));

        var (service, repository) = Build();
        repository.ThrowOnAccess = true;

        await service.PersistSnapshotAsync(snapshot);
    }

    [Fact]
    public void A_query_in_both_sections_becomes_one_sample_carrying_both_signals()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120, queryHash: "0xAAA"));
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 5000, queryHash: "0xAAA"));

        var metrics = HistoryService.ExtractMetrics(snapshot);

        var sample = Assert.Single(metrics);
        Assert.Equal(120.0, sample.AvgCpuTimeMs);
        Assert.Equal(5000.0, sample.AvgLogicalReads);
        Assert.Equal(500.0, sample.AvgPhysicalReads);
    }

    [Fact]
    public void The_io_section_upgrades_logical_reads_when_it_reports_more()
    {
        // The I/O collector orders by reads, so its figure for the same query is the more
        // complete measurement. Keeping the smaller CPU-section value would understate
        // the query in its own baseline.
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120, queryHash: "0xAAA"));
        snapshot.TopCpuQueries!.Queries[0].TotalLogicalReads = 100;
        snapshot.TopCpuQueries.Queries[0].AvgLogicalReads = 1;

        snapshot.WithIo(SnapshotBuilder.Io(
            avgLogicalReads: 5000, queryHash: "0xAAA", totalLogicalReads: 500_000));

        var sample = Assert.Single(HistoryService.ExtractMetrics(snapshot));

        Assert.Equal(500_000, sample.TotalLogicalReads);
        Assert.Equal(5000.0, sample.AvgLogicalReads);
    }

    [Fact]
    public void The_io_section_does_not_downgrade_logical_reads()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120, queryHash: "0xAAA"));
        snapshot.TopCpuQueries!.Queries[0].TotalLogicalReads = 900_000;
        snapshot.TopCpuQueries.Queries[0].AvgLogicalReads = 9000;

        snapshot.WithIo(SnapshotBuilder.Io(
            avgLogicalReads: 10, queryHash: "0xAAA", totalLogicalReads: 1000));

        var sample = Assert.Single(HistoryService.ExtractMetrics(snapshot));

        Assert.Equal(900_000, sample.TotalLogicalReads);
        Assert.Equal(9000.0, sample.AvgLogicalReads);
    }

    [Fact]
    public void An_io_only_query_is_stored_with_unmeasured_cpu_left_at_zero()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 5000, queryHash: "0xBBB"));

        var sample = Assert.Single(HistoryService.ExtractMetrics(snapshot));

        Assert.Equal("0xBBB", sample.QueryHash);
        Assert.Equal(0.0, sample.AvgCpuTimeMs);
        Assert.Equal(5000.0, sample.AvgLogicalReads);
    }

    [Fact]
    public void Queries_without_a_hash_cannot_be_tracked_and_are_skipped()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120, queryHash: null));

        Assert.Empty(HistoryService.ExtractMetrics(snapshot));
    }

    [Fact]
    public void Only_the_first_two_hundred_characters_of_sql_are_retained()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120, sqlText: new string('x', 500)));

        var sample = Assert.Single(HistoryService.ExtractMetrics(snapshot));

        Assert.Equal(200, sample.SqlTextPreview!.Length);
    }

    [Fact]
    public void Empty_sql_is_stored_as_absent_rather_than_as_an_empty_string()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 120, sqlText: null));

        Assert.Null(Assert.Single(HistoryService.ExtractMetrics(snapshot)).SqlTextPreview);
    }

    [Fact]
    public void The_query_database_is_preferred_over_the_snapshot_database()
    {
        Assert.Equal("AppDb", HistoryService.ResolveDatabase("AppDb", "OtherDb"));
    }

    [Fact]
    public void The_snapshot_database_is_used_when_the_query_has_none()
    {
        Assert.Equal("OtherDb", HistoryService.ResolveDatabase(null, "OtherDb"));
    }

    [Fact]
    public void An_unresolvable_database_uses_a_marker_that_cannot_collide_with_a_real_name()
    {
        // Storage needs a non-null grouping key. The parenthesised marker is not a legal
        // SQL Server database name, so it can never merge with a real database.
        Assert.Equal(HistoryService.UnresolvedDatabase, HistoryService.ResolveDatabase(null, null));
        Assert.Equal("(unresolved)", HistoryService.UnresolvedDatabase);
    }

    [Fact]
    public async Task Retention_cleanup_reports_zero_instead_of_failing()
    {
        var (service, repository) = Build();
        repository.ThrowOnAccess = true;

        Assert.Equal(0, await service.RunRetentionCleanupAsync());
    }
}
