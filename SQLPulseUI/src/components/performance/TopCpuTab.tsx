import { Tooltip, Typography } from '@mui/material';
import { CollectorStatus, EmptyState, DataTable, SqlText } from '../../components/common';
import type { DataTableColumn } from '../../components/common';
import {
  formatDuration,
  formatNumber,
  formatInteger,
  orDash,
  shortenHash,
} from '../../utils/formatters';
import type { TopCpuQueriesResult, TopCpuQuery } from '../../types/performance';

interface Props {
  data: TopCpuQueriesResult | null;
}

const columns: DataTableColumn<TopCpuQuery>[] = [
  {
    id: 'database_name',
    label: 'Database',
    sortValue: (q) => q.database_name,
    render: (q) => orDash(q.database_name),
  },
  {
    id: 'query_hash',
    label: 'Query Hash',
    noWrap: true,
    sortValue: (q) => q.query_hash,
    render: (q) => (
      <Tooltip title={q.query_hash ?? ''} disableHoverListener={!q.query_hash}>
        <Typography variant="body2" component="span" sx={{ fontFamily: 'monospace', fontSize: 12 }}>
          {shortenHash(q.query_hash)}
        </Typography>
      </Tooltip>
    ),
  },
  {
    id: 'execution_count',
    label: 'Exec Count',
    align: 'right',
    noWrap: true,
    sortValue: (q) => q.execution_count,
    render: (q) => (
      <Tooltip title={formatInteger(q.execution_count)}>
        <span>{formatNumber(q.execution_count)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'avg_cpu_time_ms',
    label: 'Avg CPU',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Average CPU time per execution',
    sortValue: (q) => q.avg_cpu_time_ms,
    render: (q) => (
      <Tooltip title={`${formatInteger(q.avg_cpu_time_ms)} ms`}>
        <span>{formatDuration(q.avg_cpu_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'total_cpu_time_ms',
    label: 'Total CPU',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Cumulative CPU time across all executions',
    sortValue: (q) => q.total_cpu_time_ms,
    render: (q) => (
      <Tooltip title={`${formatInteger(q.total_cpu_time_ms)} ms`}>
        <span>{formatDuration(q.total_cpu_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'avg_elapsed_time_ms',
    label: 'Avg Duration',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Average wall-clock duration per execution',
    sortValue: (q) => q.avg_elapsed_time_ms,
    render: (q) => (
      <Tooltip title={`${formatInteger(q.avg_elapsed_time_ms)} ms`}>
        <span>{formatDuration(q.avg_elapsed_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'total_logical_reads',
    label: 'Logical Reads',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Pages read from the buffer cache',
    sortValue: (q) => q.total_logical_reads,
    render: (q) => (
      <Tooltip title={formatInteger(q.total_logical_reads)}>
        <span>{formatNumber(q.total_logical_reads)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'sql_text',
    label: 'SQL Preview',
    minWidth: 240,
    render: (q) => <SqlText sql={q.sql_text} maxWidth={280} />,
  },
];

export default function TopCpuTab({ data }: Props) {
  if (!data) return <EmptyState message="Top CPU query data not available." dense />;
  if (!data.metadata.success) return <CollectorStatus metadata={data.metadata} label="Top CPU" />;

  return (
    <DataTable
      ariaLabel="Top queries by CPU consumption"
      columns={columns}
      rows={data.queries}
      getRowKey={(q, i) => q.query_hash ?? i}
      initialSortBy="total_cpu_time_ms"
      emptyMessage="No top CPU queries found."
      maxHeight={560}
    />
  );
}
