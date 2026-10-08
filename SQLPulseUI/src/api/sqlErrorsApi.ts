import apiClient from './client';
import type {
  SqlErrorDetailResponse,
  SqlErrorListResponse,
  SqlErrorParams,
  SqlErrorSummaryResponse,
} from '../types/sqlErrors';

/** List normalized SQL error events for a period. */
export const getSqlErrors = (params: SqlErrorParams) =>
  apiClient.get<SqlErrorListResponse>('/api/sql-errors', { params }).then((r) => r.data);

/** Aggregate counts for the same period and filters. */
export const getSqlErrorSummary = (params: Omit<SqlErrorParams, 'top_n'>) =>
  apiClient.get<SqlErrorSummaryResponse>('/api/sql-errors/summary', { params }).then((r) => r.data);

/**
 * One error with the performance evidence observed around it.
 *
 * SQLPulse does not store error events, so an id resolves only while it remains
 * among the recently retrieved events. An expired id comes back with
 * `found: false` and an explanation rather than an error.
 */
export const getSqlErrorDetail = (id: string, correlate = true) =>
  apiClient
    .get<SqlErrorDetailResponse>(`/api/sql-errors/${encodeURIComponent(id)}`, {
      params: { correlate },
    })
    .then((r) => r.data);
