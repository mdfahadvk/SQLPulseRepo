import { useState } from 'react';
import { Box, Typography, Tooltip, IconButton, Collapse, Dialog, DialogTitle, DialogContent, Divider } from '@mui/material';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import CheckIcon from '@mui/icons-material/Check';
import { truncateSql, normalizeSql } from '../../utils/formatters';

interface SqlTextProps {
  sql: string | null | undefined;
  /** Characters shown before truncation in inline mode. */
  maxLength?: number;
  /** Max width of the inline preview. */
  maxWidth?: number | string;
  /** Render as a full scrollable block instead of a single truncated line. */
  block?: boolean;
  /** Show a copy-to-clipboard button. */
  copyable?: boolean;
}

const MONO_SX = {
  fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
  fontSize: 12,
} as const;

/**
 * Renders SQL text safely and consistently.
 *
 * Long statements are collapsed to a single line with the full text available
 * via tooltip, keeping table rows a uniform height. React escapes the content,
 * so query text is never interpreted as markup.
 */
export default function SqlText({
  sql,
  maxLength = 120,
  maxWidth = 320,
  block = false,
  copyable = false,
}: SqlTextProps) {
  const [copied, setCopied] = useState(false);
  const [detailsOpen, setDetailsOpen] = useState(false);
  const full = normalizeSql(sql);

  if (!full) {
    return (
      <Typography variant="body2" color="text.disabled" component="span">
        —
      </Typography>
    );
  }

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(sql ?? '');
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard permission denied — fail silently rather than interrupting.
    }
  };

  const copyButton = copyable ? (
    <Tooltip title={copied ? 'Copied' : 'Copy SQL'}>
      <IconButton
        size="small"
        onClick={handleCopy}
        aria-label={copied ? 'SQL copied to clipboard' : 'Copy SQL to clipboard'}
      >
        {copied ? (
          <CheckIcon sx={{ fontSize: 15, color: 'success.main' }} />
        ) : (
          <ContentCopyIcon sx={{ fontSize: 15 }} />
        )}
      </IconButton>
    </Tooltip>
  ) : null;

  if (block) {
    return (
      <Box>
        {copyButton && (
          <Box sx={{ display: 'flex', justifyContent: 'flex-end', mb: 0.5 }}>{copyButton}</Box>
        )}
        <Box
          component="pre"
          tabIndex={0}
          sx={{
            ...MONO_SX,
            m: 0,
            p: 1.5,
            borderRadius: 1,
            bgcolor: 'grey.100',
            border: '1px solid',
            borderColor: 'divider',
            maxHeight: 260,
            overflow: 'auto',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
          }}
        >
          {sql}
        </Box>
      </Box>
    );
  }

  const truncated = truncateSql(full, maxLength);
  const isTruncated = truncated !== full;

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
      <Tooltip
        title={isTruncated ? full : ''}
        arrow
        placement="top-start"
        slotProps={{ tooltip: { sx: { ...MONO_SX, maxWidth: 560 } } }}
        disableHoverListener={!isTruncated}
      >
        <Typography
          variant="body2"
          noWrap
          component="span"
          sx={{ ...MONO_SX, maxWidth, display: 'block', cursor: 'pointer' }}
          onClick={() => setDetailsOpen(true)}
          title="Click to view SQL details"
        >
          {truncated}
        </Typography>
      </Tooltip>
      {copyButton}
      <Dialog open={detailsOpen} onClose={() => setDetailsOpen(false)} fullWidth maxWidth="md">
        <DialogTitle>SQL Details</DialogTitle>
        <DialogContent>
          <Box component="pre" sx={{ ...MONO_SX, whiteSpace: 'pre-wrap', wordBreak: 'break-word', m: 0 }}>
            {sql}
          </Box>
          <ParameterDeclarations sql={sql} />
        </DialogContent>
      </Dialog>
    </Box>
  );
}

function ParameterDeclarations({ sql }: { sql: string | null | undefined }) {
  const declarationBlock = sql?.match(/^\s*\(([\s\S]*?)\)\s*(?:SELECT|EXEC|WITH|INSERT|UPDATE|DELETE)\b/i)?.[1];
  if (!declarationBlock) return null;

  const parameters = [...declarationBlock.matchAll(/(@[A-Za-z_][A-Za-z0-9_]*)\s+([^,\s]+(?:\s*\([^)]*\))?)/gi)]
    .map((match) => `${match[1]} — ${match[2]}`);
  if (parameters.length === 0) return null;

  return (
    <>
      <Divider sx={{ my: 2 }} />
      <Typography variant="subtitle2">Parameter declarations</Typography>
      {parameters.map((parameter) => (
        <Typography key={parameter} variant="body2" sx={{ fontFamily: 'monospace', mt: 0.5 }}>
          {parameter}
        </Typography>
      ))}
      <Typography variant="caption" color="text.secondary" display="block" sx={{ mt: 1 }}>
        Runtime values were not retained for this historical query.
      </Typography>
    </>
  );
}

/** Expandable SQL block for detail drawers. */
export function ExpandableSqlText({ sql, previewLength = 240 }: { sql: string | null | undefined; previewLength?: number }) {
  const [expanded, setExpanded] = useState(false);
  const full = normalizeSql(sql);

  if (!full) {
    return (
      <Typography variant="body2" color="text.disabled">
        No SQL text available.
      </Typography>
    );
  }

  const needsToggle = full.length > previewLength;

  return (
    <Box>
      {!expanded && (
        <Typography variant="body2" sx={{ ...MONO_SX, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
          {truncateSql(full, previewLength)}
        </Typography>
      )}
      <Collapse in={expanded} unmountOnExit>
        <SqlText sql={sql} block copyable />
      </Collapse>
      {needsToggle && (
        <Typography
          component="button"
          type="button"
          onClick={() => setExpanded((e) => !e)}
          aria-expanded={expanded}
          variant="caption"
          sx={{
            mt: 0.5,
            background: 'none',
            border: 'none',
            p: 0,
            cursor: 'pointer',
            color: 'primary.main',
            textDecoration: 'underline',
            font: 'inherit',
            fontSize: 12,
          }}
        >
          {expanded ? 'Show less' : 'Show full SQL'}
        </Typography>
      )}
    </Box>
  );
}
