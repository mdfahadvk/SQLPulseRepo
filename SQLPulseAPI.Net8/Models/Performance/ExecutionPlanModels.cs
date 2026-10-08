using System.Text.Json.Serialization;
using System.Runtime.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

public enum ExecutionPlanStatus
{
    [EnumMember(Value = "AVAILABLE")]
    Available,
    [EnumMember(Value = "NOT_FOUND")]
    NotFound,
    [EnumMember(Value = "UNAVAILABLE")]
    Unavailable,
    [EnumMember(Value = "PERMISSION_DENIED")]
    PermissionDenied,
}

public sealed class ExecutionPlanFinding
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "low";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "informational";

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = "Review the evidence in query and workload context before making changes.";

    [JsonPropertyName("human_review_required")]
    public bool HumanReviewRequired { get; set; } = true;
}

public sealed class ExecutionPlanOperator
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("estimated_cost_percent")]
    public double? EstimatedCostPercent { get; set; }

    [JsonPropertyName("estimated_rows")]
    public double? EstimatedRows { get; set; }

    [JsonPropertyName("actual_rows")]
    public double? ActualRows { get; set; }
}

public sealed class ExecutionPlanParameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("data_type")]
    public string? DataType { get; set; }

    [JsonPropertyName("compiled_value")]
    public string? CompiledValue { get; set; }

    [JsonPropertyName("runtime_value")]
    public string? RuntimeValue { get; set; }
}

public sealed class ExecutionPlanResponse
{
    [JsonPropertyName("status")]
    public ExecutionPlanStatus Status { get; set; }

    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    [JsonPropertyName("query_id")]
    public long? QueryId { get; set; }

    [JsonPropertyName("plan_id")]
    public long? PlanId { get; set; }

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("retrieved_at")]
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("plan_xml")]
    public string? PlanXml { get; set; }

    [JsonPropertyName("parameters")]
    public List<ExecutionPlanParameter> Parameters { get; set; } = [];

    [JsonPropertyName("statement_count")]
    public int StatementCount { get; set; }

    [JsonPropertyName("operator_summary")]
    public List<string> OperatorSummary { get; set; } = [];

    [JsonPropertyName("total_operator_count")]
    public int TotalOperatorCount { get; set; }

    [JsonPropertyName("operator_counts_by_type")]
    public Dictionary<string, int> OperatorCountsByType { get; set; } = [];

    [JsonPropertyName("highest_cost_operators")]
    public List<ExecutionPlanOperator> HighestCostOperators { get; set; } = [];

    [JsonPropertyName("plan_limitations")]
    public List<string> PlanLimitations { get; set; } = [];

    [JsonPropertyName("operators")]
    public List<ExecutionPlanOperator> Operators { get; set; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("findings")]
    public List<ExecutionPlanFinding> Findings { get; set; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
