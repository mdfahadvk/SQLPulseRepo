import {
  Paper,
  Typography,
  Box,
  Chip,
} from '@mui/material';
import { formatFloat } from '../../utils/formatters';
import type { QueryMetricRecord, QueryBaselineResponse, BaselineStatistic } from '../../types/history';

interface Props {
  baseline: QueryBaselineResponse;
  latestSample: QueryMetricRecord | null;
}

interface RegressionRow {
  label: string;
  currentValue: number;
  stat: BaselineStatistic;
}

const METRIC_MAP: { metricKey: string; sampleKey: keyof QueryMetricRecord; label: string }[] = [
  { metricKey: 'avg_elapsed_ms', sampleKey: 'avg_elapsed_time_ms', label: 'Avg Duration (ms)' },
  { metricKey: 'avg_cpu_ms', sampleKey: 'avg_cpu_time_ms', label: 'Avg CPU (ms)' },
  { metricKey: 'avg_logical_reads', sampleKey: 'avg_logical_reads', label: 'Avg Logical Reads' },
];

function MultiplierChip({ value }: { value: number }) {
  const color = value >= 3 ? 'error' : value >= 1.5 ? 'warning' : 'success';
  return (
    <Chip
      label={`${formatFloat(value, 2)}×`}
      color={color}
      size="small"
      variant="outlined"
    />
  );
}

export default function RegressionCard({ baseline, latestSample }: Props) {
  if (baseline.baseline_status === 'INSUFFICIENT_DATA' || !latestSample) {
    return null;
  }

  const rows: RegressionRow[] = [];
  for (const m of METRIC_MAP) {
    const stat = baseline.metrics[m.metricKey];
    if (!stat || stat.sample_count < 2) continue;
    const current = latestSample[m.sampleKey] as number;
    if (typeof current !== 'number') continue;
    rows.push({ label: m.label, currentValue: current, stat });
  }

  if (rows.length === 0) return null;

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Typography variant="subtitle1" fontWeight={600} gutterBottom>
        Regression Comparison
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Latest sample vs {baseline.window} baseline
      </Typography>

      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        {rows.map((row) => {
          const multiplierMean = row.stat.mean > 0 ? row.currentValue / row.stat.mean : 0;
          const multiplierP95 = row.stat.p95 > 0 ? row.currentValue / row.stat.p95 : 0;
          return (
            <Box
              key={row.label}
              sx={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 3,
                alignItems: 'center',
                p: 1.5,
                borderRadius: 1,
                bgcolor: 'action.hover',
              }}
            >
              <Box sx={{ minWidth: 160 }}>
                <Typography variant="caption" color="text.secondary">
                  Metric
                </Typography>
                <Typography variant="body2" fontWeight={600}>
                  {row.label}
                </Typography>
              </Box>
              <Box sx={{ minWidth: 90 }}>
                <Typography variant="caption" color="text.secondary">
                  Current
                </Typography>
                <Typography variant="body2" fontWeight={600}>
                  {formatFloat(row.currentValue, 2)}
                </Typography>
              </Box>
              <Box sx={{ minWidth: 90 }}>
                <Typography variant="caption" color="text.secondary">
                  Historical Mean
                </Typography>
                <Typography variant="body2">
                  {formatFloat(row.stat.mean, 2)}
                </Typography>
              </Box>
              <Box sx={{ minWidth: 90 }}>
                <Typography variant="caption" color="text.secondary">
                  Historical P95
                </Typography>
                <Typography variant="body2">
                  {formatFloat(row.stat.p95, 2)}
                </Typography>
              </Box>
              <Box sx={{ minWidth: 80 }}>
                <Typography variant="caption" color="text.secondary">
                  vs Mean
                </Typography>
                <Box>{multiplierMean > 0 && <MultiplierChip value={multiplierMean} />}</Box>
              </Box>
              <Box sx={{ minWidth: 80 }}>
                <Typography variant="caption" color="text.secondary">
                  vs P95
                </Typography>
                <Box>{multiplierP95 > 0 && <MultiplierChip value={multiplierP95} />}</Box>
              </Box>
            </Box>
          );
        })}
      </Box>
    </Paper>
  );
}
