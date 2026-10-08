import { useState, useCallback, useEffect } from 'react';
import { Box, Typography, Alert } from '@mui/material';
import { PageHeader, LoadingState, ErrorAlert, EmptyState } from '../../components/common';
import SlowQueryFilters, {
  getDefaultFilterValues,
  type SlowQueryFilterValues,
} from '../../components/slow-queries/SlowQueryFilters';
import SlowQueryResultsTable from '../../components/slow-queries/SlowQueryResultsTable';
import SlowQueryDetailDrawer, { useSlowQueryDrawer } from '../../components/slow-queries/SlowQueryDetailDrawer';
import QueryStoreStatusBadge from '../../components/slow-queries/QueryStoreStatusBadge';
import { useAnalysis } from '../../hooks/useAnalysis';
import { getSlowHistory } from '../../api/historyApi';
import { formatTimestamp } from '../../utils/formatters';
import type { SlowHistoryResponse } from '../../types/history';

export default function SlowQueriesPage() {
  const { databases, databasesLoading } = useAnalysis();
  const drawer = useSlowQueryDrawer();

  const [filters, setFilters] = useState<SlowQueryFilterValues>(() =>
    getDefaultFilterValues(databases),
  );
  const [result, setResult] = useState<SlowHistoryResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Update default database when databases load
  useEffect(() => {
    if (databases.length > 0 && !filters.database) {
      setFilters((prev) => ({ ...prev, database: databases[0]! }));
    }
  }, [databases, filters.database]);

  const handleAnalyze = useCallback(async () => {
    if (!filters.database) return;
    setLoading(true);
    setError(null);
    try {
      const startIso = new Date(filters.startTime).toISOString();
      const endIso = new Date(filters.endTime).toISOString();
      const data = await getSlowHistory({
        database: filters.database,
        start_time: startIso,
        end_time: endIso,
        threshold_ms: filters.thresholdMs,
        top_n: filters.topN,
        ...(filters.workloadType ? { workload_type: filters.workloadType } : {}),
      });
      setResult(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to fetch slow query history');
    } finally {
      setLoading(false);
    }
  }, [filters]);

  const queryStoreAccessible =
    !result ||
    result.query_store_status === 'AVAILABLE' ||
    result.query_store_status === 'AVAILABLE_READ_ONLY';

  return (
    <>
      <PageHeader
        title="Slow Queries"
        subtitle="Query Store based slow-query and timeout candidate analysis"
      />

      {/* Filters */}
      <SlowQueryFilters
        values={filters}
        databases={databases}
        databasesLoading={databasesLoading}
        onChange={setFilters}
        onAnalyze={handleAnalyze}
        loading={loading}
      />

      {/* Loading */}
      {loading && <LoadingState message="Querying slow-query history…" />}

      {/* Error */}
      {error && <ErrorAlert message={error} title="Slow Query Error" />}

      {/* Not analyzed yet */}
      {!result && !loading && !error && (
        <EmptyState message="Select a database and time range, then click Analyze History." />
      )}

      {/* Results */}
      {result && !loading && (
        <>
          {/* Metadata */}
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, mb: 2, flexWrap: 'wrap' }}>
            <QueryStoreStatusBadge status={result.query_store_status} />
            <Typography variant="caption" color="text.secondary">
              {result.database} — {formatTimestamp(result.start_time)} to {formatTimestamp(result.end_time)}
              {' — '}threshold {result.threshold_ms / 1000}s
              {' — '}{result.total_candidates} candidate{result.total_candidates !== 1 ? 's' : ''}
            </Typography>
          </Box>

          {/* Message from backend */}
          {result.message && (
            <Alert severity="info" sx={{ mb: 2 }}>{result.message}</Alert>
          )}

          {/* Query Store unavailable */}
          {!queryStoreAccessible && (
            <Alert severity="warning" sx={{ mb: 2 }}>
              Query Store is not accessible for <strong>{result.database}</strong>.
              Slow-query analysis requires Query Store to be enabled.
            </Alert>
          )}

          {/* Results table */}
          {queryStoreAccessible && (
            <SlowQueryResultsTable
              candidates={result.candidates}
              thresholdMs={result.threshold_ms}
              onCandidateClick={drawer.open}
            />
          )}
        </>
      )}

      {/* Detail drawer */}
      <SlowQueryDetailDrawer
        candidate={drawer.selected}
        database={result?.database ?? null}
        thresholdMs={result?.threshold_ms ?? filters.thresholdMs}
        onClose={drawer.close}
      />
    </>
  );
}
