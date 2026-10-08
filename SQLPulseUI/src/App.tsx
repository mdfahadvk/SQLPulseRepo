import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider, CssBaseline, createTheme } from '@mui/material';
import AppRoutes from './routes/AppRoutes';
import { AnalysisProvider } from './hooks/useAnalysis';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: 2,
      staleTime: 30_000,
    },
  },
});

const theme = createTheme({
  palette: {
    primary: { main: '#1565c0' },
    secondary: { main: '#7c4dff' },
    background: { default: '#f5f5f5', paper: '#ffffff' },
  },
  typography: {
    fontFamily: '"Inter", "Roboto", "Helvetica", "Arial", sans-serif',
  },
  shape: { borderRadius: 8 },
  components: {
    MuiCard: {
      defaultProps: { variant: 'outlined' },
    },
  },
});

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        {/*
          TEMP: TestAda IIS deployment base path.
          Remove/change this when deploying SQLPulse to its final IIS location.
          Derived from Vite's BASE_URL so the path is defined once in
          vite.config.ts: '/' in dev, the staging sub-path in a build.
        */}
        <BrowserRouter basename={import.meta.env.BASE_URL}>
          <AnalysisProvider>
            <AppRoutes />
          </AnalysisProvider>
        </BrowserRouter>
      </ThemeProvider>
    </QueryClientProvider>
  );
}
