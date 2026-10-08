using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Detectors;
using SQLPulseAPI.Net8.Models.Performance;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Shared snapshot builders for the detector and analysis-engine tests.
/// </summary>
internal static class SnapshotBuilder
{
    public const string Server = "sql01";

    public static DetectorThresholdOptions Thresholds { get; } = new();

    public static IOptions<DetectorThresholdOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(Thresholds);

    public static PerformanceSnapshot Snapshot(
        AnalysisScope scope = AnalysisScope.Server,
        string? database = null) => new()
        {
            SnapshotId = Guid.NewGuid().ToString(),
            ServerHost = Server,
            Scope = scope,
            Database = database,
        };

    public static CollectorMetadata Ok(string name) => new()
    {
        CollectorName = name,
        ServerHost = Server,
        Success = true,
    };

    public static LongRunningQuery LongRunning(
        int sessionId,
        long elapsedMs,
        string? database = "AppDb",
        string? waitType = "LCK_M_S",
        string? programName = "MyApp",
        string? sqlText = "SELECT * FROM dbo.Orders") => new()
        {
            SessionId = sessionId,
            ElapsedTimeMs = elapsedMs,
            CpuTimeMs = 100,
            LogicalReads = 500,
            DatabaseName = database,
            WaitType = waitType,
            ProgramName = programName,
            SqlText = sqlText,
            Status = "running",
        };

    public static void WithLongRunning(this PerformanceSnapshot snapshot, params LongRunningQuery[] queries) =>
        snapshot.LongRunningQueries = new LongRunningQueriesResult
        {
            Metadata = Ok("long_running_queries"),
            ThresholdMs = 5000,
            Queries = [.. queries],
        };

    public static BlockingSession Blocking(
        int sessionId,
        long waitMs,
        int? blockedBy = null,
        bool isHeadBlocker = false) => new()
        {
            SessionId = sessionId,
            BlockingSessionId = blockedBy,
            WaitTimeMs = waitMs,
            WaitType = "LCK_M_X",
            WaitResource = "KEY: 5:72057594043236352",
            SqlText = "UPDATE dbo.Orders SET Status = 1",
            DatabaseName = "AppDb",
            IsHeadBlocker = isHeadBlocker,
            Status = "suspended",
        };

    public static void WithBlocking(this PerformanceSnapshot snapshot, params BlockingSession[] sessions) =>
        snapshot.BlockingSessions = new BlockingSessionsResult
        {
            Metadata = Ok("blocking_sessions"),
            Sessions = [.. sessions],
            BlockingChainCount = sessions.Length,
        };

    public static TopCpuQuery Cpu(
        double avgCpuMs,
        long executionCount = 100,
        string? database = "AppDb",
        string? queryHash = "0xAAA",
        string? planHash = "0xPPP",
        long? totalCpuMs = null,
        string? sqlText = "SELECT SUM(Total) FROM dbo.Orders") => new()
        {
            AvgCpuTimeMs = avgCpuMs,
            TotalCpuTimeMs = totalCpuMs ?? (long)(avgCpuMs * executionCount),
            ExecutionCount = executionCount,
            AvgElapsedTimeMs = avgCpuMs * 1.2,
            TotalElapsedTimeMs = (long)(avgCpuMs * executionCount * 1.2),
            DatabaseName = database,
            QueryHash = queryHash,
            QueryPlanHash = planHash,
            ObjectName = "dbo.GetOrderTotals",
            SqlText = sqlText,
        };

    public static void WithCpu(this PerformanceSnapshot snapshot, params TopCpuQuery[] queries) =>
        snapshot.TopCpuQueries = new TopCpuQueriesResult
        {
            Metadata = Ok("top_cpu_queries"),
            TopN = 20,
            Queries = [.. queries],
        };

    public static TopIoQuery Io(
        double avgLogicalReads,
        long executionCount = 100,
        string? database = "AppDb",
        string? queryHash = "0xAAA",
        string? planHash = "0xPPP",
        long? totalLogicalReads = null,
        string? sqlText = "SELECT SUM(Total) FROM dbo.Orders") => new()
        {
            AvgLogicalReads = avgLogicalReads,
            TotalLogicalReads = totalLogicalReads ?? (long)(avgLogicalReads * executionCount),
            ExecutionCount = executionCount,
            AvgPhysicalReads = avgLogicalReads / 10,
            TotalPhysicalReads = (long)(avgLogicalReads * executionCount / 10),
            AvgLogicalWrites = 0,
            TotalLogicalWrites = 0,
            TotalElapsedTimeMs = 1000,
            DatabaseName = database,
            QueryHash = queryHash,
            QueryPlanHash = planHash,
            ObjectName = "dbo.GetOrderTotals",
            SqlText = sqlText,
        };

