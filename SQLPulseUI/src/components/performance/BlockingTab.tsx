import { Tooltip, Chip, Box } from '@mui/material';
import { CollectorStatus, EmptyState, DataTable, SqlText } from '../../components/common';
import type { DataTableColumn } from '../../components/common';
import { formatDuration, formatInteger, orDash } from '../../utils/formatters';
import type { BlockingSessionsResult, BlockingSession } from '../../types/performance';

interface Props {
  data: BlockingSessionsResult | null;
}

const columns: DataTableColumn<BlockingSession>[] = [
  {
    id: 'session_id',
    label: 'Session',
    align: 'right',
    noWrap: true,
    sortValue: (s) => s.session_id,
    render: (s) => s.session_id,
  },
  {
    id: 'blocking_session_id',
    label: 'Blocking Session',
    align: 'right',
    noWrap: true,
    headerTooltip: 'The session holding the lock this one is waiting for',
    sortValue: (s) => s.blocking_session_id,
    render: (s) => orDash(s.blocking_session_id),
  },
  {
    id: 'is_head_blocker',
    label: 'Head Blocker',
    headerTooltip: 'The root of the blocking chain — resolve this session first',
    sortValue: (s) => (s.is_head_blocker ? 1 : 0),
    render: (s) =>
      s.is_head_blocker ? <Chip label="HEAD" size="small" color="error" /> : null,
  },
  {
    id: 'database_name',
    label: 'Database',
    sortValue: (s) => s.database_name,
    render: (s) => orDash(s.database_name),
  },
  {
    id: 'wait_type',
    label: 'Wait Type',
    sortValue: (s) => s.wait_type,
    render: (s) => orDash(s.wait_type),
  },
  {
    id: 'wait_time_ms',
    label: 'Wait Duration',
    align: 'right',
    noWrap: true,
    headerTooltip: 'How long this session has been blocked',
    sortValue: (s) => s.wait_time_ms,
    render: (s) => (
      <Tooltip title={`${formatInteger(s.wait_time_ms)} ms`}>
        <span>{formatDuration(s.wait_time_ms)}</span>
      </Tooltip>
    ),
  },
  {
    id: 'sql_text',
    label: 'SQL Preview',
    minWidth: 240,
    render: (s) => <SqlText sql={s.sql_text} maxWidth={280} />,
  },
];

export default function BlockingTab({ data }: Props) {
  if (!data) return <EmptyState message="Blocking data not available." dense />;
  if (!data.metadata.success) return <CollectorStatus metadata={data.metadata} label="Blocking" />;

  // No blocking is a healthy outcome, not an absence of data.
  if (data.sessions.length === 0) {
    return (
      <EmptyState
        variant="positive"
        title="No blocking detected"
        message="Sessions are running freely with no lock contention."
        dense
      />
    );
  }

  return (
    <Box>
      {data.blocking_chain_count > 0 && (
        <Chip
          label={`${data.blocking_chain_count} blocking chain${data.blocking_chain_count > 1 ? 's' : ''} detected`}
          color="error"
          size="small"
          variant="outlined"
          sx={{ mb: 2 }}
        />
      )}
      <DataTable
        ariaLabel="Blocking sessions"
        columns={columns}
        rows={data.sessions}
        getRowKey={(s) => `${s.session_id}-${s.blocking_session_id}`}
        initialSortBy="wait_time_ms"
        emptyMessage="No blocking detected."
        maxHeight={560}
      />
    </Box>
  );
}
