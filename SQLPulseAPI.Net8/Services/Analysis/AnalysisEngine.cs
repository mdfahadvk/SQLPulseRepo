using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Services.History;
using SQLPulseAPI.Net8.Services.Performance;

namespace SQLPulseAPI.Net8.Services.Analysis;

/// <summary>
/// Orchestrates the deterministic detectors and produces an <see cref="AnalysisResult"/>.
/// </summary>
public sealed class AnalysisEngine(
    IPerformanceSnapshotService snapshotService,
    LongRunningDetector longRunningDetector,
    BlockingDetector blockingDetector,
    HighCpuDetector highCpuDetector,
    HighIoDetector highIoDetector,
    QueryRegressionDetector regressionDetector,
    IHistoryService historyService,
    ILogger<AnalysisEngine> logger) : IAnalysisEngine
{
    /// <summary>
    /// Categories describing something wrong right now. Only these drive health status.
    /// </summary>
    private static readonly HashSet<FindingCategory> HealthCategories =
    [
        FindingCategory.LongRunningQuery,
        FindingCategory.Blocking,
        FindingCategory.QueryRegression,
    ];

    /// <summary>
    /// Categories describing tuning opportunities derived from lifetime plan-cache
    /// statistics. These never affect health status.
    /// </summary>
    private static readonly HashSet<FindingCategory> OptimizationCategories =
    [
        FindingCategory.HighCpu,
        FindingCategory.HighIo,
    ];

    /// <summary>
    /// Plan-cache categories describing the same underlying query, so a query appearing
    /// in both is one finding carrying two signals.
    /// </summary>
    private static readonly HashSet<FindingCategory> CorrelatableCategories =
    [
        FindingCategory.HighCpu,
        FindingCategory.HighIo,
    ];

    /// <inheritdoc />
    public async Task<AnalysisResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Starting performance analysis (scope={Scope}, db={Database})",
            request.Scope,
            request.Database ?? "all");

        var snapshot = await snapshotService.CollectAsync(
            new PerformanceSnapshotRequest(
                request.Scope,
                request.Database,
                request.LongRunningThresholdMs,
                request.TopN,
                request.WaitStatsTopN),
            cancellationToken);

        // Persist before analysing, matching the Python pipeline. Note the consequence:
        // the sample just collected is inside the baseline window it is then compared
        // against, which slightly dampens the reported multiplier. Persisting is
        // non-fatal and never blocks the analysis.
        await historyService.PersistSnapshotAsync(snapshot, cancellationToken);

        var result = await RunAnalysisAsync(snapshot, request.Scope, request.Database, cancellationToken);

        logger.LogInformation(
            "Analysis complete: {Status} — {Total} findings ({Critical} critical, {High} high, {Medium} medium, {Low} low)",
            result.OverallStatus,
            result.TotalFindings,
            result.CriticalCount,
            result.HighCount,
            result.MediumCount,
            result.LowCount);

        return result;
    }

    /// <inheritdoc />
    public async Task<AnalysisResult> RunAnalysisAsync(
        PerformanceSnapshot snapshot,
        AnalysisScope scope,
        string? database,
        CancellationToken cancellationToken = default)
    {
        DetectorBase[] detectors =
        [
            longRunningDetector,
            blockingDetector,
            highCpuDetector,
            highIoDetector,
        ];

        var allFindings = new List<PerformanceFinding>();
        var excludedQueries = new List<ExcludedQuery>();

        foreach (var detector in detectors)
        {
            try
            {
                var detection = detector.Detect(snapshot);
                allFindings.AddRange(detection.Findings);
                excludedQueries.AddRange(detection.ExcludedQueries);

                logger.LogDebug(
                    "Detector {Detector} produced {Count} finding(s)",
                    detector.Name,
                    detection.Findings.Count);
            }
            catch (Exception exception)
            {
                // One failing detector must not lose the findings of the others.
                logger.LogError(exception, "Detector {Detector} failed", detector.Name);
            }
        }

        // Regression detection is the only historical analysis. If it cannot run, the
        // result must say so rather than implying baselines were checked and found clean.
        var historicalStatus = HistoricalAnalysisStatus.Available;
        try
        {
            var regression = await regressionDetector.DetectAsync(snapshot, cancellationToken);
            allFindings.AddRange(regression.Findings);

            logger.LogDebug(
                "Detector {Detector} produced {Count} finding(s)",
                regressionDetector.Name,
                regression.Findings.Count);
        }
        catch (Exception exception)
        {
            historicalStatus = HistoricalAnalysisStatus.Unavailable;
            logger.LogError(exception, "Detector {Detector} failed", regressionDetector.Name);
        }

        // Fold multi-signal findings together before sorting, so counts and ordering
        // reflect distinct problems rather than distinct detectors.
        var correlated = MergeCorrelated(allFindings);
        var sorted = SortFindings(correlated);
        AddDrilldown(sorted, scope);

        // An expensive cached query is an optimization opportunity, NOT evidence of an
        // active incident — it must never drive the health status on its own.
        var healthFindings = sorted.Where(f => HealthCategories.Contains(f.Category)).ToList();
        var optimizationFindings = sorted.Where(f => OptimizationCategories.Contains(f.Category)).ToList();

        var overallStatus = DetermineOverallStatus(healthFindings);

        var affectedDatabases = scope == AnalysisScope.Server
            ? ComputeAffectedDatabases(sorted)
            : [];

        var deduplicatedExclusions = DeduplicateExclusions(excludedQueries);
        if (deduplicatedExclusions.Count > 0)
        {
            logger.LogInformation(
                "Excluded {Count} monitoring/diagnostic quer(ies) from findings",
                deduplicatedExclusions.Count);
        }

        return new AnalysisResult
        {
            AnalysisId = Guid.NewGuid().ToString(),
            AnalyzedAt = DateTimeOffset.UtcNow,
            Server = snapshot.ServerHost,
            Scope = scope,
            Database = database,
            OverallStatus = overallStatus,
            Findings = sorted,
            AffectedDatabases = affectedDatabases,
            TotalFindings = sorted.Count,
            CriticalCount = sorted.Count(f => f.Severity == Severity.Critical),
            HighCount = sorted.Count(f => f.Severity == Severity.High),
            MediumCount = sorted.Count(f => f.Severity == Severity.Medium),
            LowCount = sorted.Count(f => f.Severity == Severity.Low),
            CurrentHealth = new HealthSection
            {
                Status = overallStatus,
                Findings = healthFindings,
            },
            OptimizationOpportunities = optimizationFindings,
            HistoricalAnalysisStatus = historicalStatus,
            Summary = BuildSummary(
                sorted,
                healthFindings,
                optimizationFindings,
                overallStatus,
                affectedDatabases,
                deduplicatedExclusions.Count),
            ExcludedQueries = deduplicatedExclusions,
        };
    }

    /// <summary>Deterministically computes overall health from findings.</summary>
    /// <remarks>
    /// Only <see cref="HealthCategories"/> findings are ever passed here: a server with
    /// nothing actively wrong is HEALTHY even when tuning opportunities exist.
    /// </remarks>
    public static OverallStatus DetermineOverallStatus(IReadOnlyList<PerformanceFinding> findings)
    {
        if (findings.Count == 0)
        {
            return OverallStatus.Healthy;
        }

        if (findings.Any(f => f.Severity == Severity.Critical))
        {
            return OverallStatus.Critical;
        }

        if (findings.Any(f => f.Severity == Severity.High))
        {
            return OverallStatus.Degraded;
        }

        if (findings.Any(f => f.Severity == Severity.Medium))
        {
            return OverallStatus.Warning;
        }

        return OverallStatus.Healthy;
    }

    /// <summary>Ranks severity, most serious first.</summary>
    private static int SeverityRank(Severity severity) => severity switch
    {
        Severity.Critical => 0,
        Severity.High => 1,
        Severity.Medium => 2,
        _ => 3,
    };

    /// <summary>Ranks confidence, strongest first.</summary>
    private static int ConfidenceRank(Confidence confidence) => confidence switch
    {
        Confidence.High => 0,
        Confidence.Medium => 1,
        _ => 2,
    };

    /// <summary>Sorts by severity, then by confidence. Stable, so detector order breaks ties.</summary>
    private static List<PerformanceFinding> SortFindings(IEnumerable<PerformanceFinding> findings) =>
        findings
            .OrderBy(f => SeverityRank(f.Severity))
            .ThenBy(f => ConfidenceRank(f.Confidence))
            .ToList();

    /// <summary>
    /// Identity used to recognise findings about the same query.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the finding cannot be identified confidently. Such
    /// findings are never merged, because merging on weak identity would combine
    /// unrelated queries.
    /// </returns>
    private static (string? Database, string QueryHash, string? QueryPlanHash)? CorrelationKey(
        PerformanceFinding finding)
    {
        var identity = finding.QueryIdentity;
        if (identity is null || string.IsNullOrEmpty(identity.QueryHash))
        {
            return null;
        }

        // Plan hash is included when present: the same statement running under two
        // different plans is genuinely two different performance stories.
        return (finding.Database, identity.QueryHash, identity.QueryPlanHash);
    }

    /// <summary>
    /// Combines CPU and I/O findings describing the same query.
    /// </summary>
    /// <remarks>
    /// A query that is both CPU- and read-heavy is one problem with two symptoms, not two
    /// problems. Emitting it twice doubles the noise and makes the counts misleading. The
    /// merged finding keeps every metric from both sides, so no evidence is lost.
    /// </remarks>
    private static List<PerformanceFinding> MergeCorrelated(IReadOnlyList<PerformanceFinding> findings)
    {
        var merged = new Dictionary<(string?, string, string?), PerformanceFinding>();
        var result = new List<PerformanceFinding>();

        foreach (var finding in findings)
        {
            var key = CorrelationKey(finding);
            if (key is null || !CorrelatableCategories.Contains(finding.Category))
            {
                result.Add(finding);
                continue;
            }

            if (!merged.TryGetValue(key.Value, out var existing))
            {
                merged[key.Value] = finding;
                result.Add(finding);
                continue;
            }

            Absorb(existing, finding);
        }

        return result;
    }

    /// <summary>Folds <paramref name="other"/> into <paramref name="target"/>, preserving all evidence.</summary>
    private static void Absorb(PerformanceFinding target, PerformanceFinding other)
    {
        foreach (var signal in other.Signals)
        {
            if (!target.Signals.Contains(signal))
            {
                target.Signals.Add(signal);
            }
        }

        // Keep both metric sets, labelled so it stays clear which signal each
        // measurement came from.
        var label = EnumMemberValue(other.Category);
        foreach (var line in other.Evidence.Select(e => $"[{label}] {e}"))
        {
            if (!target.Evidence.Contains(line))
            {
                target.Evidence.Add(line);
            }
        }

        // The finding inherits the more serious severity of the two symptoms.
        if (SeverityRank(other.Severity) < SeverityRank(target.Severity))
        {
            target.Severity = other.Severity;
            target.Category = other.Category;
            target.Summary = other.Summary;
            target.LikelyCause = other.LikelyCause;
            target.Recommendation = other.Recommendation;
            target.HumanReviewRequired = other.HumanReviewRequired;
        }

        var basis = string.Join("; ", new[] { target.SeverityBasis, other.SeverityBasis }
            .Where(b => !string.IsNullOrEmpty(b)));
        target.SeverityBasis = string.IsNullOrEmpty(basis) ? null : basis;

        foreach (var step in other.ValidationSteps)
        {
            if (!target.ValidationSteps.Contains(step))
            {
                target.ValidationSteps.Add(step);
            }
        }

        // Confidence stays at the strongest of the two. Corroboration across detectors is
        // NOT treated as grounds to exceed it: both readings come from the same cached
        // statistics, so they are not independent evidence.
        if (ConfidenceRank(other.Confidence) < ConfidenceRank(target.Confidence))
        {
            target.Confidence = other.Confidence;
        }
    }

    /// <summary>
    /// Adds a drilldown to server-level findings that identify a database.
    /// </summary>
    /// <remarks>
    /// A finding with no resolved database gets no drilldown: pointing the user at a
    /// database SQLPulse could not name would be a dead end.
    /// </remarks>
    private static void AddDrilldown(IReadOnlyList<PerformanceFinding> findings, AnalysisScope scope)
    {
        if (scope != AnalysisScope.Server)
        {
            return;
        }

        foreach (var finding in findings)
        {
            if (!string.IsNullOrEmpty(finding.Database)
                && finding.Severity is Severity.Critical or Severity.High or Severity.Medium)
            {
                finding.RecommendedDrilldown = new RecommendedDrilldown
                {
                    Scope = AnalysisScope.Database,
                    Database = finding.Database,
                };
            }
        }
    }

    /// <summary>
    /// Groups findings by database.
    /// </summary>
    /// <remarks>
    /// Findings whose database could not be resolved are skipped rather than grouped
    /// under a placeholder name.
    /// </remarks>
    private static List<AffectedDatabase> ComputeAffectedDatabases(IReadOnlyList<PerformanceFinding> findings) =>
        findings
            .Where(f => !string.IsNullOrEmpty(f.Database))
            .GroupBy(f => f.Database!, StringComparer.Ordinal)
            .Select(group => new AffectedDatabase
            {
                Database = group.Key,
                HighestSeverity = group.Min(f => f.Severity),
                FindingCount = group.Count(),
            })
            .OrderBy(entry => SeverityRank(entry.HighestSeverity))
            .ToList();

    /// <summary>
    /// Collapses the same excluded query reported by several detectors.
    /// </summary>
    /// <remarks>
    /// A monitoring query typically appears in both the CPU and I/O lists, so without
    /// this the exclusion report would double-count it.
    /// </remarks>
    private static List<ExcludedQuery> DeduplicateExclusions(IReadOnlyList<ExcludedQuery> exclusions)
    {
        var seen = new HashSet<(string, string?, string?, string?)>();
        var unique = new List<ExcludedQuery>();

        foreach (var item in exclusions)
        {
            if (seen.Add((item.Reason, item.QueryHash, item.Database, item.SqlPreview)))
            {
                unique.Add(item);
            }
        }

        return unique;
    }

    /// <summary>Builds the flat digest the UI reads first.</summary>
    private static AnalysisSummary BuildSummary(
        IReadOnlyList<PerformanceFinding> findings,
        IReadOnlyList<PerformanceFinding> healthFindings,
        IReadOnlyList<PerformanceFinding> optimizationFindings,
        OverallStatus overallStatus,
        IReadOnlyList<AffectedDatabase> affectedDatabases,
        int excludedCount) => new()
        {
            CurrentHealthStatus = overallStatus,
            HealthFindingCount = healthFindings.Count,
            OptimizationOpportunityCount = optimizationFindings.Count,
            HighestSeverity = findings.Count > 0 ? findings.Min(f => f.Severity) : null,
            AffectedDatabaseCount = affectedDatabases.Count,
            TotalFindings = findings.Count,
            CriticalCount = findings.Count(f => f.Severity == Severity.Critical),
            HighCount = findings.Count(f => f.Severity == Severity.High),
            MediumCount = findings.Count(f => f.Severity == Severity.Medium),
            LowCount = findings.Count(f => f.Severity == Severity.Low),
            ExcludedQueryCount = excludedCount,
        };

    /// <summary>Returns the wire value of a finding category, used to label merged evidence.</summary>
    private static string EnumMemberValue(FindingCategory category) => category switch
    {
        FindingCategory.LongRunningQuery => "long_running_query",
        FindingCategory.Blocking => "blocking",
        FindingCategory.HighCpu => "high_cpu",
        FindingCategory.HighIo => "high_io",
        FindingCategory.QueryRegression => "query_regression",
        FindingCategory.SlowQueryCandidate => "slow_query_candidate",
        _ => "timeout_candidate",
    };
}
