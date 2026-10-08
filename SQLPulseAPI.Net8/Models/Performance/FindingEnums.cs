using System.Runtime.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>How urgent a finding is. Python: <c>Severity</c>.</summary>
public enum Severity
{
    [EnumMember(Value = "critical")]
    Critical,

    [EnumMember(Value = "high")]
    High,

    [EnumMember(Value = "medium")]
    Medium,

    [EnumMember(Value = "low")]
    Low,
}

/// <summary>How much the evidence supports a finding. Python: <c>Confidence</c>.</summary>
public enum Confidence
{
    [EnumMember(Value = "high")]
    High,

    [EnumMember(Value = "medium")]
    Medium,

    [EnumMember(Value = "low")]
    Low,
}

/// <summary>Detector that produced a finding. Python: <c>FindingCategory</c>.</summary>
public enum FindingCategory
{
    [EnumMember(Value = "long_running_query")]
    LongRunningQuery,

    [EnumMember(Value = "blocking")]
    Blocking,

    [EnumMember(Value = "high_cpu")]
    HighCpu,

    [EnumMember(Value = "high_io")]
    HighIo,

    [EnumMember(Value = "query_regression")]
    QueryRegression,

    [EnumMember(Value = "slow_query_candidate")]
    SlowQueryCandidate,

    [EnumMember(Value = "timeout_candidate")]
    TimeoutCandidate,
}

/// <summary>Overall health verdict for an analysis run. Python: <c>OverallStatus</c>.</summary>
public enum OverallStatus
{
    [EnumMember(Value = "HEALTHY")]
    Healthy,

    [EnumMember(Value = "WARNING")]
    Warning,

    [EnumMember(Value = "DEGRADED")]
    Degraded,

    [EnumMember(Value = "CRITICAL")]
    Critical,

    [EnumMember(Value = "UNKNOWN")]
    Unknown,
}

/// <summary>Resource signals contributing to a merged finding. Python: <c>FindingSignal</c>.</summary>
public enum FindingSignal
{
    [EnumMember(Value = "high_cpu")]
    HighCpu,

    [EnumMember(Value = "high_io")]
    HighIo,
}

/// <summary>Whether historical baselines were available for an analysis run.</summary>
public enum HistoricalAnalysisStatus
{
    [EnumMember(Value = "available")]
    Available,

    [EnumMember(Value = "unavailable")]
    Unavailable,
}
