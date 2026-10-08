using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Models.Performance;
using SQLPulseAPI.Net8.Models.SqlErrors;
using SQLPulseAPI.Net8.Services.Agent;
using SQLPulseAPI.Net8.Services.History;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Services.QueryStore;

namespace SQLPulseAPI.Net8.Services.SqlErrors;

/// <summary>Finds SQLPulse performance evidence around a SQL error.</summary>
public interface ISqlErrorCorrelationService
{
    /// <summary>
    /// Gathers evidence observed within the configured window either side of an error.
    /// </summary>
    Task<SqlErrorCorrelation> CorrelateAsync(
        SqlErrorEvent errorEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Correlates an error with the performance evidence SQLPulse already collects.
/// </summary>
/// <remarks>
/// <para>
/// Every conclusion this service reaches is about coincidence. Two things happening in the
/// same five minutes is weak evidence on its own, which is why confidence rises only when
/// the evidence refers to the same query as the error — and even then the wording stays
/// "coincided with" and "possible contributor". An investigation that quietly upgrades
/// correlation into cause sends people to fix the wrong thing.
/// </para>
/// <para>
/// The assessment text is assembled here from the evidence rather than written by the
/// language model, so no amount of prompting can produce a stronger claim than the
/// evidence supports.
/// </para>
/// <para>
/// Every lookup is read-only and reuses the services behind the existing endpoints.
/// </para>
/// </remarks>
public sealed class SqlErrorCorrelationService(
    ISlowHistoryService slowHistoryService,
    IBaselineService baselineService,
    IPerformanceSnapshotService snapshotService,
    IOptions<SqlErrorOptions> sqlErrorOptions,
    IOptions<AnalysisOptions> analysisOptions,
    ILogger<SqlErrorCorrelationService> logger) : ISqlErrorCorrelationService
{
    /// <summary>Caveat added when the error names no database.</summary>
    public const string NoDatabaseCaveat =
        "The error does not name a database, so SQLPulse could not look for matching "
        + "performance evidence.";

    /// <summary>Caveat added when no evidence at all was found in the window.</summary>
    public const string NoEvidenceCaveat =
        "Absence of correlated evidence does not mean the database was healthy; it means "
        + "SQLPulse holds no matching observation for this window.";

    /// <summary>Caveat added when live evidence was not applicable because the error is historical.</summary>
    public const string HistoricalIncidentCaveat =
        "Blocking and currently running queries are point-in-time measurements, so they are "
        + "not available for an incident outside the live window.";

    /// <summary>How many Query Store observations to report.</summary>
    private const int MaxQueryStoreEvidence = 3;

    /// <summary>Duration multiple of the historical P95 that counts as a regression.</summary>
    private const double RegressionMultiplier = 1.5;

    private const int LongRunningThresholdMs = 5_000;
    private const int EvidenceTopN = 20;

    private readonly SqlErrorOptions _sqlErrors = sqlErrorOptions.Value;
    private readonly AnalysisOptions _analysis = analysisOptions.Value;

    /// <inheritdoc />
    public async Task<SqlErrorCorrelation> CorrelateAsync(
        SqlErrorEvent errorEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(errorEvent);

        var window = TimeSpan.FromMinutes(_sqlErrors.CorrelationWindowMinutes);

        var correlation = new SqlErrorCorrelation
        {
            WindowStart = errorEvent.Timestamp - window,
            WindowEnd = errorEvent.Timestamp + window,
            WindowMinutes = _sqlErrors.CorrelationWindowMinutes,
            Caveats = [SqlErrorCorrelation.CausationCaveat],
        };

        if (string.IsNullOrWhiteSpace(errorEvent.Database))
        {
            correlation.Caveats.Add(NoDatabaseCaveat);
            correlation.Assessment = BuildAssessment(correlation, errorEvent, regression: false, blocking: false);
            return correlation;
        }

        var matchedCandidates = await AddQueryStoreEvidenceAsync(correlation, errorEvent, cancellationToken);
        var regression = await AddBaselineEvidenceAsync(correlation, errorEvent, matchedCandidates, cancellationToken);
        var blocking = await AddLiveEvidenceAsync(correlation, errorEvent, cancellationToken);

        correlation.IdentityMatched = correlation.CorrelatedEvidence.Any(item =>
            item.MatchedOn.Contains("query_hash") || item.MatchedOn.Contains("stored_procedure"));

        correlation.CorrelationConfidence = DetermineConfidence(correlation, regression, blocking);

        if (correlation.CorrelatedEvidence.Count == 0)
        {
            correlation.Caveats.Add(NoEvidenceCaveat);
        }

        correlation.Assessment = BuildAssessment(correlation, errorEvent, regression, blocking);
        return correlation;
    }

    /// <summary>
    /// Adds Query Store observations from the window, preferring ones that refer to the
    /// same query as the error.
    /// </summary>
    private async Task<List<TimeoutCandidate>> AddQueryStoreEvidenceAsync(
        SqlErrorCorrelation correlation,
        SqlErrorEvent errorEvent,
        CancellationToken cancellationToken)
    {
        SlowHistoryResponse response;
        try
        {
            response = await slowHistoryService.GetSlowHistoryAsync(
                errorEvent.Database!,
                correlation.WindowStart,
                correlation.WindowEnd,
                _analysis.SlowQueryThresholdMs,
                EvidenceTopN,
                groupResults: true,
                workloadType: null,
                applicationTimeoutMs: _analysis.ApplicationTimeoutMs,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Query Store correlation failed for {Database}", errorEvent.Database);
            correlation.Caveats.Add(
                $"Query Store evidence could not be read: {LlmSanitizer.SanitizeText(exception.Message)}");

            return [];
        }

        if (!response.QueryStoreStatus.IsAccessible())
        {
            correlation.Caveats.Add(
                $"Query Store is not readable for '{errorEvent.Database}', so historical query "
                + "evidence is missing from this correlation.");

            return [];
        }

        var identityMatches = response.Candidates
            .Where(candidate => IdentityMatch(candidate, errorEvent) is not null)
            .ToList();

        // An identity match is real evidence; anything else is only "something else was
        // also slow", so the two are never mixed into one list.
        var selected = identityMatches.Count > 0
            ? identityMatches
            : response.Candidates.Take(1).ToList();

        foreach (var candidate in selected.Take(MaxQueryStoreEvidence))
        {
            List<string> matchedOn = IdentityMatch(candidate, errorEvent) is { } match
                ? [match]
                : ["database", "time_window"];

            var detail = new List<string>
            {
                $"Maximum duration {candidate.MaxDurationMs:F0} ms",
                $"Average duration {candidate.AvgDurationMs:F0} ms over {candidate.ExecutionCount} execution(s)",
                $"Average CPU {candidate.AvgCpuMs:F0} ms",
                $"Average logical reads {candidate.AvgLogicalReads:F0}",
            };

            correlation.CorrelatedEvidence.Add(new CorrelatedEvidenceItem
            {
                Kind = CorrelatedEvidenceKind.QueryStoreHistory,
                Source = "query_store",
                Summary = candidate.ProcedureName is { Length: > 0 } procedure
                    ? $"{procedure} ran for up to {candidate.MaxDurationMs / 1000.0:F1} sec in this window"
                    : $"A statement ran for up to {candidate.MaxDurationMs / 1000.0:F1} sec in this window",
                Detail = detail,
                MatchedOn = matchedOn,
                ObservedAt = candidate.LastExecutionTime,
            });
        }

        return identityMatches;
    }

    /// <summary>
    /// Adds the historical baseline for the query, and reports whether it shows a regression.
    /// </summary>
    private async Task<bool> AddBaselineEvidenceAsync(
        SqlErrorCorrelation correlation,
        SqlErrorEvent errorEvent,
        List<TimeoutCandidate> matchedCandidates,
        CancellationToken cancellationToken)
    {
        // The candidate builder has already computed a comparison for matched queries, so
        // reuse it rather than paying for the same statistics twice.
        var fromCandidate = matchedCandidates
            .Select(candidate => candidate.BaselineComparison)
            .FirstOrDefault(comparison => comparison is { BaselineStatus: BaselineStatus.Ready });

        if (fromCandidate is not null)
        {
            var observed = matchedCandidates[0].MaxDurationMs;
            return AddBaselineItem(
                correlation,
                fromCandidate.HistoricalMeanMs,
                fromCandidate.HistoricalP95Ms,
                fromCandidate.SampleCount,
                observed,
                "query_hash");
        }

        if (string.IsNullOrWhiteSpace(errorEvent.QueryHash) || errorEvent.DurationMs is not { } duration)
        {
            return false;
        }

        QueryBaseline baseline;
        try
        {
            baseline = await baselineService.CalculateAsync(
                errorEvent.QueryHash,
                errorEvent.Database!,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Baseline correlation failed for {QueryHash}", errorEvent.QueryHash);
            correlation.Caveats.Add("The historical baseline for this query could not be read.");
            return false;
        }

        if (baseline.BaselineStatus != BaselineStatus.Ready || baseline.AvgElapsedMs is not { } elapsed)
        {
            correlation.Caveats.Add(
                "There is not enough history for this query to compare the incident against a baseline.");

            return false;
        }

        return AddBaselineItem(
            correlation,
            elapsed.Mean,
            elapsed.P95,
            baseline.SampleCount,
            duration,
            "query_hash");
    }

    private static bool AddBaselineItem(
        SqlErrorCorrelation correlation,
        double historicalMeanMs,
        double historicalP95Ms,
        int sampleCount,
        double observedMs,
        string matchedOn)
    {
        var regression = historicalP95Ms > 0 && observedMs >= historicalP95Ms * RegressionMultiplier;

        correlation.CorrelatedEvidence.Add(new CorrelatedEvidenceItem
        {
            Kind = CorrelatedEvidenceKind.QueryBaseline,
            Source = "baseline",
            Summary = regression
                ? $"Observed {observedMs / 1000.0:F1} sec against a historical P95 of {historicalP95Ms / 1000.0:F1} sec"
                : $"Observed {observedMs / 1000.0:F1} sec is in line with a historical P95 of {historicalP95Ms / 1000.0:F1} sec",
            Detail =
            [
                $"Historical mean {historicalMeanMs:F0} ms",
                $"Historical P95 {historicalP95Ms:F0} ms",
                $"Baseline built from {sampleCount} sample(s)",
            ],
            MatchedOn = [matchedOn],
        });

        return regression;
    }

    /// <summary>
    /// Adds blocking and long-running evidence, but only for an incident that is still
    /// inside the live window.
    /// </summary>
    /// <remarks>
    /// Blocking is observed from live dynamic management views and is not retained, so it
    /// cannot be recovered for a past incident. Saying so is more useful than silently
    /// returning nothing and letting a reader conclude there was no blocking.
    /// </remarks>
    private async Task<bool> AddLiveEvidenceAsync(
        SqlErrorCorrelation correlation,
        SqlErrorEvent errorEvent,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < correlation.WindowStart || now > correlation.WindowEnd)
        {
            correlation.Caveats.Add(HistoricalIncidentCaveat);
            return false;
        }

        PerformanceSnapshot snapshot;
        try
        {
            snapshot = await snapshotService.CollectAsync(
                new PerformanceSnapshotRequest(
                    AnalysisScope.Database,
                    errorEvent.Database,
                    LongRunningThresholdMs,
                    EvidenceTopN,
                    EvidenceTopN),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Live correlation failed for {Database}", errorEvent.Database);
            correlation.Caveats.Add("Live blocking and long-running evidence could not be collected.");
            return false;
        }

        var blockingResult = snapshot.BlockingSessions is { Metadata.Success: true } section ? section : null;

        // Head blockers are not themselves waiting; only the sessions behind them measure
        // how long the workload was actually held up.
        var blocked = blockingResult?.Sessions.Where(session => !session.IsHeadBlocker).ToList() ?? [];

        if (blocked.Count > 0)
        {
            var longestWaitMs = blocked.Max(session => session.WaitTimeMs);

            correlation.CorrelatedEvidence.Add(new CorrelatedEvidenceItem
            {
                Kind = CorrelatedEvidenceKind.Blocking,
                Source = "blocking_sessions",
                Summary = $"{blocked.Count} session(s) blocked, longest wait {longestWaitMs / 1000.0:F1} sec",
                Detail =
                [
                    $"Blocking chains observed: {blockingResult!.BlockingChainCount}",
                    $"Longest observed wait {longestWaitMs} ms",
                ],
                MatchedOn = ["database", "time_window"],
                ObservedAt = snapshot.CollectedAt,
            });
        }

        var longRunning = snapshot.LongRunningQueries is { Metadata.Success: true } queries
            ? queries.Queries
            : new List<LongRunningQuery>();

        if (longRunning.Count > 0)
        {
            var longest = longRunning.Max(query => query.ElapsedTimeMs);

            correlation.CorrelatedEvidence.Add(new CorrelatedEvidenceItem
            {
                Kind = CorrelatedEvidenceKind.LongRunningQuery,
                Source = "long_running_queries",
                Summary = $"{longRunning.Count} query(ies) running longer than "
                    + $"{LongRunningThresholdMs / 1000} sec, longest {longest / 1000.0:F1} sec",
                Detail = [$"Longest elapsed time {longest} ms"],
                MatchedOn = ["database", "time_window"],
                ObservedAt = snapshot.CollectedAt,
            });
        }

        return blocked.Count > 0;
    }

    /// <summary>
    /// Grades the strength of the correlation, never of a causal claim.
    /// </summary>
    private static CorrelationConfidence DetermineConfidence(
        SqlErrorCorrelation correlation,
        bool regression,
        bool blocking)
    {
        if (correlation.CorrelatedEvidence.Count == 0)
        {
            return CorrelationConfidence.None;
        }

        if (!correlation.IdentityMatched)
        {
            // Same database, same window, different query. That is coincidence and is
            // graded as such however dramatic the numbers look.
            return CorrelationConfidence.Low;
        }

        return regression || blocking
            ? CorrelationConfidence.High
            : CorrelationConfidence.Medium;
    }

    /// <summary>
    /// Writes the assessment from the evidence, in language that stops at correlation.
    /// </summary>
    private string BuildAssessment(
        SqlErrorCorrelation correlation,
        SqlErrorEvent errorEvent,
        bool regression,
        bool blocking)
    {
        var minutes = _sqlErrors.CorrelationWindowMinutes;

        if (correlation.CorrelationConfidence == CorrelationConfidence.None)
        {
            return $"SQLPulse found no correlated performance evidence within \u00b1{minutes} minutes "
                + "of this error.";
        }

        var count = correlation.CorrelatedEvidence.Count;

        if (!correlation.IdentityMatched)
        {
            return $"The error coincided with {count} performance observation(s) in "
                + $"{errorEvent.Database} within \u00b1{minutes} minutes, none of which identify the "
                + "same query. Proximity in time alone is not evidence of a relationship.";
        }

        var identity = errorEvent.StoredProcedure is { Length: > 0 } procedure
            ? $"the same stored procedure ({procedure})"
            : "the same query";

        var contributors = new List<string>();
        if (regression)
        {
            contributors.Add("a duration regression against the historical baseline");
        }

        if (blocking)
        {
            contributors.Add("concurrent blocking");
        }

        var opening = $"The error coincided with {count} correlated observation(s) for {identity} "
            + $"within \u00b1{minutes} minutes.";

        return contributors.Count == 0
            ? opening + " No further corroborating evidence was found."
            : opening + $" SQLPulse also observed {string.Join(" and ", contributors)}, which are "
                + "possible contributors.";
    }

    /// <summary>Returns which identifier tied a candidate to the error, or null when none did.</summary>
    private static string? IdentityMatch(TimeoutCandidate candidate, SqlErrorEvent errorEvent)
    {
        if (errorEvent.QueryHash is { Length: > 0 } hash
            && string.Equals(candidate.QueryHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            return "query_hash";
        }

        if (errorEvent.StoredProcedure is { Length: > 0 } procedure
            && candidate.ProcedureName is { Length: > 0 } candidateProcedure
            && string.Equals(candidateProcedure, procedure, StringComparison.OrdinalIgnoreCase))
        {
            return "stored_procedure";
        }

        return null;
    }
}
