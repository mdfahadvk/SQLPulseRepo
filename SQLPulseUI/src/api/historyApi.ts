import apiClient from './client';
import type {
  QueryHistoryResponse,
  QueryBaselineResponse,
  SlowHistoryResponse,
  SlowHistoryParams,
} from '../types/history';

export const getQueryHistory = (queryHash: string, database: string, limit = 500) =>
  apiClient
    .get<QueryHistoryResponse>(`/api/history/query/${encodeURIComponent(queryHash)}`, {
      params: { database, limit },
    })
    .then((r) => r.data);

export const getQueryBaseline = (
  queryHash: string,
  database: string,
  window: '1h' | '24h' | '7d' | '30d' = '7d',
) =>
  apiClient
    .get<QueryBaselineResponse>(
      `/api/history/query/${encodeURIComponent(queryHash)}/baseline`,
      { params: { database, window } },
    )
    .then((r) => r.data);

export const getSlowHistory = (params: SlowHistoryParams) =>
  apiClient
    .get<SlowHistoryResponse>('/api/performance/slow-history', { params })
    .then((r) => r.data);
