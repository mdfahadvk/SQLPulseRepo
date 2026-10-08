import { useState } from 'react';
import {
  Drawer,
  Box,
  Typography,
  IconButton,
  Divider,
  Chip,
  List,
  ListItem,
  ListItemIcon,
  ListItemText,
  Alert,
} from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import GavelIcon from '@mui/icons-material/Gavel';
import type { PerformanceFinding } from '../../types/performance';
import { SeverityBadge, ConfidenceBadge } from '../common';
import { formatTimestamp } from '../../utils/formatters';

interface FindingDetailDrawerProps {
  finding: PerformanceFinding | null;
  onClose: () => void;
}

function formatCategory(category: string): string {
  return category
    .split('_')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}

export default function FindingDetailDrawer({ finding, onClose }: FindingDetailDrawerProps) {
  return (
    <Drawer
      anchor="right"
      open={finding !== null}
      onClose={onClose}
      PaperProps={{ sx: { width: { xs: '100%', sm: 520 }, p: 3 } }}
    >
      {finding && <FindingContent finding={finding} onClose={onClose} />}
    </Drawer>
  );
}

function FindingContent({ finding, onClose }: { finding: PerformanceFinding; onClose: () => void }) {
  return (
    <Box>
      {/* Header */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 2 }}>
        <Typography variant="h6" fontWeight={600}>
          Finding Detail
        </Typography>
        <IconButton onClick={onClose} size="small">
          <CloseIcon />
        </IconButton>
      </Box>

      {/* Category & Badges */}
      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, mb: 2 }}>
        <Chip label={formatCategory(finding.category)} size="small" color="primary" />
        <SeverityBadge severity={finding.severity} />
        <ConfidenceBadge confidence={finding.confidence} />
        {finding.human_review_required && (
          <Chip label="Human Review Required" size="small" color="warning" variant="outlined" />
        )}
      </Box>

      <Divider sx={{ mb: 2 }} />

      {/* Summary */}
      <Section title="Summary">
        <Typography variant="body2">{finding.summary}</Typography>
      </Section>

      {/* Database */}
      <Section title="Database">
        <Typography variant="body2">{finding.database ?? 'Not resolved'}</Typography>
      </Section>

      {/* Evidence */}
      {finding.evidence.length > 0 && (
        <Section title="Evidence">
          <List dense disablePadding>
            {finding.evidence.map((e, i) => (
              <ListItem key={i} disableGutters sx={{ py: 0.25 }}>
                <ListItemText primary={e} primaryTypographyProps={{ variant: 'body2' }} />
              </ListItem>
            ))}
          </List>
        </Section>
      )}

      {/* Likely Cause */}
      <Section title="Likely Cause">
        <Typography variant="body2">{finding.likely_cause}</Typography>
      </Section>

      {/* Recommendation */}
      <Section title="Recommendation">
        <Typography variant="body2">{finding.recommendation}</Typography>
      </Section>

      {/* Validation Steps */}
      {finding.validation_steps.length > 0 && (
        <Section title="Validation Steps">
          <List dense disablePadding>
            {finding.validation_steps.map((step, i) => (
              <ListItem key={i} disableGutters sx={{ py: 0.25 }}>
                <ListItemIcon sx={{ minWidth: 28 }}>
                  <GavelIcon fontSize="small" color="action" />
                </ListItemIcon>
                <ListItemText primary={step} primaryTypographyProps={{ variant: 'body2' }} />
              </ListItem>
            ))}
          </List>
        </Section>
      )}

      {/* Timestamps */}
      <Box sx={{ display: 'flex', gap: 4, mt: 2, mb: 2 }}>
        <Box>
          <Typography variant="caption" color="text.secondary">First Observed</Typography>
          <Typography variant="body2">{formatTimestamp(finding.first_observed)}</Typography>
        </Box>
        <Box>
          <Typography variant="caption" color="text.secondary">Last Observed</Typography>
          <Typography variant="body2">{formatTimestamp(finding.last_observed)}</Typography>
        </Box>
      </Box>

      <Divider sx={{ mb: 2 }} />

      {/* Advisory notice */}
      <Alert severity="info" icon={<GavelIcon fontSize="small" />} sx={{ mt: 1 }}>
        SQLPulse is advisory only. No database remediation was performed.
      </Alert>
    </Box>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <Box sx={{ mb: 2 }}>
      <Typography variant="subtitle2" color="text.secondary" gutterBottom>
        {title}
      </Typography>
      {children}
    </Box>
  );
}

// Hook for managing drawer state
export function useFindingDrawer() {
  const [selectedFinding, setSelectedFinding] = useState<PerformanceFinding | null>(null);
  const open = (f: PerformanceFinding) => setSelectedFinding(f);
  const close = () => setSelectedFinding(null);
  return { selectedFinding, open, close };
}
