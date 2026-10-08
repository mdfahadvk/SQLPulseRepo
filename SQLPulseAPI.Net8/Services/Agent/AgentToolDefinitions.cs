using System.Text.Json.Nodes;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>Names of the tools the agent is permitted to call.</summary>
/// <remarks>
/// Declared as constants so the definition list, the executor table and the audit trail
/// cannot drift apart through a typo.
/// </remarks>
public static class AgentToolNames
{
    public const string GetServerHealth = "get_server_health";
    public const string GetDatabaseHealth = "get_database_health";
    public const string GetActiveRequests = "get_active_requests";
    public const string GetBlocking = "get_blocking";
    public const string GetLongRunningQueries = "get_long_running_queries";
    public const string GetTopCpuQueries = "get_top_cpu_queries";
    public const string GetTopIoQueries = "get_top_io_queries";
    public const string GetQueryHistory = "get_query_history";
    public const string GetQueryBaseline = "get_query_baseline";
    public const string GetQueryRegressions = "get_query_regressions";
    public const string GetSlowHistory = "get_slow_history";
    public const string GetTimeoutCandidates = "get_timeout_candidates";
    public const string GetExecutionPlan = "get_execution_plan";
    public const string GetSqlErrors = "get_sql_errors";
    public const string GetSqlErrorDetails = "get_sql_error_details";
    public const string InvestigateSqlError = "investigate_sql_error";
}

/// <summary>
/// The approved, read-only tool catalogue offered to the model.
/// Ported from <c>TOOL_DEFINITIONS</c> in <c>app/agent/tools.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// This list is the agent's entire capability surface: the model chooses which of these to
/// call and never writes SQL itself. Every tool is read-only. There is deliberately no tool
/// for running arbitrary SQL, for INSERT, UPDATE or DELETE, for KILL, for creating indexes,
/// for forcing plans, or for changing configuration — an AI-driven diagnosis must not be
/// able to change the server it is diagnosing.
/// </para>
/// <para>
/// The descriptions are the model's only instructions for when to use each tool, so their
/// wording is behaviour, not documentation, and is kept verbatim from the reference agent.
/// </para>
/// </remarks>
public static class AgentToolDefinitions
{
    /// <summary>Every approved tool, in the order the reference agent presents them.</summary>
    public static IReadOnlyList<LlmToolDefinition> All { get; } = Build();

    /// <summary>Tool definitions keyed by name.</summary>
    public static IReadOnlyDictionary<string, LlmToolDefinition> ByName { get; } =
        All.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    /// <summary>
    /// The allow-list checked before any tool runs.
    /// </summary>
    /// <remarks>
    /// A model can ask for a tool that does not exist, whether through a hallucination or a
    /// prompt-injected instruction in query text it just read. Membership of this set is the
    /// gate that keeps such a request from ever reaching an executor.
    /// </remarks>
    public static IReadOnlySet<string> ApprovedNames { get; } =
        All.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

