import type { ReactNode } from 'react';
import { Box, Typography } from '@mui/material';
import InboxIcon from '@mui/icons-material/Inbox';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';

interface EmptyStateProps {
  message?: string;
  /** Optional heading shown above the message. */
  title?: string;
  /**
   * `positive` is for "nothing found and that's good news" cases such as
   * no blocking chains detected — it avoids implying something went wrong.
   */
  variant?: 'neutral' | 'positive';
  /** Optional call-to-action (e.g. a button to run analysis). */
  action?: ReactNode;
  /** Reduce vertical padding when nested inside a card or tab. */
  dense?: boolean;
}

export default function EmptyState({
  message = 'No data available.',
  title,
  variant = 'neutral',
  action,
  dense = false,
}: EmptyStateProps) {
  const positive = variant === 'positive';
  const Icon = positive ? CheckCircleOutlineIcon : InboxIcon;

  return (
    <Box
      role="status"
      sx={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        textAlign: 'center',
        py: dense ? 4 : 8,
        px: 2,
        gap: 1,
        color: 'text.secondary',
      }}
    >
      <Icon
        sx={{ fontSize: dense ? 36 : 48, color: positive ? 'success.main' : 'inherit' }}
        aria-hidden
      />
      {title && (
        <Typography variant="subtitle1" fontWeight={600} color="text.primary">
          {title}
        </Typography>
      )}
      <Typography variant="body2" sx={{ maxWidth: 460 }}>
        {message}
      </Typography>
      {action && <Box sx={{ mt: 1 }}>{action}</Box>}
    </Box>
  );
}
