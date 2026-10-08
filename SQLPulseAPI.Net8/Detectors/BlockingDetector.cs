using System.Globalization;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.Performance;

namespace SQLPulseAPI.Net8.Detectors;

/// <summary>
/// Detects significant blocking based on wait time and chain size.
/// Ported from <c>BlockingDetector</c> in <c>app/detectors/blocking.py</c>.
/// </summary>
public sealed class BlockingDetector(IOptions<DetectorThresholdOptions> thresholds)
    : DetectorBase(thresholds.Value)
{
    /// <inheritdoc />
    public override string Name => "blocking_detector";

    /// <inheritdoc />
    public override DetectionResult Detect(PerformanceSnapshot snapshot)
    {
        var section = snapshot.BlockingSessions;
        if (section is null || !section.Metadata.Success || section.Sessions.Count == 0)
        {
            return DetectionResult.Empty;
        }

        var sessions = section.Sessions;
        var t = Thresholds;
        var now = Now();

        var headBlockers = sessions.Count(session => session.IsHeadBlocker);
        var maxWaitMs = sessions.Max(session => session.WaitTimeMs);
        var totalBlocked = sessions.Count;

        Severity severity;
        Confidence confidence;

        // A long wait and a wide chain are both escalation triggers: many sessions
        // blocked briefly is as disruptive as one session blocked for a long time.
        if (maxWaitMs >= t.BlockingCriticalWaitMs || totalBlocked >= t.BlockingCriticalChainCount)
        {
            severity = Severity.Critical;
            confidence = Confidence.High;
        }
        else if (maxWaitMs >= t.BlockingHighWaitMs)
        {
            severity = Severity.High;
            confidence = Confidence.High;
        }
        else if (maxWaitMs >= t.BlockingWarningWaitMs)
        {
            severity = Severity.Medium;
            confidence = Confidence.High;
        }
        else
        {
            // Below every threshold — brief blocking is normal and not a finding.
            return DetectionResult.Empty;
        }

        var evidence = sessions.Select(session =>
        {
            var blockerInfo = session.BlockingSessionId is > 0
                ? $"blocked by session {session.BlockingSessionId}"
                : "HEAD BLOCKER";
            var preview = Truncate(session.SqlText, 100);

            return $"Session {session.SessionId} ({blockerInfo}): "
                + $"wait={session.WaitTimeMs}ms, type={session.WaitType ?? "N/A"}, "
                + $"resource={session.WaitResource ?? "N/A"} — {(preview.Length == 0 ? "N/A" : preview)}";
        }).ToList();

        var summary = $"Blocking chain detected: {headBlockers} head blocker(s), "
            + $"{totalBlocked} total session(s) involved, "
            + $"max wait time {(maxWaitMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture)}s";

        var finding = new PerformanceFinding
        {
            FindingId = NewFindingId(),
            Category = FindingCategory.Blocking,
            Server = snapshot.ServerHost,
            // Blocking is reported against the analysis target; it stays null at server
            // scope rather than guessing which database owns the contention.
            Database = snapshot.Database,
            Severity = severity,
            Confidence = confidence,
            FirstObserved = now,
            LastObserved = now,
            Summary = summary,
            Evidence = evidence,
            LikelyCause =
                "A session is holding locks that other sessions need. "
                + "The specific root cause (long transaction, application logic, "
                + "lock escalation) requires examining the head blocker's session state.",
            Recommendation =
                "Identify the head blocker's query and determine why it holds locks. "
                + "Check for uncommitted transactions via DBCC OPENTRAN, "
                + "then examine the blocker's execution plan and session state.",
            ValidationSteps =
            [
                "Identify the head blocker session and its current query",
                "Check if the head blocker has open uncommitted transactions",
                "Review the lock resource to determine the contention point",
                "Check if the blocking query can be optimized or split",
            ],
            HumanReviewRequired = severity is Severity.Critical or Severity.High,
        };

        return new DetectionResult([finding], []);
    }
}
