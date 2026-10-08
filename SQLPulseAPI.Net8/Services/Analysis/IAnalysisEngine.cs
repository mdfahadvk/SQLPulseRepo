using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Services.Analysis;

/// <summary>Parameters for an analysis run.</summary>
/// <param name="Scope">Instance-wide or single-database.</param>
/// <param name="Database">Target database. Required when <paramref name="Scope"/> is database.</param>
/// <param name="LongRunningThresholdMs">Elapsed-time threshold used when collecting the snapshot.</param>
/// <param name="TopN">Row limit for the plan-cache collectors.</param>
/// <param name="WaitStatsTopN">Row limit for wait statistics.</param>
/// <remarks>
/// A record class rather than a struct, so the defaults always apply — a struct's
/// implicit parameterless constructor would silently zero the row limits.
/// </remarks>
public sealed record AnalysisRequest(
    AnalysisScope Scope = AnalysisScope.Server,
    string? Database = null,
    int LongRunningThresholdMs = 5000,
    int TopN = 20,
    int WaitStatsTopN = 25);

/// <summary>
/// Runs the deterministic detectors over a snapshot and assembles the analysis result.
/// Ported from <c>app/detectors/engine.py</c>.
/// </summary>
/// <remarks>
/// No LLM is involved: the same snapshot and thresholds always produce the same findings.
/// </remarks>
public interface IAnalysisEngine
{
    /// <summary>Collects a snapshot and analyses it.</summary>
    Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyses an already-collected snapshot. Exposed so analysis can be tested and
    /// reasoned about without collecting from SQL Server.
    /// </summary>
    /// <remarks>
    /// Asynchronous because regression detection reads stored history; the other
    /// detectors work purely from the snapshot.
    /// </remarks>
    Task<AnalysisResult> RunAnalysisAsync(
        PerformanceSnapshot snapshot,
        AnalysisScope scope,
        string? database,
        CancellationToken cancellationToken = default);
}
