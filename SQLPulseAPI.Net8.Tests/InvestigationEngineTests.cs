using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// The agent loop: understand, gather evidence with approved tools, correlate, explain.
/// </summary>
/// <remarks>
/// Every test scripts the model, so the loop's own decisions — which tools count as used,
/// when the budgets stop it, how it recovers from a provider rejecting the conversation
/// size — are what is under test, not the model's behaviour.
/// </remarks>
public sealed class InvestigationEngineTests
{
    private const string Question = "What is affecting Merchandising performance right now?";

    private sealed class Harness
    {
        public ScriptedLlmClient Llm { get; } = new();

        public StubAgentToolInvoker Tools { get; } = new();

        public AgentOptions Options { get; } = new();

        public InvestigationEngine Build() => new(
            Llm,
            Tools,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<InvestigationEngine>.Instance);
    }

    private static string StructuredAnswer(string severity = "high", string confidence = "medium") =>
        $"""
         Answer: Session 71 is blocking four sessions in Merchandising.

         Severity: {severity}
         Confidence: {confidence}
         Affected Database: Merchandising
         Likely Cause: an open transaction holding an exclusive lock

         Evidence:
         - Session 71 has been open for 210 seconds

         Human Review Required: Yes
         """;

    // --- The basic flow -------------------------------------------------------

    [Fact]
    public async Task An_answer_without_tool_calls_ends_the_investigation_immediately()
    {
        var harness = new Harness();
        harness.Llm.ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal(1, harness.Llm.CallCount);
        Assert.Empty(harness.Tools.Calls);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Equal(Question, result.Question);
        Assert.NotEmpty(result.InvestigationId);
    }

    [Fact]
    public async Task Evidence_is_gathered_before_the_answer_is_written()
    {
        var harness = new Harness();
        harness.Llm
            .ThenCalls((AgentToolNames.GetBlocking, """{"database":"Merchandising"}"""))
            .ThenCalls((AgentToolNames.GetActiveRequests, "{}"))
            .ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal(
            [AgentToolNames.GetBlocking, AgentToolNames.GetActiveRequests],
            harness.Tools.Calls.Select(call => call.ToolName));

        Assert.Equal(2, result.ToolCallCount);
        Assert.Equal(
            [AgentToolNames.GetBlocking, AgentToolNames.GetActiveRequests],
            result.ToolsUsed);
    }

    [Fact]
    public async Task Tool_results_are_fed_back_to_the_model_as_tool_messages()
    {
        var harness = new Harness();
        harness.Llm
            .ThenCalls((AgentToolNames.GetBlocking, "{}"))
            .ThenAnswers(StructuredAnswer());

        await harness.Build().InvestigateAsync(Question);

        var second = harness.Llm.Requests[1].Messages;

        Assert.Equal("tool", second[^1].Role);
        Assert.Equal(AgentToolNames.GetBlocking, second[^1].Name);
        Assert.Contains("\"ok\"", second[^1].Content);
    }

    [Fact]
    public async Task The_same_tool_used_twice_is_listed_once_but_counted_twice()
    {
        // tools_used answers "what did SQLPulse consult"; tool_call_count answers "how
        // much work did it do". Conflating them would hide a model looping on one tool.
        var harness = new Harness();
        harness.Llm
            .ThenCalls((AgentToolNames.GetTopCpuQueries, "{}"))
            .ThenCalls((AgentToolNames.GetTopCpuQueries, "{}"))
            .ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal([AgentToolNames.GetTopCpuQueries], result.ToolsUsed);
        Assert.Equal(2, result.ToolCallCount);
    }

    [Fact]
    public async Task Several_tools_requested_in_one_turn_all_run()
    {
        var harness = new Harness();
        harness.Llm
            .ThenCalls(
                (AgentToolNames.GetActiveRequests, "{}"),
                (AgentToolNames.GetBlocking, "{}"),
                (AgentToolNames.GetLongRunningQueries, "{}"))
            .ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal(3, harness.Tools.Calls.Count);
        Assert.Equal(3, result.ToolsUsed.Count);
    }

    // --- The allow-list -------------------------------------------------------

    [Fact]
    public async Task A_tool_that_is_not_approved_is_recorded_but_never_reported_as_used()
    {
        // The audit must show what was asked for; tools_used must show only what SQLPulse
        // actually consulted, so a refused request cannot look like evidence.
        var harness = new Harness();
        harness.Llm
            .ThenCalls(("apply_index", """{"database":"Merchandising"}"""))
            .ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Empty(result.ToolsUsed);
        Assert.Equal(1, result.ToolCallCount);

        var audited = Assert.Single(result.ToolCallsDetail);
        Assert.Equal("apply_index", audited.ToolName);
        Assert.False(audited.Success);
        Assert.Equal(AgentToolInvoker.NotApprovedMessage, audited.Error);
    }

    [Fact]
    public async Task Only_approved_tools_are_ever_offered_to_the_model()
    {
        var harness = new Harness();
        harness.Llm.ThenAnswers(StructuredAnswer());

        await harness.Build().InvestigateAsync(Question);

        var offered = harness.Llm.Requests[0].Tools!;

        Assert.NotEmpty(offered);
        Assert.All(offered, tool => Assert.Contains(tool.Name, AgentToolDefinitions.ApprovedNames));
    }

