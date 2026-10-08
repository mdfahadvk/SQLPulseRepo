import { useMemo } from 'react';
import { Typography, Box, Tooltip } from '@mui/material';
import type { PerformanceFinding } from '../../types/performance';
import {
  SeverityBadge,
  ConfidenceBadge,
  EmptyState,
  DataTable,
  type DataTableColumn,
} from '../common';
import { formatTimestamp } from '../../utils/formatters';

/** Highest severity sorts first when the column is sorted descending. */
const SEVERITY_RANK: Record<string, number> = {
  critical: 4,
  high: 3,
  medium: 2,
  low: 1,
};

const CONFIDENCE_RANK: Record<string, number> = {
  high: 3,
  medium: 2,
  low: 1,
};

function formatCategory(category: string): string {
  return category
    .split('_')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}

interface FindingsTableProps {
  title: string;
  findings: PerformanceFinding[];
  onFindingClick: (finding: PerformanceFinding) => void;
  showDatabase?: boolean;
  showLastObserved?: boolean;
  emptyMessage?: string;
}

export default function FindingsTable({
  title,
  findings,
  onFindingClick,
  showDatabase = true,
  showLastObserved = true,
  emptyMessage = 'No findings — looking good!',
}: FindingsTableProps) {
  const columns = useMemo<DataTableColumn<PerformanceFinding>[]>(() => {
    const base: DataTableColumn<PerformanceFinding>[] = [
      {
        id: 'severity',
        label: 'Severity',
        render: (f) => <SeverityBadge severity={f.severity} />,
        sortValue: (f) => SEVERITY_RANK[String(f.severity).toLowerCase()] ?? 0,
        noWrap: true,
      },
      {
        id: 'category',
        label: 'Category',
        render: (f) => formatCategory(f.category),
        sortValue: (f) => f.category,
        noWrap: true,
      },
      {
        id: 'summary',
        label: 'Summary',
        minWidth: 280,
        render: (f) => (
          <Tooltip title={f.summary} placement="top-start">
            <Typography variant="body2" noWrap sx={{ maxWidth: 460 }}>
              {f.summary}
            </Typography>
          </Tooltip>
        ),
        sortValue: (f) => f.summary,
      },
      {
        id: 'confidence',
        label: 'Confidence',
        render: (f) => <ConfidenceBadge confidence={f.confidence} />,
        sortValue: (f) => CONFIDENCE_RANK[String(f.confidence).toLowerCase()] ?? 0,
        noWrap: true,
      },
    ];

    if (showDatabase) {
      base.push({
        id: 'database',
        label: 'Database',
        render: (f) => f.database ?? '—',
        sortValue: (f) => f.database ?? '',
        noWrap: true,
      });
    }

    if (showLastObserved) {
      base.push({
        id: 'last_observed',
        label: 'Last Observed',
        headerTooltip: 'Most recent time this finding was detected',
        render: (f) => formatTimestamp(f.last_observed),
        sortValue: (f) => new Date(f.last_observed).getTime(),
        noWrap: true,
      });
    }

    return base;
  }, [showDatabase, showLastObserved]);

  return (
    <Box sx={{ mb: 3 }}>
      <Typography variant="h6" fontWeight={600} sx={{ mb: 1 }}>
        {title}
      </Typography>

      {findings.length === 0 ? (
        <EmptyState variant="positive" message={emptyMessage} dense />
      ) : (
        <DataTable
          ariaLabel={title}
          columns={columns}
          rows={findings}
          getRowKey={(f) => f.finding_id}
          onRowClick={onFindingClick}
          initialSortBy="severity"
          initialSortDirection="desc"
          maxHeight={520}
        />
      )}
    </Box>
  );
}
