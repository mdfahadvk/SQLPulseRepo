using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Agent;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// The investigation audit trail: what SQLPulse looked at, what it concluded, and the
/// standing record that it changed nothing.
/// </summary>
/// <remarks>
/// The audit is written after the answer already exists, so its guiding rule is that it
/// must never be able to cost the caller that answer. It is also long-lived storage for
/// text that came from a user and from a model, so it is sanitized on the way in.
/// </remarks>
public sealed class InvestigationAuditServiceTests
{
    private const string Server = "sql01";

    private static InvestigationAuditService Build(InMemoryHistoryRepository repository) => new(
        repository,
        Options.Create(new SqlServerOptions
        {
            Server = Server,
            Username = "sqlpulse",
            Password = "irrelevant-for-tests",
            DefaultDatabase = "master",
        }),
        NullLogger<InvestigationAuditService>.Instance);

    private static InvestigationResponse Response(string question = "Why was AppDb slow?") => new()
    {
        InvestigationId = Guid.NewGuid().ToString(),
        Question = question,
        Answer = "Severity: high\nAffected Database: AppDb",
        Severity = "high",
        Confidence = "medium",
        AffectedDatabase = "AppDb",
        HumanReviewRequired = true,
    };

    // --- Persistence must never cost the caller an answer ---------------------

    [Fact]
    public async Task An_investigation_is_recorded()
    {
        var repository = new InMemoryHistoryRepository();
        var response = Response();

        await Build(repository).PersistAsync(response);

        var stored = await repository.ListInvestigationAuditsAsync(10);
        var record = Assert.Single(stored);

        Assert.Equal(response.InvestigationId, record.InvestigationId);
        Assert.Equal(Server, record.Server);
        Assert.Equal("AppDb", record.Database);
        Assert.Equal("high", record.Severity);
        Assert.True(record.HumanReviewRequired);
    }

    [Fact]
    public async Task A_storage_failure_does_not_fail_the_investigation()
    {
        // The answer is already computed by the time the audit is written. Losing the
        // trail is a degraded audit; throwing here would lose the work as well.
        var repository = new InMemoryHistoryRepository { ThrowOnAccess = true };

        var persist = Build(repository).PersistAsync(Response());

        await persist;
        Assert.True(persist.IsCompletedSuccessfully);
    }

    // --- What gets stored -----------------------------------------------------

    [Fact]
    public void The_record_states_that_no_remediation_occurred()
    {
        // Structurally true rather than merely defaulted: no approved tool can modify a
        // database, so the audit can assert this unconditionally.
        var record = InvestigationAuditService.BuildRecord(Response(), Server);

        Assert.False(record.RemediationPerformed);
    }

