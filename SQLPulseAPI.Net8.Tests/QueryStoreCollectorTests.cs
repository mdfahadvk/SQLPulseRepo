using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Models.History;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Services.Performance;
using SQLPulseAPI.Net8.Services.QueryStore;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Query Store collector behaviour: availability mapping, and the guarantee that an
/// unavailable, disabled or permission-denied Query Store is reported as a status
/// rather than thrown at the caller.
/// </summary>
public class QueryStoreCollectorTests
{
    private const string Database = "AppDb";

    private static readonly DateTimeOffset Start = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);

    private static QueryStoreCollector Collector(ISqlQueryExecutor executor) =>
        new(
            executor,
            Options.Create(new SqlServerOptions
            {
                Server = "sql01",
                Username = "sqlpulse",
                Password = "irrelevant-for-tests",
                DefaultDatabase = "master",
            }),
            NullLogger<QueryStoreCollector>.Instance);

    private static SqlRow StatusRow(string actualState) => FakeSqlQueryExecutor.Row(
        ("actual_state_desc", actualState),
        ("desired_state_desc", actualState),
        ("readonly_reason", 0),
        ("current_storage_size_mb", 12L),
        ("max_storage_size_mb", 1024L));

    // --- Status mapping ------------------------------------------------------

    [Theory]
    [InlineData("READ_WRITE", QueryStoreStatus.Available)]
    [InlineData("READ_ONLY", QueryStoreStatus.AvailableReadOnly)]
    [InlineData("read_write", QueryStoreStatus.Available)]
    [InlineData("  READ_ONLY  ", QueryStoreStatus.AvailableReadOnly)]
    public async Task Accessible_states_carry_no_message(string actualState, QueryStoreStatus expected)
    {
        var executor = new FakeSqlQueryExecutor { Rows = [StatusRow(actualState)] };

        var (status, message) = await Collector(executor).CheckStatusAsync(Database);

        Assert.Equal(expected, status);
        Assert.Null(message);
        Assert.True(status.IsAccessible());
    }

    [Fact]
    public async Task Off_state_is_disabled_and_explains_why()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [StatusRow("OFF")] };

        var (status, message) = await Collector(executor).CheckStatusAsync(Database);

        Assert.Equal(QueryStoreStatus.Disabled, status);
        Assert.Equal($"Query Store is disabled (OFF) for database '{Database}'.", message);
        Assert.False(status.IsAccessible());
    }

    [Fact]
    public async Task Unrecognised_state_is_reported_verbatim_not_worked_around()
    {
        // ERROR and TRANSITIONING are recoverable only by an administrative write, which
        // SQLPulse must never perform, so the state is surfaced instead.
        var executor = new FakeSqlQueryExecutor { Rows = [StatusRow("ERROR")] };

        var (status, message) = await Collector(executor).CheckStatusAsync(Database);

        Assert.Equal(QueryStoreStatus.Disabled, status);
        Assert.Equal($"Query Store is in state 'ERROR' for database '{Database}'.", message);
    }

    [Fact]
    public async Task No_options_row_is_disabled()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [] };

        var (status, message) = await Collector(executor).CheckStatusAsync(Database);

        Assert.Equal(QueryStoreStatus.Disabled, status);
        Assert.Equal($"Query Store options not found for database '{Database}'.", message);
    }

    [Fact]
    public async Task Permission_failure_is_distinguished_from_a_generic_failure()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnExecute = new InvalidOperationException(
                "VIEW DATABASE STATE permission was denied on object 'AppDb'."),
        };

        var (status, message) = await Collector(executor).CheckStatusAsync(Database);

        // An operator can act on a missing grant; they cannot act on "something failed".
        Assert.Equal(QueryStoreStatus.PermissionDenied, status);
        Assert.Equal($"Permission denied when checking Query Store for database '{Database}'.", message);
    }

    [Fact]
    public async Task Generic_failure_is_query_failed_and_leaks_nothing()
    {
        var executor = new FakeSqlQueryExecutor
        {
            ThrowOnExecute = new TimeoutException(
                "Timeout expired connecting to MSI-STGERPDBL as sqlpulse_svc."),
        };

        var (status, message) = await Collector(executor).CheckStatusAsync(Database);

        Assert.Equal(QueryStoreStatus.QueryFailed, status);
        Assert.Equal($"Query Store availability check failed for database '{Database}'.", message);
        Assert.DoesNotContain("MSI-STGERPDBL", message);
        Assert.DoesNotContain("sqlpulse_svc", message);
    }

    [Fact]
    public async Task Invalid_database_name_never_reaches_sql_server()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [StatusRow("READ_WRITE")] };

        var (status, message) = await Collector(executor).CheckStatusAsync("App]; DROP DATABASE X--");

        Assert.Equal(QueryStoreStatus.QueryFailed, status);
        Assert.Equal("Invalid database name", message);
        Assert.Empty(executor.ExecutedQueries);
    }

    // --- Collection ----------------------------------------------------------

    [Fact]
    public async Task Collection_is_skipped_entirely_when_query_store_is_disabled()
    {
        var executor = new FakeSqlQueryExecutor { Rows = [StatusRow("OFF")] };

        var result = await Collector(executor).CollectSlowHistoryAsync(Database, Start, End, 5000);

        Assert.Equal(QueryStoreStatus.Disabled, result.Status);
        Assert.Empty(result.Records);
        Assert.Equal($"Query Store is disabled (OFF) for database '{Database}'.", result.Message);

        // Only the status query ran: there is no point asking for data that cannot exist.
        Assert.Single(executor.ExecutedQueries);
    }

    [Fact]
    public async Task Collection_maps_rows_and_binds_the_window_and_threshold()
    {
        var executor = new FakeSqlQueryExecutor
        {
            RowsForQuery = sql => sql.Contains("database_query_store_options", StringComparison.Ordinal)
                ? [StatusRow("READ_WRITE")]
                : [SlowRow()],
        };

        var result = await Collector(executor).CollectSlowHistoryAsync(Database, Start, End, 5000, topN: 7);

        Assert.Equal(QueryStoreStatus.Available, result.Status);
        Assert.Null(result.Message);

        var record = Assert.Single(result.Records);
        Assert.Equal(42L, record.QueryId);
        Assert.Equal("0xABCD", record.QueryHash);
        Assert.Equal("dbo.uspGetOrders", record.ProcedureName);
        Assert.Equal(9749.0, record.MaxDurationMs);
        Assert.Equal(3L, record.ExecutionCount);

        // Query Store carries no session metadata, and the collector must not invent any.
        Assert.Null(record.ProgramName);
        Assert.Null(record.HostName);
        Assert.Null(record.IsScheduledJob);

        Assert.Contains("TOP (7)", executor.ExecutedQueries[1], StringComparison.Ordinal);
        Assert.Equal(Database, executor.ExecutedDatabases[1]);

        var parameters = executor.ExecutedParameters[1];
        Assert.NotNull(parameters);
        Assert.Equal(Start, parameters[0]);
        Assert.Equal(End, parameters[1]);
        Assert.Equal(5000.0, parameters[2]);
    }

    [Fact]
    public async Task Collection_permission_failure_degrades_to_a_status()
    {
        var executor = new FakeSqlQueryExecutor
        {
            RowsForQuery = sql => sql.Contains("database_query_store_options", StringComparison.Ordinal)
                ? [StatusRow("READ_WRITE")]
                : throw new InvalidOperationException("SELECT permission was denied on 'sys.query_store_query'."),
        };

        var result = await Collector(executor).CollectSlowHistoryAsync(Database, Start, End, 5000);

        Assert.Equal(QueryStoreStatus.PermissionDenied, result.Status);
        Assert.Empty(result.Records);
        Assert.Equal($"Permission denied when reading Query Store data for database '{Database}'.", result.Message);
    }

    [Fact]
    public async Task Collection_generic_failure_degrades_to_a_status()
    {
        var executor = new FakeSqlQueryExecutor
        {
            RowsForQuery = sql => sql.Contains("database_query_store_options", StringComparison.Ordinal)
                ? [StatusRow("READ_WRITE")]
                : throw new TimeoutException("Execution timeout expired."),
        };

        var result = await Collector(executor).CollectSlowHistoryAsync(Database, Start, End, 5000);

        Assert.Equal(QueryStoreStatus.QueryFailed, result.Status);
        Assert.Empty(result.Records);
        Assert.Equal($"Query Store data collection failed for database '{Database}'.", result.Message);
    }

    // --- Read-only guarantee -------------------------------------------------

    [Fact]
    public void Query_store_sql_never_changes_query_store_configuration()
    {
        foreach (var sql in new[] { PerformanceQueries.QueryStoreStatus, PerformanceQueries.QueryStoreSlowHistory })
        {
            Assert.DoesNotContain("ALTER DATABASE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("QUERY_STORE =", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(PerformanceQueries.MonitoringMarker, sql, StringComparison.Ordinal);
        }
    }

    private static SqlRow SlowRow() => FakeSqlQueryExecutor.Row(
        ("query_id", 42L),
        ("query_sql_text", "SELECT * FROM Orders"),
        ("query_hash", "0xABCD"),
        ("object_id", 991L),
        ("procedure_name", "dbo.uspGetOrders"),
        ("plan_id", 7L),
        ("execution_count", 3L),
        ("avg_duration_ms", 6100.0),
        ("max_duration_ms", 9749.0),
        ("min_duration_ms", 5200.0),
        ("avg_cpu_time_ms", 1200.0),
        ("max_cpu_time_ms", 2400.0),
        ("avg_logical_reads", 5000.0),
        ("max_logical_reads", 9000.0),
        ("avg_physical_reads", 10.0),
        ("first_execution_time", new DateTime(2026, 8, 24, 9, 5, 0, DateTimeKind.Utc)),
        ("last_execution_time", new DateTime(2026, 8, 24, 10, 55, 0, DateTimeKind.Utc)),
        ("interval_start_time", new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc)));
}
