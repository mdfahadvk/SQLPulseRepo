using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Performance;

/// <summary>Stable identity of the query a finding refers to. Python: <c>QueryIdentity</c>.</summary>
public sealed class QueryIdentity
{
    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    [JsonPropertyName("query_plan_hash")]
    public string? QueryPlanHash { get; set; }

    [JsonPropertyName("object_name")]
    public string? ObjectName { get; set; }
}

/// <summary>Suggested next analysis scope. Python: <c>RecommendedDrilldown</c>.</summary>
public sealed class RecommendedDrilldown
{
    [JsonPropertyName("scope")]
    public AnalysisScope Scope { get; set; } = AnalysisScope.Database;

    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;
}

/// <summary>
/// An observation produced by a detector. SQLPulse only observes and recommends —
/// findings never describe an action taken against the database.
/// Python: <c>PerformanceFinding</c>.
/// </summary>
public sealed class PerformanceFinding
{
    [JsonPropertyName("finding_id")]
    public string FindingId { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public FindingCategory Category { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("severity")]
    public Severity Severity { get; set; }

    [JsonPropertyName("confidence")]
    public Confidence Confidence { get; set; }

    [JsonPropertyName("first_observed")]
    public DateTimeOffset FirstObserved { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("last_observed")]
    public DateTimeOffset LastObserved { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("likely_cause")]
    public string LikelyCause { get; set; } = string.Empty;

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = string.Empty;

    [JsonPropertyName("validation_steps")]
    public List<string> ValidationSteps { get; set; } = [];

    [JsonPropertyName("human_review_required")]
    public bool HumanReviewRequired { get; set; }

    [JsonPropertyName("recommended_drilldown")]
    public RecommendedDrilldown? RecommendedDrilldown { get; set; }

    [JsonPropertyName("signals")]
    public List<FindingSignal> Signals { get; set; } = [];

    [JsonPropertyName("query_identity")]
    public QueryIdentity? QueryIdentity { get; set; }

    [JsonPropertyName("severity_basis")]
    public string? SeverityBasis { get; set; }
}

/// <summary>A query deliberately excluded from analysis. Python: <c>ExcludedQuery</c>.</summary>
public sealed class ExcludedQuery
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("sql_preview")]
    public string? SqlPreview { get; set; }
}

/// <summary>Per-database roll-up of findings. Python: <c>AffectedDatabase</c>.</summary>
public sealed class AffectedDatabase
{
    [JsonPropertyName("database")]
    public string Database { get; set; } = string.Empty;

    [JsonPropertyName("highest_severity")]
    public Severity HighestSeverity { get; set; }

    [JsonPropertyName("finding_count")]
    public int FindingCount { get; set; }
}

/// <summary>Headline counts for an analysis run. Python: <c>AnalysisSummary</c>.</summary>
public sealed class AnalysisSummary
{
    [JsonPropertyName("current_health_status")]
    public OverallStatus CurrentHealthStatus { get; set; } = OverallStatus.Healthy;

    [JsonPropertyName("health_finding_count")]
    public int HealthFindingCount { get; set; }

    [JsonPropertyName("optimization_opportunity_count")]
    public int OptimizationOpportunityCount { get; set; }

    [JsonPropertyName("highest_severity")]
    public Severity? HighestSeverity { get; set; }

    [JsonPropertyName("affected_database_count")]
    public int AffectedDatabaseCount { get; set; }

    [JsonPropertyName("total_findings")]
    public int TotalFindings { get; set; }

    [JsonPropertyName("critical_count")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("high_count")]
    public int HighCount { get; set; }

    [JsonPropertyName("medium_count")]
    public int MediumCount { get; set; }

    [JsonPropertyName("low_count")]
    public int LowCount { get; set; }

    [JsonPropertyName("excluded_query_count")]
    public int ExcludedQueryCount { get; set; }
}

/// <summary>Findings describing what is wrong right now. Python: <c>HealthSection</c>.</summary>
public sealed class HealthSection
{
    [JsonPropertyName("status")]
    public OverallStatus Status { get; set; } = OverallStatus.Healthy;

    [JsonPropertyName("findings")]
    public List<PerformanceFinding> Findings { get; set; } = [];
}

/// <summary>
/// Full result of an analysis run. Python: <c>AnalysisResult</c>
/// (exposed by <c>GET /api/performance/analyze</c>).
/// </summary>
public sealed class AnalysisResult
{
    [JsonPropertyName("analysis_id")]
    public string AnalysisId { get; set; } = string.Empty;

    [JsonPropertyName("analyzed_at")]
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public AnalysisScope Scope { get; set; } = AnalysisScope.Server;

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("overall_status")]
    public OverallStatus OverallStatus { get; set; } = OverallStatus.Unknown;

    [JsonPropertyName("findings")]
    public List<PerformanceFinding> Findings { get; set; } = [];

    [JsonPropertyName("affected_databases")]
    public List<AffectedDatabase> AffectedDatabases { get; set; } = [];

    [JsonPropertyName("total_findings")]
    public int TotalFindings { get; set; }

    [JsonPropertyName("critical_count")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("high_count")]
    public int HighCount { get; set; }

    [JsonPropertyName("medium_count")]
    public int MediumCount { get; set; }

    [JsonPropertyName("low_count")]
    public int LowCount { get; set; }

    [JsonPropertyName("current_health")]
    public HealthSection? CurrentHealth { get; set; }

    [JsonPropertyName("optimization_opportunities")]
    public List<PerformanceFinding> OptimizationOpportunities { get; set; } = [];

    [JsonPropertyName("historical_analysis_status")]
    public HistoricalAnalysisStatus HistoricalAnalysisStatus { get; set; } = HistoricalAnalysisStatus.Available;

    [JsonPropertyName("summary")]
    public AnalysisSummary? Summary { get; set; }

    [JsonPropertyName("excluded_queries")]
    public List<ExcludedQuery> ExcludedQueries { get; set; } = [];
}
