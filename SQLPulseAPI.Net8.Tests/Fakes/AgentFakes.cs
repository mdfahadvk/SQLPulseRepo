using System.Text.Json.Nodes;
using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Services.Analysis;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.QueryStore;

namespace SQLPulseAPI.Net8.Tests.Fakes;

/// <summary>
/// An <see cref="ILlmClient"/> that replays a scripted sequence of model turns.
/// </summary>
/// <remarks>
/// The agent loop is driven entirely by what the model returns, so scripting those turns
/// is what makes the loop testable: tool selection, budget exhaustion, malformed answers
/// and provider failures all become deterministic. No test contacts a real provider.
/// </remarks>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<Func<LlmCompletion>> _turns = new();

    /// <summary>Whether the client reports itself as configured.</summary>
    public bool IsConfigured { get; set; } = true;

    /// <summary>Every request the engine sent, in order.</summary>
    public List<(IReadOnlyList<LlmMessage> Messages, IReadOnlyList<LlmToolDefinition>? Tools)> Requests { get; } = [];

    /// <summary>Returned once the script is exhausted.</summary>
    public LlmCompletion Fallback { get; set; } = new("Summary: nothing further.", [], "stop");

    /// <summary>Number of completions requested.</summary>
    public int CallCount => Requests.Count;

    /// <summary>Queues a turn in which the model writes an answer and stops.</summary>
    public ScriptedLlmClient ThenAnswers(string answer)
    {
        _turns.Enqueue(() => new LlmCompletion(answer, [], "stop"));
        return this;
    }

    /// <summary>Queues a turn in which the model requests tools.</summary>
    public ScriptedLlmClient ThenCalls(params (string Name, string ArgumentsJson)[] calls)
    {
        var toolCalls = calls
            .Select((call, index) => new LlmToolCall($"call_{index + 1}", call.Name, call.ArgumentsJson))
            .ToList();

        _turns.Enqueue(() => new LlmCompletion(null, toolCalls, "tool_calls"));
        return this;
    }

    /// <summary>Queues a turn that fails, so provider failures can be exercised.</summary>
    public ScriptedLlmClient ThenThrows(Exception exception)
    {
        _turns.Enqueue(() => throw exception);
        return this;
    }

    /// <inheritdoc />
    public Task<LlmCompletion> CompleteAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Copied because the engine keeps mutating its own list between turns.
        Requests.Add((messages.ToList(), tools));

        return Task.FromResult(_turns.Count > 0 ? _turns.Dequeue()() : Fallback);
    }
}

/// <summary>
/// Records tool invocations without touching SQL Server, while still honouring the
/// approved-tool allow-list.
/// </summary>
/// <remarks>
/// The allow-list is reproduced rather than bypassed so loop tests observe the same
/// refusal the real invoker produces for a tool SQLPulse does not offer.
/// </remarks>
public sealed class StubAgentToolInvoker : IAgentToolInvoker
{
    /// <summary>Every tool the model asked for, approved or not, in order.</summary>
    public List<(string ToolName, string? ArgumentsJson)> Calls { get; } = [];

    /// <summary>Optional payload override, used to exercise large evidence.</summary>
    public Func<string, JsonObject>? PayloadFor { get; set; }

    /// <inheritdoc />
    public Task<AgentToolInvocation> InvokeAsync(
        string toolName,
        string? argumentsJson,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((toolName, argumentsJson));

        var approved = AgentToolDefinitions.ApprovedNames.Contains(toolName);
        var payload = PayloadFor?.Invoke(toolName) ?? new JsonObject { ["ok"] = true };

        return Task.FromResult(new AgentToolInvocation(
            payload,
            new ToolCall
            {
                ToolName = toolName,
                Success = approved,
                Error = approved ? null : AgentToolInvoker.NotApprovedMessage,
            }));
    }
}

/// <summary>
/// Serves canned HTTP responses so the LLM client can be tested without a provider.
/// </summary>
/// <remarks>
/// No test may ever call a real AI provider: that would make the suite non-deterministic,
/// dependent on a network and on someone's API key, and would spend money to assert
/// behaviour SQLPulse controls entirely on its own side of the wire.
/// </remarks>
public sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    /// <summary>Body of the most recent request, captured before the responder runs.</summary>
    public string? LastRequestBody { get; private set; }

    /// <summary>The most recent request, for header and URI assertions.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Number of requests sent through this handler.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Responds with a fixed status and body.</summary>
    public static StubHttpMessageHandler Returning(System.Net.HttpStatusCode statusCode, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }));

    /// <summary>Never responds, so the caller's timeout is what ends the request.</summary>
    public static StubHttpMessageHandler NeverResponds() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });

    /// <summary>Fails as though the provider could not be reached.</summary>
    public static StubHttpMessageHandler Unreachable() =>
        new((_, _) => throw new HttpRequestException("No such host is known."));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return await responder(request, cancellationToken);
    }
}

/// <summary>Hands out clients bound to a single stub handler.</summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    /// <inheritdoc />
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// <see cref="IDatabaseDiscoveryService"/> backed by a fixed allow-list.
/// </summary>
public sealed class StubDatabaseDiscoveryService : IDatabaseDiscoveryService
{
    /// <summary>Message returned for a name outside the allow-list.</summary>
    public const string RejectionMessage = "Database not found or not accessible.";

    /// <summary>Names accepted by <see cref="ValidateTargetDatabaseAsync"/>.</summary>
    public HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names passed to <see cref="ValidateTargetDatabaseAsync"/>, in order.</summary>
    public List<string?> Validated { get; } = [];

