import {
  FormControl,
  InputLabel,
  Select,
  MenuItem,
  CircularProgress,
  Typography,
  Box,
} from '@mui/material';
import type { SelectChangeEvent } from '@mui/material';
import { useAnalysis } from '../../hooks/useAnalysis';

export default function DatabaseSelector() {
  const {
    selectedDatabase,
    databases,
    databasesLoading,
    databasesError,
    analysisScope,
    setDatabase,
  } = useAnalysis();

  const disabled = analysisScope === 'server';

  const handleChange = (e: SelectChangeEvent) => {
    setDatabase(e.target.value || null);
  };

  if (databasesLoading) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 200 }}>
        <CircularProgress size={18} />
        <Typography variant="body2" color="text.secondary">
          Loading databases…
        </Typography>
      </Box>
    );
  }

  if (databasesError) {
    return (
      <Typography variant="body2" color="error" sx={{ minWidth: 200 }}>
        {databasesError}
      </Typography>
    );
  }

  if (databases.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary" sx={{ minWidth: 200 }}>
        No databases available
      </Typography>
    );
  }

  return (
    <FormControl size="small" sx={{ minWidth: 200 }} disabled={disabled}>
      <InputLabel id="db-selector-label">Database</InputLabel>
      <Select
        labelId="db-selector-label"
        value={selectedDatabase ?? ''}
        label="Database"
        onChange={handleChange}
      >
        {databases.map((db) => (
          <MenuItem key={db} value={db}>
            {db}
          </MenuItem>
        ))}
      </Select>
    </FormControl>
  );
}
