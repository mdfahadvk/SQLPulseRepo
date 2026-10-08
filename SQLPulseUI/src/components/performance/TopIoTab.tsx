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
import type { TopIoQueriesResult, TopIoQuery } from '../../types/performance';

interface Props {
  data: TopIoQueriesResult | null;
}

const columns: DataTableColumn<TopIoQuery>[] = [
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
    id: 'avg_logical_reads',
    label: 'Avg Logical Reads',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Average pages read from the buffer cache per execution',
    sortValue: (q) => q.avg_logical_reads,
    render: (q) => (
      <Tooltip title={formatInteger(q.avg_logical_reads)}>
        <span>{formatNumber(Math.round(q.avg_logical_reads))}</span>
      </Tooltip>
    ),
  },
  {
    id: 'total_physical_reads',
    label: 'Physical Reads',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Pages read from disk — high values indicate cache pressure',
    sortValue: (q) => q.total_physical_reads,
    render: (q) => (
      <Tooltip title={formatInteger(q.total_physical_reads)}>
        <span>{formatNumber(q.total_physical_reads)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'total_logical_writes',
    label: 'Writes',
    align: 'right',
    noWrap: true,
    sortValue: (q) => q.total_logical_writes,
    render: (q) => (
      <Tooltip title={formatInteger(q.total_logical_writes)}>
        <span>{formatNumber(q.total_logical_writes)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'total_elapsed_time_ms',
    label: 'Duration',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Cumulative elapsed time across all executions',
    sortValue: (q) => q.total_elapsed_time_ms,
    render: (q) => (
      <Tooltip title={`${formatInteger(q.total_elapsed_time_ms)} ms`}>
        <span>{formatDuration(q.total_elapsed_time_ms)}</span>
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

export default function TopIoTab({ data }: Props) {
  if (!data) return <EmptyState message="Top I/O query data not available." dense />;
  if (!data.metadata.success) return <CollectorStatus metadata={data.metadata} label="Top I/O" />;

  return (
    <DataTable
      ariaLabel="Top queries by I/O"
      columns={columns}
      rows={data.queries}
      getRowKey={(q, i) => q.query_hash ?? i}
      initialSortBy="avg_logical_reads"
      emptyMessage="No top I/O queries found."
      maxHeight={560}
    />
  );
}
