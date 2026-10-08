import { Typography, Box, IconButton, Tooltip } from '@mui/material';
import LaunchIcon from '@mui/icons-material/Launch';
import type { AffectedDatabase } from '../../types/performance';
import { SeverityBadge, DataTable, type DataTableColumn } from '../common';

const SEVERITY_RANK: Record<string, number> = {
  critical: 4,
  high: 3,
  medium: 2,
  low: 1,
};

interface AffectedDatabasesTableProps {
  databases: AffectedDatabase[];
  onDatabaseClick: (database: string) => void;
}

export default function AffectedDatabasesTable({
  databases,
  onDatabaseClick,
}: AffectedDatabasesTableProps) {
  if (databases.length === 0) {
    return null;
  }

  const columns: DataTableColumn<AffectedDatabase>[] = [
    {
      id: 'database',
      label: 'Database',
      render: (db) => (
        <Typography variant="body2" fontWeight={500}>
          {db.database}
        </Typography>
      ),
      sortValue: (db) => db.database,
      noWrap: true,
    },
    {
      id: 'highest_severity',
      label: 'Highest Severity',
      headerTooltip: 'Most severe finding currently open for this database',
      render: (db) => <SeverityBadge severity={db.highest_severity} />,
      sortValue: (db) => SEVERITY_RANK[String(db.highest_severity).toLowerCase()] ?? 0,
      noWrap: true,
    },
    {
      id: 'finding_count',
      label: 'Finding Count',
      align: 'right',
      render: (db) => db.finding_count,
      sortValue: (db) => db.finding_count,
      noWrap: true,
    },
    {
      id: 'analyze',
      label: 'Analyze',
      align: 'right',
      render: (db) => (
        <Tooltip title={`Analyze ${db.database}`}>
          <IconButton
            size="small"
            color="primary"
            aria-label={`Analyze ${db.database}`}
            onClick={() => onDatabaseClick(db.database)}
          >
            <LaunchIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      ),
    },
  ];

  return (
    <Box sx={{ mb: 3 }}>
      <Typography variant="h6" fontWeight={600} sx={{ mb: 1 }}>
        Affected Databases
      </Typography>
      <DataTable
        ariaLabel="Affected databases"
        columns={columns}
        rows={databases}
        getRowKey={(db) => db.database}
        initialSortBy="highest_severity"
        initialSortDirection="desc"
        maxHeight={440}
      />
    </Box>
  );
}
