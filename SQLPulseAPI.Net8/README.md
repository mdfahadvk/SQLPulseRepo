# SQLPulseAPI.Net8

.NET 8 migration of the SQLPulse backend.

SQLPulse is a **read-only** SQL Server performance analysis agent. It observes and
recommends; it never remediates.

## Project roles

| Project | Role | Status |
| --- | --- | --- |
| `sql-pulse-agent/` (Python / FastAPI) | **Reference implementation and source of truth.** Defines every route, model, JSON field name, enum literal, severity and confidence rule. | Unchanged — do not modify |
| `SQLPulseAPI.Net8/` (this project) | New .NET 8 implementation being migrated to match the Python behaviour and API contract. | In progress |
| `SQLPulseAPI.Net8.Tests/` | xUnit test project for the .NET implementation. | In progress |
| `SQLPulseUI/` (React) | Front end. Must be able to switch from the Python backend to the .NET backend **without model changes**. | Unchanged — do not modify |

When the two backends disagree, the Python implementation is correct.

## Current scope

Implemented:

- ASP.NET Core Web API host, layered project structure, dependency injection
- Strongly typed configuration with validation and environment-variable overrides
- Complete DTO/enum surface matching the Python JSON contract
- Swagger / OpenAPI
- `GET /` — resource discovery document
- `GET /health` — liveness, identical payload to the Python endpoint
- `GET /database/connection-test` — sanitized connectivity probe
- `GET /api/databases` — discovery of accessible monitoring targets
- `GET /api/performance/snapshot` — raw performance measurements, server or database scope
- `GET /api/performance/analyze` — deterministic detector findings, server or database scope
- `GET /api/performance/slow-history` — historical slow-query candidates from Query Store
- `GET /api/history/query/{query_hash}` — stored historical samples for one query
- `GET /api/history/query/{query_hash}/baseline` — statistical baseline for one query
- `POST /api/agent/investigate` — natural-language investigation, evidence-backed
- `GET /api/agent/investigations` — investigation audit trail, newest first
- `GET /api/agent/investigations/{investigation_id}` — one audit record
- SQL Server connectivity, read-only query execution and centralized read-only enforcement
- Database discovery, caching and reusable target-database validation
- The six performance collectors (active requests, long-running queries, blocking sessions,
  top CPU, top I/O, wait statistics) with independent failure handling
- The five deterministic detectors (long-running, blocking, high CPU, high I/O, query
  regression) and the analysis engine that correlates, ranks and separates their findings
- SQLPulse-owned historical storage on SQLite, snapshot persistence, statistical baselines
  and wait-statistics deltas
- Query Store integration: availability reporting, historical slow-query collection,
  interval aggregation, workload classification, and slow-versus-timeout candidate
  classification with ranking
- Monitoring-query exclusion rules (`Utilities/QueryFilters`)
- LLM infrastructure: OpenAI-compatible client over `IHttpClientFactory` (OpenAI, Azure
  OpenAI, Groq), payload sanitization and token-budget trimming, deterministic date
  resolution, and provider failure translation
- The twelve approved read-only agent tools, each delegating to the same service the
  matching REST endpoint uses, with sanitized tool auditing
- The investigation agent loop and its system prompt, structured-answer parsing, and the
  sanitized investigation audit trail
- Global exception handler returning the FastAPI-compatible `{"detail": "..."}` envelope
- xUnit tests covering the endpoints, JSON contracts, configuration, SQL safety, error
  sanitization, collectors, snapshot orchestration, detectors, analysis, baseline
  statistics, history persistence, regression detection, concurrent storage access,
  Query Store availability, slow-query candidate classification, the mocked LLM client,
  sanitization, date resolution, the agent tools, the agent loop, answer parsing, the
  audit trail, and the guarantee that a request to modify the database cannot modify it

Deliberately **out of scope**:

- Execution-plan analysis. Query Store stores plans, but reading and interpreting them is a
  separate capability and is not part of this migration.

## Getting started

```powershell
cd SQLPulseAPI.Net8
dotnet restore
dotnet build
dotnet run
```

The API listens on <http://localhost:5266>. Swagger UI is served at
<http://localhost:5266/swagger>, matching the always-available `/docs` endpoint of
the Python app.

### SigNoz SQL errors

The SQL Errors page can read SQL-related application error logs from SigNoz's
`POST /api/v5/query_range` Logs API. Copy `appsettings.Local.example.json` to
`appsettings.Local.json`, then replace the instance URL and service-account API
key. `appsettings.Local.json` is loaded automatically and ignored by git:

```powershell
Copy-Item appsettings.Local.example.json appsettings.Local.json
# Edit appsettings.Local.json and replace <your-instance> and <your-api-key>
dotnet run
```

The existing `VendorMaintenanceAPI` mapping covers `NS_APP` and `NSWeb`.
`SIGNOZ_ACCESS_TOKEN` and `SIGNOZ_URL` are also supported for local runs.
The ingestion endpoint (`ingest.<region>.signoz.cloud`) is for sending
telemetry and must not be used as the query API base URL.
Run the tests from the repository root:

```powershell
dotnet test
```

## Configuration

Settings live in `appsettings.json` and bind to strongly typed options classes in
`Configuration/`. Every value can be overridden by environment variables.

| Section | Options class | Python counterpart |
| --- | --- | --- |
| `Application` | `ApplicationOptions` | `app_name`, `app_version`, `debug` |
| `SqlServer` | `SqlServerOptions` | `SQL_*` settings |
| `Analysis` | `AnalysisOptions` | slow-query threshold, application timeout, workload program lists |
| `Detectors` | `DetectorThresholdOptions` | `DETECTOR_*` settings |
| `History` | `HistoryOptions` | `HISTORY_*`, `BASELINE_MIN_SAMPLES` |
| `Llm` | `LlmOptions` | `LLM_*` |
| `Agent` | `AgentOptions` | `MAX_TOOL_RESULT_CHARS`, `MAX_AGENT_TOOL_CALLS`, `MAX_AGENT_EXECUTION_SECONDS` |

