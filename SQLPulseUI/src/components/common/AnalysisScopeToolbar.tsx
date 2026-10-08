import {
  Paper,
  Box,
  Typography,
  ToggleButtonGroup,
  ToggleButton,
  Chip,
  Skeleton,
} from '@mui/material';
import DnsIcon from '@mui/icons-material/Dns';
import DatabaseSelector from './DatabaseSelector';
import { useAnalysis } from '../../hooks/useAnalysis';
import type { AnalysisScope } from '../../hooks/useAnalysis';

export default function AnalysisScopeToolbar() {
  const { selectedServer, analysisScope, setScope } = useAnalysis();

  const handleScopeChange = (_: React.MouseEvent<HTMLElement>, value: string | null) => {
    if (value === 'server' || value === 'database') {
      setScope(value as AnalysisScope);
    }
  };

  return (
    <Paper
      variant="outlined"
      sx={{
        display: 'flex',
        alignItems: 'center',
        flexWrap: 'wrap',
        gap: 2,
        px: 2,
        py: 1.5,
        mb: 3,
      }}
    >
      {/* Server */}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <DnsIcon fontSize="small" color="primary" />
        <Typography variant="body2" color="text.secondary">
          Server
        </Typography>
        {selectedServer ? (
          <Chip label={selectedServer} size="small" variant="outlined" />
        ) : (
          <Skeleton width={120} height={24} />
        )}
      </Box>

      {/* Divider */}
      <Box sx={{ borderLeft: '1px solid', borderColor: 'divider', height: 28 }} />

      {/* Scope */}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <Typography variant="body2" color="text.secondary">
          Scope
        </Typography>
        <ToggleButtonGroup
          value={analysisScope}
          exclusive
          onChange={handleScopeChange}
          size="small"
        >
          <ToggleButton value="server">Entire Server</ToggleButton>
          <ToggleButton value="database">Specific Database</ToggleButton>
        </ToggleButtonGroup>
      </Box>

      {/* Divider */}
      <Box sx={{ borderLeft: '1px solid', borderColor: 'divider', height: 28 }} />

      {/* Database */}
      <DatabaseSelector />
    </Paper>
  );
}
