using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Agent;

/// <summary>
/// Sanitized record of a tool invocation kept in the audit trail.
/// Raw arguments are deliberately absent. Python: <c>AuditToolCall</c>.
/// </summary>
public sealed class AuditToolCall
{
    [JsonPropertyName("tool_name")]
    public string ToolName { get; set; } = string.Empty;

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
/// Immutable audit record for one investigation. Python: <c>InvestigationAuditRecord</c>.
/// </summary>
public sealed class InvestigationAuditRecord
{
    [JsonPropertyName("investigation_id")]
    public string InvestigationId { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("question_sanitized")]
    public string QuestionSanitized { get; set; } = string.Empty;

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("time_range_start")]
    public string? TimeRangeStart { get; set; }

    [JsonPropertyName("time_range_end")]
    public string? TimeRangeEnd { get; set; }

    [JsonPropertyName("tools_called")]
    public List<AuditToolCall> ToolsCalled { get; set; } = [];

    [JsonPropertyName("finding_references")]
    public List<string> FindingReferences { get; set; } = [];

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "unknown";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "low";

    [JsonPropertyName("answer")]
    public string Answer { get; set; } = string.Empty;

    [JsonPropertyName("human_review_required")]
    public bool HumanReviewRequired { get; set; } = true;

    /// <summary>Always false: SQLPulse is read-only and never remediates.</summary>
    [JsonPropertyName("remediation_performed")]
    public bool RemediationPerformed { get; set; }
}

/// <summary>Audit trail listing. Python: <c>InvestigationAuditListResponse</c>.</summary>
public sealed class InvestigationAuditListResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("investigations")]
    public List<InvestigationAuditRecord> Investigations { get; set; } = [];
}

/// <summary>Single audit record lookup. Python: <c>InvestigationAuditDetailResponse</c>.</summary>
public sealed class InvestigationAuditDetailResponse
{
    [JsonPropertyName("found")]
    public bool Found { get; set; } = true;

    [JsonPropertyName("investigation")]
    public InvestigationAuditRecord? Investigation { get; set; }
}
