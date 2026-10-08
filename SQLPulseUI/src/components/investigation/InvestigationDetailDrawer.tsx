import { useState, useEffect, useCallback } from 'react';
import {
  Drawer,
  Box,
  Typography,
  IconButton,
  Divider,
  Chip,
  Alert,
  CircularProgress,
  Paper,
  List,
  ListItem,
  ListItemIcon,
  ListItemText,
} from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';
import ReportProblemIcon from '@mui/icons-material/ReportProblem';
import { SeverityBadge, ConfidenceBadge } from '../common';
import { getInvestigationDetail } from '../../api/agentApi';
import { formatTimestamp } from '../../utils/formatters';
import type { InvestigationAuditRecord } from '../../types/agent';

interface Props {
  investigationId: string | null;
  onClose: () => void;
}

/** Map backend errors to friendly, non-leaking messages. */
function friendlyError(err: unknown): string {
  const msg = err instanceof Error ? err.message : String(err);
  const lower = msg.toLowerCase();
  if (lower.includes('unable to retrieve') || lower.includes('500'))
    return 'Investigation history storage is currently unavailable. Other SQLPulse features are unaffected.';
  if (msg.length > 200) return 'Unable to load this investigation. Please try again.';
  return msg;
}

function Field({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <Box sx={{ mb: 1.5 }}>
      <Typography variant="caption" color="text.secondary">
        {label}
      </Typography>
      <Typography
        variant="body2"
        fontFamily={mono ? 'monospace' : undefined}
        fontSize={mono ? 13 : undefined}
        whiteSpace="pre-wrap"
      >
        {value}
      </Typography>
    </Box>
  );
}

function SectionTitle({ children }: { children: React.ReactNode }) {
  return (
    <Typography variant="subtitle2" fontWeight={700} sx={{ mt: 2, mb: 1 }}>
      {children}
    </Typography>
  );
}

