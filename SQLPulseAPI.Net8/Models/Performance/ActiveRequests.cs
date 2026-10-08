using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>A currently executing request. Python: <c>ActiveRequest</c>.</summary>
public sealed class ActiveRequest
{
    [JsonPropertyName("session_id")]
    public int SessionId { get; set; }

    [JsonPropertyName("request_id")]
    public int RequestId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("sql_text")]
    public string? SqlText { get; set; }

    [JsonPropertyName("database_name")]
    public string? DatabaseName { get; set; }

    [JsonPropertyName("wait_type")]
    public string? WaitType { get; set; }

    [JsonPropertyName("wait_time_ms")]
    public long WaitTimeMs { get; set; }

    [JsonPropertyName("cpu_time_ms")]
    public long CpuTimeMs { get; set; }

    [JsonPropertyName("total_elapsed_time_ms")]
    public long TotalElapsedTimeMs { get; set; }

    [JsonPropertyName("reads")]
    public long Reads { get; set; }

    [JsonPropertyName("writes")]
    public long Writes { get; set; }

    [JsonPropertyName("logical_reads")]
    public long LogicalReads { get; set; }

    [JsonPropertyName("blocking_session_id")]
    public int? BlockingSessionId { get; set; }

    [JsonPropertyName("open_transaction_count")]
    public int OpenTransactionCount { get; set; }

    [JsonPropertyName("login_name")]
    public string? LoginName { get; set; }

    [JsonPropertyName("host_name")]
    public string? HostName { get; set; }

    [JsonPropertyName("program_name")]
    public string? ProgramName { get; set; }
}

/// <summary>Result of the active requests collector. Python: <c>ActiveRequestsResult</c>.</summary>
public sealed class ActiveRequestsResult
{
    [JsonPropertyName("metadata")]
    public CollectorMetadata Metadata { get; set; } = new();

    [JsonPropertyName("requests")]
    public List<ActiveRequest> Requests { get; set; } = [];
}
