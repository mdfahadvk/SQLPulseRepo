import { Chip } from '@mui/material';
import type { WorkloadType } from '../../types/history';

const workloadConfig: Record<WorkloadType, { label: string; color: 'primary' | 'secondary' | 'default' }> = {
  APPLICATION_OR_INTERACTIVE: { label: 'Application / Interactive', color: 'primary' },
  BACKGROUND_OR_SCHEDULED: { label: 'Background / Scheduled', color: 'secondary' },
  UNKNOWN: { label: 'Unknown', color: 'default' },
};

interface Props {
  type: WorkloadType;
}

export default function WorkloadBadge({ type }: Props) {
  const cfg = workloadConfig[type] ?? { label: type, color: 'default' as const };
  return <Chip label={cfg.label} size="small" color={cfg.color} variant="outlined" />;
}
