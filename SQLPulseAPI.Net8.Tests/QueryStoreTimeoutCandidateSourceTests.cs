using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.SqlErrors;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// The Query Store source, which is the only error source SQLPulse ships with.
/// </summary>
/// <remarks>
/// It derives timeout candidates from performance evidence, which makes it useful and
/// dangerous in equal measure. These tests pin the boundary: it may say a query ran long
/// enough that a client on the configured timeout would have given up, and it may never
/// say a client actually did.
/// </remarks>
public class QueryStoreTimeoutCandidateSourceTests
{
    private const string Database = "Merchandising";

    private static readonly DateTimeOffset WindowStart = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public StubSlowHistoryService SlowHistory { get; } = new();

        public AnalysisOptions Analysis { get; } = new() { ApplicationTimeoutMs = 30_000 };

        public SqlErrorOptions SqlErrors { get; } = new();

        public QueryStoreTimeoutCandidateSource Build() => new(
            SlowHistory,
            Options.Create(Analysis),
            Options.Create(SqlErrors));
    }

    private static SqlErrorQuery Query(string? database = Database) => new(
        Server: "sql01",
        Database: database,
        StartTime: WindowStart,
        EndTime: WindowEnd);

    private static TimeoutCandidate Candidate(
        bool isTimeoutCandidate,
        double maxDurationMs,
        string? procedure = "dbo.usp_GetMerchandiseCatalog") =>
        new()
        {
            IsTimeoutCandidate = isTimeoutCandidate,
            MaxDurationMs = maxDurationMs,
            AvgDurationMs = maxDurationMs * 0.7,
            ProcedureName = procedure,
            QueryHash = "0xABCDEF0123456789",
            LastExecutionTime = new DateTimeOffset(2026, 9, 17, 10, 42, 16, TimeSpan.Zero),
        };

    // ─── Identity ───────────────────────────────────────────────────────────

    [Fact]
    public void The_source_declares_itself_as_derived_evidence_not_telemetry()
    {
        var source = new Harness().Build();

        Assert.Equal(QueryStoreTimeoutCandidateSource.SourceName, source.Name);
        Assert.Equal(SqlErrorSourceKind.DerivedPerformanceEvidence, source.Kind);
    }

    // ─── Refusing to guess ──────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_database_the_source_says_it_does_not_apply()
    {
        var harness = new Harness();

        var result = await harness.Build().QueryAsync(Query(database: null));

        Assert.Equal(SqlErrorSourceStatus.NotApplicable, result.Status);
        Assert.Equal(QueryStoreTimeoutCandidateSource.DatabaseRequiredMessage, result.Message);
        Assert.Empty(result.Events);
        Assert.Null(harness.SlowHistory.LastCall);
    }

    [Fact]
    public async Task Without_a_configured_application_timeout_the_source_refuses_rather_than_guessing()
    {
        var harness = new Harness();
        harness.Analysis.ApplicationTimeoutMs = null;
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate(isTimeoutCandidate: true, maxDurationMs: 61_000)],
        };

        var result = await harness.Build().QueryAsync(Query());

        Assert.Equal(SqlErrorSourceStatus.NotApplicable, result.Status);
        Assert.Equal(QueryStoreTimeoutCandidateSource.ApplicationTimeoutRequiredMessage, result.Message);
        Assert.Empty(result.Events);
        Assert.Null(harness.SlowHistory.LastCall);
    }

    [Fact]
    public async Task The_monitoring_threshold_is_never_used_as_a_substitute_for_the_timeout()
    {
        var harness = new Harness();
        harness.Analysis.ApplicationTimeoutMs = null;
        harness.Analysis.SlowQueryThresholdMs = 1_000;

        var result = await harness.Build().QueryAsync(Query());

        Assert.Equal(SqlErrorSourceStatus.NotApplicable, result.Status);
    }

    [Fact]
    public async Task Disabling_the_source_reports_it_as_not_configured()
    {
        var harness = new Harness();
        harness.SqlErrors.EnableQueryStoreTimeoutCandidates = false;

        var result = await harness.Build().QueryAsync(Query());

        Assert.Equal(SqlErrorSourceStatus.NotConfigured, result.Status);
        Assert.Equal(QueryStoreTimeoutCandidateSource.DisabledMessage, result.Message);
    }

    // ─── Unreadable Query Store ─────────────────────────────────────────────

    [Theory]
    [InlineData(QueryStoreStatus.Disabled)]
    [InlineData(QueryStoreStatus.PermissionDenied)]
    [InlineData(QueryStoreStatus.QueryFailed)]
    public async Task An_unreadable_query_store_is_a_failure_not_an_empty_result(QueryStoreStatus status)
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            QueryStoreStatus = status,
        };

        var result = await harness.Build().QueryAsync(Query());

        Assert.Equal(SqlErrorSourceStatus.Failed, result.Status);
        Assert.Empty(result.Events);
        Assert.Contains(Database, result.Message);
    }

    [Fact]
    public async Task A_read_only_query_store_is_still_readable()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            QueryStoreStatus = QueryStoreStatus.AvailableReadOnly,
            Candidates = [Candidate(isTimeoutCandidate: true, maxDurationMs: 61_000)],
        };

        var result = await harness.Build().QueryAsync(Query());

        Assert.Equal(SqlErrorSourceStatus.Available, result.Status);
        Assert.Single(result.Events);
        Assert.Empty(Assert.Single(result.Events).Parameters);
    }

    // ─── What is and is not an error ────────────────────────────────────────

    [Fact]
    public async Task Only_queries_that_reached_the_application_timeout_are_surfaced()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates =
            [
                Candidate(isTimeoutCandidate: true, maxDurationMs: 61_000, procedure: "dbo.usp_TimedOut"),
                Candidate(isTimeoutCandidate: false, maxDurationMs: 8_400, procedure: "dbo.usp_MerelySlow"),
            ],
        };

        var result = await harness.Build().QueryAsync(Query());

        var surfaced = Assert.Single(result.Events);
        Assert.Equal("dbo.usp_TimedOut", surfaced.StoredProcedure);
    }

    [Fact]
    public async Task A_derived_event_never_claims_an_application_reported_a_timeout()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate(isTimeoutCandidate: true, maxDurationMs: 61_000)],
        };

        var result = await harness.Build().QueryAsync(Query());

        var derived = Assert.Single(result.Events);
        Assert.False(derived.ApplicationTimeoutReported);
        Assert.Contains("unconfirmed timeout candidate", derived.Message);
    }

    [Fact]
    public async Task A_derived_event_carries_the_evidence_needed_to_classify_it()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate(isTimeoutCandidate: true, maxDurationMs: 61_000)],
        };

        var result = await harness.Build().QueryAsync(Query());

        var derived = Assert.Single(result.Events);
        Assert.Equal("sql01", derived.Server);
        Assert.Equal(Database, derived.Database);
        Assert.Equal("0xABCDEF0123456789", derived.QueryHash);
        Assert.Equal(61_000, derived.DurationMs);
        Assert.Equal(30, derived.CommandTimeoutSeconds);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 10, 42, 16, TimeSpan.Zero), derived.Timestamp);
    }

    [Fact]
    public async Task A_candidate_without_an_execution_time_is_dated_at_the_end_of_the_window()
    {
        var harness = new Harness();
        var candidate = Candidate(isTimeoutCandidate: true, maxDurationMs: 61_000);
        candidate.LastExecutionTime = null;
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [candidate],
        };

        var result = await harness.Build().QueryAsync(Query());

        Assert.Equal(WindowEnd, Assert.Single(result.Events).Timestamp);
    }

    [Fact]
    public async Task A_quiet_window_is_reported_as_available_with_nothing_in_it()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse { Database = Database };

        var result = await harness.Build().QueryAsync(Query());

        Assert.Equal(SqlErrorSourceStatus.Available, result.Status);
        Assert.Empty(result.Events);
    }

    // ─── Delegation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_window_and_the_configured_timeout_are_passed_to_query_store()
    {
        var harness = new Harness();

        await harness.Build().QueryAsync(Query());

        Assert.NotNull(harness.SlowHistory.LastCall);
        var call = harness.SlowHistory.LastCall!.Value;
        Assert.Equal(Database, call.Database);
        Assert.Equal(WindowStart, call.Start);
        Assert.Equal(WindowEnd, call.End);
        Assert.Equal(30_000, call.ApplicationTimeoutMs);
        Assert.Equal(harness.Analysis.SlowQueryThresholdMs, call.ThresholdMs);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(5_000, 100)]
    public async Task The_requested_page_size_is_clamped_to_a_sane_range(int requested, int expected)
    {
        var harness = new Harness();

        await harness.Build().QueryAsync(Query() with { TopN = requested });

        Assert.Equal(expected, harness.SlowHistory.LastCall!.Value.TopN);
    }
}
