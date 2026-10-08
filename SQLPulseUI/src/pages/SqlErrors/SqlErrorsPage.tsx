import { useCallback, useState } from 'react';
import { Box, Paper, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from '@mui/material';
import { PageHeader, LoadingState, ErrorAlert, EmptyState } from '../../components/common';
import SqlErrorFilters, {
  getDefaultSqlErrorFilters,
  type SqlErrorFilterValues,
} from '../../components/sql-errors/SqlErrorFilters';
import SqlErrorStatusAlert from '../../components/sql-errors/SqlErrorStatusAlert';
import SqlErrorSummaryCards from '../../components/sql-errors/SqlErrorSummaryCards';
import SqlErrorsTable from '../../components/sql-errors/SqlErrorsTable';
import SqlErrorDetailDrawer, {
  useSqlErrorDrawer,
} from '../../components/sql-errors/SqlErrorDetailDrawer';
import { useAnalysis } from '../../hooks/useAnalysis';
import { getSqlErrors, getSqlErrorSummary } from '../../api/sqlErrorsApi';
import { formatTimestamp } from '../../utils/formatters';
import type {
  SqlErrorListResponse,
  SqlErrorParams,
  SqlErrorSummaryResponse,
} from '../../types/sqlErrors';

/**
 * SQL error investigation, kept separate from performance analysis.
 *
 * Performance pages answer "what is slow?". This one answers "what failed?".
 * Every classification, severity and timeout judgement on this screen is made by
 * the backend; the page only renders it, and is careful never to present an
 * empty table as an all-clear.
 */
export default function SqlErrorsPage() {
  const { databases, databasesLoading } = useAnalysis();
  const drawer = useSqlErrorDrawer();

  const [filters, setFilters] = useState<SqlErrorFilterValues>(getDefaultSqlErrorFilters);
  const [list, setList] = useState<SqlErrorListResponse | null>(null);
  const [summary, setSummary] = useState<SqlErrorSummaryResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleFiltersChange = useCallback((next: SqlErrorFilterValues) => {
    // Do not keep showing a response for the previous database/time range while
    // the user is editing filters. The old response can otherwise look like it
    // belongs to the newly selected database until Search Errors is clicked.
    setFilters(next);
    setList(null);
    setSummary(null);
    setError(null);
  }, []);

  const handleSearch = useCallback(async () => {
    setLoading(true);
    setError(null);

    const params: SqlErrorParams = {
      start_time: new Date(filters.startTime).toISOString(),
      end_time: new Date(filters.endTime).toISOString(),
      top_n: filters.topN,
      ...(filters.database ? { database: filters.database } : {}),
      ...(filters.category ? { category: filters.category } : {}),
      ...(filters.severity ? { severity: filters.severity } : {}),
      ...(filters.application ? { application: filters.application } : {}),
      ...(filters.storedProcedure ? { stored_procedure: filters.storedProcedure } : {}),
      ...(filters.confirmedOnly ? { confirmed_only: true } : {}),
    };

    try {
      // The summary is not derived from the returned page, so a top_n limit can
      // never make the totals disagree with the table.
      const [listResult, summaryResult] = await Promise.all([
        getSqlErrors(params),
        getSqlErrorSummary(params),
      ]);

      setList(listResult);
      setSummary(summaryResult);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to fetch SQL errors');
      setList(null);
      setSummary(null);
    } finally {
      setLoading(false);
    }
  }, [filters]);

  return (
    <>
      <PageHeader
        title="SQL Errors"
        subtitle="Normalized SQL error events, correlated with SQLPulse performance evidence"
      />

      <SqlErrorFilters
        values={filters}
        databases={databases}
        databasesLoading={databasesLoading}
        onChange={handleFiltersChange}
        onSearch={handleSearch}
        loading={loading}
      />

      {loading && <LoadingState message="Querying SQL errors…" />}

      {error && <ErrorAlert message={error} title="SQL Error Query Failed" />}

      {!list && !loading && !error && (
        <EmptyState message="Choose a period and any filters, then click Search Errors." />
      )}

      {list && !loading && (
        <>
          <SqlErrorStatusAlert
            status={list.status}
            message={list.message}
            applicationTelemetryAvailable={list.application_telemetry_available}
            applicationTelemetryStatus={list.application_telemetry?.status ?? null}
            applicationTelemetry={list.application_telemetry ?? null}
            sources={list.sources}
          />

          {summary && <SqlErrorSummaryCards summary={summary} />}

          {list.application_telemetry && (
            <ApplicationTelemetrySection telemetry={list.application_telemetry} />
          )}

          <Box sx={{ mb: 1 }}>
            <Typography variant="caption" color="text.secondary">
              {formatTimestamp(list.start_time)} to {formatTimestamp(list.end_time)}
              {list.database ? ` — ${list.database}` : ' — all databases'}
              {' — '}showing {list.returned} of {list.total_matched} matching error
              {list.total_matched === 1 ? '' : 's'}
              {list.total_matched > list.returned ? ` (top ${list.top_n})` : ''}
            </Typography>
          </Box>

          <SqlErrorsTable errors={list.errors} onErrorClick={drawer.open} />
        </>
      )}

      <SqlErrorDetailDrawer
        errorId={drawer.selected?.id ?? null}
        fallback={drawer.selected}
        onClose={drawer.close}
      />
    </>
  );
}

function ApplicationTelemetrySection({ telemetry }: { telemetry: NonNullable<SqlErrorListResponse['application_telemetry']> }) {
  return (
    <Box sx={{ mb: 2 }}>
      <Typography variant="h6" sx={{ mb: 1 }}>Application Telemetry</Typography>
      <TableContainer component={Paper} variant="outlined">
        <Table size="small">
          <TableHead><TableRow><TableCell>Application</TableCell><TableCell>SigNoz Status</TableCell><TableCell align="right">Errors</TableCell></TableRow></TableHead>
          <TableBody>{telemetry.applications.map((application) => (
            <TableRow key={`${application.name}:${application.service_name}`}>
              <TableCell>{application.name || application.service_name}</TableCell>
              <TableCell>{application.status}</TableCell>
              <TableCell align="right">{application.error_count}</TableCell>
            </TableRow>
          ))}</TableBody>
        </Table>
      </TableContainer>
    </Box>
  );
}
