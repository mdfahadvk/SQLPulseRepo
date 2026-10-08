import { useState, useCallback, useEffect } from 'react';
import {
  Box,
  TextField,
  MenuItem,
  Button,
  Paper,
  Typography,
  CircularProgress,
} from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import { PageHeader, LoadingState, ErrorAlert, EmptyState } from '../../components/common';
import QueryHistoryCharts from '../../components/query-history/QueryHistoryCharts';
import BaselineCard from '../../components/query-history/BaselineCard';
import RegressionCard from '../../components/query-history/RegressionCard';
import { useAnalysis } from '../../hooks/useAnalysis';
import { getQueryHistory, getQueryBaseline } from '../../api/historyApi';
import { formatTimestamp } from '../../utils/formatters';
import type { QueryHistoryResponse, QueryBaselineResponse } from '../../types/history';

const BASELINE_WINDOWS = ['1h', '24h', '7d', '30d'] as const;

export default function QueryHistoryPage() {
  const { databases, databasesLoading } = useAnalysis();

  const [database, setDatabase] = useState('');
  const [queryHash, setQueryHash] = useState('');
  const [limit, setLimit] = useState(500);
  const [baselineWindow, setBaselineWindow] = useState<(typeof BASELINE_WINDOWS)[number]>('7d');

  const [history, setHistory] = useState<QueryHistoryResponse | null>(null);
  const [baseline, setBaseline] = useState<QueryBaselineResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Select the first database once the list resolves.
  useEffect(() => {
    if (databases.length > 0 && !database) {
      setDatabase(databases[0]!);
    }
  }, [databases, database]);

  const handleSearch = useCallback(async () => {
    const hash = queryHash.trim();
    if (!hash || !database) return;
    setLoading(true);
    setError(null);
    setHistory(null);
    setBaseline(null);
    try {
      const [historyResult, baselineResult] = await Promise.all([
        getQueryHistory(hash, database, limit),
        getQueryBaseline(hash, database, baselineWindow),
      ]);
      setHistory(historyResult);
      setBaseline(baselineResult);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to fetch query history');
    } finally {
      setLoading(false);
    }
  }, [queryHash, database, limit, baselineWindow]);

  const latestSample =
    history && history.samples.length > 0
      ? [...history.samples].sort(
          (a, b) => new Date(b.collected_at).getTime() - new Date(a.collected_at).getTime(),
        )[0]!
      : null;

  const timeRange =
    history && history.samples.length > 0
      ? (() => {
          const sorted = [...history.samples].sort(
            (a, b) => new Date(a.collected_at).getTime() - new Date(b.collected_at).getTime(),
          );
          return {
            from: formatTimestamp(sorted[0]!.collected_at),
            to: formatTimestamp(sorted[sorted.length - 1]!.collected_at),
          };
        })()
      : null;

  return (
    <>
      <PageHeader
        title="Query History"
        subtitle="Historical query metrics and baseline comparisons"
      />

      {/* ── Inputs ── */}
      <Paper
        variant="outlined"
        sx={{ p: 2, mb: 3 }}
        component="section"
        aria-label="Query history search"
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !loading && queryHash.trim() && database) {
            e.preventDefault();
            void handleSearch();
          }
        }}
      >
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2, alignItems: 'flex-start' }}>
          <TextField
            select
            label="Database"
            value={database}
            onChange={(e) => setDatabase(e.target.value)}
            size="small"
            sx={{ minWidth: 200 }}
            disabled={databasesLoading}
          >
            {databases.map((db) => (
              <MenuItem key={db} value={db}>
                {db}
              </MenuItem>
            ))}
          </TextField>

          <TextField
            label="Query Hash"
            value={queryHash}
            onChange={(e) => setQueryHash(e.target.value)}
            size="small"
            sx={{ minWidth: 260 }}
            placeholder="e.g. 0xABCD1234…"
            helperText="Copy from Slow Queries or Performance tables"
          />

          <TextField
            label="History Limit"
            type="number"
            value={limit}
            onChange={(e) => setLimit(Math.max(1, Math.min(5000, Number(e.target.value) || 500)))}
            size="small"
            sx={{ width: 130 }}
            slotProps={{ htmlInput: { min: 1, max: 5000 } }}
          />

          <TextField
            select
            label="Baseline Window"
            value={baselineWindow}
            onChange={(e) => setBaselineWindow(e.target.value as typeof baselineWindow)}
            size="small"
            sx={{ width: 150 }}
          >
            {BASELINE_WINDOWS.map((w) => (
              <MenuItem key={w} value={w}>
                {w}
              </MenuItem>
            ))}
          </TextField>

          <Button
            variant="contained"
            startIcon={loading ? <CircularProgress size={18} color="inherit" /> : <SearchIcon />}
            onClick={handleSearch}
            disabled={loading || !queryHash.trim() || !database}
          >
            Search
          </Button>
        </Box>
      </Paper>

      {/* ── Loading ── */}
      {loading && <LoadingState message="Fetching query history…" />}

      {/* ── Error ── */}
      {error && (
        <ErrorAlert
          message={error}
          title="Query History Error"
          onRetry={() => void handleSearch()}
        />
      )}

      {/* ── Empty ── */}
      {!history && !loading && !error && (
        <EmptyState
          title="No query selected"
          message="Enter a query hash and database, then click Search."
        />
      )}

      {/* ── Searched, but no samples ── */}
      {history && !loading && history.samples.length === 0 && (
        <EmptyState
          title="No history recorded"
          message="No samples were captured for this query hash in the selected database."
        />
      )}

      {/* ── Results ── */}
      {history && !loading && history.samples.length > 0 && (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
          {/* Query Summary */}
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="subtitle1" fontWeight={600} gutterBottom>
              Query Summary
            </Typography>
            <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 3 }}>
              <Box>
                <Typography variant="caption" color="text.secondary">
                  Query Hash
                </Typography>
                <Typography
                  variant="body2"
                  fontWeight={500}
                  fontFamily="monospace"
                  sx={{ wordBreak: 'break-all', maxWidth: 320 }}
                >
                  {history.query_hash}
                </Typography>
              </Box>
              <Box>
                <Typography variant="caption" color="text.secondary">
                  Database
                </Typography>
                <Typography variant="body2" fontWeight={500}>
                  {history.database}
                </Typography>
              </Box>
              <Box>
                <Typography variant="caption" color="text.secondary">
                  Sample Count
                </Typography>
                <Typography variant="body2" fontWeight={500}>
                  {history.samples.length}
                </Typography>
              </Box>
              {timeRange && (
                <Box>
                  <Typography variant="caption" color="text.secondary">
                    Time Range
                  </Typography>
                  <Typography variant="body2" fontWeight={500}>
                    {timeRange.from} — {timeRange.to}
                  </Typography>
                </Box>
              )}
            </Box>
          </Paper>

          {/* Charts */}
          <QueryHistoryCharts samples={history.samples} />

          {/* Baseline */}
          {baseline && <BaselineCard baseline={baseline} />}

          {/* Regression */}
          {baseline && (
            <RegressionCard baseline={baseline} latestSample={latestSample} />
          )}
        </Box>
      )}
    </>
  );
}
