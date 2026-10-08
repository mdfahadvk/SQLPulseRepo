import { Chip, Tooltip } from '@mui/material';
import type { Severity } from '../../types/performance';

const severityColors: Record<string, 'error' | 'warning' | 'info' | 'success' | 'default'> = {
  critical: 'error',
  high: 'error',
  medium: 'warning',
  low: 'info',
  info: 'info',
  unknown: 'default',
};

const severityTooltips: Record<string, string> = {
  critical: 'Critical — requires immediate attention',
  high: 'High — significant impact on performance',
  medium: 'Medium — worth investigating',
  low: 'Low — minor or informational',
  unknown: 'Severity could not be determined',
};

interface SeverityBadgeProps {
  severity: Severity | string;
}

export default function SeverityBadge({ severity }: SeverityBadgeProps) {
  const key = String(severity ?? '').toLowerCase();
  const tooltip = severityTooltips[key] ?? '';

  const chip = (
    <Chip
      label={String(severity ?? 'unknown').toUpperCase()}
      size="small"
      color={severityColors[key] ?? 'default'}
      variant="outlined"
    />
  );

  return tooltip ? (
    <Tooltip title={tooltip} arrow>
      <span>{chip}</span>
    </Tooltip>
  ) : (
    chip
  );
}
