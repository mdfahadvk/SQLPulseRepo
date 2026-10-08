using System.Diagnostics;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Agent;

namespace SQLPulseAPI.Net8.Services.Agent;

/// <summary>Runs a natural-language performance investigation.</summary>
public interface IInvestigationEngine
{
    /// <summary>
    /// Investigates a question and returns an evidence-backed, structured answer.
    /// </summary>
    /// <param name="question">Natural-language performance question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<InvestigationResponse> InvestigateAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>
/// The tool-calling agent loop.
/// Ported from <c>investigate</c> in <c>app/agent/engine.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// The flow is UNDERSTAND → OBSERVE → ASSESS → CORRELATE → PRIORITIZE → RECOMMEND. The
/// model decides which evidence to gather; the loop decides nothing about SQL Server. It
/// observes and explains, and performs no remediation — there is no tool that could.
/// </para>
/// <para>
/// Three guard-rails bound every investigation: a maximum number of tool calls, a
/// wall-clock budget, and the approved-tool allow-list. The first two exist because a model
/// can loop indefinitely on an ambiguous question; when either is reached the loop asks for
/// a conclusion from the evidence already gathered rather than returning nothing.
/// </para>
/// </remarks>
public sealed class InvestigationEngine(
    ILlmClient llmClient,
    IAgentToolInvoker toolInvoker,
    IOptions<AgentOptions> agentOptions,
    ILogger<InvestigationEngine> logger) : IInvestigationEngine
{
    /// <summary>Used when the model returns no text at all.</summary>
    public const string FallbackAnswer = "Unable to produce an analysis.";

    private const string TimeLimitPrompt =
        "Time limit reached. Please provide your analysis based on the evidence gathered so far.";

    private const string ToolLimitPrompt =
        "Tool-call limit reached. Please provide your analysis based on the evidence gathered so far.";

    private const string EvidenceTruncatedSuffix = " \u2026[evidence truncated to fit token budget]";

    /// <summary>Budget kept for the newest tool result when the conversation must shrink.</summary>
    private const int CompactedRecentChars = 2000;

    /// <summary>Budget for older tool results, which have already shaped the conversation.</summary>
    private const int CompactedOlderChars = 400;

    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(30);

    private readonly AgentOptions _agent = agentOptions.Value;

    /// <inheritdoc />
    public async Task<InvestigationResponse> InvestigateAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var investigationId = Guid.NewGuid().ToString();
        var stopwatch = Stopwatch.StartNew();

        logger.LogInformation(
            "Investigation {InvestigationId} started - question: {Question}",
            investigationId,
            question.Length > 200 ? question[..200] : question);

        var messages = new List<LlmMessage>
        {
            LlmMessage.System(AgentPrompt.Build(DateTimeOffset.Now)),
            LlmMessage.User(question),
        };

        var toolCalls = new List<ToolCall>();
        var toolsUsed = new List<string>();
        var sentToolEvidence = new HashSet<string>(StringComparer.Ordinal);
        var totalCalls = 0;

        LlmCompletion completion;

        while (true)
        {
            if (stopwatch.Elapsed.TotalSeconds > _agent.MaxExecutionSeconds)
            {
                logger.LogWarning(
                    "Investigation {InvestigationId} hit the time limit ({Seconds}s)",
                    investigationId,
                    _agent.MaxExecutionSeconds);

                messages.Add(LlmMessage.User(TimeLimitPrompt));
                completion = await GenerateAsync(messages, null, investigationId, cancellationToken);
                break;
            }

            if (totalCalls >= _agent.MaxToolCalls)
            {
                logger.LogWarning(
                    "Investigation {InvestigationId} hit the tool-call limit ({Limit})",
                    investigationId,
                    _agent.MaxToolCalls);

                messages.Add(LlmMessage.User(ToolLimitPrompt));
                completion = await GenerateAsync(messages, null, investigationId, cancellationToken);
                break;
            }

            completion = await GenerateAsync(
                messages, AgentToolDefinitions.All, investigationId, cancellationToken);

            if (!completion.HasToolCalls)
            {
                break;
            }

            messages.Add(LlmMessage.Assistant(completion.Content, completion.ToolCalls));

            foreach (var call in completion.ToolCalls)
            {
                totalCalls++;

                var invocation = await toolInvoker.InvokeAsync(
                    call.FunctionName, call.ArgumentsJson, cancellationToken);

                toolCalls.Add(invocation.Audit);

                // A refused tool is recorded in the audit but never counted as used: the
                // list is what SQLPulse actually consulted, not what the model asked for.
                if (AgentToolDefinitions.ApprovedNames.Contains(call.FunctionName)
                    && !toolsUsed.Contains(call.FunctionName, StringComparer.Ordinal))
                {
                    toolsUsed.Add(call.FunctionName);
                }

                logger.LogInformation(
                    "Investigation {InvestigationId} - tool {ToolName} completed (success={Success}, {Duration}s)",
                    investigationId,
                    call.FunctionName,
                    invocation.Audit.Success,
                    invocation.Audit.DurationSeconds);

                // Bounded so a single large snapshot cannot exhaust the model context or
                // the provider's token quota.
                var toolResult = LlmSanitizer.SerializeForLlm(
                    invocation.Payload, _agent.MaxToolResultChars);
                var evidenceKey = $"{call.FunctionName}:{toolResult}";
                if (!sentToolEvidence.Add(evidenceKey))
                {
                    toolResult = "[duplicate evidence omitted; the same result was already provided]";
                }

                messages.Add(LlmMessage.Tool(toolResult, call.Id, call.FunctionName));
            }
        }

        stopwatch.Stop();

        var answer = string.IsNullOrWhiteSpace(completion.Content)
            ? FallbackAnswer
            : completion.Content;

        var response = new InvestigationResponse
        {
            InvestigationId = investigationId,
            Question = question,
            Answer = answer,
            ToolsUsed = toolsUsed,
            ToolCallsDetail = toolCalls,
            ToolCallCount = totalCalls,
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
        };

        // The structured fields are read back out of the answer, so the prose and the
        // structured data are the same statement expressed twice and cannot disagree.
        InvestigationAnswerParser.Apply(response, answer);

        logger.LogInformation(
            "Investigation {InvestigationId} completed - severity={Severity} confidence={Confidence} "
            + "tools={ToolCount} duration={Duration}s",
            investigationId,
            response.Severity,
            response.Confidence,
            totalCalls,
            response.DurationSeconds);

        return response;
    }

