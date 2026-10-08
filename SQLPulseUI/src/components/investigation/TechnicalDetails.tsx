import { useState } from 'react';
import {
  Paper,
  Typography,
  Box,
  Collapse,
  IconButton,
  Chip,
} from '@mui/material';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import ExpandLessIcon from '@mui/icons-material/ExpandLess';
import { formatTimestamp } from '../../utils/formatters';
import type { InvestigationResponse } from '../../types/agent';

interface Props {
  result: InvestigationResponse;
}

export default function TechnicalDetails({ result }: Props) {
  const [open, setOpen] = useState(false);

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Box
        sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', cursor: 'pointer' }}
        onClick={() => setOpen((o) => !o)}
      >
        <Typography variant="subtitle2" fontWeight={600}>
          Technical Details
        </Typography>
        <IconButton size="small">
          {open ? <ExpandLessIcon /> : <ExpandMoreIcon />}
        </IconButton>
      </Box>

      <Collapse in={open}>
        <Box sx={{ mt: 1.5, display: 'flex', flexDirection: 'column', gap: 1 }}>
          <Detail label="Investigation ID" value={result.investigation_id} mono />
          <Detail label="Timestamp" value={formatTimestamp(result.timestamp)} />
          <Detail label="Duration" value={`${result.duration_seconds.toFixed(1)}s`} />
          <Detail label="Tool Call Count" value={String(result.tool_call_count)} />

          {result.tools_used.length > 0 && (
            <Box>
              <Typography variant="caption" color="text.secondary">
                Tools Used
              </Typography>
              <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.5, mt: 0.5 }}>
                {result.tools_used.map((t) => (
                  <Chip key={t} label={t} size="small" variant="outlined" />
                ))}
              </Box>
            </Box>
          )}
        </Box>
      </Collapse>
    </Paper>
  );
}

function Detail({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary">
        {label}
      </Typography>
      <Typography variant="body2" fontFamily={mono ? 'monospace' : undefined} fontSize={mono ? 13 : undefined}>
        {value}
      </Typography>
    </Box>
  );
}
