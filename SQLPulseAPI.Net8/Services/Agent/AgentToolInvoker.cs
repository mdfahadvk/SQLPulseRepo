using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Collectors;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Repositories;
using SQLPulseAPI.Net8.Services.Analysis;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.History;
using SQLPulseAPI.Net8.Services.QueryStore;
using SQLPulseAPI.Net8.Services.SqlErrors;
using SQLPulseAPI.Net8.Utilities;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>The outcome of one tool call: what the model sees, and what the audit records.</summary>
/// <param name="Payload">Sanitized tool output, forwarded to the model.</param>
/// <param name="Audit">Audit entry for the investigation record.</param>
public sealed record AgentToolInvocation(JsonObject Payload, ToolCall Audit);

/// <summary>Runs an approved tool on the model's behalf.</summary>
public interface IAgentToolInvoker
{
    /// <summary>
    /// Executes a tool the model asked for and returns its sanitized output.
    /// </summary>
    /// <param name="toolName">Tool name supplied by the model.</param>
    /// <param name="argumentsJson">Raw JSON arguments supplied by the model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Never throws for a bad request from the model. An unapproved tool, malformed
    /// arguments or a failing collector all come back as an <c>error</c> payload, because
    /// the model can read that and correct itself, whereas an exception would abandon an
    /// investigation over one recoverable mistake.
    /// </remarks>
    Task<AgentToolInvocation> InvokeAsync(
        string toolName,
        string? argumentsJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes the approved read-only tools.
/// Ported from <c>execute_tool</c> and the executors in <c>app/agent/tools.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every tool delegates to the same collector, detector or service that backs the
/// corresponding REST endpoint. No SQL is written here, so the AI path and the API path
/// can never disagree about what the server is doing, and a fix to a query benefits both.
/// </para>
/// <para>
/// Results pass through <see cref="LlmSanitizer"/> before they are returned, so credentials
/// cannot reach the provider even if one ever appeared in collected text.
/// </para>
/// </remarks>
public sealed class AgentToolInvoker(
    IAnalysisEngine analysisEngine,
    IDatabaseDiscoveryService discovery,
    ActiveRequestsCollector activeRequests,
    BlockingSessionsCollector blockingSessions,
    LongRunningQueriesCollector longRunningQueries,
    TopCpuQueriesCollector topCpuQueries,
    TopIoQueriesCollector topIoQueries,
    IHistoryRepository historyRepository,
    IBaselineService baselineService,
    ISlowHistoryService slowHistoryService,
    IQueryStoreCollector queryStoreCollector,
    ISlowQueryCandidateBuilder candidateBuilder,
    IExecutionPlanService executionPlanService,
    ISqlErrorService sqlErrorService,
    ISqlErrorCorrelationService sqlErrorCorrelationService,
    IOptions<SqlServerOptions> sqlServerOptions,
    IOptions<AnalysisOptions> analysisOptions,
    ILogger<AgentToolInvoker> logger) : IAgentToolInvoker
{
    /// <summary>Audit text recorded when the model asks for a tool that is not approved.</summary>
    public const string NotApprovedMessage = "Not an approved tool";

    private const int DefaultThresholdMs = 30_000;
    private const int DefaultTopN = 20;
    private const int HistorySampleLimit = 100;
    private const string BaselineWindow = "7d";

    // Result caps, matching the reference agent. Evidence is bounded per tool so one
    // pathological result set cannot consume the whole conversation budget.
    private const int MaxSessions = 50;
    private const int MaxPlanCacheQueries = 20;
    private const int MaxAnalysisFindings = 20;
    private const int MaxRegressions = 20;
    private const int MaxCandidates = 20;

    /// <summary>Cap on SQL error events handed to the model in one tool result.</summary>
    private const int MaxSqlErrors = 20;

    private readonly AnalysisOptions _analysisOptions = analysisOptions.Value;
    private readonly SqlServerOptions _sqlServerOptions = sqlServerOptions.Value;

    /// <inheritdoc />
    public async Task<AgentToolInvocation> InvokeAsync(
        string toolName,
        string? argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var arguments = ParseArguments(argumentsJson);
        var sanitizedArguments = LlmSanitizer.Sanitize(arguments) as JsonObject ?? [];

        // The allow-list is checked before anything else. A model can request a tool that
        // does not exist, and query text it has just read could try to talk it into one.
        if (!AgentToolDefinitions.ApprovedNames.Contains(toolName))
        {
            logger.LogWarning("Rejected unapproved tool request: {ToolName}", toolName);

            return new AgentToolInvocation(
                Error($"Tool '{toolName}' is not approved"),
                new ToolCall
                {
                    ToolName = toolName,
                    Arguments = ToDictionary(arguments),
                    ArgumentsSanitized = ToDictionary(sanitizedArguments),
                    ResultSummary = new Dictionary<string, object?> { ["error"] = NotApprovedMessage },
                    Success = false,
                    Error = NotApprovedMessage,
                    DurationSeconds = 0.0,
                });
        }

        var stopwatch = Stopwatch.StartNew();
        JsonObject payload;

        try
        {
            payload = LlmSanitizer.SanitizeForLlm(
                await ExecuteAsync(toolName, arguments, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Tool {ToolName} failed", toolName);

            // Sanitized because an exception message can carry a connection string.
            payload = Error($"Tool execution failed: {LlmSanitizer.SanitizeText(exception.Message)}");
        }

        stopwatch.Stop();

        var success = !payload.ContainsKey("error");

        return new AgentToolInvocation(
            payload,
            new ToolCall
            {
                ToolName = toolName,
                Arguments = ToDictionary(arguments),
                ArgumentsSanitized = ToDictionary(sanitizedArguments),
                ResultSummary = BuildResultSummary(payload),
                Success = success,
                Error = ReadString(payload, "error"),
                DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            });
    }

    private Task<JsonObject> ExecuteAsync(
        string toolName,
        JsonObject arguments,
        CancellationToken cancellationToken) => toolName switch
    {
        AgentToolNames.GetServerHealth => ServerHealthAsync(cancellationToken),
        AgentToolNames.GetDatabaseHealth => DatabaseHealthAsync(arguments, cancellationToken),
        AgentToolNames.GetActiveRequests => ActiveRequestsAsync(arguments, cancellationToken),
        AgentToolNames.GetBlocking => BlockingAsync(arguments, cancellationToken),
        AgentToolNames.GetLongRunningQueries => LongRunningAsync(arguments, cancellationToken),
        AgentToolNames.GetTopCpuQueries => TopCpuAsync(arguments, cancellationToken),
        AgentToolNames.GetTopIoQueries => TopIoAsync(arguments, cancellationToken),
        AgentToolNames.GetQueryHistory => QueryHistoryAsync(arguments, cancellationToken),
        AgentToolNames.GetQueryBaseline => QueryBaselineAsync(arguments, cancellationToken),
        AgentToolNames.GetQueryRegressions => QueryRegressionsAsync(arguments, cancellationToken),
        AgentToolNames.GetSlowHistory => SlowHistoryAsync(arguments, cancellationToken),
        AgentToolNames.GetTimeoutCandidates => TimeoutCandidatesAsync(arguments, cancellationToken),
        AgentToolNames.GetExecutionPlan => ExecutionPlanAsync(arguments, cancellationToken),
        AgentToolNames.GetSqlErrors => SqlErrorsAsync(arguments, cancellationToken),
        AgentToolNames.GetSqlErrorDetails => SqlErrorDetailsAsync(arguments),
        AgentToolNames.InvestigateSqlError => InvestigateSqlErrorAsync(arguments, cancellationToken),

        // Approved but unimplemented would be a wiring bug, not a model mistake.
        _ => Task.FromResult(Error($"Tool '{toolName}' has no executor registered")),
    };

    // ─── Executors ──────────────────────────────────────────────────────────

    private async Task<JsonObject> ServerHealthAsync(CancellationToken cancellationToken)
    {
        var result = await analysisEngine.AnalyzeAsync(
            new AnalysisRequest(AnalysisScope.Server),
            cancellationToken);

        return AnalysisToPayload(result);
    }

    private async Task<JsonObject> DatabaseHealthAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (database, failure) = await ResolveRequiredDatabaseAsync(arguments, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var result = await analysisEngine.AnalyzeAsync(
            new AnalysisRequest(AnalysisScope.Database, database),
            cancellationToken);

        return AnalysisToPayload(result);
    }

    private async Task<JsonObject> ActiveRequestsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (database, failure) = await ResolveOptionalDatabaseAsync(arguments, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var result = await activeRequests.CollectAsync(database, cancellationToken);

        return new JsonObject
        {
            ["request_count"] = result.Requests.Count,
            ["requests"] = ToJsonArray(result.Requests, MaxSessions),
        };
    }

    private async Task<JsonObject> BlockingAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (database, failure) = await ResolveOptionalDatabaseAsync(arguments, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var result = await blockingSessions.CollectAsync(database, cancellationToken);

        return new JsonObject
        {
            ["blocking_chain_count"] = result.BlockingChainCount,
            ["sessions"] = ToJsonArray(result.Sessions, MaxSessions),
        };
    }

    private async Task<JsonObject> LongRunningAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (database, failure) = await ResolveOptionalDatabaseAsync(arguments, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var result = await longRunningQueries.CollectAsync(
            LongRunningQueriesCollector.DefaultThresholdMs,
            database,
            cancellationToken);

        return new JsonObject
        {
            ["query_count"] = result.Queries.Count,
            ["threshold_ms"] = result.ThresholdMs,
            ["queries"] = ToJsonArray(result.Queries, MaxSessions),
        };
    }

    private async Task<JsonObject> TopCpuAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (database, failure) = await ResolveOptionalDatabaseAsync(arguments, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var result = await topCpuQueries.CollectAsync(DefaultTopN, database, cancellationToken);

        return new JsonObject
        {
            ["query_count"] = result.Queries.Count,
            ["queries"] = ToJsonArray(result.Queries, MaxPlanCacheQueries),
        };
    }

    private async Task<JsonObject> TopIoAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (database, failure) = await ResolveOptionalDatabaseAsync(arguments, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var result = await topIoQueries.CollectAsync(DefaultTopN, database, cancellationToken);

        return new JsonObject
        {
            ["query_count"] = result.Queries.Count,
            ["queries"] = ToJsonArray(result.Queries, MaxPlanCacheQueries),
        };
    }

    private async Task<JsonObject> QueryHistoryAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var queryHash = ReadString(arguments, "query_hash");
        var database = ReadString(arguments, "database");

        if (string.IsNullOrWhiteSpace(queryHash) || string.IsNullOrWhiteSpace(database))
        {
            return Error("query_hash and database are required");
        }

        var records = await historyRepository.GetQueryHistoryAsync(
            queryHash, database, HistorySampleLimit, cancellationToken);

        return new JsonObject
        {
            ["query_hash"] = queryHash,
            ["database"] = database,
            ["sample_count"] = records.Count,
            ["samples"] = ToJsonArray(records, HistorySampleLimit),
        };
    }

    private async Task<JsonObject> QueryBaselineAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var queryHash = ReadString(arguments, "query_hash");
        var database = ReadString(arguments, "database");

        if (string.IsNullOrWhiteSpace(queryHash) || string.IsNullOrWhiteSpace(database))
        {
            return Error("query_hash and database are required");
        }

        var baseline = await baselineService.CalculateAsync(
            queryHash, database, BaselineWindow, null, cancellationToken);

        return ToJsonObject(baseline);
    }

    private async Task<JsonObject> QueryRegressionsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (database, failure) = await ResolveRequiredDatabaseAsync(arguments, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var result = await analysisEngine.AnalyzeAsync(
            new AnalysisRequest(AnalysisScope.Database, database),
            cancellationToken);

        var regressions = result.Findings
            .Where(finding => finding.Category == FindingCategory.QueryRegression)
            .ToList();

        return new JsonObject
        {
            ["database"] = database,
            ["regression_count"] = regressions.Count,
            ["regressions"] = ToJsonArray(regressions, MaxRegressions),
        };
    }

    private async Task<JsonObject> SlowHistoryAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (window, windowFailure) = await ResolveWindowAsync(arguments, cancellationToken);
        if (windowFailure is not null)
        {
            return windowFailure;
        }

        if (!TryReadPositiveInt(arguments, "threshold_ms", DefaultThresholdMs, out var thresholdMs))
        {
            return Error("threshold_ms must be a positive integer");
        }

        if (!TryReadPositiveInt(arguments, "top_n", DefaultTopN, out var topN))
        {
            return Error("top_n must be a positive integer");
        }

        var response = await slowHistoryService.GetSlowHistoryAsync(
            window.Database,
            window.Start,
            window.End,
            thresholdMs,
            topN,
            groupResults: true,
            ReadWorkloadType(arguments),
            ReadOptionalPositiveInt(arguments, "application_timeout_ms"),
            cancellationToken);

        var data = ToJsonObject(response);
        var queryStoreStatus = data["query_store_status"]?.GetValue<string>();
        var queryStoreAvailable = queryStoreStatus is "AVAILABLE" or "AVAILABLE_READ_ONLY";
        var timeoutClassificationAvailable =
            data["timeout_classification_available"]?.GetValue<bool>() == true;

        // Projected rather than forwarded whole: the model only needs the fields it is
        // expected to reason about, and every extra field costs tokens.
        return new JsonObject
        {
            ["database"] = Copy(data, "database"),
            ["start_time"] = Copy(data, "start_time"),
            ["end_time"] = Copy(data, "end_time"),
            ["slow_threshold_ms"] = Copy(data, "slow_threshold_ms"),
            ["application_timeout_ms"] = Copy(data, "application_timeout_ms"),
            ["timeout_classification_available"] = Copy(data, "timeout_classification_available"),
            ["slow_query_analysis_status"] = queryStoreAvailable
                ? "evaluated"
                : "data_unavailable",
            ["timeout_classification_status"] = !queryStoreAvailable
                ? "data_unavailable"
                : timeoutClassificationAvailable
                    ? "evaluated"
                    : "not_evaluated",
            ["timeout_classification_message"] = !queryStoreAvailable
                ? "Query Store data was unavailable, so timeout classification could not be evaluated."
                : timeoutClassificationAvailable
                ? "Application timeout classification: Evaluated against the configured ApplicationTimeoutMs."
                : "Application timeout classification: Not evaluated — ApplicationTimeoutMs is not configured.",
            ["threshold_ms"] = Copy(data, "threshold_ms"),
            ["top_n"] = Copy(data, "top_n"),
            ["query_store_status"] = Copy(data, "query_store_status"),
            ["candidate_count"] = Copy(data, "total_candidates"),
            ["slow_query_candidate_count"] = Copy(data, "slow_query_candidate_count"),
            ["timeout_candidate_count"] = Copy(data, "timeout_candidate_count"),
            ["candidates"] = Copy(data, "candidates"),
            ["message"] = Copy(data, "message"),
        };
    }

    private async Task<JsonObject> TimeoutCandidatesAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (window, windowFailure) = await ResolveWindowAsync(arguments, cancellationToken);
        if (windowFailure is not null)
        {
            return windowFailure;
        }

        if (!TryReadPositiveInt(arguments, "threshold_ms", DefaultThresholdMs, out var thresholdMs))
        {
            return Error("threshold_ms must be a positive integer");
        }

        // Falls back to the configured value, and stays null when neither is set, so the
        // monitoring threshold is never mistaken for the application's timeout.
        var applicationTimeoutMs =
            ReadOptionalPositiveInt(arguments, "application_timeout_ms")
            ?? _analysisOptions.ApplicationTimeoutMs;

        var read = await queryStoreCollector.CollectSlowHistoryAsync(
            window.Database,
            window.Start,
            window.End,
            thresholdMs,
            DefaultTopN,
            cancellationToken);

        if (!read.Status.IsAccessible())
        {
            return new JsonObject
            {
                ["database"] = window.Database,
                ["query_store_status"] = EnumMemberJsonConverter<QueryStoreStatus>.ToWireValue(read.Status),
                ["slow_query_analysis_status"] = "data_unavailable",
                ["timeout_classification_status"] = "data_unavailable",
                ["timeout_classification_message"] = "Query Store data was unavailable, so slow-query and timeout analysis could not be evaluated.",
                ["candidate_count"] = 0,
                ["candidates"] = new JsonArray(),
            };
        }

        var candidates = await candidateBuilder.BuildAsync(
            read.Records,
            thresholdMs,
            window.Database,
            groupResults: true,
            ReadWorkloadType(arguments),
            applicationTimeoutMs,
            cancellationToken);

        return new JsonObject
        {
            ["database"] = window.Database,
            ["query_store_status"] = EnumMemberJsonConverter<QueryStoreStatus>.ToWireValue(read.Status),
            ["slow_threshold_ms"] = thresholdMs,
            ["application_timeout_ms"] = applicationTimeoutMs,
            ["slow_query_analysis_status"] = "evaluated",
            ["timeout_classification_status"] = applicationTimeoutMs is null
                ? "not_evaluated"
                : "evaluated",
            ["timeout_classification_message"] = applicationTimeoutMs is null
                ? "Application timeout classification: Not evaluated — ApplicationTimeoutMs is not configured."
                : "Application timeout classification: Evaluated against the configured ApplicationTimeoutMs.",

            // Stated explicitly so the model reports "not determinable" instead of
            // treating the monitoring threshold as the application's timeout.
            ["timeout_classification_available"] = applicationTimeoutMs is not null,
            ["candidate_count"] = candidates.Count,
            ["slow_query_candidate_count"] = candidates.Count(c => c.IsSlowQueryCandidate),
            ["timeout_candidate_count"] = candidates.Count(c => c.IsTimeoutCandidate),
            ["candidates"] = ToJsonArray(candidates, MaxCandidates),
        };
    }

