using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>
/// A point-in-time, read-only picture of SQL Server activity.
/// Python: <c>PerformanceSnapshot</c>.
/// </summary>
public sealed class PerformanceSnapshot
{
    [JsonPropertyName("snapshot_id")]
    public string SnapshotId { get; set; } = string.Empty;

    [JsonPropertyName("collected_at")]
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("server_host")]
    public string ServerHost { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public AnalysisScope Scope { get; set; } = AnalysisScope.Server;

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("active_requests")]
    public ActiveRequestsResult? ActiveRequests { get; set; }

    [JsonPropertyName("long_running_queries")]
    public LongRunningQueriesResult? LongRunningQueries { get; set; }

    [JsonPropertyName("blocking_sessions")]
    public BlockingSessionsResult? BlockingSessions { get; set; }

    [JsonPropertyName("top_cpu_queries")]
    public TopCpuQueriesResult? TopCpuQueries { get; set; }

    [JsonPropertyName("top_io_queries")]
    public TopIoQueriesResult? TopIoQueries { get; set; }

    [JsonPropertyName("wait_statistics")]
    public WaitStatisticsResult? WaitStatistics { get; set; }
}
