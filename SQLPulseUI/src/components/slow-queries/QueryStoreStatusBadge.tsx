import { Chip, Tooltip } from '@mui/material';
import type { QueryStoreStatus } from '../../types/history';

const statusConfig: Record<QueryStoreStatus, { label: string; color: 'success' | 'warning' | 'error' | 'default'; tip: string }> = {
  AVAILABLE: {
    label: 'Query Store Available',
    color: 'success',
    tip: 'Query Store is enabled and collecting data.',
  },
  AVAILABLE_READ_ONLY: {
    label: 'Query Store (Read Only)',
    color: 'warning',
    tip: 'Query Store is available in read-only mode. New data is not being collected.',
  },
  DISABLED: {
    label: 'Query Store Disabled',
    color: 'error',
    tip: 'Query Store is disabled on this database. Enable it to use slow-query analysis.',
  },
  PERMISSION_DENIED: {
    label: 'Permission Denied',
    color: 'error',
    tip: 'Insufficient permissions to access Query Store. Contact your DBA.',
  },
  QUERY_FAILED: {
    label: 'Query Failed',
    color: 'error',
    tip: 'The Query Store query failed. Check database status and permissions.',
  },
};

interface Props {
  status: QueryStoreStatus;
}

export default function QueryStoreStatusBadge({ status }: Props) {
  const cfg = statusConfig[status] ?? { label: status, color: 'default' as const, tip: '' };
  return (
    <Tooltip title={cfg.tip} arrow>
      <Chip label={cfg.label} size="small" color={cfg.color} variant="outlined" />
    </Tooltip>
  );
}
