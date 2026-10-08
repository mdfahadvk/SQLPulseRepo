using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SQLPulseAPI.Net8.Controllers;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Models.Responses;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Endpoint behaviour for <c>/api/agent/investigate</c> and the investigation audit trail.
/// </summary>
/// <remarks>
/// The provider is scripted and storage is in memory, so these tests exercise the contract
/// the React client is written against — including the one that matters most: a request to
/// change the database cannot change the database.
/// </remarks>
public sealed class AgentEndpointTests
{
    private const string BlockingAnswer =
        """
        Answer: Session 71 is blocking four sessions in Merchandising.

        Severity: high
        Confidence: medium
        Affected Database: Merchandising
        Likely Cause: an open transaction holding an exclusive lock

        Evidence:
        - Session 71 has been open for 210 seconds

        Recommended Investigation:
        - Identify the owner of session 71

        Human Review Required: Yes
        """;

    private static SqlPulseApiFactory Factory(ScriptedLlmClient llm, FakeSqlQueryExecutor? executor = null) =>
        new()
        {
            ConfigureTestServices = services =>
            {
                services.AddSingleton<ILlmClient>(llm);

                if (executor is not null)
                {
                    services.AddSingleton<ISqlQueryExecutor>(executor);
                }
            },
        };

    private static StringContent Ask(string question) => new(
        JsonSerializer.Serialize(new { question }, SqlPulseJson.Options),
        Encoding.UTF8,
        "application/json");

