import { useState } from 'react';
import {
  Alert,
  AlertTitle,
  Box,
  Chip,
  CircularProgress,
  Divider,
  Drawer,
  IconButton,
  List,
  ListItem,
  ListItemText,
  Paper,
  Typography,
} from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import { SeverityBadge } from '../common';
import SqlErrorCategoryBadge from './SqlErrorCategoryBadge';
import TimeoutClassificationBadge from './TimeoutClassificationBadge';
import { useSqlErrorDetail } from '../../hooks/useQueries';
import { formatDuration, formatTimestamp } from '../../utils/formatters';
import type {
  CorrelatedEvidenceItem,
  CorrelationConfidence,
  SqlErrorCorrelation,
  SqlErrorEvent,
} from '../../types/sqlErrors';

const EM_DASH = '—';

const confidenceColor: Record<CorrelationConfidence, 'error' | 'warning' | 'info' | 'default'> = {
  HIGH: 'error',
  MEDIUM: 'warning',
  LOW: 'info',
  NONE: 'default',
};

const evidenceKindLabels: Record<string, string> = {
  QUERY_STORE_HISTORY: 'Query Store history',
  QUERY_BASELINE: 'Query baseline',
  BLOCKING: 'Blocking',
  LONG_RUNNING_QUERY: 'Long-running queries',
};

interface Props {
  errorId: string | null;
  /** The row that was clicked, shown immediately while the detail loads. */
  fallback: SqlErrorEvent | null;
  onClose: () => void;
}

export default function SqlErrorDetailDrawer({ errorId, fallback, onClose }: Props) {
  const { data, isLoading, error } = useSqlErrorDetail(errorId);

  const errorEvent = data?.error ?? fallback;

  return (
    <Drawer
      anchor="right"
      open={errorId !== null}
      onClose={onClose}
      PaperProps={{ sx: { width: { xs: '100%', sm: 620 }, p: 3 } }}
    >
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 2 }}>
        <Typography variant="h6" fontWeight={600}>
          SQL Error Detail
        </Typography>
        <IconButton onClick={onClose} size="small" aria-label="Close">
          <CloseIcon />
        </IconButton>
      </Box>

      {error && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {error instanceof Error ? error.message : 'Failed to load the error detail.'}
        </Alert>
      )}

      {/* SQLPulse does not store error events, so an id can expire. */}
      {data && !data.found && (
        <Alert severity="info" sx={{ mb: 2 }}>
          <AlertTitle>Event no longer available</AlertTitle>
          {data.message}
        </Alert>
      )}

      {errorEvent && <ErrorFacts errorEvent={errorEvent} />}

      <Divider sx={{ my: 2 }} />

      {isLoading && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 2 }}>
          <CircularProgress size={18} />
          <Typography variant="body2" color="text.secondary">
            Correlating performance evidence…
          </Typography>
        </Box>
      )}

      {data?.correlation && <CorrelationSections correlation={data.correlation} />}
    </Drawer>
  );
}

function ErrorFacts({ errorEvent: e }: { errorEvent: SqlErrorEvent }) {
  const parameters = e.parameters ?? {};

  return (
    <Box>
      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, mb: 2 }}>
        <SqlErrorCategoryBadge category={e.error_category} />
        <SeverityBadge severity={e.severity} />
        <TimeoutClassificationBadge classification={e.timeout_classification} hideWhenNotApplicable />
        {!e.confirmed && (
          <Chip
            label="Unconfirmed"
            size="small"
            color="warning"
            variant="outlined"
            title="SQLPulse derived this event from its own evidence; nothing reported it."
          />
        )}
      </Box>

      <Alert severity="error" variant="outlined" sx={{ mb: 2 }}>
        {e.message}
      </Alert>

      <Section title="When and where">
        <Row label="Timestamp" value={formatTimestamp(e.timestamp)} />
        <Row label="Server" value={e.server || EM_DASH} />
        <Row label="Database" value={e.database ?? EM_DASH} />
        <Row label="Application" value={e.application ?? EM_DASH} />
        <Row label="Service" value={e.application ?? EM_DASH} />
        <Row label="Environment" value={e.environment ?? EM_DASH} />
        <Row label="Host" value={e.host ?? EM_DASH} />
      </Section>

      <Section title="Execution Context">
        <Row label="Stored Procedure" value={e.stored_procedure ?? EM_DASH} />
        <Row label="Query Hash" value={e.query_hash ?? EM_DASH} mono />
        <Row label="Duration" value={formatDuration(e.duration_ms)} />
        <Row label="Request Endpoint" value={e.request_endpoint ?? EM_DASH} />
        <Row label="Exception Type" value={e.exception_type ?? EM_DASH} />
        <Row
          label="Command Timeout"
          value={
            e.command_timeout_seconds != null ? `${e.command_timeout_seconds} sec` : EM_DASH
          }
        />
        <Box sx={{ mt: 1 }}>
          <Typography variant="body2" color="text.secondary" gutterBottom>
            Parameters
          </Typography>
          {Object.keys(parameters).length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              Parameter values were not available for this event.
            </Typography>
          ) : (
            Object.entries(parameters).map(([name, value]) => (
              <Row key={name} label={name} value={value ?? EM_DASH} mono />
            ))
          )}
        </Box>
      </Section>

      <Section title="SQL Server error">
        <Row
          label="Error Number"
          value={e.sql_error_number != null ? String(e.sql_error_number) : EM_DASH}
        />
        <Row
          label="State"
          value={e.sql_error_state != null ? String(e.sql_error_state) : EM_DASH}
        />
        <Row
          label="Class"
          value={e.sql_error_class != null ? String(e.sql_error_class) : EM_DASH}
        />
      </Section>

      <Section title="Correlation identifiers">
        <Row label="Trace ID" value={e.trace_id ?? EM_DASH} mono />
        <Row label="Correlation ID" value={e.correlation_id ?? EM_DASH} mono />
        <Row label="Session" value={e.session_identifier ?? EM_DASH} mono />
      </Section>

      <Section title="Reported by">
        <Row label="Source" value={e.source || EM_DASH} />
        <Row label="Source Kind" value={e.source_kind.replace(/_/g, ' ').toLowerCase()} />
      </Section>

      {/*
        The classification is shown with the rules that produced it so a reader
        can check the reasoning rather than trusting the label.
      */}
      {e.classification_evidence.length > 0 && (
        <Section title="Why it was classified this way">
          <List dense disablePadding>
            {e.classification_evidence.map((line) => (
              <ListItem key={line} disableGutters sx={{ py: 0.25 }}>
                <ListItemText
                  primary={line}
                  slotProps={{ primary: { variant: 'body2', color: 'text.secondary' } }}
                />
              </ListItem>
            ))}
          </List>
        </Section>
      )}
    </Box>
  );
}

