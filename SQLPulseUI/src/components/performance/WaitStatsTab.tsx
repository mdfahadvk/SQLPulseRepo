import { Tooltip, Alert, Typography } from '@mui/material';
import { CollectorStatus, EmptyState, DataTable } from '../../components/common';
import type { DataTableColumn } from '../../components/common';
import { formatDuration, formatNumber, formatInteger } from '../../utils/formatters';
import type { WaitStatisticsResult, WaitStatistic } from '../../types/performance';

interface Props {
  data: WaitStatisticsResult | null;
  isDatabaseScope: boolean;
}

const columns: DataTableColumn<WaitStatistic>[] = [
  {
    id: 'wait_type',
    label: 'Wait Type',
    sortValue: (s) => s.wait_type,
    render: (s) => (
      <Typography variant="body2" component="span" sx={{ fontFamily: 'monospace', fontSize: 12 }}>
        {s.wait_type}
      </Typography>
    ),
  },
  {
    id: 'waiting_tasks_count',
    label: 'Waiting Tasks',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Number of tasks that waited on this resource',
    sortValue: (s) => s.waiting_tasks_count,
    render: (s) => (
      <Tooltip title={formatInteger(s.waiting_tasks_count)}>
        <span>{formatNumber(s.waiting_tasks_count)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'wait_time_ms',
    label: 'Wait Time',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Total time spent waiting on this resource',
    sortValue: (s) => s.wait_time_ms,
    render: (s) => (
      <Tooltip title={`${formatInteger(s.wait_time_ms)} ms`}>
        <span>{formatDuration(s.wait_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'max_wait_time_ms',
    label: 'Max Wait',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Longest single wait recorded for this type',
    sortValue: (s) => s.max_wait_time_ms,
    render: (s) => (
      <Tooltip title={`${formatInteger(s.max_wait_time_ms)} ms`}>
        <span>{formatDuration(s.max_wait_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'signal_wait_time_ms',
    label: 'Signal Wait',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Time waiting for CPU after the resource became available',
    sortValue: (s) => s.signal_wait_time_ms,
    render: (s) => (
      <Tooltip title={`${formatInteger(s.signal_wait_time_ms)} ms`}>
        <span>{formatDuration(s.signal_wait_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'resource_wait_time_ms',
    label: 'Resource Wait',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Time waiting for the resource itself',
    sortValue: (s) => s.resource_wait_time_ms,
    render: (s) => (
      <Tooltip title={`${formatInteger(s.resource_wait_time_ms)} ms`}>
        <span>{formatDuration(s.resource_wait_time_ms)}</span>
      </Tooltip>
    ),
  },
];

export default function WaitStatsTab({ data, isDatabaseScope }: Props) {
  // Wait statistics are a server-level DMV; database scope has nothing to show.
  if (!data && isDatabaseScope) {
    return (
      <Alert severity="info">
        Wait statistics are collected at the server level and are not applicable for
        database-scoped analysis.
      </Alert>
    );
  }
  if (!data) return <EmptyState message="Wait statistics data not available." dense />;

  if (!data.metadata.success) {
    if (data.metadata.error_message?.toLowerCase().includes('not_applicable')) {
      return (
        <Alert severity="info">
          Wait statistics are not applicable for database-scoped analysis.
        </Alert>
      );
    }
    return <CollectorStatus metadata={data.metadata} label="Wait Statistics" />;
  }

  return (
    <DataTable
      ariaLabel="Wait statistics"
      columns={columns}
      rows={data.statistics}
      getRowKey={(s) => s.wait_type}
      initialSortBy="wait_time_ms"
      emptyMessage="No significant wait statistics recorded."
      maxHeight={560}
    />
  );
}
