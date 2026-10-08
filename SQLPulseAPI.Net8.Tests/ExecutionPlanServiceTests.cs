using Microsoft.Extensions.Logging.Abstractions;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.QueryStore;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

public sealed class ExecutionPlanServiceTests
{
    private const string Database = "AppDb";
    private const string QueryHash = "0xABCDEF0123456789";

    [Fact]
    public async Task A_plan_is_retrieved_and_parsed_without_executing_user_sql()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows =
            [
                FakeSqlQueryExecutor.Row(("plan_xml", PlanXml)),
            ],
        };
        var result = await Service(executor).GetAsync(Database, QueryHash, 42);

        Assert.Equal(ExecutionPlanStatus.Available, result.Status);
        Assert.Null(result.PlanXml);
        Assert.Contains(result.OperatorSummary, summary => summary.StartsWith("Index Scan", StringComparison.Ordinal));
        Assert.Equal(3, result.TotalOperatorCount);
        Assert.Equal(1, result.OperatorCountsByType["Index Scan"]);
        Assert.Contains(result.Findings, finding => finding.Category == "INDEX_SCAN");
        Assert.Contains(result.Findings, finding => finding.Category == "SPILL");
        Assert.Contains(result.Findings, finding => finding.Category == "IMPLICIT_CONVERSION");
        Assert.Contains(result.Findings, finding => finding.Category == "CARDINALITY_ESTIMATE");
        Assert.Equal(18_500, result.Operators[0].ActualRows);
        Assert.DoesNotContain(executor.ExecutedQueries, query => query.Contains("EXEC ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Generic_hash_sort_and_index_scan_operators_are_not_findings()
    {
        var result = ParsePlan("""
            <RelOp PhysicalOp="Hash Match" EstimateRows="42" EstimatedTotalSubtreeCost="1" />
            <RelOp PhysicalOp="Sort" EstimateRows="10" EstimatedTotalSubtreeCost="1" />
            <RelOp PhysicalOp="Nested Loops" EstimateRows="10" EstimatedTotalSubtreeCost="1" />
            <RelOp PhysicalOp="Index Scan" EstimateRows="120" EstimatedTotalSubtreeCost="1">
              <IndexScan Table="[dbo].[Orders]" />
            </RelOp>
            <RelOp PhysicalOp="Table Scan" EstimateRows="10" EstimatedTotalSubtreeCost="1" />
            <RelOp PhysicalOp="Table Spool" EstimateRows="10" EstimatedTotalSubtreeCost="1" />
            <RelOp PhysicalOp="Clustered Index Scan" EstimateRows="10" EstimatedTotalSubtreeCost="1" />
            """);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void A_material_relative_operator_cost_creates_a_qualified_finding()
    {
        var result = ParsePlan("""
            <RelOp PhysicalOp="Sort" EstimateRows="5000" EstimatedTotalSubtreeCost="40" />
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("SORT", finding.Category);
        Assert.Contains("high relative estimated cost", finding.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(finding.Evidence, item => item.Contains("40%", StringComparison.Ordinal));
        Assert.Equal("medium", finding.Severity);
        Assert.Equal("medium", finding.Confidence);
        Assert.True(finding.HumanReviewRequired);
    }

    [Fact]
    public void Explicit_spill_evidence_creates_a_high_priority_finding()
    {
        var result = ParsePlan("""
            <RelOp PhysicalOp="Hash Match" EstimateRows="10" EstimatedTotalSubtreeCost="1">
              <Warnings><SpillToTempDb SpillLevel="2" SpilledThreadCount="1" /></Warnings>
            </RelOp>
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("SPILL", finding.Category);
        Assert.Equal("high", finding.Severity);
        Assert.Equal("high", finding.Confidence);
        Assert.Contains(finding.Evidence, item => item.Contains("SpillLevel", StringComparison.Ordinal));
    }

    [Fact]
    public void A_large_memory_grant_without_issue_evidence_is_not_reported()
    {
        var result = ParsePlan("<MemoryGrantInfo GrantedMemory=\"120000\" />");

        Assert.DoesNotContain(result.Findings, finding => finding.Category == "MEMORY_GRANT");
    }

    [Fact]
    public void An_explicit_memory_grant_warning_creates_a_finding()
    {
        var result = ParsePlan("""
            <Warnings><MemoryGrantWarning GrantWarningKind="ExcessiveGrant" /></Warnings>
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("MEMORY_GRANT", finding.Category);
        Assert.Equal("high", finding.Confidence);
        Assert.Contains(finding.Evidence, item => item.Contains("MemoryGrantWarning", StringComparison.Ordinal));
    }

    [Fact]
    public void A_convert_issue_without_an_implicit_conversion_expression_is_not_reported()
    {
        var result = ParsePlan("""
            <RelOp PhysicalOp="Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="1">
              <ConvertIssue>Seek Plan</ConvertIssue>
            </RelOp>
            """);

        Assert.DoesNotContain(result.Findings, finding => finding.Category == "IMPLICIT_CONVERSION");
    }

    [Fact]
    public void An_explicit_implicit_conversion_creates_a_high_confidence_finding()
    {
        var result = ParsePlan("""
            <RelOp PhysicalOp="Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="1">
              <ScalarOperator ScalarString="CONVERT_IMPLICIT(bigint,[dbo].[Orders].[OrderId],0)" />
            </RelOp>
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("IMPLICIT_CONVERSION", finding.Category);
        Assert.Equal("high", finding.Confidence);
        Assert.Contains(finding.Evidence, item => item.Contains("CONVERT_IMPLICIT", StringComparison.Ordinal));
        Assert.Contains("review", finding.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_explicit_missing_index_recommendation_is_reported_for_human_review()
    {
        var result = ParsePlan("""
            <MissingIndexes>
              <MissingIndexGroup Impact="75.5"><MissingIndex Database="[AppDb]" /></MissingIndexGroup>
            </MissingIndexes>
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("MISSING_INDEX", finding.Category);
        Assert.Equal("high", finding.Confidence);
        Assert.Equal("medium", finding.Severity);
        Assert.Contains(finding.Evidence, item => item.Contains("75.5%", StringComparison.Ordinal));
        Assert.Contains("existing indexes and workload", finding.Recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.True(finding.HumanReviewRequired);
    }

    [Fact]
    public void Actual_rows_are_summed_and_a_significant_estimate_mismatch_is_reported()
    {
        var result = ParsePlan("""
            <RelOp PhysicalOp="Hash Match" EstimateRows="1" EstimatedTotalSubtreeCost="1">
              <RunTimeInformation>
                <RunTimeCountersPerThread ActualRows="500000" />
                <RunTimeCountersPerThread ActualRows="750000" />
              </RunTimeInformation>
            </RelOp>
            """);

        Assert.Equal(1_250_000, Assert.Single(result.Operators).ActualRows);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("CARDINALITY_ESTIMATE", finding.Category);
        Assert.Contains(finding.Evidence, item => item == "Estimated rows: 1");
        Assert.Contains(finding.Evidence, item => item == "Actual rows: 1,250,000");
        Assert.Equal("high", finding.Severity);
        Assert.Equal("high", finding.Confidence);
    }

    [Fact]
    public void Actual_rows_are_not_invented_when_runtime_statistics_are_absent()
    {
        var result = ParsePlan("<RelOp PhysicalOp=\"Sort\" EstimateRows=\"10\" EstimatedTotalSubtreeCost=\"1\" />");

        Assert.Null(Assert.Single(result.Operators).ActualRows);
        Assert.Contains("Actual execution statistics are unavailable.", result.PlanLimitations);
        Assert.DoesNotContain(result.Findings, finding => finding.Category == "CARDINALITY_ESTIMATE");
    }

    [Fact]
    public void Repeated_significant_operators_are_grouped()
    {
        var result = ParsePlan("""
            <RelOp PhysicalOp="Hash Match" EstimateRows="500" EstimatedTotalSubtreeCost="30" />
            <RelOp PhysicalOp="Hash Match" EstimateRows="900" EstimatedTotalSubtreeCost="40" />
            """);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("HASH_JOIN", finding.Category);
        Assert.Contains("2 Hash Match operators", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Findings_are_limited_and_explicit_warnings_rank_before_cost_observations()
    {
        var mismatchOperators = string.Join(Environment.NewLine,
            Enumerable.Range(0, 12).Select(index =>
                $"<RelOp PhysicalOp=\"Operator{index}\" EstimateRows=\"1\" EstimatedTotalSubtreeCost=\"30\"><RunTimeCountersPerThread ActualRows=\"10000\" /></RelOp>"));
        var result = ParsePlan($"""
            <RelOp PhysicalOp="Sort" EstimateRows="100" EstimatedTotalSubtreeCost="40">
              <Warnings><SpillToTempDb SpillLevel="1" /></Warnings>
            </RelOp>
            {mismatchOperators}
            """);

        Assert.Equal(10, result.Findings.Count);
        Assert.Equal("SPILL", result.Findings[0].Category);
    }

    [Fact]
    public void Row_volume_without_cost_is_low_severity_and_low_confidence()
    {
        var result = ParsePlan("<RelOp PhysicalOp=\"Sort\" EstimateRows=\"100000\" EstimatedTotalSubtreeCost=\"1\" />");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("low", finding.Severity);
        Assert.Equal("low", finding.Confidence);
    }

    [Fact]
    public void Every_recommendation_requires_human_review_and_is_tied_to_evidence()
    {
        var result = ParsePlan("""
            <RelOp PhysicalOp="Sort" EstimateRows="5000" EstimatedTotalSubtreeCost="40" />
            <MissingIndexes><MissingIndexGroup Impact="55"><MissingIndex /></MissingIndexGroup></MissingIndexes>
            """);

        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding =>
        {
            Assert.True(finding.HumanReviewRequired);
            Assert.NotEmpty(finding.Evidence);
            Assert.Contains("review", finding.Recommendation, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task A_plan_can_include_raw_xml_only_when_explicitly_requested()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows = [FakeSqlQueryExecutor.Row(("plan_xml", PlanXml))],
        };

        var result = await Service(executor).GetAsync(Database, QueryHash, 42, includeXml: true);

        Assert.Equal(PlanXml, result.PlanXml);
    }

    [Fact]
    public async Task A_missing_plan_is_reported_without_fabricating_findings()
    {
        var result = await Service(new FakeSqlQueryExecutor()).GetAsync(Database, QueryHash, 42);

        Assert.Equal(ExecutionPlanStatus.NotFound, result.Status);
        Assert.Empty(result.Findings);
        Assert.Null(result.PlanXml);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0xnot-hex")]
    public async Task An_invalid_query_hash_does_not_reach_sql_server(string queryHash)
    {
        var executor = new FakeSqlQueryExecutor();
        var result = await Service(executor).GetAsync(Database, queryHash, 42);

        Assert.Equal(ExecutionPlanStatus.NotFound, result.Status);
        Assert.Equal(0, executor.ExecuteCount);
    }

    [Fact]
    public async Task An_unavailable_query_store_is_reported_explicitly()
    {
        var queryStore = new StubQueryStoreCollector { Status = QueryStoreStatus.Disabled };
        var result = await Service(new FakeSqlQueryExecutor(), queryStore)
            .GetAsync(Database, QueryHash, 42);

        Assert.Equal(ExecutionPlanStatus.Unavailable, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task A_cached_plan_is_used_only_when_query_store_is_unavailable_and_selection_is_unique()
    {
        var queryStore = new StubQueryStoreCollector { Status = QueryStoreStatus.Disabled };
        var executor = new FakeSqlQueryExecutor
        {
            Rows = [FakeSqlQueryExecutor.Row(("plan_xml", PlanXml))],
        };

        var result = await Service(executor, queryStore)
            .GetAsync(Database, QueryHash, null, null);

        Assert.Equal(ExecutionPlanStatus.Available, result.Status);
        Assert.Contains(result.Warnings, warning => warning.Contains("plan cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Multiple_matching_plans_are_not_guessed()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows =
            [
                FakeSqlQueryExecutor.Row(("plan_xml", PlanXml)),
                FakeSqlQueryExecutor.Row(("plan_xml", PlanXml)),
            ],
        };

        var result = await Service(executor).GetAsync(Database, QueryHash, null, null);

        Assert.Equal(ExecutionPlanStatus.NotFound, result.Status);
        Assert.Contains("plan_id", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Findings);
    }

    private static ExecutionPlanService Service(
        FakeSqlQueryExecutor executor,
        StubQueryStoreCollector? queryStore = null) =>
        new(
            queryStore ?? new StubQueryStoreCollector(),
            executor,
            NullLogger<ExecutionPlanService>.Instance);

    private static ExecutionPlanResponse ParsePlan(string operatorsAndWarnings)
    {
        var response = new ExecutionPlanResponse();
        var xml = $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements><StmtSimple StatementSubTreeCost="100">
                <QueryPlan>{operatorsAndWarnings}</QueryPlan>
              </StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
        PlanXmlParser.Populate(response, xml);
        return response;
    }

    [Fact]
    public void ParsePlan_reads_stored_procedure_parameter_metadata_and_masks_sensitive_values()
    {
        var result = ParsePlan("""
          <ParameterList>
            <ColumnReference Column="@CustomerId" ParameterDataType="int" ParameterCompiledValue="(42)" ParameterRuntimeValue="(99)" />
            <ColumnReference Column="@Password" ParameterDataType="nvarchar(100)" ParameterCompiledValue="N'secret'" />
          </ParameterList>
        """);

        Assert.Equal(2, result.Parameters.Count);
        Assert.Equal("@CustomerId", result.Parameters[0].Name);
        Assert.Equal("int", result.Parameters[0].DataType);
        Assert.Equal("(42)", result.Parameters[0].CompiledValue);
        Assert.Equal("(99)", result.Parameters[0].RuntimeValue);
        Assert.Equal("[REDACTED]", result.Parameters[1].CompiledValue);
    }

    private const string PlanXml = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementSubTreeCost="4.2">
              <QueryPlan>
                <MemoryGrantInfo GrantedMemory="120000" />
                <RelOp PhysicalOp="Index Scan" EstimateRows="120" EstimatedTotalSubtreeCost="1.2">
                  <IndexScan Table="[dbo].[Orders]" />
                  <RunTimeInformation><RunTimeCountersPerThread ActualRows="18500" /></RunTimeInformation>
                  <Warnings><SpillToTempDb SpillOccurred="1" /></Warnings>
              <ConvertIssue>Seek Plan</ConvertIssue>
              <ScalarOperator ScalarString="CONVERT_IMPLICIT(int,[dbo].[Orders].[OrderId],0)" />
                </RelOp>
                <RelOp PhysicalOp="Key Lookup" EstimateRows="1" />
                <RelOp PhysicalOp="Sort" EstimateRows="10" />
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;
}
