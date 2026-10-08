# SQLPulse

**Read-only SQL Server Performance Analysis Agent**

SQLPulse connects to Microsoft SQL Server using a dedicated read-only monitoring account, collects performance information, and detects meaningful database performance issues using deterministic analysis.

> **Safety Guarantee:** SQLPulse will NEVER modify SQL Server data, configuration, indexes, execution plans, database objects, or sessions. All connections use `ApplicationIntent=ReadOnly` and queries are validated before execution.

---

## Prerequisites

- Python 3.12+
- Microsoft ODBC Driver 18 for SQL Server
- A SQL Server instance with a **read-only** monitoring account

### Installing the ODBC Driver

- **Windows:** Download from [Microsoft](https://learn.microsoft.com/en-us/sql/connect/odbc/download-odbc-driver-for-sql-server)
- **Linux (Ubuntu/Debian):**
  ```bash
  curl https://packages.microsoft.com/keys/microsoft.asc | sudo apt-key add -
  sudo apt-get update
  sudo apt-get install -y msodbcsql18
  ```

---

## Setup

1. **Clone the repository:**
   ```bash
   cd sql-pulse-agent
   ```

2. **Create a virtual environment:**
   ```bash
   python -m venv .venv
   # Windows
   .venv\Scripts\activate
   # Linux/macOS
   source .venv/bin/activate
   ```

3. **Install dependencies:**
   ```bash
   pip install -r requirements.txt
   ```

4. **Configure environment:**
   ```bash
   cp .env.example .env
   # Edit .env with your SQL Server connection details
   ```

   Required environment variables:
   ```env
   SQL_SERVER=your-sql-server-hostname
   SQL_USERNAME=your-monitoring-user
   SQL_PASSWORD=your-password
   SQL_DRIVER={ODBC Driver 18 for SQL Server}
   SQL_DEFAULT_DATABASE=master
   SQL_ENCRYPT=yes
   SQL_TRUST_SERVER_CERTIFICATE=yes
   ```

---

## Running the Application

```bash
uvicorn app.main:app --host 0.0.0.0 --port 8000 --reload
```

The API will be available at `http://localhost:8000`.

---

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health` | GET | Reports whether SQLPulse is running |
| `/database/connection-test` | GET | Verifies SQL Server connectivity |
| `/api/databases` | GET | Lists accessible user databases |
| `/api/performance/snapshot` | GET | Collects a performance snapshot |
| `/api/performance/analyze` | GET | Runs deterministic performance analysis |
| `/api/performance/slow-history` | GET | Historical timeout candidates from Query Store |
| `/api/agent/investigate` | POST | AI-powered natural-language performance investigation |
| `/api/history/query/{hash}` | GET | Historical metrics for a specific query |
| `/api/history/query/{hash}/baseline` | GET | Computed baseline for a query |
| `/docs` | GET | Interactive API documentation (Swagger UI) |

### Analysis Scopes

SQLPulse supports two analysis modes via the `scope` query parameter:

| Scope | Description | Question Answered |
|-------|-------------|-------------------|
| `server` (default) | Instance-wide analysis | "What is affecting this SQL Server's performance?" |
| `database` | Single database analysis | "Why is this database experiencing performance problems?" |

### Query Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `scope` | `server` | `server` or `database` |
| `database` | — | Target database name (required when `scope=database`) |
| `long_running_threshold_ms` | 5000 | Minimum elapsed time (ms) to flag a query |
| `top_n` | 20 | Number of top CPU / I/O queries to return |
| `wait_stats_top_n` | 25 | Number of top wait types (server scope only) |

### Example API Calls

```bash
# Server-level analysis (default)
curl http://localhost:8000/api/performance/analyze

# Database-level analysis
curl "http://localhost:8000/api/performance/analyze?scope=database&database=MyAppDB"

# List accessible databases
curl http://localhost:8000/api/databases

# Server-level snapshot
curl http://localhost:8000/api/performance/snapshot

# Database-level snapshot
curl "http://localhost:8000/api/performance/snapshot?scope=database&database=MyAppDB"

# Query history (time-series metrics for a specific query)
curl "http://localhost:8000/api/history/query/0xABC123?database=MyAppDB"

# Query baseline (computed historical baseline)
curl "http://localhost:8000/api/history/query/0xABC123/baseline?database=MyAppDB&window=7d"

# Timeout candidate investigation (Query Store)
curl "http://localhost:8000/api/performance/slow-history?database=Merchandising&start_time=2026-08-24T09:00:00Z&end_time=2026-08-24T11:00:00Z&threshold_ms=30000&top_n=20"
```

---

## Analysis Output

The `/api/performance/analyze` endpoint returns:

- **overall_status**: `HEALTHY`, `WARNING`, `DEGRADED`, or `CRITICAL`
- **findings**: Ordered list of all performance issues (by severity)
- **current_health**: Active problems requiring immediate attention (blocking, long-running, regressions)
- **optimization_opportunities**: Optimization suggestions from plan cache analysis (high CPU, high I/O)
- **historical_analysis_status**: Whether historical/regression analysis is available
- **affected_databases**: Summary of which databases have problems (server scope)
- **recommended_drilldown**: Machine-readable suggestions for deeper analysis

### Health vs Optimization Separation

| Category | Classification | Drives overall_status? |
|----------|---------------|----------------------|
| Long-Running Queries | **Health** | Yes |
| Blocking | **Health** | Yes |
| Query Regression | **Health** | Yes |
| High CPU | Optimization | No |
| High I/O | Optimization | No |

The `overall_status` is determined exclusively from **health** findings. Optimization opportunities are reported separately and do not escalate the server's health status.

### Overall Status Logic

| Status | Condition |
|--------|-----------|
| HEALTHY | No findings, or only LOW severity |
| WARNING | Highest severity is MEDIUM |
| DEGRADED | Highest severity is HIGH |
| CRITICAL | Any CRITICAL severity finding |

---

## Running Tests

```bash
pytest
```

Tests use mocked database connections — no live SQL Server required.

---

## Project Structure

```
sql-pulse-agent/
├── app/
│   ├── main.py              # FastAPI application entry point
│   ├── config.py            # Configuration management (Pydantic Settings)
│   ├── logging_config.py    # Structured logging setup
│   ├── database/
│   │   ├── connection.py    # SQL Server connection layer (read-only)
│   │   ├── queries.py       # Monitoring queries (incl. database discovery)
│   │   ├── discovery.py     # Database discovery service with caching
│   │   └── performance_queries.py  # DMV-based performance queries
│   ├── collectors/
│   │   ├── base.py          # Abstract base collector (scope-aware)
│   │   ├── active_requests.py
│   │   ├── long_running.py
│   │   ├── blocking.py
│   │   ├── top_cpu.py
│   │   ├── top_io.py
│   │   ├── wait_stats.py
│   │   ├── query_store.py   # Query Store slow-history collector
│   │   └── snapshot.py      # Orchestrates all collectors (scope-aware)
│   ├── detectors/
│   │   ├── base.py          # Abstract base detector
│   │   ├── engine.py        # Detection engine with health/optimization split
│   │   ├── long_running.py  # Long-running query detector
│   │   ├── blocking.py      # Blocking chain detector
│   │   ├── high_cpu.py      # High CPU usage detector
│   │   ├── high_io.py       # High I/O usage detector
│   │   ├── regression.py    # Query regression detector (historical)
│   │   ├── timeout_candidate.py  # Timeout candidate detector (Query Store)
│   │   └── thresholds.py    # Configurable detection thresholds
│   ├── storage/
│   │   ├── repository.py    # Abstract history repository interface
│   │   ├── sqlite_repository.py  # SQLite implementation (POC)
│   │   ├── history_service.py    # Snapshot persistence & retention
│   │   └── baseline_service.py   # Baseline calculation from history
│   ├── models/
│   │   ├── schemas.py       # API response models
│   │   ├── performance.py   # Performance data models
│   │   ├── findings.py      # Detection finding models (incl. health status)
│   │   ├── history.py       # Historical storage & baseline models
│   │   ├── slow_history.py  # Timeout candidate & Query Store models
│   │   └── agent.py         # AI investigation request/response models
│   ├── agent/
│   │   ├── __init__.py
│   │   ├── llm_provider.py   # LLM abstraction (OpenAI, Azure OpenAI)
│   │   ├── engine.py         # Investigation engine (tool-calling loop)
│   │   ├── tools.py          # Approved read-only agent tools
│   │   └── sanitizer.py      # Data sanitization before LLM
├── tests/
├── .env.example
├── .gitignore
├── requirements.txt
├── pytest.ini
└── README.md
```

---

## Performance Collectors

SQLPulse includes the following collectors, each querying SQL Server DMVs:

| Collector | What it collects | DMV Source | Scope |
|-----------|-----------------|------------|-------|
| Active Requests | Currently executing SQL requests | `sys.dm_exec_requests` + `sys.dm_exec_sessions` | Both |
| Long-Running Queries | Queries exceeding a time threshold | `sys.dm_exec_requests` (filtered) | Both |
| Blocking Sessions | Sessions in blocking chains | `sys.dm_exec_requests` + `sys.dm_exec_sessions` | Both |
| Top CPU Queries | Highest CPU consumers from plan cache | `sys.dm_exec_query_stats` | Both |
| Top I/O Queries | Highest logical-read consumers | `sys.dm_exec_query_stats` | Both |
| Wait Statistics | Top wait types (excludes idle waits) | `sys.dm_os_wait_stats` | Server only |
| Query Store History | Historical slow queries/SPs | `sys.query_store_*` views | Database |

Each collector:
- Returns results even if other collectors fail (partial success)
- Records metadata (timestamp, server, success/failure status)
- Detects and reports permission issues gracefully
- Accepts an optional `database` parameter for scoped collection

---

## Detection Engine

Six deterministic detectors analyze collected data:

| Detector | Detects | Severity Range |
|----------|---------|----------------|
| Long-Running | Queries exceeding configurable time thresholds | LOW → CRITICAL |
| Blocking | Blocking chains and their duration | MEDIUM → CRITICAL |
| High CPU | Queries with excessive CPU usage | LOW → HIGH |
| High I/O | Queries with excessive logical reads | LOW → HIGH |
| Query Regression | Queries performing significantly worse than historical baseline | MEDIUM → CRITICAL |
| Timeout Candidate | Historical queries exceeding application timeout threshold | MEDIUM → CRITICAL |

All detection is deterministic Python logic — no LLM is involved.

---

## Historical Storage & Baselines

SQLPulse automatically stores query performance metrics from every snapshot in a local SQLite database. This enables:

1. **Baseline Calculation**: Statistical baselines computed over configurable time windows (1h, 24h, 7d, 30d)
2. **Regression Detection**: Automatic identification of queries that have degraded relative to their baseline
3. **Trend Analysis**: Time-series history for any tracked query

### Configuration

| Environment Variable | Default | Description |
|---------------------|---------|-------------|
| `HISTORY_RETENTION_DAYS` | 30 | Days to retain historical data |
| `BASELINE_MIN_SAMPLES` | 5 | Minimum samples before a baseline is ready |
| `HISTORY_DB_PATH` | `sqlpulse_history.db` | Path to the SQLite history database |

### Regression Detection Thresholds

| Threshold | Default | Meaning |
|-----------|---------|---------|
| Warning multiplier | 2x | Query is 2x worse than baseline → MEDIUM |
| High multiplier | 4x | Query is 4x worse → HIGH |
| Critical multiplier | 8x | Query is 8x worse → CRITICAL |

### Storage Architecture

The storage layer uses a **Repository Pattern** with an abstract interface (`HistoryRepository`), allowing the SQLite implementation to be replaced by SQL Server or PostgreSQL without changing detector/service logic.

---

## Timeout Candidate Investigation

SQLPulse can investigate historical SQL activity that may have caused application command timeouts using SQL Server **Query Store**.

### How It Works

```
Historical SQL activity
        ↓
Query Store (read-only)
        ↓
Slow query / stored procedure identification
        ↓
Timeout threshold comparison
        ↓
Historical baseline comparison
        ↓
Timeout candidate finding
```

### Important: This Does Not Prove a Timeout

A SQL duration exceeding the application timeout threshold does **not** prove that the .NET application actually received a timeout exception. Results are labelled `timeout_candidate` — not `confirmed_timeout`. Confirming an actual application timeout requires application telemetry (logs, APM traces).

### Query Store Dependency

- Query Store must be enabled (`READ_WRITE` or `READ_ONLY`) on the target database
- If Query Store is unavailable, the API returns `query_store_status: "UNAVAILABLE"` instead of failing
- SQLPulse will **never** enable Query Store automatically — it remains strictly read-only

### Endpoint: `/api/performance/slow-history`

| Parameter | Default | Description |
|-----------|---------|-------------|
| `database` | (required) | Target database name |
| `start_time` | (required) | Start of time range (ISO 8601 UTC) |
| `end_time` | (required) | End of time range (ISO 8601 UTC) |
| `threshold_ms` | 30000 | Application timeout threshold in ms |
| `top_n` | 20 | Max candidates to return (1–100) |

### Example Request

```bash
curl "http://localhost:8000/api/performance/slow-history?database=Merchandising&start_time=2026-08-24T09:00:00Z&end_time=2026-08-24T11:00:00Z&threshold_ms=30000"
```

### Example Response

```json
{
  "server": "DCIR-ERPDBL",
  "database": "Merchandising",
  "start_time": "2026-08-24T09:00:00Z",
  "end_time": "2026-08-24T11:00:00Z",
  "threshold_ms": 30000,
  "query_store_status": "AVAILABLE",
  "top_n": 20,
  "total_candidates": 1,
  "candidates": [
    {
      "label": "timeout_candidate",
      "query_hash": "0x...",
      "query_id": 123,
      "plan_id": 456,
      "object_id": 789,
      "procedure_name": "dbo.uspExample",
      "execution_count": 12,
      "avg_duration_ms": 8500,
      "max_duration_ms": 47200,
      "avg_cpu_ms": 4200,
      "max_cpu_ms": 17000,
      "avg_logical_reads": 180000,
      "max_logical_reads": 920000,
      "first_execution_time": "...",
      "last_execution_time": "...",
      "confidence": "high",
      "severity": "high",
      "baseline_comparison": {
        "baseline_status": "READY",
        "sample_count": 15,
        "historical_mean_ms": 6100,
        "historical_median_ms": 5800,
        "historical_p95_ms": 9400,
        "standard_deviation_ms": 1200,
        "regression_multiplier_vs_mean": 7.7,
        "regression_multiplier_vs_p95": 5.0
      }
    }
  ]
}
```

### Confidence Levels

| Scenario | Confidence |
|----------|------------|
| Exceeded threshold repeatedly + baseline shows normally fast | HIGH |
| Exceeded threshold once OR no baseline available | MEDIUM |
| Duration near threshold + noisy baseline | LOW |

### Severity Levels

| Scenario | Severity |
|----------|----------|
| >3x threshold OR >8x baseline regression | CRITICAL |
| >1.5x threshold OR >4x baseline regression | HIGH |
| At/near threshold | MEDIUM |

### Read-Only Safety

The timeout candidate investigation:
- Only reads from Query Store catalog views
- Never enables Query Store on any database
- Never creates Extended Events sessions
- Never kills sessions or modifies configuration
- Never performs automatic remediation

---

## AI Investigation Agent

SQLPulse includes an optional AI reasoning layer that answers natural-language performance questions by calling approved read-only tools.

### What the AI Agent Does

- Observes SQL Server performance via approved tool calls
- Explains findings using evidence from deterministic collectors
- Correlates multiple data sources (health, blocking, history, baselines)
- Recommends investigation steps for the DBA
- Labels every finding with severity, confidence, and evidence

### What the AI Agent Does NOT Do

- **Never executes arbitrary SQL** against the monitored SQL Server
- **Never modifies** the database, indexes, configuration, or sessions
- **Never invents** procedure names, query hashes, execution times, or blocking info
- **Never claims** a timeout candidate is a confirmed timeout
- **Never performs** automatic remediation
- **Never receives** SQL passwords, connection strings, or API secrets

### Architecture

```
                     User
                      ↓
                  AI Agent
                      ↓
              Approved Tools
          ┌───────────┼───────────┐
          ↓           ↓           ↓
      Current      Historical    Baseline
      Health       SQL Data      Data
          ↓           ↓           ↓
          └───────────┼───────────┘
                      ↓
                AI Correlation
                      ↓
           Explanation + Advice
```

### Investigation Flow

```
UNDERSTAND → OBSERVE → ASSESS → CORRELATE → PRIORITIZE → RECOMMEND
```

The AI selects which SQLPulse tools to call based on the user's question, gathers evidence, then produces a structured explanation.

### Approved Tools

| Tool | Description |
|------|-------------|
| `get_server_health` | Server-wide health analysis |
| `get_database_health` | Single-database health analysis |
| `get_active_requests` | Currently executing SQL requests |
| `get_blocking` | Blocking chain detection |
| `get_long_running_queries` | Queries exceeding time threshold |
| `get_top_cpu_queries` | Top CPU consumers from plan cache |
| `get_top_io_queries` | Top I/O consumers from plan cache |
| `get_query_history` | Historical metrics for a specific query |
| `get_query_baseline` | Computed baseline for a query |
| `get_query_regressions` | Queries that regressed vs baseline |
| `get_slow_history` | Slow query history from Query Store |
| `get_timeout_candidates` | Timeout candidates from Query Store |

### Configuring the LLM Provider

Add these environment variables to your `.env` file:

```env
# Required
LLM_API_KEY=sk-your-api-key-here

# Optional
LLM_PROVIDER=openai          # openai or azure_openai
LLM_MODEL=gpt-4o             # Model name
LLM_BASE_URL=                # Custom base URL (for proxies/Azure)
LLM_TEMPERATURE=0.1          # Lower = more deterministic
MAX_AGENT_TOOL_CALLS=10      # Maximum tool calls per investigation
MAX_AGENT_EXECUTION_SECONDS=60  # Maximum wall-clock time
```

API keys are loaded only from environment variables, never hard-coded or logged.

### Endpoint: `POST /api/agent/investigate`

**Request:**
```json
{
  "question": "Why was Merchandising slow between 9 AM and 11 AM?"
}
```

**Response:**
```json
{
  "investigation_id": "uuid",
  "question": "Why was Merchandising slow between 9 AM and 11 AM?",
  "answer": "Merchandising experienced a significant query slowdown...",
  "severity": "critical",
  "confidence": "high",
  "affected_database": "Merchandising",
  "affected_query": "dbo.uspExample",
  "evidence": [
    "Max duration: 47.2 seconds",
    "Timeout threshold: 30 seconds",
    "Historical mean: 6.1 seconds"
  ],
  "historical_comparison": "7.8x regression vs mean",
  "likely_cause": "Abnormal performance regression",
  "what_is_known": [
    "dbo.uspExample reached 47.2 seconds",
    "Historical mean is 6.1 seconds"
  ],
  "what_is_uncertain": [
    "Whether blocking caused the slowdown",
    "Whether plan regression is involved"
  ],
  "recommended_investigation": [
    "Review execution plan",
    "Check blocking history"
  ],
  "human_review_required": true,
  "agent_action": "Observation and recommendation only. No database change was performed.",
  "tools_used": ["get_slow_history", "get_timeout_candidates"],
  "tool_call_count": 2,
  "duration_seconds": 4.2
}
```

### Timeout-Candidate Limitation

The AI will label queries exceeding the timeout threshold as `timeout_candidate`, not `confirmed_timeout`. Confirming an actual application timeout requires application telemetry (logs, APM traces) that SQLPulse does not have access to.

### Guard Rails

| Protection | Details |
|------------|---------|         
| Max tool calls | Configurable limit (default 10) prevents infinite loops |
| Max execution time | Configurable limit (default 60s) prevents runaway investigations |
| Approved tools only | AI can only call registered read-only SQLPulse tools |
| No SQL generation | AI never generates or executes SQL |
| Data sanitization | Credentials and sensitive values stripped before reaching LLM |
| No hallucination | System prompt requires every claim be backed by tool output |

---

## Required SQL Server Permissions

The monitoring account needs the following **read-only** permissions:

```sql
-- Create a dedicated monitoring login
CREATE LOGIN [sqlpulse_monitor] WITH PASSWORD = '<strong-password>';
USE [YourDatabase];
CREATE USER [sqlpulse_monitor] FOR LOGIN [sqlpulse_monitor];

-- Grant read-only DMV access (server level)
GRANT VIEW SERVER STATE TO [sqlpulse_monitor];

-- Optional: for Query Store access (database level)
GRANT VIEW DATABASE STATE TO [sqlpulse_monitor];

-- Do NOT grant any write permissions
-- Do NOT add to db_owner, db_datawriter, or sysadmin roles
```

| Permission | Required By |
|-----------|-------------|
| `VIEW SERVER STATE` | All collectors (active requests, blocking, CPU, I/O, wait stats) |
| `VIEW DATABASE STATE` | Database-scoped queries and Query Store (optional) |

> **Important:** The account should have NO write permissions whatsoever. SQLPulse enforces this both at the application level (query blocklist + database name validation) and connection level (`ApplicationIntent=ReadOnly`).

---

## Security

- **Read-only enforcement**: All queries are validated against a blocklist before execution
- **Database name injection prevention**: User-supplied database names are validated with regex `^[A-Za-z0-9_\-]+$` and max 128 chars
- **No credential exposure**: Passwords use Pydantic `SecretStr` and never appear in logs or responses
- **Encrypted connections**: TLS encryption enabled by default
- **System database exclusion**: `master`, `model`, `msdb`, `tempdb` excluded from discovery

---

## Security Notes

- Credentials are managed via environment variables and never logged or exposed in API responses.
- The `SQL_SERVER_PASSWORD` is stored as a `SecretStr` — it won't appear in object representations.
- All SQL queries are validated against a blocklist of mutating statements before execution.
- Connections are opened with `ApplicationIntent=ReadOnly`.

---

## License

Private — Internal use only.
