// ─── Agent / Investigation Types ───

export interface InvestigationRequest {
  question: string;
}

export interface ToolCall {
  tool_name: string;
  arguments: Record<string, unknown>;
  arguments_sanitized: Record<string, unknown>;
  result_summary: Record<string, unknown>;
  success: boolean;
  error: string | null;
  duration_seconds: number;
}

export interface InvestigationResponse {
  investigation_id: string;
  question: string;
  answer: string;
  severity: string;
  confidence: string;
  affected_database: string | null;
  affected_query: string | null;
  evidence: string[];
  historical_comparison: string | null;
  likely_cause: string | null;
  what_is_known: string[];
  what_is_uncertain: string[];
  recommended_investigation: string[];
  human_review_required: boolean;
  agent_action: string;
  tools_used: string[];
  tool_calls_detail: ToolCall[];
  tool_call_count: number;
  duration_seconds: number;
  timestamp: string;
}

export interface AuditToolCall {
  tool_name: string;
  arguments_sanitized: Record<string, unknown>;
  result_summary: Record<string, unknown>;
  success: boolean;
  error: string | null;
  duration_seconds: number;
}

export interface InvestigationAuditRecord {
  investigation_id: string;
  created_at: string;
  question_sanitized: string;
  server: string;
  database: string | null;
  time_range_start: string | null;
  time_range_end: string | null;
  tools_called: AuditToolCall[];
  finding_references: string[];
  severity: string;
  confidence: string;
  answer: string;
  human_review_required: boolean;
  remediation_performed: boolean;
}

export interface InvestigationAuditListResponse {
  total: number;
  investigations: InvestigationAuditRecord[];
}

export interface InvestigationAuditDetailResponse {
  found: boolean;
  investigation: InvestigationAuditRecord | null;
}
