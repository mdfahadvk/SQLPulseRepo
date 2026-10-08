using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>Aggregated I/O consumer from the plan cache. Python: <c>TopIoQuery</c>.</summary>
public sealed class TopIoQuery
{
    [JsonPropertyName("sql_text")]
    public string? SqlText { get; set; }

    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    [JsonPropertyName("query_plan_hash")]
    public string? QueryPlanHash { get; set; }

    [JsonPropertyName("object_name")]
    public string? ObjectName { get; set; }

    [JsonPropertyName("execution_count")]
    public long ExecutionCount { get; set; }

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

    [JsonPropertyName("total_elapsed_time_ms")]
    public long TotalElapsedTimeMs { get; set; }

    [JsonPropertyName("database_name")]
    public string? DatabaseName { get; set; }
}

/// <summary>Result of the top-I/O collector. Python: <c>TopIoQueriesResult</c>.</summary>
public sealed class TopIoQueriesResult
{
    [JsonPropertyName("metadata")]
    public CollectorMetadata Metadata { get; set; } = new();

    [JsonPropertyName("top_n")]
    public int TopN { get; set; }

    [JsonPropertyName("queries")]
    public List<TopIoQuery> Queries { get; set; } = [];
}
