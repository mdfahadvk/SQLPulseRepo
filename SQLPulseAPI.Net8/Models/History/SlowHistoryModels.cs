using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Models.History;

/// <summary>Who or what runs the query. Python: <c>WorkloadType</c>.</summary>
public enum WorkloadType
{
    [EnumMember(Value = "APPLICATION_OR_INTERACTIVE")]
    ApplicationOrInteractive,

    [EnumMember(Value = "BACKGROUND_OR_SCHEDULED")]
    BackgroundOrScheduled,

    [EnumMember(Value = "UNKNOWN")]
    Unknown,
}

/// <summary>How much weight a piece of workload evidence carries. Python: <c>EvidenceStrength</c>.</summary>
public enum EvidenceStrength
{
    [EnumMember(Value = "strong")]
    Strong,

    [EnumMember(Value = "weak")]
    Weak,
}

/// <summary>
/// Classification of a slow query. A query is only a <c>TIMEOUT_CANDIDATE</c> when an
/// application timeout has been explicitly configured — it is never inferred.
/// Python: <c>CandidateClassification</c>.
/// </summary>
public enum CandidateClassification
{
    [EnumMember(Value = "SLOW_QUERY_CANDIDATE")]
    SlowQueryCandidate,

    [EnumMember(Value = "TIMEOUT_CANDIDATE")]
    TimeoutCandidate,
}

/// <summary>Availability of Query Store on the target database. Python: <c>QueryStoreStatus</c>.</summary>
public enum QueryStoreStatus
{
    [EnumMember(Value = "AVAILABLE")]
    Available,

    [EnumMember(Value = "AVAILABLE_READ_ONLY")]
    AvailableReadOnly,

    [EnumMember(Value = "DISABLED")]
    Disabled,

    [EnumMember(Value = "PERMISSION_DENIED")]
    PermissionDenied,

    [EnumMember(Value = "QUERY_FAILED")]
    QueryFailed,
}

/// <summary>Helpers for <see cref="QueryStoreStatus"/>.</summary>
public static class QueryStoreStatusExtensions
{
    /// <summary>True when Query Store data can be read. Python: <c>QueryStoreStatus.is_accessible</c>.</summary>
    public static bool IsAccessible(this QueryStoreStatus status) =>
        status is QueryStoreStatus.Available or QueryStoreStatus.AvailableReadOnly;
}

/// <summary>A single piece of evidence for a workload classification. Python: <c>WorkloadEvidence</c>.</summary>
public sealed class WorkloadEvidence
{
    [JsonPropertyName("strength")]
    public EvidenceStrength Strength { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("supports")]
    public WorkloadType Supports { get; set; }
}

/// <summary>A raw Query Store row for a slow query. Python: <c>SlowQueryRecord</c>.</summary>
public sealed class SlowQueryRecord
{
    [JsonPropertyName("query_id")]
    public long? QueryId { get; set; }

    [JsonPropertyName("query_sql_text")]
    public string? QuerySqlText { get; set; }

    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    [JsonPropertyName("object_id")]
    public long? ObjectId { get; set; }

    [JsonPropertyName("procedure_name")]
    public string? ProcedureName { get; set; }

    [JsonPropertyName("plan_id")]
    public long? PlanId { get; set; }

    [JsonPropertyName("execution_count")]
    public long ExecutionCount { get; set; }

    [JsonPropertyName("avg_duration_ms")]
    public double AvgDurationMs { get; set; }

    [JsonPropertyName("max_duration_ms")]
    public double MaxDurationMs { get; set; }

    [JsonPropertyName("min_duration_ms")]
    public double MinDurationMs { get; set; }

    [JsonPropertyName("avg_cpu_time_ms")]
    public double AvgCpuTimeMs { get; set; }

    [JsonPropertyName("max_cpu_time_ms")]
    public double MaxCpuTimeMs { get; set; }

    [JsonPropertyName("avg_logical_reads")]
    public double AvgLogicalReads { get; set; }

    [JsonPropertyName("max_logical_reads")]
    public double MaxLogicalReads { get; set; }

    [JsonPropertyName("avg_physical_reads")]
    public double AvgPhysicalReads { get; set; }

    [JsonPropertyName("first_execution_time")]
    public DateTimeOffset? FirstExecutionTime { get; set; }

    [JsonPropertyName("last_execution_time")]
    public DateTimeOffset? LastExecutionTime { get; set; }

    [JsonPropertyName("interval_start_time")]
    public DateTimeOffset? IntervalStartTime { get; set; }

    [JsonPropertyName("intervals_observed")]
    public int IntervalsObserved { get; set; } = 1;

    [JsonPropertyName("intervals_exceeding_threshold")]
    public int IntervalsExceedingThreshold { get; set; }

    [JsonPropertyName("program_name")]
    public string? ProgramName { get; set; }

    [JsonPropertyName("host_name")]
    public string? HostName { get; set; }

    [JsonPropertyName("is_scheduled_job")]
    public bool? IsScheduledJob { get; set; }
}

