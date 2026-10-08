namespace SQLPulseAPI.Net8.Tests.Compatibility;

/// <summary>
/// Representative, sanitized responses from the Python implementation.
/// </summary>
/// <remarks>
/// <para>
/// These are the golden masters for the migration. Each fixture is transcribed from the
/// Pydantic model that produces it in <c>sql-pulse-agent/app/models/</c>, so it records
/// the Python JSON contract independently of the .NET code — a test that compares .NET
/// against these is not comparing .NET against itself.
/// </para>
/// <para>
/// Values are illustrative and deliberately sanitized; the tests assert on structure and
/// on the literals the React UI switches on, never on generated values such as identifiers
/// or timestamps, which cannot match across implementations.
/// </para>
/// </remarks>
internal static class PythonResponseFixtures
{
    /// <summary>Python: <c>HealthResponse</c>.</summary>
    public const string Health =
        """
        {
          "status": "healthy",
          "application": "SQLPulse",
          "version": "0.1.0"
        }
        """;

    /// <summary>Python: <c>ConnectionTestResponse</c>.</summary>
    public const string ConnectionTest =
        """
        {
          "connected": true,
          "message": "Successfully connected to SQL Server."
        }
        """;

    /// <summary>Python: <c>DatabaseListResponse</c>.</summary>
    public const string DatabaseList =
        """
        {
          "server": "sql01",
          "databases": [
            { "name": "AppDb", "status": "ONLINE" }
          ]
        }
        """;

    /// <summary>Python: <c>ErrorResponse</c> — FastAPI's <c>HTTPException</c> envelope.</summary>
    public const string Error =
        """
        { "detail": "database parameter is required when scope=database" }
        """;

