import { useState, useCallback } from 'react';
import { Box, Stack, Button, Card, CardContent, Typography, Alert } from '@mui/material';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import CheckCircleIcon from '@mui/icons-material/CheckCircle';
import ErrorIcon from '@mui/icons-material/Error';
import ReportProblemIcon from '@mui/icons-material/ReportProblem';
import InfoIcon from '@mui/icons-material/Info';
import AssessmentIcon from '@mui/icons-material/Assessment';
import {
  PageHeader,
  MetricCard,
  LoadingState,
  ErrorAlert,
  HealthBadge,
  AnalysisScopeToolbar,
} from '../../components/common';
import FindingsTable from '../../components/dashboard/FindingsTable';
import AffectedDatabasesTable from '../../components/dashboard/AffectedDatabasesTable';
import FindingDetailDrawer, { useFindingDrawer } from '../../components/dashboard/FindingDetailDrawer';
import { useAnalysis } from '../../hooks/useAnalysis';
import { analyzePerformance } from '../../api/performanceApi';
import { formatTimestamp } from '../../utils/formatters';
import type { AnalysisResult } from '../../types/performance';

export default function DashboardPage() {
  const { analysisScope, selectedDatabase, setScope, setDatabase } = useAnalysis();
  const drawer = useFindingDrawer();

  const [result, setResult] = useState<AnalysisResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const canAnalyze = analysisScope === 'server' || (analysisScope === 'database' && !!selectedDatabase);

  const handleAnalyze = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const params =
        analysisScope === 'database' && selectedDatabase
          ? { scope: 'database' as const, database: selectedDatabase }
          : { scope: 'server' as const };
      const data = await analyzePerformance(params);
      setResult(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Analysis failed');
    } finally {
      setLoading(false);
    }
  }, [analysisScope, selectedDatabase]);

  const handleDatabaseDrilldown = useCallback(
    (database: string) => {
      setDatabase(database);
      setScope('database');
      // Trigger analysis for the selected database
      setLoading(true);
      setError(null);
      analyzePerformance({ scope: 'database', database })
        .then(setResult)
        .catch((e) => setError(e instanceof Error ? e.message : 'Analysis failed'))
        .finally(() => setLoading(false));
    },
    [setDatabase, setScope],
  );

  return (
    <>
      <PageHeader
        title="Dashboard"
        subtitle="SQLPulse — SQL Server performance analysis"
      />

      {/* Toolbar + Analyze button */}
      <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 2, flexWrap: 'wrap', mb: 0 }}>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <AnalysisScopeToolbar />
        </Box>
        <Button
          variant="contained"
          startIcon={<PlayArrowIcon />}
          onClick={handleAnalyze}
          disabled={loading || !canAnalyze}
          sx={{ mt: 0.5, whiteSpace: 'nowrap' }}
        >
          {loading ? 'Analyzing…' : 'Analyze Now'}
        </Button>
      </Box>

      {/* Validation: database required */}
      {analysisScope === 'database' && !selectedDatabase && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Select a database to analyze, or switch scope to Entire Server.
        </Alert>
      )}

      {/* Loading */}
      {loading && <LoadingState message="Running performance analysis…" />}

      {/* Error */}
      {error && <ErrorAlert message={error} title="Analysis Error" />}

      {/* Not analyzed yet */}
      {!result && !loading && !error && <NotAnalyzedState />}

      {/* Results */}
      {result && !loading && (
        <AnalysisResults
          result={result}
          onFindingClick={drawer.open}
          onDatabaseClick={handleDatabaseDrilldown}
        />
      )}

      {/* Finding drawer */}
      <FindingDetailDrawer finding={drawer.selectedFinding} onClose={drawer.close} />
    </>
  );
}

// ─── Not-analyzed placeholder ───

function NotAnalyzedState() {
  return (
    <Card variant="outlined" sx={{ textAlign: 'center', py: 6 }}>
      <CardContent>
        <AssessmentIcon sx={{ fontSize: 56, color: 'text.disabled', mb: 1 }} />
        <Typography variant="h6" color="text.secondary">
          No analysis performed yet
        </Typography>
        <Typography variant="body2" color="text.secondary">
          Click <strong>Analyze Now</strong> to run a performance analysis.
        </Typography>
      </CardContent>
    </Card>
  );
}

// ─── Full results view ───

interface AnalysisResultsProps {
  result: AnalysisResult;
  onFindingClick: (f: import('../../types/performance').PerformanceFinding) => void;
  onDatabaseClick: (db: string) => void;
}

