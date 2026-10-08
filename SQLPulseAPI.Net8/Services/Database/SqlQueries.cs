namespace SQLPulseAPI.Net8.Services.Database;

/// <summary>
/// Read-only SQL used for connectivity and discovery.
/// Ported verbatim from <c>app/database/queries.py</c>.
/// </summary>
public static class SqlQueries
{
    /// <summary>Minimal connectivity probe.</summary>
    public const string ConnectivityCheck = "SELECT 1 AS connected";

    /// <summary>SQL Server version banner.</summary>
    public const string ServerVersion = "SELECT @@VERSION AS version";

    /// <summary>Name of the database the connection is currently using.</summary>
    public const string CurrentDatabase = "SELECT DB_NAME() AS database_name";

    /// <summary>
    /// User databases that are online and accessible to the monitoring login.
    /// </summary>
    /// <remarks>
    /// <c>database_id &gt; 4</c> excludes master, tempdb, model and msdb.
    /// <c>HAS_DBACCESS</c> excludes databases the login cannot open, so an
    /// inaccessible database never appears as a monitoring target.
    /// </remarks>
    public const string DiscoverDatabases = """
        SELECT
            d.name,
            d.state_desc AS status
        FROM sys.databases d
        WHERE d.database_id > 4
            AND d.state_desc = 'ONLINE'
            AND HAS_DBACCESS(d.name) = 1
        ORDER BY d.name;
        """;
}