    /// <summary>Python: <c>PerformanceSnapshot</c> with every collector populated.</summary>
    public const string PerformanceSnapshot =
        """
        {
          "snapshot_id": "0f8f6f1e-7b1a-4b0e-9a1c-2f3d4e5a6b7c",
          "collected_at": "2026-08-24T09:00:00Z",
          "server_host": "sql01",
          "scope": "server",
          "database": null,
          "active_requests": {
            "metadata": {
              "collector_name": "active_requests",
              "server_host": "sql01",
              "database": "master",
              "collected_at": "2026-08-24T09:00:00Z",
              "success": true,
              "error_message": null,
              "permission_issue": false
            },
            "requests": [
              {
                "session_id": 71,
                "request_id": 0,
                "status": "running",
                "command": "SELECT",
                "sql_text": "SELECT * FROM dbo.Orders",
                "database_name": "AppDb",
                "wait_type": "LCK_M_X",
                "wait_time_ms": 1200,
                "cpu_time_ms": 340,
                "total_elapsed_time_ms": 5400,
                "reads": 12,
                "writes": 0,
                "logical_reads": 980,
                "blocking_session_id": 65,
                "open_transaction_count": 1,
                "login_name": "app_user",
                "host_name": "APPSRV1",
                "program_name": "OrdersService"
              }
            ]
          },
          "long_running_queries": {
            "metadata": {
              "collector_name": "long_running_queries",
              "server_host": "sql01",
              "database": "master",
              "collected_at": "2026-08-24T09:00:00Z",
              "success": true,
              "error_message": null,
              "permission_issue": false
            },
            "threshold_ms": 5000,
            "queries": [
              {
                "session_id": 71,
                "request_id": 0,
                "sql_text": "SELECT * FROM dbo.Orders",
                "database_name": "AppDb",
                "elapsed_time_ms": 5400,
                "cpu_time_ms": 340,
                "reads": 12,
                "logical_reads": 980,
                "writes": 0,
                "wait_type": "LCK_M_X",
                "wait_time_ms": 1200,
                "status": "running",
                "login_name": "app_user",
                "host_name": "APPSRV1",
                "program_name": "OrdersService"
              }
            ]
          },
          "blocking_sessions": {
            "metadata": {
              "collector_name": "blocking_sessions",
              "server_host": "sql01",
              "database": "master",
              "collected_at": "2026-08-24T09:00:00Z",
              "success": true,
              "error_message": null,
              "permission_issue": false
            },
            "sessions": [
              {
                "session_id": 71,
                "blocking_session_id": 65,
                "wait_type": "LCK_M_X",
                "wait_time_ms": 1200,
                "wait_resource": "KEY: 5:72057594045595648",
                "database_name": "AppDb",
                "sql_text": "SELECT * FROM dbo.Orders",
                "status": "suspended",
                "login_name": "app_user",
                "host_name": "APPSRV1",
                "program_name": "OrdersService",
                "is_head_blocker": false
              }
            ],
            "blocking_chain_count": 1
          },
          "top_cpu_queries": {
            "metadata": {
              "collector_name": "top_cpu_queries",
              "server_host": "sql01",
              "database": "master",
              "collected_at": "2026-08-24T09:00:00Z",
              "success": true,
              "error_message": null,
              "permission_issue": false
            },
            "top_n": 20,
            "queries": [
              {
                "sql_text": "SELECT * FROM dbo.Orders",
                "query_hash": "0xAB12CD34",
                "query_plan_hash": "0xEF56AB78",
                "object_name": "usp_GetOrders",
                "execution_count": 1200,
                "total_cpu_time_ms": 480000,
                "avg_cpu_time_ms": 400.0,
                "total_elapsed_time_ms": 720000,
                "avg_elapsed_time_ms": 600.0,
                "total_logical_reads": 96000,
                "avg_logical_reads": 80.0,
                "database_name": "AppDb"
              }
            ]
          },
          "top_io_queries": {
            "metadata": {
              "collector_name": "top_io_queries",
              "server_host": "sql01",
              "database": "master",
              "collected_at": "2026-08-24T09:00:00Z",
              "success": true,
              "error_message": null,
              "permission_issue": false
            },
            "top_n": 20,
            "queries": [
              {
                "sql_text": "SELECT * FROM dbo.Orders",
                "query_hash": "0xAB12CD34",
                "query_plan_hash": "0xEF56AB78",
                "object_name": "usp_GetOrders",
                "execution_count": 1200,
                "total_logical_reads": 96000,
                "avg_logical_reads": 80.0,
                "total_physical_reads": 9600,
                "avg_physical_reads": 8.0,
                "total_logical_writes": 120,
                "avg_logical_writes": 0.1,
                "total_elapsed_time_ms": 720000,
                "database_name": "AppDb"
              }
            ]
          },
          "wait_statistics": {
            "metadata": {
              "collector_name": "wait_statistics",
              "server_host": "sql01",
              "database": "master",
              "collected_at": "2026-08-24T09:00:00Z",
              "success": true,
              "error_message": null,
              "permission_issue": false
            },
            "top_n": 25,
            "statistics": [
              {
                "wait_type": "PAGEIOLATCH_SH",
                "waiting_tasks_count": 4200,
                "wait_time_ms": 180000,
                "max_wait_time_ms": 900,
                "signal_wait_time_ms": 1200,
                "resource_wait_time_ms": 178800
              }
            ],
            "applicability": "server_level",
            "collected_at": "2026-08-24T09:00:00Z",
            "sqlserver_start_time": "2026-08-01T00:00:00Z",
            "totals": {
              "waiting_tasks_count": 4200,
              "wait_time_ms": 180000,
              "signal_wait_time_ms": 1200,
              "resource_wait_time_ms": 178800
            },
            "delta": {
              "previous_collected_at": "2026-08-24T08:55:00Z",
              "interval_seconds": 300.0,
              "waiting_tasks_count": 120,
              "wait_time_ms": 4800,
              "signal_wait_time_ms": 60,
              "resource_wait_time_ms": 4740
            },
            "delta_status": "available"
          }
        }
        """;

