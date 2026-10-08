import { Box, Tooltip, Typography } from '@mui/material';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';
import HourglassEmptyIcon from '@mui/icons-material/HourglassEmpty';
import LoopIcon from '@mui/icons-material/Loop';
import LinkOffIcon from '@mui/icons-material/LinkOff';
import LockPersonIcon from '@mui/icons-material/LockPerson';
import { MetricCard } from '../common';
import { formatInteger } from '../../utils/formatters';
import type { SqlErrorSummaryResponse } from '../../types/sqlErrors';

interface Props {
  summary: SqlErrorSummaryResponse;
}

export default function SqlErrorSummaryCards({ summary }: Props) {
  const cards = [
    {
      title: 'Total Errors',
      value: formatInteger(summary.total_errors),
      icon: <ErrorOutlineIcon />,
      color: undefined,
    },
    {
      title: 'Timeouts',
      value: formatInteger(summary.timeout_count),
      icon: <HourglassEmptyIcon />,
      color: 'error.main',
    },
    {
      title: 'Deadlocks',
      value: formatInteger(summary.deadlock_count),
      icon: <LoopIcon />,
      color: 'error.main',
    },
    {
      title: 'Connection Errors',
      value: formatInteger(summary.connection_error_count),
      icon: <LinkOffIcon />,
      color: 'warning.main',
    },
    {
      title: 'Permission Errors',
      value: formatInteger(summary.permission_error_count),
      icon: <LockPersonIcon />,
      color: 'warning.main',
    },
  ];

  return (
    <Box sx={{ mb: 3 }}>
      <Box
        sx={{
          display: 'grid',
          gap: 2,
          gridTemplateColumns: {
            xs: '1fr',
            sm: 'repeat(2, 1fr)',
            md: 'repeat(3, 1fr)',
            lg: 'repeat(5, 1fr)',
          },
        }}
      >
        {cards.map((card) => (
          <MetricCard
            key={card.title}
            title={card.title}
            value={card.value}
            icon={card.icon}
            color={card.color}
          />
        ))}
      </Box>

      {/*
        The timeout card counts both kinds, so the split is stated immediately
        beneath it. A single "Timeouts" number that quietly included inferred
        candidates would overstate what was actually observed.
      */}
      {summary.timeout_count > 0 && (
        <Tooltip
          arrow
          title={
            'Confirmed timeouts were recorded by an application or telemetry source. '
            + 'Candidates were inferred from SQLPulse performance evidence and were never '
            + 'observed by anything watching the client.'
          }
        >
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
            Of {formatInteger(summary.timeout_count)} timeout
            {summary.timeout_count === 1 ? '' : 's'}:{' '}
            {formatInteger(summary.confirmed_timeout_count)} confirmed,{' '}
            {formatInteger(summary.timeout_candidate_count)} unconfirmed candidate
            {summary.timeout_candidate_count === 1 ? '' : 's'}.
          </Typography>
        </Tooltip>
      )}
    </Box>
  );
}
