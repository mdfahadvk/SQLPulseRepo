# SQLPulse — Python → .NET 8 Migration Status

**Status:** Migration complete and validated against automated tests.
**Target:** `SQLPulseAPI.Net8` (ASP.NET Core Web API, `net8.0`)
**Source of truth:** `sql-pulse-agent/` (FastAPI + Pydantic)
**Consumer:** `SQLPulseUI` (React + TypeScript + axios) — unchanged

| Gate | Result |
| --- | --- |
| `dotnet restore` | Succeeded |
| `dotnet build -c Release` | Succeeded — 0 warnings, 0 errors |
| `dotnet test -c Release` | Succeeded — 807 passed, 0 failed, 0 skipped |

> This document covers functional migration status only. Deployment and IIS
> configuration are deliberately out of scope and are not described here.

---

## 1. Component Mapping

### Application entry point

| Python | .NET | Notes |
| --- | --- | --- |
| `app/main.py` (route decorators) | `Controllers/RootController.cs`, `HealthController.cs`, `DatabaseController.cs`, `PerformanceController.cs`, `HistoryController.cs`, `AgentController.cs` | One FastAPI module split into six controllers by resource. |
| `app/main.py` (exception handlers) | `Infrastructure/GlobalExceptionMiddleware.cs` | Maps domain exceptions to the Python status codes and the `{"detail": ...}` envelope. |
| `app/config.py` | `Configuration/*.cs` (`SqlPulseConfiguration`, `SqlServerOptions`, `HistoryOptions`, `AgentOptions`, `LlmOptions`, `AnalysisOptions`, `DetectorThresholdOptions`, `ApplicationOptions`) | Options pattern; `LegacyEnvironmentConfiguration.cs` keeps the original `.env` variable names working. |
| `app/logging_config.py` | ASP.NET Core built-in logging | Configured in `Program.cs`. |

### Database access

| Python | .NET |
| --- | --- |
| `app/database/connection.py` | `Services/Database/SqlServerConnectionFactory.cs`, `SqlQueryExecutor.cs`, `ISqlQueryExecutor.cs`, `IDatabaseConnectionFactory.cs` |
| `app/database/discovery.py` | `Services/Database/DatabaseDiscoveryService.cs`, `IDatabaseDiscoveryService.cs` |
| `app/database/queries.py` | `Services/Database/SqlQueries.cs` |
| `app/database/performance_queries.py` | `Services/Performance/PerformanceQueries.cs` |
| read-only guard inside `connection.py` | `Utilities/SqlSafety.cs` (extracted and independently tested) |
| database-name validation | `Utilities/DatabaseNames.cs` |

### Collectors

| Python | .NET |
| --- | --- |
| `app/collectors/base.py` | `Collectors/CollectorBase.cs` |
| `app/collectors/active_requests.py` | `Collectors/ActiveRequestsCollector.cs` |
| `app/collectors/long_running.py` | `Collectors/LongRunningQueriesCollector.cs` |
| `app/collectors/blocking.py` | `Collectors/BlockingSessionsCollector.cs` |
| `app/collectors/top_cpu.py` | `Collectors/TopCpuQueriesCollector.cs` |
| `app/collectors/top_io.py` | `Collectors/TopIoQueriesCollector.cs` |
| `app/collectors/wait_stats.py` | `Collectors/WaitStatisticsCollector.cs` |
| `app/collectors/snapshot.py` | `Services/Performance/PerformanceSnapshotService.cs`, `IPerformanceSnapshotService.cs` |
| `app/collectors/query_store.py` | `Services/QueryStore/QueryStoreCollector.cs` |

### Detectors and analysis

| Python | .NET |
| --- | --- |
| `app/detectors/base.py` | `Detectors/DetectorBase.cs` |
| `app/detectors/long_running.py` | `Detectors/LongRunningDetector.cs` |
| `app/detectors/blocking.py` | `Detectors/BlockingDetector.cs` |
| `app/detectors/high_cpu.py` | `Detectors/HighCpuDetector.cs` |
| `app/detectors/high_io.py` | `Detectors/HighIoDetector.cs` |
| `app/detectors/regression.py` | `Detectors/QueryRegressionDetector.cs` |
| `app/detectors/timeout_candidate.py` | `Detectors/SlowQueryCandidateBuilder.cs`, `Detectors/WorkloadClassifier.cs` |
| `app/detectors/query_filters.py` | `Utilities/QueryFilters.cs` |
| `app/detectors/thresholds.py` | `Configuration/DetectorThresholdOptions.cs` |
| `app/detectors/engine.py` | `Services/Analysis/AnalysisEngine.cs`, `IAnalysisEngine.cs` |

