using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.Requests;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Services.Agent;

namespace SQLPulseAPI.Net8.Controllers;

/// <summary>
/// AI investigation and its audit trail.
/// Mirrors <c>POST /api/agent/investigate</c>, <c>GET /api/agent/investigations</c> and
/// <c>GET /api/agent/investigations/{investigation_id}</c> in the Python implementation.
/// </summary>
/// <remarks>
/// The agent answers questions about a SQL Server; it never changes one. It has no tool
/// that can execute arbitrary SQL, modify data, kill a session, create an index or force a
/// plan, so a request such as "create and apply the best index" can only ever produce a
/// recommendation for a human to review.
/// </remarks>
[ApiController]
public sealed class AgentController(
    IInvestigationEngine engine,
    IInvestigationAuditService auditService,
    ILlmClient llmClient,
    IOptions<AgentOptions> agentOptions,
    IOptions<LlmOptions> llmOptions,
    ILogger<AgentController> logger) : ControllerBase
{
    /// <summary>Returned when no LLM provider or API key is configured.</summary>
    public const string NotConfiguredMessage =
        "AI agent is not configured. Set LLM_API_KEY in your environment to enable it.";

    /// <summary>Returned when the investigation exceeded its hard time budget.</summary>
    public const string TimedOutMessage =
        "The investigation timed out before it could complete. "
        + "Try a narrower question or a shorter time range.";

    /// <summary>Returned when the provider rejected the conversation as too large.</summary>
    public const string TokenLimitMessage =
        "The AI provider's token limit was exceeded. Try a narrower "
        + "question, such as a single database or a shorter time range.";

    /// <summary>Returned when the provider is rate limiting.</summary>
    public const string RateLimitedMessage =
        "AI provider is temporarily rate-limited. Please try again shortly.";

    /// <summary>Returned when the provider could not be reached.</summary>
    public const string ProviderUnavailableMessage =
        "The AI provider is currently unavailable. Please try again shortly.";

    /// <summary>Returned when an investigation fails for any unclassified reason.</summary>
    public const string InvestigationFailedMessage =
        "AI investigation encountered an error. See server logs.";

    /// <summary>Returned when the audit trail cannot be listed.</summary>
    public const string AuditListFailedMessage = "Unable to retrieve investigation audit records.";

    /// <summary>Returned when a single audit record cannot be read.</summary>
    public const string AuditReadFailedMessage = "Unable to retrieve investigation audit record.";

    private const int DefaultAuditLimit = 50;
    private const int MaxAuditLimit = 500;

    private readonly AgentOptions _agent = agentOptions.Value;
    private readonly LlmOptions _llm = llmOptions.Value;

    /// <summary>
    /// Asks the AI agent a natural-language performance question.
    /// </summary>
    /// <remarks>
    /// The agent classifies the question as current or historical, chooses among the
    /// approved read-only tools, gathers evidence, correlates it, and explains what it
    /// found. It never executes arbitrary SQL and never modifies the database; every
    /// technical claim is backed by tool output.
    /// </remarks>
    /// <param name="request">The question to investigate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("/api/agent/investigate")]
    [ProducesResponseType(typeof(InvestigationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<InvestigationResponse>> Investigate(
        [FromBody] InvestigationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!llmClient.IsConfigured)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, NotConfiguredMessage);
        }

        // The loop only checks its own budget between steps, so the hard cap allows
        // headroom for the final summarisation call before failing cleanly.
        var hardTimeout = TimeSpan.FromSeconds(_agent.MaxExecutionSeconds + _llm.RequestTimeoutSeconds);

        using var timeout = new CancellationTokenSource(hardTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        InvestigationResponse result;

        try
        {
            result = await engine.InvestigateAsync(request.Question, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("AI investigation timed out after {Seconds}s", hardTimeout.TotalSeconds);
            return Error(StatusCodes.Status504GatewayTimeout, TimedOutMessage);
        }
        catch (LlmPayloadTooLargeException exception)
        {
            logger.LogWarning(exception, "AI investigation exceeded the LLM token budget");
            return Error(StatusCodes.Status429TooManyRequests, TokenLimitMessage);
        }
        catch (LlmRateLimitException exception)
        {
            logger.LogWarning(exception, "AI provider rate limit hit");
            return Error(StatusCodes.Status429TooManyRequests, RateLimitedMessage);
        }
        catch (LlmTimeoutException exception)
        {
            logger.LogWarning(exception, "AI provider did not respond in time");
            return Error(StatusCodes.Status504GatewayTimeout, TimedOutMessage);
        }
        catch (LlmNotConfiguredException exception)
        {
            logger.LogWarning(exception, "AI provider is not configured");
            return Error(StatusCodes.Status503ServiceUnavailable, NotConfiguredMessage);
        }
        catch (LlmAuthenticationException exception)
        {
            logger.LogWarning(exception, "AI provider rejected its configured credentials");
            return Error(StatusCodes.Status503ServiceUnavailable, ProviderUnavailableMessage);
        }
        catch (LlmInvalidRequestException exception)
        {
            logger.LogWarning(exception, "AI provider rejected the request");
            return Error(StatusCodes.Status400BadRequest, InvestigationFailedMessage);
        }
        catch (LlmUnavailableException exception)
        {
            logger.LogWarning(exception, "AI provider is unavailable");
            return Error(StatusCodes.Status503ServiceUnavailable, ProviderUnavailableMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AI investigation failed");
            return Error(StatusCodes.Status500InternalServerError, InvestigationFailedMessage);
        }

        // Auditing records work that is already finished, so it cannot be allowed to
        // discard the answer. PersistAsync never throws; the token is deliberately not
        // passed, so a client that has stopped waiting still leaves a trail.
        await auditService.PersistAsync(result, CancellationToken.None);

        return Ok(result);
    }

    /// <summary>
    /// Lists recent investigation audit records, newest first.
    /// </summary>
    /// <remarks>
    /// The trail shows what SQLPulse investigated, which tools it used, what it referenced
    /// and what it concluded. No credentials, API keys or connection strings are stored.
    /// </remarks>
    /// <param name="limit">Maximum number of records to return (1–500).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/agent/investigations")]
    [ProducesResponseType(typeof(InvestigationAuditListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<InvestigationAuditListResponse>> ListInvestigations(
        [FromQuery(Name = "limit")] int limit = DefaultAuditLimit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > MaxAuditLimit)
        {
            return Error(
                StatusCodes.Status422UnprocessableEntity,
                $"limit must be between 1 and {MaxAuditLimit}");
        }

        try
        {
            var audits = await auditService.ListAsync(limit, cancellationToken);

            return Ok(new InvestigationAuditListResponse
            {
                Total = audits.Count,
                Investigations = [.. audits],
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to list investigation audits");
            return Error(StatusCodes.Status500InternalServerError, AuditListFailedMessage);
        }
    }

    /// <summary>
    /// Retrieves a single investigation audit record.
    /// </summary>
    /// <remarks>
    /// An unknown identifier returns <c>found: false</c> with HTTP 200 rather than 404,
    /// matching the Python contract the UI is written against.
    /// </remarks>
    /// <param name="investigationId">Identifier returned by the investigation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("/api/agent/investigations/{investigation_id}")]
    [ProducesResponseType(typeof(InvestigationAuditDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<InvestigationAuditDetailResponse>> GetInvestigation(
        [FromRoute(Name = "investigation_id")] string investigationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var audit = await auditService.GetAsync(investigationId, cancellationToken);

            return Ok(new InvestigationAuditDetailResponse
            {
                Found = audit is not null,
                Investigation = audit,
            });
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to retrieve investigation audit {InvestigationId}",
                investigationId);

            return Error(StatusCodes.Status500InternalServerError, AuditReadFailedMessage);
        }
    }

    private ObjectResult Error(int statusCode, string detail) =>
        StatusCode(statusCode, new ErrorResponse { Detail = detail });
}
