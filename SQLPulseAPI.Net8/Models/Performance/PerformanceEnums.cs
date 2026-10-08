using System.Runtime.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>Analysis scope. Python: the <c>scope</c> string literal.</summary>
public enum AnalysisScope
{
    [EnumMember(Value = "server")]
    Server,

    [EnumMember(Value = "database")]
    Database,
}

/// <summary>Whether wait statistics apply to the requested scope.</summary>
public enum WaitStatisticsApplicability
{
    [EnumMember(Value = "server_level")]
    ServerLevel,

    [EnumMember(Value = "not_applicable_database_scope")]
    NotApplicableDatabaseScope,
}

/// <summary>Availability of a wait statistics delta between two collections.</summary>
public enum WaitDeltaStatus
{
    [EnumMember(Value = "available")]
    Available,

    [EnumMember(Value = "insufficient_previous_data")]
    InsufficientPreviousData,

    [EnumMember(Value = "server_restarted")]
    ServerRestarted,

    [EnumMember(Value = "not_applicable")]
    NotApplicable,
}
