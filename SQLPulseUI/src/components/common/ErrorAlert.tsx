import { Alert, AlertTitle, Button } from '@mui/material';
import RefreshIcon from '@mui/icons-material/Refresh';

interface ErrorAlertProps {
  message: string;
  title?: string;
  severity?: 'error' | 'warning';
  /** When provided, renders a Retry action. */
  onRetry?: () => void;
}

export default function ErrorAlert({
  message,
  title = 'Error',
  severity = 'error',
  onRetry,
}: ErrorAlertProps) {
  return (
    <Alert
      severity={severity}
      sx={{ mb: 2 }}
      action={
        onRetry && (
          <Button color="inherit" size="small" startIcon={<RefreshIcon />} onClick={onRetry}>
            Retry
          </Button>
        )
      }
    >
      <AlertTitle>{title}</AlertTitle>
      {message}
    </Alert>
  );
}
