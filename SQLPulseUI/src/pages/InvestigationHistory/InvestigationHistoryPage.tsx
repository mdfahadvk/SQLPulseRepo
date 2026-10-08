import { useState, useMemo } from 'react';
import {
  Box,
  Paper,
  TextField,
  MenuItem,
  Typography,
  Chip,
  Alert,
  InputAdornment,
  Tooltip,
} from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import {
  PageHeader,
  LoadingState,
  EmptyState,
  SeverityBadge,
  ConfidenceBadge,
  DataTable,
} from '../../components/common';
import type { DataTableColumn } from '../../components/common';
import InvestigationDetailDrawer, {
  useInvestigationDrawer,
} from '../../components/investigation/InvestigationDetailDrawer';
import { useInvestigationList } from '../../hooks/useQueries';
import { formatTimestamp, formatFloat, orDash } from '../../utils/formatters';
import type { InvestigationAuditRecord } from '../../types/agent';

const SEVERITIES = ['all', 'critical', 'high', 'medium', 'low', 'unknown'];

/** Total wall-clock time spent in tool calls for an investigation. */
function toolDuration(inv: InvestigationAuditRecord): number {
  return inv.tools_called.reduce((sum, t) => sum + (t.duration_seconds || 0), 0);
}

const columns: DataTableColumn<InvestigationAuditRecord>[] = [
  {
    id: 'created_at',
    label: 'Timestamp',
    noWrap: true,
    sortValue: (inv) => new Date(inv.created_at).getTime(),
    render: (inv) => formatTimestamp(inv.created_at),
  },
  {
    id: 'database',
    label: 'Database',
    sortValue: (inv) => inv.database,
    render: (inv) => orDash(inv.database),
  },
  {
    id: 'question_sanitized',
    label: 'Question',
    minWidth: 260,
    sortValue: (inv) => inv.question_sanitized,
    render: (inv) => (
      <Tooltip title={inv.question_sanitized}>
        <Typography variant="body2" noWrap sx={{ maxWidth: 340 }}>
          {inv.question_sanitized}
        </Typography>
      </Tooltip>
    ),
  },
  {
    id: 'severity',
    label: 'Severity',
    sortValue: (inv) => inv.severity,
    render: (inv) => <SeverityBadge severity={inv.severity} />,
  },
  {
    id: 'confidence',
    label: 'Confidence',
    sortValue: (inv) => inv.confidence,
    render: (inv) => <ConfidenceBadge confidence={inv.confidence} />,
  },
  {
    id: 'tools_used',
    label: 'Tools Used',
    align: 'right',
    headerTooltip: 'Number of read-only tools the agent invoked',
    sortValue: (inv) => inv.tools_called.length,
    render: (inv) => <Chip label={inv.tools_called.length} size="small" variant="outlined" />,
  },
  {
    id: 'duration',
    label: 'Duration',
    align: 'right',
    noWrap: true,
    headerTooltip: 'Total time spent executing tools',
    sortValue: (inv) => toolDuration(inv),
    render: (inv) => `${formatFloat(toolDuration(inv), 2)}s`,
  },
];

export default function InvestigationHistoryPage() {
  const { data, isLoading, isError } = useInvestigationList(100);
  const drawer = useInvestigationDrawer();

  const [search, setSearch] = useState('');
  const [severityFilter, setSeverityFilter] = useState('all');
  const [databaseFilter, setDatabaseFilter] = useState('all');

  const investigations = data?.investigations ?? [];

  const databaseOptions = useMemo(() => {
    const set = new Set<string>();
    for (const inv of investigations) {
      if (inv.database) set.add(inv.database);
    }
    return ['all', ...Array.from(set).sort()];
  }, [investigations]);

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    return investigations.filter((inv) => {
      if (severityFilter !== 'all' && inv.severity !== severityFilter) return false;
      if (databaseFilter !== 'all' && inv.database !== databaseFilter) return false;
      if (term) {
        const haystack = `${inv.question_sanitized} ${inv.answer} ${inv.database ?? ''}`.toLowerCase();
        if (!haystack.includes(term)) return false;
      }
      return true;
    });
  }, [investigations, search, severityFilter, databaseFilter]);

  return (
    <>
      <PageHeader title="Investigation History" subtitle="Past AI investigation audit trail" />

      {/* Storage failure — friendly, non-blocking */}
      {isError && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          Investigation history storage is currently unavailable. All other SQLPulse features
          continue to work normally.
        </Alert>
      )}

      {/* Filters */}
      {!isError && (
        <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2, alignItems: 'center' }}>
            <TextField
              placeholder="Search question or answer…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              size="small"
              sx={{ minWidth: 280, flex: 1 }}
              slotProps={{
                input: {
                  startAdornment: (
                    <InputAdornment position="start">
                      <SearchIcon fontSize="small" />
                    </InputAdornment>
                  ),
                },
              }}
            />

            <TextField
              select
              label="Severity"
              value={severityFilter}
              onChange={(e) => setSeverityFilter(e.target.value)}
              size="small"
              sx={{ width: 150 }}
            >
              {SEVERITIES.map((s) => (
                <MenuItem key={s} value={s}>
                  {s === 'all' ? 'All severities' : s}
                </MenuItem>
              ))}
            </TextField>

            <TextField
              select
              label="Database"
              value={databaseFilter}
              onChange={(e) => setDatabaseFilter(e.target.value)}
              size="small"
              sx={{ width: 180 }}
            >
              {databaseOptions.map((db) => (
                <MenuItem key={db} value={db}>
                  {db === 'all' ? 'All databases' : db}
                </MenuItem>
              ))}
            </TextField>

            <Typography variant="body2" color="text.secondary">
              {filtered.length} of {investigations.length}
            </Typography>
          </Box>
        </Paper>
      )}

      {/* Loading */}
      {isLoading && <LoadingState message="Loading investigation history…" />}

      {/* Empty */}
      {!isLoading && !isError && investigations.length === 0 && (
        <EmptyState message="No investigations have been recorded yet. Run an AI investigation to build the audit trail." />
      )}

      {!isLoading && !isError && investigations.length > 0 && filtered.length === 0 && (
        <EmptyState message="No investigations match the current filters." />
      )}

      {/* List */}
      {!isLoading && !isError && filtered.length > 0 && (
        <DataTable
          ariaLabel="Investigation history"
          columns={columns}
          rows={filtered}
          getRowKey={(inv) => inv.investigation_id}
          onRowClick={(inv) => drawer.open(inv.investigation_id)}
          initialSortBy="created_at"
          emptyMessage="No investigations match the current filters."
          maxHeight={620}
        />
      )}

      {/* Detail drawer */}
      <InvestigationDetailDrawer investigationId={drawer.selectedId} onClose={drawer.close} />
    </>
  );
}
