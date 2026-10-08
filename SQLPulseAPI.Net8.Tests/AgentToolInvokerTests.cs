using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Collectors;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Behaviour of the approved agent tools: the allow-list that bounds what an AI can do,
/// reuse of the same services the REST endpoints call, bounded evidence, sanitized output,
/// and an audit trail that records what happened without storing the payloads.
/// </summary>
public class AgentToolInvokerTests
{
    private const string Database = "AppDb";

    private static IOptions<SqlServerOptions> SqlServer() => Options.Create(new SqlServerOptions
    {
        Server = "sql01",
        Username = "sqlpulse",
        Password = "irrelevant-for-tests",
        DefaultDatabase = "master",
    });

    private sealed class Harness
    {
        public FakeSqlQueryExecutor Executor { get; } = new();

        public StubAnalysisEngine AnalysisEngine { get; } = new();

        public StubDatabaseDiscoveryService Discovery { get; } = StubDatabaseDiscoveryService.Allowing(Database);

        public InMemoryHistoryRepository History { get; } = new();

        public StubBaselineService Baselines { get; } = new();

        public StubSlowHistoryService SlowHistory { get; } = new();

        public StubQueryStoreCollector QueryStore { get; } = new();

        public StubSlowQueryCandidateBuilder CandidateBuilder { get; } = new();

        public StubExecutionPlanService ExecutionPlans { get; } = new();

        public StubSqlErrorService SqlErrors { get; } = new();

        public StubSqlErrorCorrelationService SqlErrorCorrelation { get; } = new();

        public AnalysisOptions Analysis { get; } = new();

        public AgentToolInvoker Build() => new(
            AnalysisEngine,
            Discovery,
            new ActiveRequestsCollector(Executor, SqlServer(), NullLogger<ActiveRequestsCollector>.Instance),
            new BlockingSessionsCollector(Executor, SqlServer(), NullLogger<BlockingSessionsCollector>.Instance),
            new LongRunningQueriesCollector(Executor, SqlServer(), NullLogger<LongRunningQueriesCollector>.Instance),
            new TopCpuQueriesCollector(Executor, SqlServer(), NullLogger<TopCpuQueriesCollector>.Instance),
            new TopIoQueriesCollector(Executor, SqlServer(), NullLogger<TopIoQueriesCollector>.Instance),
            History,
            Baselines,
            SlowHistory,
            QueryStore,
            CandidateBuilder,
            ExecutionPlans,
            SqlErrors,
            SqlErrorCorrelation,
            SqlServer(),
            Options.Create(Analysis),
            NullLogger<AgentToolInvoker>.Instance);
    }

    private static string Args(params (string Key, JsonNode? Value)[] values)
    {
        var arguments = new JsonObject();
        foreach (var (key, value) in values)
        {
            arguments[key] = value;
        }

        return arguments.ToJsonString();
    }

    private static SqlRow ActiveRequestRow(int sessionId, string sqlText = "SELECT 1") =>
        FakeSqlQueryExecutor.Row(
            ("session_id", sessionId),
            ("request_id", 0),
            ("status", "running"),
            ("command", "SELECT"),
            ("sql_text", sqlText),
            ("database_name", Database),
            ("wait_type", null),
            ("wait_time_ms", 0L),
            ("cpu_time_ms", 10L),
            ("total_elapsed_time_ms", 100L),
            ("reads", 1L),
            ("writes", 0L),
            ("logical_reads", 5L),
            ("blocking_session_id", 0),
            ("open_transaction_count", 0),
            ("login_name", "app_user"),
            ("host_name", "APPSRV1"),
            ("program_name", "SQLPulseTests"));

