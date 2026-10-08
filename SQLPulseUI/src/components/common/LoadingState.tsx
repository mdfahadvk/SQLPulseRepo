import { Box, CircularProgress, Typography } from '@mui/material';

interface LoadingStateProps {
  message?: string;
  /** Reduce vertical padding when nested inside a card or tab. */
  dense?: boolean;
}

export default function LoadingState({ message = 'Loading…', dense = false }: LoadingStateProps) {
  return (
    <Box
      role="status"
      aria-live="polite"
      aria-busy="true"
      sx={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        py: dense ? 4 : 8,
        gap: 2,
      }}
    >
      <CircularProgress aria-hidden />
      <Typography color="text.secondary" variant="body2">
        {message}
      </Typography>
    </Box>
  );
}
