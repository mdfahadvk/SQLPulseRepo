using System.ComponentModel.DataAnnotations;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// Slow-query monitoring and workload classification settings.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SlowQueryThresholdMs"/> and <see cref="ApplicationTimeoutMs"/> answer
/// different questions and must never be conflated:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="SlowQueryThresholdMs"/> — "which queries are worth looking at?"</description></item>
///   <item><description><see cref="ApplicationTimeoutMs"/> — "at what duration does the client give up?"</description></item>
/// </list>
/// <para>
/// Only the application owns the second number. SQLPulse cannot observe it, cannot
/// derive it from a monitoring filter, and must not guess it. When it is not
/// configured, timeout classification is simply unavailable.
/// </para>
/// </remarks>
public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    /// <summary>Duration above which a query is a <c>SLOW_QUERY_CANDIDATE</c>.</summary>
    [Range(1, int.MaxValue)]
    public int SlowQueryThresholdMs { get; set; } = 5_000;

    /// <summary>
    /// Explicitly configured application/client command timeout in milliseconds.
    /// Leave unset when unknown — never inferred from slow-query filters.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? ApplicationTimeoutMs { get; set; }

    /// <summary>
    /// Comma-separated program/application names treated as interactive application
    /// workload. Matching is case-insensitive substring matching against
    /// <c>program_name</c>.
    /// </summary>
    public string WorkloadApplicationPrograms { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated program/application names treated as background or scheduled
    /// workload. Matching is case-insensitive substring matching against
    /// <c>program_name</c>.
    /// </summary>
    public string WorkloadBackgroundPrograms { get; set; } = string.Empty;

    /// <summary>Parsed entries of <see cref="WorkloadApplicationPrograms"/>.</summary>
    public IReadOnlyList<string> ApplicationProgramNames => SplitCsv(WorkloadApplicationPrograms);

    /// <summary>Parsed entries of <see cref="WorkloadBackgroundPrograms"/>.</summary>
    public IReadOnlyList<string> BackgroundProgramNames => SplitCsv(WorkloadBackgroundPrograms);

    /// <summary>True when timeout classification can be performed at all.</summary>
    public bool TimeoutClassificationAvailable => ApplicationTimeoutMs is > 0;

    private static IReadOnlyList<string> SplitCsv(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