    // --- Guard-rails ----------------------------------------------------------

    [Fact]
    public async Task The_tool_call_budget_stops_the_loop_and_asks_for_a_conclusion()
    {
        var harness = new Harness();
        harness.Options.MaxToolCalls = 2;

        harness.Llm
            .ThenCalls((AgentToolNames.GetBlocking, "{}"))
            .ThenCalls((AgentToolNames.GetActiveRequests, "{}"))
            .ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        // Two evidence turns plus a final summarisation turn.
        Assert.Equal(3, harness.Llm.CallCount);
        Assert.Equal(2, result.ToolCallCount);

        var final = harness.Llm.Requests[^1];
        Assert.Null(final.Tools);
        Assert.Contains("Tool-call limit reached", final.Messages[^1].Content);
    }

    [Fact]
    public async Task The_time_budget_stops_the_loop_and_asks_for_a_conclusion()
    {
        // Zero seconds makes the very first budget check fail, which is the same code
        // path a long investigation reaches — without making the test slow.
        var harness = new Harness();
        harness.Options.MaxExecutionSeconds = 0;
        harness.Llm.ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal(1, harness.Llm.CallCount);
        Assert.Empty(harness.Tools.Calls);
        Assert.Null(harness.Llm.Requests[0].Tools);
        Assert.Contains("Time limit reached", harness.Llm.Requests[0].Messages[^1].Content);
        Assert.Equal(0, result.ToolCallCount);
    }

    [Fact]
    public async Task A_stopped_investigation_still_returns_what_it_found()
    {
        // Running out of budget is not a failure. Partial evidence with an honest
        // confidence rating is more useful than no answer at all.
        var harness = new Harness();
        harness.Options.MaxToolCalls = 1;

        harness.Llm
            .ThenCalls((AgentToolNames.GetBlocking, "{}"))
            .ThenAnswers(StructuredAnswer(severity: "medium", confidence: "low"));

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal("medium", result.Severity);
        Assert.Equal("low", result.Confidence);
        Assert.NotEmpty(result.Evidence);
    }

    // --- Provider limits ------------------------------------------------------

    [Fact]
    public async Task An_oversized_conversation_is_compacted_and_retried()
    {
        // A provider rejecting the payload must not lose an investigation's evidence; the
        // conversation is shrunk and a final answer is requested from what was gathered.
        var harness = new Harness();
        harness.Tools.PayloadFor = _ => new JsonObject
        {
            ["rows"] = new string('x', 6000),
        };

        harness.Llm
            .ThenCalls((AgentToolNames.GetTopIoQueries, "{}"))
            .ThenThrows(new LlmPayloadTooLargeException("too large"))
            .ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal(3, harness.Llm.CallCount);
        Assert.Null(harness.Llm.Requests[^1].Tools);
        Assert.Equal("high", result.Severity);
    }

