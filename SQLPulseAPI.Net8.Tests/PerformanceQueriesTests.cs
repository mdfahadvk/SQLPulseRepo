using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Utilities;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Guards the ported SQL: every statement must stay read-only, carry the monitoring
/// marker, and expose exactly the parameters the collectors bind.
/// </summary>
public class PerformanceQueriesTests
{
    public static TheoryData<string, string> AllQueries() => new()
    {
        { nameof(PerformanceQueries.ActiveRequests), PerformanceQueries.ActiveRequests },
        { nameof(PerformanceQueries.ActiveRequestsByDatabase), PerformanceQueries.ActiveRequestsByDatabase },
        { nameof(PerformanceQueries.LongRunningQueries), PerformanceQueries.LongRunningQueries },
        { nameof(PerformanceQueries.LongRunningQueriesByDatabase), PerformanceQueries.LongRunningQueriesByDatabase },
        { nameof(PerformanceQueries.BlockingSessions), PerformanceQueries.BlockingSessions },
        { nameof(PerformanceQueries.BlockingSessionsByDatabase), PerformanceQueries.BlockingSessionsByDatabase },
        { nameof(PerformanceQueries.TopCpuQueries), PerformanceQueries.TopCpuQueries },
        { nameof(PerformanceQueries.TopCpuQueriesByDatabase), PerformanceQueries.TopCpuQueriesByDatabase },
        { nameof(PerformanceQueries.TopIoQueries), PerformanceQueries.TopIoQueries },
        { nameof(PerformanceQueries.TopIoQueriesByDatabase), PerformanceQueries.TopIoQueriesByDatabase },
        { nameof(PerformanceQueries.WaitStatistics), PerformanceQueries.WaitStatistics },
        { nameof(PerformanceQueries.WaitStatisticsSummary), PerformanceQueries.WaitStatisticsSummary },
    };

    [Theory]
    [MemberData(nameof(AllQueries))]
    public void Every_query_is_read_only(string name, string sql)
    {
        // A mutating statement must never be able to reach SQL Server through a collector.
        Assert.True(SqlSafety.IsReadOnly(PerformanceQueries.WithTopN(sql, 10)), name);
    }

    [Theory]
    [MemberData(nameof(AllQueries))]
    public void Every_query_carries_the_monitoring_marker(string name, string sql)
    {
        // The marker is how SQLPulse recognises its own plan-cache entries later.
        Assert.True(sql.Contains(QueryFilters.MonitoringQueryMarker, StringComparison.Ordinal), name);
    }

    [Theory]
    [InlineData(nameof(PerformanceQueries.ActiveRequestsByDatabase))]
    [InlineData(nameof(PerformanceQueries.BlockingSessionsByDatabase))]
    [InlineData(nameof(PerformanceQueries.TopCpuQueriesByDatabase))]
    [InlineData(nameof(PerformanceQueries.TopIoQueriesByDatabase))]
    public void Database_scoped_queries_bind_the_database_as_a_parameter(string name)
    {
        var sql = name switch
        {
            nameof(PerformanceQueries.ActiveRequestsByDatabase) => PerformanceQueries.ActiveRequestsByDatabase,
            nameof(PerformanceQueries.BlockingSessionsByDatabase) => PerformanceQueries.BlockingSessionsByDatabase,
            nameof(PerformanceQueries.TopCpuQueriesByDatabase) => PerformanceQueries.TopCpuQueriesByDatabase,
            _ => PerformanceQueries.TopIoQueriesByDatabase,
        };

        // The database name is never concatenated into the statement.
        Assert.Contains("@p0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_running_by_database_binds_threshold_then_database()
    {
        var sql = PerformanceQueries.LongRunningQueriesByDatabase;

        Assert.Contains("r.total_elapsed_time >= @p0", sql, StringComparison.Ordinal);
        Assert.Contains("DB_NAME(r.database_id) = @p1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_queries_exclude_the_monitoring_session_and_system_processes()
    {
        // This is what keeps SQLPulse's own session and background tasks out of the results.
        Assert.Contains("r.session_id <> @@SPID", PerformanceQueries.ActiveRequests, StringComparison.Ordinal);
        Assert.Contains("s.is_user_process = 1", PerformanceQueries.ActiveRequests, StringComparison.Ordinal);
        Assert.Contains("r.session_id <> @@SPID", PerformanceQueries.LongRunningQueries, StringComparison.Ordinal);
        Assert.Contains("s.is_user_process = 1", PerformanceQueries.LongRunningQueries, StringComparison.Ordinal);
    }

    [Fact]
    public void Wait_statistics_excludes_benign_idle_waits()
    {
        Assert.Contains("waiting_tasks_count > 0", PerformanceQueries.WaitStatistics, StringComparison.Ordinal);
        Assert.Contains("LAZYWRITER_SLEEP", PerformanceQueries.WaitStatistics, StringComparison.Ordinal);
        Assert.Contains("SP_SERVER_DIAGNOSTICS_SLEEP", PerformanceQueries.WaitStatistics, StringComparison.Ordinal);
    }

    [Fact]
    public void Wait_statistics_summary_reports_the_counter_epoch()
    {
        // Cumulative counters are meaningless without knowing when they were reset.
        Assert.Contains("sqlserver_start_time", PerformanceQueries.WaitStatisticsSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTopN_substitutes_the_row_limit()
    {
        var sql = PerformanceQueries.WithTopN(PerformanceQueries.TopCpuQueries, 7);

        Assert.Contains("SELECT TOP (7)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("{top_n}", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PerformanceQueries.MaxTopN + 1)]
    public void WithTopN_rejects_out_of_range_limits(int topN)
    {
        // TOP is not parameterizable, so the value is range-checked before formatting.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PerformanceQueries.WithTopN(PerformanceQueries.TopCpuQueries, topN));
    }
}
