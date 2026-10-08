// ─── History Types ───

export type BaselineStatus = 'READY' | 'INSUFFICIENT_DATA';

export interface QueryMetricRecord {
  id: number | null;
  snapshot_id: string;
  collected_at: string;
  server: string;
  database: string;
  query_hash: string;
  sql_text_preview: string | null;
  execution_count: number;
  total_cpu_time_ms: number;
  avg_cpu_time_ms: number;
  total_elapsed_time_ms: number;
  avg_elapsed_time_ms: number;
  total_logical_reads: number;
  avg_logical_reads: number;
  total_physical_reads: number;
  avg_physical_reads: number;
  total_logical_writes: number;
  avg_logical_writes: number;
}

export interface BaselineStatistic {
  sample_count: number;
  mean: number;
  median: number;
  minimum: number;
  maximum: number;
  standard_deviation: number;
  p50: number;
  p95: number;
}

export interface QueryHistoryResponse {
  query_hash: string;
  database: string;
  server: string;
  samples: QueryMetricRecord[];
}

export interface QueryBaselineResponse {
  query_hash: string;
  database: string;
  window: string;
  sample_count: number;
  baseline_status: BaselineStatus;
  metrics: Record<string, BaselineStatistic>;
}

// ─── Slow History / Query Store ───

export type WorkloadType =
  | 'APPLICATION_OR_INTERACTIVE'
  | 'BACKGROUND_OR_SCHEDULED'
  | 'UNKNOWN';

export type QueryStoreStatus =
  | 'AVAILABLE'
  | 'AVAILABLE_READ_ONLY'
  | 'DISABLED'
  | 'PERMISSION_DENIED'
  | 'QUERY_FAILED';

export interface BaselineComparison {
  baseline_status: string;
  sample_count: number;
  historical_mean_ms: number;
  historical_median_ms: number;
  historical_p95_ms: number;
  standard_deviation_ms: number;
  regression_multiplier_vs_mean: number;
  regression_multiplier_vs_p95: number;
}

export type CandidateClassification = 'SLOW_QUERY_CANDIDATE' | 'TIMEOUT_CANDIDATE';

export type EvidenceStrength = 'strong' | 'weak';

export interface WorkloadEvidence {
  strength: EvidenceStrength;
  source: string;
  detail: string;
  supports: WorkloadType;
}

export interface TimeoutCandidate {
  label: string;
  /** SLOW_QUERY_CANDIDATE unless an application timeout was configured and reached. */
  classification: CandidateClassification;
  slow_query_candidate: boolean;
  timeout_candidate: boolean;
  slow_threshold_ms: number;
  /** Null when no application timeout is configured — never inferred. */
  application_timeout_ms: number | null;
  classification_reason: string;
  query_id: number | null;
  query_hash: string | null;
  object_id: number | null;
  procedure_name: string | null;
  plan_id: number | null;
  sql_text_preview: string | null;
  execution_count: number;
  total_execution_count: number;
  intervals_observed: number;
  intervals_exceeding_threshold: number;
  avg_duration_ms: number;
  max_duration_ms: number;
  min_duration_ms: number;
  avg_cpu_ms: number;
  max_cpu_ms: number;
  avg_logical_reads: number;
  max_logical_reads: number;
  first_execution_time: string | null;
  last_execution_time: string | null;
  baseline_comparison: BaselineComparison | null;
  confidence: string;
  severity: string;
  workload_type: WorkloadType;
  classification_evidence: string[];
  workload_evidence: WorkloadEvidence[];
  rank: number | null;
}

export interface SlowHistoryResponse {
  server: string;
  database: string;
  start_time: string;
  end_time: string;
  threshold_ms: number;
  slow_threshold_ms: number;
  application_timeout_ms: number | null;
  /** False when no application timeout is configured, so timeout status is unknown. */
  timeout_classification_available: boolean;
  query_store_status: QueryStoreStatus;
  top_n: number;
  total_candidates: number;
  slow_query_candidate_count: number;
  timeout_candidate_count: number;
  candidates: TimeoutCandidate[];
  message: string | null;
}

export interface SlowHistoryParams {
  database: string;
  start_time: string;
  end_time: string;
  threshold_ms?: number;
  application_timeout_ms?: number;
  top_n?: number;
  group_results?: boolean;
  workload_type?: WorkloadType;
}
