"""Read-only SQL Server performance monitoring queries.

All queries use DMVs (Dynamic Management Views) and system catalog views.
None of these queries modify data, configuration, or server state.

Required Permissions (per collector):
--------------------------------------
- Active Requests:      VIEW SERVER STATE
- Long-Running Queries: VIEW SERVER STATE
- Blocking Sessions:    VIEW SERVER STATE
- Slow History (QS):    VIEW DATABASE STATE
- Top CPU Queries:      VIEW SERVER STATE (DMV path) or VIEW DATABASE STATE (Query Store)
- Top I/O Queries:      VIEW SERVER STATE (DMV path) or VIEW DATABASE STATE (Query Store)
- Wait Statistics:      VIEW SERVER STATE
"""

# =============================================================================
# ACTIVE REQUESTS
# Requires: VIEW SERVER STATE
# =============================================================================

ACTIVE_REQUESTS = """
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
"""

# Database-scoped variant: only requests in the target database
ACTIVE_REQUESTS_BY_DATABASE = """
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
    AND DB_NAME(r.database_id) = ?
ORDER BY r.total_elapsed_time DESC;
"""

# =============================================================================
# LONG-RUNNING QUERIES
# Requires: VIEW SERVER STATE
# Parameter: @threshold_ms INT — minimum elapsed time in milliseconds
# =============================================================================

LONG_RUNNING_QUERIES = """
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
    AND r.total_elapsed_time >= ?
ORDER BY r.total_elapsed_time DESC;
"""

# Database-scoped variant: only long-running queries in the target database
LONG_RUNNING_QUERIES_BY_DATABASE = """
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
    AND r.total_elapsed_time >= ?
    AND DB_NAME(r.database_id) = ?
ORDER BY r.total_elapsed_time DESC;
"""

# =============================================================================
# BLOCKING SESSIONS
# Requires: VIEW SERVER STATE
# =============================================================================

BLOCKING_SESSIONS = """
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
"""

# Database-scoped variant: only blocking sessions in the target database
BLOCKING_SESSIONS_BY_DATABASE = """
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
        AND DB_NAME(r.database_id) = ?
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
"""

# =============================================================================
# TOP CPU-CONSUMING QUERIES (from plan cache DMVs)
# Requires: VIEW SERVER STATE
# Parameter: top_n is handled via Python (TOP is not parameterizable in pyodbc)
# =============================================================================

TOP_CPU_QUERIES = """
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
"""

# Database-scoped variant: only queries belonging to the target database
TOP_CPU_QUERIES_BY_DATABASE = """
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
WHERE DB_NAME(st.dbid) = ?
ORDER BY qs.total_worker_time DESC;
"""

# =============================================================================
# TOP I/O-CONSUMING QUERIES (from plan cache DMVs)
# Requires: VIEW SERVER STATE
# =============================================================================

TOP_IO_QUERIES = """
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
"""

# Database-scoped variant: only queries belonging to the target database
TOP_IO_QUERIES_BY_DATABASE = """
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
WHERE DB_NAME(st.dbid) = ?
ORDER BY qs.total_logical_reads DESC;
"""

# =============================================================================
# WAIT STATISTICS
# Requires: VIEW SERVER STATE
# Excludes benign / idle waits that are always present and not actionable.
# =============================================================================

# Benign / idle waits that are always present on a healthy instance and are not
# actionable. Shared by the detail and summary queries so both report the same
# population — otherwise the totals would not reconcile with the listed rows.
_BENIGN_WAIT_TYPES = """(
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
    )"""

WAIT_STATISTICS = """
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
    AND wait_type NOT IN """ + _BENIGN_WAIT_TYPES + """
ORDER BY wait_time_ms DESC;
"""

# =============================================================================
# WAIT STATISTICS SUMMARY — instance-wide totals plus the counter epoch.
# Requires: VIEW SERVER STATE
#
# sys.dm_os_wait_stats is cumulative since the last service restart, so the
# totals are meaningless without sqlserver_start_time: it identifies the epoch
# the counters belong to and lets SQLPulse detect a restart between snapshots
# (after which a delta would be nonsense).
# =============================================================================

WAIT_STATISTICS_SUMMARY = """
/* SQLPulse:monitoring */
SELECT
    (SELECT sqlserver_start_time FROM sys.dm_os_sys_info) AS sqlserver_start_time,
    ISNULL(SUM(waiting_tasks_count), 0) AS waiting_tasks_count,
    ISNULL(SUM(wait_time_ms), 0) AS wait_time_ms,
    ISNULL(SUM(signal_wait_time_ms), 0) AS signal_wait_time_ms,
    ISNULL(SUM(wait_time_ms - signal_wait_time_ms), 0) AS resource_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE waiting_tasks_count > 0
    AND wait_type NOT IN """ + _BENIGN_WAIT_TYPES + """;
"""

# =============================================================================
# QUERY STORE — Check availability
# Requires: VIEW DATABASE STATE (run against the target database)
# =============================================================================

QUERY_STORE_STATUS = """
SELECT
    actual_state_desc,
    desired_state_desc,
    readonly_reason,
    current_storage_size_mb,
    max_storage_size_mb
FROM sys.database_query_store_options;
"""

# =============================================================================
# QUERY STORE — Slow query history within a time range
# Requires: VIEW DATABASE STATE
# Parameters: @start_time, @end_time, @threshold_ms, @top_n
#             (all parameterized via ? except top_n which is formatted)
#
# Returns queries/procedures whose max_duration exceeded the threshold
# during the requested time window.  Durations in QS are stored in
# microseconds, so we convert to milliseconds for consistency.
#
# Includes:
#   - query_id, plan_id, query_hash, object_id
#   - procedure name via OBJECT_SCHEMA_NAME + OBJECT_NAME
#   - execution count, avg/max/min duration, avg/max CPU, avg/max logical reads
#   - first/last execution time, execution interval start
# =============================================================================

QUERY_STORE_SLOW_HISTORY = """
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
WHERE rs.last_execution_time >= ?
    AND rs.first_execution_time <= ?
    AND rs.max_duration / 1000.0 >= ?
ORDER BY rs.max_duration DESC;
"""
