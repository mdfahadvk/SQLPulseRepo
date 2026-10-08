using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Repositories;
using SQLPulseAPI.Net8.Services.History;

namespace SQLPulseAPI.Net8.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IHistoryRepository"/> for tests that need history without SQLite.
/// </summary>
/// <remarks>
/// Ordering and the duplicate-suppression rule mirror the SQLite implementation, so a
/// test that passes here is testing the same contract the real repository provides.
/// </remarks>
public sealed class InMemoryHistoryRepository : IHistoryRepository
{
    private readonly List<QueryMetricRecord> _metrics = [];
    private readonly List<WaitTotalsRecord> _waitTotals = [];
    private readonly HashSet<string> _snapshotIds = new(StringComparer.Ordinal);
    private readonly List<InvestigationAuditRecord> _audits = [];

    /// <summary>Set to make every read and write fail, simulating unavailable storage.</summary>
    public bool ThrowOnAccess { get; set; }

    /// <summary>Snapshots saved through this repository.</summary>
    public List<SnapshotRecord> Snapshots { get; } = [];

    /// <summary>Query metric samples saved through this repository.</summary>
    public IReadOnlyList<QueryMetricRecord> Metrics => _metrics;

    /// <summary>Wait totals saved through this repository.</summary>
    public IReadOnlyList<WaitTotalsRecord> WaitTotals => _waitTotals;

    /// <summary>Seeds samples directly, bypassing the save path.</summary>
    public void Seed(params QueryMetricRecord[] records) => _metrics.AddRange(records);

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task SaveSnapshotAsync(SnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        Guard();
        if (_snapshotIds.Add(snapshot.SnapshotId))
        {
            Snapshots.Add(snapshot);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> SnapshotExistsAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        Guard();
        return Task.FromResult(_snapshotIds.Contains(snapshotId));
    }

    /// <inheritdoc />
    public Task SaveQueryMetricsAsync(
        IReadOnlyCollection<QueryMetricRecord> metrics,
        CancellationToken cancellationToken = default)
    {
        Guard();
        lock (_metrics)
        {
            foreach (var metric in metrics)
            {
                var duplicate = _metrics.Any(m =>
                    string.Equals(m.SnapshotId, metric.SnapshotId, StringComparison.Ordinal)
                    && string.Equals(m.QueryHash, metric.QueryHash, StringComparison.Ordinal));

                if (!duplicate)
                {
                    _metrics.Add(metric);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveWaitTotalsAsync(WaitTotalsRecord totals, CancellationToken cancellationToken = default)
    {
        Guard();
        if (!_waitTotals.Any(w => string.Equals(w.SnapshotId, totals.SnapshotId, StringComparison.Ordinal)))
        {
            _waitTotals.Add(totals);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WaitTotalsRecord?> GetLatestWaitTotalsAsync(
        string server,
        DateTimeOffset? before = null,
        CancellationToken cancellationToken = default)
    {
        Guard();
        var match = _waitTotals
            .Where(w => string.Equals(w.Server, server, StringComparison.Ordinal))
            .Where(w => before is null || w.CollectedAt < before.Value)
            .OrderByDescending(w => w.CollectedAt)
            .FirstOrDefault();

        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueryMetricRecord>> GetQueryHistoryAsync(
        string queryHash,
        string database,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Guard();
        IReadOnlyList<QueryMetricRecord> result = Match(queryHash, database)
            .OrderByDescending(m => m.CollectedAt)
            .Take(limit)
            .ToList();

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueryMetricRecord>> GetQuerySamplesAsync(
        string queryHash,
        string database,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        Guard();
        IReadOnlyList<QueryMetricRecord> result = Match(queryHash, database)
            .Where(m => m.CollectedAt >= since)
            .OrderBy(m => m.CollectedAt)
            .ToList();

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task SaveInvestigationAuditAsync(
        InvestigationAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        Guard();
        _audits.Add(record);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InvestigationAuditRecord>> ListInvestigationAuditsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        Guard();
        IReadOnlyList<InvestigationAuditRecord> result = _audits
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToList();

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<InvestigationAuditRecord?> GetInvestigationAuditAsync(
        string investigationId,
        CancellationToken cancellationToken = default)
    {
        Guard();
        return Task.FromResult(_audits.FirstOrDefault(a =>
            string.Equals(a.InvestigationId, investigationId, StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public Task<int> RunRetentionCleanupAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        Guard();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var removed = _metrics.RemoveAll(m => m.CollectedAt < cutoff);
        _waitTotals.RemoveAll(w => w.CollectedAt < cutoff);
        return Task.FromResult(removed);
    }

    private IEnumerable<QueryMetricRecord> Match(string queryHash, string database) =>
        _metrics.Where(m =>
            string.Equals(m.QueryHash, queryHash, StringComparison.Ordinal)
            && string.Equals(m.Database, database, StringComparison.Ordinal));

    private void Guard()
    {
        if (ThrowOnAccess)
        {
            throw new InvalidOperationException("Simulated storage failure");
        }
    }
}

/// <summary>
/// <see cref="IBaselineService"/> returning pre-seeded baselines, so regression detection
/// can be tested without any storage at all.
/// </summary>
public sealed class StubBaselineService : IBaselineService
{
    private readonly Dictionary<string, QueryBaseline> _baselines = new(StringComparer.Ordinal);

    /// <summary>Set to make every lookup fail, simulating unavailable storage.</summary>
    public bool ThrowOnAccess { get; set; }

    /// <summary>Registers a baseline for a query hash.</summary>
    public StubBaselineService WithBaseline(string queryHash, QueryBaseline baseline)
    {
        _baselines[queryHash] = baseline;
        return this;
    }

    /// <inheritdoc />
    public Task<QueryBaseline> CalculateAsync(
        string queryHash,
        string database,
        string window = BaselineService.DefaultWindow,
        int? minSamples = null,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnAccess)
        {
            throw new InvalidOperationException("Simulated storage failure");
        }

        if (_baselines.TryGetValue(queryHash, out var baseline))
        {
            return Task.FromResult(baseline);
        }

        return Task.FromResult(new QueryBaseline
        {
            QueryHash = queryHash,
            Database = database,
            Window = window,
            SampleCount = 0,
            BaselineStatus = BaselineStatus.InsufficientData,
        });
    }

    /// <inheritdoc />
    public async Task<QueryBaseline?> GetForRegressionAsync(
        string queryHash,
        string database,
        string window = BaselineService.DefaultWindow,
        int? minSamples = null,
        CancellationToken cancellationToken = default)
    {
        var baseline = await CalculateAsync(queryHash, database, window, minSamples, cancellationToken);
        return baseline.BaselineStatus == BaselineStatus.Ready ? baseline : null;
    }
}

/// <summary>Records persistence calls without storing anything.</summary>
public sealed class RecordingHistoryService : IHistoryService
{
    /// <summary>Snapshots passed to <see cref="PersistSnapshotAsync"/>.</summary>
    public List<PerformanceSnapshot> Persisted { get; } = [];

    /// <inheritdoc />
    public Task PersistSnapshotAsync(PerformanceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Persisted.Add(snapshot);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> RunRetentionCleanupAsync(int? retentionDays = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

/// <summary>Leaves wait statistics untouched, for tests that do not exercise deltas.</summary>
public sealed class PassThroughWaitDeltaService : IWaitDeltaService
{
    /// <inheritdoc />
    public Task<WaitStatisticsResult> ApplyAsync(
        WaitStatisticsResult result,
        string server,
        string snapshotId,
        CancellationToken cancellationToken = default) => Task.FromResult(result);
}
