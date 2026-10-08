import {
  Box,
  FormControl,
  InputLabel,
  Select,
  MenuItem,
  TextField,
  Button,
  Stack,
  Paper,
  CircularProgress,
} from '@mui/material';
import type { SelectChangeEvent } from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import { toDateTimeLocal } from '../../utils/formatters';
import type { WorkloadType } from '../../types/history';

const THRESHOLD_PRESETS = [
  { label: '5 seconds', ms: 5_000 },
  { label: '10 seconds', ms: 10_000 },
  { label: '20 seconds', ms: 20_000 },
  { label: '30 seconds', ms: 30_000 },
  { label: '60 seconds', ms: 60_000 },
];

const WORKLOAD_OPTIONS: { label: string; value: '' | WorkloadType }[] = [
  { label: 'All', value: '' },
  { label: 'Application / Interactive', value: 'APPLICATION_OR_INTERACTIVE' },
  { label: 'Background / Scheduled', value: 'BACKGROUND_OR_SCHEDULED' },
  { label: 'Unknown', value: 'UNKNOWN' },
];

const TOP_N_OPTIONS = [10, 20, 50, 100];

export interface SlowQueryFilterValues {
  database: string;
  startTime: string;
  endTime: string;
  thresholdMs: number;
  workloadType: '' | WorkloadType;
  topN: number;
}

interface Props {
  values: SlowQueryFilterValues;
  databases: string[];
  databasesLoading: boolean;
  onChange: (values: SlowQueryFilterValues) => void;
  onAnalyze: () => void;
  loading: boolean;
}

export function getDefaultFilterValues(databases: string[]): SlowQueryFilterValues {
  const now = new Date();
  const twoHoursAgo = new Date(now.getTime() - 2 * 60 * 60 * 1000);
  return {
    database: databases[0] ?? '',
    startTime: toDateTimeLocal(twoHoursAgo),
    endTime: toDateTimeLocal(now),
    thresholdMs: 30_000,
    workloadType: '',
    topN: 20,
  };
}

export default function SlowQueryFilters({
  values,
  databases,
  databasesLoading,
  onChange,
  onAnalyze,
  loading,
}: Props) {
  const update = (patch: Partial<SlowQueryFilterValues>) => onChange({ ...values, ...patch });

  const rangeInvalid =
    Boolean(values.startTime) &&
    Boolean(values.endTime) &&
    new Date(values.endTime).getTime() <= new Date(values.startTime).getTime();

  const canAnalyze =
    Boolean(values.database) &&
    Boolean(values.startTime) &&
    Boolean(values.endTime) &&
    !rangeInvalid &&
    !loading;

  // Submit on Enter from any field in the panel.
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && canAnalyze) {
      e.preventDefault();
      onAnalyze();
    }
  };

  return (
    <Paper
      variant="outlined"
      sx={{ p: 2, mb: 3 }}
      component="section"
      aria-label="Slow query filters"
      onKeyDown={handleKeyDown}
    >
      <Stack spacing={2}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} flexWrap="wrap" useFlexGap>
          <FormControl size="small" sx={{ minWidth: 200 }}>
            <InputLabel id="slow-db-label">Database</InputLabel>
            <Select
              labelId="slow-db-label"
              id="slow-db"
              value={values.database}
              label="Database"
              onChange={(e: SelectChangeEvent) => update({ database: e.target.value })}
              disabled={databasesLoading}
              startAdornment={
                databasesLoading ? <CircularProgress size={16} sx={{ mr: 1 }} /> : undefined
              }
            >
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

        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={2}
          alignItems={{ xs: 'stretch', sm: 'center' }}
          useFlexGap
          flexWrap="wrap"
        >
          <FormControl size="small" sx={{ minWidth: 160 }}>
            <InputLabel id="slow-threshold-label">Threshold</InputLabel>
            <Select
              labelId="slow-threshold-label"
              id="slow-threshold"
              value={values.thresholdMs}
              label="Threshold"
              onChange={(e: SelectChangeEvent<number>) =>
                update({ thresholdMs: Number(e.target.value) })
              }
            >
              {THRESHOLD_PRESETS.map((p) => (
                <MenuItem key={p.ms} value={p.ms}>
                  {p.label}
                </MenuItem>
              ))}
            </Select>
          </FormControl>

          <FormControl size="small" sx={{ minWidth: 220 }}>
            <InputLabel id="slow-workload-label">Workload Type</InputLabel>
            <Select
              labelId="slow-workload-label"
              id="slow-workload"
              value={values.workloadType}
              label="Workload Type"
              onChange={(e: SelectChangeEvent) =>
                update({ workloadType: e.target.value as '' | WorkloadType })
              }
            >
              {WORKLOAD_OPTIONS.map((o) => (
                <MenuItem key={o.value} value={o.value}>
                  {o.label}
                </MenuItem>
              ))}
            </Select>
          </FormControl>

          <FormControl size="small" sx={{ minWidth: 110 }}>
            <InputLabel id="slow-topn-label">Top N</InputLabel>
            <Select
              labelId="slow-topn-label"
              id="slow-topn"
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

          <Box sx={{ flexGrow: 1 }} />
          <Button
            variant="contained"
            startIcon={
              loading ? <CircularProgress size={18} color="inherit" /> : <SearchIcon />
            }
            onClick={onAnalyze}
            disabled={!canAnalyze}
          >
            {loading ? 'Analyzing…' : 'Analyze History'}
          </Button>
        </Stack>
      </Stack>
    </Paper>
  );
}