`SqlServer` supports `Server`, `Username`, `Password`, `DefaultDatabase`,
`ConnectionTimeoutSeconds`, `CommandTimeoutSeconds`, `Encrypt`,
`TrustServerCertificate` and `ApplicationName`.

### Overriding settings

Standard ASP.NET Core double-underscore names are supported and take precedence:

```powershell
$env:SqlServer__Server = "sql01"
$env:SqlServer__Username = "sqlpulse_reader"
$env:SqlServer__Password = "..."
```

The flat environment-variable names used by the Python deployment are also
accepted, so an existing environment keeps working:

```powershell
$env:SQL_SERVER = "sql01"
$env:SQL_USERNAME = "sqlpulse_reader"
$env:SQL_PASSWORD = "..."
$env:APPLICATION_TIMEOUT_MS = "30000"
$env:LLM_API_KEY = "..."
```

### Secrets

Secrets are never committed. `appsettings.json` ships with empty
`SqlServer:Password` and `Llm:ApiKey` values. Use environment variables in
deployment, or user secrets during development:

```powershell
cd SQLPulseAPI.Net8
dotnet user-secrets set "SqlServer:Password" "..."
dotnet user-secrets set "Llm:ApiKey" "..."
```

The SQL password, the LLM API key and complete connection strings are never
logged. Connections are built with `SqlConnectionStringBuilder` and
`PersistSecurityInfo=false`.

### Slow query threshold vs application timeout

`Analysis:SlowQueryThresholdMs` and `Analysis:ApplicationTimeoutMs` answer
different questions and must never be conflated:

- `SlowQueryThresholdMs` — "which queries are worth looking at?"
- `ApplicationTimeoutMs` — "at what duration does the client give up?"

Only the application owns the second number. SQLPulse cannot observe it and must
not guess it. When it is unset, timeout classification is simply unavailable and
candidates stay `SLOW_QUERY_CANDIDATE`.

## Read-only safety

SQLPulse must never change the state of a monitored server. Three layers enforce
this, in order of importance:

1. **The SQL login.** This is the actual security boundary. The monitoring account
   should hold only `VIEW SERVER STATE` / `VIEW DATABASE STATE` and, where needed,
   `db_datareader`. Everything below is defence in depth.
