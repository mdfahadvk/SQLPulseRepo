// Public entry point for the analysis scope context.
// The implementation lives in useAnalysisContext.tsx so that this module can be
// imported as `../../hooks/useAnalysis` from anywhere without a .tsx extension.
export { AnalysisProvider, useAnalysis } from './useAnalysisContext';
export type { AnalysisScope, AnalysisContextValue } from './useAnalysisContext';
