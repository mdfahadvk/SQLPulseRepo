using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.History;

/// <summary>Whether a baseline has enough samples to be trusted. Python: <c>BaselineStatus</c>.</summary>
public enum BaselineStatus
{
    [EnumMember(Value = "READY")]
    Ready,

    [EnumMember(Value = "INSUFFICIENT_DATA")]
    InsufficientData,
}

/// <summary>
/// One historical sample of a query's aggregated metrics.
/// Python: <c>QueryMetricRecord</c>.
/// </summary>
public sealed class QueryMetricRecord
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("snapshot_id")]
    public string SnapshotId { get; set; } = string.Empty;

    [JsonPropertyName("collected_at")]
    public DateTimeOffset CollectedAt { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("query_hash")]
    public string QueryHash { get; set; } = string.Empty;

    [JsonPropertyName("sql_text_preview")]
    public string? SqlTextPreview { get; set; }

    [JsonPropertyName("execution_count")]
    public long ExecutionCount { get; set; }

    [JsonPropertyName("total_cpu_time_ms")]
    public long TotalCpuTimeMs { get; set; }

    [JsonPropertyName("avg_cpu_time_ms")]
    public double AvgCpuTimeMs { get; set; }

    [JsonPropertyName("total_elapsed_time_ms")]
    public long TotalElapsedTimeMs { get; set; }

    [JsonPropertyName("avg_elapsed_time_ms")]
    public double AvgElapsedTimeMs { get; set; }

    [JsonPropertyName("total_logical_reads")]
    public long TotalLogicalReads { get; set; }

    [JsonPropertyName("avg_logical_reads")]
    public double AvgLogicalReads { get; set; }

    [JsonPropertyName("total_physical_reads")]
    public long TotalPhysicalReads { get; set; }

    [JsonPropertyName("avg_physical_reads")]
    public double AvgPhysicalReads { get; set; }

    [JsonPropertyName("total_logical_writes")]
    public long TotalLogicalWrites { get; set; }

    [JsonPropertyName("avg_logical_writes")]
    public double AvgLogicalWrites { get; set; }
}

/// <summary>Server-wide wait totals persisted for delta calculation. Python: <c>WaitTotalsRecord</c>.</summary>
public sealed class WaitTotalsRecord
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("snapshot_id")]
    public string SnapshotId { get; set; } = string.Empty;

    [JsonPropertyName("collected_at")]
    public DateTimeOffset CollectedAt { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("sqlserver_start_time")]
    public DateTimeOffset? SqlServerStartTime { get; set; }

    [JsonPropertyName("waiting_tasks_count")]
    public long WaitingTasksCount { get; set; }

    [JsonPropertyName("wait_time_ms")]
    public long WaitTimeMs { get; set; }

    [JsonPropertyName("signal_wait_time_ms")]
    public long SignalWaitTimeMs { get; set; }

    [JsonPropertyName("resource_wait_time_ms")]
    public long ResourceWaitTimeMs { get; set; }
}

/// <summary>Descriptive statistics for one baselined metric. Python: <c>BaselineStatistic</c>.</summary>
public sealed class BaselineStatistic
{
    [JsonPropertyName("sample_count")]
    public int SampleCount { get; set; }

    [JsonPropertyName("mean")]
    public double Mean { get; set; }

    [JsonPropertyName("median")]
    public double Median { get; set; }

    [JsonPropertyName("minimum")]
    public double Minimum { get; set; }

    [JsonPropertyName("maximum")]
    public double Maximum { get; set; }

    [JsonPropertyName("standard_deviation")]
    public double StandardDeviation { get; set; }

    [JsonPropertyName("p50")]
    public double P50 { get; set; }

    [JsonPropertyName("p95")]
    public double P95 { get; set; }
}

/// <summary>Baseline for a single query over a time window. Python: <c>QueryBaseline</c>.</summary>
public sealed class QueryBaseline
{
    [JsonPropertyName("query_hash")]
    public string QueryHash { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("window")]
    public string Window { get; set; } = string.Empty;

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; set; }

    [JsonPropertyName("baseline_status")]
    public BaselineStatus BaselineStatus { get; set; } = BaselineStatus.InsufficientData;

    [JsonPropertyName("avg_cpu_ms")]
    public BaselineStatistic? AvgCpuMs { get; set; }

    [JsonPropertyName("avg_elapsed_ms")]
    public BaselineStatistic? AvgElapsedMs { get; set; }

    [JsonPropertyName("avg_logical_reads")]
    public BaselineStatistic? AvgLogicalReads { get; set; }

    [JsonPropertyName("avg_physical_reads")]
    public BaselineStatistic? AvgPhysicalReads { get; set; }

    [JsonPropertyName("execution_count")]
    public BaselineStatistic? ExecutionCount { get; set; }
}

/// <summary>Persisted snapshot header. Python: <c>SnapshotRecord</c>.</summary>
public sealed class SnapshotRecord
{
    [JsonPropertyName("snapshot_id")]
    public string SnapshotId { get; set; } = string.Empty;

    [JsonPropertyName("collected_at")]
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string? Database { get; set; }
}

/// <summary>
/// Historical samples for one query.
/// Python: <c>QueryHistoryResponse</c> (<c>GET /api/history/query/{query_hash}</c>).
/// </summary>
public sealed class QueryHistoryResponse
{
    [JsonPropertyName("query_hash")]
    public string QueryHash { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("samples")]
    public List<QueryMetricRecord> Samples { get; set; } = [];
}

/// <summary>
/// Baseline statistics for one query.
/// Python: <c>QueryBaselineResponse</c> (<c>GET /api/history/query/{query_hash}/baseline</c>).
/// </summary>
public sealed class QueryBaselineResponse
{
    [JsonPropertyName("query_hash")]
    public string QueryHash { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("window")]
    public string Window { get; set; } = string.Empty;

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; set; }

    [JsonPropertyName("baseline_status")]
    public BaselineStatus BaselineStatus { get; set; } = BaselineStatus.InsufficientData;

    [JsonPropertyName("metrics")]
    public Dictionary<string, BaselineStatistic> Metrics { get; set; } = [];
}