/**
 * Renders the correlation in two visually distinct blocks.
 *
 * The evidence is what SQLPulse observed. The assessment is a reading of that
 * evidence. Merging them would let a reader mistake an interpretation for a
 * measurement, so they never share a container.
 */
function CorrelationSections({ correlation: c }: { correlation: SqlErrorCorrelation }) {
  return (
    <Box>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 1, flexWrap: 'wrap' }}>
        <Typography variant="subtitle1" fontWeight={600}>
          Correlated SQL Evidence
        </Typography>
        <Chip
          size="small"
          variant="outlined"
          color={confidenceColor[c.correlation_confidence]}
          label={`${c.correlation_confidence} correlation`}
        />
        {c.identity_matched && (
          <Chip size="small" variant="outlined" label="Same query identity" />
        )}
      </Box>

      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1.5 }}>
        Observed between {formatTimestamp(c.window_start)} and {formatTimestamp(c.window_end)}{' '}
        (±{c.window_minutes} minutes).
      </Typography>

      {c.correlated_evidence.length === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          SQLPulse holds no matching observation for this window.
        </Typography>
      ) : (
        <Box sx={{ mb: 2 }}>
          {c.correlated_evidence.map((item, index) => (
            <EvidenceCard key={`${item.kind}-${index}`} item={item} />
          ))}
        </Box>
      )}

      <Divider sx={{ my: 2 }} />

      <Typography variant="subtitle1" fontWeight={600} gutterBottom>
        Assessment
      </Typography>
      <Paper variant="outlined" sx={{ p: 2, mb: 2, bgcolor: 'action.hover' }}>
        <Typography variant="body2">{c.assessment}</Typography>
      </Paper>

      {c.caveats.length > 0 && (
        <Alert severity="info" variant="outlined">
          <AlertTitle>Limits of this evidence</AlertTitle>
          <List dense disablePadding>
            {c.caveats.map((caveat) => (
              <ListItem key={caveat} disableGutters sx={{ py: 0.25 }}>
                <ListItemText primary={caveat} slotProps={{ primary: { variant: 'body2' } }} />
              </ListItem>
            ))}
          </List>
        </Alert>
      )}
    </Box>
  );
}

function EvidenceCard({ item }: { item: CorrelatedEvidenceItem }) {
  return (
    <Paper variant="outlined" sx={{ p: 1.5, mb: 1 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5, flexWrap: 'wrap' }}>
        <Typography variant="body2" fontWeight={600}>
          {evidenceKindLabels[item.kind] ?? item.kind}
        </Typography>
        {item.matched_on.map((match) => (
          <Chip
            key={match}
            size="small"
            variant="outlined"
            label={`matched on ${match}`}
            color={match === 'query_hash' || match === 'stored_procedure' ? 'primary' : 'default'}
          />
        ))}
      </Box>

      <Typography variant="body2" sx={{ mb: 0.5 }}>
        {item.summary}
      </Typography>

      {item.detail.map((line) => (
        <Typography key={line} variant="caption" color="text.secondary" sx={{ display: 'block' }}>
          {line}
        </Typography>
      ))}

      {item.observed_at && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
          Observed {formatTimestamp(item.observed_at)}
        </Typography>
      )}
    </Paper>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Box sx={{ mb: 2 }}>
      <Typography variant="subtitle2" color="text.secondary" gutterBottom>
        {title}
      </Typography>
      {children}
    </Box>
  );
}

function Row({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <Box sx={{ display: 'flex', justifyContent: 'space-between', gap: 2, py: 0.25 }}>
      <Typography variant="body2" color="text.secondary">
        {label}
      </Typography>
      <Typography
        variant="body2"
        fontWeight={500}
        sx={{
          textAlign: 'right',
          wordBreak: 'break-word',
          ...(mono ? { fontFamily: 'monospace', fontSize: 12 } : {}),
        }}
      >
        {value}
      </Typography>
    </Box>
  );
}

/** Drawer state, keyed by id so the detail is always re-fetched. */
export function useSqlErrorDrawer() {
  const [selected, setSelected] = useState<SqlErrorEvent | null>(null);

  return {
    selected,
    open: (errorEvent: SqlErrorEvent) => setSelected(errorEvent),
    close: () => setSelected(null),
  };
}
