using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Agent;

/// <summary>A single tool invocation made during an investigation. Python: <c>ToolCall</c>.</summary>
public sealed class ToolCall
{
    [JsonPropertyName("tool_name")]
    public string ToolName { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public Dictionary<string, object?> Arguments { get; set; } = [];

    [JsonPropertyName("arguments_sanitized")]
    public Dictionary<string, object?> ArgumentsSanitized { get; set; } = [];

    [JsonPropertyName("result_summary")]
    public Dictionary<string, object?> ResultSummary { get; set; } = [];

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }
}

/// <summary>
/// Result of a read-only investigation. SQLPulse never remediates: the response
/// only ever reports observations and recommendations.
/// Python: <c>InvestigationResponse</c>.
/// </summary>
public sealed class InvestigationResponse
{
    /// <summary>Default value of <see cref="AgentAction"/>; SQLPulse performs no remediation.</summary>
    public const string NoRemediationPerformed =
        "No database remediation was performed. Observation and recommendation only.";

    [JsonPropertyName("investigation_id")]
    public string InvestigationId { get; set; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("answer")]
    public string Answer { get; set; } = string.Empty;

    /// <summary>Severity literal. Uses finding severities plus <c>unknown</c>.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "unknown";

    /// <summary>Confidence literal: <c>high</c>, <c>medium</c> or <c>low</c>.</summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "low";

    [JsonPropertyName("affected_database")]
    public string? AffectedDatabase { get; set; }

    [JsonPropertyName("affected_query")]
    public string? AffectedQuery { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("historical_comparison")]
    public string? HistoricalComparison { get; set; }

    [JsonPropertyName("likely_cause")]
    public string? LikelyCause { get; set; }

    [JsonPropertyName("what_is_known")]
    public List<string> WhatIsKnown { get; set; } = [];

    [JsonPropertyName("what_is_uncertain")]
    public List<string> WhatIsUncertain { get; set; } = [];

    [JsonPropertyName("recommended_investigation")]
    public List<string> RecommendedInvestigation { get; set; } = [];

    [JsonPropertyName("human_review_required")]
    public bool HumanReviewRequired { get; set; } = true;

    [JsonPropertyName("agent_action")]
    public string AgentAction { get; set; } = NoRemediationPerformed;

    [JsonPropertyName("tools_used")]
    public List<string> ToolsUsed { get; set; } = [];

    /// <summary>
    /// Full tool-call detail. Excluded from the API response (Python: <c>exclude=True</c>)
    /// because it can contain raw SQL; it is persisted to the audit trail instead.
    /// </summary>
    [JsonIgnore]
    public List<ToolCall> ToolCallsDetail { get; set; } = [];

    [JsonPropertyName("tool_call_count")]
    public int ToolCallCount { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
