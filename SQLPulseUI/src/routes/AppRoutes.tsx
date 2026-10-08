import { lazy, Suspense } from 'react';
import { Routes, Route } from 'react-router-dom';
import AppLayout from '../layout/AppLayout';
import { LoadingState } from '../components/common';

// Route-level code splitting keeps the initial bundle small; the charting and
// investigation views are only downloaded when their page is opened.
const DashboardPage = lazy(() => import('../pages/Dashboard/DashboardPage'));
const PerformancePage = lazy(() => import('../pages/Performance/PerformancePage'));
const SlowQueriesPage = lazy(() => import('../pages/SlowQueries/SlowQueriesPage'));
const SqlErrorsPage = lazy(() => import('../pages/SqlErrors/SqlErrorsPage'));
const QueryHistoryPage = lazy(() => import('../pages/QueryHistory/QueryHistoryPage'));
const AgentInvestigationPage = lazy(
  () => import('../pages/AgentInvestigation/AgentInvestigationPage'),
);
const InvestigationHistoryPage = lazy(
  () => import('../pages/InvestigationHistory/InvestigationHistoryPage'),
);

export default function AppRoutes() {
  return (
    <Suspense fallback={<LoadingState message="Loading page…" />}>
      <Routes>
        <Route element={<AppLayout />}>
          <Route index element={<DashboardPage />} />
          <Route path="performance" element={<PerformancePage />} />
          <Route path="slow-queries" element={<SlowQueriesPage />} />
          <Route path="sql-errors" element={<SqlErrorsPage />} />
          <Route path="query-history" element={<QueryHistoryPage />} />
          <Route path="ai" element={<AgentInvestigationPage />} />
          <Route path="investigations" element={<InvestigationHistoryPage />} />
        </Route>
      </Routes>
    </Suspense>
  );
}
