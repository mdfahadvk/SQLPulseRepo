# SQLPulse UI

React frontend for **SQLPulse**, the SQL Server performance analysis agent.

The UI is a thin presentation layer. All collection, detection, correlation, and
LLM reasoning happens in the FastAPI backend — the browser only renders what the
API returns.

---

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| Node.js | 18 or newer (20 LTS recommended) | `node -v` |
| npm | 9 or newer | Ships with Node |
| SQLPulse backend | running and reachable | See below |

### Backend requirement

The UI cannot function on its own — every page fetches from the SQLPulse API.
Start a backend before running the frontend.

Two interchangeable backends expose the **same REST contract**, so the UI runs
against either one without any code change.

#### .NET 8 backend — port 5266 (currently active)

```bash
cd ../SQLPulseAPI.Net8
dotnet run
```

Confirm it is up:

```bash
curl http://127.0.0.1:5266/health
```

Swagger UI: <http://127.0.0.1:5266/swagger>

#### Python FastAPI backend — port 8000 (alternative)

```bash
cd ../sql-pulse-agent
pip install -r requirements.txt
uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload
```

Confirm it is up:

```bash
curl http://127.0.0.1:8000/health
```

#### Switching between them

Edit `BACKEND_TARGET` in `vite.config.ts` — comment out one line, uncomment the
other — then restart `npm run dev`.

The backend owns the SQL Server connection string and the LLM API key. Those
values live in the backend's own configuration (the Python `.env`, or .NET User
Secrets / environment variables) and are **never** sent to the browser.

---

## Install

```bash
npm install
```

Then create your local environment file:

```bash
cp .env.example .env    # PowerShell: Copy-Item .env.example .env
```

---

## Environment configuration

Only one variable is used:

| Variable | Default | Meaning |
| --- | --- | --- |
| `VITE_API_BASE_URL` | *(empty)* | Base URL for API requests. |

- **Leave it empty during development.** Requests then go to the Vite dev server,
  which proxies `/api`, `/health`, and `/database` to the backend selected by
  `BACKEND_TARGET` in `vite.config.ts` (currently the .NET 8 API on port 5266).
  This avoids CORS configuration entirely.
- **Set it for production** when the built assets are served from a different
  origin than the API, e.g. `VITE_API_BASE_URL=https://sqlpulse.internal.example`.

### Security note

Everything prefixed with `VITE_` is **inlined into the public JavaScript bundle**
at build time and is readable by anyone who loads the page.

Never put the following in this project's `.env`:

- SQL Server credentials or connection strings
- LLM / OpenAI / Azure OpenAI API keys
- Any other secret

They belong exclusively to the FastAPI backend. `.env` is git-ignored;
`.env.example` is committed and must contain no real values.

---

## Development

```bash
npm run dev
```

Opens on <http://localhost:3000>. The proxy targets and dev port are defined in
[vite.config.ts](vite.config.ts).

> **Network drives / UNC paths:** if the project lives on a mapped drive, npm
> scripts can fail to resolve the local `vite` binary. Invoke it directly:
>
> ```powershell
> node .\node_modules\vite\bin\vite.js --config .\vite.config.ts
> ```

---

## Build

```bash
npm run build     # tsc -b (typecheck) then vite build -> dist/
npm run preview   # serve dist/ locally to verify the production bundle
```

Type-check without emitting:

```bash
node .\node_modules\typescript\bin\tsc --noEmit
```

Lint:

```bash
npm run lint
```

Deploy the contents of `dist/` behind any static file server. If that server is
on a different origin than the API, rebuild with `VITE_API_BASE_URL` set.

---

## Available pages

| Route | Page | What it does |
| --- | --- | --- |
| `/` | **Dashboard** | Runs an on-demand analysis for the selected scope (whole server or one database). Shows overall health, key metrics, affected databases, and a sortable findings table. Click a finding for full evidence and recommendations. |
| `/performance` | **Performance** | Live diagnostics across seven tabs: Overview, Active Requests, Top CPU, Long Running, Wait Stats, Top I/O, and Blocking. Each tab is a sortable, paginated table with metric tooltips. |
| `/slow-queries` | **Slow Queries** | Query Store-backed history search by database, time range, duration threshold, workload type, and result count. Flags likely timeout candidates and opens a detail drawer per query. |
| `/query-history` | **Query History** | Metric trends for a single query hash over time, plus baseline statistics for a chosen window and a regression comparison against the latest sample. |
| `/ai` | **AI Investigation** | Ask a natural-language question about server or database performance. The backend agent runs the diagnostic tools and returns a summary with severity, confidence, evidence, and technical details. |
| `/investigations` | **Investigation History** | Audit trail of past AI investigations, filterable and sortable, with a drawer showing the question, answer, and tools invoked. |

The scope selector (server vs. database) is shared across pages and persists for
the browser session.

---

## Architecture

```
src/
  api/         Axios client + one module per backend area
  components/
    common/    Reusable primitives (DataTable, SqlText, badges, state views)
    dashboard/ performance/ slow-queries/ query-history/ investigation/
  hooks/       Analysis scope context, TanStack Query hooks
  layout/      App shell: responsive sidebar, header, content area
  pages/       One folder per route
  types/       TypeScript mirrors of the backend response models
  utils/       Shared formatters (durations, numbers, timestamps, SQL)
```

Two shared components carry most of the UX consistency:

- **`DataTable`** — every table in the app. Provides column sorting, pagination
  past 25 rows, sticky headers, header tooltips, keyboard-navigable rows, and a
  built-in empty state.
- **`SqlText`** — renders query text as truncated monospace with the full
  statement in a tooltip, plus optional copy-to-clipboard. Keeps rows a uniform
  height and prevents long SQL from breaking layouts.

All dates, durations, and numbers go through `src/utils/formatters.ts` so
formatting is identical everywhere. Missing values render as an em dash.

---

## Stack

- React 19 + TypeScript 5.7
- Vite 6
- Material UI (MUI) v6
- React Router v7
- TanStack Query v5
- Axios
- Recharts

### Known constraint: MUI icons

The installed `@mui/icons-material` build ships JavaScript without type
declarations, so deep icon imports are typed by
[src/mui-icons.d.ts](src/mui-icons.d.ts). Icon names are therefore **not**
type-checked — a missing icon only fails during `vite build`. Verify an icon
exists under `node_modules/@mui/icons-material/` before importing it.

