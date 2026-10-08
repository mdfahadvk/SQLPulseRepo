using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.SqlErrors;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Correlating a SQL error with the performance evidence around it.
/// </summary>
/// <remarks>
/// The point of these tests is the line the service must not cross. Evidence observed in
/// the same five minutes is correlation; the service may grade how strong that correlation
/// is, but nothing it produces may read as a demonstrated cause.
/// </remarks>
public class SqlErrorCorrelationServiceTests
{
    private const string Database = "Merchandising";
    private const string QueryHash = "0xABCDEF0123456789";
    private const string Procedure = "dbo.usp_GetMerchandiseCatalog";

    /// <summary>Words that would turn correlated evidence into a causal claim.</summary>
    private static readonly string[] CausalPhrases = ["caused by", "because of", "due to", "resulted from"];

    private sealed class Harness
    {
        public StubSlowHistoryService SlowHistory { get; } = new();

        public StubBaselineService Baselines { get; } = new();

        public StubPerformanceSnapshotService Snapshots { get; } = new();

        public SqlErrorOptions SqlErrors { get; } = new();

        public SqlErrorCorrelationService Build() => new(
            SlowHistory,
            Baselines,
            Snapshots,
            Options.Create(SqlErrors),
            Options.Create(new AnalysisOptions { ApplicationTimeoutMs = 30_000 }),
            NullLogger<SqlErrorCorrelationService>.Instance);
    }

    private static SqlErrorEvent ErrorEvent(
        DateTimeOffset? timestamp = null,
        string? database = Database,
        string? queryHash = QueryHash,
        string? storedProcedure = Procedure,
        double? durationMs = 42_800) =>
        new()
        {
            Id = "err_test",
            Timestamp = timestamp ?? new DateTimeOffset(2026, 9, 17, 10, 42, 16, TimeSpan.Zero),
            Server = "sql01",
            Database = database,
            QueryHash = queryHash,
            StoredProcedure = storedProcedure,
            DurationMs = durationMs,
            ErrorCategory = SqlErrorCategory.Timeout,
            TimeoutClassification = TimeoutClassification.ConfirmedApplicationTimeout,
            Message = "Execution Timeout Expired.",
        };

    private static TimeoutCandidate Candidate(
        string? queryHash = QueryHash,
        string? procedure = Procedure,
        double maxDurationMs = 42_800,
        BaselineComparison? baseline = null) =>
        new()
        {
            QueryHash = queryHash,
            ProcedureName = procedure,
            MaxDurationMs = maxDurationMs,
            AvgDurationMs = maxDurationMs * 0.8,
            AvgCpuMs = 1_200,
            AvgLogicalReads = 2_800_000,
            ExecutionCount = 4,
            BaselineComparison = baseline,
        };

    private static BaselineComparison ReadyBaseline(double p95Ms, double meanMs) => new()
    {
        BaselineStatus = BaselineStatus.Ready,
        SampleCount = 40,
        HistoricalMeanMs = meanMs,
        HistoricalP95Ms = p95Ms,
    };

    private static PerformanceSnapshot SnapshotWithBlocking(long longestWaitMs) => new()
    {
        SnapshotId = "snap-1",
        ServerHost = "sql01",
        BlockingSessions = new BlockingSessionsResult
        {
            Metadata = new CollectorMetadata { CollectorName = "blocking_sessions", Success = true },
            BlockingChainCount = 1,
            Sessions =
            [
                new() { SessionId = 51, IsHeadBlocker = true },
                new() { SessionId = 74, BlockingSessionId = 51, WaitTimeMs = longestWaitMs },
            ],
        },
    };

    // ─── The window ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Evidence_is_gathered_from_the_configured_window_either_side_of_the_error()
    {
        var harness = new Harness();
        harness.SqlErrors.CorrelationWindowMinutes = 5;
        var errorEvent = ErrorEvent();

        var correlation = await harness.Build().CorrelateAsync(errorEvent);

        Assert.Equal(errorEvent.Timestamp.AddMinutes(-5), correlation.WindowStart);
        Assert.Equal(errorEvent.Timestamp.AddMinutes(5), correlation.WindowEnd);
        Assert.Equal(5, correlation.WindowMinutes);

        Assert.NotNull(harness.SlowHistory.LastCall);
        var call = harness.SlowHistory.LastCall!.Value;
        Assert.Equal(errorEvent.Timestamp.AddMinutes(-5), call.Start);
        Assert.Equal(errorEvent.Timestamp.AddMinutes(5), call.End);
    }

    [Fact]
    public async Task A_wider_window_is_honoured()
    {
        var harness = new Harness();
        harness.SqlErrors.CorrelationWindowMinutes = 30;

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Equal(30, correlation.WindowMinutes);
        Assert.Equal(TimeSpan.FromHours(1), correlation.WindowEnd - correlation.WindowStart);
    }

    // ─── Confidence grading ─────────────────────────────────────────────────

    [Fact]
    public async Task No_evidence_in_the_window_is_graded_as_no_correlation()
    {
        var correlation = await new Harness().Build().CorrelateAsync(ErrorEvent());

        Assert.Equal(CorrelationConfidence.None, correlation.CorrelationConfidence);
        Assert.Empty(correlation.CorrelatedEvidence);
        Assert.Contains(SqlErrorCorrelationService.NoEvidenceCaveat, correlation.Caveats);
    }

