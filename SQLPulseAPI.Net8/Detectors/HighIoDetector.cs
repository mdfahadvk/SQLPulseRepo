using System.Globalization;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Detectors;

/// <summary>
/// Detects queries with excessive logical reads.
/// Ported from <c>HighIoDetector</c> in <c>app/detectors/high_io.py</c>.
/// </summary>
/// <remarks>
/// Like the CPU detector, this reads lifetime plan-cache statistics, so its findings are
/// optimization opportunities rather than evidence of a current incident.
/// </remarks>
public sealed class HighIoDetector(IOptions<DetectorThresholdOptions> thresholds)
    : DetectorBase(thresholds.Value)
{
    /// <inheritdoc />
    public override string Name => "high_io_detector";

    /// <inheritdoc />
    public override DetectionResult Detect(PerformanceSnapshot snapshot)
    {
        var section = snapshot.TopIoQueries;
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
            if (query.ExecutionCount < t.IoMinExecutions)
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
                Category = FindingCategory.HighIo,
                Server = snapshot.ServerHost,
                Database = database,
                Severity = severity.Value,
                Confidence = confidence,
                FirstObserved = now,
                LastObserved = now,
                Summary =
                    $"High I/O query: avg {Grouped(query.AvgLogicalReads, 0)} logical reads/exec "
                    + $"over {query.ExecutionCount} executions "
                    + $"(total {Grouped(query.TotalLogicalReads, 0)} reads)",
                Evidence =
                [
                    $"Query hash: {query.QueryHash ?? "N/A"}",
                    $"Query plan hash: {query.QueryPlanHash ?? "N/A"}",
                    $"Object: {query.ObjectName ?? "N/A"}",
                    $"Execution count: {query.ExecutionCount}",
                    $"Total logical reads: {Grouped(query.TotalLogicalReads, 0)}",
                    $"Avg logical reads per execution: {Grouped(query.AvgLogicalReads, 0)}",
                    $"Total physical reads: {Grouped(query.TotalPhysicalReads, 0)}",
                    $"Avg physical reads: {Grouped(query.AvgPhysicalReads, 1)}",
                    $"Database: {query.DatabaseName ?? "unresolved"}",
                    $"Severity basis: {basis}",
                    $"SQL: {(preview.Length == 0 ? "N/A" : preview)}",
                ],
                Signals = [FindingSignal.HighIo],
                QueryIdentity = new QueryIdentity
                {
                    QueryHash = query.QueryHash,
                    QueryPlanHash = query.QueryPlanHash,
                    ObjectName = query.ObjectName,
                },
                SeverityBasis = basis,
                LikelyCause =
                    "This query performs a high number of logical reads per execution "
                    + "based on plan-cache statistics. The specific root cause cannot be "
                    + "determined from aggregate metrics alone.",
                Recommendation =
                    "Retrieve the execution plan via sys.dm_exec_query_plan to "
                    + "identify operators with highest I/O cost. Look for scan operators "
                    + "that could benefit from index changes.",
                ValidationSteps =
                [
                    "Get the execution plan and find operators with highest I/O cost",
                    "Check for Table Scan or Clustered Index Scan operators",
                    "Look for missing index DMV recommendations",
                    "Compare actual vs estimated row counts",
                ],
                HumanReviewRequired = severity == Severity.High,
            });
        }

        return new DetectionResult(findings, excluded);
    }

    /// <summary>
    /// Assigns severity from per-execution reads first, aggregate reads second.
    /// </summary>
    /// <remarks>
    /// Mirrors the CPU detector: reads-per-execution identify an inefficient access path,
    /// whereas lifetime totals mostly reflect how often the query ran. Totals are
    /// therefore gated behind a per-execution floor.
    /// </remarks>
    private (Severity? Severity, Confidence Confidence, string Basis) Classify(TopIoQuery query)
    {
        var t = Thresholds;

        // Extremely read-heavy on every execution. HIGH rather than CRITICAL: cached
        // lifetime statistics cannot demonstrate current resource pressure.
        if (query.AvgLogicalReads >= t.IoCriticalAvgLogicalReads)
        {
            return (
                Severity.High,
                Confidence.Medium,
                $"avg logical reads/exec {Grouped(query.AvgLogicalReads, 0)} >= "
                    + $"{Grouped(t.IoCriticalAvgLogicalReads, 0)} over "
                    + $"{query.ExecutionCount} executions");
        }

        if (query.AvgLogicalReads >= t.IoHighAvgLogicalReads)
        {
            return (
                Severity.Medium,
                Confidence.Medium,
                $"avg logical reads/exec {Grouped(query.AvgLogicalReads, 0)} >= "
                    + $"{Grouped(t.IoHighAvgLogicalReads, 0)} over "
                    + $"{query.ExecutionCount} executions");
        }

        // Large lifetime total. Only meaningful if each execution is itself non-trivial,
        // otherwise this is just a frequently-run cheap query.
        if (query.TotalLogicalReads >= t.IoCriticalTotalLogicalReads
            && query.AvgLogicalReads >= t.IoMinSignificantAvgReads)
        {
            return (
                Severity.Low,
                Confidence.Low,
                $"total logical reads {Grouped(query.TotalLogicalReads, 0)} >= "
                    + $"{Grouped(t.IoCriticalTotalLogicalReads, 0)} with avg "
                    + $"{Grouped(query.AvgLogicalReads, 0)}/exec; aggregate cost only");
        }

        return (null, Confidence.Low, string.Empty);
    }

    /// <summary>Formats with thousands separators, matching Python's <c>:,</c> format.</summary>
    private static string Grouped(double value, int decimals) =>
        value.ToString($"N{decimals}", CultureInfo.InvariantCulture);
}
