import {
  Paper,
  Typography,
  Box,
  Alert,
  Tooltip,
} from '@mui/material';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import { formatFloat } from '../../utils/formatters';
import type { QueryBaselineResponse, BaselineStatistic } from '../../types/history';

interface Props {
  baseline: QueryBaselineResponse;
}

const METRIC_LABELS: Record<string, string> = {
  avg_elapsed_ms: 'Avg Duration (ms)',
  avg_cpu_ms: 'Avg CPU (ms)',
  avg_logical_reads: 'Avg Logical Reads',
  avg_physical_reads: 'Avg Physical Reads',
  execution_count: 'Execution Count',
};

const STAT_TOOLTIPS: Record<string, string> = {
  Mean: 'Average value across all samples',
  Median: 'Typical middle value — half of observations are above, half below',
  P50: '50th percentile — same as median',
  P95: '95% of observations were at or below this value',
  'Std Dev': 'How much performance normally varies from the mean',
};

function StatLabel({ label }: { label: string }) {
  const tip = STAT_TOOLTIPS[label];
  return (
    <Box sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5 }}>
      <Typography variant="caption" color="text.secondary" fontWeight={600}>
        {label}
      </Typography>
      {tip && (
        <Tooltip title={tip} arrow placement="top">
          <InfoOutlinedIcon sx={{ fontSize: 14, color: 'text.disabled', cursor: 'help' }} />
        </Tooltip>
      )}
    </Box>
  );
}

function StatRow({ stat }: { stat: BaselineStatistic }) {
  const items: { label: string; value: string }[] = [
    { label: 'Samples', value: stat.sample_count.toLocaleString() },
    { label: 'Mean', value: formatFloat(stat.mean, 2) },
    { label: 'Median', value: formatFloat(stat.median, 2) },
    { label: 'P50', value: formatFloat(stat.p50, 2) },
    { label: 'P95', value: formatFloat(stat.p95, 2) },
    { label: 'Std Dev', value: formatFloat(stat.standard_deviation, 2) },
    { label: 'Min', value: formatFloat(stat.minimum, 2) },
    { label: 'Max', value: formatFloat(stat.maximum, 2) },
  ];

  return (
    <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 3 }}>
      {items.map((it) => (
        <Box key={it.label} sx={{ minWidth: 80 }}>
          <StatLabel label={it.label} />
          <Typography variant="body2" fontWeight={500}>
            {it.value}
          </Typography>
        </Box>
      ))}
    </Box>
  );
}

export default function BaselineCard({ baseline }: Props) {
  if (baseline.baseline_status === 'INSUFFICIENT_DATA') {
    return (
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" fontWeight={600} gutterBottom>
          Baseline — {baseline.window} window
        </Typography>
        <Alert severity="info" variant="outlined">
          SQLPulse does not yet have enough historical samples to establish a reliable baseline.
        </Alert>
      </Paper>
    );
  }

  const metricEntries = Object.entries(baseline.metrics);

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Typography variant="subtitle1" fontWeight={600} gutterBottom>
        Baseline — {baseline.window} window
        <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 1 }}>
          ({baseline.sample_count} total samples)
        </Typography>
      </Typography>

      {metricEntries.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          No baseline metrics available.
        </Typography>
      )}

      {metricEntries.map(([key, stat]) => (
        <Box key={key} sx={{ mb: 2 }}>
          <Typography variant="body2" fontWeight={600} sx={{ mb: 0.5 }}>
            {METRIC_LABELS[key] ?? key}
          </Typography>
          <StatRow stat={stat} />
        </Box>
      ))}
    </Paper>
  );
}
