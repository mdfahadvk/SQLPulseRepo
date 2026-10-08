using System.Globalization;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.History;

namespace SQLPulseAPI.Net8.Detectors;

/// <summary>Turns raw Query Store rows into classified, ranked slow-query candidates.</summary>
public interface ISlowQueryCandidateBuilder
{
    /// <summary>
    /// Builds candidates for every record at or above <paramref name="thresholdMs"/>.
    /// </summary>
    /// <param name="slowRecords">Query Store records from the time window.</param>
    /// <param name="thresholdMs">Slow-query monitoring threshold in milliseconds.</param>
    /// <param name="database">Database name, used for baseline lookups.</param>
    /// <param name="groupResults">When true, duplicate intervals are merged per query/plan.</param>
    /// <param name="workloadTypeFilter">When set, only candidates of this workload type are returned.</param>
    /// <param name="applicationTimeoutMs">
    /// The application's explicitly configured command timeout, or <see langword="null"/>
    /// when unknown. Never inferred from <paramref name="thresholdMs"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<TimeoutCandidate>> BuildAsync(
        IReadOnlyList<SlowQueryRecord> slowRecords,
        double thresholdMs,
        string database,
        bool groupResults = true,
        WorkloadType? workloadTypeFilter = null,
        double? applicationTimeoutMs = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Slow-query and timeout candidate builder.
/// Ported from <c>app/detectors/timeout_candidate.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two thresholds, two different claims:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>SLOW_QUERY_CANDIDATE</c> — <c>max_duration &gt;= </c> the slow monitoring
///     threshold. Means "worth looking at". That threshold is chosen by whoever is
///     monitoring and is arbitrary.
///   </description></item>
///   <item><description>
///     <c>TIMEOUT_CANDIDATE</c> — <c>max_duration &gt;= </c> an <em>explicitly
///     configured</em> application timeout. Means "the client could plausibly have
///     given up".
///   </description></item>
/// </list>
/// <para>
/// The second is a much stronger claim and is only made when the application's timeout
/// is actually known. A monitoring filter of 5,000 ms says nothing about whether the
/// application waits 30 seconds before abandoning a request, so the slow threshold is
/// never reused as a timeout threshold. Neither label is a confirmed timeout: proving a
/// client abandoned a request needs application telemetry SQLPulse does not have.
/// </para>
/// </remarks>
public sealed class SlowQueryCandidateBuilder(
    IBaselineService baselineService,
    IOptions<AnalysisOptions> analysisOptions,
    ILogger<SlowQueryCandidateBuilder> logger) : ISlowQueryCandidateBuilder
{
    /// <summary>Window used for baseline regression lookups. Python: <c>window="7d"</c>.</summary>
    private const string BaselineWindow = "7d";

    /// <summary>Minimum samples before a baseline is usable. Python: <c>min_samples=5</c>.</summary>
    private const int BaselineMinSamples = 5;

    /// <summary>Maximum characters of SQL text echoed back to the caller.</summary>
    private const int SqlPreviewLength = 200;

    private readonly AnalysisOptions _analysis = analysisOptions.Value;

    /// <inheritdoc />
    public async Task<List<TimeoutCandidate>> BuildAsync(
        IReadOnlyList<SlowQueryRecord> slowRecords,
        double thresholdMs,
        string database,
        bool groupResults = true,
        WorkloadType? workloadTypeFilter = null,
        double? applicationTimeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var records = groupResults
            ? AggregateSlowRecords(slowRecords, thresholdMs)
            : slowRecords;

        var candidates = new List<TimeoutCandidate>();

        foreach (var record in records)
        {
            if (record.MaxDurationMs < thresholdMs)
            {
                continue;
            }

            var baseline = await CompareWithBaselineAsync(record, database, cancellationToken);
            var confidence = ComputeConfidence(record, thresholdMs, baseline);
            var severity = ComputeSeverity(record, thresholdMs, baseline);

            var workloadEvidence = WorkloadClassifier.GatherEvidence(
                record.QuerySqlText,
                record.ProcedureName,
                record.ProgramName,
                record.HostName,
                record.IsScheduledJob,
                _analysis.ApplicationProgramNames,
                _analysis.BackgroundProgramNames);

            var workloadType = WorkloadClassifier.ResolveWorkloadType(workloadEvidence);

            var (classification, reason) =
                ClassifyCandidate(record.MaxDurationMs, thresholdMs, applicationTimeoutMs);

            var preview = (record.QuerySqlText ?? string.Empty);
            preview = preview.Length > SqlPreviewLength ? preview[..SqlPreviewLength] : preview;

            candidates.Add(new TimeoutCandidate
            {
                Label = Wire(classification).ToLowerInvariant(),
                Classification = classification,
                IsSlowQueryCandidate = true,
                IsTimeoutCandidate = classification == CandidateClassification.TimeoutCandidate,
                SlowThresholdMs = thresholdMs,
                ApplicationTimeoutMs = applicationTimeoutMs,
                ClassificationReason = reason,
                QueryId = record.QueryId,
                QueryHash = record.QueryHash,
                ObjectId = record.ObjectId,
                ProcedureName = record.ProcedureName,
                PlanId = record.PlanId,
                SqlTextPreview = preview.Length == 0 ? null : preview,
                ExecutionCount = record.ExecutionCount,
                TotalExecutionCount = record.ExecutionCount,
                IntervalsObserved = record.IntervalsObserved,
                IntervalsExceedingThreshold = record.IntervalsExceedingThreshold,
                AvgDurationMs = Math.Round(record.AvgDurationMs, 1),
                MaxDurationMs = Math.Round(record.MaxDurationMs, 1),
                MinDurationMs = Math.Round(record.MinDurationMs, 1),
                AvgCpuMs = Math.Round(record.AvgCpuTimeMs, 1),
                MaxCpuMs = Math.Round(record.MaxCpuTimeMs, 1),
                AvgLogicalReads = Math.Round(record.AvgLogicalReads, 1),
                MaxLogicalReads = Math.Round(record.MaxLogicalReads, 1),
                FirstExecutionTime = record.FirstExecutionTime,
                LastExecutionTime = record.LastExecutionTime,
                BaselineComparison = baseline,
                Confidence = confidence,
                Severity = severity,
                WorkloadType = workloadType,
                ClassificationEvidence = workloadEvidence.Select(WorkloadClassifier.FormatEvidence).ToList(),
                WorkloadEvidence = workloadEvidence,
            });
        }

        if (workloadTypeFilter is { } filter)
        {
            candidates = candidates.Where(c => c.WorkloadType == filter).ToList();
        }

        return RankCandidates(candidates, thresholdMs);
    }

    // --- Aggregation ---------------------------------------------------------

    /// <summary>
    /// Merges duplicate Query Store intervals into one record per query/plan.
    /// Python: <c>aggregate_slow_records</c>.
    /// </summary>
    /// <remarks>
    /// Grouping key is <c>(query_hash, query_id, plan_id, object_id)</c>. Averages are
    /// weighted by execution count, because an interval with 1,000 executions describes
    /// the query far better than one with 2; max and min are preserved across intervals
    /// so the worst observation is never averaged away.
    /// </remarks>
    public static List<SlowQueryRecord> AggregateSlowRecords(
        IReadOnlyList<SlowQueryRecord> records,
        double thresholdMs)
    {
        var groups = records.GroupBy(r => (r.QueryHash, r.QueryId, r.PlanId, r.ObjectId));
        var aggregated = new List<SlowQueryRecord>();

        foreach (var group in groups)
        {
            var items = group.ToList();

            if (items.Count == 1)
            {
                var single = items[0];
                single.IntervalsObserved = 1;
                single.IntervalsExceedingThreshold = single.MaxDurationMs >= thresholdMs ? 1 : 0;
                aggregated.Add(single);
                continue;
            }

            var totalExecutions = items.Sum(r => r.ExecutionCount);

            // A group of intervals that all recorded zero executions still describes real
            // measurements, so fall back to an unweighted mean rather than dividing by zero.
            double weight = totalExecutions > 0 ? totalExecutions : 1;

            double WeightedAverage(Func<SlowQueryRecord, double> selector) =>
                items.Sum(r => selector(r) * r.ExecutionCount) / weight;

            var first = items[0];

            aggregated.Add(new SlowQueryRecord
            {
                QueryId = first.QueryId,
                QuerySqlText = first.QuerySqlText,
                QueryHash = first.QueryHash,
                ObjectId = first.ObjectId,
                ProcedureName = first.ProcedureName,
                PlanId = first.PlanId,
                ProgramName = first.ProgramName,
                HostName = first.HostName,
                IsScheduledJob = first.IsScheduledJob,
                ExecutionCount = totalExecutions,
                AvgDurationMs = WeightedAverage(r => r.AvgDurationMs),
                MaxDurationMs = items.Max(r => r.MaxDurationMs),
                MinDurationMs = items.Min(r => r.MinDurationMs),
                AvgCpuTimeMs = WeightedAverage(r => r.AvgCpuTimeMs),
                MaxCpuTimeMs = items.Max(r => r.MaxCpuTimeMs),
                AvgLogicalReads = WeightedAverage(r => r.AvgLogicalReads),
                MaxLogicalReads = items.Max(r => r.MaxLogicalReads),
                AvgPhysicalReads = WeightedAverage(r => r.AvgPhysicalReads),
                FirstExecutionTime = items
                    .Where(r => r.FirstExecutionTime.HasValue)
                    .Select(r => r.FirstExecutionTime)
                    .DefaultIfEmpty(null)
                    .Min(),
                LastExecutionTime = items
                    .Where(r => r.LastExecutionTime.HasValue)
                    .Select(r => r.LastExecutionTime)
                    .DefaultIfEmpty(null)
                    .Max(),
                IntervalStartTime = items
                    .Where(r => r.IntervalStartTime.HasValue)
                    .Select(r => r.IntervalStartTime)
                    .DefaultIfEmpty(null)
                    .Min(),
                IntervalsObserved = items.Count,
                IntervalsExceedingThreshold = items.Count(r => r.MaxDurationMs >= thresholdMs),
            });
        }

        return aggregated;
    }

    // --- Classification ------------------------------------------------------

    /// <summary>
    /// Decides whether an observation is merely slow or also a timeout candidate.
    /// Python: <c>classify_candidate</c>.
    /// </summary>
    /// <remarks>
    /// A <paramref name="applicationTimeoutMs"/> of <see langword="null"/> means the
    /// application's timeout is not configured. The query then stays a
    /// <c>SLOW_QUERY_CANDIDATE</c>: the monitoring threshold describes what an operator
    /// asked to see, not what the client tolerates, so promoting on it would invent a
    /// timeout that was never measured.
    /// </remarks>
    public static (CandidateClassification Classification, string Reason) ClassifyCandidate(
        double maxDurationMs,
        double slowThresholdMs,
        double? applicationTimeoutMs)
    {
        var slowSeconds = FormatSeconds(slowThresholdMs / 1000.0);
        var observedSeconds = FormatSeconds(maxDurationMs / 1000.0);

        if (applicationTimeoutMs is null)
        {
            return (
                CandidateClassification.SlowQueryCandidate,
                $"Exceeded the {slowSeconds}s slow-query monitoring threshold "
                + $"({observedSeconds}s observed). No application timeout is "
                + "configured, so timeout classification is unavailable.");
        }

        var timeoutSeconds = FormatSeconds(applicationTimeoutMs.Value / 1000.0);

        if (maxDurationMs >= applicationTimeoutMs.Value)
        {
            return (
                CandidateClassification.TimeoutCandidate,
                $"Reached {observedSeconds}s, at or above the configured "
                + $"{timeoutSeconds}s application timeout.");
        }

        return (
            CandidateClassification.SlowQueryCandidate,
            $"Exceeded the {slowSeconds}s slow-query monitoring threshold "
            + $"({observedSeconds}s observed) but stayed below the configured "
            + $"{timeoutSeconds}s application timeout.");
    }

    // --- Confidence and severity --------------------------------------------

    /// <summary>
    /// Computes confidence from measurable evidence only. Python: <c>_compute_confidence</c>.
    /// </summary>
    public static Confidence ComputeConfidence(
        SlowQueryRecord record,
        double thresholdMs,
        BaselineComparison? baseline)
    {
        var exceededMargin = thresholdMs > 0 ? record.MaxDurationMs / thresholdMs : 1.0;

        var hasBaseline = baseline is not null && baseline.BaselineStatus == BaselineStatus.Ready;

        var baselineShowsFast =
            hasBaseline
            && baseline!.HistoricalMeanMs > 0
            && baseline.RegressionMultiplierVsMean >= 3.0;

        var baselineNoisy =
            hasBaseline
            && baseline!.HistoricalMeanMs > 0
            && baseline.StandardDeviationMs / baseline.HistoricalMeanMs > 1.0;

        var repeated = record.ExecutionCount >= 3;

        // Repeated slowness against a baseline that is normally fast is the only
        // combination strong enough to call HIGH.
        if (repeated && baselineShowsFast)
        {
            return Confidence.High;
        }

        // Barely over the threshold against a baseline that swings wildly anyway.
        if (exceededMargin < 1.5 && baselineNoisy)
        {
            return Confidence.Low;
        }

        if (exceededMargin < 1.2)
        {
            return Confidence.Low;
        }

        return Confidence.Medium;
    }

    /// <summary>
    /// Computes severity from how far the query exceeded the threshold.
    /// Python: <c>_compute_severity</c>.
    /// </summary>
    public static Severity ComputeSeverity(
        SlowQueryRecord record,
        double thresholdMs,
        BaselineComparison? baseline)
    {
        var multiplier = thresholdMs > 0 ? record.MaxDurationMs / thresholdMs : 1.0;

        var baselineRegression = 0.0;
        if (baseline is { BaselineStatus: BaselineStatus.Ready, RegressionMultiplierVsMean: > 0 })
        {
            baselineRegression = baseline.RegressionMultiplierVsMean;
        }

        if (multiplier >= 3.0 || baselineRegression >= 8.0)
        {
            return Severity.Critical;
        }

        if (multiplier >= 1.5 || baselineRegression >= 4.0)
        {
            return Severity.High;
        }

        return Severity.Medium;
    }

    // --- Ranking -------------------------------------------------------------

    /// <summary>
    /// Composite ranking score, higher meaning more urgent. Python: <c>_compute_rank_score</c>.
    /// </summary>
    /// <remarks>
    /// Weights: duration vs threshold 40%, intervals exceeding threshold 20%, baseline
    /// regression 20%, CPU impact 10%, then a workload multiplier because a slow
    /// application query is felt by someone waiting while a slow nightly batch is not.
    /// </remarks>
    public static double ComputeRankScore(TimeoutCandidate candidate, double thresholdMs)
    {
        var durationRatio = thresholdMs > 0 ? candidate.MaxDurationMs / thresholdMs : 1.0;

        var intervalRatio = candidate.IntervalsObserved > 0
            ? (double)candidate.IntervalsExceedingThreshold / candidate.IntervalsObserved
            : 0.0;

        var regression = 0.0;
        if (candidate.BaselineComparison is { BaselineStatus: BaselineStatus.Ready } comparison)
        {
            regression = comparison.RegressionMultiplierVsMean;
        }

        var cpuRatio = candidate.MaxDurationMs > 0
            ? candidate.MaxCpuMs / candidate.MaxDurationMs
            : 0.0;

        var workloadBoost = candidate.WorkloadType switch
        {
            WorkloadType.ApplicationOrInteractive => 1.2,
            WorkloadType.BackgroundOrScheduled => 0.8,
            _ => 1.0,
        };

        var score =
            ((durationRatio * 0.4)
             + (intervalRatio * 0.2)
             + (regression * 0.2)
             + (cpuRatio * 0.1))
            * workloadBoost;

        return Math.Round(score, 4);
    }

    /// <summary>
    /// Sorts candidates worst-first and assigns <c>rank</c> starting at 1.
    /// Python: <c>rank_candidates</c>.
    /// </summary>
    public static List<TimeoutCandidate> RankCandidates(
        IReadOnlyList<TimeoutCandidate> candidates,
        double thresholdMs)
    {
        // OrderByDescending is stable, so equally scored candidates keep the order the
        // server returned them in, which keeps repeated calls reproducible.
        var ranked = candidates
            .OrderByDescending(c => ComputeRankScore(c, thresholdMs))
            .ToList();

        for (var index = 0; index < ranked.Count; index++)
        {
            ranked[index].Rank = index + 1;
        }

        return ranked;
    }

    // --- Baseline ------------------------------------------------------------

    /// <summary>
    /// Compares a record against its stored SQLPulse baseline.
    /// Python: <c>_compare_with_baseline</c>.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the query has no hash to look up, or when the
    /// lookup itself fails — a missing baseline must degrade the candidate's detail, not
    /// the candidate.
    /// </remarks>
    private async Task<BaselineComparison?> CompareWithBaselineAsync(
        SlowQueryRecord record,
        string database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(record.QueryHash))
        {
            return null;
        }

        try
        {
            var baseline = await baselineService.GetForRegressionAsync(
                record.QueryHash, database, BaselineWindow, BaselineMinSamples, cancellationToken);

            if (baseline?.AvgElapsedMs is not { } elapsed)
            {
                return new BaselineComparison { BaselineStatus = BaselineStatus.InsufficientData };
            }

            return new BaselineComparison
            {
                BaselineStatus = BaselineStatus.Ready,
                SampleCount = baseline.SampleCount,
                HistoricalMeanMs = Math.Round(elapsed.Mean, 1),
                HistoricalMedianMs = Math.Round(elapsed.Median, 1),
                HistoricalP95Ms = Math.Round(elapsed.P95, 1),
                StandardDeviationMs = Math.Round(elapsed.StandardDeviation, 1),
                RegressionMultiplierVsMean = elapsed.Mean > 0
                    ? Math.Round(record.MaxDurationMs / elapsed.Mean, 1)
                    : 0.0,
                RegressionMultiplierVsP95 = elapsed.P95 > 0
                    ? Math.Round(record.MaxDurationMs / elapsed.P95, 1)
                    : 0.0,
            };
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception, "Baseline comparison unavailable for {QueryHash}", record.QueryHash);

            return null;
        }
    }

    // --- Formatting ----------------------------------------------------------

    /// <summary>
    /// Formats a second count with three significant digits, matching Python's
    /// <c>{:.3g}</c> so classification reasons read identically in both implementations.
    /// </summary>
    private static string FormatSeconds(double seconds) =>
        seconds.ToString("G3", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);

    private static string Wire(CandidateClassification classification) =>
        Utilities.EnumMemberJsonConverter<CandidateClassification>.ToWireValue(classification);
}
