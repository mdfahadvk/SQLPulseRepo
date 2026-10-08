import { useQuery } from '@tanstack/react-query';
import { getHealth, testConnection, listDatabases } from '../api/databaseApi';
import { getPerformanceSnapshot, analyzePerformance } from '../api/performanceApi';
import { getSlowHistory } from '../api/historyApi';
import { listInvestigations } from '../api/agentApi';
import { getSqlErrors, getSqlErrorSummary, getSqlErrorDetail } from '../api/sqlErrorsApi';
import type { SnapshotParams, AnalyzeParams } from '../types/performance';
import type { SlowHistoryParams } from '../types/history';
import type { SqlErrorParams } from '../types/sqlErrors';

// ─── Health ───

export const useHealth = () =>
  useQuery({
    queryKey: ['health'],
    queryFn: getHealth,
    refetchInterval: 15_000,
    retry: 1,
  });

// ─── Connection ───

export const useConnectionTest = () =>
  useQuery({
    queryKey: ['connectionTest'],
    queryFn: testConnection,
    retry: 1,
  });

// ─── Databases ───

export const useDatabases = () =>
  useQuery({
    queryKey: ['databases'],
    queryFn: listDatabases,
    staleTime: 60_000,
  });

// ─── Performance Snapshot ───

export const usePerformanceSnapshot = (params: SnapshotParams, enabled = true) =>
  useQuery({
    queryKey: ['performanceSnapshot', params],
    queryFn: () => getPerformanceSnapshot(params),
    enabled,
  });

// ─── Performance Analysis ───

export const usePerformanceAnalysis = (params: AnalyzeParams, enabled = true) =>
  useQuery({
    queryKey: ['performanceAnalysis', params],
    queryFn: () => analyzePerformance(params),
    enabled,
  });

// ─── Slow History ───

export const useSlowHistory = (params: SlowHistoryParams, enabled = true) =>
  useQuery({
    queryKey: ['slowHistory', params],
    queryFn: () => getSlowHistory(params),
    enabled,
  });


// ─── Investigation Audit List ───

export const useInvestigationList = (limit = 50) =>
  useQuery({
    queryKey: ['investigations', limit],
    queryFn: () => listInvestigations(limit),
    retry: 1,
  });

// ─── SQL Errors ───

export const useSqlErrors = (params: SqlErrorParams, enabled = true) =>
  useQuery({
    queryKey: ['sqlErrors', params],
    queryFn: () => getSqlErrors(params),
    enabled,
  });

export const useSqlErrorSummary = (params: Omit<SqlErrorParams, 'top_n'>, enabled = true) =>
  useQuery({
    queryKey: ['sqlErrorSummary', params],
    queryFn: () => getSqlErrorSummary(params),
    enabled,
  });

/**
 * Error events are not stored, so the detail is fetched fresh and never cached
 * across a reload — a stale id would otherwise look like a missing event.
 */
export const useSqlErrorDetail = (id: string | null, correlate = true) =>
  useQuery({
    queryKey: ['sqlErrorDetail', id, correlate],
    queryFn: () => getSqlErrorDetail(id!, correlate),
    enabled: Boolean(id),
    gcTime: 0,
  });
