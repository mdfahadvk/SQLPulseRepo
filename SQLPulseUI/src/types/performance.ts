// ─── Performance Types ───

export interface CollectorMetadata {
  collector_name: string;
  server_host: string;
  database: string;
  collected_at: string;
  success: boolean;
  error_message: string | null;
  permission_issue: boolean;
}

export interface ActiveRequest {
  session_id: number;
  request_id: number;
  status: string;
  command: string;
  sql_text: string | null;
  database_name: string | null;
  wait_type: string | null;
  wait_time_ms: number;
  cpu_time_ms: number;
  total_elapsed_time_ms: number;
  reads: number;
  writes: number;
  logical_reads: number;
  blocking_session_id: number | null;
  open_transaction_count: number;
  login_name: string | null;
  host_name: string | null;
  program_name: string | null;
}

export interface ActiveRequestsResult {
  metadata: CollectorMetadata;
  requests: ActiveRequest[];
}

export interface LongRunningQuery {
  session_id: number;
  request_id: number;
  sql_text: string | null;
  database_name: string | null;
  elapsed_time_ms: number;
  cpu_time_ms: number;
  reads: number;
  logical_reads: number;
  writes: number;
  wait_type: string | null;
  wait_time_ms: number;
  status: string;
  login_name: string | null;
  host_name: string | null;
  program_name: string | null;
}

export interface LongRunningQueriesResult {
  metadata: CollectorMetadata;
  threshold_ms: number;
  queries: LongRunningQuery[];
}

export interface BlockingSession {
  session_id: number;
  blocking_session_id: number | null;
  wait_type: string | null;
  wait_time_ms: number;
  wait_resource: string | null;
  database_name: string | null;
  sql_text: string | null;
  status: string;
  login_name: string | null;
  host_name: string | null;
  program_name: string | null;
  is_head_blocker: boolean;
}

export interface BlockingSessionsResult {
  metadata: CollectorMetadata;
  sessions: BlockingSession[];
  blocking_chain_count: number;
}

export interface TopCpuQuery {
  sql_text: string | null;
  query_hash: string | null;
  execution_count: number;
  total_cpu_time_ms: number;
  avg_cpu_time_ms: number;
  total_elapsed_time_ms: number;
  avg_elapsed_time_ms: number;
  total_logical_reads: number;
  avg_logical_reads: number;
  database_name: string | null;
}

export interface TopCpuQueriesResult {
  metadata: CollectorMetadata;
  top_n: number;
  queries: TopCpuQuery[];
}

export interface TopIoQuery {
  sql_text: string | null;
  query_hash: string | null;
  execution_count: number;
  total_logical_reads: number;
  avg_logical_reads: number;
  total_physical_reads: number;
  avg_physical_reads: number;
  total_logical_writes: number;
  avg_logical_writes: number;
  total_elapsed_time_ms: number;
  database_name: string | null;
}

export interface TopIoQueriesResult {
  metadata: CollectorMetadata;
  top_n: number;
  queries: TopIoQuery[];
}

export interface WaitStatistic {
  wait_type: string;
  waiting_tasks_count: number;
  wait_time_ms: number;
  max_wait_time_ms: number;
  signal_wait_time_ms: number;
  resource_wait_time_ms: number;
}

export interface WaitStatisticsTotals {
  waiting_tasks_count: number;
  wait_time_ms: number;
  signal_wait_time_ms: number;
  resource_wait_time_ms: number;
}

export interface WaitStatisticsDelta {
  previous_collected_at: string;
  interval_seconds: number;
  waiting_tasks_count: number;
  wait_time_ms: number;
  signal_wait_time_ms: number;
  resource_wait_time_ms: number;
}

export type WaitDeltaStatus =
  | 'available'
  | 'insufficient_previous_data'
  | 'server_restarted'
  | 'not_applicable';

export type WaitStatisticsApplicability =
  | 'server_level'
  | 'not_applicable_database_scope';

export interface WaitStatisticsResult {
  metadata: CollectorMetadata;
  top_n: number;
  statistics: WaitStatistic[];
  applicability: WaitStatisticsApplicability;
  collected_at: string;
  sqlserver_start_time: string | null;
  totals: WaitStatisticsTotals | null;
  delta: WaitStatisticsDelta | null;
  delta_status: WaitDeltaStatus;
}

export interface PerformanceSnapshot {
  snapshot_id: string;
  collected_at: string;
  server_host: string;
  scope: string;
  database: string | null;
  active_requests: ActiveRequestsResult | null;
  long_running_queries: LongRunningQueriesResult | null;
  blocking_sessions: BlockingSessionsResult | null;
  top_cpu_queries: TopCpuQueriesResult | null;
  top_io_queries: TopIoQueriesResult | null;
  wait_statistics: WaitStatisticsResult | null;
}

