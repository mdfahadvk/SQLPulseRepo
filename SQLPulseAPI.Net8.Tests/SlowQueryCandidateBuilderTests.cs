using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Slow-query candidate behaviour: the separation of monitoring threshold from
/// application timeout, workload classification that refuses to guess, interval
/// aggregation, and deterministic confidence, severity and ranking.
/// </summary>
public class SlowQueryCandidateBuilderTests
{
    private const string Database = "AppDb";
    private const double Threshold = 5000;

    private static readonly DateTimeOffset First = new(2026, 8, 24, 9, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Last = new(2026, 8, 24, 10, 55, 0, TimeSpan.Zero);

    private static SlowQueryCandidateBuilder Builder(
        StubBaselineService? baselineService = null,
        AnalysisOptions? analysis = null) =>
        new(
            baselineService ?? new StubBaselineService(),
            Options.Create(analysis ?? new AnalysisOptions()),
            NullLogger<SlowQueryCandidateBuilder>.Instance);

    private static SlowQueryRecord Record(
        double maxDurationMs = 9749,
        double avgDurationMs = 6100,
        double minDurationMs = 5200,
        long executionCount = 3,
        string? queryHash = "0xABCD",
        string? procedureName = null,
        string? sqlText = null,
        string? programName = null,
        string? hostName = null,
        bool? isScheduledJob = null,
        long? planId = 7,
        long? queryId = 42,
        long? objectId = 991,
        double maxCpuMs = 2400,
        double avgCpuMs = 1200) => new()
    {
        QueryId = queryId,
        QueryHash = queryHash,
        ObjectId = objectId,
        PlanId = planId,
        ProcedureName = procedureName,
        QuerySqlText = sqlText,
        ProgramName = programName,
        HostName = hostName,
        IsScheduledJob = isScheduledJob,
        ExecutionCount = executionCount,
        AvgDurationMs = avgDurationMs,
        MaxDurationMs = maxDurationMs,
        MinDurationMs = minDurationMs,
        AvgCpuTimeMs = avgCpuMs,
        MaxCpuTimeMs = maxCpuMs,
        AvgLogicalReads = 5000,
        MaxLogicalReads = 9000,
        AvgPhysicalReads = 10,
        FirstExecutionTime = First,
        LastExecutionTime = Last,
        IntervalStartTime = First,
    };

    private static QueryBaseline ReadyBaseline(
        double mean, double median, double p95, double standardDeviation, int sampleCount = 20) => new()
        {
            QueryHash = "0xABCD",
            Database = Database,
            Window = "7d",
            SampleCount = sampleCount,
            BaselineStatus = BaselineStatus.Ready,
            AvgElapsedMs = new BaselineStatistic
            {
                SampleCount = sampleCount,
                Mean = mean,
                Median = median,
                P95 = p95,
                StandardDeviation = standardDeviation,
            },
        };

    // --- Slow versus timeout -------------------------------------------------

    [Fact]
    public async Task Nine_seconds_against_a_thirty_second_timeout_is_slow_not_a_timeout()
    {
        // The heart of the corrected behaviour: 9,749 ms exceeds the 5,000 ms monitoring
        // threshold but is nowhere near the 30,000 ms the client actually waits, so
        // calling it a timeout candidate would be a claim the evidence does not support.
        var candidates = await Builder().BuildAsync(
            [Record(maxDurationMs: 9749)], Threshold, Database, applicationTimeoutMs: 30_000);

        var candidate = Assert.Single(candidates);

        Assert.Equal(CandidateClassification.SlowQueryCandidate, candidate.Classification);
        Assert.True(candidate.IsSlowQueryCandidate);
        Assert.False(candidate.IsTimeoutCandidate);
        Assert.Equal("slow_query_candidate", candidate.Label);
        Assert.Equal(
            "Exceeded the 5s slow-query monitoring threshold (9.75s observed) but stayed "
            + "below the configured 30s application timeout.",
            candidate.ClassificationReason);
    }

    [Fact]
    public async Task Reaching_the_configured_timeout_promotes_the_candidate()
    {
        var candidates = await Builder().BuildAsync(
            [Record(maxDurationMs: 30_000)], Threshold, Database, applicationTimeoutMs: 30_000);

        var candidate = Assert.Single(candidates);

        Assert.Equal(CandidateClassification.TimeoutCandidate, candidate.Classification);
        Assert.True(candidate.IsTimeoutCandidate);
        Assert.True(candidate.IsSlowQueryCandidate);
        Assert.Equal("timeout_candidate", candidate.Label);
        Assert.Equal(
            "Reached 30s, at or above the configured 30s application timeout.",
            candidate.ClassificationReason);
    }

    [Fact]
    public async Task Without_a_configured_timeout_classification_is_reported_unavailable()
    {
        // The monitoring threshold describes what an operator asked to see. Reusing it as
        // a timeout would invent a number the application never stated.
        var candidates = await Builder().BuildAsync(
            [Record(maxDurationMs: 120_000)], Threshold, Database, applicationTimeoutMs: null);

        var candidate = Assert.Single(candidates);

        Assert.Equal(CandidateClassification.SlowQueryCandidate, candidate.Classification);
        Assert.False(candidate.IsTimeoutCandidate);
        Assert.Null(candidate.ApplicationTimeoutMs);
        Assert.Equal(
            "Exceeded the 5s slow-query monitoring threshold (120s observed). No "
            + "application timeout is configured, so timeout classification is unavailable.",
            candidate.ClassificationReason);
    }

    [Fact]
    public async Task Records_below_the_threshold_are_not_candidates()
    {
        var candidates = await Builder().BuildAsync(
            [Record(maxDurationMs: 4999)], Threshold, Database);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Classification_is_a_pure_function_of_the_two_thresholds()
    {
        Assert.Equal(
            CandidateClassification.SlowQueryCandidate,
            SlowQueryCandidateBuilder.ClassifyCandidate(9749, 5000, 30_000).Classification);

        Assert.Equal(
            CandidateClassification.TimeoutCandidate,
            SlowQueryCandidateBuilder.ClassifyCandidate(30_001, 5000, 30_000).Classification);

        Assert.Equal(
            CandidateClassification.SlowQueryCandidate,
            SlowQueryCandidateBuilder.ClassifyCandidate(600_000, 5000, null).Classification);
    }

    // --- Workload classification --------------------------------------------

    [Fact]
    public async Task A_usp_prefix_alone_is_never_enough_to_claim_application_workload()
    {
        // "usp" is a naming convention a DBA also uses for maintenance helpers, so it is
        // recorded as weak evidence and the answer stays UNKNOWN.
        var candidates = await Builder().BuildAsync(
            [Record(procedureName: "dbo.uspGetOrders")], Threshold, Database);

        var candidate = Assert.Single(candidates);

        Assert.Equal(WorkloadType.Unknown, candidate.WorkloadType);
        Assert.All(candidate.WorkloadEvidence, e => Assert.Equal(EvidenceStrength.Weak, e.Strength));
        Assert.Contains(
            candidate.ClassificationEvidence,
            line => line.StartsWith("weak: [procedure_name]", StringComparison.Ordinal)
                && line.Contains("a convention, not proof of caller", StringComparison.Ordinal));
    }

    [Fact]
    public void No_evidence_at_all_resolves_to_unknown()
    {
        var (workloadType, evidence) = WorkloadClassifier.Classify(null, null);

        Assert.Equal(WorkloadType.Unknown, workloadType);
        Assert.Contains(
            evidence,
            line => line.Contains("No SQL text, procedure name, or session metadata available", StringComparison.Ordinal));
    }

    [Fact]
    public void A_maintenance_statement_outranks_an_application_connection()
    {
        // An application connection running BACKUP DATABASE is still doing maintenance:
        // what the statement does matters more than who called it.
        var (workloadType, _) = WorkloadClassifier.Classify(
            "BACKUP DATABASE AppDb TO DISK = 'x.bak'",
            procedureName: null,
            programName: ".Net SqlClient Data Provider");

        Assert.Equal(WorkloadType.BackgroundOrScheduled, workloadType);
    }

    [Fact]
    public void A_recognised_application_program_is_strong_evidence()
    {
        var (workloadType, evidence) = WorkloadClassifier.Classify(
            "SELECT 1 FROM Orders", procedureName: null, programName: "Microsoft.Data.SqlClient");

        Assert.Equal(WorkloadType.ApplicationOrInteractive, workloadType);
        Assert.Contains(evidence, line => line.StartsWith("strong: [program_name]", StringComparison.Ordinal));
    }

    [Fact]
    public void A_sql_agent_program_is_strong_background_evidence()
    {
        var (workloadType, _) = WorkloadClassifier.Classify(
            "SELECT 1 FROM Orders", procedureName: null, programName: "SQLAgent - TSQL JobStep (Job 0x01)");

        Assert.Equal(WorkloadType.BackgroundOrScheduled, workloadType);
    }

    [Fact]
    public void An_unrecognised_program_name_is_only_weak_evidence()
    {
        var (workloadType, evidence) = WorkloadClassifier.Classify(
            sqlText: null, procedureName: null, programName: "SomeInternalTool");

        Assert.Equal(WorkloadType.Unknown, workloadType);
        Assert.Contains(
            evidence,
            line => line.Contains("is not a recognised application or agent program", StringComparison.Ordinal));
    }

    [Fact]
    public void An_operator_configured_program_mapping_wins()
    {
        // Someone who knows the environment has stated what this program is, which beats
        // any pattern SQLPulse ships with.
        var (workloadType, evidence) = WorkloadClassifier.Classify(
            "SELECT 1 FROM Orders",
            procedureName: null,
            programName: "Microsoft.Data.SqlClient",
            applicationPrograms: [],
            backgroundPrograms: ["Microsoft.Data.SqlClient"]);

        Assert.Equal(WorkloadType.BackgroundOrScheduled, workloadType);
        Assert.Contains(evidence, line => line.Contains("configured background program", StringComparison.Ordinal));
    }

    [Fact]
    public void An_explicit_scheduled_job_flag_is_strong_evidence()
    {
        var (background, _) = WorkloadClassifier.Classify(null, null, isScheduledJob: true);
        var (application, _) = WorkloadClassifier.Classify(null, null, isScheduledJob: false);

        Assert.Equal(WorkloadType.BackgroundOrScheduled, background);
        Assert.Equal(WorkloadType.ApplicationOrInteractive, application);
    }

    [Fact]
    public void Only_one_generic_statement_shape_is_recorded()
    {
        // Listing five ways of saying "this is a normal query" adds no information.
        var evidence = WorkloadClassifier.GatherEvidence(
            "SELECT a FROM t; INSERT INTO t VALUES (1); UPDATE t SET a = 1; DELETE FROM t",
            procedureName: null);

        Assert.Single(
            evidence,
            e => e.Source == "sql_text"
                && e.Detail.StartsWith("Generic statement shape", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_workload_filter_only_returns_matching_candidates()
    {
        var records = new List<SlowQueryRecord>
        {
            Record(queryHash: "0xAPP", planId: 1, programName: "Microsoft.Data.SqlClient"),
            Record(queryHash: "0xJOB", planId: 2, programName: "SQLAgent - TSQL JobStep"),
        };

        var candidates = await Builder().BuildAsync(
            records, Threshold, Database, workloadTypeFilter: WorkloadType.BackgroundOrScheduled);

        var candidate = Assert.Single(candidates);
        Assert.Equal("0xJOB", candidate.QueryHash);
    }

    // --- Aggregation ---------------------------------------------------------

    [Fact]
    public void Duplicate_intervals_merge_with_execution_weighted_averages()
    {
        var records = new List<SlowQueryRecord>
        {
            Record(executionCount: 1, avgDurationMs: 6000, maxDurationMs: 6000, minDurationMs: 6000),
            Record(executionCount: 9, avgDurationMs: 11_000, maxDurationMs: 12_000, minDurationMs: 4000),
        };

        var merged = Assert.Single(SlowQueryCandidateBuilder.AggregateSlowRecords(records, Threshold));

        Assert.Equal(10, merged.ExecutionCount);

        // (6000*1 + 11000*9) / 10 — an interval with nine executions describes the query
        // far better than one with a single execution.
        Assert.Equal(10_500, merged.AvgDurationMs, 3);

        // The worst and best observations survive rather than being averaged away.
        Assert.Equal(12_000, merged.MaxDurationMs);
        Assert.Equal(4000, merged.MinDurationMs);

        Assert.Equal(2, merged.IntervalsObserved);
        Assert.Equal(2, merged.IntervalsExceedingThreshold);
    }

    [Fact]
    public void Interval_counts_record_how_often_the_threshold_was_actually_exceeded()
    {
        var records = new List<SlowQueryRecord>
        {
            Record(maxDurationMs: 9000),
            Record(maxDurationMs: 1200),
            Record(maxDurationMs: 400),
        };

        var merged = Assert.Single(SlowQueryCandidateBuilder.AggregateSlowRecords(records, Threshold));

        Assert.Equal(3, merged.IntervalsObserved);
        Assert.Equal(1, merged.IntervalsExceedingThreshold);
    }

    [Fact]
    public void A_single_interval_still_reports_its_interval_counts()
    {
        var single = Assert.Single(
            SlowQueryCandidateBuilder.AggregateSlowRecords([Record(maxDurationMs: 9000)], Threshold));

        Assert.Equal(1, single.IntervalsObserved);
        Assert.Equal(1, single.IntervalsExceedingThreshold);
    }

    [Fact]
    public void Different_plans_are_never_merged()
    {
        var records = new List<SlowQueryRecord>
        {
            Record(planId: 1),
            Record(planId: 2),
        };

        Assert.Equal(2, SlowQueryCandidateBuilder.AggregateSlowRecords(records, Threshold).Count);
    }

    [Fact]
    public async Task Grouping_can_be_turned_off()
    {
        var records = new List<SlowQueryRecord> { Record(), Record() };

        var grouped = await Builder().BuildAsync(records, Threshold, Database, groupResults: true);
        var ungrouped = await Builder().BuildAsync(records, Threshold, Database, groupResults: false);

        Assert.Single(grouped);
        Assert.Equal(2, ungrouped.Count);
    }

    // --- Confidence and severity --------------------------------------------

    [Fact]
    public void Repeated_slowness_against_a_normally_fast_baseline_is_high_confidence()
    {
        var baseline = new BaselineComparison
        {
            BaselineStatus = BaselineStatus.Ready,
            HistoricalMeanMs = 1000,
            StandardDeviationMs = 100,
            RegressionMultiplierVsMean = 9.7,
        };

        Assert.Equal(
            Confidence.High,
            SlowQueryCandidateBuilder.ComputeConfidence(Record(executionCount: 3), Threshold, baseline));
    }

    [Fact]
    public void Barely_over_the_threshold_is_low_confidence()
    {
        Assert.Equal(
            Confidence.Low,
            SlowQueryCandidateBuilder.ComputeConfidence(Record(maxDurationMs: 5500), Threshold, null));
    }

    [Fact]
    public void A_noisy_baseline_lowers_confidence_near_the_threshold()
    {
        var noisy = new BaselineComparison
        {
            BaselineStatus = BaselineStatus.Ready,
            HistoricalMeanMs = 1000,
            StandardDeviationMs = 2000,
            RegressionMultiplierVsMean = 7.0,
        };

        Assert.Equal(
            Confidence.Low,
            SlowQueryCandidateBuilder.ComputeConfidence(Record(maxDurationMs: 7000, executionCount: 1), Threshold, noisy));
    }

    [Fact]
    public void Without_a_baseline_a_clear_exceedance_is_medium_confidence()
    {
        Assert.Equal(
            Confidence.Medium,
            SlowQueryCandidateBuilder.ComputeConfidence(Record(maxDurationMs: 9749), Threshold, null));
    }

    [Theory]
    [InlineData(15_000, Severity.Critical)]
    [InlineData(9749, Severity.High)]
    [InlineData(5200, Severity.Medium)]
    public void Severity_follows_how_far_the_threshold_was_exceeded(double maxDurationMs, Severity expected)
    {
        Assert.Equal(
            expected,
            SlowQueryCandidateBuilder.ComputeSeverity(Record(maxDurationMs: maxDurationMs), Threshold, null));
    }

    [Fact]
    public void A_large_baseline_regression_raises_severity_on_its_own()
    {
        var baseline = new BaselineComparison
        {
            BaselineStatus = BaselineStatus.Ready,
            HistoricalMeanMs = 700,
            RegressionMultiplierVsMean = 8.5,
        };

        Assert.Equal(
            Severity.Critical,
            SlowQueryCandidateBuilder.ComputeSeverity(Record(maxDurationMs: 5200), Threshold, baseline));
    }

    // --- Baseline comparison -------------------------------------------------

    [Fact]
    public async Task A_query_without_a_hash_has_no_baseline_comparison()
    {
        var candidates = await Builder().BuildAsync(
            [Record(queryHash: null)], Threshold, Database);

        Assert.Null(Assert.Single(candidates).BaselineComparison);
    }

    [Fact]
    public async Task A_query_with_no_stored_history_reports_insufficient_data()
    {
        var candidates = await Builder().BuildAsync([Record()], Threshold, Database);

        var comparison = Assert.Single(candidates).BaselineComparison;
        Assert.NotNull(comparison);
        Assert.Equal(BaselineStatus.InsufficientData, comparison.BaselineStatus);
    }

    [Fact]
    public async Task A_ready_baseline_produces_regression_multipliers()
    {
        var stub = new StubBaselineService()
            .WithBaseline("0xABCD", ReadyBaseline(mean: 1000, median: 950, p95: 1900, standardDeviation: 120));

        var candidates = await Builder(stub).BuildAsync(
            [Record(maxDurationMs: 9749)], Threshold, Database);

        var comparison = Assert.Single(candidates).BaselineComparison;
        Assert.NotNull(comparison);
        Assert.Equal(BaselineStatus.Ready, comparison.BaselineStatus);
        Assert.Equal(1000, comparison.HistoricalMeanMs);
        Assert.Equal(1900, comparison.HistoricalP95Ms);
        Assert.Equal(9.7, comparison.RegressionMultiplierVsMean);
        Assert.Equal(5.1, comparison.RegressionMultiplierVsP95);
    }

    [Fact]
    public async Task Unavailable_baseline_storage_degrades_the_detail_not_the_candidate()
    {
        var stub = new StubBaselineService { ThrowOnAccess = true };

        var candidates = await Builder(stub).BuildAsync(
            [Record()], Threshold, Database);

        var candidate = Assert.Single(candidates);
        Assert.Null(candidate.BaselineComparison);
        Assert.Equal(CandidateClassification.SlowQueryCandidate, candidate.Classification);
    }

    // --- Ranking -------------------------------------------------------------

    [Fact]
    public async Task Candidates_are_ranked_worst_first()
    {
        var records = new List<SlowQueryRecord>
        {
            Record(queryHash: "0xSLOW", planId: 1, maxDurationMs: 6000, maxCpuMs: 100),
            Record(queryHash: "0xWORST", planId: 2, maxDurationMs: 40_000, maxCpuMs: 100),
            Record(queryHash: "0xMIDDLE", planId: 3, maxDurationMs: 12_000, maxCpuMs: 100),
        };

        var candidates = await Builder().BuildAsync(records, Threshold, Database);

        Assert.Equal(new string?[] { "0xWORST", "0xMIDDLE", "0xSLOW" }, candidates.Select(c => c.QueryHash));
        Assert.Equal(new int?[] { 1, 2, 3 }, candidates.Select(c => c.Rank));
    }

    [Fact]
    public void An_application_workload_outranks_an_identical_background_workload()
    {
        var application = new TimeoutCandidate
        {
            MaxDurationMs = 10_000,
            MaxCpuMs = 0,
            IntervalsObserved = 1,
            IntervalsExceedingThreshold = 1,
            WorkloadType = WorkloadType.ApplicationOrInteractive,
        };

        var background = new TimeoutCandidate
        {
            MaxDurationMs = 10_000,
            MaxCpuMs = 0,
            IntervalsObserved = 1,
            IntervalsExceedingThreshold = 1,
            WorkloadType = WorkloadType.BackgroundOrScheduled,
        };

        // ((10000/5000)*0.4 + 1*0.2) * 1.2 = 1.2 ; the same figures at 0.8 give 0.8.
        Assert.Equal(1.2, SlowQueryCandidateBuilder.ComputeRankScore(application, Threshold));
        Assert.Equal(0.8, SlowQueryCandidateBuilder.ComputeRankScore(background, Threshold));
    }

    // --- Projection ----------------------------------------------------------

    [Fact]
    public async Task Candidate_carries_the_identity_and_metrics_a_dba_needs()
    {
        var longText = new string('x', 500);

        var candidates = await Builder().BuildAsync(
            [Record(sqlText: longText, procedureName: "dbo.uspGetOrders")],
            Threshold,
            Database,
            applicationTimeoutMs: 30_000);

        var candidate = Assert.Single(candidates);

        Assert.Equal(42L, candidate.QueryId);
        Assert.Equal("0xABCD", candidate.QueryHash);
        Assert.Equal(991L, candidate.ObjectId);
        Assert.Equal("dbo.uspGetOrders", candidate.ProcedureName);
        Assert.Equal(7L, candidate.PlanId);
        Assert.Equal(200, candidate.SqlTextPreview!.Length);
        Assert.Equal(3, candidate.ExecutionCount);
        Assert.Equal(3, candidate.TotalExecutionCount);
        Assert.Equal(9749, candidate.MaxDurationMs);
        Assert.Equal(6100, candidate.AvgDurationMs);
        Assert.Equal(5200, candidate.MinDurationMs);
        Assert.Equal(2400, candidate.MaxCpuMs);
        Assert.Equal(1200, candidate.AvgCpuMs);
        Assert.Equal(9000, candidate.MaxLogicalReads);
        Assert.Equal(First, candidate.FirstExecutionTime);
        Assert.Equal(Last, candidate.LastExecutionTime);
        Assert.Equal(Threshold, candidate.SlowThresholdMs);
        Assert.Equal(30_000, candidate.ApplicationTimeoutMs);
        Assert.Equal(1, candidate.Rank);
    }

    [Fact]
    public async Task Empty_sql_text_produces_no_preview_rather_than_an_empty_string()
    {
        var candidates = await Builder().BuildAsync([Record(sqlText: null)], Threshold, Database);

        Assert.Null(Assert.Single(candidates).SqlTextPreview);
    }
}
