using System.Text.Json;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.Responses;
using Xunit;

namespace SQLPulseAPI.Net8.Tests.Compatibility;

/// <summary>
/// Golden-master tests: the .NET models must consume and reproduce the Python JSON contract.
/// </summary>
/// <remarks>
/// <para>
/// Each test round-trips a recorded Python response through the corresponding .NET model
/// and asserts the re-serialized JSON has the same shape. That single operation covers
/// both directions of compatibility: the model must be able to <em>read</em> everything
/// Python emits, and must <em>emit</em> everything a client expects.
/// </para>
/// <para>
/// A property Python emits and .NET drops would silently blank a field in the UI. A
/// property .NET adds is equally reported, because it means the two implementations have
/// started to diverge even though nothing is visibly broken yet.
/// </para>
/// </remarks>
public sealed class PythonContractTests
{
    private static string RoundTrip<T>(string pythonJson)
    {
        var model = JsonSerializer.Deserialize<T>(pythonJson, SqlPulseJson.Options);
        Assert.NotNull(model);

        return JsonSerializer.Serialize(model, SqlPulseJson.Options);
    }

    private static void AssertRoundTrips<T>(string pythonJson, params string[] ignore) =>
        JsonShape.AssertMatches(pythonJson, RoundTrip<T>(pythonJson), ignore);

    // --- Simple responses -----------------------------------------------------

    [Fact]
    public void Health_matches_python() =>
        AssertRoundTrips<HealthResponse>(PythonResponseFixtures.Health);

    [Fact]
    public void Connection_test_matches_python() =>
        AssertRoundTrips<ConnectionTestResponse>(PythonResponseFixtures.ConnectionTest);

    [Fact]
    public void Database_list_matches_python() =>
        AssertRoundTrips<DatabaseListResponse>(PythonResponseFixtures.DatabaseList);

    [Fact]
    public void Error_envelope_matches_python() =>
        AssertRoundTrips<ErrorResponse>(PythonResponseFixtures.Error);

    // --- Performance ----------------------------------------------------------

    [Fact]
    public void Performance_snapshot_matches_python() =>
        AssertRoundTrips<PerformanceSnapshot>(PythonResponseFixtures.PerformanceSnapshot);

    [Fact]
    public void Analysis_result_matches_python() =>
        AssertRoundTrips<AnalysisResult>(PythonResponseFixtures.AnalysisResult);

    [Fact]
    public void Slow_history_matches_python() =>
        AssertRoundTrips<SlowHistoryResponse>(PythonResponseFixtures.SlowHistory);

    [Fact]
    public void Slow_history_reports_an_unavailable_query_store_the_same_way() =>
        AssertRoundTrips<SlowHistoryResponse>(PythonResponseFixtures.SlowHistoryUnavailable);

    // --- History --------------------------------------------------------------

    [Fact]
    public void Query_history_matches_python() =>
        AssertRoundTrips<QueryHistoryResponse>(PythonResponseFixtures.QueryHistory);

    [Fact]
    public void Query_baseline_matches_python() =>
        AssertRoundTrips<QueryBaselineResponse>(PythonResponseFixtures.QueryBaseline);

    [Fact]
    public void Query_baseline_reports_insufficient_data_the_same_way() =>
        AssertRoundTrips<QueryBaselineResponse>(PythonResponseFixtures.QueryBaselineInsufficient);

    // --- Agent ----------------------------------------------------------------

    [Fact]
    public void Investigation_matches_python() =>
        AssertRoundTrips<InvestigationResponse>(PythonResponseFixtures.Investigation);

    [Fact]
    public void Investigation_audit_list_matches_python() =>
        AssertRoundTrips<InvestigationAuditListResponse>(PythonResponseFixtures.InvestigationAuditList);

    [Fact]
    public void Investigation_audit_detail_matches_python() =>
        AssertRoundTrips<InvestigationAuditDetailResponse>(
            PythonResponseFixtures.InvestigationAuditNotFound);

