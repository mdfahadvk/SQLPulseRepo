using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLPulseAPI.Net8.Collectors;
using SQLPulseAPI.Net8.Configuration;
using SQLPulseAPI.Net8.Infrastructure;
using SQLPulseAPI.Net8.Services.Database;
using SQLPulseAPI.Net8.Tests.Fakes;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Collector behaviour: metric mapping, database attribution, and the guarantee that a
/// collector reports failure in its metadata instead of throwing.
/// </summary>
public class CollectorTests
{
    private const string Server = "sql01";
    private const string DefaultDatabase = "master";

    private static IOptions<SqlServerOptions> SqlServer() => Options.Create(new SqlServerOptions
    {
        Server = Server,
        Username = "sqlpulse",
        Password = "irrelevant-for-tests",
        DefaultDatabase = DefaultDatabase,
    });

    private static ActiveRequestsCollector ActiveRequests(ISqlQueryExecutor executor) =>
        new(executor, SqlServer(), NullLogger<ActiveRequestsCollector>.Instance);

    private static LongRunningQueriesCollector LongRunning(ISqlQueryExecutor executor) =>
        new(executor, SqlServer(), NullLogger<LongRunningQueriesCollector>.Instance);

    private static BlockingSessionsCollector Blocking(ISqlQueryExecutor executor) =>
        new(executor, SqlServer(), NullLogger<BlockingSessionsCollector>.Instance);

    private static TopCpuQueriesCollector TopCpu(ISqlQueryExecutor executor) =>
        new(executor, SqlServer(), NullLogger<TopCpuQueriesCollector>.Instance);

    private static TopIoQueriesCollector TopIo(ISqlQueryExecutor executor) =>
        new(executor, SqlServer(), NullLogger<TopIoQueriesCollector>.Instance);

    private static WaitStatisticsCollector Waits(ISqlQueryExecutor executor) =>
        new(executor, SqlServer(), NullLogger<WaitStatisticsCollector>.Instance);

    /// <summary>A permission failure the driver-agnostic classifier recognises.</summary>
    private static Exception PermissionDenied() =>
        new InvalidOperationException("VIEW SERVER STATE permission was denied on object 'server'.");

    /// <summary>A failure that leaks server and login names, so sanitization can be asserted.</summary>
    private static Exception LeakyFailure() =>
        new TimeoutException("Timeout expired connecting to MSI-STGERPDBL as sqlpulse_svc.");

    // --- Active requests -----------------------------------------------------

