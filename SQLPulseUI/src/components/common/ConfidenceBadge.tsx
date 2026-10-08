import { Chip, Tooltip } from '@mui/material';
import type { Confidence } from '../../types/performance';

const confidenceColors: Record<string, 'success' | 'warning' | 'default'> = {
  high: 'success',
  medium: 'warning',
  low: 'default',
};

const confidenceTooltips: Record<string, string> = {
  high: 'High confidence — strong supporting evidence was collected',
  medium: 'Medium confidence — supporting evidence is partial',
  low: 'Low confidence — limited evidence, treat as a starting point',
};

interface ConfidenceBadgeProps {
  confidence: Confidence | string;
}

export default function ConfidenceBadge({ confidence }: ConfidenceBadgeProps) {
  const key = String(confidence ?? '').toLowerCase();
  const tooltip = confidenceTooltips[key] ?? '';

  const chip = (
    <Chip
      label={`${key || 'unknown'} confidence`}
      size="small"
      color={confidenceColors[key] ?? 'default'}
      variant="outlined"
      sx={{ textTransform: 'capitalize' }}
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
