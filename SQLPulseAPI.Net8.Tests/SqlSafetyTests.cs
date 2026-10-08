using SQLPulseAPI.Net8.Utilities;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Read-only enforcement. This is defence in depth — the SQL login itself is the
/// security boundary — but it must still reject every state-changing statement.
/// </summary>
public sealed class SqlSafetyTests
{
    [Theory]
    [InlineData("SELECT * FROM sys.dm_exec_requests")]
    [InlineData("  SELECT 1")]
    [InlineData("select 1 as connected")]
    [InlineData("\n\tSELECT name FROM sys.databases")]
    [InlineData("WITH blockers AS (SELECT 1 AS x) SELECT * FROM blockers")]
    [InlineData("(SELECT 1)")]
    public void Read_only_queries_are_allowed(string sql)
    {
        SqlSafety.EnsureReadOnly(sql);

        Assert.True(SqlSafety.IsReadOnly(sql));
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET x = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET t.x = 1")]
    [InlineData("ALTER DATABASE Sales SET OFFLINE")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("KILL 55")]
    [InlineData("GRANT VIEW SERVER STATE TO sqlpulse")]
    [InlineData("REVOKE VIEW SERVER STATE FROM sqlpulse")]
    [InlineData("DENY VIEW SERVER STATE TO sqlpulse")]
    [InlineData("DBCC FREEPROCCACHE")]
    [InlineData("DBCC SHRINKFILE (1, 1)")]
    [InlineData("sp_configure 'show advanced options', 1")]
    [InlineData("EXEC sp_configure")]
    [InlineData("EXECUTE sp_who2")]
    [InlineData("xp_cmdshell 'dir'")]
    [InlineData("BACKUP DATABASE Sales TO DISK = 'x'")]
    [InlineData("RESTORE DATABASE Sales FROM DISK = 'x'")]
    [InlineData("SHUTDOWN")]
    [InlineData("BULK INSERT t FROM 'f'")]
    public void State_changing_statements_are_rejected(string sql)
    {
        var exception = Assert.Throws<ReadOnlyViolationException>(() => SqlSafety.EnsureReadOnly(sql));

        Assert.Contains("Blocked", exception.Message, StringComparison.Ordinal);
        Assert.Contains("read-only", exception.Message, StringComparison.Ordinal);
        Assert.False(SqlSafety.IsReadOnly(sql));
    }

    [Theory]
    [InlineData("insert into t values (1)")]
    [InlineData("DrOp TaBlE t")]
    [InlineData("   delete from t")]
    public void Blocking_is_case_and_whitespace_insensitive(string sql) =>
        Assert.Throws<ReadOnlyViolationException>(() => SqlSafety.EnsureReadOnly(sql));

    [Theory]
    [InlineData("SELECT 1; DROP TABLE t")]
    [InlineData("SELECT 1;DELETE FROM t")]
    [InlineData("SELECT 1; EXEC sp_configure")]
    public void Mutating_statements_cannot_hide_behind_a_leading_select(string sql) =>
        Assert.Throws<ReadOnlyViolationException>(() => SqlSafety.EnsureReadOnly(sql));

    [Theory]
    [InlineData("-- SELECT 1\nDROP TABLE t")]
    [InlineData("/* SELECT 1 */ DELETE FROM t")]
    public void Mutating_statements_cannot_hide_behind_comments(string sql) =>
        Assert.Throws<ReadOnlyViolationException>(() => SqlSafety.EnsureReadOnly(sql));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_queries_are_rejected(string? sql) =>
        Assert.Throws<ReadOnlyViolationException>(() => SqlSafety.EnsureReadOnly(sql));

    [Fact]
    public void Every_requested_keyword_is_covered()
    {
        string[] required =
        [
            "INSERT", "UPDATE", "DELETE", "MERGE", "ALTER", "CREATE", "DROP",
            "TRUNCATE", "KILL", "GRANT", "REVOKE", "DENY", "DBCC",
        ];

        Assert.All(required, keyword => Assert.Contains(keyword, SqlSafety.BlockedPrefixes));

        // sp_configure and xp_cmdshell are covered by the SP_/XP_ prefixes.
        Assert.Contains("SP_", SqlSafety.BlockedPrefixes);
        Assert.Contains("XP_", SqlSafety.BlockedPrefixes);
    }

    [Fact]
    public void Discovery_and_connectivity_queries_pass_validation()
    {
        SqlSafety.EnsureReadOnly(Services.Database.SqlQueries.ConnectivityCheck);
        SqlSafety.EnsureReadOnly(Services.Database.SqlQueries.DiscoverDatabases);
        SqlSafety.EnsureReadOnly(Services.Database.SqlQueries.CurrentDatabase);
        SqlSafety.EnsureReadOnly(Services.Database.SqlQueries.ServerVersion);
    }
}
