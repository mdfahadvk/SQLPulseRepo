import { Chip, Tooltip } from '@mui/material';
import type { TimeoutClassification } from '../../types/sqlErrors';

/**
 * Renders the three-way timeout distinction.
 *
 * These are deliberately given different colours and different words. A screen
 * that showed an inferred candidate the same way as an observed timeout would
 * report incidents nobody experienced, so the badge always says which one it is.
 */
const presentation: Record<
  TimeoutClassification,
  { label: string; color: 'error' | 'warning' | 'info' | 'default'; tooltip: string }
> = {
  CONFIRMED_APPLICATION_TIMEOUT: {
    label: 'Confirmed Timeout',
    color: 'error',
    tooltip:
      'An application or telemetry source recorded that a client gave up waiting for this command.',
  },
  TIMEOUT_CANDIDATE: {
    label: 'Timeout Candidate',
    color: 'warning',
    tooltip:
      'The statement ran long enough to reach the configured application timeout, but no '
      + 'application timeout was actually observed. Unconfirmed.',
  },
  SLOW_QUERY: {
    label: 'Slow Query',
    color: 'info',
    tooltip:
      'The statement exceeded the monitoring threshold but not the configured application '
      + 'timeout. Slow is not a timeout.',
  },
  NOT_APPLICABLE: {
    label: 'Not A Timeout',
    color: 'default',
    tooltip: 'This error is not a timeout question, or carries no duration evidence.',
  },
};

interface Props {
  classification: TimeoutClassification;
  /** Hide the badge entirely when the error has nothing to say about timeouts. */
  hideWhenNotApplicable?: boolean;
}

export default function TimeoutClassificationBadge({
  classification,
  hideWhenNotApplicable,
}: Props) {
  if (hideWhenNotApplicable && classification === 'NOT_APPLICABLE') return null;

  const { label, color, tooltip } = presentation[classification] ?? presentation.NOT_APPLICABLE;

  return (
    <Tooltip title={tooltip} arrow>
      <Chip label={label} size="small" color={color} variant="outlined" />
    </Tooltip>
  );
}
