import { Box, Tooltip, Typography } from '@mui/material';
import { DataTable, SeverityBadge } from '../common';
import type { DataTableColumn } from '../common';
import SqlErrorCategoryBadge from './SqlErrorCategoryBadge';
import TimeoutClassificationBadge from './TimeoutClassificationBadge';
import { formatDuration, formatTimestamp, truncateSql } from '../../utils/formatters';
import type { SqlErrorEvent } from '../../types/sqlErrors';

interface Props {
  errors: SqlErrorEvent[];
  onErrorClick: (errorEvent: SqlErrorEvent) => void;
}

const EM_DASH = '—';

const sourceKindLabels: Record<string, string> = {
  APPLICATION_TELEMETRY: 'Application telemetry',
  SQL_SERVER: 'SQL Server',
  DERIVED_PERFORMANCE_EVIDENCE: 'Derived from SQLPulse performance evidence — not an observed error report',
};

export default function SqlErrorsTable({ errors, onErrorClick }: Props) {
  const columns: DataTableColumn<SqlErrorEvent>[] = [
    {
      id: 'timestamp',
      label: 'Time',
      noWrap: true,
      minWidth: 170,
      render: (row) => formatTimestamp(row.timestamp),
      sortValue: (row) => new Date(row.timestamp).getTime(),
    },
    {
      id: 'database',
      label: 'Database',
      render: (row) => row.database ?? EM_DASH,
      sortValue: (row) => row.database,
    },
    {
      id: 'application',
      label: 'Application',
      render: (row) => row.application ?? EM_DASH,
      sortValue: (row) => row.application,
    },
    {
      id: 'error_category',
      label: 'Category',
      render: (row) => <SqlErrorCategoryBadge category={row.error_category} />,
      sortValue: (row) => row.error_category,
    },
    {
      id: 'message',
      label: 'Error',
      minWidth: 280,
      render: (row) => (
        <Box>
          <Tooltip title={row.message} arrow>
            <Typography variant="body2" component="span">
              {truncateSql(row.message, 90)}
            </Typography>
          </Tooltip>
          {row.sql_error_number != null && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
              SQL error {row.sql_error_number}
            </Typography>
          )}
        </Box>
      ),
      sortValue: (row) => row.message,
    },
    {
      id: 'stored_procedure',
      label: 'Procedure',
      minWidth: 180,
      render: (row) => row.stored_procedure ?? EM_DASH,
      sortValue: (row) => row.stored_procedure,
    },
    {
      id: 'duration_ms',
      label: 'Duration',
      align: 'right',
      noWrap: true,
      render: (row) => formatDuration(row.duration_ms),
      sortValue: (row) => row.duration_ms,
    },
    {
      id: 'severity',
      label: 'Severity',
      render: (row) => <SeverityBadge severity={row.severity} />,
      sortValue: (row) => row.severity,
    },
    {
      id: 'source',
      label: 'Source',
      minWidth: 160,
      headerTooltip:
        'Which source reported the event. Derived evidence was inferred by SQLPulse, not reported by an application.',
      render: (row) => (
        <Box>
          <Tooltip title={sourceKindLabels[row.source_kind] ?? row.source_kind} arrow>
            <Typography variant="body2" component="span">
              {row.source}
            </Typography>
          </Tooltip>
          <Box sx={{ mt: 0.5 }}>
            <TimeoutClassificationBadge
              classification={row.timeout_classification}
              hideWhenNotApplicable
            />
          </Box>
        </Box>
      ),
      sortValue: (row) => row.source,
    },
  ];

  return (
    <DataTable
      columns={columns}
      rows={errors}
      getRowKey={(row) => row.id}
      ariaLabel="SQL errors"
      onRowClick={onErrorClick}
      initialSortBy="timestamp"
      initialSortDirection="desc"
      emptyMessage="No SQL errors matched these filters."
      paginateAfter={25}
      defaultRowsPerPage={25}
    />
  );
}
