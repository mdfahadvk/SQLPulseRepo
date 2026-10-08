import apiClient from './client';
import type {
  InvestigationRequest,
  InvestigationResponse,
  InvestigationAuditListResponse,
  InvestigationAuditDetailResponse,
} from '../types/agent';

/**
 * An investigation runs a multi-step LLM tool-calling loop server-side and can
 * legitimately take longer than the default client timeout, so it gets its own
 * budget that is comfortably above the backend's hard cap.
 */
const INVESTIGATION_TIMEOUT_MS = 180_000;

export const investigate = (body: InvestigationRequest) =>
  apiClient
    .post<InvestigationResponse>('/api/agent/investigate', body, {
      timeout: INVESTIGATION_TIMEOUT_MS,
    })
    .then((r) => r.data);

export const listInvestigations = (limit = 50) =>
  apiClient
    .get<InvestigationAuditListResponse>('/api/agent/investigations', {
      params: { limit },
    })
    .then((r) => r.data);

export const getInvestigationDetail = (investigationId: string) =>
  apiClient
    .get<InvestigationAuditDetailResponse>(
      `/api/agent/investigations/${encodeURIComponent(investigationId)}`,
    )
    .then((r) => r.data);