    public static void WithIo(this PerformanceSnapshot snapshot, params TopIoQuery[] queries) =>
        snapshot.TopIoQueries = new TopIoQueriesResult
        {
            Metadata = Ok("top_io_queries"),
            TopN = 20,
            Queries = [.. queries],
        };
}

/// <summary>
/// Behaviour of the long-running query detector.
/// </summary>
public sealed class LongRunningDetectorTests
{
    private static readonly LongRunningDetector Detector = new(SnapshotBuilder.Options);

    [Fact]
    public void Missing_section_produces_nothing()
    {
        var result = Detector.Detect(SnapshotBuilder.Snapshot());

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Failed_collector_produces_nothing()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.LongRunningQueries = new LongRunningQueriesResult
        {
            Metadata = new CollectorMetadata { CollectorName = "long_running_queries", Success = false },
            Queries = [SnapshotBuilder.LongRunning(51, 200_000)],
        };

        // A collector that could not read is not evidence of a healthy server, so it
        // must not be reported as one either.
        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Fact]
    public void Query_below_the_warning_threshold_produces_nothing()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(51, 9_000));

        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Fact]
    public void Single_critical_query_is_high_not_critical()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(51, 150_000));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        // One stuck query is serious, but CRITICAL is reserved for several at once.
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(Confidence.Medium, finding.Confidence);
        Assert.True(finding.HumanReviewRequired);
    }

    [Fact]
    public void Three_critical_queries_collapse_into_one_critical_finding()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(
            SnapshotBuilder.LongRunning(51, 150_000),
            SnapshotBuilder.LongRunning(52, 160_000),
            SnapshotBuilder.LongRunning(53, 170_000));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Equal(Confidence.High, finding.Confidence);
        Assert.Equal(3, finding.Evidence.Count);
    }

    [Fact]
    public void High_tier_query_is_medium()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(51, 45_000));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.False(finding.HumanReviewRequired);
    }

    [Fact]
    public void Two_medium_queries_are_not_reported_but_three_are()
    {
        var two = SnapshotBuilder.Snapshot();
        two.WithLongRunning(
            SnapshotBuilder.LongRunning(51, 12_000),
            SnapshotBuilder.LongRunning(52, 13_000));

        Assert.Empty(Detector.Detect(two).Findings);

        var three = SnapshotBuilder.Snapshot();
        three.WithLongRunning(
            SnapshotBuilder.LongRunning(51, 12_000),
            SnapshotBuilder.LongRunning(52, 13_000),
            SnapshotBuilder.LongRunning(53, 14_000));

        var finding = Assert.Single(Detector.Detect(three).Findings);
        Assert.Equal(Severity.Low, finding.Severity);
    }

    [Theory]
    [InlineData("sp_server_diagnostics", null, "SELECT 1")]
    [InlineData("MyApp", "BROKER_RECEIVE_WAITFOR", "SELECT 1")]
    [InlineData("MyApp", "LCK_M_S", "WAITFOR(RECEIVE TOP(1) * FROM queue)")]
    public void System_background_tasks_are_never_reported(string program, string? waitType, string sql)
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(
            SnapshotBuilder.LongRunning(51, 500_000, programName: program, waitType: waitType, sqlText: sql));

        // These are always present on a healthy instance and are not actionable.
        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Fact]
    public void Database_scope_ignores_other_databases()
    {
        var snapshot = SnapshotBuilder.Snapshot(AnalysisScope.Database, "AppDb");
        snapshot.WithLongRunning(
            SnapshotBuilder.LongRunning(51, 150_000, database: "OtherDb"),
            SnapshotBuilder.LongRunning(52, 150_000, database: "AppDb"));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Contains("Session 52", finding.Evidence[0], StringComparison.Ordinal);
        Assert.Equal("AppDb", finding.Database);
    }

    [Fact]
    public void Unresolved_database_stays_null()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(51, 150_000, database: null));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        // SQLPulse reports what it knows. It never substitutes a placeholder name that
        // an operator could mistake for a real database.
        Assert.Null(finding.Database);
    }

    [Fact]
    public void Finding_carries_evidence_cause_and_validation_steps()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithLongRunning(SnapshotBuilder.LongRunning(51, 150_000));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.NotEmpty(finding.Evidence);
        Assert.NotEmpty(finding.LikelyCause);
        Assert.NotEmpty(finding.Recommendation);
        Assert.NotEmpty(finding.ValidationSteps);
        Assert.Contains("LCK_M_S", finding.LikelyCause, StringComparison.Ordinal);
        Assert.Equal(FindingCategory.LongRunningQuery, finding.Category);
    }
}