    private async Task<JsonObject> ExecutionPlanAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var (database, failure) = await ResolveRequiredDatabaseAsync(arguments, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var queryHash = ReadString(arguments, "query_hash");
        var queryId = ReadOptionalLong(arguments, "query_id");
        var planId = ReadOptionalLong(arguments, "plan_id");
        if (string.IsNullOrWhiteSpace(queryHash) && queryId is null && planId is null)
        {
            return Error("query_hash, query_id, or plan_id parameter is required");
        }

        if (queryId is < 1 || planId is < 1)
        {
            return Error("query_id and plan_id must be positive integers");
        }

        var result = await executionPlanService.GetAsync(
            database,
            queryHash,
            queryId,
            planId,
            includeXml: false,
            cancellationToken);

        return new JsonObject
        {
            ["status"] = JsonSerializer.SerializeToNode(result.Status, SqlPulseJson.Options),
            ["query_hash"] = result.QueryHash,
            ["query_id"] = result.QueryId,
            ["plan_id"] = result.PlanId,
            ["database"] = result.Database,
            ["statement_count"] = result.StatementCount,
            ["total_operator_count"] = result.TotalOperatorCount,
            ["operator_summary"] = JsonSerializer.SerializeToNode(result.OperatorSummary, SqlPulseJson.Options),
            ["operator_counts_by_type"] = JsonSerializer.SerializeToNode(result.OperatorCountsByType, SqlPulseJson.Options),
            ["important_operators"] = JsonSerializer.SerializeToNode(result.HighestCostOperators, SqlPulseJson.Options),
            ["findings"] = JsonSerializer.SerializeToNode(result.Findings, SqlPulseJson.Options),
            ["warnings"] = JsonSerializer.SerializeToNode(result.Warnings, SqlPulseJson.Options),
            ["plan_limitations"] = JsonSerializer.SerializeToNode(result.PlanLimitations, SqlPulseJson.Options),
        };
    }

