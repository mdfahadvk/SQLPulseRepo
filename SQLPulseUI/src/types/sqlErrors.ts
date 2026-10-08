// ─── SQL Error Types ───
//
// Mirrors the contract served by /api/sql-errors. Nothing here interprets data:
// the backend decides every category, severity and timeout classification, and
// the UI only renders what it is told.

/** Normalized error category. Assigned by the backend, never inferred here. */
export type SqlErrorCategory =
  | 'TIMEOUT'
  | 'DEADLOCK'
  | 'CONNECTION'
  | 'LOGIN_FAILURE'
  | 'PERMISSION_DENIED'
  | 'CONSTRAINT_VIOLATION'
  | 'CONVERSION_ERROR'
  | 'ARITHMETIC_ERROR'
  | 'INVALID_OBJECT'
  | 'INVALID_COLUMN'
  | 'TRANSACTION_ERROR'
  | 'SQL_EXECUTION_ERROR'
  | 'UNKNOWN';

/**
 * How certain the backend is that a slow or failed statement was a timeout.
 *
 * These four are never interchangeable in the UI. Only
 * `CONFIRMED_APPLICATION_TIMEOUT` means something observed a client giving up;
 * `TIMEOUT_CANDIDATE` means SQLPulse inferred it from performance evidence, and
 * `SLOW_QUERY` means the statement was merely slow.
 */
export type TimeoutClassification =
  | 'CONFIRMED_APPLICATION_TIMEOUT'
  | 'TIMEOUT_CANDIDATE'
  | 'SLOW_QUERY'
  | 'NOT_APPLICABLE';

/** What kind of evidence a source contributes. */
export type SqlErrorSourceKind =
  | 'APPLICATION_TELEMETRY'
  | 'SQL_SERVER'
  | 'DERIVED_PERFORMANCE_EVIDENCE';

/** Outcome of asking one source for a time range. */
export type SqlErrorSourceStatus =
  | 'AVAILABLE'
  | 'NOT_CONFIGURED'
  | 'NOT_APPLICABLE'
  | 'PERMISSION_DENIED'
  | 'FAILED';

/**
 * Overall outcome of a query.
 *
 * `NO_ERRORS_FOUND` and `ERROR_SOURCE_UNAVAILABLE` must never be rendered the
 * same way — one is a quiet period, the other is blindness.
 */
export type SqlErrorQueryStatus =
  | 'OK'
  | 'NO_ERRORS_FOUND'
  | 'PARTIAL_ERROR_DATA'
  | 'ERROR_SOURCE_UNAVAILABLE';

/** Strength of a correlation. Never a causal claim, even at HIGH. */
export type CorrelationConfidence = 'HIGH' | 'MEDIUM' | 'LOW' | 'NONE';

/** Which SQLPulse capability a correlated observation came from. */
export type CorrelatedEvidenceKind =
  | 'QUERY_STORE_HISTORY'
  | 'QUERY_BASELINE'
  | 'BLOCKING'
  | 'LONG_RUNNING_QUERY';

export type SqlErrorSeverity = 'critical' | 'high' | 'medium' | 'low';

export interface SqlErrorSourceState {
  name: string;
  kind: SqlErrorSourceKind;
  status: SqlErrorSourceStatus;
  event_count: number;
  message: string | null;
}

export type ApplicationTelemetryStatus =
  | 'NOT_CONFIGURED' | 'CONFIGURED' | 'AVAILABLE' | 'NO_DATA' | 'UNAVAILABLE'
  | 'PERMISSION_DENIED' | 'PARTIAL';

export interface ApplicationTelemetryApplication {
  name: string;
  service_name: string;
  database: string | null;
  status: ApplicationTelemetryStatus;
  error_count: number;
}

export interface ApplicationTelemetrySummary {
  status: ApplicationTelemetryStatus;
  applications: ApplicationTelemetryApplication[];
}

export interface SqlErrorEvent {
  id: string;
  timestamp: string;
  server: string;
  database: string | null;
  application: string | null;
  environment: string | null;
  host: string | null;
  session_identifier: string | null;
  trace_id: string | null;
  correlation_id: string | null;
  request_endpoint: string | null;
  stored_procedure: string | null;
  query_hash: string | null;
  sql_error_number: number | null;
  sql_error_state: number | null;
  sql_error_class: number | null;
  error_category: SqlErrorCategory;
  message: string;
  exception_type: string | null;
  command_timeout_seconds: number | null;
  duration_ms: number | null;
  parameters: Record<string, string | null>;
  /** Name of the source that reported the event. */
  source: string;
  source_kind: SqlErrorSourceKind;
  is_timeout: boolean;
  is_deadlock: boolean;
  severity: SqlErrorSeverity;
  timeout_classification: TimeoutClassification;
  /** Why the backend chose this category and classification. */
  classification_evidence: string[];
  /** False when SQLPulse derived the event rather than observing it reported. */
  confirmed: boolean;
}

interface SqlErrorResponseBase {
  server: string;
  database: string | null;
  start_time: string;
  end_time: string;
  status: SqlErrorQueryStatus;
  message: string;
  /**
   * False when no application feed answered. SQLPulse can hold complete SQL
   * Server evidence and still be blind to what the application experienced.
   */
  application_telemetry_available: boolean;
  sources: SqlErrorSourceState[];
  application_telemetry: ApplicationTelemetrySummary | null;
}

export interface SqlErrorListResponse extends SqlErrorResponseBase {
  total_matched: number;
  returned: number;
  top_n: number;
  errors: SqlErrorEvent[];
}

export interface SqlErrorSummaryResponse extends SqlErrorResponseBase {
  total_errors: number;
  timeout_count: number;
  deadlock_count: number;
  connection_error_count: number;
  permission_error_count: number;
  other_error_count: number;
  /** Timeouts an application or telemetry source actually recorded. */
  confirmed_timeout_count: number;
  /** Timeouts inferred from performance evidence alone. */
  timeout_candidate_count: number;
  affected_databases: string[];
  affected_applications: string[];
  category_counts: Partial<Record<SqlErrorCategory, number>>;
}

export interface CorrelatedEvidenceItem {
  kind: CorrelatedEvidenceKind;
  source: string;
  summary: string;
  detail: string[];
  /** query_hash, stored_procedure, database or time_window. */
  matched_on: string[];
  observed_at: string | null;
}

export interface SqlErrorCorrelation {
  window_start: string;
  window_end: string;
  window_minutes: number;
  correlation_confidence: CorrelationConfidence;
  identity_matched: boolean;
  correlated_evidence: CorrelatedEvidenceItem[];
  /** Deterministic, non-causal reading produced by the backend. */
  assessment: string;
  caveats: string[];
}

export interface SqlErrorDetailResponse {
  found: boolean;
  error: SqlErrorEvent | null;
  correlation: SqlErrorCorrelation | null;
  message: string | null;
}

export interface SqlErrorParams {
  server?: string;
  database?: string;
  start_time: string;
  end_time: string;
  category?: SqlErrorCategory;
  application?: string;
  stored_procedure?: string;
  severity?: SqlErrorSeverity;
  confirmed_only?: boolean;
  top_n?: number;
}
