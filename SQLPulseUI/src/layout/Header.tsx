import { AppBar, Toolbar, Typography, Box, Chip, IconButton, Tooltip } from '@mui/material';
import MenuIcon from '@mui/icons-material/Menu';
import { useHealth } from '../hooks/useQueries';
import { DRAWER_WIDTH } from './Sidebar';

interface HeaderProps {
  onMenuClick: () => void;
}

export default function Header({ onMenuClick }: HeaderProps) {
  const { data, isError, isLoading } = useHealth();

  const backendOnline = !isError && data != null;
  const statusLabel = isLoading
    ? 'Checking backend…'
    : backendOnline
      ? 'Backend Online'
      : 'Backend Unavailable';

  return (
    <AppBar
      position="fixed"
      elevation={0}
      sx={{
        width: { md: `calc(100% - ${DRAWER_WIDTH}px)` },
        ml: { md: `${DRAWER_WIDTH}px` },
        bgcolor: 'background.paper',
        borderBottom: '1px solid',
        borderColor: 'divider',
      }}
    >
      <Toolbar sx={{ gap: 2 }}>
        <IconButton
          edge="start"
          onClick={onMenuClick}
          aria-label="Open navigation menu"
          sx={{ display: { md: 'none' }, color: 'text.primary' }}
        >
          <MenuIcon />
        </IconButton>

        <Typography
          variant="h6"
          color="text.primary"
          fontWeight={500}
          noWrap
          sx={{ flexGrow: 1, fontSize: { xs: '0.95rem', sm: '1.25rem' } }}
        >
          SQL Server Performance Analysis Agent
        </Typography>

        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, flexShrink: 0 }}>
          {data?.version && (
            <Typography
              variant="caption"
              color="text.secondary"
              sx={{ display: { xs: 'none', sm: 'block' } }}
            >
              v{data.version}
            </Typography>
          )}
          <Tooltip
            title={
              backendOnline
                ? 'Connected to the SQLPulse FastAPI backend'
                : 'Cannot reach the SQLPulse FastAPI backend'
            }
          >
            <Chip
              label={statusLabel}
              size="small"
              color={isLoading ? 'default' : backendOnline ? 'success' : 'error'}
              variant="outlined"
            />
          </Tooltip>
        </Box>
      </Toolbar>
    </AppBar>
  );
}