    [Fact]
    public void Credentials_pasted_into_a_question_are_not_stored()
    {
        var response = Response("Why is Password=SuperSecret123 in the connection string slow?");

        var record = InvestigationAuditService.BuildRecord(response, Server);

        Assert.DoesNotContain("SuperSecret123", record.QuestionSanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_echoed_back_by_the_model_are_not_stored()
    {
        var response = Response();
        response.Answer = "The connection uses Password=SuperSecret123 which is slow to negotiate.";

        var record = InvestigationAuditService.BuildRecord(response, Server);

        Assert.DoesNotContain("SuperSecret123", record.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_tool_arguments_are_dropped_in_favour_of_the_sanitized_ones()
    {
        // The raw arguments exist for the live response. The durable record keeps only
        // what has already been scrubbed.
        var response = Response();
        response.ToolCallsDetail =
        [
            new ToolCall
            {
                ToolName = AgentToolNames.GetBlocking,
                Arguments = new Dictionary<string, object?> { ["password"] = "SuperSecret123" },
                ArgumentsSanitized = new Dictionary<string, object?> { ["password"] = "***REDACTED***" },
                Success = true,
                DurationSeconds = 0.25,
            },
        ];

        var record = InvestigationAuditService.BuildRecord(response, Server);

        var call = Assert.Single(record.ToolsCalled);
        Assert.Equal(AgentToolNames.GetBlocking, call.ToolName);
        Assert.Equal("***REDACTED***", call.ArgumentsSanitized["password"]);
        Assert.True(call.Success);
        Assert.Equal(0.25, call.DurationSeconds);
    }

    [Fact]
    public void A_refused_tool_is_recorded_with_its_reason()
    {
        var response = Response();
        response.ToolCallsDetail =
        [
            new ToolCall
            {
                ToolName = "apply_index",
                Success = false,
                Error = AgentToolInvoker.NotApprovedMessage,
            },
        ];

        var record = InvestigationAuditService.BuildRecord(response, Server);

        var call = Assert.Single(record.ToolsCalled);
        Assert.False(call.Success);
        Assert.Equal(AgentToolInvoker.NotApprovedMessage, call.Error);
        Assert.False(record.RemediationPerformed);
    }

    // --- Making the trail searchable -----------------------------------------

    [Fact]
    public void A_time_range_in_the_question_is_recorded()
    {
        var response = Response("What happened between 2025-03-14 09:00 and 2025-03-14 10:30:00?");

        var record = InvestigationAuditService.BuildRecord(response, Server);

        Assert.Equal("2025-03-14 09:00", record.TimeRangeStart);
        Assert.Equal("2025-03-14 10:30:00", record.TimeRangeEnd);
    }

    [Fact]
    public void A_single_timestamp_records_only_a_start()
    {
        var response = Response("What was running at 2025-03-14T09:26?");

        var record = InvestigationAuditService.BuildRecord(response, Server);

        Assert.Equal("2025-03-14T09:26", record.TimeRangeStart);
        Assert.Null(record.TimeRangeEnd);
    }

    [Fact]
    public void A_question_without_timestamps_records_no_range()
    {
        var record = InvestigationAuditService.BuildRecord(Response("Is anything slow right now?"), Server);

        Assert.Null(record.TimeRangeStart);
        Assert.Null(record.TimeRangeEnd);
    }

    [Fact]
    public void Referenced_queries_are_indexed_from_the_answer()
    {
        // These make "has SQLPulse looked at this query before?" answerable without
        // re-reading every stored answer.
        var response = Response();
        response.AffectedQuery = "usp_GetOrders";
        response.Evidence =
        [
            "Query 0xAB12CD34 averaged 4200 ms",
            "Query 0xAB12CD34 also appears in the top I/O list",
            "Query 0xFF0099EE averaged 900 ms",
        ];

        var record = InvestigationAuditService.BuildRecord(response, Server);

        Assert.Equal(new[] { "usp_GetOrders", "0xAB12CD34", "0xFF0099EE" }, record.FindingReferences);
    }

    [Fact]
    public void Evidence_without_a_query_hash_yields_no_references()
    {
        var response = Response();
        response.Evidence = ["CPU was saturated across the instance"];

        var record = InvestigationAuditService.BuildRecord(response, Server);

        Assert.Empty(record.FindingReferences);
    }

    // --- Reads ----------------------------------------------------------------

    [Fact]
    public async Task Records_are_listed_newest_first_and_bounded_by_the_limit()
    {
        var repository = new InMemoryHistoryRepository();
        var service = Build(repository);

        for (var i = 0; i < 3; i++)
        {
            var response = Response($"Question {i}");
            response.Timestamp = DateTimeOffset.UtcNow.AddMinutes(-i);
            await service.PersistAsync(response);
        }

        var listed = await service.ListAsync(2);

        Assert.Equal(2, listed.Count);
        Assert.True(listed[0].CreatedAt >= listed[1].CreatedAt);
    }

    [Fact]
    public async Task A_single_record_can_be_retrieved_by_id()
    {
        var repository = new InMemoryHistoryRepository();
        var service = Build(repository);
        var response = Response();

        await service.PersistAsync(response);

        var found = await service.GetAsync(response.InvestigationId);

        Assert.NotNull(found);
        Assert.Equal(response.InvestigationId, found.InvestigationId);
    }

    [Fact]
    public async Task An_unknown_id_returns_nothing_rather_than_failing()
    {
        var service = Build(new InMemoryHistoryRepository());

        Assert.Null(await service.GetAsync("does-not-exist"));
    }

    [Fact]
    public async Task A_read_failure_is_surfaced_rather_than_reported_as_an_empty_trail()
    {
        // "No investigations" and "cannot read investigations" are different answers.
        var service = Build(new InMemoryHistoryRepository { ThrowOnAccess = true });

        await Assert.ThrowsAnyAsync<Exception>(() => service.ListAsync(10));
    }
}