    /// <summary>
    /// Calls the model, shrinking the conversation if the provider rejects its size.
    /// </summary>
    /// <remarks>
    /// A summarisation call offers no tools, and providers reject a history that still
    /// contains tool calls in that case, so the conversation is flattened first.
    /// </remarks>
    private async Task<LlmCompletion> GenerateAsync(
        List<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        string investigationId,
        CancellationToken cancellationToken)
    {
        var outbound = tools is not null ? messages : FlattenForSummary(messages);

        try
        {
            return await llmClient.CompleteAsync(outbound, tools, cancellationToken);
        }
        catch (LlmRateLimitException exception)
            when (!string.Equals(
                exception.Message,
                "AI provider is temporarily rate-limited. Please try again shortly.",
                StringComparison.Ordinal))
        {
            // Compatibility fallback for alternate ILlmClient implementations. The
            // production HTTP client owns its three retries and cooldown; its explicit
            // cooldown exception is intentionally not retried here.
            var wait = exception.RetryAfter is { } hint && hint > TimeSpan.Zero
                ? (hint < MaxRateLimitWait ? hint : MaxRateLimitWait)
                : DefaultRateLimitWait;

            logger.LogWarning(
                "Investigation {InvestigationId} hit a rate limit from its LLM client; "
                + "waiting {WaitMs}ms and retrying",
                investigationId,
                (int)wait.TotalMilliseconds);

            await Task.Delay(wait, cancellationToken);
            return await llmClient.CompleteAsync(outbound, tools, cancellationToken);
        }
        catch (LlmPayloadTooLargeException)
        {
            logger.LogWarning(
                "Investigation {InvestigationId} exceeded the provider token budget - "
                + "compacting evidence and retrying",
                investigationId);

            CompactToolMessages(messages);

            // The retry drops the tool schemas too: at this point the goal is a final
            // answer from the evidence already collected, not more calls.
            return await llmClient.CompleteAsync(FlattenForSummary(messages), null, cancellationToken);
        }
    }

    /// <summary>
    /// Rebuilds the conversation as plain text, without tool-call plumbing.
    /// Ported from <c>_flatten_for_summary</c>.
    /// </summary>
    /// <remarks>
    /// Tool results are preserved as ordinary evidence text so the model keeps everything
    /// it needs to answer; only the protocol scaffolding is dropped.
    /// </remarks>
    internal static List<LlmMessage> FlattenForSummary(IReadOnlyList<LlmMessage> messages)
    {
        var flat = new List<LlmMessage>(messages.Count);

        foreach (var message in messages)
        {
            if (string.Equals(message.Role, "tool", StringComparison.Ordinal))
            {
                flat.Add(LlmMessage.User(
                    $"Evidence from {message.Name ?? "tool"}:\n{message.Content ?? string.Empty}"));
            }
            else if (string.Equals(message.Role, "assistant", StringComparison.Ordinal)
                     && message.ToolCalls is { Count: > 0 })
            {
                // The request to call a tool is noise once the results are inlined.
                if (!string.IsNullOrEmpty(message.Content))
                {
                    flat.Add(LlmMessage.Assistant(message.Content));
                }
            }
            else
            {
                flat.Add(message);
            }
        }

        return flat;
    }

    /// <summary>
    /// Shrinks accumulated tool output so the conversation fits again.
    /// Ported from <c>_compact_tool_messages</c>.
    /// </summary>
    /// <remarks>
    /// The most recent tool result is kept at a larger size and older ones are cut harder:
    /// recent evidence is what the model still needs to reason about, while older results
    /// have already shaped the conversation.
    /// </remarks>
    internal static void CompactToolMessages(List<LlmMessage> messages)
    {
        var toolIndexes = new List<int>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (string.Equals(messages[index].Role, "tool", StringComparison.Ordinal))
            {
                toolIndexes.Add(index);
            }
        }

        if (toolIndexes.Count == 0)
        {
            return;
        }

        var newest = toolIndexes[^1];

        foreach (var messageIndex in toolIndexes)
        {
            var message = messages[messageIndex];
            var content = message.Content ?? string.Empty;
            var limit = messageIndex == newest ? CompactedRecentChars : CompactedOlderChars;

            if (content.Length > limit)
            {
                messages[messageIndex] = LlmMessage.Tool(
                    string.Concat(content.AsSpan(0, limit), EvidenceTruncatedSuffix),
                    message.ToolCallId ?? string.Empty,
                    message.Name ?? string.Empty);
            }
        }
    }
}
