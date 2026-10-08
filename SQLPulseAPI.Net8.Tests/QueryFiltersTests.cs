using SQLPulseAPI.Net8.Utilities;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Monitoring-query exclusion. The costly mistake here is over-filtering: hiding real
/// application workload is far worse than reporting a little diagnostic noise.
/// </summary>
public class QueryFiltersTests
{
    [Fact]
    public void SqlPulse_own_queries_are_excluded_by_marker()
    {
        var sql = "/* SQLPulse:monitoring */ SELECT 1";

        Assert.Equal(QueryFilters.ReasonSqlPulseSelf, QueryFilters.ClassifyExclusion(sql));
    }

    [Fact]
    public void Third_party_monitoring_queries_are_excluded_by_diagnostic_dmv()
    {
        var sql = "SELECT TOP 10 * FROM sys.dm_exec_query_stats ORDER BY total_worker_time DESC";

        Assert.Equal(QueryFilters.ReasonDiagnosticDmv, QueryFilters.ClassifyExclusion(sql));
    }

    [Theory]
    [InlineData("SELECT * FROM sys.objects WHERE type = 'U'")]
    [InlineData("SELECT c.name FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id")]
    [InlineData("SELECT * FROM sys.indexes")]
    [InlineData("SELECT SUM(rows) FROM sys.partitions")]
    public void Legitimate_queries_over_system_catalog_views_are_kept(string sql)
    {
        // ORMs and migration tools read these constantly; excluding them would hide
        // real business workload.
        Assert.Null(QueryFilters.ClassifyExclusion(sql));
    }

    [Fact]
    public void Application_queries_are_kept()
    {
        Assert.Null(QueryFilters.ClassifyExclusion("SELECT * FROM dbo.Orders WHERE CustomerId = @p0"));
    }

    [Fact]
    public void A_column_merely_containing_a_dmv_name_is_not_excluded()
    {
        // The reference must be schema-qualified, so an alias or column cannot trip the filter.
        Assert.Null(QueryFilters.ClassifyExclusion("SELECT dm_exec_requests FROM dbo.AuditLog"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_sql_text_is_kept(string? sql)
    {
        // Unknown means "real workload" — never hide something silently.
        Assert.Null(QueryFilters.ClassifyExclusion(sql));
        Assert.False(QueryFilters.IsExcluded(sql));
    }
}