### History and baselines

| Python | .NET |
| --- | --- |
| `app/storage/repository.py` | `Repositories/IHistoryRepository.cs` |
| `app/storage/sqlite_repository.py` | `Repositories/SqliteHistoryRepository.cs` |
| `app/storage/history_service.py` | `Services/History/HistoryService.cs` |
| `app/storage/baseline_service.py` | `Services/History/BaselineService.cs`, `BaselineStatistics.cs` |
| `app/storage/wait_stats_service.py` | `Services/History/WaitDeltaService.cs` |
| `app/services/slow_history_service.py` | `Services/QueryStore/SlowHistoryService.cs` |

### AI agent

| Python | .NET |
| --- | --- |
| `app/agent/engine.py` | `Services/Agent/InvestigationEngine.cs`, `AgentPrompt.cs`, `InvestigationAnswerParser.cs`, `AgentDateResolver.cs` |
| `app/agent/tools.py` | `Services/Agent/AgentToolDefinitions.cs`, `AgentToolInvoker.cs` |
| `app/agent/llm_provider.py` | `Services/Agent/ILlmClient.cs`, `OpenAiCompatibleLlmClient.cs`, `LlmContracts.cs`, `LlmExceptions.cs` |
| `app/agent/sanitizer.py` | `Services/Agent/LlmSanitizer.cs` |
| investigation audit persistence | `Services/Agent/InvestigationAuditService.cs` |

### Models

| Python | .NET |
| --- | --- |
| `app/models/performance.py` | `Models/Performance/*.cs` |
| `app/models/findings.py` | `Models/Performance/Findings.cs`, `FindingEnums.cs` |
| `app/models/history.py` | `Models/History/HistoryModels.cs` |
| `app/models/slow_history.py` | `Models/History/SlowHistoryModels.cs` |
| `app/models/agent.py` | `Models/Agent/InvestigationResponse.cs`, `InvestigationAudit.cs`, `Models/Requests/InvestigationRequest.cs` |
| `app/models/schemas.py` | `Models/Responses/CommonResponses.cs` |
| Pydantic `alias` / `Enum` serialization | `Infrastructure/SqlPulseJson.cs`, `Utilities/EnumMemberJsonConverter.cs` |

Modules present in Python but empty (`app/analysis/`, `app/notifications/`) have no
.NET counterpart, as there is nothing to port.

---

## 2. Migrated Endpoints

All 12 FastAPI routes are implemented. Routes, verbs, query parameters and default
values match the Python implementation.

| Method | Route | .NET controller | Compatibility |
| --- | --- | --- | --- |
| GET | `/` | `RootController` | Full |
| GET | `/health` | `HealthController` | Full |
| GET | `/database/connection-test` | `DatabaseController` | Full |
| GET | `/api/databases` | `DatabaseController` | Full |
| GET | `/api/performance/snapshot` | `PerformanceController` | Full |
| GET | `/api/performance/analyze` | `PerformanceController` | Full |
| GET | `/api/performance/slow-history` | `PerformanceController` | Full — see difference D2 |
| GET | `/api/history/query/{query_hash}` | `HistoryController` | Full |
| GET | `/api/history/query/{query_hash}/baseline` | `HistoryController` | Full — see difference D1 |
| POST | `/api/agent/investigate` | `AgentController` | Full — see differences D4, D6 |
| GET | `/api/agent/investigations` | `AgentController` | Full |
| GET | `/api/agent/investigations/{investigation_id}` | `AgentController` | Full |

### Query parameter defaults (verified by test)

| Endpoint | Parameters |
| --- | --- |
| `snapshot`, `analyze` | `scope="server"`, `database=null`, `long_running_threshold_ms=5000`, `top_n=20`, `wait_stats_top_n=25` |
| `slow-history` | `threshold_ms=30000`, `top_n=20`, `group_results=true`, `application_timeout_ms=null`, `workload_type=null` |
| `history/query/{hash}` | `limit=500` |
| `baseline` | `window="7d"` |
| `investigations` | `limit=50` |

### Status codes