2. **`SqlSafety.EnsureReadOnly`** (`Utilities/SqlSafety.cs`). Every statement is
   validated before a connection is opened. Comments are stripped and batches are
   split on `;`, so a mutating statement cannot hide behind a leading `SELECT` or a
   comment. Blocked prefixes match the Python `_BLOCKED_PREFIXES` list: `INSERT`,
   `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `TRUNCATE`, `EXEC`, `EXECUTE`,
   `GRANT`, `REVOKE`, `DENY`, `MERGE`, `BULK`, `DBCC`, `SHUTDOWN`, `KILL`, `BACKUP`,
   `RESTORE`, `SP_`, `XP_`. The `SP_`/`XP_` prefixes cover `sp_configure` and
   `xp_cmdshell`.
3. **Parameterised SQL.** User input is never concatenated into a statement.

### Database validation

`IDatabaseDiscoveryService` is the allow-list for every database-scoped endpoint:

- `DatabaseNames.IsValid` enforces the Python identifier rule `^[A-Za-z0-9_\-]+$`
  with a 128-character limit, so brackets, quotes, semicolons and whitespace are
  rejected before anything reaches SQL Server.
- `ValidateTargetDatabaseAsync` then checks the name against the discovered set, so
  a caller cannot point SQLPulse at a database the monitoring login cannot open. It
  fails closed when discovery is unavailable.
- `DatabaseNames.Quote` bracket-quotes an already-validated identifier for the rare
  cases where a database name must appear in SQL text rather than as a parameter.

Discovery itself returns only databases with `database_id > 4` (excluding master,
model, msdb and tempdb), `state_desc = 'ONLINE'` and `HAS_DBACCESS(name) = 1`. The
filter is applied again in C# so the guarantee survives any future change to the
query.

### Error and permission handling

SQL Server error text routinely echoes login names, database names and statement
fragments, so nothing from an exception reaches an HTTP response.
`SqlErrorSanitizer` classifies a failure as one of `success`, `permission_denied`,
`failed` or `not_applicable` (`SqlOperationStatus`) and pairs it with one of a small
set of fixed, caller-safe messages. Permission failures are detected by SQL Server
error number (229, 230, 262, 297, 300, 916, 15247, 21089) so a missing
`VIEW SERVER STATE` grant is reported as a permission problem rather than a generic
outage. The original exception is logged at the point of failure and never returned.

## API contract compatibility

The JSON contract is pinned to the Python implementation:

- Property names are snake_case, declared explicitly with `[JsonPropertyName]`.
  The serializer naming policy is disabled so nothing is silently camel-cased.
- Enums serialize to their exact Python literals via `[EnumMember]` and
  `EnumMemberJsonConverter<T>` — for example `HEALTHY`, `critical`, `high_cpu`,
  `SLOW_QUERY_CANDIDATE`, `APPLICATION_OR_INTERACTIVE`.
- Timestamps are `DateTimeOffset` and serialize as ISO-8601 with a `+00:00`
  offset, matching Pydantic.
- `InvestigationResponse.tool_calls_detail` is excluded from responses, matching
  Pydantic's `exclude=True`.

Naming note: the Python analysis payload is `AnalysisResult` and the slow-query
candidate payload is `TimeoutCandidate`; those class names are preserved here
rather than renamed to `AnalysisResponse` / `SlowQueryCandidate`, so the mapping
back to the reference implementation stays obvious.

`JsonContractTests` locks these rules down.

## Structure

```text
SQLPulseAPI.Net8/
├── Collectors/          # the six read-only performance collectors
├── Configuration/       # options classes, validation, legacy env mapping
├── Controllers/         # HTTP endpoints
├── Detectors/           # deterministic detectors, severity/confidence rules
├── Infrastructure/      # JSON contract settings, global exception handling
├── Models/
│   ├── Agent/           # investigation response and audit trail
│   ├── History/         # history, baselines, slow-query candidates
│   ├── Performance/     # snapshots, collector results, findings, analysis
│   ├── Requests/        # request bodies
│   └── Responses/       # health, connection test, database list, errors
├── Repositories/        # IHistoryRepository, SqliteHistoryRepository
├── Services/
│   ├── Agent/           # LLM client, sanitizer, dates, tools, agent loop, audit trail
│   ├── Analysis/        # detector orchestration, correlation, roll-up
│   ├── Database/        # connection factory, read-only executor, discovery
│   ├── History/         # snapshot persistence, baselines, wait deltas
│   ├── Performance/     # collector SQL and snapshot orchestration
│   └── QueryStore/      # Query Store collector and slow-history service
└── Utilities/           # read-only SQL validation, database names, query filters
```

## Performance collectors

Each collector owns one DMV read and **absorbs its own failures**: on error it returns a
result whose `metadata.success` is `false`, with `permission_issue` set when the monitoring
login lacks `VIEW SERVER STATE` / `VIEW DATABASE STATE`. A snapshot therefore always
returns `200` — one unavailable section never takes down the rest.

| Collector | `collector_name` | Source |
| --- | --- | --- |
| Active requests | `active_requests` | `sys.dm_exec_requests` |
| Long-running queries | `long_running_queries` | `sys.dm_exec_requests` |
| Blocking sessions | `blocking_sessions` | `sys.dm_exec_requests` |
| Top CPU | `top_cpu_queries` | plan cache (`sys.dm_exec_query_stats`) |
| Top I/O | `top_io_queries` | plan cache (`sys.dm_exec_query_stats`) |
| Wait statistics | `wait_statistics` | `sys.dm_os_wait_stats` |

Notes:

- **Wait statistics are instance-wide.** At `scope=database` the section is returned with
  `applicability = not_applicable_database_scope`, `delta_status = not_applicable` and
  `metadata.success = true` — "does not apply here" must not read as "could not be collected".
- **Noise exclusion.** The request-level queries filter `session_id <> @@SPID` and
  `is_user_process = 1`, which removes SQLPulse's own session and system background tasks.
  Plan-cache noise is handled by `Utilities/QueryFilters`, which excludes a query only on
  specific evidence — SQLPulse's own `/* SQLPulse:monitoring */` marker, or a reference to a
  DMV with no application use. It never filters `sys.*` broadly, because ORMs and migration
  tools legitimately read `sys.objects`, `sys.tables`, `sys.columns` and friends. As in the
  Python implementation these rules apply at the detector stage, so a snapshot still reports
  everything the DMVs returned.
- **Row limits.** `TOP (n)` is not parameterizable in T-SQL, so the limit is substituted by
  `PerformanceQueries.WithTopN`, which accepts only a range-checked `int`. Every other value,
  including database names and thresholds, is bound as a parameter.
- **Error text is sanitized.** The Python implementation copies `str(exc)` into
  `error_message`; here the raw exception is logged and the response carries a fixed message,
  so server names, login names and statement fragments cannot leak to the client.

## Detectors and analysis

`GET /api/performance/analyze` collects a snapshot and runs five deterministic detectors
over it. There is **no LLM at this layer**: the same snapshot and thresholds always produce
the same findings. Nothing is ever executed against the database — every finding is an
observation carrying evidence, a likely cause, a recommendation and validation steps so a
DBA can confirm it independently.

| Detector | Category | Source section | Severity driver |
| --- | --- | --- | --- |
| Long-running | `long_running_query` | `long_running_queries` | elapsed time, and how many queries are stuck at once |
| Blocking | `blocking` | `blocking_sessions` | longest lock wait, and how wide the chain is |
| High CPU | `high_cpu` | `top_cpu_queries` | CPU per execution, then lifetime total |
| High I/O | `high_io` | `top_io_queries` | logical reads per execution, then lifetime total |
| Query regression | `query_regression` | `top_cpu_queries`, `top_io_queries` + stored history | how many times worse than the stored baseline mean |

### Current health vs optimization opportunities

The response answers two different questions and keeps them apart:

- `current_health` — **is something wrong right now?** Fed only by live activity:
  long-running queries and blocking. `overall_status` is derived from this section alone.
- `optimization_opportunities` — **what is worth tuning?** Fed by the plan cache.

Plan-cache statistics accumulate from the moment a plan was cached, so an expensive cached
query says nothing about whether the server is struggling at this moment. **A cached
expensive query therefore never marks the server `DEGRADED` on its own** — it appears in
`optimization_opportunities` while `overall_status` stays `HEALTHY`. Mixing the two would
make SQLPulse cry wolf on a perfectly healthy instance.

For the same reason the plan-cache detectors never emit `critical`: `high` is the ceiling
for evidence that cannot demonstrate a live problem.

### Correlation

A query that is both CPU-heavy and read-heavy is one problem with two symptoms, not two
problems. Findings are correlated on **database + `query_hash` + `query_plan_hash`** and
merged into a single finding that carries both signals, with the absorbed metrics kept and
labelled (`[high_io] ...`). The plan hash is part of the identity because the same statement
running under two different plans is genuinely two different performance stories.

Merging takes the more serious severity, but **confidence never rises above the stronger of
the two inputs**: both readings come from the same cached statistics, so their agreement is
not independent corroboration. A finding with no `query_hash` is never merged, because
merging on weak identity would combine unrelated queries.

### Unresolved databases

Plan-cache metadata does not always resolve a database. When it cannot, `database` stays
`null` — SQLPulse never substitutes a placeholder that an operator could mistake for a real
database name. Such findings have their confidence reduced (the observation cannot be
attributed), are skipped in `affected_databases`, and get no `recommended_drilldown`, since
pointing someone at a database that could not be named is a dead end.

> **Deviation from the Python implementation.** `app/detectors/long_running.py` writes
> `snapshot.database or "unknown"`. That contradicts the documented contract of the field —
> the Python model's own docstring states it "never emits the literal string 'unknown'" — so
> the .NET port keeps `null`.

### Thresholds

Configured under the `Detectors` section, or the flat `DETECTOR_*` environment variables.

| Setting | Default | Meaning |
| --- | --- | --- |
| `LongRunningWarningMs` | `10000` | lower bound for a `low` cluster finding |
| `LongRunningHighMs` | `30000` | single query becomes `medium` |
| `LongRunningCriticalMs` | `120000` | single query becomes `high` |
| `LongRunningCriticalCount` | `3` | this many at once becomes one `critical` finding |
| `BlockingWarningWaitMs` | `5000` | blocking becomes `medium` |
| `BlockingHighWaitMs` | `30000` | blocking becomes `high` |
| `BlockingCriticalWaitMs` | `60000` | blocking becomes `critical` |
| `BlockingCriticalChainCount` | `3` | sessions in a chain that force `critical` |
| `CpuHighAvgMs` | `5000` | CPU/execution for `medium` |
| `CpuCriticalAvgMs` | `30000` | CPU/execution for `high` |
| `CpuCriticalTotalMs` | `3600000` | lifetime CPU for a `low` aggregate finding |
| `CpuMinExecutions` | `5` | below this, one slow run cannot be told from a pattern |
| `CpuMinSignificantAvgMs` | `10` | floor that stops a cheap, frequent query scoring on totals |
| `IoHighAvgLogicalReads` | `50000` | reads/execution for `medium` |
| `IoCriticalAvgLogicalReads` | `500000` | reads/execution for `high` |
| `IoCriticalTotalLogicalReads` | `100000000` | lifetime reads for a `low` aggregate finding |
| `IoMinExecutions` | `5` | as above |
| `IoMinSignificantAvgReads` | `100` | as above |
| `RegressionWarningMultiplier` | `2.0` | times worse than baseline for `medium` |
| `RegressionHighMultiplier` | `4.0` | times worse than baseline for `high` |
| `RegressionCriticalMultiplier` | `8.0` | times worse than baseline for `critical` |
| `RegressionMinSamples` | `5` | history required before a baseline may be compared against |
| `RegressionBaselineWindow` | `7d` | baseline window used for comparison |

### Other notes

- **Findings are ordered** by severity, then confidence (strongest first). Python sorts the
  confidence key by its raw string value, which orders it alphabetically as
  high → low → medium; the .NET port uses the semantic order the surrounding code already
  defines.
- **Excluded queries are reported, not hidden.** A monitoring query filtered out of the
  findings is listed in `excluded_queries` with a machine-readable reason, deduplicated
  across detectors, so an operator can confirm the filter is not concealing real workload.
- **A failing detector cannot fail the analysis.** Each runs inside its own error boundary;
  a crash is logged and the remaining findings are still returned.
- **Detectors are stateless singletons.** Unlike the Python originals, which accumulate
  exclusions on the instance, each `Detect` call returns its own exclusions — per-instance
  mutable state would leak one caller's results into another's response under concurrency.

## Historical storage, baselines and regression

SQLPulse keeps its own history so it can answer "is this query slower than it used to be?".
This storage is **entirely separate from the monitored SQL Server**, which SQLPulse only ever
reads.

### Storage

SQLite, via `Repositories/SqliteHistoryRepository`, matching the Python reference. The
database file is `sqlpulse_history.db` by default (`History:DatabasePath`, or the legacy
`HISTORY_DB_PATH`).

Concurrency is what makes this safe under ASP.NET Core:

- **WAL journal mode**, so readers never block the writer and the writer never blocks readers.
- **`busy_timeout = 30000`**, so a request that arrives while another is committing waits
  instead of failing immediately.
- **Short-lived pooled connections**, opened per operation rather than held. Both pragmas are
  re-applied on every open, because the pool resets session state.
- **Private cache, not shared.** Shared cache serializes on table-level locks that
  `busy_timeout` does not cover, so concurrent writers would fail outright with
  `SQLITE_LOCKED` rather than waiting.
- **`INSERT OR IGNORE` with a `UNIQUE(snapshot_id, query_hash)` constraint**, so re-persisting
  a snapshot cannot double-count a sample and inflate its own baseline.
- Batched inserts run in **one transaction with parameters bound per row**.

`HistoryRepositoryTests` exercises this directly with 32 concurrent writers and interleaved
readers against a real database file.

Each sample stores `snapshot_id`, `collected_at`, `server`, `database`, `query_hash`, a
200-character sanitized SQL preview, `execution_count`, and both total and average CPU,
elapsed time, logical reads, physical reads and logical writes.

### Baselines

`GET /api/history/query/{query_hash}/baseline?database=...&window=7d`

Windows: `1h`, `24h`, `7d` (default), `30d`. Anything else is a `400`.

Every statistic is computed arithmetically — **no LLM is involved in any calculation**:

| Statistic | Definition |
| --- | --- |
| `sample_count` | number of samples in the window |
| `mean` | arithmetic mean |
| `median` | middle value, or the mean of the two middle values |
| `p50` | the median |
| `p95` | nearest-rank: `ceil(0.95 × n) - 1`, clamped to the last index |
| `standard_deviation` | **sample** standard deviation (divides by `n - 1`); `0` for a single sample |
| `minimum` / `maximum` | smallest and largest sample |

All values are rounded to two decimals using banker's rounding, matching Python's `round()`.

**Insufficient data is never dressed up as a result.** Below `History:BaselineMinSamples`
(default 5) the response reports `baseline_status = INSUFFICIENT_DATA`, the real
`sample_count`, and an **empty** `metrics` object. Zero-valued statistics are never emitted:
they would be indistinguishable from a query that genuinely measured zero, and a zero-mean
baseline would make any current value look like an infinite regression.

### Regression detection

The regression detector compares current plan-cache metrics against the stored baseline
**mean** for `avg_elapsed_ms`, `avg_cpu_ms` and `avg_logical_reads`, and reports the worst
dimension. A dimension is skipped when its baseline statistic is missing, its mean is not
positive, or the current value is not positive.

Confidence is independent of severity, because a baseline that swings wildly cannot support
a strong claim however large the multiplier is:

| Samples | Coefficient of variation (`stddev / mean`) | Confidence |
| --- | --- | --- |
| `< 5` | any | `low` |
| `>= 10` | `< 0.5` | `high` |
| `>= 5` | `< 1.0` | `medium` |
| `>= 5` | `>= 1.0` | `low` |

`historical_analysis_status` now reports `available` when baselines were consulted, and
`unavailable` only when the historical lookup actually failed — "no baseline yet" and
"could not check" are different answers.

### Wait-statistics deltas

`sys.dm_os_wait_stats` is cumulative since the last service restart, so a single reading
describes the server's entire uptime rather than its current state. Each server-scope
snapshot stores its totals and reports the change since the previous reading.

A delta is **refused, with the reason, rather than fabricated**:

| Condition | `delta_status` |
| --- | --- |
| First reading for this server, or no totals collected | `insufficient_previous_data` |
| Reported SQL Server start time differs from the previous reading | `server_restarted` |
| Any counter decreased (for example `DBCC SQLPERF` cleared them) | `server_restarted` |
| Storage unreachable | `insufficient_previous_data` |
| Database scope | `not_applicable` |

### Notes and deviations

- **Persistence is non-fatal.** A storage failure is logged and swallowed: SQLPulse's primary
  job is reading the monitored server, and that does not depend on local history being
  writable. History endpoints then return `503 Historical storage unavailable` rather than an
  empty result, because "no history" and "cannot read history" are different answers.
- **Unresolved databases are stored as `(unresolved)`.** Storage needs a non-null grouping
  key. The Python original uses `"unknown"`, which is a legal SQL Server database name and
  would silently merge with a real database called `unknown`; the parenthesised marker cannot
  be a database name. Findings still keep `null`, as documented above.
- **The repository is asynchronous**, unlike the synchronous Python original, so request
  threads are never blocked on storage I/O. This is why the regression detector exposes
  `DetectAsync` instead of deriving from the synchronous `DetectorBase`.
- **Baseline comparison uses the mean, matching the reference.** `p95` is computed and
  returned on every statistic but is not used to set severity, because changing that would
  change which queries get reported.
- **The snapshot is persisted before it is analysed**, as in the Python pipeline. The sample
  just collected is therefore inside the window it is compared against, which slightly dampens
  the reported multiplier. The ordering is preserved rather than "fixed" so results match the
  reference implementation.

## Query Store and historical slow queries

`GET /api/performance/slow-history?database=...&start_time=...&end_time=...`

Reads SQL Server Query Store to answer "which queries were slow, historically?" — the
question the live DMVs cannot answer once a query has finished.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `database` | required | Target database. Validated against the discovery allow-list. |
| `start_time` / `end_time` | required | ISO-8601 window. `end_time` must be after `start_time`. |
| `threshold_ms` | `30000` | Slow-query **monitoring** threshold. Selects what is reported. |
| `application_timeout_ms` | `Analysis:ApplicationTimeoutMs` | The client's real command timeout. |
| `top_n` | `20` | Row limit, 1–100. |
| `group_results` | `true` | Merge duplicate Query Store intervals per query/plan. |
| `workload_type` | none | Filter by `APPLICATION_OR_INTERACTIVE`, `BACKGROUND_OR_SCHEDULED` or `UNKNOWN`. |

### Query Store is read, never changed

SQLPulse reports Query Store's state and **never enables, disables or reconfigures it**.
Turning Query Store on is a write to a monitored server, and SQLPulse does not write to
the servers it observes — that decision belongs to a DBA who understands the storage and
overhead implications for that database.

| `query_store_status` | Meaning |
| --- | --- |
| `AVAILABLE` | `actual_state_desc = READ_WRITE`. |
| `AVAILABLE_READ_ONLY` | `READ_ONLY`. Still answers historical questions; it just records nothing new. |
| `DISABLED` | `OFF`, an unrecognised state such as `ERROR`, or no options row at all. |
| `PERMISSION_DENIED` | The monitoring login cannot read Query Store. |
| `QUERY_FAILED` | The availability check or the data read failed. |

Only `AVAILABLE` and `AVAILABLE_READ_ONLY` are accessible; anything else short-circuits the
data query entirely. **None of these fail the request.** The endpoint returns `200` with the
status and a message, because one unavailable feature must not take the rest of SQLPulse
down with it. `ERROR` and `TRANSITIONING` are reported verbatim rather than worked around,
since recovering them is an administrative write.

### Slow query candidate vs timeout candidate

Two thresholds, two different claims:

- **`SLOW_QUERY_CANDIDATE`** — `max_duration >= threshold_ms`. Means "worth looking at".
  That threshold is chosen by whoever is monitoring and is arbitrary.
- **`TIMEOUT_CANDIDATE`** — `max_duration >= application_timeout_ms`, and only when that
  timeout has been **explicitly configured**. Means "the client could plausibly have given up".

A monitoring filter of 5,000 ms says nothing about whether the application waits 30 seconds
before abandoning a request, so the slow threshold is never reused as a timeout threshold.
With `threshold_ms = 5000`, `application_timeout_ms = 30000` and an observed 9,749 ms, the
result is a `SLOW_QUERY_CANDIDATE` — not a timeout candidate.

When no application timeout is configured, timeout classification is reported as
**unavailable** rather than guessed, and every candidate stays `SLOW_QUERY_CANDIDATE`.

Neither label is a confirmed timeout: proving a client actually abandoned a request needs
application telemetry SQLPulse does not have.

### Workload classification

`workload_type` is decided only by **strong** evidence:

| Strength | Evidence | Examples |
| --- | --- | --- |
| Strong | What the connection *is* | `program_name` matching a configured mapping, a SQL Agent job step, or a known client driver |
| Strong | What the statement *fundamentally does* | `BACKUP`, `RESTORE`, `DBCC`, `ALTER INDEX ... REBUILD`, `UPDATE STATISTICS` |
| Strong | Caller-supplied `is_scheduled_job` | explicit knowledge from the caller |
| Weak | Naming convention | `uspGetOrders`, `procNightlyPurge` |
| Weak | Generic statement shape | `SELECT ... FROM`, `EXEC` |
| Weak | Host name, unrecognised program name, missing metadata | context only |

Rules that follow from that split:

- **A `usp...` procedure name is never enough to call something application workload.**
  `usp` is a convention a DBA also uses for maintenance helpers, so it is recorded as weak
  evidence and nothing more.
- **Strong background evidence beats strong application evidence.** An application
  connection running `BACKUP DATABASE` is still doing maintenance.
- **Operator-configured program mappings win outright** (`Analysis:WorkloadApplicationPrograms`,
  `Analysis:WorkloadBackgroundPrograms`), because someone who knows the environment has
  stated what that program is.
- **With no strong evidence the answer is `UNKNOWN`**, with the weak signals listed in
  `classification_evidence` so a reader can see exactly what was considered. Query Store
  keeps no session metadata, so `UNKNOWN` is the common and honest answer.

### Grouping and ranking

With `group_results=true`, intervals sharing `(query_hash, query_id, plan_id, object_id)` are
merged into one candidate. Averages are weighted by execution count — an interval with 1,000
executions describes the query far better than one with 2 — while max and min are preserved
so the worst observation is never averaged away. `intervals_observed` and
`intervals_exceeding_threshold` record how consistent the problem was.

Candidates are then ranked (1 = most urgent):

```text
score = (duration_ratio × 0.4
       + interval_ratio × 0.2
       + baseline_regression × 0.2
       + cpu_ratio × 0.1) × workload_boost
