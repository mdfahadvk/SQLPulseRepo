using System.Text.RegularExpressions;

namespace SQLPulseAPI.Net8.Utilities;

/// <summary>
/// Identifies monitoring / diagnostic queries that must not be reported as workload.
/// Ported from <c>app/detectors/query_filters.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// SQLPulse reads the plan cache, so its own diagnostic queries — and those of any
/// other monitoring tool on the instance — show up in the "top CPU" and "top I/O"
/// lists. Reporting them as optimization opportunities is noise.
/// </para>
/// <para>
/// Design constraints carried over from the Python original:
/// never filter on <c>sys.*</c> broadly, because ORMs and migration tools legitimately
/// read <c>sys.objects</c>, <c>sys.tables</c>, <c>sys.columns</c>, <c>sys.indexes</c> and
/// <c>sys.partitions</c>; only exclude on evidence specific to diagnostics; and always
/// carry a machine-readable reason so an operator can audit what was hidden and why.
/// </para>
/// <para>
/// Python applies this at the detector stage, not in the collectors, so a snapshot still
/// reports everything the DMVs returned. The same split is kept here.
/// </para>
/// </remarks>
public static partial class QueryFilters
{
    /// <summary>
    /// Embedded in every SQLPulse-issued diagnostic query. This is the primary and most
    /// reliable mechanism: it cannot produce a false positive, because only SQLPulse
    /// emits it.
    /// </summary>
    public const string MonitoringQueryMarker = "SQLPulse:monitoring";

    /// <summary>Reason code for SQLPulse's own diagnostic queries.</summary>
    public const string ReasonSqlPulseSelf = "sqlpulse_monitoring_query";

    /// <summary>Reason code for third-party monitoring queries recognised by DMV usage.</summary>
    public const string ReasonDiagnosticDmv = "diagnostic_dmv_query";

    /// <summary>
    /// DMVs and catalog views with no meaningful application use — referencing one of
    /// these means the query is diagnostic, not business workload.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes general-purpose metadata views such as <c>sys.objects</c>,
    /// <c>sys.tables</c>, <c>sys.columns</c>, <c>sys.indexes</c> and <c>sys.partitions</c>.
    /// </remarks>
    private static readonly HashSet<string> DiagnosticObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        // Execution / plan cache introspection
        "sys.dm_exec_query_stats",
        "sys.dm_exec_sql_text",
        "sys.dm_exec_query_plan",
        "sys.dm_exec_text_query_plan",
        "sys.dm_exec_cached_plans",
        "sys.dm_exec_query_profiles",
        "sys.dm_exec_procedure_stats",
        "sys.dm_exec_trigger_stats",
        "sys.dm_exec_input_buffer",
        // Session / request introspection
        "sys.dm_exec_requests",
        "sys.dm_exec_sessions",
        "sys.dm_exec_connections",
        // OS / wait introspection
        "sys.dm_os_wait_stats",
        "sys.dm_os_waiting_tasks",
        "sys.dm_os_performance_counters",
        "sys.dm_os_sys_info",
        "sys.dm_os_memory_clerks",
        "sys.dm_os_schedulers",
        // Locking / transactions
        "sys.dm_tran_locks",
        // Index / IO diagnostics
        "sys.dm_db_index_usage_stats",
        "sys.dm_db_index_physical_stats",
        "sys.dm_db_missing_index_details",
        "sys.dm_db_missing_index_groups",
        "sys.dm_db_missing_index_group_stats",
        "sys.dm_io_virtual_file_stats",
        // Query Store runtime views (SQLPulse and other monitors read these)
        "sys.query_store_runtime_stats",
        "sys.query_store_runtime_stats_interval",
        "sys.query_store_plan",
        "sys.query_store_query",
        "sys.query_store_query_text",
    };

    /// <summary>
    /// Returns the exclusion reason for <paramref name="sqlText"/>, or
    /// <see langword="null"/> when it should be kept.
    /// </summary>
    /// <remarks>
    /// Returning <see langword="null"/> means "this is real workload" — the default, so an
    /// unrecognised query is always reported rather than silently hidden.
    /// </remarks>
    public static string? ClassifyExclusion(string? sqlText)
    {
        if (string.IsNullOrEmpty(sqlText))
        {
            return null;
        }

        if (sqlText.Contains(MonitoringQueryMarker, StringComparison.Ordinal))
        {
            return ReasonSqlPulseSelf;
        }

        foreach (Match match in ObjectReferencePattern().Matches(sqlText))
        {
            if (DiagnosticObjects.Contains(match.Value))
            {
                return ReasonDiagnosticDmv;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="sqlText"/> is a monitoring / diagnostic query.</summary>
    public static bool IsExcluded(string? sqlText) => ClassifyExclusion(sqlText) is not null;

    /// <summary>
    /// Matches a schema-qualified object reference, so <c>sys.dm_exec_requests</c> is found
    /// but a column or alias merely containing that text is not.
    /// </summary>
    [GeneratedRegex(@"\bsys\.[a-z0-9_]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObjectReferencePattern();
}