    [Fact]
    public async Task The_retry_after_compaction_carries_the_evidence_as_plain_text()
    {
        var harness = new Harness();
        harness.Llm
            .ThenCalls((AgentToolNames.GetBlocking, "{}"))
            .ThenThrows(new LlmPayloadTooLargeException("too large"))
            .ThenAnswers(StructuredAnswer());

        await harness.Build().InvestigateAsync(Question);

        var retry = harness.Llm.Requests[^1].Messages;

        Assert.DoesNotContain(retry, message => message.Role == "tool");
        Assert.Contains(retry, message =>
            message.Role == "user"
            && message.Content!.StartsWith($"Evidence from {AgentToolNames.GetBlocking}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_transient_rate_limit_is_waited_out_and_retried()
    {
        // A full per-minute quota window is fixed by waiting, not by sending less. Giving
        // up on the first 429 would fail every investigation needing more than one call
        // on a small provider quota.
        var harness = new Harness();
        harness.Llm
            .ThenThrows(new LlmRateLimitException("slow down", TimeSpan.FromMilliseconds(1)))
            .ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal(2, harness.Llm.CallCount);
        Assert.Equal("high", result.Severity);
    }

    [Fact]
    public async Task A_rate_limit_that_persists_is_surfaced_to_the_caller()
    {
        // Retried once, not indefinitely. A provider that is still refusing must be
        // reported rather than presented as a clean bill of health for the server.
        var harness = new Harness();
        harness.Llm
            .ThenThrows(new LlmRateLimitException("slow down", TimeSpan.FromMilliseconds(1)))
            .ThenThrows(new LlmRateLimitException("slow down", TimeSpan.FromMilliseconds(1)));

        await Assert.ThrowsAsync<LlmRateLimitException>(
            () => harness.Build().InvestigateAsync(Question));
    }

    [Fact]
    public async Task A_provider_failure_other_than_size_is_surfaced_to_the_caller()
    {
        // An unreachable provider is not retryable here. Swallowing it would present a
        // provider outage as a clean bill of health for the server.
        var harness = new Harness();
        harness.Llm.ThenThrows(new LlmUnavailableException("provider down"));

        await Assert.ThrowsAsync<LlmUnavailableException>(
            () => harness.Build().InvestigateAsync(Question));
    }

    // --- The answer -----------------------------------------------------------

    [Fact]
    public async Task The_structured_fields_are_derived_from_the_answer_text()
    {
        // One source of truth: a client cannot be shown "severity: high" beside prose
        // that says nothing is wrong, because the severity was read out of that prose.
        var harness = new Harness();
        harness.Llm.ThenAnswers(StructuredAnswer(severity: "critical", confidence: "high"));

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Contains("Severity: critical", result.Answer);
        Assert.Equal("critical", result.Severity);
        Assert.Equal("high", result.Confidence);
        Assert.Equal("Merchandising", result.AffectedDatabase);
        Assert.Equal("an open transaction holding an exclusive lock", result.LikelyCause);
        Assert.Equal(["Session 71 has been open for 210 seconds"], result.Evidence);
        Assert.True(result.HumanReviewRequired);
    }

    [Fact]
    public async Task An_empty_model_response_becomes_an_explicit_failure_to_analyse()
    {
        var harness = new Harness();
        harness.Llm.Fallback = new LlmCompletion("   ", [], "stop");

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal(InvestigationEngine.FallbackAnswer, result.Answer);
        Assert.Equal("unknown", result.Severity);
        Assert.True(result.HumanReviewRequired);
    }

    [Fact]
    public async Task An_investigation_never_reports_having_changed_anything()
    {
        var harness = new Harness();
        harness.Llm.ThenAnswers(StructuredAnswer());

        var result = await harness.Build().InvestigateAsync(Question);

        Assert.Equal(InvestigationResponse.NoRemediationPerformed, result.AgentAction);
    }

    // --- The prompt -----------------------------------------------------------

    [Fact]
    public async Task The_conversation_opens_with_the_system_prompt_and_the_question()
    {
        var harness = new Harness();
        harness.Llm.ThenAnswers(StructuredAnswer());

        await harness.Build().InvestigateAsync(Question);

        var opening = harness.Llm.Requests[0].Messages;

        Assert.Equal("system", opening[0].Role);
        Assert.Equal("user", opening[1].Role);
        Assert.Equal(Question, opening[1].Content);
    }

    [Fact]
    public void The_system_prompt_carries_the_current_date()
    {
        // "Right now" and "yesterday" are unanswerable without it, and a model asked to
        // guess the date will resolve relative ranges against the wrong window.
        var now = new DateTimeOffset(2025, 3, 14, 9, 26, 53, TimeSpan.Zero);

        var prompt = AgentPrompt.Build(now);

        Assert.Contains("2025-03-14T09:26:53", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{current_datetime}", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_system_prompt_states_the_distinction_the_answer_must_preserve()
    {
        var prompt = AgentPrompt.Build(DateTimeOffset.UtcNow);

        Assert.Contains("CURRENT INCIDENT", prompt, StringComparison.Ordinal);
        Assert.Contains("OPTIMIZATION OPPORTUNITY", prompt, StringComparison.Ordinal);
    }

    // --- Conversation shaping -------------------------------------------------

    [Fact]
    public void Flattening_keeps_the_evidence_and_drops_the_tool_plumbing()
    {
        var messages = new List<LlmMessage>
        {
            LlmMessage.System("system"),
            LlmMessage.User("question"),
            LlmMessage.Assistant(null, [new LlmToolCall("call_1", "get_blocking", "{}")]),
            LlmMessage.Tool("{\"blocked\":1}", "call_1", "get_blocking"),
        };

        var flat = InvestigationEngine.FlattenForSummary(messages);

        Assert.DoesNotContain(flat, message => message.ToolCalls is { Count: > 0 });
        Assert.DoesNotContain(flat, message => message.Role == "tool");
        Assert.Contains(flat, message => message.Content!.Contains("{\"blocked\":1}", StringComparison.Ordinal));
    }

    [Fact]
    public void Compaction_keeps_the_newest_evidence_and_cuts_the_older_harder()
    {
        // Recent evidence is what the model still has to reason about; older results have
        // already shaped the conversation.
        var messages = new List<LlmMessage>
        {
            LlmMessage.Tool(new string('a', 9000), "call_1", "get_top_cpu_queries"),
            LlmMessage.Tool(new string('b', 9000), "call_2", "get_blocking"),
        };

        InvestigationEngine.CompactToolMessages(messages);

        Assert.True(messages[0].Content!.Length < messages[1].Content!.Length);
        Assert.All(messages, message =>
            Assert.Contains("evidence truncated", message.Content!, StringComparison.Ordinal));
        Assert.All(messages, message => Assert.Equal("tool", message.Role));
    }

    [Fact]
    public void Compaction_leaves_short_evidence_untouched()
    {
        var messages = new List<LlmMessage> { LlmMessage.Tool("{\"blocked\":1}", "call_1", "get_blocking") };

        InvestigationEngine.CompactToolMessages(messages);

        Assert.Equal("{\"blocked\":1}", messages[0].Content);
    }
}
