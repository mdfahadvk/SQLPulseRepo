using System.Globalization;

namespace SQLPulseAPI.Net8.Services.Performance;

/// <summary>
/// Read-only SQL Server performance monitoring queries.
/// Ported verbatim from <c>app/database/performance_queries.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every query reads DMVs or system catalog views only; none modifies data,
/// configuration or server state.
/// </para>
/// <para>
/// Required permissions per collector:
/// active requests, long-running queries, blocking sessions, top CPU, top I/O and
/// wait statistics all need <c>VIEW SERVER STATE</c>.
/// </para>
/// <para>
/// Positional placeholders differ from the Python original: pyodbc uses <c>?</c>,
/// <see cref="Microsoft.Data.SqlClient"/> uses named parameters, so the queries bind
/// <c>@p0</c>, <c>@p1</c>, … in the same left-to-right order.
/// </para>
/// <para>
/// The <c>TOP (n)</c> clause is not parameterizable in T-SQL, so it is substituted
/// through <see cref="WithTopN"/>, which only ever emits a validated integer.
/// </para>
/// </remarks>
public static class PerformanceQueries
{
    /// <summary>Reads one stored Query Store plan without executing user SQL.</summary>
    public const string ExecutionPlan = """
/* SQLPulse:monitoring */
SELECT TOP (1)
    CONVERT(VARCHAR(64), q.query_hash, 1) AS query_hash,
    p.plan_id,
    CONVERT(NVARCHAR(MAX), p.query_plan) AS plan_xml
FROM sys.query_store_plan p
INNER JOIN sys.query_store_query q
    ON p.query_id = q.query_id
WHERE (@p0 IS NULL OR CONVERT(VARCHAR(64), q.query_hash, 1) = @p0)
    AND (@p1 IS NULL OR q.query_id = @p1)
    AND (@p2 IS NULL OR p.plan_id = @p2);
""";

    /// <summary>Reads a uniquely matching current cached plan as a safe fallback.</summary>
    public const string CachedExecutionPlan = """
/* SQLPulse:monitoring */
SELECT TOP (2)
    CONVERT(VARCHAR(64), qs.query_hash, 1) AS query_hash,
    CONVERT(NVARCHAR(MAX), qp.query_plan) AS plan_xml
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
WHERE CONVERT(VARCHAR(64), qs.query_hash, 1) = @p0;
""";

    /// <summary>
    /// Marker embedded in every SQLPulse-issued query so the plan-cache collectors can
    /// recognise SQLPulse's own diagnostic reads later. See <c>Utilities.QueryFilters</c>.
    /// </summary>
    public const string MonitoringMarker = "/* SQLPulse:monitoring */";

    /// <summary>Placeholder replaced by <see cref="WithTopN"/>.</summary>
    private const string TopNToken = "{top_n}";

