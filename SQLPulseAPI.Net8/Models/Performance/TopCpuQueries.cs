using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>Aggregated CPU consumer from the plan cache. Python: <c>TopCpuQuery</c>.</summary>
public sealed class TopCpuQuery
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

    [JsonPropertyName("database_name")]
    public string? DatabaseName { get; set; }
}

/// <summary>Result of the top-CPU collector. Python: <c>TopCpuQueriesResult</c>.</summary>
public sealed class TopCpuQueriesResult
{
    [JsonPropertyName("metadata")]
    public CollectorMetadata Metadata { get; set; } = new();

    [JsonPropertyName("top_n")]
    public int TopN { get; set; }

    [JsonPropertyName("queries")]
    public List<TopCpuQuery> Queries { get; set; } = [];
}