```

`workload_boost` is 1.2 for application workload, 0.8 for background and 1.0 for unknown:
a slow application query is felt by someone waiting, a slow nightly batch usually is not.

### Notes and deviations

- **The time window is an interval-overlap test, not a half-open range.** The predicate is
  `last_execution_time >= start_time AND first_execution_time <= end_time`, inclusive at both
  ends, ported exactly from the Python reference. A Query Store runtime-stats row covers a
  span rather than an instant, so this returns every row whose activity touched the window.
- **`baseline_status`, `confidence` and `severity` are strongly typed enums here** where
  Python uses strings. The serialized JSON is identical.
- **Execution-plan analysis is deliberately absent.** Query Store stores plans; interpreting
  them is a separate capability and out of scope for this migration.
- Classification reasons format seconds with three significant digits to match Python's
  `{:.3g}`, so the wording is identical in both implementations.

## AI agent

The agent lets someone ask a question in plain language and get an answer grounded in the
same measurements the REST API exposes. The model decides *which evidence to gather*; it
never decides *what the evidence is*, and it never writes SQL.

### Provider configuration

OpenAI, Azure OpenAI and Groq all speak the same `/chat/completions` protocol, so one
client (`Services/Agent/OpenAiCompatibleLlmClient`) covers all three and switching provider
is a configuration change:

| Setting | Purpose | Default |
| --- | --- | --- |
| `Llm:Provider` | `openai`, `azure_openai` or `groq` | `openai` |
| `Llm:ApiKey` | Provider API key. Environment variable or user secrets only | *(empty)* |
| `Llm:Model` | Model name | `gpt-4o` |
| `Llm:BaseUrl` | Overrides the provider's default endpoint | *(provider default)* |
| `Llm:Temperature` | Sampling temperature; kept low for analysis | `0.1` |
| `Llm:MaxTokens` | Maximum tokens per completion | `1500` |
| `Llm:RequestTimeoutSeconds` | Timeout for a single call | `60` |
| `Agent:MaxToolResultChars` | Budget for one tool result | `4000` |
| `Agent:MaxToolCalls` | Tool calls allowed per investigation | `10` |
| `Agent:MaxExecutionSeconds` | Wall-clock budget per investigation | `60` |

With no base URL configured, `groq` uses `https://api.groq.com/openai/v1` and the OpenAI
providers use `https://api.openai.com/v1`.

