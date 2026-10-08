import { Tooltip } from '@mui/material';
import { CollectorStatus, EmptyState, DataTable, SqlText } from '../../components/common';
import type { DataTableColumn } from '../../components/common';
import { formatDuration, formatNumber, formatInteger, orDash } from '../../utils/formatters';
import type { LongRunningQueriesResult, LongRunningQuery } from '../../types/performance';

interface Props {
  data: LongRunningQueriesResult | null;
}

const columns: DataTableColumn<LongRunningQuery>[] = [
  {
    id: 'database_name',
    label: 'Database',
    sortValue: (q) => q.database_name,
    render: (q) => orDash(q.database_name),
  },
  {
    id: 'session_id',
    label: 'Session',
    align: 'right',
    noWrap: true,
    sortValue: (q) => q.session_id,
    render: (q) => q.session_id,
  },
  {
    id: 'elapsed_time_ms',
    label: 'Elapsed Time',
    align: 'right',
    noWrap: true,
    headerTooltip: 'How long the query has been running',
    sortValue: (q) => q.elapsed_time_ms,
    render: (q) => (
      <Tooltip title={`${formatInteger(q.elapsed_time_ms)} ms`}>
        <span>{formatDuration(q.elapsed_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'cpu_time_ms',
    label: 'CPU',
    align: 'right',
    noWrap: true,
    sortValue: (q) => q.cpu_time_ms,
    render: (q) => (
      <Tooltip title={`${formatInteger(q.cpu_time_ms)} ms`}>
        <span>{formatDuration(q.cpu_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'logical_reads',
    label: 'Reads',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Pages read from the buffer cache',
    sortValue: (q) => q.logical_reads,
    render: (q) => (
      <Tooltip title={formatInteger(q.logical_reads)}>
        <span>{formatNumber(q.logical_reads)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'wait_type',
    label: 'Wait Type',
    sortValue: (q) => q.wait_type,
    render: (q) => orDash(q.wait_type),
  },
  {
    id: 'sql_text',
    label: 'SQL Preview',
    minWidth: 240,
    render: (q) => <SqlText sql={q.sql_text} maxWidth={280} />,
  },
];

export default function LongRunningTab({ data }: Props) {
  if (!data) return <EmptyState message="Long-running query data not available." dense />;
  if (!data.metadata.success) {
    return <CollectorStatus metadata={data.metadata} label="Long Running" />;
  }

  return (
    <DataTable
      ariaLabel="Long running queries"
      columns={columns}
      rows={data.queries}
      getRowKey={(q) => `${q.session_id}-${q.request_id}`}
      initialSortBy="elapsed_time_ms"
      emptyMessage="No long-running queries detected."
      maxHeight={560}
    />
  );
}
