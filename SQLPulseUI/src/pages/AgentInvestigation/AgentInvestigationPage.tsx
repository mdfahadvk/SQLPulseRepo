import { useState, useCallback } from 'react';
import {
  Box,
  TextField,
  Button,
  Paper,
  Typography,
  Chip,
  CircularProgress,
  Alert,
} from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import { PageHeader } from '../../components/common';
import InvestigationResult from '../../components/investigation/InvestigationResult';
import { investigate } from '../../api/agentApi';
import type { InvestigationResponse } from '../../types/agent';

const SUGGESTED_QUESTIONS = [
  'Why is Merchandising slow right now?',
  'Which queries were slow yesterday?',
  'Which query should we investigate first?',
  'Were there any timeout candidates?',
  'What are the biggest performance issues on the server?',
];

/**
 * Map backend failures to safe, user-facing text.
 *
 * The backend already sanitises its error details, but this is the last line
 * of defence: anything unrecognised collapses to a generic message so raw
 * exceptions, connection strings or credentials can never surface in the UI.
 */
function friendlyError(err: unknown): string {
  const msg = err instanceof Error ? err.message : String(err);
  const lower = msg.toLowerCase();

  if (lower.includes('not configured') || lower.includes('llm_api_key'))
    return 'AI agent is not configured. An administrator needs to set the LLM API key on the server.';
  if (lower.includes('dependency not installed'))
    return 'AI agent dependency is missing on the server. Contact your administrator.';
  if (lower.includes('token limit') || lower.includes('too large'))
    return 'The question gathered more evidence than the AI provider allows. Try a narrower question, such as a single database or a shorter time range.';
  if (lower.includes('429') || lower.includes('rate limit'))
    return 'The AI provider is rate-limiting requests. Please wait a moment and try again.';
  if (lower.includes('timeout') || lower.includes('timed out'))
    return 'The investigation timed out. Try a more specific question or try again later.';
  if (lower.includes('query store'))
    return 'Query Store is not available for the target database, so some analysis was unavailable.';
  if (lower.includes('tool') && lower.includes('fail'))
    return 'One of the analysis tools failed while gathering evidence. Please try again.';
  if (lower.includes('network') || lower.includes('econnaborted'))
    return 'Could not reach the SQLPulse backend. Check that the API is running.';
  if (lower.includes('503') || lower.includes('unavailable'))
    return 'The AI service is temporarily unavailable. Please try again shortly.';

  // Only surface short, known-safe backend messages verbatim.
  const looksSafe = msg.length <= 160 && !/[{}\\]|::|password|pwd=|server=/i.test(msg);
  return looksSafe ? msg : 'The investigation could not be completed. Please try again.';
}

export default function AgentInvestigationPage() {
  const [question, setQuestion] = useState('');
  const [result, setResult] = useState<InvestigationResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleInvestigate = useCallback(async () => {
    const q = question.trim();
    if (!q) return;
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const data = await investigate({ question: q });
      setResult(data);
    } catch (e) {
      setError(friendlyError(e));
    } finally {
      setLoading(false);
    }
  }, [question]);

  return (
    <>
      <PageHeader
        title="AI Investigation"
        subtitle="Ask the AI agent to investigate SQL Server performance issues"
      />

      {/* Question Input */}
      <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
        <TextField
          fullWidth
          multiline
          minRows={3}
          maxRows={8}
          placeholder="Ask SQLPulse about SQL Server performance..."
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          disabled={loading}
          sx={{ mb: 2 }}
        />

        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap' }}>
          <Button
            variant="contained"
            startIcon={loading ? <CircularProgress size={18} color="inherit" /> : <SearchIcon />}
            onClick={handleInvestigate}
            disabled={loading || !question.trim()}
          >
            Investigate
          </Button>

          {loading && (
            <Typography variant="body2" color="text.secondary" sx={{ fontStyle: 'italic' }}>
              SQLPulse is investigating…
            </Typography>
          )}
        </Box>
      </Paper>

      {/* Suggested Questions */}
      {!result && !loading && (
        <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
          <Typography variant="subtitle2" color="text.secondary" gutterBottom>
            Suggested questions
          </Typography>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
            {SUGGESTED_QUESTIONS.map((q) => (
              <Chip
                key={q}
                label={q}
                variant="outlined"
                clickable
                onClick={() => setQuestion(q)}
              />
            ))}
          </Box>
        </Paper>
      )}

      {/* Error */}
      {error && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {error}
        </Alert>
      )}

      {/* Result */}
      {result && !loading && <InvestigationResult result={result} />}
    </>
  );
}
