import { useState } from 'react';
import { Box, Tabs, Tab, Paper, Typography } from '@mui/material';
import {
  ResponsiveContainer,
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
} from 'recharts';
import { formatTimestamp, formatTimestampShort } from '../../utils/formatters';
import type { QueryMetricRecord } from '../../types/history';

interface Props {
  samples: QueryMetricRecord[];
}

interface ChartDef {
  label: string;
  dataKey: keyof QueryMetricRecord;
  color: string;
  unit: string;
}

const CHARTS: ChartDef[] = [
  { label: 'Duration', dataKey: 'avg_elapsed_time_ms', color: '#1976d2', unit: 'ms' },
  { label: 'CPU', dataKey: 'avg_cpu_time_ms', color: '#ed6c02', unit: 'ms' },
  { label: 'Logical Reads', dataKey: 'avg_logical_reads', color: '#2e7d32', unit: '' },
];

export default function QueryHistoryCharts({ samples }: Props) {
  const [tab, setTab] = useState(0);

  if (samples.length === 0) {
    return (
      <Paper variant="outlined" sx={{ p: 3, textAlign: 'center' }}>
        <Typography color="text.secondary">No historical samples to chart.</Typography>
      </Paper>
    );
  }

  const sorted = [...samples].sort(
    (a, b) => new Date(a.collected_at).getTime() - new Date(b.collected_at).getTime(),
  );

  const chart = CHARTS[tab]!;

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ mb: 2 }}>
        {CHARTS.map((c) => (
          <Tab key={c.label} label={c.label} />
        ))}
      </Tabs>

      <Box sx={{ width: '100%', height: 320 }}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={sorted} margin={{ top: 8, right: 24, left: 8, bottom: 8 }}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis
              dataKey="collected_at"
              tickFormatter={(v: string) => formatTimestampShort(v)}
              tick={{ fontSize: 11 }}
              minTickGap={40}
            />
            <YAxis
              tick={{ fontSize: 11 }}
              label={
                chart.unit
                  ? { value: chart.unit, angle: -90, position: 'insideLeft', fontSize: 12 }
                  : undefined
              }
            />
            <Tooltip
              labelFormatter={(v: string) => formatTimestamp(v)}
              formatter={(value: number) =>
                chart.unit ? [`${value.toLocaleString()} ${chart.unit}`, chart.label] : [value.toLocaleString(), chart.label]
              }
            />
            <Line
              type="monotone"
              dataKey={chart.dataKey}
              stroke={chart.color}
              strokeWidth={2}
              dot={{ r: 2 }}
              activeDot={{ r: 5 }}
            />
          </LineChart>
        </ResponsiveContainer>
      </Box>
    </Paper>
  );
}
