import { useEffect, useState } from 'react';
import {
  Drawer,
  Box,
  Typography,
  IconButton,
  Divider,
  Chip,
  List,
  ListItem,
  ListItemText,
  Alert,
  Button,
  CircularProgress,
  Accordion,
  AccordionSummary,
  AccordionDetails,
  Dialog,
  DialogTitle,
  DialogContent,
} from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import { SeverityBadge, ConfidenceBadge, SqlText } from '../../components/common';
import ErrorAlert from '../../components/common/ErrorAlert';
import WorkloadBadge from './WorkloadBadge';
import { formatDuration, formatNumber, formatTimestamp } from '../../utils/formatters';
import { getExecutionPlan } from '../../api/performanceApi';
import type { TimeoutCandidate } from '../../types/history';
import type { ExecutionPlanParameter, ExecutionPlanResponse } from '../../types/performance';

const planCache = new Map<string, ExecutionPlanResponse>();

interface Props {
  candidate: TimeoutCandidate | null;
  database: string | null;
  thresholdMs: number;
  onClose: () => void;
}

export default function SlowQueryDetailDrawer({ candidate, database, thresholdMs, onClose }: Props) {
  return (
    <Drawer
      anchor="right"
      open={candidate !== null}
      onClose={onClose}
      PaperProps={{ sx: { width: { xs: '100%', sm: 560 }, p: 3 } }}
    >
      {candidate && <Content candidate={candidate} database={database} thresholdMs={thresholdMs} onClose={onClose} />}
    </Drawer>
  );
}

