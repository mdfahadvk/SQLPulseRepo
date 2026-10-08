import { useMemo } from 'react';
import { Tooltip, Typography, Chip, Box } from '@mui/material';
import { SeverityBadge, ConfidenceBadge, DataTable } from '../../components/common';
import type { DataTableColumn } from '../../components/common';
import WorkloadBadge from './WorkloadBadge';
import {
  formatDuration,
  formatNumber,
  formatTimestamp,
  formatInteger,
  orDash,
  shortenHash,
} from '../../utils/formatters';
import type { TimeoutCandidate } from '../../types/history';

interface Props {
  candidates: TimeoutCandidate[];
  thresholdMs: number;
  onCandidateClick: (candidate: TimeoutCandidate) => void;
}

export default function SlowQueryResultsTable({
  candidates,
  thresholdMs,
  onCandidateClick,
}: Props) {
  // Rebuilt only when the threshold changes — the timeout indicator depends on it.
  const columns = useMemo<DataTableColumn<TimeoutCandidate>[]>(
    () => [
      {
        id: 'procedure_name',
        label: 'Procedure',
        minWidth: 150,
        sortValue: (c) => c.procedure_name,
        render: (c) => (
          <Tooltip title={c.procedure_name ?? ''} disableHoverListener={!c.procedure_name}>
            <Typography variant="body2" noWrap sx={{ maxWidth: 160 }}>
              {orDash(c.procedure_name)}
            </Typography>
          </Tooltip>
        ),
      },
      {
        id: 'query_hash',
        label: 'Query Hash',
        noWrap: true,
        sortValue: (c) => c.query_hash,
        render: (c) => (
          <Tooltip title={c.query_hash ?? ''} disableHoverListener={!c.query_hash}>
            <Typography
              variant="body2"
              component="span"
              sx={{ fontFamily: 'monospace', fontSize: 12 }}
            >
              {shortenHash(c.query_hash)}
            </Typography>
          </Tooltip>
        ),
      },
      {
        id: 'workload_type',
        label: 'Workload',
        sortValue: (c) => c.workload_type,
        render: (c) => <WorkloadBadge type={c.workload_type} />,
      },
      {
        id: 'execution_count',
        label: 'Executions',
        align: 'right',
        noWrap: true,
        sortValue: (c) => c.execution_count,
        render: (c) => (
          <Tooltip title={formatInteger(c.execution_count)}>
            <span>{formatNumber(c.execution_count)}</span>
          </Tooltip>
        ),
      },
      {
        id: 'avg_duration_ms',
        label: 'Avg Duration',
        align: 'right',
        noWrap: true,
        headerTooltip: 'Average execution duration over the selected window',
        sortValue: (c) => c.avg_duration_ms,
        render: (c) => (
          <Tooltip title={`${c.avg_duration_ms.toFixed(1)} ms`}>
            <span>{formatDuration(c.avg_duration_ms)}</span>
          </Tooltip>
        ),
      },
      {
        id: 'max_duration_ms',
        label: 'Max Duration',
        align: 'right',
        minWidth: 190,
        headerTooltip: 'Longest recorded execution — flagged when above the threshold',
        sortValue: (c) => c.max_duration_ms,
        render: (c) => (
          <Box
            sx={{ display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 0.75 }}
          >
            <Tooltip title={`${c.max_duration_ms.toFixed(1)} ms`}>
              <span>{formatDuration(c.max_duration_ms)}</span>
            </Tooltip>
            {c.max_duration_ms > thresholdMs && (
              <Chip
                label="Timeout Candidate"
                size="small"
                color="error"
                variant="outlined"
                sx={{ height: 20, fontSize: 10 }}
              />
            )}
          </Box>
        ),
      },
      {
        id: 'avg_cpu_ms',
        label: 'Avg CPU',
        align: 'right',
        noWrap: true,
        sortValue: (c) => c.avg_cpu_ms,
        render: (c) => (
          <Tooltip title={`${c.avg_cpu_ms.toFixed(1)} ms`}>
            <span>{formatDuration(c.avg_cpu_ms)}</span>
          </Tooltip>
        ),
      },
      {
        id: 'max_cpu_ms',
        label: 'Max CPU',
        align: 'right',
        noWrap: true,
        sortValue: (c) => c.max_cpu_ms,
        render: (c) => (
          <Tooltip title={`${c.max_cpu_ms.toFixed(1)} ms`}>
            <span>{formatDuration(c.max_cpu_ms)}</span>
          </Tooltip>
        ),
      },
      {
        id: 'avg_logical_reads',
        label: 'Avg Reads',
        align: 'right',
        noWrap: true,
        sortValue: (c) => c.avg_logical_reads,
        render: (c) => (
          <Tooltip title={formatInteger(c.avg_logical_reads)}>
            <span>{formatNumber(Math.round(c.avg_logical_reads))}</span>
          </Tooltip>
        ),
      },
      {
        id: 'max_logical_reads',
        label: 'Max Reads',
        align: 'right',
        noWrap: true,
        sortValue: (c) => c.max_logical_reads,
        render: (c) => (
          <Tooltip title={formatInteger(c.max_logical_reads)}>
            <span>{formatNumber(Math.round(c.max_logical_reads))}</span>
          </Tooltip>
        ),
      },
      {
        id: 'first_execution_time',
        label: 'First Observed',
        noWrap: true,
        sortValue: (c) =>
          c.first_execution_time ? new Date(c.first_execution_time).getTime() : null,
        render: (c) => (
          <Typography variant="body2" noWrap sx={{ fontSize: 12 }}>
            {formatTimestamp(c.first_execution_time)}
          </Typography>
        ),
      },
      {
        id: 'last_execution_time',
        label: 'Last Observed',
        noWrap: true,
        sortValue: (c) =>
          c.last_execution_time ? new Date(c.last_execution_time).getTime() : null,
        render: (c) => (
          <Typography variant="body2" noWrap sx={{ fontSize: 12 }}>
            {formatTimestamp(c.last_execution_time)}
          </Typography>
        ),
      },
      {
        id: 'intervals_exceeding_threshold',
        label: 'Intervals Exceeded',
        align: 'right',
        noWrap: true,
        headerTooltip: 'Query Store intervals above the threshold, out of those observed',
        sortValue: (c) => c.intervals_exceeding_threshold,
        render: (c) => `${c.intervals_exceeding_threshold} / ${c.intervals_observed}`,
      },
      {
        id: 'severity',
        label: 'Severity',
        sortValue: (c) => c.severity,
        render: (c) => <SeverityBadge severity={c.severity} />,
      },
      {
        id: 'confidence',
        label: 'Confidence',
        sortValue: (c) => c.confidence,
        render: (c) => <ConfidenceBadge confidence={c.confidence} />,
      },
    ],
    [thresholdMs],
  );

  return (
    <DataTable
      ariaLabel="Slow query candidates"
      columns={columns}
      rows={candidates}
      getRowKey={(c, i) => c.query_hash ?? c.query_id ?? i}
      onRowClick={onCandidateClick}
      initialSortBy="max_duration_ms"
      emptyMessage="No slow queries found in the selected time range."
      maxHeight={600}
    />
  );
}
