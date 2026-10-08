using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Services.Performance;

/// <summary>Parameters for a snapshot collection.</summary>
/// <param name="Scope">Instance-wide or single-database.</param>
/// <param name="Database">Target database. Required when <paramref name="Scope"/> is database.</param>
/// <param name="LongRunningThresholdMs">Elapsed-time threshold for long-running queries.</param>
/// <param name="TopN">Row limit for the plan-cache collectors.</param>
/// <param name="WaitStatsTopN">Row limit for wait statistics.</param>
/// <remarks>
/// Deliberately a record class, not a struct: a struct's implicit parameterless
/// constructor bypasses these defaults and would silently produce a row limit of zero.
/// </remarks>
public sealed record PerformanceSnapshotRequest(
    AnalysisScope Scope = AnalysisScope.Server,
    string? Database = null,
    int LongRunningThresholdMs = 5000,
    int TopN = 20,
    int WaitStatsTopN = 25);

/// <summary>
/// Runs every collector and assembles a single snapshot.
/// Ported from <c>collect_performance_snapshot</c> in <c>app/collectors/snapshot.py</c>.
/// </summary>
public interface IPerformanceSnapshotService
{
    /// <summary>
    /// Collects a snapshot. Individual collector failures are recorded in that
    /// collector's metadata; the snapshot itself always succeeds.
    /// </summary>
    Task<PerformanceSnapshot> CollectAsync(
        PerformanceSnapshotRequest request,
        CancellationToken cancellationToken = default);
}