The API key is attached as an `Authorization: Bearer` header and never appears in a request
body, a log entry or an exception message. Provider error text is sanitized and truncated
before it is copied anywhere, because an error body can echo part of the request.

### Approved tools

The catalogue in `Services/Agent/AgentToolDefinitions` is the agent's entire capability
surface. Every tool is read-only and delegates to the service that backs the corresponding
REST endpoint, so the AI path and the API path can never disagree about what the server is
doing:

| Tool | Delegates to |
| --- | --- |
| `get_server_health` | `IAnalysisEngine` (server scope) |
| `get_database_health` | `IAnalysisEngine` (database scope) |
| `get_active_requests` | `ActiveRequestsCollector` |
| `get_blocking` | `BlockingSessionsCollector` |
| `get_long_running_queries` | `LongRunningQueriesCollector` |
| `get_top_cpu_queries` | `TopCpuQueriesCollector` |
| `get_top_io_queries` | `TopIoQueriesCollector` |
| `get_query_history` | `IHistoryRepository` |
| `get_query_baseline` | `IBaselineService` |
| `get_query_regressions` | `IAnalysisEngine`, filtered to regression findings |
| `get_slow_history` | `ISlowHistoryService` |
| `get_timeout_candidates` | `IQueryStoreCollector` + `ISlowQueryCandidateBuilder` |

