using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>A query running longer than the configured threshold. Python: <c>LongRunningQuery</c>.</summary>
public sealed class LongRunningQuery
{
    [JsonPropertyName("session_id")]
    public int SessionId { get; set; }

    [JsonPropertyName("request_id")]
    public int RequestId { get; set; }

    [JsonPropertyName("sql_text")]
    public string? SqlText { get; set; }

    [JsonPropertyName("database_name")]
    public string? DatabaseName { get; set; }

    [JsonPropertyName("elapsed_time_ms")]
    public long ElapsedTimeMs { get; set; }

    [JsonPropertyName("cpu_time_ms")]
    public long CpuTimeMs { get; set; }

    [JsonPropertyName("reads")]
    public long Reads { get; set; }

    [JsonPropertyName("logical_reads")]
    public long LogicalReads { get; set; }

    [JsonPropertyName("writes")]
    public long Writes { get; set; }

    [JsonPropertyName("wait_type")]
    public string? WaitType { get; set; }

    [JsonPropertyName("wait_time_ms")]
    public long WaitTimeMs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("login_name")]
    public string? LoginName { get; set; }

    [JsonPropertyName("host_name")]
    public string? HostName { get; set; }

    [JsonPropertyName("program_name")]
    public string? ProgramName { get; set; }
}

/// <summary>Result of the long-running query collector. Python: <c>LongRunningQueriesResult</c>.</summary>
public sealed class LongRunningQueriesResult
{
    [JsonPropertyName("metadata")]
    public CollectorMetadata Metadata { get; set; } = new();

    [JsonPropertyName("threshold_ms")]
    public int ThresholdMs { get; set; }

    [JsonPropertyName("queries")]
    public List<LongRunningQuery> Queries { get; set; } = [];
}
