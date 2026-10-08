using System.ComponentModel.DataAnnotations;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// Historical storage settings (Python: <c>HISTORY_*</c> and <c>BASELINE_MIN_SAMPLES</c>).
/// SQLite is used for local historical samples, matching the Python implementation.
/// </summary>
public sealed class HistoryOptions
{
    public const string SectionName = "History";

    /// <summary>Number of days historical samples are retained.</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>Minimum samples required before a baseline is reported as <c>READY</c>.</summary>
    [Range(1, 10_000)]
    public int BaselineMinSamples { get; set; } = 5;

    /// <summary>Path to the SQLite history database file.</summary>
    [Required]
    public string DatabasePath { get; set; } = "sqlpulse_history.db";
}