    private static SqlRow PlanCacheRow(int index) =>
        FakeSqlQueryExecutor.Row(
            ("query_hash", $"0x{index:X8}"),
            ("query_plan_hash", $"0x{index:X8}"),
            ("sql_text", "SELECT 1"),
            ("database_name", Database),
            ("execution_count", 10L),
            ("total_worker_time_ms", 100.0),
            ("avg_worker_time_ms", 10.0),
            ("total_elapsed_time_ms", 200.0),
            ("avg_elapsed_time_ms", 20.0),
            ("total_logical_reads", 500L),
            ("avg_logical_reads", 50L),
            ("total_physical_reads", 5L),
            ("avg_physical_reads", 1L),
            ("last_execution_time", DateTime.UtcNow));

    // --- The allow-list ------------------------------------------------------

    [Fact]
    public async Task An_unapproved_tool_is_refused_and_recorded()
    {
        var invocation = await new Harness().Build()
            .InvokeAsync("run_sql", Args(("sql", "DROP TABLE dbo.Orders")));

        Assert.Contains("not approved", invocation.Payload["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(invocation.Audit.Success);
        Assert.Equal(AgentToolInvoker.NotApprovedMessage, invocation.Audit.Error);
        Assert.Equal(0.0, invocation.Audit.DurationSeconds);
        Assert.Equal("run_sql", invocation.Audit.ToolName);
    }

    /// <summary>
    /// The catalogue is the agent's entire capability surface, and it is read-only: there
    /// is no tool for arbitrary SQL, for modifying data, for killing sessions, for creating
    /// indexes, for forcing plans, or for changing configuration.
    /// </summary>
    [Theory]
    [InlineData("run_sql")]
    [InlineData("execute_query")]
    [InlineData("kill_session")]
    [InlineData("create_index")]
    [InlineData("force_plan")]
    [InlineData("set_configuration")]
    [InlineData("update_statistics")]
    public void No_mutating_tool_is_exposed(string name)
    {
        Assert.DoesNotContain(name, AgentToolDefinitions.ApprovedNames);
    }

    [Fact]
    public async Task Every_approved_tool_has_an_executor()
    {
        var invoker = new Harness().Build();

        foreach (var tool in AgentToolDefinitions.All)
        {
            var invocation = await invoker.InvokeAsync(tool.Name, "{}");
            var error = invocation.Payload["error"]?.GetValue<string>();

            Assert.DoesNotContain("no executor registered", error ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Get_execution_plan_returns_compact_summary_and_evidence_to_the_model()
    {
        var harness = new Harness();
        harness.ExecutionPlans.Response = new ExecutionPlanResponse
        {
            Status = ExecutionPlanStatus.Available,
            QueryHash = "0xABCDEF0123456789",
            QueryId = 42,
            PlanId = 7,
            Database = Database,
            StatementCount = 1,
            TotalOperatorCount = 100,
            OperatorCountsByType = new Dictionary<string, int> { ["Hash Match"] = 38 },
            OperatorSummary = ["Hash Match (38)"],
            Operators = Enumerable.Range(0, 100)
                .Select(index => new ExecutionPlanOperator { Type = $"Operator {index}" })
                .ToList(),
            HighestCostOperators = [new ExecutionPlanOperator { Type = "Hash Match", EstimatedRows = 125_000 }],
            Findings =
            [
                new ExecutionPlanFinding
                {
                    Category = "HASH_JOIN",
                    Operator = "Hash Match",
                    Summary = "High estimated row volume within the plan.",
                    Evidence = ["Estimated rows: 125,000"],
                    Severity = "low",
                    Confidence = "low",
                    Recommendation = "Review the plan with a human reviewer.",
                },
            ],
            Warnings = ["SpillToTempDb (SpillLevel=1)"],
            PlanLimitations = ["Actual execution statistics are unavailable."],
        };

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetExecutionPlan,
            Args(("database", JsonValue.Create(Database)), ("query_hash", JsonValue.Create("0xABCDEF0123456789"))));

        Assert.Equal(100, invocation.Payload["total_operator_count"]!.GetValue<int>());
        Assert.Equal("Hash Match (38)", invocation.Payload["operator_summary"]!.AsArray()[0]!.GetValue<string>());
        Assert.Null(invocation.Payload["operators"]);
        Assert.Single(invocation.Payload["important_operators"]!.AsArray());
        Assert.Single(invocation.Payload["findings"]!.AsArray());
        Assert.Single(invocation.Payload["warnings"]!.AsArray());
        Assert.Equal("Actual execution statistics are unavailable.",
            invocation.Payload["plan_limitations"]!.AsArray()[0]!.GetValue<string>());
    }

    // --- Argument handling ---------------------------------------------------

    [Fact]
    public async Task Malformed_arguments_become_a_message_the_model_can_act_on()
    {
        var invocation = await new Harness().Build()
            .InvokeAsync(AgentToolNames.GetDatabaseHealth, "{not valid json");

        Assert.Equal("database parameter is required", invocation.Payload["error"]!.GetValue<string>());
        Assert.False(invocation.Audit.Success);
    }

    [Fact]
    public async Task A_database_outside_the_allow_list_is_refused()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetDatabaseHealth,
            Args(("database", "master; DROP DATABASE AppDb")));

        Assert.Equal(StubDatabaseDiscoveryService.RejectionMessage, invocation.Payload["error"]!.GetValue<string>());
        Assert.Empty(harness.AnalysisEngine.Requests);
    }

    [Fact]
    public async Task An_optional_database_filter_is_validated_too()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetActiveRequests,
            Args(("database", "NotDiscovered")));

        Assert.Equal(StubDatabaseDiscoveryService.RejectionMessage, invocation.Payload["error"]!.GetValue<string>());
        Assert.Equal(0, harness.Executor.ExecuteCount);
    }