There is deliberately **no** tool for arbitrary SQL, for `INSERT`, `UPDATE` or `DELETE`,
for `KILL`, for creating indexes, for forcing plans, or for changing configuration. An
AI-driven diagnosis must not be able to change the server it is diagnosing.

A tool name that is not in the allow-list is refused before any executor runs. A model can
request a tool that does not exist, and query text it has just read could try to talk it
into one; the allow-list is what makes that a logged rejection rather than an action.

Database names supplied by the model are validated against the discovery allow-list, the
same check the REST endpoints apply, so the model cannot steer SQLPulse at a database the
monitoring login has no access to.

### What the model is never given

- The SQL password, any complete connection string, and the LLM API key
- Any value under a sensitive key (`password`, `api_key`, `token`, `connection_string`, …),
  which is replaced with `[REDACTED]` wherever it appears in a payload
- Full SQL and plan text: verbose fields are trimmed to a 300-character preview

Results are bounded per tool (50 sessions, 20 plan-cache queries, 20 findings, 20
candidates, 100 history samples). When a payload still exceeds `Agent:MaxToolResultChars`,
its longest list is halved repeatedly rather than the text being cut: the counts stay
intact and a note explains that the lists were shortened, so the model cannot report
"3 blocking sessions" when there were thirty.

### Deterministic date resolution