function Content({ candidate: c, database, thresholdMs, onClose }: { candidate: TimeoutCandidate; database: string | null; thresholdMs: number; onClose: () => void }) {
  const [plan, setPlan] = useState<ExecutionPlanResponse | null>(null);
  const [planLoading, setPlanLoading] = useState(false);
  const [planError, setPlanError] = useState<string | null>(null);
  const [rawOpen, setRawOpen] = useState(false);
  const planKey = `${database ?? ''}|${c.query_hash ?? ''}|${c.query_id ?? ''}|${c.plan_id ?? ''}`;
  const hasPlanIdentifiers = Boolean(database && (c.query_hash || c.query_id != null || c.plan_id != null));

  useEffect(() => {
    setPlan(planCache.get(planKey) ?? null);
    setPlanError(null);
    setRawOpen(false);
  }, [planKey]);

  const loadPlan = async (includeXml = false) => {
    if (!hasPlanIdentifiers || !database) return;
    setPlanLoading(true);
    setPlanError(null);
    try {
      const result = await getExecutionPlan({
        database,
        ...(c.query_hash ? { query_hash: c.query_hash } : {}),
        ...(c.query_id != null ? { query_id: c.query_id } : {}),
        ...(c.plan_id != null ? { plan_id: c.plan_id } : {}),
        include_xml: includeXml,
      });
      setPlan(result);
      planCache.set(planKey, result);
      if (includeXml && result.status === 'AVAILABLE' && result.plan_xml) setRawOpen(true);
    } catch (error) {
      setPlanError(error instanceof Error ? error.message : 'Execution plan could not be retrieved.');
    } finally {
      setPlanLoading(false);
    }
  };
  const isTimeoutCandidate = c.max_duration_ms > thresholdMs;
  const baseline = c.baseline_comparison;
  const hasBaseline = baseline && baseline.baseline_status === 'READY';
  const planParameters = plan?.parameters ?? [];
  const parameters = planParameters.length > 0 ? planParameters : parseParameterDeclarations(c.sql_text_preview);
  const hasCapturedParameterValues = planParameters.some((parameter) => parameter.compiled_value || parameter.runtime_value);

  return (
    <Box>
      {/* Header */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 2 }}>
        <Typography variant="h6" fontWeight={600}>
          Slow Query Detail
        </Typography>
        <IconButton onClick={onClose} size="small"><CloseIcon /></IconButton>
      </Box>

      {/* Badges */}
      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, mb: 2 }}>
        <SeverityBadge severity={c.severity} />
        <ConfidenceBadge confidence={c.confidence} />
        <WorkloadBadge type={c.workload_type} />
        {isTimeoutCandidate && (
          <Chip label="Timeout Candidate" size="small" color="error" variant="outlined" />
        )}
      </Box>

      <Divider sx={{ mb: 2 }} />

      {/* Identity */}
      <Section title="Identity">
        <MetricRow label="Procedure" value={c.procedure_name ?? '—'} />
        <MetricRow label="Query Hash" value={c.query_hash ?? '—'} mono />
        <MetricRow label="Query ID" value={c.query_id != null ? String(c.query_id) : '—'} />
        <MetricRow label="Plan ID" value={c.plan_id != null ? String(c.plan_id) : '—'} />
      </Section>

      {/* Duration metrics */}
      <Section title="Duration">
        <MetricRow label="Avg Duration" value={formatDuration(c.avg_duration_ms)} tooltip={`${c.avg_duration_ms.toFixed(1)} ms`} />
        <MetricRow label="Max Duration" value={formatDuration(c.max_duration_ms)} tooltip={`${c.max_duration_ms.toFixed(1)} ms`} />
        <MetricRow label="Min Duration" value={formatDuration(c.min_duration_ms)} tooltip={`${c.min_duration_ms.toFixed(1)} ms`} />
      </Section>

      {/* CPU metrics */}
      <Section title="CPU">
        <MetricRow label="Avg CPU" value={formatDuration(c.avg_cpu_ms)} tooltip={`${c.avg_cpu_ms.toFixed(1)} ms`} />
        <MetricRow label="Max CPU" value={formatDuration(c.max_cpu_ms)} tooltip={`${c.max_cpu_ms.toFixed(1)} ms`} />
      </Section>

      {/* I/O metrics */}
      <Section title="I/O">
        <MetricRow label="Avg Logical Reads" value={formatNumber(Math.round(c.avg_logical_reads))} tooltip={c.avg_logical_reads.toLocaleString()} />
        <MetricRow label="Max Logical Reads" value={formatNumber(Math.round(c.max_logical_reads))} tooltip={c.max_logical_reads.toLocaleString()} />
      </Section>

      {/* Execution */}
      <Section title="Execution">
        <MetricRow label="Execution Count" value={formatNumber(c.execution_count)} />
        <MetricRow label="Total Executions" value={formatNumber(c.total_execution_count)} />
        <MetricRow label="Intervals Observed" value={String(c.intervals_observed)} />
        <MetricRow label="Intervals Exceeding Threshold" value={String(c.intervals_exceeding_threshold)} />
      </Section>

      {/* Timestamps */}
      <Section title="Observed">
        <MetricRow label="First Execution" value={c.first_execution_time ? formatTimestamp(c.first_execution_time) : '—'} />
        <MetricRow label="Last Execution" value={c.last_execution_time ? formatTimestamp(c.last_execution_time) : '—'} />
      </Section>

      {/* Baseline Comparison */}
      <Section title="Baseline Comparison">
        {!baseline && (
          <Typography variant="body2" color="text.secondary">No baseline data available.</Typography>
        )}
        {baseline && !hasBaseline && (
          <Alert severity="info" sx={{ mb: 1 }}>
            Baseline Status: <strong>INSUFFICIENT_DATA</strong> — Not enough historical samples to establish a performance baseline.
          </Alert>
        )}
        {baseline && hasBaseline && (
          <>
            <MetricRow label="Samples" value={String(baseline.sample_count)} />
            <MetricRow label="Historical Mean" value={formatDuration(baseline.historical_mean_ms)} />
            <MetricRow label="Historical Median" value={formatDuration(baseline.historical_median_ms)} />
            <MetricRow label="Historical P95" value={formatDuration(baseline.historical_p95_ms)} />
            <MetricRow label="Std Deviation" value={formatDuration(baseline.standard_deviation_ms)} />
            {baseline.regression_multiplier_vs_mean > 0 && (
              <MetricRow label="Regression vs Mean" value={`${baseline.regression_multiplier_vs_mean.toFixed(1)}×`} />
            )}
            {baseline.regression_multiplier_vs_p95 > 0 && (
              <MetricRow label="Regression vs P95" value={`${baseline.regression_multiplier_vs_p95.toFixed(1)}×`} />
            )}
          </>
        )}
      </Section>

      {/* Classification Evidence */}
      {c.classification_evidence.length > 0 && (
        <Section title="Classification Evidence">
          <List dense disablePadding>
            {c.classification_evidence.map((e, i) => (
              <ListItem key={i} disableGutters sx={{ py: 0.25 }}>
                <ListItemText primary={e} primaryTypographyProps={{ variant: 'body2' }} />
              </ListItem>
            ))}
          </List>
        </Section>
      )}

      {/* SQL Preview */}
      {c.sql_text_preview && (
        <Section title="SQL Preview">
          <SqlText sql={c.sql_text_preview} block copyable />
        </Section>
      )}

      <Section title="Execution Plan">
        {!hasPlanIdentifiers ? (
          <Alert severity="info">Execution plan is not available for this query.</Alert>
        ) : !plan ? (
          <Button variant="outlined" onClick={() => void loadPlan()} disabled={planLoading}>
            {planLoading ? <CircularProgress size={18} sx={{ mr: 1 }} /> : null}
            View Execution Plan
          </Button>
        ) : null}
        {planLoading && <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>Loading execution plan…</Typography>}
        {planError && <ErrorAlert title="Execution Plan Error" message={planError} onRetry={() => void loadPlan()} />}
        {plan && plan.status !== 'AVAILABLE' && (
          <Alert severity={plan.status === 'PERMISSION_DENIED' ? 'warning' : 'info'} sx={{ mt: 1 }}>
            {plan.status === 'NOT_FOUND'
              ? 'Execution plan was not found for this query.'
              : plan.status === 'PERMISSION_DENIED'
                ? 'Permission is required to read this execution plan.'
                : plan.message ?? 'Execution plan could not currently be retrieved.'}
          </Alert>
        )}
        {plan?.status === 'AVAILABLE' && (
          <>
            <Accordion defaultExpanded disableGutters>
              <AccordionSummary expandIcon={<ExpandMoreIcon />}>Execution Plan Summary</AccordionSummary>
              <AccordionDetails>
                <MetricRow label="Plan ID" value={String(plan.plan_id ?? c.plan_id ?? '—')} />
                <MetricRow label="Query ID" value={String(plan.query_id ?? c.query_id ?? '—')} />
                <MetricRow label="Query Hash" value={plan.query_hash ?? c.query_hash ?? '—'} mono />
                <MetricRow label="Database" value={plan.database ?? database ?? '—'} />
                <MetricRow label="Retrieved At" value={formatTimestamp(plan.retrieved_at)} />
                <MetricRow label="Statements" value={String(plan.statement_count)} />
                <MetricRow label="Operators" value={String(plan.total_operator_count)} />
                <Typography variant="subtitle2" sx={{ mt: 1 }}>Highest relative estimated cost</Typography>
                {plan.highest_cost_operators.length === 0 ? (
                  <Typography variant="body2" color="text.secondary">No relative operator cost was available.</Typography>
                ) : (
                  <List dense disablePadding>
                    {plan.highest_cost_operators.map((operator, index) => (
                      <ListItem key={`${operator.type}-${index}`} disableGutters sx={{ py: 0.25 }}>
                        <ListItemText
                          primary={operator.type}
                          secondary={[
                            operator.estimated_cost_percent == null ? null : `Relative estimated cost: ${operator.estimated_cost_percent}% of statement estimate`,
                            operator.estimated_rows == null ? null : `Estimated rows: ${formatNumber(Math.round(operator.estimated_rows))}`,
                            operator.actual_rows == null ? null : `Actual rows: ${formatNumber(Math.round(operator.actual_rows))}`,
                          ].filter(Boolean).join(' | ')}
                        />
                      </ListItem>
                    ))}
                  </List>
                )}
              </AccordionDetails>
            </Accordion>
            <Accordion disableGutters>
              <AccordionSummary expandIcon={<ExpandMoreIcon />}>Stored Procedure Parameters</AccordionSummary>
              <AccordionDetails>
                <Alert severity="info" sx={{ mb: 1 }}>
                  {hasCapturedParameterValues
                    ? 'Values come from the execution plan. Compiled values are optimizer estimates; runtime values are shown only when SQL Server captured them.'
                    : 'Parameter names and types come from the SQL text. SQL Server did not retain the values used for this historical execution.'}
                </Alert>
                {parameters.length === 0 ? (
                  <Typography variant="body2" color="text.secondary">No parameter metadata was present in this plan.</Typography>
                ) : (
                  <List dense disablePadding>
                    {parameters.map((parameter) => (
                      <ListItem key={parameter.name} disableGutters sx={{ display: 'block', py: 0.5 }}>
                        <ListItemText
                          primary={parameter.name}
                          secondary={[
                            parameter.data_type ? `Type: ${parameter.data_type}` : null,
                            parameter.runtime_value ? `Runtime value: ${parameter.runtime_value}` : null,
                            parameter.compiled_value ? `Compiled value: ${parameter.compiled_value}` : null,
                          ].filter(Boolean).join(' | ') || 'No value was captured'}
                        />
                      </ListItem>
                    ))}
                  </List>
                )}
              </AccordionDetails>
            </Accordion>
            <Accordion disableGutters>
              <AccordionSummary expandIcon={<ExpandMoreIcon />}>Operator Counts</AccordionSummary>
              <AccordionDetails>
                <List dense disablePadding>
                  {Object.entries(plan.operator_counts_by_type).map(([type, count]) => (
                    <ListItem key={type} disableGutters sx={{ py: 0.25 }}>
                      <ListItemText primary={`${type}: ${count}`} />
                    </ListItem>
                  ))}
                </List>
              </AccordionDetails>
            </Accordion>
            <Accordion defaultExpanded disableGutters>
              <AccordionSummary expandIcon={<ExpandMoreIcon />}>Top Plan Findings</AccordionSummary>
              <AccordionDetails>
                {plan.findings.length === 0 ? <Typography variant="body2" color="text.secondary">No evidence-based plan concerns were identified.</Typography> : plan.findings.map((finding, index) => (
                  <Box key={`${finding.category}-${index}`} sx={{ mb: 1.5 }}>
                    <Typography variant="body2" fontWeight={600}>{finding.category} | {finding.operator}</Typography>
                    <Typography variant="body2">{finding.summary}</Typography>
                    <Typography variant="caption" color="text.secondary">Severity: {finding.severity} | Confidence: {finding.confidence}</Typography>
                    <Typography variant="body2" color="text.secondary">{finding.evidence.join(' | ')}</Typography>
                    <Typography variant="caption" display="block">Recommendation: {finding.recommendation}</Typography>
                    {finding.human_review_required && <Chip label="Human review required" size="small" sx={{ mt: 0.5 }} />}
                  </Box>
                ))}
              </AccordionDetails>
            </Accordion>
            {plan.warnings.length > 0 && <Accordion disableGutters>
              <AccordionSummary expandIcon={<ExpandMoreIcon />}>Warnings</AccordionSummary>
              <AccordionDetails><List dense>{plan.warnings.map((warning) => <ListItem key={warning}><ListItemText primary={warning} /></ListItem>)}</List></AccordionDetails>
            </Accordion>}
            {plan.plan_limitations.length > 0 && <Accordion disableGutters>
              <AccordionSummary expandIcon={<ExpandMoreIcon />}>Plan Limitations</AccordionSummary>
              <AccordionDetails>
                <List dense disablePadding>
                  {plan.plan_limitations.map((limitation) => (
                    <ListItem key={limitation} disableGutters sx={{ py: 0.25 }}>
                      <ListItemText primary={limitation} />
                    </ListItem>
                  ))}
                </List>
              </AccordionDetails>
            </Accordion>}
            <Accordion disableGutters>
              <AccordionSummary expandIcon={<ExpandMoreIcon />}>Detailed Operators</AccordionSummary>
              <AccordionDetails>
                <List dense disablePadding>
                  {plan.operators.map((operator, index) => (
                    <ListItem key={`${operator.type}-${index}`} disableGutters sx={{ display: 'block', py: 0.5 }}>
                      <ListItemText
                        primary={`${operator.type}${operator.object ? ` — ${operator.object}` : ''}`}
                        secondary={[
                          operator.estimated_cost_percent == null ? null : `Relative estimated cost: ${operator.estimated_cost_percent}% of statement estimate`,
                          operator.estimated_rows == null ? null : `Estimated rows: ${formatNumber(Math.round(operator.estimated_rows))}`,
                          operator.actual_rows == null ? null : `Actual rows: ${formatNumber(Math.round(operator.actual_rows))}`,
                        ].filter(Boolean).join(' | ') || undefined}
                      />
                    </ListItem>
                  ))}
                </List>
              </AccordionDetails>
            </Accordion>            <Button sx={{ mt: 1 }} size="small" onClick={() => void loadPlan(true)} disabled={planLoading}>
              View Raw Plan
            </Button>
          </>
        )}
      </Section>

      <Dialog open={rawOpen} onClose={() => setRawOpen(false)} fullWidth maxWidth="md">
        <DialogTitle>Raw Execution Plan XML</DialogTitle>
        <DialogContent>
          <Box component="pre" sx={{ maxHeight: '70vh', overflow: 'auto', whiteSpace: 'pre-wrap', fontSize: 12, fontFamily: 'monospace' }}>
            {plan?.plan_xml}
          </Box>
        </DialogContent>
      </Dialog>
    </Box>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Box sx={{ mb: 2 }}>
      <Typography variant="subtitle2" color="text.secondary" gutterBottom>{title}</Typography>
      {children}
    </Box>
  );
}