export default function InvestigationDetailDrawer({ investigationId, onClose }: Props) {
  const [record, setRecord] = useState<InvestigationAuditRecord | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!investigationId) {
      setRecord(null);
      setNotFound(false);
      setError(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    setRecord(null);
    setNotFound(false);

    getInvestigationDetail(investigationId)
      .then((res) => {
        if (cancelled) return;
        if (!res.found || !res.investigation) {
          setNotFound(true);
        } else {
          setRecord(res.investigation);
        }
      })
      .catch((e) => {
        if (!cancelled) setError(friendlyError(e));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [investigationId]);

  const totalDuration = record
    ? record.tools_called.reduce((sum, t) => sum + (t.duration_seconds || 0), 0)
    : 0;

  return (
    <Drawer
      anchor="right"
      open={Boolean(investigationId)}
      onClose={onClose}
      slotProps={{ paper: { sx: { width: { xs: '100%', sm: 560 }, p: 2.5 } } }}
    >
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mb: 1 }}>
        <Typography variant="h6" fontWeight={700}>
          Investigation Detail
        </Typography>
        <IconButton onClick={onClose} size="small">
          <CloseIcon />
        </IconButton>
      </Box>
      <Divider sx={{ mb: 2 }} />

      {loading && (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
          <CircularProgress size={28} />
        </Box>
      )}

      {error && <Alert severity="warning">{error}</Alert>}

      {notFound && !loading && (
        <Alert severity="info">This investigation record could not be found.</Alert>
      )}

      {record && !loading && (
        <Box>
          <Field label="Investigation ID" value={record.investigation_id} mono />
          <Field label="Timestamp" value={formatTimestamp(record.created_at)} />

          <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', mb: 2 }}>
            <SeverityBadge severity={record.severity} />
            <ConfidenceBadge confidence={record.confidence} />
            <Chip label={`Server: ${record.server}`} size="small" variant="outlined" />
            {record.database && (
              <Chip label={`DB: ${record.database}`} size="small" variant="outlined" />
            )}
          </Box>

          <Divider />

          <SectionTitle>Question</SectionTitle>
          <Typography variant="body2" whiteSpace="pre-wrap">
            {record.question_sanitized}
          </Typography>

          <SectionTitle>Answer</SectionTitle>
          <Typography variant="body2" whiteSpace="pre-wrap">
            {record.answer || '—'}
          </Typography>

          {(record.time_range_start || record.time_range_end) && (
            <>
              <SectionTitle>Time Range Analysed</SectionTitle>
              <Typography variant="body2">
                {record.time_range_start ? formatTimestamp(record.time_range_start) : '—'}
                {' → '}
                {record.time_range_end ? formatTimestamp(record.time_range_end) : '—'}
              </Typography>
            </>
          )}

          {/* Evidence — tool calls actually executed */}
          <SectionTitle>Evidence — Tools Executed</SectionTitle>
          {record.tools_called.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              No tool calls were recorded.
            </Typography>
          ) : (
            <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
              {record.tools_called.map((tc, i) => (
                <Paper
                  key={i}
                  variant="outlined"
                  sx={{ p: 1.5, borderLeft: 4, borderColor: tc.success ? 'info.main' : 'error.main' }}
                >
                  <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
                    <Typography variant="body2" fontWeight={600} fontFamily="monospace">
                      {tc.tool_name}
                    </Typography>
                    <Chip
                      label={tc.success ? 'success' : 'failed'}
                      size="small"
                      color={tc.success ? 'success' : 'error'}
                      variant="outlined"
                    />
                    <Typography variant="caption" color="text.secondary">
                      {tc.duration_seconds.toFixed(2)}s
                    </Typography>
                  </Box>

                  {Object.keys(tc.arguments_sanitized).length > 0 && (
                    <Typography
                      variant="caption"
                      component="pre"
                      sx={{ m: 0, fontFamily: 'monospace', whiteSpace: 'pre-wrap', color: 'text.secondary' }}
                    >
                      {JSON.stringify(tc.arguments_sanitized, null, 2)}
                    </Typography>
                  )}

                  {Object.keys(tc.result_summary).length > 0 && (
                    <Typography
                      variant="caption"
                      component="pre"
                      sx={{ m: 0, mt: 0.5, fontFamily: 'monospace', whiteSpace: 'pre-wrap' }}
                    >
                      {JSON.stringify(tc.result_summary, null, 2)}
                    </Typography>
                  )}

                  {tc.error && (
                    <Typography variant="caption" color="error.main" display="block" sx={{ mt: 0.5 }}>
                      {tc.error}
                    </Typography>
                  )}
                </Paper>
              ))}
            </Box>
          )}

          {record.finding_references.length > 0 && (
            <>
              <SectionTitle>Findings Referenced</SectionTitle>
              <List dense disablePadding>
                {record.finding_references.map((f, i) => (
                  <ListItem key={i} disableGutters sx={{ py: 0.25 }}>
                    <ListItemIcon sx={{ minWidth: 28 }}>
                      <CheckCircleOutlineIcon sx={{ fontSize: 16, color: 'info.main' }} />
                    </ListItemIcon>
                    <ListItemText
                      primary={f}
                      primaryTypographyProps={{ variant: 'body2', fontFamily: 'monospace', fontSize: 13 }}
                    />
                  </ListItem>
                ))}
              </List>
            </>
          )}

          <SectionTitle>Summary</SectionTitle>
          <Box sx={{ display: 'flex', gap: 3, flexWrap: 'wrap', mb: 1 }}>
            <Box>
              <Typography variant="caption" color="text.secondary">
                Tools Used
              </Typography>
              <Typography variant="body2">{record.tools_called.length}</Typography>
            </Box>
            <Box>
              <Typography variant="caption" color="text.secondary">
                Total Tool Duration
              </Typography>
              <Typography variant="body2">{totalDuration.toFixed(2)}s</Typography>
            </Box>
          </Box>

          <Divider sx={{ my: 2 }} />

          {/* Safety */}
          {record.human_review_required && (
            <Alert severity="warning" icon={<ReportProblemIcon />} sx={{ mb: 1.5 }}>
              <strong>Human Review Required</strong> — this conclusion should be verified by a DBA.
            </Alert>
          )}

          <Alert
            severity={record.remediation_performed ? 'warning' : 'info'}
            variant="outlined"
            icon={record.remediation_performed ? <ErrorOutlineIcon /> : <CheckCircleOutlineIcon />}
          >
            <strong>Agent Action:</strong>{' '}
            {record.remediation_performed
              ? 'A remediation action was recorded for this investigation.'
              : 'No database remediation was performed. Observation and recommendation only.'}
          </Alert>
        </Box>
      )}
    </Drawer>
  );
}

/** Hook to manage which investigation is open in the drawer. */
export function useInvestigationDrawer() {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const open = useCallback((id: string) => setSelectedId(id), []);
  const close = useCallback(() => setSelectedId(null), []);
  return { selectedId, open, close };
}