    private static List<LlmToolDefinition> Build() =>
    [
        new()
        {
            Name = AgentToolNames.GetServerHealth,
            Description =
                "Collect a server-wide performance snapshot and run all detectors. "
                + "Returns overall health status, findings ordered by severity, "
                + "affected databases, and recommended drilldowns. "
                + "Use as the FIRST tool for server-wide questions or when no specific database is mentioned.",
            Parameters = Schema(new JsonObject()),
        },

        new()
        {
            Name = AgentToolNames.GetDatabaseHealth,
            Description =
                "Collect performance data scoped to a specific database and run detectors. "
                + "Returns health status and findings for that database only. "
                + "Use as the FIRST tool when a specific database is mentioned in a current-performance question.",
            Parameters = Schema(
                new JsonObject { ["database"] = StringParameter("Target database name") },
                "database"),
        },

        new()
        {
            Name = AgentToolNames.GetActiveRequests,
            Description =
                "List currently executing SQL requests on the server, "
                + "optionally filtered to a specific database. "
                + "Use AFTER get_database_health/get_server_health to see what is running right now.",
            Parameters = Schema(
                new JsonObject { ["database"] = StringParameter("Optional database filter") }),
        },

        new()
        {
            Name = AgentToolNames.GetBlocking,
            Description =
                "Detect blocking chains. Returns sessions that are blocking "
                + "other sessions, with head-blocker identification. "
                + "Use when current-performance evidence suggests contention or waits.",
            Parameters = Schema(
                new JsonObject { ["database"] = StringParameter("Optional database filter") }),
        },

        new()
        {
            Name = AgentToolNames.GetLongRunningQueries,
            Description =
                "Find queries running longer than the threshold (default 5 seconds). "
                + "Returns elapsed time, CPU, reads, and wait info.",
            Parameters = Schema(
                new JsonObject { ["database"] = StringParameter("Optional database filter") }),
        },

        new()
        {
            Name = AgentToolNames.GetTopCpuQueries,
            Description = "Return top queries ranked by CPU consumption from plan cache.",
            Parameters = Schema(
                new JsonObject { ["database"] = StringParameter("Optional database filter") }),
        },

        new()
        {
            Name = AgentToolNames.GetTopIoQueries,
            Description = "Return top queries ranked by logical reads (I/O consumption) from plan cache.",
            Parameters = Schema(
                new JsonObject { ["database"] = StringParameter("Optional database filter") }),
        },

        new()
        {
            Name = AgentToolNames.GetQueryHistory,
            Description =
                "Retrieve historical performance measurements for a specific query "
                + "identified by query_hash. Shows how the query performed over time.",
            Parameters = Schema(
                new JsonObject
                {
                    ["query_hash"] = StringParameter("Query hash identifier"),
                    ["database"] = StringParameter("Database name"),
                },
                "query_hash",
                "database"),
        },

        new()
        {
            Name = AgentToolNames.GetQueryBaseline,
            Description =
                "Compute the performance baseline for a query over a time window. "
                + "Returns historical mean, median, P95, and standard deviation.",
            Parameters = Schema(
                new JsonObject
                {
                    ["query_hash"] = StringParameter("Query hash identifier"),
                    ["database"] = StringParameter("Database name"),
                },
                "query_hash",
                "database"),
        },

        new()
        {
            Name = AgentToolNames.GetQueryRegressions,
            Description =
                "Detect queries that have regressed compared to their baseline. "
                + "Runs the full analysis pipeline and filters for regression findings.",
            Parameters = Schema(
                new JsonObject { ["database"] = StringParameter("Database name") },
                "database"),
        },

        new()
        {
            Name = AgentToolNames.GetSlowHistory,
            Description =
                "Retrieve slow query history from Query Store for a time range. "
                + "Returns queries whose duration exceeded the slow-query monitoring "
                + "threshold, with interval aggregation and workload classification. "
                + "Use for HISTORICAL questions about past performance, NOT current issues.",
            Parameters = Schema(
                new JsonObject
                {
                    ["database"] = StringParameter("Target database name"),
                    ["start_time"] = StringParameter("Start of time range (ISO 8601)"),
                    ["end_time"] = StringParameter("End of time range (ISO 8601)"),
                    ["threshold_ms"] = IntegerParameter(
                        "Slow-query monitoring threshold in milliseconds "
                        + "(default 30000). This selects which queries are "
                        + "returned. It is NOT the application timeout."),
                    ["application_timeout_ms"] = IntegerParameter(ApplicationTimeoutDescription),
                    ["top_n"] = IntegerParameter("Maximum number of candidates to return (default 20)"),
                    ["workload_type"] = StringParameter(
                        "Filter by workload type: APPLICATION_OR_INTERACTIVE, "
                        + "BACKGROUND_OR_SCHEDULED, UNKNOWN, or omit for all"),
                },
                "database",
                "start_time",
                "end_time"),
        },

        new()
        {
            Name = AgentToolNames.GetTimeoutCandidates,
            Description =
                "Identify slow-query and timeout candidates from Query Store history. "
                + "Every result exceeded the slow-query monitoring threshold and is a "
                + "'slow_query_candidate'. A result is only a 'timeout_candidate' when "
                + "it also reached an explicitly configured application timeout \u2014 never "
                + "a confirmed timeout. Read each candidate's 'classification' field "
                + "rather than assuming. "
                + "Includes confidence, severity, baseline comparison, workload "
                + "classification with evidence, interval aggregation, and ranking. "
                + "Use for timeout/slow-query investigations with a specific time range.",
            Parameters = Schema(
                new JsonObject
                {
                    ["database"] = StringParameter("Target database name"),
                    ["start_time"] = StringParameter("Start of time range (ISO 8601)"),
                    ["end_time"] = StringParameter("End of time range (ISO 8601)"),
                    ["threshold_ms"] = IntegerParameter(
                        "Slow-query monitoring threshold in milliseconds "
                        + "(default 30000). Selects which queries are returned. "
                        + "It is NOT the application timeout."),
                    ["application_timeout_ms"] = IntegerParameter(ApplicationTimeoutDescription),
                    ["workload_type"] = StringParameter(
                        "Filter by workload type: APPLICATION_OR_INTERACTIVE, "
                        + "BACKGROUND_OR_SCHEDULED, or omit for all"),
                },
                "database",
                "start_time",
                "end_time"),
        },

        new()
        {
            Name = AgentToolNames.GetExecutionPlan,
            Description =
                "Retrieve a read-only Query Store execution plan for a query hash and plan id. "
                + "Returns a compact plan summary, important operators, warnings, limitations, and evidence-based findings. "
                + "It never executes the query or recommends an automatic database change.",
            Parameters = Schema(
                new JsonObject
                {
                    ["database"] = StringParameter("Target database name"),
                    ["query_hash"] = StringParameter("Optional query hash, for example 0xABC123"),
                    ["query_id"] = IntegerParameter("Optional Query Store query id"),
                    ["plan_id"] = IntegerParameter("Query Store plan id"),
                },
                "database",
                "query_hash"),
        },

        new()
        {
            Name = AgentToolNames.GetSqlErrors,
            Description =
                "List SQL errors \u2014 timeouts, deadlocks, connection failures, login and "
                + "permission failures, constraint and conversion errors \u2014 for a time range. "
                + "Use for questions about FAILURES ('why did X get SQL timeouts', 'what "
                + "errors did Y see'), not about slowness. "
                + "ALWAYS read the response 'status' first: NO_ERRORS_FOUND means every source "
                + "was consulted and reported nothing, while ERROR_SOURCE_UNAVAILABLE means "
                + "SQLPulse could not look at all and proves nothing. Also read "
                + "'application_telemetry_available': when it is false, SQLPulse cannot tell "
                + "whether the application saw SQL errors. "
                + "Each event carries 'timeout_classification' \u2014 report it, do not derive it.",
            Parameters = Schema(
                new JsonObject
                {
                    ["database"] = StringParameter("Optional database filter"),
                    ["start_time"] = StringParameter("Start of time range (ISO 8601)"),
                    ["end_time"] = StringParameter("End of time range (ISO 8601)"),
                    ["category"] = StringParameter(
                        "Filter by normalized category: TIMEOUT, DEADLOCK, CONNECTION, "
                        + "LOGIN_FAILURE, PERMISSION_DENIED, CONSTRAINT_VIOLATION, "
                        + "CONVERSION_ERROR, ARITHMETIC_ERROR, INVALID_OBJECT, INVALID_COLUMN, "
                        + "TRANSACTION_ERROR, SQL_EXECUTION_ERROR, UNKNOWN, or omit for all"),
                    ["application"] = StringParameter("Optional application name filter"),
                    ["stored_procedure"] = StringParameter("Optional stored procedure filter"),
                    ["severity"] = StringParameter(
                        "Filter by severity: critical, high, medium, low, or omit for all"),
                    ["confirmed_only"] = BooleanParameter(
                        "When true, return only failures a source actually recorded, "
                        + "excluding candidates SQLPulse derived from performance evidence"),
                    ["top_n"] = IntegerParameter("Maximum number of errors to return (default 20)"),
                },
                "start_time",
                "end_time"),
        },

        new()
        {
            Name = AgentToolNames.GetSqlErrorDetails,
            Description =
                "Return the full detail of one SQL error using an 'id' from a previous "
                + "get_sql_errors result. Does not collect any correlated performance "
                + "evidence \u2014 use investigate_sql_error for that.",
            Parameters = Schema(
                new JsonObject
                {
                    ["error_id"] = StringParameter("The 'id' field of an error from get_sql_errors"),
                },
                "error_id"),
        },

        new()
        {
            Name = AgentToolNames.InvestigateSqlError,
            Description =
                "Return one SQL error together with the SQLPulse performance evidence "
                + "observed within the configured window either side of it: Query Store "
                + "history, baseline comparison, and blocking or long-running queries when "
                + "the incident is still live. "
                + "The result reports CORRELATION ONLY. Read 'correlation_confidence' and "
                + "repeat the supplied 'assessment' wording; never upgrade correlated "
                + "evidence into a cause, and never claim causation because two things "
                + "happened at the same time.",
            Parameters = Schema(
                new JsonObject
                {
                    ["error_id"] = StringParameter("The 'id' field of an error from get_sql_errors"),
                },
                "error_id"),
        },
    ];

    /// <summary>
    /// Wording that stops the model conflating the monitoring threshold with the
    /// application's timeout.
    /// </summary>
    /// <remarks>
    /// The distinction decides whether a query is reported as merely slow or as having hit
    /// a timeout, so the model is told plainly not to invent or copy the value.
    /// </remarks>
    private const string ApplicationTimeoutDescription =
        "The application's configured command timeout in "
        + "milliseconds. Supply ONLY if the user stated it. Do "
        + "not guess and do not copy threshold_ms into this.";

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var requiredNames = new JsonArray();
        foreach (var name in required)
        {
            requiredNames.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = requiredNames,
        };
    }

    private static JsonObject StringParameter(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject IntegerParameter(string description) =>
        new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject BooleanParameter(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };
}