    private static async Task<T> ReadAs<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), SqlPulseJson.Options)!;

    private static async Task<string> DetailOf(HttpResponseMessage response) =>
        (await ReadAs<ErrorResponse>(response)).Detail;

    // --- Configuration --------------------------------------------------------

    [Fact]
    public async Task Investigating_without_a_configured_provider_says_so_plainly()
    {
        // The default host has no API key, which is the state most installations start in.
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/agent/investigate", Ask("Is anything slow?"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(AgentController.NotConfiguredMessage, await DetailOf(response));
    }

    [Fact]
    public async Task A_question_that_is_too_short_is_rejected_before_the_provider_is_called()
    {
        // Model validation runs first, so a malformed request never costs a provider call.
        var llm = new ScriptedLlmClient();
        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/agent/investigate", Ask("?"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, llm.CallCount);
    }

    // --- The response contract ------------------------------------------------

    [Fact]
    public async Task An_investigation_returns_the_full_contract()
    {
        var llm = new ScriptedLlmClient();
        llm.ThenAnswers(BlockingAnswer);

        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/agent/investigate",
            Ask("What is affecting Merchandising performance right now?"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        foreach (var field in new[]
                 {
                     "investigation_id", "question", "answer", "severity", "confidence",
                     "affected_database", "affected_query", "evidence", "historical_comparison",
                     "likely_cause", "what_is_known", "what_is_uncertain",
                     "recommended_investigation", "human_review_required", "agent_action",
                     "tools_used", "tool_call_count", "duration_seconds", "timestamp",
                 })
        {
            Assert.True(root.TryGetProperty(field, out _), $"missing field: {field}");
        }
    }

    [Fact]
    public async Task The_raw_tool_detail_is_never_returned_to_the_client()
    {
        // It can contain full SQL text. The audit trail keeps it; the API response does not.
        var llm = new ScriptedLlmClient();
        llm.ThenCalls((AgentToolNames.GetBlocking, "{}")).ThenAnswers(BlockingAnswer);

        using var factory = Factory(llm, new FakeSqlQueryExecutor());
        using var client = factory.CreateClient();

        var json = await (await client.PostAsync("/api/agent/investigate", Ask("Is anything blocking?")))
            .Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty("tool_calls_detail", out _));
    }

    [Fact]
    public async Task The_structured_fields_agree_with_the_written_answer()
    {
        var llm = new ScriptedLlmClient();
        llm.ThenAnswers(BlockingAnswer);

        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        var body = await ReadAs<InvestigationResponse>(
            await client.PostAsync("/api/agent/investigate", Ask("What is blocking Merchandising?")));

        Assert.Contains("Severity: high", body.Answer, StringComparison.Ordinal);
        Assert.Equal("high", body.Severity);
        Assert.Equal("Merchandising", body.AffectedDatabase);
        Assert.NotEmpty(body.Evidence);
        Assert.NotEmpty(body.RecommendedInvestigation);
        Assert.True(body.HumanReviewRequired);
    }

    // --- Provider failures ----------------------------------------------------

    [Theory]
    [InlineData(nameof(LlmPayloadTooLargeException), HttpStatusCode.TooManyRequests)]
    [InlineData(nameof(LlmRateLimitException), HttpStatusCode.TooManyRequests)]
    [InlineData(nameof(LlmTimeoutException), HttpStatusCode.GatewayTimeout)]
    [InlineData(nameof(LlmUnavailableException), HttpStatusCode.ServiceUnavailable)]
    public async Task A_provider_failure_is_reported_as_an_actionable_status(
        string exceptionName,
        HttpStatusCode expected)
    {
        // The client shows the detail text verbatim, so each failure has to say what the
        // operator can do about it: wait, narrow the question, or try again later.
        Exception failure = exceptionName switch
        {
            nameof(LlmPayloadTooLargeException) => new LlmPayloadTooLargeException("too large"),
            nameof(LlmRateLimitException) => new LlmRateLimitException("slow down", TimeSpan.FromMilliseconds(1)),
            nameof(LlmTimeoutException) => new LlmTimeoutException("no response"),
            _ => new LlmUnavailableException("unreachable"),
        };

        var llm = new ScriptedLlmClient();
        llm.ThenThrows(failure);

        // The engine retries once for the two failures it can recover from: it compacts
        // an oversized conversation, and waits out a full rate-limit window. The endpoint
        // therefore only sees those failures when the second attempt fails as well.
        if (failure is LlmPayloadTooLargeException)
        {
            llm.ThenThrows(new LlmPayloadTooLargeException("still too large"));
        }
        else if (failure is LlmRateLimitException)
        {
            llm.ThenThrows(new LlmRateLimitException("still limited", TimeSpan.FromMilliseconds(1)));
        }

        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/agent/investigate", Ask("Is anything slow?"));

        Assert.Equal(expected, response.StatusCode);
        Assert.NotEmpty(await DetailOf(response));
    }

    [Fact]
    public async Task An_unexpected_failure_does_not_leak_internal_detail()
    {
        var llm = new ScriptedLlmClient();
        llm.ThenThrows(new InvalidOperationException("connection string sql01;Password=hunter2"));

        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/agent/investigate", Ask("Is anything slow?"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(AgentController.InvestigationFailedMessage, await DetailOf(response));
    }

    // --- Audit trail ----------------------------------------------------------

    [Fact]
    public async Task An_investigation_is_written_to_the_audit_trail()
    {
        var llm = new ScriptedLlmClient();
        llm.ThenAnswers(BlockingAnswer);

        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        var investigation = await ReadAs<InvestigationResponse>(
            await client.PostAsync("/api/agent/investigate", Ask("What is blocking Merchandising?")));

        var listed = await ReadAs<InvestigationAuditListResponse>(
            await client.GetAsync("/api/agent/investigations"));

        Assert.Equal(1, listed.Total);
        Assert.Equal(investigation.InvestigationId, listed.Investigations[0].InvestigationId);
        Assert.False(listed.Investigations[0].RemediationPerformed);
    }

    [Fact]
    public async Task An_audit_failure_does_not_cost_the_caller_the_answer()
    {
        // The investigation is already finished by the time the audit is written.
        var llm = new ScriptedLlmClient();
        llm.ThenAnswers(BlockingAnswer);

        using var factory = Factory(llm);
        factory.HistoryRepository.ThrowOnAccess = true;
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/agent/investigate", Ask("Is anything slow?"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("high", (await ReadAs<InvestigationResponse>(response)).Severity);
    }

    [Fact]
    public async Task The_audit_listing_uses_the_python_property_names()
    {
        var llm = new ScriptedLlmClient();
        llm.ThenAnswers(BlockingAnswer);

        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        await client.PostAsync("/api/agent/investigate", Ask("What is blocking Merchandising?"));

        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/agent/investigations"));

        var record = document.RootElement.GetProperty("investigations")[0];

        Assert.True(record.TryGetProperty("investigation_id", out _));
        Assert.True(record.TryGetProperty("question_sanitized", out _));
        Assert.True(record.TryGetProperty("tools_called", out _));
        Assert.True(record.TryGetProperty("finding_references", out _));
        Assert.True(record.TryGetProperty("remediation_performed", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task The_audit_listing_rejects_an_out_of_range_limit(int limit)
    {
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/agent/investigations?limit={limit}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task The_audit_listing_reports_unavailable_storage()
    {
        // An empty trail and an unreadable trail are different answers.
        using var factory = new SqlPulseApiFactory();
        factory.HistoryRepository.ThrowOnAccess = true;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/agent/investigations");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(AgentController.AuditListFailedMessage, await DetailOf(response));
    }

    [Fact]
    public async Task A_single_investigation_can_be_retrieved_by_id()
    {
        var llm = new ScriptedLlmClient();
        llm.ThenAnswers(BlockingAnswer);

        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        var investigation = await ReadAs<InvestigationResponse>(
            await client.PostAsync("/api/agent/investigate", Ask("What is blocking Merchandising?")));

        var detail = await ReadAs<InvestigationAuditDetailResponse>(
            await client.GetAsync($"/api/agent/investigations/{investigation.InvestigationId}"));

        Assert.True(detail.Found);
        Assert.NotNull(detail.Investigation);
        Assert.Equal(investigation.InvestigationId, detail.Investigation.InvestigationId);
    }

    [Fact]
    public async Task An_unknown_investigation_id_reports_not_found_in_the_body()
    {
        // Deliberately HTTP 200 with found=false, matching the contract the UI reads.
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/agent/investigations/does-not-exist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await ReadAs<InvestigationAuditDetailResponse>(response);
        Assert.False(detail.Found);
        Assert.Null(detail.Investigation);
    }

    [Fact]
    public async Task A_single_audit_read_failure_is_reported()
    {
        using var factory = new SqlPulseApiFactory();
        factory.HistoryRepository.ThrowOnAccess = true;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/agent/investigations/abc");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(AgentController.AuditReadFailedMessage, await DetailOf(response));
    }

    // --- Discovery ------------------------------------------------------------

    [Fact]
    public async Task The_root_document_advertises_the_agent_endpoints()
    {
        using var factory = new SqlPulseApiFactory();
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/");
        using var document = JsonDocument.Parse(json);
        var endpoints = document.RootElement.GetProperty("endpoints");

        Assert.True(endpoints.TryGetProperty("agent_investigate", out var investigate), json);
        Assert.Equal("/api/agent/investigate", investigate.GetString());
        Assert.Equal("/api/agent/investigations", endpoints.GetProperty("agent_investigations").GetString());
        Assert.True(endpoints.TryGetProperty("agent_investigation_detail", out _));
    }

    // --- Safety ---------------------------------------------------------------

    [Fact]
    public async Task Asking_the_agent_to_create_and_apply_an_index_changes_nothing()
    {
        // The central safety property. Even with a model that tries every mutating name it
        // can think of, there is no tool that could carry the request out: the agent's
        // whole surface is a read-only allow-list.
        var executor = new FakeSqlQueryExecutor();
        var llm = new ScriptedLlmClient();

        llm
            .ThenCalls(
                ("create_index", """{"database":"Merchandising","table":"Orders"}"""),
                ("apply_index", """{"database":"Merchandising"}"""),
                ("run_sql", """{"sql":"CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId)"}"""),
                ("execute_sql", """{"sql":"DROP INDEX IX_Old ON dbo.Orders"}"""))
            .ThenAnswers(
                """
                Answer: I cannot create or apply indexes. Below is a recommendation for review.

                Severity: medium
                Confidence: medium
                Affected Database: Merchandising

                Recommended Investigation:
                - Have a DBA review a nonclustered index on dbo.Orders(CustomerId)

                Human Review Required: Yes
                """);

        using var factory = Factory(llm, executor);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/agent/investigate", Ask("Create and apply the best index for Merchandising."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAs<InvestigationResponse>(response);

        // Nothing ran against SQL Server at all.
        Assert.Equal(0, executor.ExecuteCount);

        // Nothing the model asked for was approved, so nothing counts as consulted.
        Assert.Empty(body.ToolsUsed);
        Assert.Equal(4, body.ToolCallCount);
        Assert.Equal(InvestigationResponse.NoRemediationPerformed, body.AgentAction);
        Assert.True(body.HumanReviewRequired);

        // The refusals are recorded, and the record states that nothing changed.
        var audit = await ReadAs<InvestigationAuditDetailResponse>(
            await client.GetAsync($"/api/agent/investigations/{body.InvestigationId}"));

        Assert.True(audit.Found);
        Assert.False(audit.Investigation!.RemediationPerformed);
        Assert.Equal(4, audit.Investigation.ToolsCalled.Count);
        Assert.All(audit.Investigation.ToolsCalled, call =>
        {
            Assert.False(call.Success);
            Assert.Equal(AgentToolInvoker.NotApprovedMessage, call.Error);
        });
    }

    [Fact]
    public async Task No_tool_offered_to_the_model_can_modify_anything()
    {
        // The allow-list is the guarantee; this pins it so a future tool cannot be added
        // with a name that implies mutation without this test failing first.
        var llm = new ScriptedLlmClient();
        llm.ThenAnswers(BlockingAnswer);

        using var factory = Factory(llm);
        using var client = factory.CreateClient();

        await client.PostAsync("/api/agent/investigate", Ask("Is anything slow right now?"));

        var offered = llm.Requests[0].Tools!;

        Assert.All(offered, tool => Assert.True(
            tool.Name.StartsWith("get_", StringComparison.Ordinal)
            || tool.Name.StartsWith("investigate_", StringComparison.Ordinal),
            $"'{tool.Name}' does not begin with a read-only verb."));
    }
}