function MetricRow({ label, value, mono, tooltip }: { label: string; value: string; mono?: boolean; tooltip?: string }) {
  return (
    <Box sx={{ display: 'flex', justifyContent: 'space-between', py: 0.25 }}>
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography
        variant="body2"
        fontWeight={500}
        title={tooltip}
        sx={mono ? { fontFamily: 'monospace', fontSize: 12 } : undefined}
      >
        {value}
      </Typography>
    </Box>
  );
}

function parseParameterDeclarations(sql: string | null): ExecutionPlanParameter[] {
  if (!sql) return [];
  const declarationBlock = sql.match(/^\s*\(([\s\S]*?)\)\s*(?:SELECT|EXEC|WITH|INSERT|UPDATE|DELETE)\b/i)?.[1];
  if (!declarationBlock) return [];

  const parameters: ExecutionPlanParameter[] = [];
  const parameterPattern = /(@[A-Za-z_][A-Za-z0-9_]*)\s+([^,\s]+(?:\s*\([^)]*\))?)/gi;
  for (const match of declarationBlock.matchAll(parameterPattern)) {
    parameters.push({
      name: match[1] ?? '',
      data_type: match[2] ?? null,
      compiled_value: null,
      runtime_value: null,
    });
  }
  return parameters;
}

// Hook for managing drawer state
export function useSlowQueryDrawer() {
  const [selected, setSelected] = useState<TimeoutCandidate | null>(null);
  const open = (c: TimeoutCandidate) => setSelected(c);
  const close = () => setSelected(null);
  return { selected, open, close };
}
