import apiClient from './client';
import type {
  PerformanceSnapshot,
  AnalysisResult,
  SnapshotParams,
  AnalyzeParams,
  ExecutionPlanResponse,
} from '../types/performance';

export const getPerformanceSnapshot = (params: SnapshotParams) =>
  apiClient
    .get<PerformanceSnapshot>('/api/performance/snapshot', { params })
    .then((r) => r.data);

export const analyzePerformance = (params: AnalyzeParams) =>
  apiClient
    .get<AnalysisResult>('/api/performance/analyze', { params })
    .then((r) => r.data);

export const getExecutionPlan = (params: {
  database?: string;
  query_hash?: string;
  query_id?: number;
  plan_id?: number;
  include_xml?: boolean;
}) =>
  apiClient
    .get<ExecutionPlanResponse>('/api/performance/execution-plan', {
      params: Object.fromEntries(
        Object.entries({ include_xml: false, ...params }).filter(([, value]) => value != null),
      ),
    })
    .then((r) => r.data);