    [Fact]
    public async Task An_unparseable_time_range_is_refused_before_any_query_runs()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetSlowHistory,
            Args(("database", Database), ("start_time", "some time last week"), ("end_time", "today")));

        Assert.Contains("Cannot parse start_time", invocation.Payload["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(harness.SlowHistory.LastCall);
    }

    // --- Reuse of the REST services -----------------------------------------

    [Fact]
    public async Task Database_health_runs_the_shared_analysis_engine_scoped_to_the_database()
    {
        var harness = new Harness();
        harness.AnalysisEngine.Result = new AnalysisResult
        {
            OverallStatus = OverallStatus.Degraded,
            TotalFindings = 3,
            CurrentHealth = new HealthSection { Status = OverallStatus.Degraded },
        };

        var invocation = await harness.Build()
            .InvokeAsync(AgentToolNames.GetDatabaseHealth, Args(("database", Database)));

        var request = Assert.Single(harness.AnalysisEngine.Requests);
        Assert.Equal(AnalysisScope.Database, request.Scope);
        Assert.Equal(Database, request.Database);
        Assert.Equal("DEGRADED", invocation.Payload["overall_status"]!.GetValue<string>());
        Assert.Equal(3, invocation.Payload["total_findings"]!.GetValue<int>());
    }

    [Fact]
    public async Task Server_health_runs_the_shared_analysis_engine_at_server_scope()
    {
        var harness = new Harness();

        await harness.Build().InvokeAsync(AgentToolNames.GetServerHealth, "{}");

        Assert.Equal(AnalysisScope.Server, Assert.Single(harness.AnalysisEngine.Requests).Scope);
    }

    [Fact]
    public async Task Active_requests_come_from_the_shared_collector()
    {
        var harness = new Harness();
        harness.Executor.Rows = [ActiveRequestRow(55), ActiveRequestRow(56)];

        var invocation = await harness.Build().InvokeAsync(AgentToolNames.GetActiveRequests, "{}");

        Assert.Equal(2, invocation.Payload["request_count"]!.GetValue<int>());
        Assert.Equal(2, invocation.Payload["requests"]!.AsArray().Count);
        Assert.Equal(1, harness.Executor.ExecuteCount);
    }

    [Fact]
    public async Task Query_history_comes_from_the_shared_repository()
    {
        var harness = new Harness();
        harness.History.Seed(new QueryMetricRecord
        {
            QueryHash = "0xABCD",
            Database = Database,
            CollectedAt = DateTimeOffset.UtcNow,
        });

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetQueryHistory,
            Args(("query_hash", "0xABCD"), ("database", Database)));

        Assert.Equal(1, invocation.Payload["sample_count"]!.GetValue<int>());
        Assert.Equal("0xABCD", invocation.Payload["query_hash"]!.GetValue<string>());
    }

    [Fact]
    public async Task Query_regressions_are_filtered_from_the_shared_analysis()
    {
        var harness = new Harness();
        harness.AnalysisEngine.Result = new AnalysisResult
        {
            Findings =
            [
                new PerformanceFinding { Category = FindingCategory.QueryRegression, Database = Database },
                new PerformanceFinding { Category = FindingCategory.Blocking, Database = Database },
            ],
        };

        var invocation = await harness.Build()
            .InvokeAsync(AgentToolNames.GetQueryRegressions, Args(("database", Database)));

        Assert.Equal(1, invocation.Payload["regression_count"]!.GetValue<int>());
        Assert.Single(invocation.Payload["regressions"]!.AsArray());
    }

    [Fact]
    public async Task Slow_history_calls_the_shared_service_with_resolved_dates()
    {
        var harness = new Harness();

        await harness.Build().InvokeAsync(
            AgentToolNames.GetSlowHistory,
            Args(
                ("database", Database),
                ("start_time", "2026-01-05T00:00:00"),
                ("end_time", "2026-01-05T23:59:59"),
                ("threshold_ms", 15000)));

        var call = harness.SlowHistory.LastCall!.Value;
        Assert.Equal(Database, call.Database);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), call.Start);

        // The inclusive end-of-day the model supplied becomes the following midnight.
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero), call.End);
        Assert.Equal(15000, call.ThresholdMs);
        Assert.True(call.GroupResults);
    }

    // --- Bounded evidence ----------------------------------------------------

    [Fact]
    public async Task Session_evidence_is_capped_but_the_count_stays_truthful()
    {
        var harness = new Harness();
        harness.Executor.Rows = Enumerable.Range(1, 60).Select(id => ActiveRequestRow(id)).ToList();

        var invocation = await harness.Build().InvokeAsync(AgentToolNames.GetActiveRequests, "{}");

        Assert.Equal(60, invocation.Payload["request_count"]!.GetValue<int>());
        Assert.Equal(50, invocation.Payload["requests"]!.AsArray().Count);
    }

    [Fact]
    public async Task Plan_cache_evidence_is_capped_at_twenty()
    {
        var harness = new Harness();
        harness.Executor.Rows = Enumerable.Range(1, 40).Select(PlanCacheRow).ToList();

        var invocation = await harness.Build().InvokeAsync(AgentToolNames.GetTopCpuQueries, "{}");

        Assert.Equal(40, invocation.Payload["query_count"]!.GetValue<int>());
        Assert.Equal(20, invocation.Payload["queries"]!.AsArray().Count);
    }

    // --- Timeout classification ---------------------------------------------

    [Fact]
    public async Task Timeout_classification_is_unavailable_when_no_application_timeout_is_known()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetTimeoutCandidates,
            Args(("database", Database), ("start_time", "2026-01-05"), ("end_time", "2026-01-06")));

        Assert.False(invocation.Payload["timeout_classification_available"]!.GetValue<bool>());
        Assert.Equal("evaluated", invocation.Payload["slow_query_analysis_status"]!.GetValue<string>());
        Assert.Equal("not_evaluated", invocation.Payload["timeout_classification_status"]!.GetValue<string>());
        Assert.Contains(
            "ApplicationTimeoutMs is not configured",
            invocation.Payload["timeout_classification_message"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Null(harness.CandidateBuilder.LastApplicationTimeoutMs);
    }

    [Fact]
    public async Task The_configured_application_timeout_is_used_when_the_model_does_not_supply_one()
    {
        var harness = new Harness();
        harness.Analysis.ApplicationTimeoutMs = 30_000;

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetTimeoutCandidates,
            Args(("database", Database), ("start_time", "2026-01-05"), ("end_time", "2026-01-06")));

        Assert.True(invocation.Payload["timeout_classification_available"]!.GetValue<bool>());
        Assert.Equal("evaluated", invocation.Payload["timeout_classification_status"]!.GetValue<string>());
        Assert.Equal(30_000, harness.CandidateBuilder.LastApplicationTimeoutMs);
    }

    /// <summary>
    /// An unusable application timeout is dropped rather than coerced: "unknown" is a
    /// different answer from any number that could be invented, and inventing one would
    /// turn merely slow queries into reported timeouts.
    /// </summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task An_unusable_application_timeout_is_treated_as_unknown(string value)
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetTimeoutCandidates,
            Args(
                ("database", Database),
                ("start_time", "2026-01-05"),
                ("end_time", "2026-01-06"),
                ("threshold_ms", 30_000),
                ("application_timeout_ms", value)));

        Assert.False(invocation.Payload["timeout_classification_available"]!.GetValue<bool>());
        Assert.Equal("not_evaluated", invocation.Payload["timeout_classification_status"]!.GetValue<string>());
        Assert.Null(harness.CandidateBuilder.LastApplicationTimeoutMs);
    }

    [Fact]
    public async Task An_inaccessible_query_store_reports_no_candidates_rather_than_failing()
    {
        var harness = new Harness();
        harness.QueryStore.Status = QueryStoreStatus.Disabled;

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetTimeoutCandidates,
            Args(("database", Database), ("start_time", "2026-01-05"), ("end_time", "2026-01-06")));

        Assert.Equal(0, invocation.Payload["candidate_count"]!.GetValue<int>());
        Assert.Equal("DISABLED", invocation.Payload["query_store_status"]!.GetValue<string>());
        Assert.Equal("data_unavailable", invocation.Payload["slow_query_analysis_status"]!.GetValue<string>());
        Assert.Equal("data_unavailable", invocation.Payload["timeout_classification_status"]!.GetValue<string>());
        Assert.True(invocation.Audit.Success);
    }

    [Fact]
    public async Task An_unusable_threshold_is_reported_instead_of_silently_defaulted()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetSlowHistory,
            Args(
                ("database", Database),
                ("start_time", "2026-01-05"),
                ("end_time", "2026-01-06"),
                ("threshold_ms", "abc")));

        Assert.Contains("threshold_ms", invocation.Payload["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(harness.SlowHistory.LastCall);
    }

    // --- SQL error tools -----------------------------------------------------

    [Fact]
    public void The_sql_error_tools_are_approved()
    {
        Assert.Contains(AgentToolNames.GetSqlErrors, AgentToolDefinitions.ApprovedNames);
        Assert.Contains(AgentToolNames.GetSqlErrorDetails, AgentToolDefinitions.ApprovedNames);
        Assert.Contains(AgentToolNames.InvestigateSqlError, AgentToolDefinitions.ApprovedNames);
    }

    [Fact]
    public async Task Listing_sql_errors_goes_through_the_same_service_as_the_endpoint()
    {
        var harness = new Harness();

        await harness.Build().InvokeAsync(
            AgentToolNames.GetSqlErrors,
            Args(
                ("database", Database),
                ("start_time", "2026-01-05T09:00:00Z"),
                ("end_time", "2026-01-05T11:00:00Z"),
                ("category", "TIMEOUT"),
                ("confirmed_only", true)));

        var query = Assert.Single(harness.SqlErrors.Queries);
        Assert.Equal(Database, query.Database);
        Assert.Equal(SqlErrorCategory.Timeout, query.Category);
        Assert.True(query.ConfirmedOnly);
    }

    /// <summary>
    /// The fields that stop a model reading an empty list as a healthy period are the
    /// whole point of the tool, so they must survive the trip into the payload.
    /// </summary>
    [Fact]
    public async Task The_visibility_of_the_error_sources_is_forwarded_to_the_model()
    {
        var harness = new Harness();
        harness.SqlErrors.ListResponse = new SqlErrorListResponse
        {
            Status = SqlErrorQueryStatus.ErrorSourceUnavailable,
            Message = "No error source could be consulted.",
            ApplicationTelemetryAvailable = false,
        };

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetSqlErrors,
            Args(("start_time", "2026-01-05T09:00:00Z"), ("end_time", "2026-01-05T11:00:00Z")));

        Assert.Equal("ERROR_SOURCE_UNAVAILABLE", invocation.Payload["status"]!.GetValue<string>());
        Assert.False(invocation.Payload["application_telemetry_available"]!.GetValue<bool>());
        Assert.True(invocation.Audit.Success);
    }

    [Fact]
    public async Task A_sql_error_window_that_ends_before_it_starts_is_refused()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetSqlErrors,
            Args(("start_time", "2026-01-05T11:00:00Z"), ("end_time", "2026-01-05T09:00:00Z")));

        Assert.Contains("end_time", invocation.Payload["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(harness.SqlErrors.Queries);
    }

    [Fact]
    public async Task An_unrecognized_error_category_is_refused_with_the_accepted_values()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetSqlErrors,
            Args(
                ("start_time", "2026-01-05T09:00:00Z"),
                ("end_time", "2026-01-05T11:00:00Z"),
                ("category", "SLOWNESS")));

        Assert.Contains("TIMEOUT", invocation.Payload["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(harness.SqlErrors.Queries);
    }

    [Fact]
    public async Task A_database_filter_on_the_error_tool_is_validated_against_the_allow_list()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetSqlErrors,
            Args(
                ("database", "master; DROP DATABASE AppDb"),
                ("start_time", "2026-01-05T09:00:00Z"),
                ("end_time", "2026-01-05T11:00:00Z")));

        Assert.Equal(StubDatabaseDiscoveryService.RejectionMessage, invocation.Payload["error"]!.GetValue<string>());
        Assert.Empty(harness.SqlErrors.Queries);
    }

    [Fact]
    public async Task An_expired_error_id_tells_the_model_to_re_list_rather_than_failing()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetSqlErrorDetails,
            Args(("error_id", "err_nolongerheld")));

        Assert.False(invocation.Payload["found"]!.GetValue<bool>());
        Assert.Equal(
            SqlErrorDetailResponse.NotFoundMessage,
            invocation.Payload["message"]!.GetValue<string>());

        Assert.True(invocation.Audit.Success);
    }

    [Fact]
    public async Task Error_details_are_returned_without_collecting_new_evidence()
    {
        var harness = new Harness();
        var errorEvent = new SqlErrorEvent
        {
            Id = "err_abc",
            Database = Database,
            ErrorCategory = SqlErrorCategory.Deadlock,
        };

        harness.SqlErrors.With(errorEvent);

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetSqlErrorDetails,
            Args(("error_id", "err_abc")));

        Assert.True(invocation.Payload["found"]!.GetValue<bool>());
        Assert.Empty(harness.SqlErrorCorrelation.Correlated);
    }

    /// <summary>
    /// The grading and its wording are computed before the model sees them, and the
    /// caveat travels with the payload so the model cannot present correlation as cause.
    /// </summary>
    [Fact]
    public async Task Investigating_an_error_hands_the_model_a_graded_non_causal_correlation()
    {
        var harness = new Harness();
        var errorEvent = new SqlErrorEvent { Id = "err_abc", Database = Database };
        harness.SqlErrors.With(errorEvent);
        harness.SqlErrorCorrelation.Correlation = new SqlErrorCorrelation
        {
            CorrelationConfidence = CorrelationConfidence.Low,
            Assessment = "Proximity in time alone is not evidence of a relationship.",
            Caveats = [SqlErrorCorrelation.CausationCaveat],
        };

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.InvestigateSqlError,
            Args(("error_id", "err_abc")));

        Assert.Equal("LOW", invocation.Payload["correlation_confidence"]!.GetValue<string>());
        Assert.Equal(
            SqlErrorCorrelation.CausationCaveat,
            invocation.Payload["causation_caveat"]!.GetValue<string>());

        Assert.Same(errorEvent, Assert.Single(harness.SqlErrorCorrelation.Correlated));
    }

    [Fact]
    public async Task Investigating_without_an_error_id_is_refused()
    {
        var harness = new Harness();

        var invocation = await harness.Build().InvokeAsync(AgentToolNames.InvestigateSqlError, "{}");

        Assert.Contains("error_id", invocation.Payload["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(harness.SqlErrorCorrelation.Correlated);
    }

    // --- Sanitization and auditing ------------------------------------------

    [Fact]
    public async Task Credentials_in_collected_text_never_reach_the_model()
    {
        var harness = new Harness();
        harness.Executor.Rows =
            [ActiveRequestRow(55, "EXEC dbo.Connect @cs = 'Server=sql01;UID=sa; PWD=letmein'")];

        var invocation = await harness.Build().InvokeAsync(AgentToolNames.GetActiveRequests, "{}");

        var payload = invocation.Payload.ToJsonString();
        Assert.DoesNotContain("letmein", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LlmSanitizer.Redacted, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arguments_are_recorded_both_raw_and_sanitized()
    {
        var invocation = await new Harness().Build().InvokeAsync(
            AgentToolNames.GetDatabaseHealth,
            Args(("database", Database), ("password", "letmein")));

        Assert.Equal(
            "letmein",
            Assert.IsAssignableFrom<JsonNode>(invocation.Audit.Arguments["password"]).GetValue<string>());

        Assert.Equal(
            LlmSanitizer.Redacted,
            Assert.IsAssignableFrom<JsonNode>(invocation.Audit.ArgumentsSanitized["password"]).GetValue<string>());
    }

    [Fact]
    public async Task The_audit_summary_records_counts_rather_than_payloads()
    {
        var harness = new Harness();
        harness.AnalysisEngine.Result = new AnalysisResult
        {
            OverallStatus = OverallStatus.Degraded,
            TotalFindings = 2,
            Findings =
            [
                new PerformanceFinding { Category = FindingCategory.Blocking },
                new PerformanceFinding { Category = FindingCategory.HighCpu },
            ],
        };

        var invocation = await harness.Build()
            .InvokeAsync(AgentToolNames.GetDatabaseHealth, Args(("database", Database)));

        var summary = invocation.Audit.ResultSummary;

        Assert.True(invocation.Audit.Success);
        Assert.Equal(2, Assert.IsAssignableFrom<JsonNode>(summary["total_findings"]).GetValue<int>());
        Assert.Equal(2, summary["findings_count"]);
        Assert.False(summary.ContainsKey("findings"));
    }

    [Fact]
    public async Task A_failing_service_is_reported_as_a_tool_error_rather_than_an_exception()
    {
        var harness = new Harness();
        harness.History.ThrowOnAccess = true;

        var invocation = await harness.Build().InvokeAsync(
            AgentToolNames.GetQueryHistory,
            Args(("query_hash", "0xABCD"), ("database", Database)));

        Assert.False(invocation.Audit.Success);
        Assert.Contains("Tool execution failed", invocation.Payload["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(invocation.Payload["error"]!.GetValue<string>(), invocation.Audit.Error);
    }

    // --- Tool catalogue ------------------------------------------------------

    [Fact]
    public void Every_tool_definition_declares_a_usable_schema()
    {
        foreach (var tool in AgentToolDefinitions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal("object", tool.Parameters["type"]!.GetValue<string>());
            Assert.NotNull(tool.Parameters["properties"]);
            Assert.NotNull(tool.Parameters["required"]);
        }
    }

    [Fact]
    public void Tool_schemas_can_be_rendered_repeatedly()
    {
        // Definitions are static and reused on every request, so rendering must not
        // consume them.
        foreach (var tool in AgentToolDefinitions.All)
        {
            Assert.Equal(tool.ToOpenAiSchema().ToJsonString(), tool.ToOpenAiSchema().ToJsonString());
        }
    }
}
