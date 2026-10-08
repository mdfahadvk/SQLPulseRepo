using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.History;

namespace SQLPulseAPI.Net8.Repositories;

/// <summary>
/// Local historical storage for snapshots, query metrics, wait totals and the
/// investigation audit trail.
/// </summary>
/// <remarks>
/// This is SQLPulse's own storage and is entirely separate from the monitored SQL
/// Server, which SQLPulse only ever reads. The abstraction exists so the SQLite
/// implementation can be replaced without touching collector or detector logic.
/// </remarks>
public interface IHistoryRepository
{
    /// <summary>Creates the schema if it does not already exist.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a snapshot header.</summary>
    Task SaveSnapshotAsync(SnapshotRecord snapshot, CancellationToken cancellationToken = default);

    /// <summary>Returns whether a snapshot has already been stored.</summary>
    Task<bool> SnapshotExistsAsync(string snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Persists query metric samples belonging to a snapshot.</summary>
    Task SaveQueryMetricsAsync(IReadOnlyCollection<QueryMetricRecord> metrics, CancellationToken cancellationToken = default);

    /// <summary>Persists server-wide wait totals belonging to a snapshot.</summary>
    Task SaveWaitTotalsAsync(WaitTotalsRecord totals, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent wait totals for a server, or <see langword="null"/> when none exist.
    /// </summary>
    /// <param name="server">Server host the observations belong to.</param>
    /// <param name="before">
    /// When supplied, only observations strictly earlier than this are considered, so a
    /// reading is never compared against itself.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WaitTotalsRecord?> GetLatestWaitTotalsAsync(
        string server,
        DateTimeOffset? before = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent samples for a query, newest first.</summary>
    Task<IReadOnlyList<QueryMetricRecord>> GetQueryHistoryAsync(
        string queryHash,
        string database,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Returns samples for a query inside a time window.</summary>
    Task<IReadOnlyList<QueryMetricRecord>> GetQuerySamplesAsync(
        string queryHash,
        string database,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    /// <summary>Appends an investigation audit record.</summary>
    Task SaveInvestigationAuditAsync(InvestigationAuditRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent audit records, newest first.</summary>
    Task<IReadOnlyList<InvestigationAuditRecord>> ListInvestigationAuditsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a single audit record, or <see langword="null"/> when unknown.</summary>
    Task<InvestigationAuditRecord?> GetInvestigationAuditAsync(
        string investigationId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes records older than the retention window and returns the number removed.</summary>
    Task<int> RunRetentionCleanupAsync(int retentionDays, CancellationToken cancellationToken = default);
}