| Condition | Status | Body |
| --- | --- | --- |
| Success | 200 | Endpoint payload |
| `scope=database` without `database` | 400 | `{"detail": ...}` |
| Unknown / invalid database name | 400 | `{"detail": ...}` |
| `end_time` not after `start_time` | 400 | `{"detail": ...}` |
| Invalid baseline window | 400 | `{"detail": ...}` — see D1 |
| Unknown investigation id | 404 | `{"detail": ...}` |
| Historical storage unavailable | 503 | `{"detail": ...}` |
| SQL Server unreachable | 503 | `{"detail": ...}` |
| LLM provider unavailable | 503 | `{"detail": ...}` — see D6 |
| LLM timeout | 504 | `{"detail": ...}` — see D6 |

Every error path returns the FastAPI-style `{"detail": "..."}` envelope, which is
what the React axios interceptor reads.

---

## 3. Automated Test Status

`dotnet test -c Release` → **807 passed, 0 failed, 0 skipped.**

| Area | Coverage |
| --- | --- |
| Configuration | `ConfigurationTests` |
| Connectivity and discovery | `DatabaseConnectionTests`, `DatabaseDiscoveryTests`, `DatabaseEndpointTests`, `SqlSafetyTests` |
| Collectors and snapshot | `CollectorTests`, `PerformanceQueriesTests`, `PerformanceSnapshotServiceTests`, `PerformanceEndpointTests` |
| Detectors and analysis | `DetectorTests`, `QueryRegressionDetectorTests`, `SlowQueryCandidateBuilderTests`, `QueryFiltersTests`, `AnalysisEngineTests`, `AnalyzeEndpointTests` |
| History and baselines | `HistoryRepositoryTests`, `HistoryServiceTests`, `BaselineServiceTests`, `BaselineStatisticsTests`, `WaitDeltaServiceTests`, `HistoryEndpointTests` |
| Query Store and slow history | `QueryStoreCollectorTests`, `SlowHistoryEndpointTests` |
| AI agent | `AgentToolInvokerTests`, `AgentDateResolverTests`, `LlmSanitizerTests`, `OpenAiCompatibleLlmClientTests`, `InvestigationEngineTests`, `InvestigationAnswerParserTests`, `InvestigationAuditServiceTests`, `AgentEndpointTests` |
| JSON contract | `JsonContractTests` |

### Compatibility and security suites added during final validation (89 tests)

| Suite | Tests | Purpose |
| --- | --- | --- |
| `Compatibility/PythonContractTests` | 22 | Deserializes representative sanitized Python responses into the .NET models, re-serializes, and compares structure. Catches renamed, missing or extra JSON properties. |
| `Compatibility/PythonEndpointContractTests` | 21 | Exercises the live endpoints through the test host and checks routes, verbs, parameter defaults, served payload shape, the error envelope and timestamp formatting. |
| `Compatibility/SecurityPostureTests` | 46 | Asserts the security guarantees listed in section 5. |

Supporting files: `Compatibility/PythonResponseFixtures.cs` holds the golden-master
payloads; `Compatibility/JsonShape.cs` performs the recursive structural comparison.

The comparison intentionally checks **structure and meaningful values only**.
Generated values — investigation UUIDs, `collected_at` / `generated_at` timestamps,
durations — are not required to match, since they differ on every run. Open-ended
maps (`metrics`, tool `arguments`, `result_summary`) are treated as data rather
than contract and are exempt from the property-name check.

The harness has demonstrated that it detects real divergence: an initial fixture
that guessed the wrong shape for `WorkloadEvidence` failed immediately, and the
Python model confirmed the .NET implementation was the correct one.

---

## 4. Known Differences

These are accepted, understood divergences. None of them break the React UI.

**D1 — Invalid baseline window message formatting.**
Python interpolates a Python list and emits
`Invalid window '5d'. Use one of: ['1h', '24h', '30d', '7d']`.
.NET emits `Invalid window '5d'. Use one of: 1h, 24h, 30d, 7d`.
Same status code (400), same allowed values, same field. Cosmetic only — the UI
displays `detail` verbatim and never parses it.

**D2 — `slow-history` validates the database more strictly.**
Python calls `validate_database_name`, which checks the name's *format* only.
.NET resolves the name against the discovery allow-list, as the other endpoints
already did. This is a deliberate hardening: it rejects well-formed names for
databases that do not exist or are not monitorable. It is safe for the UI, which
populates its database selector from `/api/databases`.

