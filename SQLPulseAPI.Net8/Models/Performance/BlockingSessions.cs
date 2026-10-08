using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>A session participating in a blocking chain. Python: <c>BlockingSession</c>.</summary>
public sealed class BlockingSession
{
    [JsonPropertyName("session_id")]
    public int SessionId { get; set; }

    [JsonPropertyName("blocking_session_id")]
    public int? BlockingSessionId { get; set; }

    [JsonPropertyName("wait_type")]
    public string? WaitType { get; set; }

    [JsonPropertyName("wait_time_ms")]
    public long WaitTimeMs { get; set; }

    [JsonPropertyName("wait_resource")]
    public string? WaitResource { get; set; }

    [JsonPropertyName("database_name")]
    public string? DatabaseName { get; set; }

    [JsonPropertyName("sql_text")]
    public string? SqlText { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("login_name")]
    public string? LoginName { get; set; }

    [JsonPropertyName("host_name")]
    public string? HostName { get; set; }

    [JsonPropertyName("program_name")]
    public string? ProgramName { get; set; }

    [JsonPropertyName("is_head_blocker")]
    public bool IsHeadBlocker { get; set; }
}

/// <summary>Result of the blocking collector. Python: <c>BlockingSessionsResult</c>.</summary>
public sealed class BlockingSessionsResult
{
    [JsonPropertyName("metadata")]
    public CollectorMetadata Metadata { get; set; } = new();

    [JsonPropertyName("sessions")]
    public List<BlockingSession> Sessions { get; set; } = [];

    [JsonPropertyName("blocking_chain_count")]
    public int BlockingChainCount { get; set; }
}