    // ─── SQL error executors ────────────────────────────────────────────────

    /// <summary>
    /// Lists normalized SQL errors through the same service the REST endpoint uses.
    /// </summary>
    /// <remarks>
    /// The whole response is forwarded, including <c>status</c>,
    /// <c>application_telemetry_available</c> and the per-source states. Those fields are
    /// what stop the model reading an empty list as a quiet period, so stripping them to
    /// save tokens would remove the only thing preventing a false all-clear.
    /// </remarks>
    private async Task<JsonObject> SqlErrorsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var (database, databaseFailure) = await ResolveOptionalDatabaseAsync(arguments, cancellationToken);
        if (databaseFailure is not null)
        {
            return databaseFailure;
        }

        var startText = ReadString(arguments, "start_time");
        var endText = ReadString(arguments, "end_time");

        if (string.IsNullOrWhiteSpace(startText) || string.IsNullOrWhiteSpace(endText))
        {
            return Error("start_time and end_time are required");
        }

        if (!AgentDateResolver.TryResolve(startText, out var start))
        {
            return Error($"Cannot parse start_time: '{startText}'");
        }

        if (!AgentDateResolver.TryResolve(endText, out var end))
        {
            return Error($"Cannot parse end_time: '{endText}'");
        }

        if (end <= start)
        {
            return Error("end_time must be after start_time");
        }