**D3 — Timestamps sourced from SQL Server carry an explicit offset.**
Python emits naive ISO-8601 strings for values read out of SQL Server, which
JavaScript's `Date` parses as *local* time. .NET treats a SQL `DateTime` with
unspecified kind as UTC and emits an explicit offset, which JavaScript parses as
the intended instant. The .NET behaviour is unambiguous and preserves
`DateTimeOffset` round-tripping; reverting it would reintroduce the ambiguity.
Python model-generated timestamps were already timezone-aware
(`datetime.now(UTC)`), so this affects SQL-sourced values only. Worth a visual
check in staging — see section 7.

**D4 — Request body validation returns 400, not 422.**
ASP.NET Core model validation returns 400 where FastAPI/Pydantic returns 422.
This applies to any endpoint with a validated body, currently
`POST /api/agent/investigate`. The React client does not branch on status codes;
it reads `error.response?.data?.detail` and falls back to `error.message`, so the
rendered behaviour is unchanged.

**D5 — Investigation answer bullet parsing.**
The Python parser's handling of a literal `"* "` bullet prefix was ported exactly,
including its quirk, and is pinned by a test so it cannot drift silently.

**D6 — LLM failures are distinguished.**
.NET returns 503 for an unavailable LLM provider and 504 for a timeout, where
Python surfaced a generic 500. More accurate, and still an error response with the
same `detail` envelope.

**D7 — Rate-limited LLM calls are retried; Python gave up.**
Python treated every token-per-minute 429 as fatal. .NET inspects the provider's
own numbers: when they show the request would have fit and only the rolling
window was full, the call is retried once after the provider's stated delay. A
request genuinely larger than the whole quota keeps the original "send less"
handling. This is required for providers with small per-minute quotas, where an
investigation needing more than one model call would otherwise always fail.
See section 8.

---

## 5. Security Posture

Each guarantee below is enforced by a test in
`Compatibility/SecurityPostureTests.cs`.

| Guarantee | How it is enforced |
| --- | --- |
| SQL access is read-only | `Utilities/SqlSafety.EnsureReadOnly` strips comments, splits on `;` and rejects 22 mutating statement prefixes (INSERT, UPDATE, DELETE, DROP, ALTER, CREATE, TRUNCATE, EXEC, EXECUTE, GRANT, REVOKE, DENY, MERGE, BULK, DBCC, SHUTDOWN, KILL, BACKUP, RESTORE, SP_, XP_). Tested including mutations hidden behind a leading `SELECT` or a comment. |
| No remediation endpoints exist | The route table is enumerated from the live `EndpointDataSource`: zero PUT, PATCH or DELETE routes, and the only POST is `api/agent/investigate`. This is structural — a future remediation endpoint fails the test on arrival. |
| No arbitrary SQL is exposed to the AI | Every agent tool name begins with `get_`; the invoker rejects any tool outside the allow-list. An unapproved tool call is verified to never reach SQL Server (`ExecuteCount == 0`). |
| The AI cannot change the database | The agent has no path to SQL other than the allow-listed read tools, and those go through the same read-only guard. |
| Credentials are never returned | A connection failure is asserted not to leak user, password or connection string. |
| Connection strings are never returned | Covered by the same assertions and by `SqlErrorSanitizer`. |
| LLM API keys are never exposed | No response payload carries the configured key. |
| SQL errors are sanitized | `Infrastructure/SqlErrorSanitizer.cs`; a collector failure returns a safe message, not the underlying SQL error text. |
| Database names are validated | `Utilities/DatabaseNames.cs` rejects malformed and injection-shaped names; system databases are excluded from monitoring targets. |
| Sensitive values are redacted before reaching the LLM | `Services/Agent/LlmSanitizer.cs`, tested over multiple secret shapes. |
| Raw tool detail is withheld from the response | The investigation response exposes `tools_used`, not raw tool arguments. |

---

## 6. React Compatibility

**Status: compatible. No changes were made to `SQLPulseUI`, and none are required.**

The React application was reviewed in full against the .NET responses:

- **Data models** — every TypeScript interface in `src/types/` was compared against
  the corresponding .NET model. All JSON property names match.
- **Enum and string literals** — all 44 `[EnumMember]` wire values in the .NET
  models were extracted and compared against the literals the UI switches on. All
  match exactly, including the values consumed by `SeverityBadge`,
  `ConfidenceBadge`, `HealthBadge`, `QueryStoreStatusBadge`, `WorkloadBadge`, the
  `db.status === 'ONLINE'` check and the `baseline_status === 'READY'` check.