    /// <summary>Python: <c>AnalysisResult</c>.</summary>
    public const string AnalysisResult =
        """
        {
          "analysis_id": "6c7fe8ca-a9e5-4227-8a58-920b43d59e15",
          "analyzed_at": "2026-08-24T09:00:00Z",
          "server": "sql01",
          "scope": "server",
          "database": null,
          "overall_status": "DEGRADED",
          "findings": [
            {
              "finding_id": "f1a2b3c4",
              "category": "blocking",
              "server": "sql01",
              "database": "AppDb",
              "severity": "high",
              "confidence": "high",
              "first_observed": "2026-08-24T08:59:00Z",
              "last_observed": "2026-08-24T09:00:00Z",
              "summary": "Session 65 is blocking 4 sessions in AppDb.",
              "evidence": [
                "Head blocker session 65 has been open for 210 seconds"
              ],
              "likely_cause": "An open transaction holding an exclusive lock.",
              "recommendation": "Identify the owner of session 65 and confirm the transaction scope.",
              "validation_steps": [
                "Run sp_who2 and confirm the blocking chain"
              ],
              "human_review_required": true,
              "recommended_drilldown": {
                "scope": "database",
                "database": "AppDb"
              },
              "signals": ["high_cpu"],
              "query_identity": {
                "query_hash": "0xAB12CD34",
                "query_plan_hash": "0xEF56AB78",
                "object_name": "usp_GetOrders"
              },
              "severity_basis": "4 sessions blocked for longer than 60 seconds"
            }
          ],
          "affected_databases": [
            {
              "database": "AppDb",
              "highest_severity": "high",
              "finding_count": 1
            }
          ],
          "total_findings": 1,
          "critical_count": 0,
          "high_count": 1,
          "medium_count": 0,
          "low_count": 0,
          "current_health": {
            "status": "DEGRADED",
            "findings": [
              {
                "finding_id": "f1a2b3c4",
                "category": "blocking",
                "server": "sql01",
                "database": "AppDb",
                "severity": "high",
                "confidence": "high",
                "first_observed": "2026-08-24T08:59:00Z",
                "last_observed": "2026-08-24T09:00:00Z",
                "summary": "Session 65 is blocking 4 sessions in AppDb.",
                "evidence": ["Head blocker session 65 has been open for 210 seconds"],
                "likely_cause": "An open transaction holding an exclusive lock.",
                "recommendation": "Identify the owner of session 65.",
                "validation_steps": ["Run sp_who2 and confirm the blocking chain"],
                "human_review_required": true,
                "recommended_drilldown": null,
                "signals": [],
                "query_identity": null,
                "severity_basis": null
              }
            ]
          },
          "optimization_opportunities": [],
          "historical_analysis_status": "available",
          "summary": {
            "current_health_status": "DEGRADED",
            "health_finding_count": 1,
            "optimization_opportunity_count": 0,
            "highest_severity": "high",
            "affected_database_count": 1,
            "total_findings": 1,
            "critical_count": 0,
            "high_count": 1,
            "medium_count": 0,
            "low_count": 0,
            "excluded_query_count": 1
          },
          "excluded_queries": [
            {
              "reason": "SQLPulse monitoring query",
              "query_hash": "0x99887766",
              "database": "master",
              "sql_preview": "SELECT session_id FROM sys.dm_exec_requests"
            }
          ]
        }
        """;

    /// <summary>Python: <c>QueryHistoryResponse</c>.</summary>
    public const string QueryHistory =
        """
        {
          "query_hash": "0xAB12CD34",
          "database": "AppDb",
          "server": "sql01",
          "samples": [
            {
              "id": 1,
              "snapshot_id": "0f8f6f1e-7b1a-4b0e-9a1c-2f3d4e5a6b7c",
              "collected_at": "2026-08-24T09:00:00Z",
              "server": "sql01",
              "database": "AppDb",
              "query_hash": "0xAB12CD34",
              "sql_text_preview": "SELECT * FROM dbo.Orders",
              "execution_count": 1200,
              "total_cpu_time_ms": 480000,
              "avg_cpu_time_ms": 400.0,
              "total_elapsed_time_ms": 720000,
              "avg_elapsed_time_ms": 600.0,
              "total_logical_reads": 96000,
              "avg_logical_reads": 80.0,
              "total_physical_reads": 9600,
              "avg_physical_reads": 8.0,
              "total_logical_writes": 120,
              "avg_logical_writes": 0.1
            }
          ]
        }
        """;