/// <summary>
/// Behaviour of the blocking detector.
/// </summary>
public sealed class BlockingDetectorTests
{
    private static readonly BlockingDetector Detector = new(SnapshotBuilder.Options);

    [Fact]
    public void No_blocking_produces_nothing()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking();

        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Fact]
    public void Brief_blocking_is_not_a_finding()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking(SnapshotBuilder.Blocking(51, 1_000, blockedBy: 60));

        // Short lock waits are normal in any concurrent workload.
        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Theory]
    [InlineData(6_000, Severity.Medium)]
    [InlineData(31_000, Severity.High)]
    [InlineData(61_000, Severity.Critical)]
    public void Wait_time_drives_severity(long waitMs, Severity expected)
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking(SnapshotBuilder.Blocking(51, waitMs, blockedBy: 60));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Equal(expected, finding.Severity);
        Assert.Equal(Confidence.High, finding.Confidence);
    }

    [Fact]
    public void A_wide_chain_is_critical_even_when_waits_are_short()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking(
            SnapshotBuilder.Blocking(51, 6_000, isHeadBlocker: true),
            SnapshotBuilder.Blocking(52, 6_000, blockedBy: 51),
            SnapshotBuilder.Blocking(53, 6_000, blockedBy: 51));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        // Many sessions blocked at once is as disruptive as one long wait.
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.True(finding.HumanReviewRequired);
    }

    [Fact]
    public void Head_blocker_is_labelled_in_the_evidence()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking(
            SnapshotBuilder.Blocking(51, 61_000, isHeadBlocker: true),
            SnapshotBuilder.Blocking(52, 61_000, blockedBy: 51));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Contains(finding.Evidence, line => line.Contains("HEAD BLOCKER", StringComparison.Ordinal));
        Assert.Contains(finding.Evidence, line => line.Contains("blocked by session 51", StringComparison.Ordinal));
        Assert.Contains("1 head blocker(s)", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_scope_leaves_the_database_unset()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithBlocking(SnapshotBuilder.Blocking(51, 61_000, blockedBy: 60));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        // A chain can span databases, so SQLPulse does not pick one.
        Assert.Null(finding.Database);
    }

    [Fact]
    public void Database_scope_attributes_the_finding_to_the_target()
    {
        var snapshot = SnapshotBuilder.Snapshot(AnalysisScope.Database, "AppDb");
        snapshot.WithBlocking(SnapshotBuilder.Blocking(51, 61_000, blockedBy: 60));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Equal("AppDb", finding.Database);
    }
}

/// <summary>
/// Behaviour of the high-CPU detector.
/// </summary>
public sealed class HighCpuDetectorTests
{
    private static readonly HighCpuDetector Detector = new(SnapshotBuilder.Options);

