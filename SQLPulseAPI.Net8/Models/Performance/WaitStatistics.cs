using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>A single wait type sample. Python: <c>WaitStatistic</c>.</summary>
public sealed class WaitStatistic
{
    [JsonPropertyName("wait_type")]
    public string WaitType { get; set; } = string.Empty;

    [JsonPropertyName("waiting_tasks_count")]
    public long WaitingTasksCount { get; set; }

    [JsonPropertyName("wait_time_ms")]
    public long WaitTimeMs { get; set; }

    [JsonPropertyName("max_wait_time_ms")]
    public long MaxWaitTimeMs { get; set; }

    [JsonPropertyName("signal_wait_time_ms")]
    public long SignalWaitTimeMs { get; set; }

    [JsonPropertyName("resource_wait_time_ms")]
    public long ResourceWaitTimeMs { get; set; }
}

/// <summary>Server-wide wait totals. Python: <c>WaitStatisticsTotals</c>.</summary>
public sealed class WaitStatisticsTotals
{
    [JsonPropertyName("waiting_tasks_count")]
    public long WaitingTasksCount { get; set; }

    [JsonPropertyName("wait_time_ms")]
    public long WaitTimeMs { get; set; }

    [JsonPropertyName("signal_wait_time_ms")]
    public long SignalWaitTimeMs { get; set; }

    [JsonPropertyName("resource_wait_time_ms")]
    public long ResourceWaitTimeMs { get; set; }
}

/// <summary>Change in wait totals since the previous collection. Python: <c>WaitStatisticsDelta</c>.</summary>
public sealed class WaitStatisticsDelta
{
    [JsonPropertyName("previous_collected_at")]
    public DateTimeOffset PreviousCollectedAt { get; set; }

    [JsonPropertyName("interval_seconds")]
    public double IntervalSeconds { get; set; }

    [JsonPropertyName("waiting_tasks_count")]
    public long WaitingTasksCount { get; set; }

    [JsonPropertyName("wait_time_ms")]
    public long WaitTimeMs { get; set; }

    [JsonPropertyName("signal_wait_time_ms")]
    public long SignalWaitTimeMs { get; set; }

    [JsonPropertyName("resource_wait_time_ms")]
    public long ResourceWaitTimeMs { get; set; }
}

/// <summary>Result of the wait statistics collector. Python: <c>WaitStatisticsResult</c>.</summary>
public sealed class WaitStatisticsResult
{
    [JsonPropertyName("metadata")]
    public CollectorMetadata Metadata { get; set; } = new();

    [JsonPropertyName("top_n")]
    public int TopN { get; set; }

    [JsonPropertyName("statistics")]
    public List<WaitStatistic> Statistics { get; set; } = [];

    [JsonPropertyName("applicability")]
    public WaitStatisticsApplicability Applicability { get; set; } = WaitStatisticsApplicability.ServerLevel;

    [JsonPropertyName("collected_at")]
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sqlserver_start_time")]
    public DateTimeOffset? SqlServerStartTime { get; set; }

    [JsonPropertyName("totals")]
    public WaitStatisticsTotals? Totals { get; set; }

    [JsonPropertyName("delta")]
    public WaitStatisticsDelta? Delta { get; set; }

    [JsonPropertyName("delta_status")]
    public WaitDeltaStatus DeltaStatus { get; set; } = WaitDeltaStatus.InsufficientPreviousData;
}