    /// <summary>Python: <c>QueryBaselineResponse</c> with a computed baseline.</summary>
    public const string QueryBaseline =
        """
        {
          "query_hash": "0xAB12CD34",
          "database": "AppDb",
          "window": "7d",
          "sample_count": 42,
          "baseline_status": "READY",
          "metrics": {
            "avg_cpu_ms": {
              "sample_count": 42,
              "mean": 400.0,
              "median": 380.0,
              "minimum": 120.0,
              "maximum": 980.0,
              "standard_deviation": 145.5,
              "p50": 380.0,
              "p95": 910.0
            }
          }
        }
        """;

    /// <summary>
    /// Python: <c>QueryBaselineResponse</c> when too few samples exist.
    /// </summary>
    /// <remarks>
    /// <c>metrics</c> is empty rather than zero-filled: a zero statistic is
    /// indistinguishable from a query that genuinely measured zero.
    /// </remarks>
    public const string QueryBaselineInsufficient =
        """
        {
          "query_hash": "0xAB12CD34",
          "database": "AppDb",
          "window": "7d",
          "sample_count": 2,
          "baseline_status": "INSUFFICIENT_DATA",
          "metrics": {}
        }
        """;

    /// <summary>Python: <c>SlowHistoryResponse</c>.</summary>
    public const string SlowHistory =
        """
        {
          "server": "sql01",
          "database": "AppDb",
          "start_time": "2026-08-24T09:00:00Z",
          "end_time": "2026-08-24T11:00:00Z",
          "threshold_ms": 30000,
          "slow_threshold_ms": 30000,
          "application_timeout_ms": 30000,
          "timeout_classification_available": true,
          "query_store_status": "AVAILABLE",
          "top_n": 20,
          "total_candidates": 1,
          "slow_query_candidate_count": 1,
          "timeout_candidate_count": 1,
          "candidates": [
            {
              "label": "usp_GetOrders",
              "classification": "TIMEOUT_CANDIDATE",
              "slow_query_candidate": true,
              "timeout_candidate": true,
              "slow_threshold_ms": 30000,
              "application_timeout_ms": 30000,
              "classification_reason": "Reached the configured application timeout of 30000 ms.",
              "query_id": 512,
              "query_hash": "0xAB12CD34",
              "object_id": 1977058079,
              "procedure_name": "usp_GetOrders",
              "plan_id": 640,
              "sql_text_preview": "SELECT * FROM dbo.Orders",
              "execution_count": 12,
              "total_execution_count": 12,
              "intervals_observed": 4,
              "intervals_exceeding_threshold": 3,
              "avg_duration_ms": 31500.0,
              "max_duration_ms": 42000.0,
              "min_duration_ms": 28000.0,
              "avg_cpu_ms": 12000.0,
              "max_cpu_ms": 18000.0,
              "avg_logical_reads": 900000.0,
              "max_logical_reads": 1200000.0,
              "first_execution_time": "2026-08-24T09:05:00Z",
              "last_execution_time": "2026-08-24T10:55:00Z",
              "baseline_comparison": {
                "baseline_status": "READY",
                "sample_count": 42,
                "historical_mean_ms": 8000.0,
                "historical_median_ms": 7600.0,
                "historical_p95_ms": 14000.0,
                "standard_deviation_ms": 2100.0,
                "regression_multiplier_vs_mean": 3.94,
                "regression_multiplier_vs_p95": 2.25
              },
              "confidence": "high",
              "severity": "high",
              "workload_type": "APPLICATION_OR_INTERACTIVE",
              "classification_evidence": [
                "Reached the configured application timeout of 30000 ms"
              ],
              "workload_evidence": [
                {
                  "strength": "strong",
                  "source": "program_name",
                  "detail": "program_name 'OrdersService' matches a configured application mapping",
                  "supports": "APPLICATION_OR_INTERACTIVE"
                }
              ],
              "rank": 1
            }
          ],
          "message": null
        }
        """;

