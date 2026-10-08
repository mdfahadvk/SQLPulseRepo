using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>
/// Provenance for a single collector run. Python: <c>CollectorMetadata</c>.
/// </summary>
public sealed class CollectorMetadata
{
    [JsonPropertyName("collector_name")]
    public string CollectorName { get; set; } = string.Empty;

    [JsonPropertyName("server_host")]
    public string ServerHost { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("collected_at")]
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("permission_issue")]
    public bool PermissionIssue { get; set; }
}
