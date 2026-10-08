import {
  Paper,
  Typography,
  Box,
  Divider,
  Alert,
  List,
  ListItem,
  ListItemIcon,
  ListItemText,
  Chip,
  Tooltip,
} from '@mui/material';
import ReportProblemIcon from '@mui/icons-material/ReportProblem';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';
import ArrowForwardIcon from '@mui/icons-material/ArrowForward';
import { SeverityBadge, ConfidenceBadge } from '../../components/common';
import { truncateSql } from '../../utils/formatters';
import EvidenceSection from './EvidenceSection';
import TechnicalDetails from './TechnicalDetails';
import type { InvestigationResponse } from '../../types/agent';

interface Props {
  result: InvestigationResponse;
}

function BulletList({ items, icon }: { items: string[]; icon: React.ReactNode }) {
  if (items.length === 0) return null;
  return (
    <List dense disablePadding>
      {items.map((item, i) => (
        <ListItem key={i} disableGutters sx={{ py: 0.25 }}>
          <ListItemIcon sx={{ minWidth: 28 }}>{icon}</ListItemIcon>
          <ListItemText primary={item} primaryTypographyProps={{ variant: 'body2' }} />
        </ListItem>
      ))}
    </List>
  );
}

export default function InvestigationResult({ result }: Props) {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2.5 }}>
      {/* Summary */}
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" fontWeight={700} gutterBottom>
          Summary
        </Typography>
        <Typography variant="body1" whiteSpace="pre-wrap" sx={{ mb: 2 }}>
          {result.answer}
        </Typography>

        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2, alignItems: 'center' }}>
          <SeverityBadge severity={result.severity} />
          <ConfidenceBadge confidence={result.confidence} />
          {result.affected_database && (
            <Chip label={`DB: ${result.affected_database}`} size="small" variant="outlined" />
          )}
          {result.affected_query && (
            <Tooltip title={result.affected_query}>
              <Chip
                label={`Query: ${truncateSql(result.affected_query, 60)}`}
                size="small"
                variant="outlined"
                sx={{ fontFamily: 'monospace', fontSize: 12, maxWidth: '100%' }}
              />
            </Tooltip>
          )}
        </Box>
      </Paper>

      {/* Evidence */}
      <EvidenceSection evidence={result.evidence} interpretation={result.likely_cause} />

      {/* Historical Comparison */}
      {result.historical_comparison && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Typography variant="subtitle1" fontWeight={700} gutterBottom>
            Historical Comparison
          </Typography>
          <Typography variant="body2" whiteSpace="pre-wrap">
            {result.historical_comparison}
          </Typography>
        </Paper>
      )}

      {/* What Is Known / What Is Uncertain */}
      {(result.what_is_known.length > 0 || result.what_is_uncertain.length > 0) && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          {result.what_is_known.length > 0 && (
            <>
              <Typography variant="subtitle2" fontWeight={700} gutterBottom>
                What Is Known
              </Typography>
              <BulletList
                items={result.what_is_known}
                icon={<CheckCircleOutlineIcon sx={{ fontSize: 16, color: 'success.main' }} />}
              />
            </>
          )}
          {result.what_is_known.length > 0 && result.what_is_uncertain.length > 0 && (
            <Divider sx={{ my: 1.5 }} />
          )}
          {result.what_is_uncertain.length > 0 && (
            <>
              <Typography variant="subtitle2" fontWeight={700} gutterBottom>
                What Is Uncertain
              </Typography>
              <BulletList
                items={result.what_is_uncertain}
                icon={<ErrorOutlineIcon sx={{ fontSize: 16, color: 'warning.main' }} />}
              />
            </>
          )}
        </Paper>
      )}

      {/* Recommended Investigation */}
      {result.recommended_investigation.length > 0 && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Typography variant="subtitle1" fontWeight={700} gutterBottom>
            Recommended Investigation
          </Typography>
          <BulletList
            items={result.recommended_investigation}
            icon={<ArrowForwardIcon sx={{ fontSize: 16, color: 'primary.main' }} />}
          />
        </Paper>
      )}

      {/* Human Review */}
      {result.human_review_required && (
        <Alert severity="warning" icon={<ReportProblemIcon />}>
          <strong>Human Review Required</strong> — This investigation should be reviewed by a DBA
          before taking any action.
        </Alert>
      )}

      {/* Agent Action */}
      <Alert
        severity="info"
        variant="outlined"
        sx={{ fontWeight: 500 }}
      >
        <strong>Agent Action:</strong> {result.agent_action}
      </Alert>

      {/* Technical Details (collapsible) */}
      <TechnicalDetails result={result} />
    </Box>
  );
}