function AnalysisResults({ result, onFindingClick, onDatabaseClick }: AnalysisResultsProps) {
  const healthFindings = result.current_health?.findings ?? [];
  const hasCollectorIssues = result.findings.some(
    (f) => f.evidence.some((e) => e.toLowerCase().includes('permission') || e.toLowerCase().includes('collector'))
  );

  return (
    <>
      {/* Analyzed-at timestamp */}
      <Typography variant="caption" color="text.secondary" sx={{ mb: 2, display: 'block' }}>
        Analyzed at {formatTimestamp(result.analyzed_at)} — {result.scope} scope
        {result.database && ` — ${result.database}`}
      </Typography>

      {/* Current Health */}
      <HealthCard status={result.overall_status} />

      {/* Partial collector warning */}
      {hasCollectorIssues && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          Some collectors encountered permission issues. Results may be partial.
        </Alert>
      )}

      {/* Summary cards */}
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ mb: 3 }}>
        <Box sx={{ flex: 1 }}>
          <MetricCard
            title="Total Findings"
            value={result.total_findings}
            icon={<AssessmentIcon />}
          />
        </Box>
        <Box sx={{ flex: 1 }}>
          <MetricCard
            title="Critical"
            value={result.critical_count}
            icon={<ErrorIcon />}
            color={result.critical_count > 0 ? 'error.main' : 'text.secondary'}
          />
        </Box>
        <Box sx={{ flex: 1 }}>
          <MetricCard
            title="High"
            value={result.high_count}
            icon={<ReportProblemIcon />}
            color={result.high_count > 0 ? 'error.main' : 'text.secondary'}
          />
        </Box>
        <Box sx={{ flex: 1 }}>
          <MetricCard
            title="Medium"
            value={result.medium_count}
            icon={<ReportProblemIcon />}
            color={result.medium_count > 0 ? 'warning.main' : 'text.secondary'}
          />
        </Box>
        <Box sx={{ flex: 1 }}>
          <MetricCard
            title="Low"
            value={result.low_count}
            icon={<InfoIcon />}
            color="text.secondary"
          />
        </Box>
        {result.scope === 'database' && (
          <Box sx={{ flex: 1 }}>
            <MetricCard
              title="Optimizations"
              value={result.optimization_opportunities.length}
              icon={<AssessmentIcon />}
              color="secondary.main"
            />
          </Box>
        )}
      </Stack>

      {/* Current Health Findings */}
      {healthFindings.length > 0 && (
        <FindingsTable
          title="Current Health Findings"
          findings={healthFindings}
          onFindingClick={onFindingClick}
          emptyMessage="Server is healthy — no active issues detected."
        />
      )}

      {/* Affected Databases (server scope only) */}
      {result.scope === 'server' && (
        <AffectedDatabasesTable
          databases={result.affected_databases}
          onDatabaseClick={onDatabaseClick}
        />
      )}

      {/* All Findings */}
      <FindingsTable
        title="All Findings"
        findings={result.findings}
        onFindingClick={onFindingClick}
        emptyMessage="No performance issues detected — everything looks healthy!"
      />

      {/* Optimization Opportunities */}
      {result.optimization_opportunities.length > 0 && (
        <FindingsTable
          title="Optimization Opportunities"
          findings={result.optimization_opportunities}
          onFindingClick={onFindingClick}
          showLastObserved={false}
          emptyMessage="No optimization opportunities identified."
        />
      )}
    </>
  );
}

// ─── Health card ───

function HealthCard({ status }: { status: string }) {
  const colorMap: Record<string, string> = {
    HEALTHY: 'success.main',
    WARNING: 'warning.main',
    DEGRADED: 'error.main',
    CRITICAL: 'error.main',
    UNKNOWN: 'text.secondary',
  };

  const iconMap: Record<string, React.ReactNode> = {
    HEALTHY: <CheckCircleIcon sx={{ fontSize: 40 }} />,
    WARNING: <ReportProblemIcon sx={{ fontSize: 40 }} />,
    DEGRADED: <ErrorIcon sx={{ fontSize: 40 }} />,
    CRITICAL: <ErrorIcon sx={{ fontSize: 40 }} />,
    UNKNOWN: <InfoIcon sx={{ fontSize: 40 }} />,
  };

  return (
    <Card variant="outlined" sx={{ mb: 3 }}>
      <CardContent sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
        <Box sx={{ color: colorMap[status] ?? 'text.secondary' }}>
          {iconMap[status] ?? iconMap['UNKNOWN']}
        </Box>
        <Box>
          <Typography variant="subtitle2" color="text.secondary">
            Current Health
          </Typography>
          <Box sx={{ mt: 0.5 }}>
            <HealthBadge status={status} />
          </Box>
        </Box>
      </CardContent>
    </Card>
  );
}
