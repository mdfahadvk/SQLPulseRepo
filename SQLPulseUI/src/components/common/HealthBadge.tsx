import { Chip } from '@mui/material';
import type { OverallStatus } from '../../types/performance';

const healthColors: Record<string, 'success' | 'warning' | 'error' | 'default'> = {
  HEALTHY: 'success',
  WARNING: 'warning',
  DEGRADED: 'error',
  CRITICAL: 'error',
  UNKNOWN: 'default',
};

interface HealthBadgeProps {
  status: OverallStatus | string;
}

export default function HealthBadge({ status }: HealthBadgeProps) {
  return (
    <Chip
      label={status}
      size="small"
      color={healthColors[status] ?? 'default'}
      variant="filled"
    />
  );
}
