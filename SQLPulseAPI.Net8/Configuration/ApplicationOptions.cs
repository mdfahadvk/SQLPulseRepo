using System.ComponentModel.DataAnnotations;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// Application identity settings (Python: <c>app_name</c>, <c>app_version</c>, <c>debug</c>).
/// </summary>
public sealed class ApplicationOptions
{
    public const string SectionName = "Application";

    /// <summary>Application name reported by <c>/health</c>.</summary>
    [Required]
    public string Name { get; set; } = "SQLPulse";

    /// <summary>Application version reported by <c>/health</c>.</summary>
    [Required]
    public string Version { get; set; } = "0.1.0";

    /// <summary>Enables verbose diagnostics. Never enable in production.</summary>
    public bool Debug { get; set; }
}