    [Fact]
    public async Task Activity_in_the_same_window_but_a_different_query_is_only_low_confidence()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate(queryHash: "0xSOMETHINGELSE", procedure: "dbo.usp_Unrelated")],
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Equal(CorrelationConfidence.Low, correlation.CorrelationConfidence);
        Assert.False(correlation.IdentityMatched);
        Assert.Contains("not evidence of a relationship", correlation.Assessment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_matching_query_hash_without_corroboration_is_medium_confidence()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate()],
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Equal(CorrelationConfidence.Medium, correlation.CorrelationConfidence);
        Assert.True(correlation.IdentityMatched);
        Assert.Contains(correlation.CorrelatedEvidence, item => item.MatchedOn.Contains("query_hash"));
    }

    [Fact]
    public async Task A_matching_query_with_a_duration_regression_is_high_confidence()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate(baseline: ReadyBaseline(p95Ms: 6_900, meanMs: 4_100))],
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Equal(CorrelationConfidence.High, correlation.CorrelationConfidence);
        Assert.Contains(
            correlation.CorrelatedEvidence,
            item => item.Kind == CorrelatedEvidenceKind.QueryBaseline);
    }

    [Fact]
    public async Task A_duration_in_line_with_the_baseline_is_not_called_a_regression()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates =
            [
                Candidate(maxDurationMs: 7_000, baseline: ReadyBaseline(p95Ms: 6_900, meanMs: 6_000)),
            ],
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Equal(CorrelationConfidence.Medium, correlation.CorrelationConfidence);
        Assert.DoesNotContain("regression", correlation.Assessment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_matching_stored_procedure_counts_as_an_identity_match()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate(queryHash: null)],
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent(queryHash: null));

        Assert.True(correlation.IdentityMatched);
        Assert.Contains(correlation.CorrelatedEvidence, item => item.MatchedOn.Contains("stored_procedure"));
    }

    // ─── Correlation is not causation ───────────────────────────────────────

    [Fact]
    public async Task Every_correlation_carries_the_causation_caveat()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate(baseline: ReadyBaseline(p95Ms: 6_900, meanMs: 4_100))],
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Contains(SqlErrorCorrelation.CausationCaveat, correlation.Caveats);
    }

    [Theory]
    [InlineData(CorrelationConfidence.None)]
    [InlineData(CorrelationConfidence.Low)]
    [InlineData(CorrelationConfidence.Medium)]
    [InlineData(CorrelationConfidence.High)]
    public async Task The_assessment_never_asserts_a_cause_at_any_confidence(CorrelationConfidence expected)
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = expected switch
            {
                CorrelationConfidence.None => [],
                CorrelationConfidence.Low => [Candidate(queryHash: "0xOTHER", procedure: "dbo.usp_Other")],
                CorrelationConfidence.Medium => [Candidate()],
                _ => [Candidate(baseline: ReadyBaseline(p95Ms: 6_900, meanMs: 4_100))],
            },
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Equal(expected, correlation.CorrelationConfidence);

        foreach (var phrase in CausalPhrases)
        {
            Assert.DoesNotContain(phrase, correlation.Assessment, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_strongest_assessment_still_only_names_possible_contributors()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate(baseline: ReadyBaseline(p95Ms: 6_900, meanMs: 4_100))],
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Contains("coincided with", correlation.Assessment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("possible contributor", correlation.Assessment, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Live versus historical evidence ────────────────────────────────────

    [Fact]
    public async Task A_historical_incident_says_why_blocking_evidence_is_missing()
    {
        var harness = new Harness();

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent(
            timestamp: DateTimeOffset.UtcNow.AddDays(-2)));

        Assert.Contains(SqlErrorCorrelationService.HistoricalIncidentCaveat, correlation.Caveats);
        Assert.Empty(harness.Snapshots.Requests);
    }

    [Fact]
    public async Task A_live_incident_picks_up_concurrent_blocking()
    {
        var harness = new Harness();
        harness.Snapshots.Snapshot = SnapshotWithBlocking(31_000);
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            Candidates = [Candidate()],
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent(timestamp: DateTimeOffset.UtcNow));

        Assert.Contains(correlation.CorrelatedEvidence, item => item.Kind == CorrelatedEvidenceKind.Blocking);
        Assert.Equal(CorrelationConfidence.High, correlation.CorrelationConfidence);
        Assert.Contains("concurrent blocking", correlation.Assessment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failing_live_collection_degrades_the_correlation_rather_than_the_request()
    {
        var harness = new Harness();
        harness.Snapshots.Throws = new InvalidOperationException("collector unavailable");

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent(timestamp: DateTimeOffset.UtcNow));

        Assert.Equal(CorrelationConfidence.None, correlation.CorrelationConfidence);
        Assert.Contains(
            correlation.Caveats,
            caveat => caveat.Contains("could not be collected", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Degraded evidence ──────────────────────────────────────────────────

    [Fact]
    public async Task An_unreadable_query_store_is_reported_rather_than_treated_as_quiet()
    {
        var harness = new Harness();
        harness.SlowHistory.Response = new SlowHistoryResponse
        {
            Database = Database,
            QueryStoreStatus = QueryStoreStatus.Disabled,
        };

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent());

        Assert.Contains(
            correlation.Caveats,
            caveat => caveat.Contains("Query Store is not readable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_error_without_a_database_cannot_be_correlated_and_says_so()
    {
        var harness = new Harness();

        var correlation = await harness.Build().CorrelateAsync(ErrorEvent(database: null));

        Assert.Equal(CorrelationConfidence.None, correlation.CorrelationConfidence);
        Assert.Contains(SqlErrorCorrelationService.NoDatabaseCaveat, correlation.Caveats);
        Assert.Null(harness.SlowHistory.LastCall);
    }
}
