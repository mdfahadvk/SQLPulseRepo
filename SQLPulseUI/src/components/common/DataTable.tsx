import { useMemo, useState, useEffect, type ReactNode } from 'react';
import {
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TableSortLabel,
  TablePagination,
  Paper,
  Tooltip,
  Box,
} from '@mui/material';
import EmptyState from './EmptyState';

export type SortDirection = 'asc' | 'desc';

export interface DataTableColumn<T> {
  /** Stable identifier, also used as the sort key. */
  id: string;
  /** Header text. */
  label: string;
  align?: 'left' | 'right' | 'center';
  /** Cell renderer. */
  render: (row: T) => ReactNode;
  /**
   * Return a primitive to enable sorting on this column.
   * Omit to make the column non-sortable.
   */
  sortValue?: (row: T) => string | number | null | undefined;
  /** Optional header tooltip explaining the metric. */
  headerTooltip?: string;
  minWidth?: number;
  /** Prevent the cell from wrapping (dates, numbers). */
  noWrap?: boolean;
}

interface DataTableProps<T> {
  columns: DataTableColumn<T>[];
  rows: T[];
  getRowKey: (row: T, index: number) => string | number;
  /** Accessible name for the table. Required. */
  ariaLabel: string;
  onRowClick?: (row: T) => void;
  initialSortBy?: string;
  initialSortDirection?: SortDirection;
  emptyMessage?: string;
  /** Show pagination once the row count exceeds this. */
  paginateAfter?: number;
  defaultRowsPerPage?: number;
  rowsPerPageOptions?: number[];
  /** Constrain height and pin the header row. */
  maxHeight?: number | string;
  size?: 'small' | 'medium';
  /** Extra content rendered above the table (filters, counts). */
  toolbar?: ReactNode;
}

function compare(
  a: string | number | null | undefined,
  b: string | number | null | undefined,
): number {
  // Nulls always sort last regardless of direction.
  const aNull = a == null || a === '';
  const bNull = b == null || b === '';
  if (aNull && bNull) return 0;
  if (aNull) return 1;
  if (bNull) return -1;

  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b), undefined, {
    numeric: true,
    sensitivity: 'base',
  });
}

export default function DataTable<T>({
  columns,
  rows,
  getRowKey,
  ariaLabel,
  onRowClick,
  initialSortBy,
  initialSortDirection = 'desc',
  emptyMessage = 'No data available.',
  paginateAfter = 25,
  defaultRowsPerPage = 25,
  rowsPerPageOptions = [10, 25, 50, 100],
  maxHeight,
  size = 'small',
  toolbar,
}: DataTableProps<T>) {
  const [sortBy, setSortBy] = useState<string | null>(initialSortBy ?? null);
  const [sortDirection, setSortDirection] = useState<SortDirection>(initialSortDirection);
  const [page, setPage] = useState(0);
  const [rowsPerPage, setRowsPerPage] = useState(defaultRowsPerPage);

  // Reset to the first page whenever the underlying data changes size,
  // otherwise a filtered result set can leave the user on an empty page.
  useEffect(() => {
    setPage(0);
  }, [rows.length]);

  const sortedRows = useMemo(() => {
    if (!sortBy) return rows;
    const column = columns.find((c) => c.id === sortBy);
    if (!column?.sortValue) return rows;

    const factor = sortDirection === 'asc' ? 1 : -1;
    // Copy first — never mutate the array owned by the caller.
    return [...rows].sort((a, b) => factor * compare(column.sortValue!(a), column.sortValue!(b)));
  }, [rows, columns, sortBy, sortDirection]);

  const paginated = rows.length > paginateAfter;
  const visibleRows = useMemo(() => {
    if (!paginated) return sortedRows;
    const start = page * rowsPerPage;
    return sortedRows.slice(start, start + rowsPerPage);
  }, [sortedRows, paginated, page, rowsPerPage]);

  const handleSort = (columnId: string) => {
    if (sortBy === columnId) {
      setSortDirection((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortBy(columnId);
      setSortDirection('desc');
    }
    setPage(0);
  };

  if (rows.length === 0) {
    return <EmptyState message={emptyMessage} />;
  }

  return (
    <Paper variant="outlined">
      {toolbar}
      <TableContainer sx={maxHeight ? { maxHeight } : undefined}>
        <Table size={size} stickyHeader={Boolean(maxHeight)} aria-label={ariaLabel}>
          <TableHead>
            <TableRow>
              {columns.map((col) => {
                const sortable = Boolean(col.sortValue);
                const active = sortBy === col.id;
                const header = col.headerTooltip ? (
                  <Tooltip title={col.headerTooltip} arrow placement="top">
                    <span>{col.label}</span>
                  </Tooltip>
                ) : (
                  col.label
                );

                return (
                  <TableCell
                    key={col.id}
                    align={col.align}
                    sx={{ minWidth: col.minWidth, whiteSpace: 'nowrap', fontWeight: 600 }}
                    sortDirection={active ? sortDirection : false}
                  >
                    {sortable ? (
                      <TableSortLabel
                        active={active}
                        direction={active ? sortDirection : 'desc'}
                        onClick={() => handleSort(col.id)}
                      >
                        {header}
                      </TableSortLabel>
                    ) : (
                      header
                    )}
                  </TableCell>
                );
              })}
            </TableRow>
          </TableHead>
          <TableBody>
            {visibleRows.map((row, i) => {
              const clickable = Boolean(onRowClick);
              return (
                <TableRow
                  key={getRowKey(row, i)}
                  hover
                  tabIndex={clickable ? 0 : -1}
                  role={clickable ? 'button' : undefined}
                  onClick={clickable ? () => onRowClick!(row) : undefined}
                  onKeyDown={
                    clickable
                      ? (e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            onRowClick!(row);
                          }
                        }
                      : undefined
                  }
                  sx={
                    clickable
                      ? {
                          cursor: 'pointer',
                          '&:focus-visible': {
                            outline: '2px solid',
                            outlineColor: 'primary.main',
                            outlineOffset: -2,
                          },
                        }
                      : undefined
                  }
                >
                  {columns.map((col) => (
                    <TableCell
                      key={col.id}
                      align={col.align}
                      sx={{
                        minWidth: col.minWidth,
                        whiteSpace: col.noWrap ? 'nowrap' : undefined,
                      }}
                    >
                      {col.render(row)}
                    </TableCell>
                  ))}
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </TableContainer>

      {paginated && (
        <Box sx={{ borderTop: '1px solid', borderColor: 'divider' }}>
          <TablePagination
            component="div"
            count={rows.length}
            page={page}
            onPageChange={(_, p) => setPage(p)}
            rowsPerPage={rowsPerPage}
            onRowsPerPageChange={(e) => {
              setRowsPerPage(parseInt(e.target.value, 10));
              setPage(0);
            }}
            rowsPerPageOptions={rowsPerPageOptions}
            slotProps={{
              actions: {
                previousButton: { 'aria-label': 'Previous page' },
                nextButton: { 'aria-label': 'Next page' },
              },
            }}
          />
        </Box>
      )}
    </Paper>
  );
}