    [Fact]
    public void The_investigation_response_withholds_the_raw_tool_detail()
    {
        // Python marks tool_calls_detail Field(exclude=True) because it can contain raw
        // SQL. The audit trail keeps it; the API response must not.
        var response = JsonSerializer.Deserialize<InvestigationResponse>(
            PythonResponseFixtures.Investigation, SqlPulseJson.Options)!;

        response.ToolCallsDetail =
        [
            new ToolCall
            {
                ToolName = "get_blocking",
                Arguments = new Dictionary<string, object?>
                {
                    ["sql"] = "SELECT * FROM dbo.Orders WHERE SecretColumn = 'x'",
                },
            },
        ];

        var json = JsonSerializer.Serialize(response, SqlPulseJson.Options);

        Assert.DoesNotContain("tool_calls_detail", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretColumn", json, StringComparison.Ordinal);
    }

    // --- Values the UI branches on --------------------------------------------

    [Fact]
    public void Meaningful_values_survive_the_round_trip()
    {
        // Shape parity alone would pass if every value were dropped, so the fields the UI
        // actually renders are checked to still hold what Python sent.
        var analysis = JsonSerializer.Deserialize<AnalysisResult>(
            PythonResponseFixtures.AnalysisResult, SqlPulseJson.Options)!;

        Assert.Equal(OverallStatus.Degraded, analysis.OverallStatus);
        Assert.Equal(1, analysis.TotalFindings);
        Assert.Equal(1, analysis.HighCount);
        Assert.Equal("sql01", analysis.Server);

        var finding = Assert.Single(analysis.Findings);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(Confidence.High, finding.Confidence);
        Assert.Equal(FindingCategory.Blocking, finding.Category);
        Assert.Equal("AppDb", finding.Database);
        Assert.True(finding.HumanReviewRequired);
        Assert.NotEmpty(finding.Evidence);
        Assert.NotEmpty(finding.ValidationSteps);

        var affected = Assert.Single(analysis.AffectedDatabases);
        Assert.Equal("AppDb", affected.Database);
        Assert.Equal(1, affected.FindingCount);

        Assert.NotNull(analysis.CurrentHealth);
        Assert.Equal(OverallStatus.Degraded, analysis.CurrentHealth.Status);
        Assert.Single(analysis.CurrentHealth.Findings);
        Assert.Empty(analysis.OptimizationOpportunities);
        Assert.Single(analysis.ExcludedQueries);
    }

    [Fact]
    public void Slow_history_values_survive_the_round_trip()
    {
        var slow = JsonSerializer.Deserialize<SlowHistoryResponse>(
            PythonResponseFixtures.SlowHistory, SqlPulseJson.Options)!;

        Assert.Equal(QueryStoreStatus.Available, slow.QueryStoreStatus);
        Assert.True(slow.TimeoutClassificationAvailable);
        Assert.Equal(30_000, slow.ThresholdMs);
        Assert.Equal(30_000, slow.ApplicationTimeoutMs);

        var candidate = Assert.Single(slow.Candidates);
        Assert.Equal(CandidateClassification.TimeoutCandidate, candidate.Classification);
        Assert.True(candidate.IsSlowQueryCandidate);
        Assert.True(candidate.IsTimeoutCandidate);
        Assert.Equal(WorkloadType.ApplicationOrInteractive, candidate.WorkloadType);
        Assert.Equal(42_000.0, candidate.MaxDurationMs);
        Assert.Equal(1, candidate.Rank);
        Assert.NotNull(candidate.BaselineComparison);
        Assert.Equal(BaselineStatus.Ready, candidate.BaselineComparison.BaselineStatus);
        Assert.NotEmpty(candidate.WorkloadEvidence);
    }

    [Fact]
    public void A_disabled_query_store_still_reports_a_usable_response()
    {
        // The UI renders this state rather than showing an error, so the values it reads
        // must be present even though no candidates were found.
        var slow = JsonSerializer.Deserialize<SlowHistoryResponse>(
            PythonResponseFixtures.SlowHistoryUnavailable, SqlPulseJson.Options)!;

        Assert.Equal(QueryStoreStatus.Disabled, slow.QueryStoreStatus);
        Assert.False(slow.TimeoutClassificationAvailable);
        Assert.Null(slow.ApplicationTimeoutMs);
        Assert.Empty(slow.Candidates);
        Assert.NotNull(slow.Message);
    }

    [Fact]
    public void Investigation_values_survive_the_round_trip()
    {
        var investigation = JsonSerializer.Deserialize<InvestigationResponse>(
            PythonResponseFixtures.Investigation, SqlPulseJson.Options)!;

        Assert.Equal("high", investigation.Severity);
        Assert.Equal("medium", investigation.Confidence);
        Assert.Equal("AppDb", investigation.AffectedDatabase);
        Assert.True(investigation.HumanReviewRequired);
        Assert.Equal(InvestigationResponse.NoRemediationPerformed, investigation.AgentAction);
        Assert.Equal(2, investigation.ToolCallCount);
        Assert.Equal(["get_blocking", "get_active_requests"], investigation.ToolsUsed);
        Assert.NotEmpty(investigation.WhatIsKnown);
        Assert.NotEmpty(investigation.WhatIsUncertain);
        Assert.NotEmpty(investigation.RecommendedInvestigation);
    }

    [Fact]
    public void Audit_records_survive_the_round_trip_and_still_deny_remediation()
    {
        var list = JsonSerializer.Deserialize<InvestigationAuditListResponse>(
            PythonResponseFixtures.InvestigationAuditList, SqlPulseJson.Options)!;

        Assert.Equal(1, list.Total);

        var record = Assert.Single(list.Investigations);
        Assert.False(record.RemediationPerformed);
        Assert.True(record.HumanReviewRequired);
        Assert.Equal("sql01", record.Server);
        Assert.Equal("AppDb", record.Database);
        Assert.Equal(["usp_GetOrders", "0xAB12CD34"], record.FindingReferences);

        var call = Assert.Single(record.ToolsCalled);
        Assert.Equal("get_blocking", call.ToolName);
        Assert.True(call.Success);
    }

    [Fact]
    public void A_baseline_with_too_few_samples_emits_no_statistics()
    {
        // Zero-valued statistics would be indistinguishable from a query that genuinely
        // measured zero, so Python omits them entirely and .NET must too.
        var baseline = JsonSerializer.Deserialize<QueryBaselineResponse>(
            PythonResponseFixtures.QueryBaselineInsufficient, SqlPulseJson.Options)!;

        Assert.Equal(BaselineStatus.InsufficientData, baseline.BaselineStatus);
        Assert.Empty(baseline.Metrics);
    }

    [Fact]
    public void Collector_failure_metadata_survives_the_round_trip()
    {
        // A collector the monitoring login cannot read must still be distinguishable from
        // one that found nothing; the UI renders those states differently.
        var snapshot = JsonSerializer.Deserialize<PerformanceSnapshot>(
            PythonResponseFixtures.PerformanceSnapshot, SqlPulseJson.Options)!;

        Assert.NotNull(snapshot.ActiveRequests);
        Assert.True(snapshot.ActiveRequests.Metadata.Success);
        Assert.False(snapshot.ActiveRequests.Metadata.PermissionIssue);
        Assert.Null(snapshot.ActiveRequests.Metadata.ErrorMessage);
        Assert.Equal("active_requests", snapshot.ActiveRequests.Metadata.CollectorName);

        Assert.NotNull(snapshot.WaitStatistics);
        Assert.Equal(WaitStatisticsApplicability.ServerLevel, snapshot.WaitStatistics.Applicability);
        Assert.Equal(WaitDeltaStatus.Available, snapshot.WaitStatistics.DeltaStatus);
        Assert.NotNull(snapshot.WaitStatistics.Totals);
        Assert.NotNull(snapshot.WaitStatistics.Delta);
        Assert.NotNull(snapshot.WaitStatistics.SqlServerStartTime);

        Assert.NotNull(snapshot.BlockingSessions);
        Assert.Equal(1, snapshot.BlockingSessions.BlockingChainCount);
    }
}
