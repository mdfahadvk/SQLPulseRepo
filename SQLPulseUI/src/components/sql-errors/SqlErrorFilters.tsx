import {
  Box,
  Button,
  Checkbox,
  CircularProgress,
  FormControl,
  FormControlLabel,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  TextField,
} from '@mui/material';
import type { SelectChangeEvent } from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import { toDateTimeLocal } from '../../utils/formatters';
import { SQL_ERROR_CATEGORIES, sqlErrorCategoryLabel } from './SqlErrorCategoryBadge';
import type { SqlErrorCategory, SqlErrorSeverity } from '../../types/sqlErrors';

const SEVERITY_OPTIONS: { label: string; value: '' | SqlErrorSeverity }[] = [
  { label: 'All', value: '' },
  { label: 'Critical', value: 'critical' },
  { label: 'High', value: 'high' },
  { label: 'Medium', value: 'medium' },
  { label: 'Low', value: 'low' },
];

const TOP_N_OPTIONS = [25, 50, 100, 250, 500];

export interface SqlErrorFilterValues {
  database: string;
  startTime: string;
  endTime: string;
  category: '' | SqlErrorCategory;
  severity: '' | SqlErrorSeverity;
  application: string;
  storedProcedure: string;
  confirmedOnly: boolean;
  topN: number;
}

interface Props {
  values: SqlErrorFilterValues;
  databases: string[];
  databasesLoading: boolean;
  onChange: (values: SqlErrorFilterValues) => void;
  onSearch: () => void;
  loading: boolean;
}

export function getDefaultSqlErrorFilters(): SqlErrorFilterValues {
  const now = new Date();
  const dayAgo = new Date(now.getTime() - 24 * 60 * 60 * 1000);

  return {
    // Blank means every database. Errors are worth seeing server-wide before
    // narrowing down, unlike Query Store analysis which is per database.
    database: '',
    startTime: toDateTimeLocal(dayAgo),
    endTime: toDateTimeLocal(now),
    category: '',
    severity: '',
    application: '',
    storedProcedure: '',
    confirmedOnly: false,
    topN: 100,
  };
}

export default function SqlErrorFilters({
  values,
  databases,
  databasesLoading,
  onChange,
  onSearch,
  loading,
}: Props) {
  const update = (patch: Partial<SqlErrorFilterValues>) => onChange({ ...values, ...patch });

  const rangeInvalid =
    Boolean(values.startTime) &&
    Boolean(values.endTime) &&
    new Date(values.endTime).getTime() <= new Date(values.startTime).getTime();

  const canSearch =
    Boolean(values.startTime) && Boolean(values.endTime) && !rangeInvalid && !loading;

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && canSearch) {
      e.preventDefault();
      onSearch();
    }
  };

  return (
    <Paper
      variant="outlined"
      sx={{ p: 2, mb: 3 }}
      component="section"
      aria-label="SQL error filters"
      onKeyDown={handleKeyDown}
    >
      <Stack spacing={2}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} flexWrap="wrap" useFlexGap>
          <FormControl size="small" sx={{ minWidth: 200 }}>
            <InputLabel id="err-db-label">Database</InputLabel>
            <Select
              labelId="err-db-label"
              id="err-db"
              value={values.database}
              label="Database"
              onChange={(e: SelectChangeEvent) => update({ database: e.target.value })}
              disabled={databasesLoading}
              startAdornment={
                databasesLoading ? <CircularProgress size={16} sx={{ mr: 1 }} /> : undefined
              }
            >
              <MenuItem value="">All databases</MenuItem>
              {databases.map((db) => (
                <MenuItem key={db} value={db}>
                  {db}
                </MenuItem>
              ))}
            </Select>
          </FormControl>

          <TextField
            label="Start Date/Time"
            type="datetime-local"
            size="small"
            value={values.startTime}
            onChange={(e) => update({ startTime: e.target.value })}
            slotProps={{ inputLabel: { shrink: true } }}
            sx={{ minWidth: 210 }}
          />

          <TextField
            label="End Date/Time"
            type="datetime-local"
            size="small"
            value={values.endTime}
            onChange={(e) => update({ endTime: e.target.value })}
            slotProps={{ inputLabel: { shrink: true } }}
            error={rangeInvalid}
            helperText={rangeInvalid ? 'End must be after start' : undefined}
            sx={{ minWidth: 210 }}
          />
        </Stack>

        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} flexWrap="wrap" useFlexGap>
          <FormControl size="small" sx={{ minWidth: 200 }}>
            <InputLabel id="err-category-label">Error Category</InputLabel>
            <Select
              labelId="err-category-label"
              id="err-category"
              value={values.category}
              label="Error Category"
              onChange={(e: SelectChangeEvent) =>
                update({ category: e.target.value as '' | SqlErrorCategory })
              }
            >
              <MenuItem value="">All categories</MenuItem>
              {SQL_ERROR_CATEGORIES.map((category) => (
                <MenuItem key={category} value={category}>
                  {sqlErrorCategoryLabel(category)}
                </MenuItem>
              ))}
            </Select>
          </FormControl>

          <FormControl size="small" sx={{ minWidth: 140 }}>
            <InputLabel id="err-severity-label">Severity</InputLabel>
            <Select
              labelId="err-severity-label"
              id="err-severity"
              value={values.severity}
              label="Severity"
              onChange={(e: SelectChangeEvent) =>
                update({ severity: e.target.value as '' | SqlErrorSeverity })
              }
            >
              {SEVERITY_OPTIONS.map((option) => (
                <MenuItem key={option.value} value={option.value}>
                  {option.label}
                </MenuItem>
              ))}
            </Select>
          </FormControl>

          <TextField
            label="Application"
            size="small"
            value={values.application}
            onChange={(e) => update({ application: e.target.value })}
            sx={{ minWidth: 180 }}
          />

          <TextField
            label="Stored Procedure"
            size="small"
            value={values.storedProcedure}
            onChange={(e) => update({ storedProcedure: e.target.value })}
            sx={{ minWidth: 220 }}
          />
        </Stack>

        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={2}
          alignItems={{ xs: 'stretch', sm: 'center' }}
          flexWrap="wrap"
          useFlexGap
        >
          <FormControl size="small" sx={{ minWidth: 110 }}>
            <InputLabel id="err-topn-label">Top N</InputLabel>
            <Select
              labelId="err-topn-label"
              id="err-topn"
              value={values.topN}
              label="Top N"
              onChange={(e: SelectChangeEvent<number>) => update({ topN: Number(e.target.value) })}
            >
              {TOP_N_OPTIONS.map((n) => (
                <MenuItem key={n} value={n}>
                  {n}
                </MenuItem>
              ))}
            </Select>
          </FormControl>

          <FormControlLabel
            control={
              <Checkbox
                size="small"
                checked={values.confirmedOnly}
                onChange={(e) => update({ confirmedOnly: e.target.checked })}
              />
            }
            label="Confirmed only"
            title="Exclude events SQLPulse derived from its own performance evidence rather than observed being reported."
          />

          <Box sx={{ flexGrow: 1 }} />
          <Button
            variant="contained"
            startIcon={loading ? <CircularProgress size={18} color="inherit" /> : <SearchIcon />}
            onClick={onSearch}
            disabled={!canSearch}
          >
            {loading ? 'Searching…' : 'Search Errors'}
          </Button>
        </Stack>
      </Stack>
    </Paper>
  );
}
