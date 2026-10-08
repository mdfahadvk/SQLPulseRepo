using System.Text.Json;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Locks the JSON contract to the Python/FastAPI reference implementation so the
/// existing React UI can switch backends without model changes.
/// </summary>
public sealed class JsonContractTests
{
    private static readonly JsonSerializerOptions Options = SqlPulseJson.Options;

    [Fact]
    public void Property_names_stay_snake_case()
    {
        var snapshot = new PerformanceSnapshot
        {
            SnapshotId = "snap-1",
            ServerHost = "sql01",
            Scope = AnalysisScope.Database,
            Database = "AdventureWorks",
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, Options));
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "snapshot_id",
                "collected_at",
                "server_host",
                "scope",
                "database",
                "active_requests",
                "long_running_queries",
                "blocking_sessions",
                "top_cpu_queries",
                "top_io_queries",
                "wait_statistics",
            },
            names);
    }

    [Fact]
    public void Null_collector_results_are_emitted_as_null()
    {
        var snapshot = new PerformanceSnapshot { SnapshotId = "snap-1", ServerHost = "sql01" };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, Options));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("active_requests").ValueKind);
        Assert.Equal("server", document.RootElement.GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData(OverallStatus.Healthy, "HEALTHY")]
    [InlineData(OverallStatus.Warning, "WARNING")]
    [InlineData(OverallStatus.Degraded, "DEGRADED")]
    [InlineData(OverallStatus.Critical, "CRITICAL")]
    [InlineData(OverallStatus.Unknown, "UNKNOWN")]
    public void Overall_status_uses_uppercase_literals(OverallStatus status, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(status, Options));

    [Theory]
    [InlineData(Severity.Critical, "critical")]
    [InlineData(Severity.High, "high")]
    [InlineData(Severity.Medium, "medium")]
    [InlineData(Severity.Low, "low")]
    public void Severity_uses_lowercase_literals(Severity severity, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(severity, Options));

    [Theory]
    [InlineData(Confidence.High, "high")]
    [InlineData(Confidence.Medium, "medium")]
    [InlineData(Confidence.Low, "low")]
    public void Confidence_uses_lowercase_literals(Confidence confidence, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(confidence, Options));

    [Theory]
    [InlineData(AnalysisScope.Server, "server")]
    [InlineData(AnalysisScope.Database, "database")]
    public void Scope_uses_lowercase_literals(AnalysisScope scope, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(scope, Options));

    [Theory]
    [InlineData(CandidateClassification.SlowQueryCandidate, "SLOW_QUERY_CANDIDATE")]
    [InlineData(CandidateClassification.TimeoutCandidate, "TIMEOUT_CANDIDATE")]
    public void Candidate_classification_uses_uppercase_literals(
        CandidateClassification classification,
        string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(classification, Options));

    [Theory]
    [InlineData(WorkloadType.ApplicationOrInteractive, "APPLICATION_OR_INTERACTIVE")]
    [InlineData(WorkloadType.BackgroundOrScheduled, "BACKGROUND_OR_SCHEDULED")]
    [InlineData(WorkloadType.Unknown, "UNKNOWN")]
    public void Workload_type_uses_uppercase_literals(WorkloadType workloadType, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(workloadType, Options));

    [Theory]
    [InlineData(FindingCategory.LongRunningQuery, "long_running_query")]
    [InlineData(FindingCategory.Blocking, "blocking")]
    [InlineData(FindingCategory.HighCpu, "high_cpu")]
    [InlineData(FindingCategory.HighIo, "high_io")]
    [InlineData(FindingCategory.QueryRegression, "query_regression")]
    [InlineData(FindingCategory.SlowQueryCandidate, "slow_query_candidate")]
    [InlineData(FindingCategory.TimeoutCandidate, "timeout_candidate")]
    public void Finding_category_uses_python_literals(FindingCategory category, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(category, Options));

    [Theory]
    [InlineData(BaselineStatus.Ready, "READY")]
    [InlineData(BaselineStatus.InsufficientData, "INSUFFICIENT_DATA")]
    public void Baseline_status_uses_uppercase_literals(BaselineStatus status, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(status, Options));

    [Theory]
    [InlineData(QueryStoreStatus.Available, true)]
    [InlineData(QueryStoreStatus.AvailableReadOnly, true)]
    [InlineData(QueryStoreStatus.Disabled, false)]
    [InlineData(QueryStoreStatus.PermissionDenied, false)]
    [InlineData(QueryStoreStatus.QueryFailed, false)]
    public void Query_store_accessibility_matches_python(QueryStoreStatus status, bool accessible) =>
        Assert.Equal(accessible, status.IsAccessible());

    [Fact]
    public void Enum_literals_round_trip()
    {
        var json = JsonSerializer.Serialize(OverallStatus.Degraded, Options);

        Assert.Equal(OverallStatus.Degraded, JsonSerializer.Deserialize<OverallStatus>(json, Options));
    }

    // --- SQL errors ----------------------------------------------------------

    [Theory]
    [InlineData(SqlErrorCategory.Timeout, "TIMEOUT")]
    [InlineData(SqlErrorCategory.Deadlock, "DEADLOCK")]
    [InlineData(SqlErrorCategory.Connection, "CONNECTION")]
    [InlineData(SqlErrorCategory.LoginFailure, "LOGIN_FAILURE")]
    [InlineData(SqlErrorCategory.PermissionDenied, "PERMISSION_DENIED")]
    [InlineData(SqlErrorCategory.ConstraintViolation, "CONSTRAINT_VIOLATION")]
    [InlineData(SqlErrorCategory.ConversionError, "CONVERSION_ERROR")]
    [InlineData(SqlErrorCategory.ArithmeticError, "ARITHMETIC_ERROR")]
    [InlineData(SqlErrorCategory.InvalidObject, "INVALID_OBJECT")]
    [InlineData(SqlErrorCategory.InvalidColumn, "INVALID_COLUMN")]
    [InlineData(SqlErrorCategory.TransactionError, "TRANSACTION_ERROR")]
    [InlineData(SqlErrorCategory.SqlExecutionError, "SQL_EXECUTION_ERROR")]
    [InlineData(SqlErrorCategory.Unknown, "UNKNOWN")]
    public void Sql_error_category_uses_uppercase_literals(SqlErrorCategory category, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(category, Options));

    /// <summary>
    /// These four literals are the distinction the whole capability rests on. A UI that
    /// renders CONFIRMED_APPLICATION_TIMEOUT and TIMEOUT_CANDIDATE identically would
    /// report incidents nobody observed, so the wire values are locked here.
    /// </summary>
    [Theory]
    [InlineData(TimeoutClassification.ConfirmedApplicationTimeout, "CONFIRMED_APPLICATION_TIMEOUT")]
    [InlineData(TimeoutClassification.TimeoutCandidate, "TIMEOUT_CANDIDATE")]
    [InlineData(TimeoutClassification.SlowQuery, "SLOW_QUERY")]
    [InlineData(TimeoutClassification.NotApplicable, "NOT_APPLICABLE")]
    public void Timeout_classification_uses_uppercase_literals(
        TimeoutClassification classification,
        string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(classification, Options));

    [Theory]
    [InlineData(SqlErrorQueryStatus.Ok, "OK")]
    [InlineData(SqlErrorQueryStatus.NoErrorsFound, "NO_ERRORS_FOUND")]
    [InlineData(SqlErrorQueryStatus.PartialErrorData, "PARTIAL_ERROR_DATA")]
    [InlineData(SqlErrorQueryStatus.ErrorSourceUnavailable, "ERROR_SOURCE_UNAVAILABLE")]
    public void Sql_error_query_status_uses_uppercase_literals(SqlErrorQueryStatus status, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(status, Options));

    [Theory]
    [InlineData(SqlErrorSourceKind.ApplicationTelemetry, "APPLICATION_TELEMETRY")]
    [InlineData(SqlErrorSourceKind.SqlServer, "SQL_SERVER")]
    [InlineData(SqlErrorSourceKind.DerivedPerformanceEvidence, "DERIVED_PERFORMANCE_EVIDENCE")]
    public void Sql_error_source_kind_uses_uppercase_literals(SqlErrorSourceKind kind, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(kind, Options));

    [Fact]
    public void Permission_denied_source_status_uses_uppercase_wire_value() =>
        Assert.Equal("\"PERMISSION_DENIED\"", JsonSerializer.Serialize(SqlErrorSourceStatus.PermissionDenied, Options));

    [Theory]
    [InlineData(SqlErrorSourceStatus.Available, "AVAILABLE")]
    [InlineData(SqlErrorSourceStatus.NotConfigured, "NOT_CONFIGURED")]
    [InlineData(SqlErrorSourceStatus.NotApplicable, "NOT_APPLICABLE")]
    [InlineData(SqlErrorSourceStatus.Failed, "FAILED")]
    public void Sql_error_source_status_uses_uppercase_literals(SqlErrorSourceStatus status, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(status, Options));

    [Theory]
    [InlineData(CorrelationConfidence.High, "HIGH")]
    [InlineData(CorrelationConfidence.Medium, "MEDIUM")]
    [InlineData(CorrelationConfidence.Low, "LOW")]
    [InlineData(CorrelationConfidence.None, "NONE")]
    public void Correlation_confidence_uses_uppercase_literals(
        CorrelationConfidence confidence,
        string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(confidence, Options));

    [Theory]
    [InlineData(CorrelatedEvidenceKind.QueryStoreHistory, "QUERY_STORE_HISTORY")]
    [InlineData(CorrelatedEvidenceKind.QueryBaseline, "QUERY_BASELINE")]
    [InlineData(CorrelatedEvidenceKind.Blocking, "BLOCKING")]
    [InlineData(CorrelatedEvidenceKind.LongRunningQuery, "LONG_RUNNING_QUERY")]
    public void Correlated_evidence_kind_uses_uppercase_literals(
        CorrelatedEvidenceKind kind,
        string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(kind, Options));

    [Fact]
    public void Sql_error_event_serializes_the_documented_field_set()
    {
        var errorEvent = new SqlErrorEvent { Id = "err_1", Server = "sql01" };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(errorEvent, Options));
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "id",
                "timestamp",
                "server",
                "database",
                "application",
                "environment",
                "host",
                "session_identifier",
                "trace_id",
                "correlation_id",
                "request_endpoint",
                "stored_procedure",
                "query_hash",
                "sql_error_number",
                "sql_error_state",
                "sql_error_class",
                "error_category",
                "message",
                "exception_type",
                "command_timeout_seconds",
                "duration_ms",
                "parameters",
                "source",
                "source_kind",
                "is_timeout",
                "is_deadlock",
                "severity",
                "timeout_classification",
                "classification_evidence",
                "confirmed",
            },
            names);
    }

    /// <summary>
    /// A response that reported zero errors without saying whether anything could be
    /// consulted would be read as an all-clear, so every response carries its own
    /// visibility.
    /// </summary>
    [Fact]
    public void Every_sql_error_response_states_how_much_was_visible()
    {
        using var list = JsonDocument.Parse(
            JsonSerializer.Serialize(new SqlErrorListResponse(), Options));

        using var summary = JsonDocument.Parse(
            JsonSerializer.Serialize(new SqlErrorSummaryResponse(), Options));

        foreach (var root in new[] { list.RootElement, summary.RootElement })
        {
            Assert.Equal("ERROR_SOURCE_UNAVAILABLE", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("application_telemetry_available").GetBoolean());
            Assert.Equal(JsonValueKind.Array, root.GetProperty("sources").ValueKind);
        }
    }

    [Fact]
    public void A_sql_error_event_defaults_to_no_timeout_claim()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new SqlErrorEvent(), Options));
        var root = document.RootElement;

        Assert.Equal("NOT_APPLICABLE", root.GetProperty("timeout_classification").GetString());
        Assert.Equal("UNKNOWN", root.GetProperty("error_category").GetString());
        Assert.False(root.GetProperty("confirmed").GetBoolean());
        Assert.False(root.GetProperty("is_timeout").GetBoolean());
    }

    [Fact]
    public void Finding_serializes_every_contract_field()
    {
        var finding = new PerformanceFinding
        {
            FindingId = "f-1",
            Category = FindingCategory.Blocking,
            Server = "sql01",
            Database = "AdventureWorks",
            Severity = Severity.High,
            Confidence = Confidence.Medium,
            Summary = "Head blocker detected",
            LikelyCause = "Long open transaction",
            Recommendation = "Review the blocking session",
            Signals = [FindingSignal.HighCpu],
            RecommendedDrilldown = new RecommendedDrilldown { Database = "AdventureWorks" },
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(finding, Options));
        var root = document.RootElement;

        Assert.Equal("blocking", root.GetProperty("category").GetString());
        Assert.Equal("high", root.GetProperty("severity").GetString());
        Assert.Equal("medium", root.GetProperty("confidence").GetString());
        Assert.Equal("high_cpu", root.GetProperty("signals")[0].GetString());
        Assert.Equal("database", root.GetProperty("recommended_drilldown").GetProperty("scope").GetString());
        Assert.False(root.GetProperty("human_review_required").GetBoolean());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("evidence").ValueKind);
    }

    [Fact]
    public void Slow_history_defaults_match_python()
    {
        var candidate = new TimeoutCandidate();

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(candidate, Options));
        var root = document.RootElement;

        Assert.Equal("slow_query_candidate", root.GetProperty("label").GetString());
        Assert.Equal("SLOW_QUERY_CANDIDATE", root.GetProperty("classification").GetString());
        Assert.True(root.GetProperty("slow_query_candidate").GetBoolean());
        Assert.False(root.GetProperty("timeout_candidate").GetBoolean());
        Assert.Equal("medium", root.GetProperty("confidence").GetString());
        Assert.Equal("high", root.GetProperty("severity").GetString());
        Assert.Equal("UNKNOWN", root.GetProperty("workload_type").GetString());
        Assert.Equal(1, root.GetProperty("intervals_observed").GetInt32());
    }

    [Fact]
    public void Investigation_response_never_leaks_raw_tool_calls()
    {
        var response = new InvestigationResponse
        {
            InvestigationId = "inv-1",
            Question = "Why was the order service slow?",
            ToolCallsDetail = [new ToolCall { ToolName = "get_snapshot" }],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, Options));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("tool_calls_detail", out _));
        Assert.Equal("unknown", root.GetProperty("severity").GetString());
        Assert.Equal("low", root.GetProperty("confidence").GetString());
        Assert.True(root.GetProperty("human_review_required").GetBoolean());
        Assert.Equal(
            InvestigationResponse.NoRemediationPerformed,
            root.GetProperty("agent_action").GetString());
    }

    [Fact]
    public void Timestamps_serialize_as_iso8601_with_utc_offset()
    {
        var metadata = new CollectorMetadata
        {
            CollectorName = "active_requests",
            CollectedAt = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero),
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(metadata, Options));

        Assert.Equal(
            "2026-08-24T09:00:00+00:00",
            document.RootElement.GetProperty("collected_at").GetString());
    }
}
