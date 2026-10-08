import { Paper, Typography, Box, List, ListItem, ListItemIcon, ListItemText } from '@mui/material';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';
import HelpOutlineIcon from '@mui/icons-material/HelpOutline';

interface Props {
  evidence: string[];
  interpretation?: string | null;
}

export default function EvidenceSection({ evidence, interpretation }: Props) {
  if (evidence.length === 0 && !interpretation) return null;

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      {evidence.length > 0 && (
        <Paper
          variant="outlined"
          sx={{
            p: 2,
            borderLeft: 4,
            borderColor: 'info.main',
            bgcolor: 'action.hover',
          }}
        >
          <Typography variant="subtitle2" fontWeight={700} gutterBottom>
            Evidence
          </Typography>
          <List dense disablePadding>
            {evidence.map((item, i) => (
              <ListItem key={i} disableGutters sx={{ py: 0.25 }}>
                <ListItemIcon sx={{ minWidth: 28 }}>
                  <CheckCircleOutlineIcon sx={{ fontSize: 16, color: 'info.main' }} />
                </ListItemIcon>
                <ListItemText
                  primary={item}
                  primaryTypographyProps={{ variant: 'body2', fontFamily: 'monospace', fontSize: 13 }}
                />
              </ListItem>
            ))}
          </List>
        </Paper>
      )}

      {interpretation && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, mb: 1 }}>
            <HelpOutlineIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
            <Typography variant="subtitle2" fontWeight={700}>
              Interpretation
            </Typography>
          </Box>
          <Typography variant="body2" whiteSpace="pre-wrap">
            {interpretation}
          </Typography>
        </Paper>
      )}
    </Box>
  );
}