Dates are resolved by `Services/Agent/AgentDateResolver`, not by the model. A language
model has no reliable sense of today's date and will happily answer "the 5th of January"
with a year from its training data — and a silently wrong year returns an empty result set
that reads like a healthy server.

- `today` and `yesterday` resolve to midnight
- ISO 8601 dates and datetimes are accepted; ambiguous formats such as `05/01/2026` are
  refused rather than guessed
- An explicit offset is discarded rather than applied, so the wall-clock reading is
  preserved — matching the REST endpoints
- The current year and the previous one are trusted; an older year is replaced with the
  most recent past occurrence of that month and day
- An inclusive `23:59:59` end time becomes the following midnight, so the last second of
  activity is not dropped

### Failure handling

Provider failures are translated into distinct outcomes because the responses differ:

| Condition | Result |
| --- | --- |
| HTTP 429 | `LlmRateLimitException`, carrying `Retry-After` when supplied |
| HTTP 429 naming the token-per-minute quota, HTTP 413, or a 400 reporting context length | `LlmPayloadTooLargeException` — waiting will not help, the request must shrink |
| No response within the configured timeout | `LlmTimeoutException` |
| Unreachable host or HTTP 5xx | `LlmUnavailableException` |
| No provider or API key configured | `LlmNotConfiguredException` |

Tool failures are not exceptions. An unapproved tool, malformed arguments, an unparseable
date or a failing collector all come back as an `error` payload, because the model can read
that and correct itself, whereas an exception would abandon an investigation over one
recoverable mistake.

### Auditing

Every invocation produces a `ToolCall` record: tool name, raw arguments, sanitized
arguments, a result summary, success, error and duration. The summary carries counts and
scalar metadata rather than payloads, so the audit trail records what a tool found without
storing the SQL text it returned.

### The investigation loop

`POST /api/agent/investigate` runs `Services/Agent/InvestigationEngine`, which follows the
same flow a competent DBA would:

**understand the question → decide whether it is about now or about the past → choose the
approved tools that answer it → gather the evidence → correlate the results → explain what
was found → recommend what to investigate next → change nothing.**

The system prompt (`Services/Agent/AgentPrompt`) is what teaches the model that sequence,
so its wording is behaviour rather than documentation and is kept verbatim from the
reference agent. It classifies questions into five kinds and names the tools for each:

| Question | Path |
| --- | --- |
| "What is slow **right now**?" | database health, active requests, blocking, long-running, top CPU, top I/O |
| "Why was this slow **yesterday / last week**?" | slow history, query history, baselines |
| "Why did this **time out**?" | timeout candidates, then the query's history |
| "Has this **got worse**?" | regressions and baselines |
| "How is the **server** doing?" | server health first, then whatever it points at |

For a broad current question such as *"what is affecting Merchandising performance right
now?"*, the prompt requires all six current-state tools to be consulted before the model is
allowed to conclude that nothing meaningful is wrong. A quiet blocking chain or a single
saturating query is invisible if only one of them is checked, and "I found nothing" is the
most damaging wrong answer a monitoring tool can give.

Three guard-rails bound every investigation: `Agent:MaxToolCalls`,
`Agent:MaxExecutionSeconds`, and the approved-tool allow-list. A model can loop
indefinitely on an ambiguous question, so when either budget is reached the loop stops
gathering and asks for a conclusion from the evidence already collected — partial evidence
with an honest confidence rating beats no answer at all. The controller adds a hard
timeout of `MaxExecutionSeconds + Llm:RequestTimeoutSeconds`, leaving room for that final
summarising call before the request fails cleanly with HTTP 504.

