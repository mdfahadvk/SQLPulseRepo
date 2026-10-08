import { Alert, AlertTitle, Box, Chip, Typography } from '@mui/material';
import type { ApplicationTelemetryStatus, ApplicationTelemetrySummary, SqlErrorSourceState, SqlErrorQueryStatus } from '../../types/sqlErrors';

interface Props {
  status: SqlErrorQueryStatus;
  message: string;
  applicationTelemetryAvailable: boolean;
  applicationTelemetryStatus: ApplicationTelemetryStatus | null;
  applicationTelemetry: ApplicationTelemetrySummary | null;
  sources: SqlErrorSourceState[];
}

const severityFor: Record<SqlErrorQueryStatus, 'success' | 'info' | 'warning' | 'error'> = {
  OK: 'info',
  NO_ERRORS_FOUND: 'success',
  PARTIAL_ERROR_DATA: 'warning',
  ERROR_SOURCE_UNAVAILABLE: 'error',
};

const titleFor: Record<SqlErrorQueryStatus, string> = {
  OK: 'Errors found',
  NO_ERRORS_FOUND: 'No errors in this period',
  PARTIAL_ERROR_DATA: 'Partial error data',
  ERROR_SOURCE_UNAVAILABLE: 'No error source could be consulted',
};

const sourceStatusColor: Record<string, 'success' | 'warning' | 'error' | 'default'> = {
  AVAILABLE: 'success',
  NOT_CONFIGURED: 'default',
  NOT_APPLICABLE: 'default',
  PERMISSION_DENIED: 'error',
  FAILED: 'error',
};

/**
 * States how much SQLPulse could actually see.
 *
 * This is the most important thing on the page. "No errors found" and "no error
 * source could be consulted" produce the same empty table, and reading the
 * second as the first is a false all-clear during an incident — so they are
 * never rendered with the same colour, title or wording.
 */
export default function SqlErrorStatusAlert({
  status,
  message,
  applicationTelemetryAvailable,
  applicationTelemetryStatus,
  applicationTelemetry,
  sources,
}: Props) {
  // A period with errors in it needs no banner; the table speaks for itself.
  const showBanner = status !== 'OK';
  const signozStatus = sources.find((source) => source.name === 'signoz')?.status;
  const telemetryStatus = applicationTelemetryStatus ?? (signozStatus === 'NOT_CONFIGURED'
    ? 'NOT_CONFIGURED'
    : signozStatus === 'PERMISSION_DENIED'
      ? 'PERMISSION_DENIED'
      : signozStatus === 'FAILED'
        ? 'UNAVAILABLE'
        : null);

  return (
    <Box sx={{ mb: 2 }}>
      {showBanner && (
        <Alert severity={severityFor[status]} sx={{ mb: 1 }}>
          <AlertTitle>{titleFor[status]}</AlertTitle>
          {message}
        </Alert>
      )}

      {applicationTelemetryStatus === 'NO_DATA' && (
        <Alert severity="info" sx={{ mb: 1 }}>
          <AlertTitle>Application telemetry available, no SQL errors found</AlertTitle>
          {applicationTelemetry?.applications.map((app) => app.name).filter(Boolean).join(', ') || 'The mapped application'}
          {' returned no matching SQL exception telemetry for this period.'}
        </Alert>
      )}

      {!applicationTelemetryAvailable && telemetryStatus && telemetryStatus !== 'NO_DATA' && (
        <Alert severity="warning" sx={{ mb: 1 }}>
          <AlertTitle>{telemetryStatus === 'PERMISSION_DENIED'
            ? 'SigNoz authentication failed'
            : telemetryStatus === 'UNAVAILABLE' || telemetryStatus === 'PARTIAL'
              ? 'SigNoz telemetry unavailable'
              : 'Application telemetry not configured'}</AlertTitle>
          {telemetryStatus === 'PERMISSION_DENIED'
            ? 'SigNoz rejected the configured access token. Check the local configuration or SIGNOZ_ACCESS_TOKEN environment variable.'
            : telemetryStatus === 'UNAVAILABLE' || telemetryStatus === 'PARTIAL'
              ? 'SQLPulse could not read telemetry from SigNoz. Check the service endpoint and network connection.'
              : 'No SigNoz application mapping or access token is configured for the selected database.'}
        </Alert>
      )}

      {sources.length > 0 && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
          <Typography variant="caption" color="text.secondary">
            Sources consulted:
          </Typography>
          {sources.map((source) => (
            <Chip
              key={source.name}
              size="small"
              variant="outlined"
              color={sourceStatusColor[source.status] ?? 'default'}
              label={`${source.name} — ${source.status}${
                source.status === 'AVAILABLE' ? ` (${source.event_count})` : ''
              }`}
              title={source.message ?? undefined}
            />
          ))}
        </Box>
      )}
    </Box>
  );
}