- **Error handling** — `src/api/client.ts` reads `error.response?.data?.detail` and
  falls back to `error.message`. The .NET `ErrorResponse` serializes to exactly
  `{"detail": "..."}`. The client does not branch on HTTP status codes anywhere,
  which is why D4 is not user-visible.
- **Base URL** — the client uses `import.meta.env.VITE_API_BASE_URL || ''`, and the
  Vite dev proxy targets `http://127.0.0.1:8000`. Pointing the UI at the .NET API
  is a configuration change, not a code change.
- **`tool_calls_detail`** — declared in the TypeScript types but never read; the
  investigation drawer renders `tools_called` from the audit record. Withholding
  the raw detail is therefore both safe and correct.

No .NET response-contract differences that would break the UI were found, so no
contract was altered to accommodate it.

---

## 7. Remaining Limitations and Staging Work

### Limitations

- Execution-plan retrieval and analysis was not part of the Python application and
  is not implemented here.
- `app/analysis/` and `app/notifications/` are empty in Python; there is nothing to
  port and no .NET equivalent exists.
- The automated suite runs entirely against fakes and an in-memory history
  repository. No test touches a real SQL Server instance or a real LLM provider.
- Query Store behaviour is verified against recorded result shapes, not against a
  live Query Store.

### Tests still required in staging

These cannot be covered by the automated suite and must be performed against real
infrastructure:

1. **SQL Server connectivity** under the intended least-privilege login — confirm
   every collector succeeds, and confirm the login genuinely cannot write.
2. **Permission degradation** — confirm that a login lacking `VIEW SERVER STATE`
   produces the `PERMISSION_DENIED` collector status rather than a hard failure.
3. **Query Store** against a real database with Query Store enabled, disabled, and
   read-only, to confirm all three status values are reported correctly.
4. **A real LLM provider** — confirm tool-calling, the answer parser, timeout
   handling (504) and provider-unavailable handling (503) against the live model.
5. **Timestamp rendering (D3)** — load the UI against the .NET API and confirm
   SQL-sourced timestamps render at the expected wall-clock time in the browser's
   timezone.
6. **End-to-end UI run** — point `VITE_API_BASE_URL` at the .NET API and exercise
   every page: snapshot, analysis, slow history, query history, baselines and an
   AI investigation including the audit list and detail views.
7. **Load and concurrency** — concurrent snapshot and analyze requests against a
   busy server, to confirm connection handling and timeouts behave.

---

## 8. LLM Provider Token Budget

The investigation agent sends the system prompt and the full tool schema on every
model call, which costs roughly **3,500–4,200 tokens per call**. A single
investigation makes two or more calls, so it needs **7,000–15,000 tokens per
minute**.

This matters on providers with a small tokens-per-minute (TPM) quota. On the Groq
free tier every tool-calling model is capped at **8,000 TPM**, which is enough for
a two-call investigation but not a longer one. Measured on this account:

| Model | TPM | Tool calling |
| --- | --- | --- |
| `openai/gpt-oss-120b` | 8,000 | yes |
| `openai/gpt-oss-20b` | 8,000 | yes |
| `qwen/qwen3.8-27b`, `qwen/qwen3.6-27b` | 8,000 | yes |
| `allam-2-7b` | 6,000 | yes |
| `groq/compound-mini` | 70,000 | **no** — rejects `tools` |

The one high-quota model does not support tool calling, so it cannot run the
agent.

### Settings that keep an investigation inside a small quota

These are ordinary configuration values, not code changes:

| Setting | Default | Small-quota value | Effect |
| --- | --- | --- | --- |
| `Agent:MaxToolCalls` | 10 | 2 | Caps the number of model round trips, which is the dominant cost. |
| `Llm:MaxTokens` | 1500 | 1300 | Counted against TPM even when unused, but a report cut off before its `### Agent Action` block loses the human-review answer, so the completion needs headroom to finish. |
| `Agent:MaxToolResultChars` | 4000 | 1500 | Shrinks evidence carried forward in the conversation. |
| `Agent:MaxExecutionSeconds` | 60 | 120 | Leaves room for a retry to wait out a full window. |

With these applied, investigations that previously failed with HTTP 429 complete
in 2–5 seconds. Raising the provider quota is the better long-term fix, since it
restores the deeper multi-step investigations that the default settings allow.
