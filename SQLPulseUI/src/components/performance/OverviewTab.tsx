import { Stack, Box } from '@mui/material';
import { MetricCard } from '../../components/common';
import type { PerformanceSnapshot } from '../../types/performance';

interface OverviewTabProps {
  snapshot: PerformanceSnapshot;
}

export default function OverviewTab({ snapshot }: OverviewTabProps) {
  const activeCount = snapshot.active_requests?.requests.length;
  const longRunningCount = snapshot.long_running_queries?.queries.length;
  const blockingCount = snapshot.blocking_sessions?.sessions.length;
  const chainCount = snapshot.blocking_sessions?.blocking_chain_count;
  const cpuCount = snapshot.top_cpu_queries?.queries.length;
  const ioCount = snapshot.top_io_queries?.queries.length;

  return (
    <Stack direction="row" flexWrap="wrap" gap={2} sx={{ mt: 2 }}>
      {activeCount != null && (
        <Box sx={{ minWidth: 160, flex: '1 1 160px' }}>
          <MetricCard title="Active Requests" value={activeCount} />
        </Box>
      )}
      {longRunningCount != null && (
        <Box sx={{ minWidth: 160, flex: '1 1 160px' }}>
          <MetricCard
            title="Long Running"
            value={longRunningCount}
            color={longRunningCount > 0 ? 'warning.main' : undefined}
          />
        </Box>
      )}
      {blockingCount != null && (
        <Box sx={{ minWidth: 160, flex: '1 1 160px' }}>
          <MetricCard
            title="Blocking Sessions"
            value={blockingCount}
            color={blockingCount > 0 ? 'error.main' : undefined}
          />
        </Box>
      )}
      {chainCount != null && (
        <Box sx={{ minWidth: 160, flex: '1 1 160px' }}>
          <MetricCard
            title="Blocking Chains"
            value={chainCount}
            color={chainCount > 0 ? 'error.main' : undefined}
          />
        </Box>
      )}
      {cpuCount != null && (
        <Box sx={{ minWidth: 160, flex: '1 1 160px' }}>
          <MetricCard title="CPU Query Count" value={cpuCount} />
        </Box>
      )}
      {ioCount != null && (
        <Box sx={{ minWidth: 160, flex: '1 1 160px' }}>
          <MetricCard title="I/O Query Count" value={ioCount} />
        </Box>
      )}
    </Stack>
  );
}