// ─── Findings / Analysis ───

export type Severity = 'critical' | 'high' | 'medium' | 'low';
export type Confidence = 'high' | 'medium' | 'low';
export type OverallStatus = 'HEALTHY' | 'WARNING' | 'DEGRADED' | 'CRITICAL' | 'UNKNOWN';

export type FindingCategory =
  | 'long_running_query'
  | 'blocking'
  | 'high_cpu'
  | 'high_io'
  | 'query_regression'
  | 'slow_query_candidate'
  | 'timeout_candidate';

export interface RecommendedDrilldown {
  scope: string;
  database: string;
}

export type FindingSignal = 'high_cpu' | 'high_io';

export interface QueryIdentity {
  query_hash: string | null;
  query_plan_hash: string | null;
  object_name: string | null;
}

export interface PerformanceFinding {
  finding_id: string;
  category: FindingCategory;
  server: string;
  /** Null when plan-cache metadata could not resolve the database. */
  database: string | null;
  severity: Severity;
  confidence: Confidence;
  first_observed: string;
  last_observed: string;
  summary: string;
  evidence: string[];
  likely_cause: string;
  recommendation: string;
  validation_steps: string[];
  human_review_required: boolean;
  recommended_drilldown: RecommendedDrilldown | null;
  signals: FindingSignal[];
  query_identity: QueryIdentity | null;
  severity_basis: string | null;
}

export interface ExcludedQuery {
  reason: string;
  query_hash: string | null;
  database: string | null;
  sql_preview: string | null;
}

export interface AnalysisSummary {
  current_health_status: OverallStatus;
  health_finding_count: number;
  optimization_opportunity_count: number;
  highest_severity: string | null;
  affected_database_count: number;
  total_findings: number;
  critical_count: number;
  high_count: number;
  medium_count: number;
  low_count: number;
  excluded_query_count: number;
}

export interface AffectedDatabase {
  database: string;
  highest_severity: string;
  finding_count: number;
}

export interface HealthSection {
  status: OverallStatus;
  findings: PerformanceFinding[];
}

export interface AnalysisResult {
  analysis_id: string;
  analyzed_at: string;
  server: string;
  scope: string;
  database: string | null;
  overall_status: OverallStatus;
  findings: PerformanceFinding[];
  affected_databases: AffectedDatabase[];
  total_findings: number;
  critical_count: number;
  high_count: number;
  medium_count: number;
  low_count: number;
  current_health: HealthSection | null;
  optimization_opportunities: PerformanceFinding[];
  historical_analysis_status: string;
  summary: AnalysisSummary | null;
  excluded_queries: ExcludedQuery[];
}

export interface SnapshotParams {
  scope: 'server' | 'database';
  database?: string;
  long_running_threshold_ms?: number;
  top_n?: number;
  wait_stats_top_n?: number;
}

export interface AnalyzeParams {
  scope: 'server' | 'database';
  database?: string;
  long_running_threshold_ms?: number;
  top_n?: number;
  wait_stats_top_n?: number;
}

export type ExecutionPlanStatus = 'AVAILABLE' | 'NOT_FOUND' | 'UNAVAILABLE' | 'PERMISSION_DENIED';

export interface ExecutionPlanFinding {
  category: string;
  operator: string;
  severity: string;
  confidence: string;
  summary: string;
  evidence: string[];
  recommendation: string;
  human_review_required: boolean;
}

export interface ExecutionPlanOperator {
  type: string;
  object: string | null;
  estimated_cost_percent: number | null;
  estimated_rows: number | null;
  actual_rows: number | null;
}

export interface ExecutionPlanParameter {
  name: string;
  data_type: string | null;
  compiled_value: string | null;
  runtime_value: string | null;
}

export interface ExecutionPlanResponse {
  status: ExecutionPlanStatus;
  query_hash: string | null;
  query_id: number | null;
  plan_id: number | null;
  database: string | null;
  retrieved_at: string;
  plan_xml: string | null;
  parameters?: ExecutionPlanParameter[];
  statement_count: number;
  total_operator_count: number;
  operator_summary: string[];
  operator_counts_by_type: Record<string, number>;
  highest_cost_operators: ExecutionPlanOperator[];
  plan_limitations: string[];
  operators: ExecutionPlanOperator[];
  findings: ExecutionPlanFinding[];
  warnings: string[];
  message: string | null;
}
