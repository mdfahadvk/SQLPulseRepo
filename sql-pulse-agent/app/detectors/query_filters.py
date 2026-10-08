"""Identification of monitoring / diagnostic queries that must not become findings.

SQLPulse reads the plan cache, so its own diagnostic queries — and those of any
other monitoring tool on the instance — show up in the "top CPU" and "top I/O"
lists.  Reporting them as optimization opportunities is noise: nobody is going
to tune SQLPulse's own DMV reads, and doing so would crowd out real workload.

Design constraints:

  * NEVER filter on ``sys.*`` broadly.  Legitimate application queries read
    ``sys.objects``, ``sys.tables``, ``sys.columns``, ``sys.partitions`` and so
    on — ORMs and migration tools do this constantly.  Filtering those would
    hide real business workload, which is far worse than a little noise.
  * Only exclude on evidence that is *specific* to diagnostics: SQLPulse's own
    marker comment, or a reference to a DMV that has no meaningful application
    use.
  * Every exclusion carries a machine-readable reason so an operator can audit
    what was hidden and why.
"""

from __future__ import annotations

import re

# Embedded in every SQLPulse-issued diagnostic query. This is the primary and
# most reliable mechanism: it cannot produce a false positive, because only
# SQLPulse emits it.
MONITORING_QUERY_MARKER = "SQLPulse:monitoring"

# Exclusion reason codes (stable, machine-readable).
REASON_SQLPULSE_SELF = "sqlpulse_monitoring_query"
REASON_DIAGNOSTIC_DMV = "diagnostic_dmv_query"

# DMVs and catalog views with no meaningful application use — referencing one
# of these means the query is a monitoring/diagnostic query, not business
# workload.  Deliberately excludes general-purpose metadata views such as
# sys.objects / sys.tables / sys.columns / sys.indexes / sys.partitions, which
# applications and ORMs legitimately query.
_DIAGNOSTIC_OBJECTS: frozenset[str] = frozenset({
    # Execution / plan cache introspection
    "sys.dm_exec_query_stats",
    "sys.dm_exec_sql_text",
    "sys.dm_exec_query_plan",
    "sys.dm_exec_text_query_plan",
    "sys.dm_exec_cached_plans",
    "sys.dm_exec_query_profiles",
    "sys.dm_exec_procedure_stats",
    "sys.dm_exec_trigger_stats",
    "sys.dm_exec_input_buffer",
    # Session / request introspection
    "sys.dm_exec_requests",
    "sys.dm_exec_sessions",
    "sys.dm_exec_connections",
    # OS / wait introspection
    "sys.dm_os_wait_stats",
    "sys.dm_os_waiting_tasks",
    "sys.dm_os_performance_counters",
    "sys.dm_os_sys_info",
    "sys.dm_os_memory_clerks",
    "sys.dm_os_schedulers",
    # Locking / transactions
    "sys.dm_tran_locks",
    # Index / IO diagnostics
    "sys.dm_db_index_usage_stats",
    "sys.dm_db_index_physical_stats",
    "sys.dm_db_missing_index_details",
    "sys.dm_db_missing_index_groups",
    "sys.dm_db_missing_index_group_stats",
    "sys.dm_io_virtual_file_stats",
    # Query Store runtime views (SQLPulse and other monitors read these)
    "sys.query_store_runtime_stats",
    "sys.query_store_runtime_stats_interval",
    "sys.query_store_plan",
    "sys.query_store_query",
    "sys.query_store_query_text",
})

# Matches a schema-qualified object reference, so "sys.dm_exec_requests" is
# found but a column or alias merely *containing* that text is not.
_OBJECT_REFERENCE_RE = re.compile(r"\bsys\.[a-z0-9_]+", re.IGNORECASE)


def classify_exclusion(sql_text: str | None) -> str | None:
    """Return an exclusion reason for *sql_text*, or None if it should be kept.

    Returning None means "this is real workload" — the default, so an
    unrecognised query is always reported rather than silently hidden.
    """
    if not sql_text:
        return None

    if MONITORING_QUERY_MARKER in sql_text:
        return REASON_SQLPULSE_SELF

    for reference in _OBJECT_REFERENCE_RE.findall(sql_text):
        if reference.lower() in _DIAGNOSTIC_OBJECTS:
            return REASON_DIAGNOSTIC_DMV

    return None


def is_excluded(sql_text: str | None) -> bool:
    """True when *sql_text* is a monitoring/diagnostic query."""
    return classify_exclusion(sql_text) is not None
