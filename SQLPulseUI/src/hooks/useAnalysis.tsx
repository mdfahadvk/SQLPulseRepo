import { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from 'react';
import { useDatabases } from './useQueries';

// ─── Types ───

export type AnalysisScope = 'server' | 'database';

export interface AnalysisContextValue {
  /** SQL Server hostname returned by the backend */
  selectedServer: string;
  /** Currently selected database (null when scope is "server") */
  selectedDatabase: string | null;
  /** "server" = entire server, "database" = specific database */
  analysisScope: AnalysisScope;
  /** Available databases from GET /api/databases */
  databases: string[];
  /** Whether databases are loading */
  databasesLoading: boolean;
  /** Database fetch error message, if any */
  databasesError: string | null;
  /** Change analysis scope */
  setScope: (scope: AnalysisScope) => void;
  /** Change selected database */
  setDatabase: (db: string | null) => void;
}

const AnalysisContext = createContext<AnalysisContextValue | null>(null);

const SESSION_KEY_DB = 'sqlpulse_selected_database';
const SESSION_KEY_SCOPE = 'sqlpulse_analysis_scope';

export function AnalysisProvider({ children }: { children: ReactNode }) {
  const { data, isLoading, isError, error } = useDatabases();

  const [analysisScope, setAnalysisScope] = useState<AnalysisScope>(() => {
    const stored = sessionStorage.getItem(SESSION_KEY_SCOPE);
    return stored === 'database' ? 'database' : 'server';
  });

  const [selectedDatabase, setSelectedDatabase] = useState<string | null>(() => {
    return sessionStorage.getItem(SESSION_KEY_DB);
  });

  const selectedServer = data?.server ?? '';
  const databases = data?.databases
    ?.filter((db) => db.status === 'ONLINE')
    .map((db) => db.name) ?? [];

  // Persist scope to session storage
  useEffect(() => {
    sessionStorage.setItem(SESSION_KEY_SCOPE, analysisScope);
  }, [analysisScope]);

  // Persist selected database to session storage
  useEffect(() => {
    if (selectedDatabase) {
      sessionStorage.setItem(SESSION_KEY_DB, selectedDatabase);
    } else {
      sessionStorage.removeItem(SESSION_KEY_DB);
    }
  }, [selectedDatabase]);

  // Clear database when scope switches to server
  useEffect(() => {
    if (analysisScope === 'server') {
      setSelectedDatabase(null);
    }
  }, [analysisScope]);

  // If stored database is no longer in the list, clear it
  useEffect(() => {
    if (databases.length > 0 && selectedDatabase && !databases.includes(selectedDatabase)) {
      setSelectedDatabase(null);
    }
  }, [databases, selectedDatabase]);

  const setScope = useCallback((scope: AnalysisScope) => {
    setAnalysisScope(scope);
  }, []);

  const setDatabase = useCallback((db: string | null) => {
    setSelectedDatabase(db);
    if (db) {
      setAnalysisScope('database');
    }
  }, []);

  const databasesError = isError
    ? (error instanceof Error ? error.message : 'Failed to load databases')
    : null;

  return (
    <AnalysisContext.Provider
      value={{
        selectedServer,
        selectedDatabase,
        analysisScope,
        databases,
        databasesLoading: isLoading,
        databasesError,
        setScope,
        setDatabase,
      }}
    >
      {children}
    </AnalysisContext.Provider>
  );
}

export function useAnalysis(): AnalysisContextValue {
  const ctx = useContext(AnalysisContext);
  if (!ctx) {
    throw new Error('useAnalysis must be used within <AnalysisProvider>');
  }
  return ctx;
}