    /// <summary>Creates a discovery stub that accepts the given names.</summary>
    public static StubDatabaseDiscoveryService Allowing(params string[] databases)
    {
        var stub = new StubDatabaseDiscoveryService();
        foreach (var database in databases)
        {
            stub.Allowed.Add(database);
        }

        return stub;
    }

    /// <inheritdoc />
    public Task<DatabaseDiscoveryResult> DiscoverDatabasesAsync(
        bool useCache = true,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DatabaseDiscoveryResult(
            SQLPulseAPI.Net8.Infrastructure.SqlOperationOutcome.Success(),
            Allowed.Select(name => new DatabaseInfo(name, "ONLINE")).ToList()));

    /// <inheritdoc />
    public Task<string?> ValidateTargetDatabaseAsync(
        string? database,
        CancellationToken cancellationToken = default)
    {
        Validated.Add(database);

        if (string.IsNullOrWhiteSpace(database))
        {
            return Task.FromResult<string?>(DatabaseDiscoveryService.DatabaseRequiredMessage);
        }

        return Task.FromResult(Allowed.Contains(database) ? null : RejectionMessage);
    }

    /// <inheritdoc />
    public void InvalidateCache()
    {
    }
}

/// <summary>Returns a pre-built analysis result and records the requests it was given.</summary>
public sealed class StubAnalysisEngine : IAnalysisEngine
{
    /// <summary>Result returned by every call.</summary>
    public AnalysisResult Result { get; set; } = new();

    /// <summary>Requests received, in order.</summary>
    public List<AnalysisRequest> Requests { get; } = [];

    /// <inheritdoc />
    public Task<AnalysisResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }

    /// <inheritdoc />
    public Task<AnalysisResult> RunAnalysisAsync(
        PerformanceSnapshot snapshot,
        AnalysisScope scope,
        string? database,
        CancellationToken cancellationToken = default) => Task.FromResult(Result);
}

/// <summary>Returns a pre-built slow-history response and records the arguments it was called with.</summary>
public sealed class StubSlowHistoryService : ISlowHistoryService
{
    /// <summary>Response returned by every call.</summary>
    public SlowHistoryResponse Response { get; set; } = new();

    /// <summary>Arguments of the most recent call.</summary>
    public (string Database, DateTimeOffset Start, DateTimeOffset End, int ThresholdMs, int TopN,
        bool GroupResults, WorkloadType? WorkloadType, int? ApplicationTimeoutMs)? LastCall
    { get; private set; }

    /// <inheritdoc />
    public Task<SlowHistoryResponse> GetSlowHistoryAsync(
        string database,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        int thresholdMs,
        int topN = 20,
        bool groupResults = true,
        WorkloadType? workloadType = null,
        int? applicationTimeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        LastCall = (database, startTime, endTime, thresholdMs, topN, groupResults, workloadType, applicationTimeoutMs);
        return Task.FromResult(Response);
    }
}

/// <summary>Returns a fixed Query Store status and record set.</summary>
public sealed class StubQueryStoreCollector : IQueryStoreCollector
{
    /// <summary>Status reported for every database.</summary>
    public QueryStoreStatus Status { get; set; } = QueryStoreStatus.Available;

    /// <summary>Records returned by <see cref="CollectSlowHistoryAsync"/>.</summary>
    public List<SlowQueryRecord> Records { get; } = [];

    /// <inheritdoc />
    public string ServerHost => "sql01";

    /// <inheritdoc />
    public Task<(QueryStoreStatus Status, string? Message)> CheckStatusAsync(
        string database,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<(QueryStoreStatus, string?)>((Status, null));

    /// <inheritdoc />
    public Task<QueryStoreReadResult> CollectSlowHistoryAsync(
        string database,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        double thresholdMs,
        int topN = 20,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new QueryStoreReadResult(Status, Records, null));
}

/// <summary>Returns pre-built candidates and records the arguments it was called with.</summary>
public sealed class StubSlowQueryCandidateBuilder : ISlowQueryCandidateBuilder
{
    /// <summary>Candidates returned by every call.</summary>
    public List<TimeoutCandidate> Candidates { get; } = [];

    /// <summary>Application timeout passed to the most recent call.</summary>
    public double? LastApplicationTimeoutMs { get; private set; }

    /// <summary>Workload filter passed to the most recent call.</summary>
    public WorkloadType? LastWorkloadTypeFilter { get; private set; }

    /// <inheritdoc />
    public Task<List<TimeoutCandidate>> BuildAsync(
        IReadOnlyList<SlowQueryRecord> slowRecords,
        double thresholdMs,
        string database,
        bool groupResults = true,
        WorkloadType? workloadTypeFilter = null,
        double? applicationTimeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        LastApplicationTimeoutMs = applicationTimeoutMs;
        LastWorkloadTypeFilter = workloadTypeFilter;
        return Task.FromResult(Candidates);
    }
}

public sealed class StubExecutionPlanService : IExecutionPlanService
{
    public ExecutionPlanResponse Response { get; set; } = new()
    {
        Status = ExecutionPlanStatus.NotFound,
        Message = "No test execution plan configured.",
    };

    public Task<ExecutionPlanResponse> GetAsync(
        string database,
        string? queryHash,
        long? queryId,
        long? planId,
        bool includeXml = false,
        CancellationToken cancellationToken = default) => Task.FromResult(Response);
}