    [Fact]
    public void Cheap_query_produces_nothing()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 50));

        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Fact]
    public void Too_few_executions_produces_nothing()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 40_000, executionCount: 2));

        // Two runs cannot distinguish a pattern from a one-off.
        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Theory]
    [InlineData(31_000, Severity.High, Confidence.Medium)]
    [InlineData(6_000, Severity.Medium, Confidence.Medium)]
    public void Per_execution_cost_drives_severity(double avgMs, Severity severity, Confidence confidence)
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: avgMs));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        // Never CRITICAL: plan-cache statistics describe lifetime cost, not a live incident.
        Assert.Equal(severity, finding.Severity);
        Assert.Equal(confidence, finding.Confidence);
        Assert.Equal(FindingCategory.HighCpu, finding.Category);
        Assert.Equal(new[] { FindingSignal.HighCpu }, finding.Signals);
    }

    [Fact]
    public void Aggregate_cost_alone_is_low_confidence_low_severity()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(
            avgCpuMs: 100,
            executionCount: 50_000,
            totalCpuMs: 5_000_000));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        // A large total only means the server ran a lot of work, not that the query is bad.
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal(Confidence.Low, finding.Confidence);
        Assert.Contains("aggregate cost only", finding.SeverityBasis!, StringComparison.Ordinal);
    }

    [Fact]
    public void Frequent_trivial_query_is_not_reported_on_totals_alone()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(
            avgCpuMs: 2,
            executionCount: 5_000_000,
            totalCpuMs: 10_000_000));

        // Below the per-execution floor: this is a busy query, not an inefficient one.
        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Fact]
    public void Unresolved_database_weakens_confidence_and_stays_null()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: null));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Null(finding.Database);
        Assert.Equal(Confidence.Low, finding.Confidence);
        Assert.Contains("database unresolved", finding.SeverityBasis!, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitoring_queries_are_excluded_and_reported()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(
            avgCpuMs: 31_000,
            sqlText: "SELECT * FROM sys.dm_exec_requests -- SQLPulse:monitoring"));

        var result = Detector.Detect(snapshot);

        // Recorded rather than silently dropped, so an operator can confirm the filter
        // is not hiding real workload.
        Assert.Empty(result.Findings);
        var excluded = Assert.Single(result.ExcludedQueries);
        Assert.NotEmpty(excluded.Reason);
    }

    [Fact]
    public void Database_scope_ignores_other_databases()
    {
        var snapshot = SnapshotBuilder.Snapshot(AnalysisScope.Database, "AppDb");
        snapshot.WithCpu(
            SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: "OtherDb"),
            SnapshotBuilder.Cpu(avgCpuMs: 31_000, database: "AppDb"));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Equal("AppDb", finding.Database);
    }

    [Fact]
    public void Finding_carries_query_identity()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithCpu(SnapshotBuilder.Cpu(avgCpuMs: 31_000, queryHash: "0xABC", planHash: "0xDEF"));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Equal("0xABC", finding.QueryIdentity!.QueryHash);
        Assert.Equal("0xDEF", finding.QueryIdentity.QueryPlanHash);
        Assert.NotEmpty(finding.ValidationSteps);
    }
}

/// <summary>
/// Behaviour of the high-I/O detector.
/// </summary>
public sealed class HighIoDetectorTests
{
    private static readonly HighIoDetector Detector = new(SnapshotBuilder.Options);

    [Fact]
    public void Cheap_query_produces_nothing()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 500));

        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Fact]
    public void Too_few_executions_produces_nothing()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000, executionCount: 2));

        Assert.Empty(Detector.Detect(snapshot).Findings);
    }

    [Theory]
    [InlineData(600_000, Severity.High)]
    [InlineData(60_000, Severity.Medium)]
    public void Per_execution_reads_drive_severity(double avgReads, Severity expected)
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: avgReads));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Equal(expected, finding.Severity);
        Assert.Equal(FindingCategory.HighIo, finding.Category);
        Assert.Equal(new[] { FindingSignal.HighIo }, finding.Signals);
    }

    [Fact]
    public void Aggregate_reads_alone_are_low_severity()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithIo(SnapshotBuilder.Io(
            avgLogicalReads: 1_000,
            executionCount: 200_000,
            totalLogicalReads: 200_000_000));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal(Confidence.Low, finding.Confidence);
    }

    [Fact]
    public void Unresolved_database_weakens_confidence_and_stays_null()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithIo(SnapshotBuilder.Io(avgLogicalReads: 600_000, database: null));

        var finding = Assert.Single(Detector.Detect(snapshot).Findings);

        Assert.Null(finding.Database);
        Assert.Equal(Confidence.Low, finding.Confidence);
    }

    [Fact]
    public void Monitoring_queries_are_excluded_and_reported()
    {
        var snapshot = SnapshotBuilder.Snapshot();
        snapshot.WithIo(SnapshotBuilder.Io(
            avgLogicalReads: 600_000,
            sqlText: "SELECT * FROM sys.dm_exec_query_stats -- SQLPulse:monitoring"));

        var result = Detector.Detect(snapshot);

        Assert.Empty(result.Findings);
        Assert.Single(result.ExcludedQueries);
    }
}