    /// <summary>
    /// Python: <c>SlowHistoryResponse</c> when Query Store is unavailable.
    /// </summary>
    /// <remarks>
    /// Still HTTP 200 — one unavailable feature must not fail the request.
    /// </remarks>
    public const string SlowHistoryUnavailable =
        """
        {
          "server": "sql01",
          "database": "AppDb",
          "start_time": "2026-08-24T09:00:00Z",
          "end_time": "2026-08-24T11:00:00Z",
          "threshold_ms": 30000,
          "slow_threshold_ms": 30000,
          "application_timeout_ms": null,
          "timeout_classification_available": false,
          "query_store_status": "DISABLED",
          "top_n": 20,
          "total_candidates": 0,
          "slow_query_candidate_count": 0,
          "timeout_candidate_count": 0,
          "candidates": [],
          "message": "Query Store is not enabled for database 'AppDb'."
        }
        """;

    /// <summary>
    /// Python: <c>InvestigationResponse</c>.
    /// </summary>
    /// <remarks>
    /// <c>tool_calls_detail</c> is absent because Python declares it
    /// <c>Field(exclude=True)</c> — it can contain raw SQL and is persisted to the audit
    /// trail instead of being returned.
    /// </remarks>
    public const string Investigation =
        """
        {
          "investigation_id": "6c7fe8ca-a9e5-4227-8a58-920b43d59e15",
          "question": "What is affecting AppDb performance right now?",
          "answer": "Session 65 is blocking four sessions in AppDb.",
          "severity": "high",
          "confidence": "medium",
          "affected_database": "AppDb",
          "affected_query": "usp_GetOrders",
          "evidence": ["Session 65 has been open for 210 seconds"],
          "historical_comparison": "4x slower than the 7-day baseline",
          "likely_cause": "An open transaction holding an exclusive lock.",
          "what_is_known": ["The head blocker is session 65"],
          "what_is_uncertain": ["Whether the application will retry"],
          "recommended_investigation": ["Identify the owner of session 65"],
          "human_review_required": true,
          "agent_action": "No database remediation was performed. Observation and recommendation only.",
          "tools_used": ["get_blocking", "get_active_requests"],
          "tool_call_count": 2,
          "duration_seconds": 4.21,
          "timestamp": "2026-08-24T09:00:00Z"
        }
        """;

    /// <summary>Python: <c>InvestigationAuditListResponse</c>.</summary>
    public const string InvestigationAuditList =
        """
        {
          "total": 1,
          "investigations": [
            {
              "investigation_id": "6c7fe8ca-a9e5-4227-8a58-920b43d59e15",
              "created_at": "2026-08-24T09:00:00Z",
              "question_sanitized": "What is affecting AppDb performance right now?",
              "server": "sql01",
              "database": "AppDb",
              "time_range_start": null,
              "time_range_end": null,
              "tools_called": [
                {
                  "tool_name": "get_blocking",
                  "arguments_sanitized": { "database": "AppDb" },
                  "result_summary": { "session_count": 4 },
                  "success": true,
                  "error": null,
                  "duration_seconds": 0.42
                }
              ],
              "finding_references": ["usp_GetOrders", "0xAB12CD34"],
              "severity": "high",
              "confidence": "medium",
              "answer": "Session 65 is blocking four sessions in AppDb.",
              "human_review_required": true,
              "remediation_performed": false
            }
          ]
        }
        """;

    /// <summary>Python: <c>InvestigationAuditDetailResponse</c> for an unknown identifier.</summary>
    public const string InvestigationAuditNotFound =
        """
        {
          "found": false,
          "investigation": null
        }
        """;
}
