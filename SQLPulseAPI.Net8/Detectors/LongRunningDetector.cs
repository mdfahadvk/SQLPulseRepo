using System.Globalization;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Detectors;

/// <summary>
/// Detects queries running longer than the configured elapsed-time thresholds.
/// Ported from <c>LongRunningDetector</c> in <c>app/detectors/long_running.py</c>.
/// </summary>
public sealed class LongRunningDetector(IOptions<DetectorThresholdOptions> thresholds)
    : DetectorBase(thresholds.Value)
{
    /// <summary>
    /// SQL Server system/background programs that must not generate findings.
    /// They are always present and are not actionable.
    /// </summary>
    private static readonly HashSet<string> SystemProgramNames = new(StringComparer.Ordinal)
    {
        "sp_server_diagnostics",
        "SQLAgent - TSQL JobStep",
    };

    /// <summary>SQL fragments that identify a system/background task.</summary>
    private static readonly string[] SystemSqlPatterns =
    [
        "sp_server_diagnostics",
        "WAITFOR(RECEIVE",
        "BROKER",
        "xp_sqlagent_notify",
    ];

    /// <summary>Wait types indicating an idle or sleeping system task, not a real query.</summary>
    private static readonly HashSet<string> BackgroundWaitTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SP_SERVER_DIAGNOSTICS_SLEEP",
        "WAITFOR",
        "BROKER_RECEIVE_WAITFOR",
        "LAZYWRITER_SLEEP",
        "REQUEST_FOR_DEADLOCK_SEARCH",
        "XE_TIMER_EVENT",
        "SLEEP_TASK",
        "SLEEP_SYSTEMTASK",
    };

    /// <inheritdoc />
    public override string Name => "long_running_detector";

    /// <summary>Returns true when the query is a known system/background task.</summary>
    /// <remarks>
    /// Deliberately narrow: it never excludes a query merely for touching <c>sys.*</c>
    /// objects, because applications read system catalog views legitimately.
    /// </remarks>
    public static bool IsSystemBackgroundQuery(LongRunningQuery query)
    {
        if (query.ProgramName is not null && SystemProgramNames.Contains(query.ProgramName.Trim()))
        {
            return true;
        }

        if (query.WaitType is not null && BackgroundWaitTypes.Contains(query.WaitType))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(query.SqlText))
        {
            foreach (var pattern in SystemSqlPatterns)
            {
                if (query.SqlText.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override DetectionResult Detect(PerformanceSnapshot snapshot)
    {
        var section = snapshot.LongRunningQueries;
        if (section is null || !section.Metadata.Success || section.Queries.Count == 0)
        {
            return DetectionResult.Empty;
        }

        var queries = section.Queries.Where(query => !IsSystemBackgroundQuery(query)).ToList();
        if (queries.Count == 0)
        {
            return DetectionResult.Empty;
        }

        // Database-scope isolation: a database-scoped analysis must not report another
        // database's activity. Queries with no resolved database are kept, because the
        // snapshot was already collected against the target database.
        var targetDatabase = snapshot.Database;
        if (!string.IsNullOrEmpty(targetDatabase))
        {
            queries = queries
                .Where(query => string.IsNullOrEmpty(query.DatabaseName)
                    || string.Equals(query.DatabaseName, targetDatabase, StringComparison.Ordinal))
                .ToList();

            if (queries.Count == 0)
            {
                return DetectionResult.Empty;
            }
        }

        var t = Thresholds;
        var now = Now();
        var findings = new List<PerformanceFinding>();

        var criticalQueries = queries
            .Where(query => query.ElapsedTimeMs >= t.LongRunningCriticalMs)
            .ToList();

        var highQueries = queries
            .Where(query => query.ElapsedTimeMs >= t.LongRunningHighMs
                && query.ElapsedTimeMs < t.LongRunningCriticalMs)
            .ToList();

        var mediumQueries = queries
            .Where(query => query.ElapsedTimeMs >= t.LongRunningWarningMs
                && query.ElapsedTimeMs < t.LongRunningHighMs)
            .ToList();

        if (criticalQueries.Count >= t.LongRunningCriticalCount)
        {
            // Several queries stuck at once is a different problem from one slow query.
            findings.Add(BuildFinding(
                Severity.Critical,
                Confidence.High,
                criticalQueries,
                snapshot,
                now,
                $"{criticalQueries.Count} queries running longer than "
                    + $"{Seconds(t.LongRunningCriticalMs, 0)}s detected concurrently"));
        }
        else if (criticalQueries.Count > 0)
        {
            // A single critical-duration query is serious but not yet an incident.
            foreach (var query in criticalQueries)
            {
                findings.Add(BuildFinding(
                    Severity.High,
                    Confidence.Medium,
                    [query],
                    snapshot,
                    now,
                    $"Query on session {query.SessionId} running for "
                        + $"{Seconds(query.ElapsedTimeMs, 1)}s "
                        + $"(threshold: {Seconds(t.LongRunningCriticalMs, 0)}s)"));
            }
        }

        foreach (var query in highQueries)
        {
            findings.Add(BuildFinding(
                Severity.Medium,
                Confidence.Medium,
                [query],
                snapshot,
                now,
                $"Query on session {query.SessionId} running for "
                    + $"{Seconds(query.ElapsedTimeMs, 1)}s "
                    + $"(threshold: {Seconds(t.LongRunningHighMs, 0)}s)"));
        }

        // A few mid-tier queries are normal; several at once is worth noting once.
        if (mediumQueries.Count >= 3)
        {
            findings.Add(BuildFinding(
                Severity.Low,
                Confidence.Medium,
                mediumQueries,
                snapshot,
                now,
                $"{mediumQueries.Count} queries running between "
                    + $"{Seconds(t.LongRunningWarningMs, 0)}s and "
                    + $"{Seconds(t.LongRunningHighMs, 0)}s"));
        }

        return new DetectionResult(findings, []);
    }

    private static PerformanceFinding BuildFinding(
        Severity severity,
        Confidence confidence,
        IReadOnlyList<LongRunningQuery> queries,
        PerformanceSnapshot snapshot,
        DateTimeOffset now,
        string summary)
    {
        var evidence = queries.Select(query =>
        {
            var preview = Truncate(query.SqlText, 120);
            return $"Session {query.SessionId}: elapsed={query.ElapsedTimeMs}ms, "
                + $"cpu={query.CpuTimeMs}ms, reads={query.LogicalReads}, "
                + $"db={query.DatabaseName ?? "N/A"}, "
                + $"wait={query.WaitType ?? "none"} — {(preview.Length == 0 ? "N/A" : preview)}";
        }).ToList();

        // The query's own database wins; otherwise fall back to the analysis target.
        // Both may be null, which is reported as null rather than a placeholder.
        var findingDatabase = queries[0].DatabaseName ?? snapshot.Database;

        var waitTypes = queries
            .Select(query => query.WaitType)
            .Where(waitType => !string.IsNullOrEmpty(waitType))
            .Select(waitType => waitType!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(waitType => waitType, StringComparer.Ordinal)
            .ToList();

        var likelyCause = waitTypes.Count > 0
            ? $"Query is actively waiting on: {string.Join(", ", waitTypes)}. "
                + "The specific root cause requires execution plan analysis."
            : "Query has been executing beyond the expected threshold. "
                + "The specific root cause cannot be determined from elapsed time alone; "
                + "execution plan analysis is the recommended next diagnostic step.";

        return new PerformanceFinding
        {
            FindingId = NewFindingId(),
            Category = FindingCategory.LongRunningQuery,
            Server = snapshot.ServerHost,
            Database = findingDatabase,
            Severity = severity,
            Confidence = confidence,
            FirstObserved = now,
            LastObserved = now,
            Summary = summary,
            Evidence = evidence,
            LikelyCause = likelyCause,
            Recommendation =
                "Retrieve the execution plan for the running session via "
                + "sys.dm_exec_query_plan. Identify the most expensive operator "
                + "and determine whether it is blocked, scanning, or compute-bound.",
            ValidationSteps =
            [
                "Check sys.dm_exec_query_plan for the session's execution plan",
                "Examine the current wait type and resource to understand the bottleneck",
                "Check if the query is waiting on locks, I/O, or other resources",
                "Compare actual vs estimated rows in the plan to detect cardinality issues",
            ],
            HumanReviewRequired = severity is Severity.Critical or Severity.High,
        };
    }

    private static string Seconds(long milliseconds, int decimals) =>
        (milliseconds / 1000.0).ToString($"F{decimals}", CultureInfo.InvariantCulture);
}
