using Microsoft.AspNetCore.Mvc;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Services.Analysis;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Services.QueryStore;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Controllers;

/// <summary>
/// Raw performance measurements and deterministic analysis.
/// Mirrors <c>GET /api/performance/snapshot</c>, <c>GET /api/performance/analyze</c>
/// and <c>GET /api/performance/slow-history</c> in the Python implementation.
/// </summary>
[ApiController]
public sealed class PerformanceController(
    IPerformanceSnapshotService snapshotService,
    IAnalysisEngine analysisEngine,
    ISlowHistoryService slowHistoryService,
    IExecutionPlanService executionPlanService,
    IDatabaseDiscoveryService discovery) : ControllerBase
{
    /// <summary>Returned when scope=database but no database was supplied.</summary>
    public const string DatabaseRequiredMessage = "database parameter is required when scope=database";

    /// <summary>Returned when the scope value is neither <c>server</c> nor <c>database</c>.</summary>
    public const string InvalidScopeMessage = "scope must be either 'server' or 'database'";

    /// <summary>Returned when the slow-history window ends at or before it starts.</summary>
    public const string EndTimeMessage = "end_time must be after start_time";

    /// <summary>Upper bound for <c>top_n</c> on the slow-history endpoint. Python: <c>le=100</c>.</summary>
    public const int MaxSlowHistoryTopN = 100;

    private const string ServerScope = "server";
    private const string DatabaseScope = "database";

    /// <summary>Returns a read-only Query Store execution plan and deterministic findings.</summary>
    [HttpGet("/api/performance/execution-plan")]
    [ProducesResponseType(typeof(ExecutionPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ExecutionPlanResponse>> ExecutionPlan(
        [FromQuery] string? database,
        [FromQuery(Name = "query_hash")] string? queryHash,
        [FromQuery(Name = "query_id")] long? queryId,
        [FromQuery(Name = "plan_id")] long? planId,
        [FromQuery(Name = "include_xml")] bool includeXml = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return Unprocessable("database is required");
        }

        if (string.IsNullOrWhiteSpace(queryHash) && queryId is null && planId is null)
        {
            return Unprocessable("query_hash, query_id, or plan_id is required");
        }

        if (queryId is < 1 || planId is < 1)
        {
            return Unprocessable("query_id and plan_id must be 1 or greater");
        }

        return Ok(await executionPlanService.GetAsync(
            database,
            queryHash,
            queryId,
            planId,
            includeXml,
            cancellationToken));
    }

    /// <summary>
    /// Collects a point-in-time snapshot of SQL Server activity.
    /// </summary>
    /// <remarks>
    /// <para><c>scope=server</c> — instance-wide: active requests, blocking, waits, top queries.</para>
    /// <para><c>scope=database</c> — queries scoped to a single database; wait statistics are
    /// reported as not applicable, because <c>sys.dm_os_wait_stats</c> is instance-wide.</para>
    /// <para>
    /// Collectors fail independently: a section the monitoring login cannot read is returned
    /// with <c>metadata.success = false</c> while the rest of the snapshot is still produced.
    /// </para>
    /// </remarks>
    /// <param name="scope">Analysis scope: <c>server</c> or <c>database</c>.</param>
    /// <param name="database">Target database name. Required when <paramref name="scope"/> is <c>database</c>.</param>
    /// <param name="longRunningThresholdMs">Elapsed-time threshold, in milliseconds, for long-running queries.</param>
    /// <param name="topN">Row limit for the top CPU and top I/O collectors.</param>
    /// <param name="waitStatsTopN">Row limit for wait statistics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/performance/snapshot")]
    [ProducesResponseType(typeof(PerformanceSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PerformanceSnapshot>> Snapshot(
        [FromQuery(Name = "scope")] string scope = ServerScope,
        [FromQuery(Name = "database")] string? database = null,
        [FromQuery(Name = "long_running_threshold_ms")] int longRunningThresholdMs = 5000,
        [FromQuery(Name = "top_n")] int topN = 20,
        [FromQuery(Name = "wait_stats_top_n")] int waitStatsTopN = 25,
        CancellationToken cancellationToken = default)
    {
        var (failure, resolvedScope, resolvedDatabase) = await ValidateRequestAsync(
            scope, database, longRunningThresholdMs, topN, waitStatsTopN, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        var snapshot = await snapshotService.CollectAsync(
            new PerformanceSnapshotRequest(
                Scope: resolvedScope,
                Database: resolvedDatabase,
                LongRunningThresholdMs: longRunningThresholdMs,
                TopN: topN,
                WaitStatsTopN: waitStatsTopN),
            cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>
    /// Collects a snapshot and runs the deterministic detectors over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response separates two different questions. <c>current_health</c> answers "is
    /// something wrong right now?" and is driven only by live activity — long-running
    /// queries and blocking. <c>optimization_opportunities</c> answers "what is worth
    /// tuning?" and comes from lifetime plan-cache statistics.
    /// </para>
    /// <para>
    /// An expensive cached query therefore never degrades <c>overall_status</c> on its own:
    /// those statistics accumulate since the plan was cached and say nothing about whether
    /// the server is struggling at this moment.
    /// </para>
    /// <para>
    /// No action is ever taken against the database. Every finding carries evidence,
    /// a likely cause and validation steps so a DBA can confirm it independently.
    /// </para>
    /// </remarks>
    /// <param name="scope">Analysis scope: <c>server</c> or <c>database</c>.</param>
    /// <param name="database">Target database name. Required when <paramref name="scope"/> is <c>database</c>.</param>
    /// <param name="longRunningThresholdMs">Elapsed-time threshold, in milliseconds, for long-running queries.</param>
    /// <param name="topN">Row limit for the top CPU and top I/O collectors.</param>
    /// <param name="waitStatsTopN">Row limit for wait statistics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/performance/analyze")]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AnalysisResult>> Analyze(
        [FromQuery(Name = "scope")] string scope = ServerScope,
        [FromQuery(Name = "database")] string? database = null,
        [FromQuery(Name = "long_running_threshold_ms")] int longRunningThresholdMs = 5000,
        [FromQuery(Name = "top_n")] int topN = 20,
        [FromQuery(Name = "wait_stats_top_n")] int waitStatsTopN = 25,
        CancellationToken cancellationToken = default)
    {
        var (failure, resolvedScope, resolvedDatabase) = await ValidateRequestAsync(
            scope, database, longRunningThresholdMs, topN, waitStatsTopN, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        var result = await analysisEngine.AnalyzeAsync(
            new AnalysisRequest(
                Scope: resolvedScope,
                Database: resolvedDatabase,
                LongRunningThresholdMs: longRunningThresholdMs,
                TopN: topN,
                WaitStatsTopN: waitStatsTopN),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Identifies queries that exceeded the slow-query monitoring threshold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads SQL Server Query Store, strictly read-only. SQLPulse never enables, disables
    /// or reconfigures Query Store.
    /// </para>
    /// <para>
    /// Every returned query is a <c>SLOW_QUERY_CANDIDATE</c>. It is additionally a
    /// <c>TIMEOUT_CANDIDATE</c> only when its duration reached an explicitly configured
    /// <c>application_timeout_ms</c> — the monitoring threshold is chosen by whoever is
    /// monitoring and says nothing about when the client gives up, so it is never reused
    /// for that decision. Even a <c>TIMEOUT_CANDIDATE</c> is not a confirmed timeout:
    /// that needs application telemetry SQLPulse does not have.
    /// </para>
    /// <para>
    /// When <c>group_results</c> is true, duplicate Query Store intervals for the same
    /// query/plan are merged into one candidate with execution-weighted averages,
    /// preserved max/min values and interval counts.
    /// </para>
    /// <para>
    /// Each candidate is ranked (1 = most urgent) on a composite score of duration vs
    /// threshold, interval exceedance, baseline regression, CPU impact and workload type.
    /// </para>
    /// <para>
    /// When Query Store is disabled or inaccessible the response still returns 200 with
    /// <c>query_store_status</c> and a message — one unavailable feature must not fail
    /// the request.
    /// </para>
    /// </remarks>
    /// <param name="database">Target database name. Query Store must be enabled on it.</param>
    /// <param name="startTime">Start of the time range (ISO 8601).</param>
    /// <param name="endTime">End of the time range (ISO 8601).</param>
    /// <param name="thresholdMs">
    /// Slow-query monitoring threshold in milliseconds. Selects which queries are
    /// reported. This is NOT the application timeout.
    /// </param>
    /// <param name="applicationTimeoutMs">
    /// The application's configured command timeout in milliseconds. Only when a query
    /// reaches this value is it a <c>TIMEOUT_CANDIDATE</c>. Omit to use the configured
    /// default; when neither is set, timeout classification is reported as unavailable
    /// rather than guessed.
    /// </param>
    /// <param name="topN">Maximum number of candidates to return.</param>
    /// <param name="groupResults">Aggregate duplicate Query Store intervals per query/plan.</param>
    /// <param name="workloadType">
    /// Filter candidates by workload type: <c>APPLICATION_OR_INTERACTIVE</c>,
    /// <c>BACKGROUND_OR_SCHEDULED</c> or <c>UNKNOWN</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/performance/slow-history")]
    [ProducesResponseType(typeof(SlowHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SlowHistoryResponse>> SlowHistory(
        [FromQuery(Name = "database")] string? database,
        [FromQuery(Name = "start_time")] DateTimeOffset startTime,
        [FromQuery(Name = "end_time")] DateTimeOffset endTime,
        [FromQuery(Name = "threshold_ms")] int thresholdMs = 30_000,
        [FromQuery(Name = "application_timeout_ms")] int? applicationTimeoutMs = null,
        [FromQuery(Name = "top_n")] int topN = 20,
        [FromQuery(Name = "group_results")] bool groupResults = true,
        [FromQuery(Name = "workload_type")] string? workloadType = null,
        CancellationToken cancellationToken = default)
    {
        // Discovery is the allow-list: an unknown, malformed or inaccessible name is
        // rejected before it can be used to steer a query.
        var databaseError = await discovery.ValidateTargetDatabaseAsync(database, cancellationToken);
        if (databaseError is not null)
        {
            return BadRequest(new ErrorResponse { Detail = databaseError });
        }

        if (endTime <= startTime)
        {
            return BadRequest(new ErrorResponse { Detail = EndTimeMessage });
        }

        if (thresholdMs < 1)
        {
            return Unprocessable("threshold_ms must be 1 or greater");
        }

        if (applicationTimeoutMs is < 1)
        {
            return Unprocessable("application_timeout_ms must be 1 or greater");
        }

        if (topN < 1 || topN > MaxSlowHistoryTopN)
        {
            return Unprocessable($"top_n must be between 1 and {MaxSlowHistoryTopN}");
        }

        WorkloadType? workloadFilter = null;
        if (!string.IsNullOrWhiteSpace(workloadType))
        {
            if (!EnumMemberJsonConverter<WorkloadType>.TryFromWireValue(workloadType, out var parsed))
            {
                var accepted = string.Join(", ", EnumMemberJsonConverter<WorkloadType>.WireValues);
                return Unprocessable($"workload_type must be one of: {accepted}");
            }

            workloadFilter = parsed;
        }

        var response = await slowHistoryService.GetSlowHistoryAsync(
            database!,
            startTime,
            endTime,
            thresholdMs,
            topN,
            groupResults,
            workloadFilter,
            applicationTimeoutMs,
            cancellationToken);

        return Ok(response);
    }

    /// <summary>
    /// Validates the query parameters shared by both endpoints.
    /// </summary>
    /// <returns>
    /// A failure result, or the resolved scope and database when the request is valid.
    /// </returns>
    private async Task<(ActionResult? Failure, AnalysisScope Scope, string? Database)> ValidateRequestAsync(
        string scope,
        string? database,
        int longRunningThresholdMs,
        int topN,
        int waitStatsTopN,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(scope, ServerScope, StringComparison.Ordinal)
            && !string.Equals(scope, DatabaseScope, StringComparison.Ordinal))
        {
            return (Unprocessable(InvalidScopeMessage), AnalysisScope.Server, null);
        }

        // Bounds are enforced here rather than in the SQL builder so a bad value produces a
        // clear 422 instead of an exception surfacing as a failed collector.
        if (longRunningThresholdMs < 0)
        {
            return (Unprocessable("long_running_threshold_ms must be zero or greater"), AnalysisScope.Server, null);
        }

        if (topN < 1 || topN > PerformanceQueries.MaxTopN)
        {
            return (Unprocessable($"top_n must be between 1 and {PerformanceQueries.MaxTopN}"), AnalysisScope.Server, null);
        }

        if (waitStatsTopN < 1 || waitStatsTopN > PerformanceQueries.MaxTopN)
        {
            return (Unprocessable($"wait_stats_top_n must be between 1 and {PerformanceQueries.MaxTopN}"), AnalysisScope.Server, null);
        }

        var databaseScoped = string.Equals(scope, DatabaseScope, StringComparison.Ordinal);
        if (!databaseScoped)
        {
            return (null, AnalysisScope.Server, null);
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            return (BadRequest(new ErrorResponse { Detail = DatabaseRequiredMessage }), AnalysisScope.Database, null);
        }

        // Discovery is the allow-list: an unknown or inaccessible name is rejected before
        // it can be used to steer a query.
        var error = await discovery.ValidateTargetDatabaseAsync(database, cancellationToken);
        if (error is not null)
        {
            return (BadRequest(new ErrorResponse { Detail = error }), AnalysisScope.Database, null);
        }

        return (null, AnalysisScope.Database, database);
    }

    private ObjectResult Unprocessable(string detail) => StatusCode(
        StatusCodes.Status422UnprocessableEntity,
        new ErrorResponse { Detail = detail });
}
