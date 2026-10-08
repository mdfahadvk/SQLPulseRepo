import { Tooltip } from '@mui/material';
import { CollectorStatus, EmptyState, DataTable, SqlText } from '../../components/common';
import type { DataTableColumn } from '../../components/common';
import { formatDuration, formatNumber, formatInteger, orDash } from '../../utils/formatters';
import type { ActiveRequestsResult, ActiveRequest } from '../../types/performance';

interface Props {
  data: ActiveRequestsResult | null;
}

const columns: DataTableColumn<ActiveRequest>[] = [
  {
    id: 'session_id',
    label: 'Session ID',
    align: 'right',
    noWrap: true,
    sortValue: (r) => r.session_id,
    render: (r) => r.session_id,
  },
  {
    id: 'database_name',
    label: 'Database',
    sortValue: (r) => r.database_name,
    render: (r) => orDash(r.database_name),
  },
  {
    id: 'status',
    label: 'Status',
    sortValue: (r) => r.status,
    render: (r) => r.status,
  },
  {
    id: 'command',
    label: 'Command',
    sortValue: (r) => r.command,
    render: (r) => r.command,
  },
  {
    id: 'total_elapsed_time_ms',
    label: 'Elapsed Time',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Total time the request has been running',
    sortValue: (r) => r.total_elapsed_time_ms,
    render: (r) => (
      <Tooltip title={`${formatInteger(r.total_elapsed_time_ms)} ms`}>
        <span>{formatDuration(r.total_elapsed_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'cpu_time_ms',
    label: 'CPU Time',
    align: 'right',
    noWrap: true,
    headerTooltip: 'CPU time consumed by this request',
    sortValue: (r) => r.cpu_time_ms,
    render: (r) => (
      <Tooltip title={`${formatInteger(r.cpu_time_ms)} ms`}>
        <span>{formatDuration(r.cpu_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'logical_reads',
    label: 'Logical Reads',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Pages read from the buffer cache',
    sortValue: (r) => r.logical_reads,
    render: (r) => (
      <Tooltip title={formatInteger(r.logical_reads)}>
        <span>{formatNumber(r.logical_reads)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'wait_type',
    label: 'Wait Type',
    sortValue: (r) => r.wait_type,
    render: (r) => orDash(r.wait_type),
  },
  {
    id: 'blocking_session_id',
    label: 'Blocking',
    align: 'right',
    headerTooltip: 'Session ID currently blocking this request',
    sortValue: (r) => r.blocking_session_id,
    render: (r) => orDash(r.blocking_session_id),
  },
  {
    id: 'sql_text',
    label: 'SQL Preview',
    minWidth: 240,
    render: (r) => <SqlText sql={r.sql_text} maxWidth={280} />,
  },
];

export default function ActiveRequestsTab({ data }: Props) {
  if (!data) return <EmptyState message="Active requests data not available." dense />;
  if (!data.metadata.success) {
    return <CollectorStatus metadata={data.metadata} label="Active Requests" />;
  }

  return (
    <DataTable
      ariaLabel="Active requests"
      columns={columns}
      rows={data.requests}
      getRowKey={(r) => `${r.session_id}-${r.request_id}`}
      initialSortBy="total_elapsed_time_ms"
      emptyMessage="No active requests at this time."
      maxHeight={560}
    />
  );
}