        if (!TryReadEnumFilter<SqlErrorCategory>(arguments, "category", out var category, out var categoryFailure))
        {
            return categoryFailure!;
        }

        if (!TryReadEnumFilter<Severity>(arguments, "severity", out var severity, out var severityFailure))
        {
            return severityFailure!;
        }

        var result = await sqlErrorService.ListAsync(
            new SqlErrorQuery(
                _sqlServerOptions.Server,
                database,
                start,
                end,
                category,
                ReadString(arguments, "application"),
                ReadString(arguments, "stored_procedure"),
                severity,
                ReadBool(arguments, "confirmed_only"),
                Math.Min(ReadOptionalPositiveInt(arguments, "top_n") ?? MaxSqlErrors, MaxSqlErrors)),
            cancellationToken);

        return ToJsonObject(result);
    }

    /// <summary>Returns one previously listed error, without collecting any new evidence.</summary>
    private Task<JsonObject> SqlErrorDetailsAsync(JsonObject arguments)
    {
        var (errorEvent, failure) = ResolveErrorEvent(arguments);

        return Task.FromResult(failure ?? new JsonObject
        {
            ["found"] = true,
            ["error"] = ToJsonObject(errorEvent!),
        });
    }

    /// <summary>
    /// Returns one error together with the performance evidence observed around it.
    /// </summary>
    /// <remarks>
    /// The correlation and its assessment are computed deterministically before the model
    /// sees them, and the causation caveat is attached to the payload itself. The model's
    /// job is to report the grading it was given, not to produce one.
    /// </remarks>
    private async Task<JsonObject> InvestigateSqlErrorAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var (errorEvent, failure) = ResolveErrorEvent(arguments);
        if (failure is not null)
        {
            return failure;
        }

        var correlation = await sqlErrorCorrelationService.CorrelateAsync(errorEvent!, cancellationToken);

        return new JsonObject
        {
            ["found"] = true,
            ["error"] = ToJsonObject(errorEvent!),
            ["correlation"] = ToJsonObject(correlation),
            ["correlation_confidence"] =
                EnumMemberJsonConverter<CorrelationConfidence>.ToWireValue(correlation.CorrelationConfidence),
            ["causation_caveat"] = SqlErrorCorrelation.CausationCaveat,
        };
    }

    /// <summary>Resolves an <c>error_id</c> argument to a previously returned event.</summary>
    private (SqlErrorEvent? Event, JsonObject? Failure) ResolveErrorEvent(JsonObject arguments)
    {
        var errorId = ReadString(arguments, "error_id");
        if (string.IsNullOrWhiteSpace(errorId))
        {
            return (null, Error("error_id parameter is required"));
        }

        var errorEvent = sqlErrorService.Find(errorId);

        // Not an error: the id was valid once, so the model is told to re-list rather than
        // being handed a failure it would report as an incident.
        return errorEvent is null
            ? (null, new JsonObject
            {
                ["found"] = false,
                ["message"] = SqlErrorDetailResponse.NotFoundMessage,
            })
            : (errorEvent, null);
    }

    // ─── Argument handling ──────────────────────────────────────────────────

    /// <summary>Parses model-supplied arguments, treating malformed JSON as no arguments.</summary>
    /// <remarks>
    /// Models occasionally emit invalid JSON. Falling back to an empty object lets the
    /// tool's own validation produce a message the model can act on, which is more useful
    /// than a parse error.
    /// </remarks>
    private static JsonObject ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Validates a required database argument against the discovery allow-list.</summary>
    private async Task<(string Database, JsonObject? Failure)> ResolveRequiredDatabaseAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var database = ReadString(arguments, "database");
        if (string.IsNullOrWhiteSpace(database))
        {
            return (string.Empty, Error("database parameter is required"));
        }

        var validationError = await discovery.ValidateTargetDatabaseAsync(database, cancellationToken);

        return validationError is null
            ? (database, null)
            : (string.Empty, Error(validationError));
    }

    /// <summary>
    /// Validates an optional database argument, treating a blank value as "no filter".
    /// </summary>
    /// <remarks>
    /// The name originates from the model, so it is checked against the same allow-list the
    /// REST endpoints use rather than being passed straight to a collector.
    /// </remarks>
    private async Task<(string? Database, JsonObject? Failure)> ResolveOptionalDatabaseAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var database = ReadString(arguments, "database");
        if (string.IsNullOrWhiteSpace(database))
        {
            return (null, null);
        }

        var validationError = await discovery.ValidateTargetDatabaseAsync(database, cancellationToken);

        return validationError is null
            ? (database, null)
            : (null, Error(validationError));
    }

    /// <summary>Resolves the database and time range shared by the Query Store tools.</summary>
    private async Task<((string Database, DateTimeOffset Start, DateTimeOffset End) Window, JsonObject? Failure)>
        ResolveWindowAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var empty = (string.Empty, default(DateTimeOffset), default(DateTimeOffset));

        var database = ReadString(arguments, "database");
        var startText = ReadString(arguments, "start_time");
        var endText = ReadString(arguments, "end_time");

        if (string.IsNullOrWhiteSpace(database)
            || string.IsNullOrWhiteSpace(startText)
            || string.IsNullOrWhiteSpace(endText))
        {
            return (empty, Error("database, start_time, and end_time are required"));
        }

        var validationError = await discovery.ValidateTargetDatabaseAsync(database, cancellationToken);
        if (validationError is not null)
        {
            return (empty, Error(validationError));
        }

        if (!AgentDateResolver.TryResolve(startText, out var start))
        {
            return (empty, Error($"Cannot parse start_time: '{startText}'"));
        }

        if (!AgentDateResolver.TryResolve(endText, out var end))
        {
            return (empty, Error($"Cannot parse end_time: '{endText}'"));
        }

        return ((database, start, end), null);
    }

    /// <summary>Reads the optional workload filter, ignoring "ALL" and unrecognised values.</summary>
    private static WorkloadType? ReadWorkloadType(JsonObject arguments)
    {
        var raw = ReadString(arguments, "workload_type");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToUpperInvariant();
        if (normalized == "ALL")
        {
            return null;
        }

        // An unrecognised value means "no filter" rather than an error: dropping a filter
        // shows the model more than it asked for, which it can reason about, whereas
        // failing the call would lose the evidence entirely.
        return EnumMemberJsonConverter<WorkloadType>.TryFromWireValue(normalized, out var workloadType)
            ? workloadType
            : null;
    }

    private static string? ReadString(JsonObject arguments, string key) =>
        arguments[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Reads an optional positive integer, treating anything unusable as absent.
    /// Python: <c>_optional_int</c>.
    /// </summary>
    /// <remarks>
    /// Absent means "unknown", which downstream is meaningfully different from any number
    /// that could be invented, so a malformed value is dropped rather than coerced.
    /// </remarks>
    private static int? ReadOptionalPositiveInt(JsonObject arguments, string key)
    {
        if (arguments[key] is not JsonValue value)
        {
            return null;
        }

        int parsed;
        if (value.TryGetValue<int>(out var number))
        {
            parsed = number;
        }
        else if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var fromText))
        {
            parsed = fromText;
        }
        else
        {
            return null;
        }

        return parsed > 0 ? parsed : null;
    }

    private static long? ReadOptionalLong(JsonObject arguments, string key)
    {
        if (arguments[key] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<long>(out var result) ? result : null;
    }

    /// <summary>Reads an integer that has a default, reporting an unusable value as a failure.</summary>
    /// <remarks>
    /// Unlike the optional values, these select which queries are returned, so a bad value
    /// is reported rather than silently replaced with a default that would change what the
    /// model sees.
    /// </remarks>
    private static bool TryReadPositiveInt(JsonObject arguments, string key, int defaultValue, out int result)
    {
        result = defaultValue;

        if (arguments[key] is not JsonValue value)
        {
            return true;
        }

        if (value.TryGetValue<int>(out var number))
        {
            result = number;
            return number > 0;
        }

        if (value.TryGetValue<string>(out var text))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (int.TryParse(text, out var fromText))
            {
                result = fromText;
                return fromText > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads an optional enum filter, accepting the wire value in any casing.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ReadWorkloadType"/>, an unrecognised value is reported rather than
    /// dropped. Silently ignoring <c>category=TIMEOTU</c> would show the model every error
    /// in the period and invite it to describe unrelated failures as timeouts.
    /// </remarks>
    private static bool TryReadEnumFilter<TEnum>(
        JsonObject arguments,
        string key,
        out TEnum? value,
        out JsonObject? failure)
        where TEnum : struct, Enum
    {
        value = null;
        failure = null;

        var raw = ReadString(arguments, key)?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (EnumMemberJsonConverter<TEnum>.TryFromWireValue(raw, out var exact)
            || EnumMemberJsonConverter<TEnum>.TryFromWireValue(raw.ToUpperInvariant(), out exact)
            || EnumMemberJsonConverter<TEnum>.TryFromWireValue(raw.ToLowerInvariant(), out exact))
        {
            value = exact;
            return true;
        }

        var accepted = string.Join(", ", EnumMemberJsonConverter<TEnum>.WireValues);
        failure = Error($"{key} must be one of: {accepted}");
        return false;
    }

    /// <summary>Reads an optional boolean flag, treating anything unusable as false.</summary>
    private static bool ReadBool(JsonObject arguments, string key)
    {
        if (arguments[key] is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed;
    }

    // ─── Payload helpers ────────────────────────────────────────────────────

    private static JsonObject Error(string message) => new() { ["error"] = message };

    private static JsonNode? Copy(JsonObject source, string key) => source[key]?.DeepClone();

    private static JsonObject ToJsonObject<T>(T value) =>
        JsonSerializer.SerializeToNode(value, SqlPulseJson.Options) as JsonObject ?? [];

    private static JsonArray ToJsonArray<T>(IEnumerable<T> items, int limit)
    {
        var array = new JsonArray();
        foreach (var item in items.Take(limit))
        {
            array.Add(JsonSerializer.SerializeToNode(item, SqlPulseJson.Options));
        }

        return array;
    }

    private static Dictionary<string, object?> ToDictionary(JsonObject source)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            result[key] = value?.DeepClone();
        }

        return result;
    }

    /// <summary>
    /// Reduces tool output to the few fields worth auditing.
    /// Ported from <c>_build_result_summary</c> in <c>app/agent/engine.py</c>.
    /// </summary>
    /// <remarks>
    /// The audit trail records what a tool found, not everything it returned: full payloads
    /// contain SQL text and would make the audit store grow with every investigation.
    /// </remarks>
    private static Dictionary<string, object?> BuildResultSummary(JsonObject payload)
    {
        var summary = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (payload["error"] is { } error)
        {
            summary["error"] = error.DeepClone();
            return summary;
        }

        foreach (var key in ScalarSummaryKeys)
        {
            if (payload[key] is { } value)
            {
                summary[key] = value.DeepClone();
            }
        }

        foreach (var key in CountedSummaryKeys)
        {
            if (payload[key] is JsonArray array)
            {
                summary[$"{key}_count"] = array.Count;
            }
        }

        return summary;
    }

    private static readonly string[] ScalarSummaryKeys =
    [
        "database", "query_store_status", "record_count",
        "candidate_count", "overall_status", "total_findings",
        "regression_count",
        "slow_query_analysis_status", "timeout_classification_status",

        // SQL errors. "status" and "application_telemetry_available" are audited because
        // they decide whether an empty result meant "nothing failed" or "nobody looked".
        "status", "application_telemetry_available", "total_matched", "found",
        "correlation_confidence",
    ];

    private static readonly string[] CountedSummaryKeys =
        ["records", "candidates", "findings", "regressions", "errors"];

    /// <summary>
    /// Projects an analysis result down to the fields the model reasons about.
    /// Ported from <c>_analysis_to_dict</c> in <c>app/agent/tools.py</c>.
    /// </summary>
    private static JsonObject AnalysisToPayload(AnalysisResult result)
    {
        var data = ToJsonObject(result);
        var currentHealth = data["current_health"] as JsonObject;

        var findings = new JsonArray();
        if (data["findings"] is JsonArray rawFindings)
        {
            foreach (var finding in rawFindings.Take(MaxAnalysisFindings).OfType<JsonObject>())
            {
                findings.Add(new JsonObject
                {
                    ["category"] = Copy(finding, "category"),

                    // May be null when plan-cache metadata cannot resolve it.
                    ["database"] = Copy(finding, "database"),
                    ["severity"] = Copy(finding, "severity"),
                    ["confidence"] = Copy(finding, "confidence"),
                    ["signals"] = Copy(finding, "signals") ?? new JsonArray(),
                    ["query_identity"] = Copy(finding, "query_identity"),
                    ["severity_basis"] = Copy(finding, "severity_basis"),
                    ["summary"] = Copy(finding, "summary"),
                    ["evidence"] = Copy(finding, "evidence") ?? new JsonArray(),
                    ["likely_cause"] = Copy(finding, "likely_cause"),
                    ["recommendation"] = Copy(finding, "recommendation"),
                });
            }
        }

        return new JsonObject
        {
            ["overall_status"] = Copy(data, "overall_status"),

            // Health and optimization are separate concerns: a server can be healthy while
            // still having queries worth tuning. Stating this explicitly stops the model
            // reporting tuning candidates as an incident.
            ["current_health_status"] = currentHealth is null ? null : Copy(currentHealth, "status"),
            ["health_finding_count"] = (currentHealth?["findings"] as JsonArray)?.Count ?? 0,
            ["optimization_opportunity_count"] = (data["optimization_opportunities"] as JsonArray)?.Count ?? 0,
            ["summary"] = Copy(data, "summary"),
            ["total_findings"] = Copy(data, "total_findings"),
            ["critical_count"] = Copy(data, "critical_count"),
            ["high_count"] = Copy(data, "high_count"),
            ["medium_count"] = Copy(data, "medium_count"),
            ["low_count"] = Copy(data, "low_count"),
            ["affected_databases"] = Copy(data, "affected_databases") ?? new JsonArray(),
            ["findings"] = findings,
            ["historical_analysis_status"] = Copy(data, "historical_analysis_status"),
        };
    }
}
