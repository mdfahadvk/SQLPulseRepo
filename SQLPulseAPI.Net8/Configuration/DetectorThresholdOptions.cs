using System.ComponentModel.DataAnnotations;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// Thresholds controlling when detectors raise findings.
/// Ported from <c>DetectorThresholds</c> in <c>app/detectors/thresholds.py</c>.
/// </summary>
/// <remarks>
/// Bound from the <c>Detectors</c> configuration section; the legacy Python
/// <c>DETECTOR_*</c> environment variables map onto the same keys.
/// </remarks>
public sealed class DetectorThresholdOptions
{
    public const string SectionName = "Detectors";

    // --- Long-running query thresholds ---

    /// <summary>Elapsed milliseconds for medium severity.</summary>
    [Range(1, int.MaxValue)]
    public int LongRunningWarningMs { get; set; } = 10_000;

    /// <summary>Elapsed milliseconds for high severity.</summary>
    [Range(1, int.MaxValue)]
    public int LongRunningHighMs { get; set; } = 30_000;

    /// <summary>Elapsed milliseconds for critical severity.</summary>
    [Range(1, int.MaxValue)]
    public int LongRunningCriticalMs { get; set; } = 120_000;

    /// <summary>Concurrent critical-tier queries required before the finding escalates to critical.</summary>
    [Range(1, int.MaxValue)]
    public int LongRunningCriticalCount { get; set; } = 3;

    // --- Blocking thresholds ---

    /// <summary>Wait milliseconds for medium severity blocking.</summary>
    [Range(1, int.MaxValue)]
    public int BlockingWarningWaitMs { get; set; } = 5_000;

    /// <summary>Wait milliseconds for high severity blocking.</summary>
    [Range(1, int.MaxValue)]
    public int BlockingHighWaitMs { get; set; } = 30_000;

    /// <summary>Wait milliseconds for critical severity blocking.</summary>
    [Range(1, int.MaxValue)]
    public int BlockingCriticalWaitMs { get; set; } = 60_000;

    /// <summary>Sessions involved in a blocking chain before it is treated as critical.</summary>
    [Range(1, int.MaxValue)]
    public int BlockingCriticalChainCount { get; set; } = 3;

    // --- High CPU thresholds ---

    /// <summary>Average CPU milliseconds per execution for high severity.</summary>
    [Range(0.0, double.MaxValue)]
    public double CpuHighAvgMs { get; set; } = 5_000.0;

    /// <summary>Average CPU milliseconds per execution for critical severity.</summary>
    [Range(0.0, double.MaxValue)]
    public double CpuCriticalAvgMs { get; set; } = 30_000.0;

    /// <summary>Total CPU milliseconds for high severity.</summary>
    [Range(1, long.MaxValue)]
    public long CpuHighTotalMs { get; set; } = 600_000;

    /// <summary>Total CPU milliseconds for critical severity.</summary>
    [Range(1, long.MaxValue)]
    public long CpuCriticalTotalMs { get; set; } = 3_600_000;

    /// <summary>Minimum executions before a query can be flagged at all.</summary>
    [Range(1, int.MaxValue)]
    public int CpuMinExecutions { get; set; } = 5;

    /// <summary>
    /// Per-execution floor for a total-driven CPU finding.
    /// </summary>
    /// <remarks>
    /// A query that is cheap every time it runs is not a tuning target no matter how
    /// often it executes: 1,000,000 executions at 0.1 ms each is a workload-volume
    /// observation, not a query problem. Without this floor, execution count alone
    /// would create findings.
    /// </remarks>
    [Range(0.0, double.MaxValue)]
    public double CpuMinSignificantAvgMs { get; set; } = 10.0;

    // --- High I/O thresholds ---

    /// <summary>Average logical reads per execution for high severity.</summary>
    [Range(0.0, double.MaxValue)]
    public double IoHighAvgLogicalReads { get; set; } = 50_000.0;

    /// <summary>Average logical reads per execution for critical severity.</summary>
    [Range(0.0, double.MaxValue)]
    public double IoCriticalAvgLogicalReads { get; set; } = 500_000.0;

    /// <summary>Total logical reads for high severity.</summary>
    [Range(1, long.MaxValue)]
    public long IoHighTotalLogicalReads { get; set; } = 10_000_000;

    /// <summary>Total logical reads for critical severity.</summary>
    [Range(1, long.MaxValue)]
    public long IoCriticalTotalLogicalReads { get; set; } = 100_000_000;

    /// <summary>Minimum executions before a query can be flagged at all.</summary>
    [Range(1, int.MaxValue)]
    public int IoMinExecutions { get; set; } = 5;

    /// <summary>
    /// Per-execution floor for a total-driven I/O finding.
    /// </summary>
    /// <remarks>
    /// Deliberately low: a query doing a few hundred reads each time it runs is still a
    /// legitimate aggregate-I/O observation. The floor only removes queries whose
    /// individual cost is negligible.
    /// </remarks>
    [Range(0.0, double.MaxValue)]
    public double IoMinSignificantAvgReads { get; set; } = 100.0;

    // --- Query regression thresholds ---

    /// <summary>How many times worse than baseline before a regression is medium severity.</summary>
    [Range(1.0, double.MaxValue)]
    public double RegressionWarningMultiplier { get; set; } = 2.0;

    /// <summary>How many times worse than baseline before a regression is high severity.</summary>
    [Range(1.0, double.MaxValue)]
    public double RegressionHighMultiplier { get; set; } = 4.0;

    /// <summary>How many times worse than baseline before a regression is critical severity.</summary>
    [Range(1.0, double.MaxValue)]
    public double RegressionCriticalMultiplier { get; set; } = 8.0;

    /// <summary>
    /// Historical samples required before a baseline may be compared against.
    /// </summary>
    /// <remarks>
    /// Below this a "baseline" is one or two observations, and normal variation would be
    /// reported as a regression.
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int RegressionMinSamples { get; set; } = 5;

    /// <summary>Baseline window used for regression comparison.</summary>
    [Required]
    public string RegressionBaselineWindow { get; set; } = "7d";
}
