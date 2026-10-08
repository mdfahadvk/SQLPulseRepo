import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// TEMP: TestAda IIS deployment base path.
// Remove/change this when deploying SQLPulse to its final IIS location.
//
// The app is served from a sub-path on staging, so built asset URLs must be
// prefixed with it or the browser requests /assets/... and gets a 404.
// Only `build` is affected: the dev server still serves from the root, so
// `npm run dev` and the /api proxy below are unchanged.
const TEMP_DEPLOY_BASE = '/testada/vinay/A/SQLPulseUI/';

// ─────────────────────────────────────────────────────────────
// Backend selection (dev server only).
//
// Both backends expose an identical REST contract, so the UI needs no
// code changes to switch: flip which BACKEND_TARGET line is commented
// out and restart `npm run dev`.
//
//   .NET 8  — SQLPulseAPI.Net8, port 5266 (see Properties/launchSettings.json)
//             start with: dotnet run --project SQLPulseAPI.Net8
//
//   Python  — sql-pulse-agent FastAPI, port 8000
//             start with: uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload
//
// Only the dev proxy is affected. Production builds use VITE_API_BASE_URL.
// ─────────────────────────────────────────────────────────────

// --- Python FastAPI backend (previous) --------------------------------
// const BACKEND_TARGET = 'http://127.0.0.1:8000';

// --- .NET 8 backend (active) ------------------------------------------
const BACKEND_TARGET = 'http://127.0.0.1:5266';

// Routes the API owns. Everything else is served by Vite.
const PROXIED_PATHS = ['/api', '/health', '/database'];

export default defineConfig(({ command }) => ({
  base: command === 'build' ? TEMP_DEPLOY_BASE : '/',
  plugins: [react()],
  resolve: {
    alias: {
      '@': '/src',
    },
    preserveSymlinks: true,
  },
  server: {
    port: 3000,
    // This project lives on a network share (W:). Node's native filesystem
    // watcher depends on SMB change notifications, which time out and raise
    // an unhandled `ETIMEDOUT ... watch` error that kills the dev server.
    // Polling avoids the notification channel entirely. It costs a little CPU,
    // and it is the only reliable option off a local disk.
    watch: {
      usePolling: true,
      interval: 1000,
    },
    proxy: Object.fromEntries(
      PROXIED_PATHS.map((path) => [path, { target: BACKEND_TARGET, changeOrigin: true }]),
    ),
  },
}));