    [Fact]
    public async Task Active_requests_maps_every_metric()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows =
            [
                FakeSqlQueryExecutor.Row(
                    ("session_id", 55),
                    ("request_id", 0),
                    ("status", "running"),
                    ("command", "SELECT"),
                    ("sql_text", "SELECT * FROM dbo.Orders"),
                    ("database_name", "OrdersDb"),
                    ("wait_type", "PAGEIOLATCH_SH"),
                    ("wait_time_ms", 120L),
                    ("cpu_time_ms", 900L),
                    ("total_elapsed_time_ms", 4200L),
                    ("reads", 10L),
                    ("writes", 2L),
                    ("logical_reads", 5000L),
                    ("blocking_session_id", 54),
                    ("open_transaction_count", 1),
                    ("login_name", "app_user"),
                    ("host_name", "APPSRV1"),
                    ("program_name", ".Net SqlClient Data Provider")),
            ],
        };

        var result = await ActiveRequests(executor).CollectAsync();

        Assert.True(result.Metadata.Success);
        Assert.Equal("active_requests", result.Metadata.CollectorName);
        Assert.Equal(Server, result.Metadata.ServerHost);
        Assert.Equal(DefaultDatabase, result.Metadata.Database);

        var request = Assert.Single(result.Requests);
        Assert.Equal(55, request.SessionId);
        Assert.Equal("running", request.Status);
        Assert.Equal("OrdersDb", request.DatabaseName);
        Assert.Equal(4200L, request.TotalElapsedTimeMs);
        Assert.Equal(5000L, request.LogicalReads);
        Assert.Equal(54, request.BlockingSessionId);
        Assert.Equal("PAGEIOLATCH_SH", request.WaitType);
    }

    [Fact]
    public async Task Active_requests_uses_the_database_scoped_query_and_binds_the_name()
    {
        var executor = new FakeSqlQueryExecutor();

        var result = await ActiveRequests(executor).CollectAsync("OrdersDb");

        Assert.Contains("DB_NAME(r.database_id) = @p0", executor.ExecutedQueries[0], StringComparison.Ordinal);
        Assert.Equal("OrdersDb", Assert.Single(executor.ExecutedParameters[0]!));
        Assert.Equal("OrdersDb", executor.ExecutedDatabases[0]);
        Assert.Equal("OrdersDb", result.Metadata.Database);
    }

    [Fact]
    public async Task Active_requests_reports_failure_in_metadata_without_throwing()
    {
        var executor = new FakeSqlQueryExecutor { ThrowOnExecute = LeakyFailure() };

        var result = await ActiveRequests(executor).CollectAsync();

        Assert.False(result.Metadata.Success);
        Assert.False(result.Metadata.PermissionIssue);
        Assert.Equal(SqlErrorSanitizer.GenericFailureMessage, result.Metadata.ErrorMessage);
        Assert.Empty(result.Requests);
    }

    [Fact]
    public async Task Collector_error_message_never_leaks_server_or_login_names()
    {
        var executor = new FakeSqlQueryExecutor { ThrowOnExecute = LeakyFailure() };

        var result = await ActiveRequests(executor).CollectAsync();

        Assert.DoesNotContain("MSI-STGERPDBL", result.Metadata.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlpulse_svc", result.Metadata.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Permission_failures_are_flagged_as_permission_issues()
    {
        var executor = new FakeSqlQueryExecutor { ThrowOnExecute = PermissionDenied() };

        var result = await ActiveRequests(executor).CollectAsync();

        Assert.False(result.Metadata.Success);
        Assert.True(result.Metadata.PermissionIssue);
        Assert.Equal(SqlErrorSanitizer.PermissionDeniedMessage, result.Metadata.ErrorMessage);
    }

    // --- Long-running queries ------------------------------------------------

    [Fact]
    public async Task Long_running_binds_the_threshold_and_preserves_it_on_the_result()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows =
            [
                FakeSqlQueryExecutor.Row(
                    ("session_id", 61),
                    ("request_id", 0),
                    ("sql_text", "SELECT * FROM dbo.Invoices"),
                    ("database_name", "OrdersDb"),
                    ("elapsed_time_ms", 31000L),
                    ("cpu_time_ms", 2500L),
                    ("reads", 4L),
                    ("logical_reads", 900000L),
                    ("writes", 0L),
                    ("wait_type", null),
                    ("wait_time_ms", 0L),
                    ("status", "running"),
                    ("login_name", "app_user"),
                    ("host_name", "APPSRV1"),
                    ("program_name", "Orders API")),
            ],
        };

        var result = await LongRunning(executor).CollectAsync(30000);

        Assert.Equal(30000, result.ThresholdMs);
        Assert.Equal(30000, Assert.Single(executor.ExecutedParameters[0]!));
        Assert.Equal(31000L, Assert.Single(result.Queries).ElapsedTimeMs);
    }

    [Fact]
    public async Task Long_running_database_scope_binds_threshold_then_database()
    {
        var executor = new FakeSqlQueryExecutor();

        await LongRunning(executor).CollectAsync(5000, "OrdersDb");

        Assert.Equal(new object?[] { 5000, "OrdersDb" }, executor.ExecutedParameters[0]!);
    }

    [Fact]
    public async Task Long_running_keeps_the_threshold_on_failure()
    {
        var executor = new FakeSqlQueryExecutor { ThrowOnExecute = PermissionDenied() };

        var result = await LongRunning(executor).CollectAsync(12345);

        Assert.False(result.Metadata.Success);
        Assert.Equal(12345, result.ThresholdMs);
    }

    // --- Blocking ------------------------------------------------------------

    [Fact]
    public async Task Blocking_counts_head_blockers_as_chain_roots()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows =
            [
                BlockingRow(sessionId: 60, blockedBy: 59, isHeadBlocker: 1),
                BlockingRow(sessionId: 61, blockedBy: 60, isHeadBlocker: 0),
                BlockingRow(sessionId: 70, blockedBy: 69, isHeadBlocker: 1),
            ],
        };

        var result = await Blocking(executor).CollectAsync();

        Assert.Equal(3, result.Sessions.Count);
        Assert.Equal(2, result.BlockingChainCount);
        Assert.True(result.Sessions[0].IsHeadBlocker);
        Assert.False(result.Sessions[1].IsHeadBlocker);
    }

    [Fact]
    public async Task Blocking_reports_no_chains_when_nothing_is_blocked()
    {
        var executor = new FakeSqlQueryExecutor();

        var result = await Blocking(executor).CollectAsync();

        Assert.True(result.Metadata.Success);
        Assert.Empty(result.Sessions);
        Assert.Equal(0, result.BlockingChainCount);
    }

    private static SqlRow BlockingRow(int sessionId, int blockedBy, int isHeadBlocker) =>
        FakeSqlQueryExecutor.Row(
            ("session_id", sessionId),
            ("blocking_session_id", blockedBy),
            ("wait_type", "LCK_M_S"),
            ("wait_time_ms", 8000L),
            ("wait_resource", "KEY: 7:72057594043432960"),
            ("database_name", "OrdersDb"),
            ("sql_text", "SELECT * FROM dbo.Orders WITH (UPDLOCK)"),
            ("status", "suspended"),
            ("login_name", "app_user"),
            ("host_name", "APPSRV1"),
            ("program_name", "Orders API"),
            ("is_head_blocker", isHeadBlocker));

    // --- Top CPU / Top I/O ---------------------------------------------------

    [Fact]
    public async Task Top_cpu_maps_averages_and_plan_identity()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows =
            [
                FakeSqlQueryExecutor.Row(
                    ("sql_text", "SELECT * FROM dbo.Orders"),
                    ("query_hash", "0x1234567890ABCDEF"),
                    ("query_plan_hash", "0xFEDCBA0987654321"),
                    ("object_name", "usp_GetOrders"),
                    ("execution_count", 400L),
                    ("total_cpu_time_ms", 8000L),
                    ("avg_cpu_time_ms", 20.0m),
                    ("total_elapsed_time_ms", 12000L),
                    ("avg_elapsed_time_ms", 30.0m),
                    ("total_logical_reads", 200000L),
                    ("avg_logical_reads", 500.0d),
                    ("database_name", "OrdersDb")),
            ],
        };

        var result = await TopCpu(executor).CollectAsync(topN: 5);

        Assert.Equal(5, result.TopN);
        Assert.Contains("SELECT TOP (5)", executor.ExecutedQueries[0], StringComparison.Ordinal);

        var query = Assert.Single(result.Queries);
        Assert.Equal("0x1234567890ABCDEF", query.QueryHash);
        Assert.Equal("0xFEDCBA0987654321", query.QueryPlanHash);
        Assert.Equal("usp_GetOrders", query.ObjectName);
        Assert.Equal(20.0d, query.AvgCpuTimeMs);
        Assert.Equal(500.0d, query.AvgLogicalReads);
        Assert.Equal("OrdersDb", query.DatabaseName);
    }

    [Fact]
    public async Task Top_io_maps_read_and_write_metrics()
    {
        var executor = new FakeSqlQueryExecutor
        {
            Rows =
            [
                FakeSqlQueryExecutor.Row(
                    ("sql_text", "SELECT * FROM dbo.Ledger"),
                    ("query_hash", "0xAAAA"),
                    ("query_plan_hash", "0xBBBB"),
                    ("object_name", null),
                    ("execution_count", 10L),
                    ("total_logical_reads", 1000000L),
                    ("avg_logical_reads", 100000.0d),
                    ("total_physical_reads", 5000L),
                    ("avg_physical_reads", 500.0d),
                    ("total_logical_writes", 20L),
                    ("avg_logical_writes", 2.0d),
                    ("total_elapsed_time_ms", 45000L),
                    ("database_name", "LedgerDb")),
            ],
        };

        var result = await TopIo(executor).CollectAsync(topN: 3, database: "LedgerDb");

        Assert.Contains("SELECT TOP (3)", executor.ExecutedQueries[0], StringComparison.Ordinal);
        Assert.Contains("DB_NAME(st.dbid) = @p0", executor.ExecutedQueries[0], StringComparison.Ordinal);

        var query = Assert.Single(result.Queries);
        Assert.Equal(1000000L, query.TotalLogicalReads);
        Assert.Equal(500.0d, query.AvgPhysicalReads);
        Assert.Null(query.ObjectName);
    }

    [Fact]
    public async Task Top_cpu_keeps_the_row_limit_on_failure()
    {
        var executor = new FakeSqlQueryExecutor { ThrowOnExecute = PermissionDenied() };

        var result = await TopCpu(executor).CollectAsync(topN: 42);

        Assert.False(result.Metadata.Success);
        Assert.True(result.Metadata.PermissionIssue);
        Assert.Equal(42, result.TopN);
    }

    // --- Wait statistics -----------------------------------------------------

    [Fact]
    public async Task Wait_statistics_collects_detail_and_totals()
    {
        var executor = new FakeSqlQueryExecutor
        {
            RowsForQuery = sql => sql.Contains("sqlserver_start_time", StringComparison.Ordinal)
                ?
                [
                    FakeSqlQueryExecutor.Row(
                        ("sqlserver_start_time", new DateTime(2024, 5, 1, 3, 0, 0, DateTimeKind.Unspecified)),
                        ("waiting_tasks_count", 1000L),
                        ("wait_time_ms", 50000L),
                        ("signal_wait_time_ms", 2000L),
                        ("resource_wait_time_ms", 48000L)),
                ]
                :
                [
                    FakeSqlQueryExecutor.Row(
                        ("wait_type", "PAGEIOLATCH_SH"),
                        ("waiting_tasks_count", 400L),
                        ("wait_time_ms", 30000L),
                        ("max_wait_time_ms", 900L),
                        ("signal_wait_time_ms", 500L),
                        ("resource_wait_time_ms", 29500L)),
                ],
        };

        var result = await Waits(executor).CollectAsync(topN: 10);

        Assert.True(result.Metadata.Success);
        Assert.Equal(Models.Performance.WaitStatisticsApplicability.ServerLevel, result.Applicability);
        Assert.Equal(10, result.TopN);

        var statistic = Assert.Single(result.Statistics);
        Assert.Equal("PAGEIOLATCH_SH", statistic.WaitType);
        Assert.Equal(29500L, statistic.ResourceWaitTimeMs);

        Assert.NotNull(result.Totals);
        Assert.Equal(50000L, result.Totals!.WaitTimeMs);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 3, 0, 0, TimeSpan.Zero), result.SqlServerStartTime);

        // No previous observation is available, so no delta can honestly be reported.
        Assert.Null(result.Delta);
        Assert.Equal(Models.Performance.WaitDeltaStatus.InsufficientPreviousData, result.DeltaStatus);
    }

    [Fact]
    public async Task Wait_statistics_keeps_detail_when_the_summary_is_unavailable()
    {
        var executor = new FakeSqlQueryExecutor
        {
            RowsForQuery = sql => sql.Contains("sqlserver_start_time", StringComparison.Ordinal)
                ? throw PermissionDenied()
                :
                [
                    FakeSqlQueryExecutor.Row(
                        ("wait_type", "CXPACKET"),
                        ("waiting_tasks_count", 10L),
                        ("wait_time_ms", 100L),
                        ("max_wait_time_ms", 50L),
                        ("signal_wait_time_ms", 5L),
                        ("resource_wait_time_ms", 95L)),
                ],
        };

        var result = await Waits(executor).CollectAsync();

        // Losing the totals must not discard the per-wait-type detail already collected.
        Assert.True(result.Metadata.Success);
        Assert.Single(result.Statistics);
        Assert.Null(result.Totals);
        Assert.Null(result.SqlServerStartTime);
    }

    [Fact]
    public void Wait_statistics_is_a_server_scope_collector()
    {
        Assert.Equal(CollectorScope.Server, Waits(new FakeSqlQueryExecutor()).Scope);
    }
}
