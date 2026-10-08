import { useState, useCallback } from 'react';
import { Box, Button, Tabs, Tab, Typography, Alert, Stack } from '@mui/material';
import RefreshIcon from '@mui/icons-material/Refresh';
import {
  PageHeader,
  LoadingState,
  ErrorAlert,
  EmptyState,
  AnalysisScopeToolbar,
  CollectorStatus,
} from '../../components/common';
import OverviewTab from '../../components/performance/OverviewTab';
import ActiveRequestsTab from '../../components/performance/ActiveRequestsTab';
import BlockingTab from '../../components/performance/BlockingTab';
import LongRunningTab from '../../components/performance/LongRunningTab';
import TopCpuTab from '../../components/performance/TopCpuTab';
import TopIoTab from '../../components/performance/TopIoTab';
import WaitStatsTab from '../../components/performance/WaitStatsTab';
import { useAnalysis } from '../../hooks/useAnalysis';
import { getPerformanceSnapshot } from '../../api/performanceApi';
import { formatTimestamp } from '../../utils/formatters';
import type { PerformanceSnapshot } from '../../types/performance';

const TAB_LABELS = [
  'Overview',
  'Active Requests',
  'Blocking',
  'Long Running',
  'Top CPU',
  'Top I/O',
  'Wait Statistics',
] as const;

export default function PerformancePage() {
  const { analysisScope, selectedDatabase } = useAnalysis();

  const [snapshot, setSnapshot] = useState<PerformanceSnapshot | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState(0);

  const canRefresh =
    analysisScope === 'server' || (analysisScope === 'database' && !!selectedDatabase);

  const handleRefresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const params =
        analysisScope === 'database' && selectedDatabase
          ? { scope: 'database' as const, database: selectedDatabase }
          : { scope: 'server' as const };
      const data = await getPerformanceSnapshot(params);
      setSnapshot(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to fetch snapshot');
    } finally {
      setLoading(false);
    }
  }, [analysisScope, selectedDatabase]);

  const isDatabaseScope = snapshot?.scope === 'database';

  return (
    <>
      <PageHeader
        title="Performance"
        subtitle="Real-time SQL Server performance snapshot"
      />

      {/* Toolbar + Refresh */}
      <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 2, flexWrap: 'wrap', mb: 0 }}>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <AnalysisScopeToolbar />
        </Box>
        <Button
          variant="contained"
          startIcon={<RefreshIcon />}
          onClick={handleRefresh}
          disabled={loading || !canRefresh}
          sx={{ mt: 0.5, whiteSpace: 'nowrap' }}
        >
          {loading ? 'Loading…' : 'Refresh Snapshot'}
        </Button>
      </Box>

      {/* Database required */}
      {analysisScope === 'database' && !selectedDatabase && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Select a database or switch to Entire Server scope.
        </Alert>
      )}

      {/* Loading */}
      {loading && <LoadingState message="Collecting performance snapshot…" />}

      {/* Error */}
      {error && <ErrorAlert message={error} title="Snapshot Error" />}

      {/* Not collected yet */}
      {!snapshot && !loading && !error && (
        <EmptyState message="Click Refresh Snapshot to collect performance data." />
      )}

      {/* Snapshot results */}
      {snapshot && !loading && (
        <>
          {/* Metadata bar */}
          <Stack direction="row" spacing={2} alignItems="center" sx={{ mb: 2, flexWrap: 'wrap' }}>
            <Typography variant="caption" color="text.secondary">
              Collected {formatTimestamp(snapshot.collected_at)} — {snapshot.scope} scope
              {snapshot.database ? ` — ${snapshot.database}` : ''}
            </Typography>
            <CollectorStatus metadata={snapshot.active_requests?.metadata} label="Active" />
            <CollectorStatus metadata={snapshot.blocking_sessions?.metadata} label="Blocking" />
            <CollectorStatus metadata={snapshot.long_running_queries?.metadata} label="Long Running" />
            <CollectorStatus metadata={snapshot.top_cpu_queries?.metadata} label="CPU" />
            <CollectorStatus metadata={snapshot.top_io_queries?.metadata} label="I/O" />
            <CollectorStatus metadata={snapshot.wait_statistics?.metadata} label="Waits" />
          </Stack>

          {/* Tabs */}
          <Box sx={{ borderBottom: 1, borderColor: 'divider' }}>
            <Tabs
              value={activeTab}
              onChange={(_, v: number) => setActiveTab(v)}
              variant="scrollable"
              scrollButtons="auto"
            >
              {TAB_LABELS.map((label) => (
                <Tab key={label} label={label} />
              ))}
            </Tabs>
          </Box>

          {/* Tab content */}
          <Box sx={{ py: 1 }}>
            {activeTab === 0 && <OverviewTab snapshot={snapshot} />}
            {activeTab === 1 && <ActiveRequestsTab data={snapshot.active_requests} />}
            {activeTab === 2 && <BlockingTab data={snapshot.blocking_sessions} />}
            {activeTab === 3 && <LongRunningTab data={snapshot.long_running_queries} />}
            {activeTab === 4 && <TopCpuTab data={snapshot.top_cpu_queries} />}
            {activeTab === 5 && <TopIoTab data={snapshot.top_io_queries} />}
            {activeTab === 6 && <WaitStatsTab data={snapshot.wait_statistics} isDatabaseScope={isDatabaseScope} />}
          </Box>
        </>
      )}
    </>
  );
}