/// <summary>How current duration compares with the stored baseline. Python: <c>BaselineComparison</c>.</summary>
public sealed class BaselineComparison
{
    [JsonPropertyName("baseline_status")]
    public BaselineStatus BaselineStatus { get; set; } = BaselineStatus.InsufficientData;

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; set; }

    [JsonPropertyName("historical_mean_ms")]
    public double HistoricalMeanMs { get; set; }

    [JsonPropertyName("historical_median_ms")]
    public double HistoricalMedianMs { get; set; }

    [JsonPropertyName("historical_p95_ms")]
    public double HistoricalP95Ms { get; set; }

    [JsonPropertyName("standard_deviation_ms")]
    public double StandardDeviationMs { get; set; }

    [JsonPropertyName("regression_multiplier_vs_mean")]
    public double RegressionMultiplierVsMean { get; set; }

    [JsonPropertyName("regression_multiplier_vs_p95")]
    public double RegressionMultiplierVsP95 { get; set; }
}

/// <summary>
/// A slow query candidate, optionally promoted to a timeout candidate.
/// Python: <c>TimeoutCandidate</c>.
/// </summary>
public sealed class TimeoutCandidate
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "slow_query_candidate";

    [JsonPropertyName("classification")]
    public CandidateClassification Classification { get; set; } = CandidateClassification.SlowQueryCandidate;

    [JsonPropertyName("slow_query_candidate")]
    public bool IsSlowQueryCandidate { get; set; } = true;

    [JsonPropertyName("timeout_candidate")]
    public bool IsTimeoutCandidate { get; set; }

    [JsonPropertyName("slow_threshold_ms")]
    public double SlowThresholdMs { get; set; }

    [JsonPropertyName("application_timeout_ms")]
    public double? ApplicationTimeoutMs { get; set; }

    [JsonPropertyName("classification_reason")]
    public string ClassificationReason { get; set; } = string.Empty;

    [JsonPropertyName("query_id")]
    public long? QueryId { get; set; }

    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    [JsonPropertyName("object_id")]
    public long? ObjectId { get; set; }

    [JsonPropertyName("procedure_name")]
    public string? ProcedureName { get; set; }

    [JsonPropertyName("plan_id")]
    public long? PlanId { get; set; }

    [JsonPropertyName("sql_text_preview")]
    public string? SqlTextPreview { get; set; }

    [JsonPropertyName("execution_count")]
    public long ExecutionCount { get; set; }

    [JsonPropertyName("total_execution_count")]
    public long TotalExecutionCount { get; set; }

    [JsonPropertyName("intervals_observed")]
    public int IntervalsObserved { get; set; } = 1;

    [JsonPropertyName("intervals_exceeding_threshold")]
    public int IntervalsExceedingThreshold { get; set; }

    [JsonPropertyName("avg_duration_ms")]
    public double AvgDurationMs { get; set; }

    [JsonPropertyName("max_duration_ms")]
    public double MaxDurationMs { get; set; }

    [JsonPropertyName("min_duration_ms")]
    public double MinDurationMs { get; set; }

    [JsonPropertyName("avg_cpu_ms")]
    public double AvgCpuMs { get; set; }

    [JsonPropertyName("max_cpu_ms")]
    public double MaxCpuMs { get; set; }

    [JsonPropertyName("avg_logical_reads")]
    public double AvgLogicalReads { get; set; }

    [JsonPropertyName("max_logical_reads")]
    public double MaxLogicalReads { get; set; }

    [JsonPropertyName("first_execution_time")]
    public DateTimeOffset? FirstExecutionTime { get; set; }

    [JsonPropertyName("last_execution_time")]
    public DateTimeOffset? LastExecutionTime { get; set; }

    [JsonPropertyName("baseline_comparison")]
    public BaselineComparison? BaselineComparison { get; set; }

    [JsonPropertyName("confidence")]
    public Confidence Confidence { get; set; } = Confidence.Medium;

    [JsonPropertyName("severity")]
    public Severity Severity { get; set; } = Severity.High;

    [JsonPropertyName("workload_type")]
    public WorkloadType WorkloadType { get; set; } = WorkloadType.Unknown;

    [JsonPropertyName("classification_evidence")]
    public List<string> ClassificationEvidence { get; set; } = [];

    [JsonPropertyName("workload_evidence")]
    public List<WorkloadEvidence> WorkloadEvidence { get; set; } = [];

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }
}

/// <summary>
/// Historical slow-query analysis for a database and time range.
/// Python: <c>SlowHistoryResponse</c> (<c>GET /api/performance/slow-history</c>).
/// </summary>
public sealed class SlowHistoryResponse
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("start_time")]
    public DateTimeOffset StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public DateTimeOffset EndTime { get; set; }

    [JsonPropertyName("threshold_ms")]
    public int ThresholdMs { get; set; }

    [JsonPropertyName("slow_threshold_ms")]
    public int SlowThresholdMs { get; set; }

    [JsonPropertyName("application_timeout_ms")]
    public int? ApplicationTimeoutMs { get; set; }

    [JsonPropertyName("timeout_classification_available")]
    public bool TimeoutClassificationAvailable { get; set; }

    [JsonPropertyName("query_store_status")]
    public QueryStoreStatus QueryStoreStatus { get; set; } = QueryStoreStatus.Available;

    [JsonPropertyName("top_n")]
    public int TopN { get; set; } = 20;

    [JsonPropertyName("total_candidates")]
    public int TotalCandidates { get; set; }

    [JsonPropertyName("slow_query_candidate_count")]
    public int SlowQueryCandidateCount { get; set; }

    [JsonPropertyName("timeout_candidate_count")]
    public int TimeoutCandidateCount { get; set; }

    [JsonPropertyName("candidates")]
    public List<TimeoutCandidate> Candidates { get; set; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
