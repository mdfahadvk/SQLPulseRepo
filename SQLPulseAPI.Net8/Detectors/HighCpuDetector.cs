using System.Globalization;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Detectors;

/// <summary>
/// Detects queries with disproportionately high CPU consumption.
/// Ported from <c>HighCpuDetector</c> in <c>app/detectors/high_cpu.py</c>.
/// </summary>
/// <remarks>
/// Findings here come from the plan cache, which describes lifetime statistics rather
/// than current activity. They are optimization opportunities, never evidence that the
/// server is unhealthy right now — see <c>AnalysisEngine</c>.
/// </remarks>
public sealed class HighCpuDetector(IOptions<DetectorThresholdOptions> thresholds)
    : DetectorBase(thresholds.Value)
{
    /// <inheritdoc />
    public override string Name => "high_cpu_detector";

    /// <inheritdoc />
    public override DetectionResult Detect(PerformanceSnapshot snapshot)
    {
        var section = snapshot.TopCpuQueries;
        if (section is null || !section.Metadata.Success || section.Queries.Count == 0)
        {
            return DetectionResult.Empty;
        }

        var t = Thresholds;
        var now = Now();
        var targetDatabase = snapshot.Database;
        var findings = new List<PerformanceFinding>();
        var excluded = new List<ExcludedQuery>();

        foreach (var query in section.Queries)
        {
            // Database-scope isolation: never report another database's query.
            if (!string.IsNullOrEmpty(targetDatabase)
                && !string.IsNullOrEmpty(query.DatabaseName)
                && !string.Equals(query.DatabaseName, targetDatabase, StringComparison.Ordinal))
            {
                continue;
            }

            // SQLPulse's own DMV reads and other monitoring traffic are not tuning
            // targets — reporting them would crowd out real workload.
            var exclusion = ClassifyMonitoringQuery(query.SqlText, query.QueryHash, query.DatabaseName);
            if (exclusion is not null)
            {
                excluded.Add(exclusion);
                continue;
            }

            // Too few executions to distinguish a real pattern from a one-off.
            if (query.ExecutionCount < t.CpuMinExecutions)
            {
                continue;
            }

            var (severity, confidence, basis) = Classify(query);
            if (severity is null)
            {
                continue;
            }

            // Plan-cache metadata does not always carry a database. Without one the
            // observation is weaker: it cannot be attributed or drilled into.
            var database = query.DatabaseName ?? targetDatabase;
            if (database is null)
            {
                confidence = WeakenConfidence(confidence);
                basis = $"{basis}; database unresolved, confidence reduced";
            }

            var preview = Truncate(query.SqlText, 150);

            findings.Add(new PerformanceFinding
            {
                FindingId = NewFindingId(),
                Category = FindingCategory.HighCpu,
                Server = snapshot.ServerHost,
                Database = database,
                Severity = severity.Value,
                Confidence = confidence,
                FirstObserved = now,
                LastObserved = now,
                Summary =
                    $"High CPU query: avg {Fixed(query.AvgCpuTimeMs, 0)}ms/exec "
                    + $"over {query.ExecutionCount} executions "
                    + $"(total {query.TotalCpuTimeMs}ms)",
                Evidence =
                [
                    $"Query hash: {query.QueryHash ?? "N/A"}",
                    $"Query plan hash: {query.QueryPlanHash ?? "N/A"}",
                    $"Object: {query.ObjectName ?? "N/A"}",
                    $"Execution count: {query.ExecutionCount}",
                    $"Total CPU time: {query.TotalCpuTimeMs}ms",
                    $"Avg CPU time per execution: {Fixed(query.AvgCpuTimeMs, 1)}ms",
                    $"Total elapsed time: {query.TotalElapsedTimeMs}ms",
                    $"Avg elapsed time: {Fixed(query.AvgElapsedTimeMs, 1)}ms",
                    $"Database: {query.DatabaseName ?? "unresolved"}",
                    $"Severity basis: {basis}",
                    $"SQL: {(preview.Length == 0 ? "N/A" : preview)}",
                ],
                Signals = [FindingSignal.HighCpu],
                QueryIdentity = new QueryIdentity
                {
                    QueryHash = query.QueryHash,
                    QueryPlanHash = query.QueryPlanHash,
                    ObjectName = query.ObjectName,
                },
                SeverityBasis = basis,
                LikelyCause =
                    "This query consumes significant CPU per execution based on "
                    + "plan-cache statistics. The specific root cause cannot be "
                    + "determined from aggregate metrics alone.",
                Recommendation =
                    "Retrieve the execution plan via sys.dm_exec_query_plan to "
                    + "identify expensive operators. Review for optimization opportunities.",
                ValidationSteps =
                [
                    "Get the cached execution plan via sys.dm_exec_query_plan",
                    "Identify the most expensive operators in the plan",
                    "Check for missing index suggestions in the plan XML",
                    "Compare actual vs estimated row counts for cardinality issues",
                ],
                HumanReviewRequired = severity == Severity.High,
            });
        }

        return new DetectionResult(findings, excluded);
    }

    /// <summary>
    /// Assigns severity from per-execution cost first, aggregate cost second.
    /// </summary>
    /// <remarks>
    /// Per-execution cost is what identifies an inefficient query. Aggregate totals only
    /// describe how much workload the server ran, so they are treated as the weaker
    /// signal and are gated behind a per-execution floor.
    /// </remarks>
    private (Severity? Severity, Confidence Confidence, string Basis) Classify(TopCpuQuery query)
    {
        var t = Thresholds;

        // Expensive every single time it runs — the strongest plan-cache signal. Still
        // HIGH rather than CRITICAL: cached lifetime statistics cannot show that
        // anything is wrong right now.
        if (query.AvgCpuTimeMs >= t.CpuCriticalAvgMs)
        {
            return (
                Severity.High,
                Confidence.Medium,
                $"avg CPU/exec {Fixed(query.AvgCpuTimeMs, 0)}ms >= "
                    + $"{Fixed(t.CpuCriticalAvgMs, 0)}ms over {query.ExecutionCount} executions");
        }

        if (query.AvgCpuTimeMs >= t.CpuHighAvgMs)
        {
            return (
                Severity.Medium,
                Confidence.Medium,
                $"avg CPU/exec {Fixed(query.AvgCpuTimeMs, 0)}ms >= "
                    + $"{Fixed(t.CpuHighAvgMs, 0)}ms over {query.ExecutionCount} executions");
        }

        // Large lifetime total. Only meaningful if each execution is itself non-trivial,
        // otherwise this is just a frequently-run cheap query.
        if (query.TotalCpuTimeMs >= t.CpuCriticalTotalMs
            && query.AvgCpuTimeMs >= t.CpuMinSignificantAvgMs)
        {
            return (
                Severity.Low,
                Confidence.Low,
                $"total CPU {query.TotalCpuTimeMs}ms >= {t.CpuCriticalTotalMs}ms "
                    + $"with avg {Fixed(query.AvgCpuTimeMs, 0)}ms/exec; aggregate cost only");
        }

        return (null, Confidence.Low, string.Empty);
    }

    private static string Fixed(double value, int decimals) =>
        value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
}