If the provider rejects the conversation as too large, the accumulated evidence is
compacted — the newest tool result keeps 2000 characters, older ones 400 — and a final
answer is requested from what remains, without the tool schemas. Losing an investigation's
evidence to a token limit would waste every query already run against the server.

### One source of truth

The response carries both a written answer and structured fields (`severity`,
`confidence`, `affected_database`, `evidence`, `likely_cause`, `what_is_known`,
`what_is_uncertain`, `recommended_investigation`, `human_review_required` and the rest).
They are **not** requested separately. The model writes one answer, and
`Services/Agent/InvestigationAnswerParser` reads the structured fields back out of that
text.

The answer is not one fixed template. The prompt offers a set of report shapes — a current
performance issue, an optimization opportunity, a healthy result, missing history, a thin
baseline, a partial analysis — and the model writes only the sections its result actually
supports. A healthy database forced through an incident template produces a page of "N/A"
that buries the one line a reader wanted, so unused sections are omitted rather than
filled. The parser accepts the headings from every shape, and where two headings mean the
same thing to a client (`Recommended Investigation` and `Recommended Next Step`,
`Likely Cause` and `Assessment`) both map to the same field.

This is deliberate. Asking a model for prose and JSON separately lets the two disagree, and
a client showing "severity: high" beside an answer that says nothing is wrong is worse than
either statement alone. Parsing is best-effort: a model that ignores the format yields
unparsed fields that fall back to their conservative defaults — `unknown` severity, `low`
confidence, human review required — never to an unearned claim. `human_review_required` is
cleared only by an explicit "no", so a model that waffles cannot wave a finding through.
A report whose own heading says the telemetry was unavailable has its severity forced back
to `unknown`, so absent evidence can never be presented as a determinate finding.

### Current incident versus optimization opportunity

The prompt keeps these apart, and the distinction survives into the answer:

- A **current incident** is happening now — a live blocking chain, a running query
  saturating CPU — and someone may need to act today.
- An **optimization opportunity** is a query that is inefficient but is not currently
  hurting anyone, and belongs on a backlog.

Reporting the second as the first is how a monitoring tool trains people to ignore it.

### The investigation audit trail

Every completed investigation is recorded through
`Services/Agent/InvestigationAuditService` and is readable at
`GET /api/agent/investigations` and `GET /api/agent/investigations/{investigation_id}`. An
unknown identifier returns `found: false` with HTTP 200 rather than 404, matching the
contract the React client is written against.

A record holds the sanitized question, the tools called with their sanitized arguments and
result summaries, the query hashes and procedure names the answer referenced, the severity
and confidence, the sanitized answer, and `remediation_performed: false`. That last field
is structurally true rather than merely defaulted: no approved tool can modify a database.

Two rules shape it:

- **Writing the audit cannot cost the caller the answer.** It is written after the
  investigation has finished, so a storage failure is logged and swallowed; throwing there
  would discard work the caller is waiting for.
- **Nothing sensitive is stored.** The question and the answer are free text that
  originated outside SQLPulse — one from a user, one from a model — and audit storage is
  long-lived, so both are sanitized again on the way in and raw tool arguments are dropped
  in favour of the sanitized ones.

### "Create and apply the best index"

The agent cannot carry that out, and the reason is structural rather than a matter of
instructions. There is no tool that modifies anything, so a model asked to create an index
can only request a tool that does not exist; the allow-list refuses it, the refusal is
recorded, and the answer comes back as a recommendation for a human to review with
`agent_action` still reading *"No database remediation was performed. Observation and
recommendation only."* A test drives exactly that question with a model that tries
`create_index`, `apply_index`, `run_sql` and `execute_sql`, and asserts that no statement
reaches SQL Server at all.

### Notes and deviations

- **Twelve tools are implemented, not ten.** `get_server_health` and
  `get_query_regressions` are part of the reference agent and its system prompt instructs
  the model to call both, so omitting them would change the agent's behaviour.
- **Strings inside arrays are sanitized.** The Python reference copies list entries through
  untouched, which would let a credential inside a list of strings reach the provider.
- **Three additional exception types** (`LlmTimeoutException`, `LlmUnavailableException`,
  `LlmNotConfiguredException`) exist here because the Python implementation delegated those
  cases to the OpenAI SDK; talking to the HTTP API directly makes them ours to handle.
- **An unusable `threshold_ms` or `top_n` is reported rather than silently defaulted**,
  since those values select which queries the model is shown. An unusable
  `application_timeout_ms` is still treated as absent, because "unknown" is a meaningful
  answer there and inventing a timeout would turn slow queries into reported timeouts.
- **`LlmTimeoutException` maps to HTTP 504 and `LlmUnavailableException` to HTTP 503.**
  The Python implementation had no equivalent types and would have returned a generic 500;
  both still use the `{"detail": "..."}` envelope the client reads, and both say what the
  operator can do about it.
- **Bullet sections accept `-` only, not `*`.** The reference parser strips markdown
  emphasis from the whole answer before matching bullets, which means a `* ` bullet has
  already become plain text by the time it is examined. The behaviour is reproduced exactly,
  and pinned by a test, because changing it would change what existing clients receive.
- **A malformed request body returns HTTP 400, not 422.** ASP.NET Core's model validation
  runs before the action, and its default status differs from FastAPI's. This applies to
  every endpoint in the project, not only the agent, so it is left as-is rather than
  changed here.

- **Database validation uses the discovery allow-list**, which is stricter than the Python
  agent's format-only check.

