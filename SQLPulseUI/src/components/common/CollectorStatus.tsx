import { Chip, Tooltip } from '@mui/material';
import type { CollectorMetadata } from '../../types/performance';

type CollectorState = 'success' | 'permission_denied' | 'not_applicable' | 'failed';

function resolveState(metadata: CollectorMetadata | null | undefined): CollectorState {
  if (!metadata) return 'not_applicable';
  if (metadata.success) return 'success';
  if (metadata.permission_issue) return 'permission_denied';
  return 'failed';
}

const stateConfig: Record<CollectorState, { label: string; color: 'success' | 'warning' | 'default' | 'error' }> = {
  success: { label: 'Available', color: 'success' },
  permission_denied: { label: 'Permission Denied', color: 'warning' },
  not_applicable: { label: 'Not Applicable', color: 'default' },
  failed: { label: 'Failed', color: 'error' },
};

interface CollectorStatusProps {
  metadata: CollectorMetadata | null | undefined;
  label?: string;
}

export default function CollectorStatus({ metadata, label }: CollectorStatusProps) {
  const state = resolveState(metadata);
  const cfg = stateConfig[state];
  const tooltip = metadata?.error_message ?? '';

  return (
    <Tooltip title={tooltip} arrow disableHoverListener={!tooltip}>
      <Chip
        label={label ? `${label}: ${cfg.label}` : cfg.label}
        size="small"
        color={cfg.color}
        variant="outlined"
      />
    </Tooltip>
  );
}