    /// <summary>
    /// Substitutes the row limit into a <c>TOP ({top_n})</c> clause.
    /// </summary>
    /// <remarks>
    /// The value is an <see cref="int"/> and is range-checked before being formatted
    /// with the invariant culture, so no caller-supplied text can reach the statement.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="topN"/> is not between 1 and <see cref="MaxTopN"/>.
    /// </exception>
    public static string WithTopN(string query, int topN)
    {
        if (topN < 1 || topN > MaxTopN)
        {
            throw new ArgumentOutOfRangeException(
                nameof(topN), topN, $"Row limit must be between 1 and {MaxTopN}.");
        }

        return query.Replace(TopNToken, topN.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Upper bound for any <c>TOP (n)</c> clause.</summary>
    public const int MaxTopN = 1000;

    // =========================================================================
    // ACTIVE REQUESTS — requires VIEW SERVER STATE
    // =========================================================================

    /// <summary>Currently executing user requests, instance-wide.</summary>
    public const string ActiveRequests = """
/* SQLPulse:monitoring */
SELECT
    r.session_id,
    r.request_id,
    r.status,
    r.command,
    SUBSTRING(
        st.text,
        (r.statement_start_offset / 2) + 1,
        CASE
            WHEN r.statement_end_offset = -1 THEN LEN(st.text)
            ELSE (r.statement_end_offset - r.statement_start_offset) / 2 + 1
        END
    ) AS sql_text,
    DB_NAME(r.database_id) AS database_name,
    r.wait_type,
    r.wait_time AS wait_time_ms,
    r.cpu_time AS cpu_time_ms,
    r.total_elapsed_time AS total_elapsed_time_ms,
    r.reads,
    r.writes,
    r.logical_reads,
    r.blocking_session_id,
    r.open_transaction_count,
    s.login_name,
    s.host_name,
    s.program_name
FROM sys.dm_exec_requests r
INNER JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
WHERE r.session_id <> @@SPID
    AND s.is_user_process = 1
ORDER BY r.total_elapsed_time DESC;
""";

    /// <summary>Active requests limited to one database. Parameter: <c>@p0</c> database name.</summary>
    public const string ActiveRequestsByDatabase = """
/* SQLPulse:monitoring */
SELECT
    r.session_id,
    r.request_id,
    r.status,
    r.command,
    SUBSTRING(
        st.text,
        (r.statement_start_offset / 2) + 1,
        CASE
            WHEN r.statement_end_offset = -1 THEN LEN(st.text)
            ELSE (r.statement_end_offset - r.statement_start_offset) / 2 + 1
        END
    ) AS sql_text,
    DB_NAME(r.database_id) AS database_name,
    r.wait_type,
    r.wait_time AS wait_time_ms,
    r.cpu_time AS cpu_time_ms,
    r.total_elapsed_time AS total_elapsed_time_ms,
    r.reads,
    r.writes,
    r.logical_reads,
    r.blocking_session_id,
    r.open_transaction_count,
    s.login_name,
    s.host_name,
    s.program_name
FROM sys.dm_exec_requests r
INNER JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
WHERE r.session_id <> @@SPID
    AND s.is_user_process = 1
    AND DB_NAME(r.database_id) = @p0
ORDER BY r.total_elapsed_time DESC;
""";

    // =========================================================================
    // LONG-RUNNING QUERIES — requires VIEW SERVER STATE
    // =========================================================================

    /// <summary>Requests exceeding an elapsed-time threshold. Parameter: <c>@p0</c> threshold in ms.</summary>
    public const string LongRunningQueries = """
/* SQLPulse:monitoring */
SELECT
    r.session_id,
    r.request_id,
    SUBSTRING(
        st.text,
        (r.statement_start_offset / 2) + 1,
        CASE
            WHEN r.statement_end_offset = -1 THEN LEN(st.text)
            ELSE (r.statement_end_offset - r.statement_start_offset) / 2 + 1
        END
    ) AS sql_text,
    DB_NAME(r.database_id) AS database_name,
    r.total_elapsed_time AS elapsed_time_ms,
    r.cpu_time AS cpu_time_ms,
    r.reads,
    r.logical_reads,
    r.writes,
    r.wait_type,
    r.wait_time AS wait_time_ms,
    r.status,
    s.login_name,
    s.host_name,
    s.program_name
FROM sys.dm_exec_requests r
INNER JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
WHERE r.session_id <> @@SPID
    AND s.is_user_process = 1
    AND r.total_elapsed_time >= @p0
ORDER BY r.total_elapsed_time DESC;
""";

    /// <summary>
    /// Long-running requests in one database.
    /// Parameters: <c>@p0</c> threshold in ms, <c>@p1</c> database name.
    /// </summary>
    public const string LongRunningQueriesByDatabase = """
/* SQLPulse:monitoring */
SELECT
    r.session_id,
    r.request_id,
    SUBSTRING(
        st.text,
        (r.statement_start_offset / 2) + 1,
        CASE
            WHEN r.statement_end_offset = -1 THEN LEN(st.text)
            ELSE (r.statement_end_offset - r.statement_start_offset) / 2 + 1
        END
    ) AS sql_text,
    DB_NAME(r.database_id) AS database_name,
    r.total_elapsed_time AS elapsed_time_ms,
    r.cpu_time AS cpu_time_ms,
    r.reads,
    r.logical_reads,
    r.writes,
    r.wait_type,
    r.wait_time AS wait_time_ms,
    r.status,
    s.login_name,
    s.host_name,
    s.program_name
FROM sys.dm_exec_requests r
INNER JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
WHERE r.session_id <> @@SPID
    AND s.is_user_process = 1
    AND r.total_elapsed_time >= @p0
    AND DB_NAME(r.database_id) = @p1
ORDER BY r.total_elapsed_time DESC;
""";

    // =========================================================================
    // BLOCKING SESSIONS — requires VIEW SERVER STATE
    // =========================================================================

    /// <summary>Sessions waiting on another session, with head-blocker flag.</summary>
    public const string BlockingSessions = """
/* SQLPulse:monitoring */
WITH blocking_info AS (
    SELECT
        s.session_id,
        r.blocking_session_id,
        r.wait_type,
        r.wait_time AS wait_time_ms,
        r.wait_resource,
        DB_NAME(r.database_id) AS database_name,
        SUBSTRING(
            st.text,
            (r.statement_start_offset / 2) + 1,
            CASE
                WHEN r.statement_end_offset = -1 THEN LEN(st.text)
                ELSE (r.statement_end_offset - r.statement_start_offset) / 2 + 1
            END
        ) AS sql_text,
        r.status,
        s.login_name,
        s.host_name,
        s.program_name
    FROM sys.dm_exec_sessions s
    INNER JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
    WHERE s.is_user_process = 1
        AND r.blocking_session_id <> 0
)
SELECT
    bi.session_id,
    bi.blocking_session_id,
    bi.wait_type,
    bi.wait_time_ms,
    bi.wait_resource,
    bi.database_name,
    bi.sql_text,
    bi.status,
    bi.login_name,
    bi.host_name,
    bi.program_name,
    CASE
        WHEN bi.blocking_session_id NOT IN (
            SELECT session_id FROM blocking_info
        ) THEN 1
        ELSE 0
    END AS is_head_blocker
FROM blocking_info bi
ORDER BY bi.wait_time_ms DESC;
""";

    /// <summary>Blocking sessions in one database. Parameter: <c>@p0</c> database name.</summary>
    public const string BlockingSessionsByDatabase = """
/* SQLPulse:monitoring */
WITH blocking_info AS (
    SELECT
        s.session_id,
        r.blocking_session_id,
        r.wait_type,
        r.wait_time AS wait_time_ms,
        r.wait_resource,
        DB_NAME(r.database_id) AS database_name,
        SUBSTRING(
            st.text,
            (r.statement_start_offset / 2) + 1,
            CASE
                WHEN r.statement_end_offset = -1 THEN LEN(st.text)
                ELSE (r.statement_end_offset - r.statement_start_offset) / 2 + 1
            END
        ) AS sql_text,
        r.status,
        s.login_name,
        s.host_name,
        s.program_name
    FROM sys.dm_exec_sessions s
    INNER JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
    WHERE s.is_user_process = 1
        AND r.blocking_session_id <> 0
        AND DB_NAME(r.database_id) = @p0
)
SELECT
    bi.session_id,
    bi.blocking_session_id,
    bi.wait_type,
    bi.wait_time_ms,
    bi.wait_resource,
    bi.database_name,
    bi.sql_text,
    bi.status,
    bi.login_name,
    bi.host_name,
    bi.program_name,
    CASE
        WHEN bi.blocking_session_id NOT IN (
            SELECT session_id FROM blocking_info
        ) THEN 1
        ELSE 0
    END AS is_head_blocker
FROM blocking_info bi
ORDER BY bi.wait_time_ms DESC;
""";

    // =========================================================================
    // TOP CPU QUERIES (plan cache) — requires VIEW SERVER STATE
    // =========================================================================

    /// <summary>Highest cumulative CPU consumers in the plan cache.</summary>
    public const string TopCpuQueries = """
/* SQLPulse:monitoring */
SELECT TOP ({top_n})
    SUBSTRING(
        st.text,
        (qs.statement_start_offset / 2) + 1,
        CASE
            WHEN qs.statement_end_offset = -1 THEN LEN(st.text)
            ELSE (qs.statement_end_offset - qs.statement_start_offset) / 2 + 1
        END
    ) AS sql_text,
    CONVERT(VARCHAR(64), qs.query_hash, 1) AS query_hash,
    CONVERT(VARCHAR(64), qs.query_plan_hash, 1) AS query_plan_hash,
    OBJECT_NAME(st.objectid, st.dbid) AS object_name,
    qs.execution_count,
    qs.total_worker_time / 1000 AS total_cpu_time_ms,
    CASE
        WHEN qs.execution_count > 0
        THEN (qs.total_worker_time / 1000.0) / qs.execution_count
        ELSE 0
    END AS avg_cpu_time_ms,
    qs.total_elapsed_time / 1000 AS total_elapsed_time_ms,
    CASE
        WHEN qs.execution_count > 0
        THEN (qs.total_elapsed_time / 1000.0) / qs.execution_count
        ELSE 0
    END AS avg_elapsed_time_ms,
    qs.total_logical_reads,
    CASE
        WHEN qs.execution_count > 0
        THEN CAST(qs.total_logical_reads AS FLOAT) / qs.execution_count
        ELSE 0
    END AS avg_logical_reads,
    DB_NAME(st.dbid) AS database_name
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
ORDER BY qs.total_worker_time DESC;
""";

    /// <summary>Top CPU consumers attributed to one database. Parameter: <c>@p0</c> database name.</summary>
    public const string TopCpuQueriesByDatabase = """
/* SQLPulse:monitoring */
SELECT TOP ({top_n})
    SUBSTRING(
        st.text,
        (qs.statement_start_offset / 2) + 1,
        CASE
            WHEN qs.statement_end_offset = -1 THEN LEN(st.text)
            ELSE (qs.statement_end_offset - qs.statement_start_offset) / 2 + 1
        END
    ) AS sql_text,
    CONVERT(VARCHAR(64), qs.query_hash, 1) AS query_hash,
    CONVERT(VARCHAR(64), qs.query_plan_hash, 1) AS query_plan_hash,
    OBJECT_NAME(st.objectid, st.dbid) AS object_name,
    qs.execution_count,
    qs.total_worker_time / 1000 AS total_cpu_time_ms,
    CASE
        WHEN qs.execution_count > 0
        THEN (qs.total_worker_time / 1000.0) / qs.execution_count
        ELSE 0
    END AS avg_cpu_time_ms,
    qs.total_elapsed_time / 1000 AS total_elapsed_time_ms,
    CASE
        WHEN qs.execution_count > 0
        THEN (qs.total_elapsed_time / 1000.0) / qs.execution_count
        ELSE 0
    END AS avg_elapsed_time_ms,
    qs.total_logical_reads,
    CASE
        WHEN qs.execution_count > 0
        THEN CAST(qs.total_logical_reads AS FLOAT) / qs.execution_count
        ELSE 0
    END AS avg_logical_reads,
    DB_NAME(st.dbid) AS database_name
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
WHERE DB_NAME(st.dbid) = @p0
ORDER BY qs.total_worker_time DESC;
""";

    // =========================================================================
    // TOP I/O QUERIES (plan cache) — requires VIEW SERVER STATE
    // =========================================================================

    /// <summary>Highest cumulative logical-read consumers in the plan cache.</summary>
    public const string TopIoQueries = """
/* SQLPulse:monitoring */
SELECT TOP ({top_n})
    SUBSTRING(
        st.text,
        (qs.statement_start_offset / 2) + 1,
        CASE
            WHEN qs.statement_end_offset = -1 THEN LEN(st.text)
            ELSE (qs.statement_end_offset - qs.statement_start_offset) / 2 + 1
        END
    ) AS sql_text,
    CONVERT(VARCHAR(64), qs.query_hash, 1) AS query_hash,
    CONVERT(VARCHAR(64), qs.query_plan_hash, 1) AS query_plan_hash,
    OBJECT_NAME(st.objectid, st.dbid) AS object_name,
    qs.execution_count,
    qs.total_logical_reads,
    CASE
        WHEN qs.execution_count > 0
        THEN CAST(qs.total_logical_reads AS FLOAT) / qs.execution_count
        ELSE 0
    END AS avg_logical_reads,
    qs.total_physical_reads,
    CASE
        WHEN qs.execution_count > 0
        THEN CAST(qs.total_physical_reads AS FLOAT) / qs.execution_count
        ELSE 0
    END AS avg_physical_reads,
    qs.total_logical_writes,
    CASE
        WHEN qs.execution_count > 0
        THEN CAST(qs.total_logical_writes AS FLOAT) / qs.execution_count
        ELSE 0
    END AS avg_logical_writes,
    qs.total_elapsed_time / 1000 AS total_elapsed_time_ms,
    DB_NAME(st.dbid) AS database_name
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
ORDER BY qs.total_logical_reads DESC;
""";

    /// <summary>Top I/O consumers attributed to one database. Parameter: <c>@p0</c> database name.</summary>
    public const string TopIoQueriesByDatabase = """
/* SQLPulse:monitoring */
SELECT TOP ({top_n})
    SUBSTRING(
        st.text,
        (qs.statement_start_offset / 2) + 1,
        CASE
            WHEN qs.statement_end_offset = -1 THEN LEN(st.text)
            ELSE (qs.statement_end_offset - qs.statement_start_offset) / 2 + 1
        END
    ) AS sql_text,
    CONVERT(VARCHAR(64), qs.query_hash, 1) AS query_hash,
    CONVERT(VARCHAR(64), qs.query_plan_hash, 1) AS query_plan_hash,
    OBJECT_NAME(st.objectid, st.dbid) AS object_name,
    qs.execution_count,
    qs.total_logical_reads,
    CASE
        WHEN qs.execution_count > 0
        THEN CAST(qs.total_logical_reads AS FLOAT) / qs.execution_count
        ELSE 0
    END AS avg_logical_reads,
    qs.total_physical_reads,
    CASE
        WHEN qs.execution_count > 0
        THEN CAST(qs.total_physical_reads AS FLOAT) / qs.execution_count
        ELSE 0
    END AS avg_physical_reads,
    qs.total_logical_writes,
    CASE
        WHEN qs.execution_count > 0
        THEN CAST(qs.total_logical_writes AS FLOAT) / qs.execution_count
        ELSE 0
    END AS avg_logical_writes,
    qs.total_elapsed_time / 1000 AS total_elapsed_time_ms,
    DB_NAME(st.dbid) AS database_name
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
WHERE DB_NAME(st.dbid) = @p0
ORDER BY qs.total_logical_reads DESC;
""";

    // =========================================================================
    // WAIT STATISTICS — requires VIEW SERVER STATE
    // =========================================================================

    /// <summary>
    /// Benign / idle waits that are always present on a healthy instance and are not
    /// actionable. Shared by the detail and summary queries so both report the same
    /// population — otherwise the totals would not reconcile with the listed rows.
    /// </summary>
    private const string BenignWaitTypes = """
(
        'CLR_SEMAPHORE', 'LAZYWRITER_SLEEP', 'RESOURCE_QUEUE',
        'SLEEP_TASK', 'SLEEP_SYSTEMTASK', 'SQLTRACE_BUFFER_FLUSH',
        'WAITFOR', 'LOGMGR_QUEUE', 'CHECKPOINT_QUEUE',
        'REQUEST_FOR_DEADLOCK_SEARCH', 'XE_TIMER_EVENT',
        'BROKER_TO_FLUSH', 'BROKER_TASK_STOP', 'CLR_MANUAL_EVENT',
        'CLR_AUTO_EVENT', 'DISPATCHER_QUEUE_SEMAPHORE',
        'FT_IFTS_SCHEDULER_IDLE_WAIT', 'XE_DISPATCHER_WAIT',
        'XE_DISPATCHER_JOIN', 'BROKER_EVENTHANDLER',
        'TRACEWRITE', 'FT_IFTSHC_MUTEX', 'SQLTRACE_INCREMENTAL_FLUSH_SLEEP',
        'BROKER_RECEIVE_WAITFOR', 'ONDEMAND_TASK_QUEUE',
        'DBMIRROR_EVENTS_QUEUE', 'DBMIRRORING_CMD',
        'DIRTY_PAGE_POLL', 'HADR_FILESTREAM_IOMGR_IOCOMPLETION',
        'SP_SERVER_DIAGNOSTICS_SLEEP', 'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP',
        'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP',
        'QDS_ASYNC_QUEUE', 'WAIT_XTP_OFFLINE_CKPT_NEW_LOG',
        'SOSHOST_SLEEP', 'SP_PREEMPTIVE_SERVER_DIAGNOSTICS_SLEEP'
    )
""";

    /// <summary>Top actionable wait types, ordered by accumulated wait time.</summary>
    public static readonly string WaitStatistics = """
/* SQLPulse:monitoring */
SELECT TOP ({top_n})
    wait_type,
    waiting_tasks_count,
    wait_time_ms,
    max_wait_time_ms,
    signal_wait_time_ms,
    wait_time_ms - signal_wait_time_ms AS resource_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE waiting_tasks_count > 0
    AND wait_type NOT IN 
""" + BenignWaitTypes + """
ORDER BY wait_time_ms DESC;
""";

    /// <summary>
    /// Instance-wide wait totals plus the counter epoch.
    /// </summary>
    /// <remarks>
    /// <c>sys.dm_os_wait_stats</c> is cumulative since the last service restart, so the
    /// totals are meaningless without <c>sqlserver_start_time</c>: it identifies the epoch
    /// the counters belong to and lets SQLPulse detect a restart between snapshots
    /// (after which a delta would be nonsense).
    /// </remarks>
    public static readonly string WaitStatisticsSummary = """
/* SQLPulse:monitoring */
SELECT
    (SELECT sqlserver_start_time FROM sys.dm_os_sys_info) AS sqlserver_start_time,
    ISNULL(SUM(waiting_tasks_count), 0) AS waiting_tasks_count,
    ISNULL(SUM(wait_time_ms), 0) AS wait_time_ms,
    ISNULL(SUM(signal_wait_time_ms), 0) AS signal_wait_time_ms,
    ISNULL(SUM(wait_time_ms - signal_wait_time_ms), 0) AS resource_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE waiting_tasks_count > 0
    AND wait_type NOT IN 
""" + BenignWaitTypes + """
;
""";

    // =========================================================================
    // QUERY STORE — requires VIEW DATABASE STATE, run against the target database
    // =========================================================================

    /// <summary>
    /// Query Store configuration for the current database.
    /// </summary>
    /// <remarks>
    /// Read-only. SQLPulse never enables, disables or reconfigures Query Store: changing
    /// a monitored server's configuration is a write, and SQLPulse does not write.
    /// </remarks>
    public const string QueryStoreStatus = """
/* SQLPulse:monitoring */
SELECT
    actual_state_desc,
    desired_state_desc,
    readonly_reason,
    current_storage_size_mb,
    max_storage_size_mb
FROM sys.database_query_store_options;
""";

    /// <summary>
    /// Slow queries recorded by Query Store inside a time window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Durations and CPU times are stored in microseconds, so they are divided by 1000
    /// to give milliseconds before the threshold comparison.
    /// </para>
    /// <para>
    /// The window predicate is an interval-overlap test
    /// (<c>last_execution_time &gt;= start AND first_execution_time &lt;= end</c>), matching
    /// the Python reference exactly. A runtime-stats row covers a span rather than an
    /// instant, so this returns every row whose activity touched the window.
    /// </para>
    /// </remarks>
    public const string QueryStoreSlowHistory = """
/* SQLPulse:monitoring */
SELECT TOP ({top_n})
    q.query_id,
    qt.query_sql_text,
    CONVERT(VARCHAR(64), q.query_hash, 1) AS query_hash,
    q.object_id,
    CASE
        WHEN q.object_id > 0
        THEN CONCAT(OBJECT_SCHEMA_NAME(q.object_id), '.', OBJECT_NAME(q.object_id))
        ELSE NULL
    END AS procedure_name,
    p.plan_id,
    rs.count_executions AS execution_count,
    rs.avg_duration / 1000.0 AS avg_duration_ms,
    rs.max_duration / 1000.0 AS max_duration_ms,
    rs.min_duration / 1000.0 AS min_duration_ms,
    rs.avg_cpu_time / 1000.0 AS avg_cpu_time_ms,
    rs.max_cpu_time / 1000.0 AS max_cpu_time_ms,
    rs.avg_logical_io_reads AS avg_logical_reads,
    rs.max_logical_io_reads AS max_logical_reads,
    rs.avg_physical_io_reads AS avg_physical_reads,
    CAST(rs.first_execution_time AS datetime2) AS first_execution_time,
    CAST(rs.last_execution_time AS datetime2) AS last_execution_time,
    CAST(rsi.start_time AS datetime2) AS interval_start_time
FROM sys.query_store_runtime_stats rs
INNER JOIN sys.query_store_plan p
    ON rs.plan_id = p.plan_id
INNER JOIN sys.query_store_query q
    ON p.query_id = q.query_id
INNER JOIN sys.query_store_query_text qt
    ON q.query_text_id = qt.query_text_id
INNER JOIN sys.query_store_runtime_stats_interval rsi
    ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rs.last_execution_time >= @p0
    AND rs.first_execution_time <= @p1
    AND rs.max_duration / 1000.0 >= @p2
ORDER BY rs.max_duration DESC;
""";
}
