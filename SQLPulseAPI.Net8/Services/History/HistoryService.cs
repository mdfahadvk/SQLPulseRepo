using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Repositories;

namespace SQLPulseAPI.Net8.Services.History;

/// <summary>
/// Persists collected snapshots into SQLPulse's own historical storage.
/// </summary>
public interface IHistoryService
{
    /// <summary>
    /// Persists a snapshot and its query metrics. Never throws.
    /// </summary>
    Task PersistSnapshotAsync(PerformanceSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Deletes history older than the retention window and returns the number of samples removed.</summary>
    Task<int> RunRetentionCleanupAsync(int? retentionDays = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Snapshot persistence.
/// Ported from <c>HistoryService</c> in <c>app/storage/history_service.py</c>.
/// </summary>
public sealed class HistoryService(
    IHistoryRepository repository,
    IOptions<HistoryOptions> historyOptions,
    ILogger<HistoryService> logger) : IHistoryService
{
    /// <summary>
    /// Characters of SQL retained per sample.
    /// </summary>
    /// <remarks>
    /// A preview, not the statement: history is for spotting that a query changed shape,
    /// and storing full text would retain literal values from the monitored workload
    /// indefinitely.
    /// </remarks>
    private const int SqlPreviewLength = 200;

    /// <summary>
    /// Grouping key used when a sample's database cannot be determined.
    /// </summary>
    /// <remarks>
    /// Storage needs a non-null key to group samples by. The Python original uses
    /// <c>"unknown"</c>, which is a legal SQL Server database name and would silently
    /// merge with a real database called <c>unknown</c>. The parenthesised marker cannot
    /// be a database name, so the ambiguity is impossible.
    /// </remarks>
    public const string UnresolvedDatabase = "(unresolved)";

    private readonly HistoryOptions _history = historyOptions.Value;

    /// <inheritdoc />
    public async Task PersistSnapshotAsync(
        PerformanceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Re-persisting the same snapshot would duplicate every sample and inflate
            // baseline sample counts, making a query look better-established than it is.
            if (await repository.SnapshotExistsAsync(snapshot.SnapshotId, cancellationToken))
            {
                logger.LogDebug("Snapshot {SnapshotId} already persisted; skipping", snapshot.SnapshotId);
                return;
            }

            await repository.SaveSnapshotAsync(
                new SnapshotRecord
                {
                    SnapshotId = snapshot.SnapshotId,
                    CollectedAt = snapshot.CollectedAt,
                    Server = snapshot.ServerHost,
                    Scope = snapshot.Scope == AnalysisScope.Database ? "database" : "server",
                    Database = snapshot.Database,
                },
                cancellationToken);

            var metrics = ExtractMetrics(snapshot);
            if (metrics.Count > 0)
            {
                await repository.SaveQueryMetricsAsync(metrics, cancellationToken);
            }

            logger.LogDebug(
                "Persisted snapshot {SnapshotId} with {Count} query metric(s)",
                snapshot.SnapshotId,
                metrics.Count);
        }
        catch (Exception exception)
        {
            // History is a supporting capability. Losing a sample must never turn a
            // successful monitoring call into a failed one.
            logger.LogWarning(
                exception,
                "Non-fatal: failed to persist snapshot {SnapshotId}",
                snapshot.SnapshotId);
        }
    }

    /// <inheritdoc />
    public async Task<int> RunRetentionCleanupAsync(
        int? retentionDays = null,
        CancellationToken cancellationToken = default)
    {
        var days = retentionDays ?? _history.RetentionDays;

        try
        {
            var removed = await repository.RunRetentionCleanupAsync(days, cancellationToken);
            logger.LogInformation("Retention cleanup removed {Count} sample(s) older than {Days}d", removed, days);
            return removed;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Non-fatal: retention cleanup failed");
            return 0;
        }
    }

    /// <summary>
    /// Builds the per-query samples for a snapshot from the CPU and I/O sections.
    /// </summary>
    /// <remarks>
    /// The two sections describe the same queries from different angles, so a query
    /// appearing in both must become one sample carrying both sets of metrics rather
    /// than two partial samples that would each distort its baseline.
    /// </remarks>
    internal static List<QueryMetricRecord> ExtractMetrics(PerformanceSnapshot snapshot)
    {
        var byHash = new Dictionary<string, QueryMetricRecord>(StringComparer.Ordinal);
        var ordered = new List<QueryMetricRecord>();

        var cpuSection = snapshot.TopCpuQueries;
        if (cpuSection is not null && cpuSection.Metadata.Success)
        {
            foreach (var query in cpuSection.Queries)
            {
                if (string.IsNullOrEmpty(query.QueryHash) || byHash.ContainsKey(query.QueryHash))
                {
                    continue;
                }

                var record = new QueryMetricRecord
                {
                    SnapshotId = snapshot.SnapshotId,
                    CollectedAt = snapshot.CollectedAt,
                    Server = snapshot.ServerHost,
                    Database = ResolveDatabase(query.DatabaseName, snapshot.Database),
                    QueryHash = query.QueryHash,
                    SqlTextPreview = Preview(query.SqlText),
                    ExecutionCount = query.ExecutionCount,
                    TotalCpuTimeMs = query.TotalCpuTimeMs,
                    AvgCpuTimeMs = query.AvgCpuTimeMs,
                    TotalElapsedTimeMs = query.TotalElapsedTimeMs,
                    AvgElapsedTimeMs = query.AvgElapsedTimeMs,
                    TotalLogicalReads = query.TotalLogicalReads,
                    AvgLogicalReads = query.AvgLogicalReads,
                };

                byHash[query.QueryHash] = record;
                ordered.Add(record);
            }
        }

        var ioSection = snapshot.TopIoQueries;
        if (ioSection is not null && ioSection.Metadata.Success)
        {
            foreach (var query in ioSection.Queries)
            {
                if (string.IsNullOrEmpty(query.QueryHash))
                {
                    continue;
                }

                if (byHash.TryGetValue(query.QueryHash, out var existing))
                {
                    existing.TotalPhysicalReads = query.TotalPhysicalReads;
                    existing.AvgPhysicalReads = query.AvgPhysicalReads;
                    existing.TotalLogicalWrites = query.TotalLogicalWrites;
                    existing.AvgLogicalWrites = query.AvgLogicalWrites;

                    // The I/O collector orders by reads, so its figure for a query is the
                    // more complete one when it is larger. Taking the smaller value would
                    // understate the query in its own baseline.
                    if (query.TotalLogicalReads > existing.TotalLogicalReads)
                    {
                        existing.TotalLogicalReads = query.TotalLogicalReads;
                        existing.AvgLogicalReads = query.AvgLogicalReads;
                    }

                    continue;
                }

                // I/O-only query: CPU and elapsed metrics are genuinely unmeasured here
                // and stay at zero rather than being invented.
                var record = new QueryMetricRecord
                {
                    SnapshotId = snapshot.SnapshotId,
                    CollectedAt = snapshot.CollectedAt,
                    Server = snapshot.ServerHost,
                    Database = ResolveDatabase(query.DatabaseName, snapshot.Database),
                    QueryHash = query.QueryHash,
                    SqlTextPreview = Preview(query.SqlText),
                    ExecutionCount = query.ExecutionCount,
                    TotalElapsedTimeMs = query.TotalElapsedTimeMs,
                    TotalLogicalReads = query.TotalLogicalReads,
                    AvgLogicalReads = query.AvgLogicalReads,
                    TotalPhysicalReads = query.TotalPhysicalReads,
                    AvgPhysicalReads = query.AvgPhysicalReads,
                    TotalLogicalWrites = query.TotalLogicalWrites,
                    AvgLogicalWrites = query.AvgLogicalWrites,
                };

                byHash[query.QueryHash] = record;
                ordered.Add(record);
            }
        }

        return ordered;
    }

    /// <summary>
    /// Chooses the database a sample is filed under: the query's own database, else the
    /// snapshot's, else <see cref="UnresolvedDatabase"/>.
    /// </summary>
    internal static string ResolveDatabase(string? queryDatabase, string? snapshotDatabase)
    {
        if (!string.IsNullOrEmpty(queryDatabase))
        {
            return queryDatabase;
        }

        return string.IsNullOrEmpty(snapshotDatabase) ? UnresolvedDatabase : snapshotDatabase;
    }

    private static string? Preview(string? sqlText)
    {
        if (string.IsNullOrEmpty(sqlText))
        {
            return null;
        }

        return sqlText.Length <= SqlPreviewLength ? sqlText : sqlText[..SqlPreviewLength];
    }
}
