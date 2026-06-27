# Sprint8 M3 Ingestion Queue And SQLite Concurrency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the M2 synchronous LocalMonitor write path with a bounded ingestion queue, single SQLite writer worker, additive schema migration, graceful shutdown, and the first real `/health/*` surface without falsely reporting the monitor as fully ready before M4 projection exists.

**Architecture:** `MonitorHost` accepts `POST /v1/traces`, decodes and validates the payload, then enqueues an ack-backed write request into a bounded `System.Threading.Channels` queue. A hosted `IngestionWriterWorker` owns all SQLite writes and migration state; HTTP responses wait for commit ack so `2xx` is still returned only after raw-store commit. Health reads shared writer/migration/backpressure state and returns the pinned machine-readable readiness body; in M3 `/health/ready` remains `not_ready` because the projection worker is deliberately deferred to M4.

**Tech Stack:** .NET 10, ASP.NET Core/Kestrel, `BackgroundService`, `System.Threading.Channels`, `Microsoft.Data.Sqlite`, xUnit, existing `Telemetry` and `Persistence.Sqlite` internal assemblies.

---

## Source Of Truth

Use the repository instruction order from `AGENTS.md`. The governing current product sources for M3 are:

1. `docs/requirements.md` - Local Ingestion Monitor goal and validation requirements.
2. `docs/spec.md` - Local Ingestion Monitor public endpoints and readiness public interface.
3. `docs/specifications/layers/telemetry-ingestion.md` - receiver, queue error mapping, health thresholds, readiness body.
4. `docs/specifications/layers/raw-store-normalization.md` - single writer, WAL, migration, concurrent reader compatibility.
5. `docs/specifications/security-data-boundaries.md` - loopback, Host validation, request/error/log safety boundary.
6. `docs/decisions.md` D020 - accepted queue/readiness and local trust decisions.

Sprint-local files under `docs/sprints/` are planning evidence only. If this plan conflicts with the source of truth above, stop and surface the conflict before editing code.

## Scope

M3 owns:

- bounded ingestion queue with explicit full/backpressure behavior.
- single hosted SQLite writer worker replacing M2's `SemaphoreSlim` write path.
- commit ack / timeout / shutdown mapping: success only after commit, queue full `503`, commit timeout `504`, shutdown `503`, DB busy after retry `503`.
- `schema_version` table and idempotent additive migration that creates the empty `monitor_ingestions` and `monitor_traces` projection tables, as required by the current raw-store specification.
- WAL and `busy_timeout` retained for concurrent external readers.
- graceful shutdown that rejects new work and drains queued accepted work.
- first real `/health/live` and `/health/ready` endpoints. In M3, `/health/ready` never returns `ready` because `projection_worker_running=false`; it can still surface ingestion-specific `degraded` and `not_ready` details in the required body.
- readiness threshold configuration:
  `--ingestion-stall-threshold-seconds` / `CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS` and `--projection-lag-threshold-seconds` / `CAO_MONITOR_PROJECTION_LAG_THRESHOLD_SECONDS`.

M3 does not own:

- population of `monitor_ingestions` / `monitor_traces`, the projection worker, cursor API, and projection lag calculation. Those are M4. M3 creates the empty tables only because the current migration specification requires the additive schema to include them.
- `/api/monitor/*`, SSE, Razor UI, or diagnostics pages. Those are M4/M5.
- `GET /traces/{rawRecordId}/raw`. The raw-detail route remains absent in M3.
- the full DR6 negative matrix and live VS Code validation. Those remain M6 gates.

Because the product readiness body already includes projection fields, M3 returns a truthful partial state: `projection_worker_running=false`, `projection_lag_seconds=0`, and `projection_backlog=0` until M4 adds the projection worker. `GET /health/ready` maps that state to `503` / `status=not_ready` with `degraded_reasons` containing `projection_worker_missing`. Do not fake a running projection worker or return `ready` before M4.

## Target Files

- Modify `src/CopilotAgentObservability.LocalMonitor/MonitorOptions.cs` for ingestion and projection threshold options and env fallbacks.
- Modify `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` to wire DI, hosted worker, queue, shutdown state, and health endpoints.
- Create `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionQueue.cs` for bounded channel enqueue behavior and ack model.
- Create `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionWriteRequest.cs` for decoded record + commit result coordination.
- Create `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionWriterWorker.cs` for the single writer hosted service.
- Create `src/CopilotAgentObservability.LocalMonitor/Health/MonitorHealthState.cs` for readiness state and threshold evaluation.
- Modify `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs` for schema-version migration helpers and creation of empty monitor projection tables.
- Modify `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorOptionsTests.cs`.
- Modify `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`.
- Add focused LocalMonitor test files if `MonitorHostTests.cs` becomes too large: `IngestionQueueTests.cs`, `IngestionWriterWorkerTests.cs`, and `MonitorHealthTests.cs`.

Keep dependency direction unchanged: `Telemetry <- Persistence.Sqlite <- LocalMonitor`; `LocalMonitor` must not reference `ConfigCli`.

## Implementation Tasks

### Task 1: Add Threshold Options

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorOptions.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorOptionsTests.cs`

- [ ] **Step 1: Write option tests**

Add tests that assert:

- defaults include `IngestionStallThresholdSeconds = 10`.
- defaults include `ProjectionLagThresholdSeconds = 60`.
- `--ingestion-stall-threshold-seconds 3` overrides the default.
- `--projection-lag-threshold-seconds 7` overrides the default.
- `CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS=4` is used when the CLI flag is absent.
- `CAO_MONITOR_PROJECTION_LAG_THRESHOLD_SECONDS=8` is used when the CLI flag is absent.
- non-positive and non-numeric values fail with deterministic parse errors.
- duplicate threshold options are rejected deterministically.

- [ ] **Step 2: Run the targeted tests and confirm failure**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorOptionsTests
```

Expected: new tests fail because the threshold properties do not exist.

- [ ] **Step 3: Implement the option**

Extend `MonitorOptions` with:

- `IngestionStallThresholdSeconds`, default `10`, env fallback `CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS`.
- `ProjectionLagThresholdSeconds`, default `60`, env fallback `CAO_MONITOR_PROJECTION_LAG_THRESHOLD_SECONDS`.

Use positive integer validation and duplicate-option rejection matching the existing option parse style. In M3 the projection threshold is stored and emitted in health checks but projection lag remains `0` until M4 implements the projection worker.

- [ ] **Step 4: Run the targeted tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorOptionsTests
```

Expected: all `MonitorOptionsTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\MonitorOptions.cs tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorOptionsTests.cs
git commit -m "Sprint8 M3: feat: add ingestion stall threshold option"
```

### Task 2: Introduce Ack-Backed Ingestion Queue

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionWriteRequest.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionQueue.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/IngestionQueueTests.cs`

- [ ] **Step 1: Write queue tests**

Cover these behaviors with tiny synthetic `RawTelemetryRecord` instances:

- enqueue succeeds when capacity is available.
- enqueue returns `false` immediately when bounded capacity is full. Use capacity `1` in the test, enqueue one request without completing it, then assert the second enqueue fails.
- completing a request with a raw record id releases the awaiting HTTP side.
- completing a request with an error maps to a typed failure result.
- completing the queue rejects future enqueue attempts.

- [ ] **Step 2: Run the tests and confirm failure**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~IngestionQueueTests
```

Expected: compile failure because the queue types do not exist.

- [ ] **Step 3: Implement queue types**

Use `Channel.CreateBounded<IngestionWriteRequest>` with default capacity `1024`, `BoundedChannelFullMode.Wait`, and a public `TryEnqueue` method implemented with `Writer.TryWrite` so full queues become deterministic `503` instead of request-thread blocking. Provide an internal constructor overload that accepts capacity so tests can use capacity `1`. Do not add a public CLI/env queue-capacity option in M3.

Each request should carry:

- `RawTelemetryRecord Record`
- `TaskCompletionSource<IngestionCommitResult>` created with `TaskCreationOptions.RunContinuationsAsynchronously`
- enqueue timestamp from `TimeProvider.GetUtcNow()` for health/backpressure accounting

- [ ] **Step 4: Run queue tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~IngestionQueueTests
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\Ingestion tests\CopilotAgentObservability.LocalMonitor.Tests\IngestionQueueTests.cs
git commit -m "Sprint8 M3: feat: add ingestion queue"
```

### Task 3: Add Single SQLite Writer Worker

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionWriterWorker.cs`
- Create/Modify: `src/CopilotAgentObservability.LocalMonitor/Health/MonitorHealthState.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/IngestionWriterWorkerTests.cs`

- [ ] **Step 1: Write worker tests**

Cover:

- worker creates schema once at startup.
- worker inserts queued records in order and returns committed ids.
- `SqliteException` busy-like failures return a typed busy result.
- non-busy persistence failures mark health fatal or commit-failing without leaking raw exception text.
- worker drains already accepted queue items during shutdown.

- [ ] **Step 2: Run tests and confirm failure**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~IngestionWriterWorkerTests
```

Expected: compile failure because the worker does not exist.

- [ ] **Step 3: Implement worker**

Implement `BackgroundService` that:

- owns one `RawTelemetryStore` configured with `RawTelemetryStoreConnectionOptions.MonitorWriter`.
- runs migration/schema setup before marking the writer healthy.
- reads queue requests sequentially.
- calls `RawTelemetryStore.Insert`.
- completes each ack with committed raw record id or a typed failure that distinguishes DB-busy (after `busy_timeout` / retry) from non-busy persistence failure, so the HTTP layer can map `503` vs `500`.
- updates `MonitorHealthState` for writer running, migration complete, last commit success/failure, and queue-full/backpressure timestamps.
- receives `TimeProvider.System` from DI in production and accepts a test `TimeProvider` so health threshold tests can advance time without sleeping.

Keep M2's per-request decode logic outside the worker; the worker only persists already validated `RawTelemetryRecord` values.

- [ ] **Step 4: Run worker tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~IngestionWriterWorkerTests
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\Ingestion src\CopilotAgentObservability.LocalMonitor\Health src\CopilotAgentObservability.Persistence.Sqlite\RawTelemetryStore.cs tests\CopilotAgentObservability.LocalMonitor.Tests\IngestionWriterWorkerTests.cs
git commit -m "Sprint8 M3: feat: add single ingestion writer"
```

### Task 4: Wire Queue Into `POST /v1/traces`

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`

- [ ] **Step 1: Write endpoint tests**

Extend real Kestrel tests to assert:

- valid JSON/protobuf still persists and returns `{"accepted":true,"rawRecordId":...}`.
- response is not returned before commit ack. Use a controllable test queue/worker injection point or test-only delayed store factory.
- full queue returns `503` / `queue_full` and writes no record.
- commit timeout returns `504` / `commit_timeout` after `5s`. Use an internal test host option to override the timeout to `50ms`; do not expose commit timeout as a public CLI/env option in M3.
- shutdown returns `503` / `shutting_down`.
- DB busy after retry returns `503` / `persistence_busy`.
- non-busy persistence failure returns `500` / `persistence_failed` (preserving M2 behavior) with a sanitized body.
- `/traces/{id}/raw` remains `404`.

- [ ] **Step 2: Run endpoint tests and confirm failure**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorHostTests
```

Expected: new queue-specific tests fail under the M2 synchronous write path.

- [ ] **Step 3: Replace synchronous write path**

In `MonitorHost.Build`:

- register `IngestionQueue`, `MonitorHealthState`, and `IngestionWriterWorker`.
- remove local `RawTelemetryStore` creation and `SemaphoreSlim` write gate from request mapping.
- keep content-type, size-limit, Host-header, unsupported-path, and sanitized error behavior unchanged.
- after decoding, create `RawTelemetryRecord` and call `TryEnqueue`.
- if enqueue fails, write `503` `queue_full`.
- await commit ack for `5s`; on timeout write `504` `commit_timeout`. Keep the timeout as an internal constant/test-overridable host setting, not a public interface.
- on a failed ack, map DB-busy to `503` `persistence_busy` and non-busy persistence failure to `500` `persistence_failed`; bodies stay sanitized (no DB path, user name, or exception text).
- on successful ack write `200` with raw record id.

- [ ] **Step 4: Run endpoint tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorHostTests
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor tests\CopilotAgentObservability.LocalMonitor.Tests
git commit -m "Sprint8 M3: feat: queue monitor ingestion requests"
```

### Task 5: Add Schema Version Migration

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/RawTelemetryStoreTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/IngestionWriterWorkerTests.cs`

- [ ] **Step 1: Write migration tests**

Cover:

- opening a raw-records-only DB adds `schema_version`.
- opening a raw-records-only DB adds empty `monitor_ingestions` and `monitor_traces` projection tables with the allowlist columns from `raw-store-normalization.md` (assert the column set; no raw/PII columns).
- running migration twice is idempotent.
- existing `raw_records` rows remain readable by `normalize-raw` / `ListRecords`.
- migration failure is surfaced to `MonitorHealthState` as not ready.

- [ ] **Step 2: Run tests and confirm failure**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~RawTelemetryStoreTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~IngestionWriterWorkerTests
```

Expected: new migration tests fail before migration helpers exist.

- [ ] **Step 3: Implement migration**

Add an internal monitor migration method on the persistence layer, called by the M3 writer startup, that:

- creates `schema_version` if absent.
- records a monitor schema version row.
- creates empty `monitor_ingestions` and `monitor_traces` tables using the exact per-table allowlist columns defined in the "Projection table allowlist schema" section of `raw-store-normalization.md` (sanitized columns only; no raw payload, no PII).
- is additive only and preserves existing `raw_records`.
- does not populate `monitor_ingestions` or `monitor_traces`; M4 owns projection population, cursor queries, and projection retry/failure state.

- [ ] **Step 4: Run migration tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~RawTelemetryStoreTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~IngestionWriterWorkerTests
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.Persistence.Sqlite\RawTelemetryStore.cs tests\CopilotAgentObservability.ConfigCli.Tests\RawTelemetryStoreTests.cs tests\CopilotAgentObservability.LocalMonitor.Tests\IngestionWriterWorkerTests.cs
git commit -m "Sprint8 M3: feat: add monitor schema migration"
```

### Task 6: Implement Health Endpoints

**Files:**
- Modify/Create: `src/CopilotAgentObservability.LocalMonitor/Health/MonitorHealthState.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHealthTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`

- [ ] **Step 1: Write health tests**

Cover exact contract:

- `GET /health/live` returns `200`.
- `GET /health/ready` returns `503` + `status=not_ready` + `projection_worker_missing` when loopback bound, DB open, migration complete, writer running, no fatal error, and ingestion accepting, because M4 has not added the projection worker yet.
- momentary queue full/backpressure is reflected in the body, but while the projection worker is absent the overall M3 response remains `503` + `status=not_ready` with `projection_worker_missing`.
- sustained queue full or commit failure at/after default `10s` returns `503` + `status=not_ready` + `ingestion_stalled`.
- configured override threshold is honored.
- migration failure returns `503` + `migration_failed`.
- body contains `checks.loopback_bound`, `db_open`, `migration_complete`, `writer_running`, `projection_worker_running`, `ingestion_accepting`, `projection_lag_seconds`, and `projection_backlog`.
- default `10s` and override threshold tests use an injected `TimeProvider` so they do not sleep for wall-clock seconds.

- [ ] **Step 2: Run health tests and confirm failure**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorHealthTests|FullyQualifiedName~MonitorHostTests"
```

Expected: failures because M2 returned `404` for health endpoints.

- [ ] **Step 3: Implement health endpoints**

Register:

- `GET /health/live`: fixed `200` JSON body indicating process responsiveness.
- `GET /health/ready`: evaluates `MonitorHealthState` and emits the pinned body.

Use exact `status` values `ready`, `degraded`, `not_ready`; map `ready` and `degraded` to `200`, `not_ready` to `503`. Use `degraded_reasons` values from the spec where applicable: `ingestion_stalled`, `projection_lag_exceeded`, `migration_failed`, `fatal_error`; M3 also emits `projection_worker_missing` so the body explains why a queue-healthy M3 monitor is still not fully ready. In M3, projection checks are present in the body but do not claim a running projection worker.

Readiness decision table for M3:

| Condition | HTTP | `status` | Required reason |
| --- | --- | --- | --- |
| live endpoint | `200` | n/a | n/a |
| writer healthy, migration complete, ingestion accepting, projection worker absent | `503` | `not_ready` | `projection_worker_missing` |
| queue/backpressure shorter than ingestion threshold, projection worker absent | `503` | `not_ready` | `projection_worker_missing` plus backpressure detail |
| queue/backpressure at or beyond ingestion threshold | `503` | `not_ready` | `ingestion_stalled` |
| migration failure | `503` | `not_ready` | `migration_failed` |
| fatal worker error | `503` | `not_ready` | `fatal_error` |

M3 does not have a `ready` case. M4 changes the healthy row to `200` / `ready` after it adds a running projection worker and projection-lag calculation.

- [ ] **Step 4: Run health tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorHealthTests|FullyQualifiedName~MonitorHostTests"
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor tests\CopilotAgentObservability.LocalMonitor.Tests
git commit -m "Sprint8 M3: feat: add monitor health endpoints"
```

### Task 7: Validate Concurrent Reader And Shutdown Behavior

**Files:**
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/IngestionWriterWorkerTests.cs`

- [ ] **Step 1: Add integration tests**

Cover:

- an external SQLite read transaction over the same DB does not corrupt or lose monitor writes.
- queued accepted requests drain during graceful shutdown.
- new requests during shutdown return `503` and do not write records.
- all fixed error bodies exclude DB full path, Windows user name, and raw exception text.

- [ ] **Step 2: Run LocalMonitor tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj
```

Expected: pass.

- [ ] **Step 3: Commit**

```powershell
git add tests\CopilotAgentObservability.LocalMonitor.Tests
git commit -m "Sprint8 M3: test: cover shutdown and concurrent readers"
```

### Task 8: Update Sprint Tracking And Final Validation

**Files:**
- Modify: `docs/sprints/sprint8-local-raw-receiver-monitor/README.md`
- Modify: `docs/task.md`
- Create: `docs/sprints/sprint8-local-raw-receiver-monitor/milestones/M3-ingestion-queue-sqlite-concurrency/review.md`

- [ ] **Step 1: Update tracking docs**

Mark M3 implemented only after the code and tests above pass. Keep M4-M6 pending. Note that M3 is still not a shippable monitor because projections, API/UI/SSE, raw-detail route, full security matrix, and live VS Code evidence remain later milestones.

- [ ] **Step 2: Run required validation**

Run:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Expected: build has 0 errors and 0 warnings; tests pass with a count greater than the M2 baseline of 322 passing / 0 failing / 0 skipped.

- [ ] **Step 3: Write review record**

Create `review.md` summarizing:

- source-of-truth comparison.
- dependency direction check.
- health/readiness contract check.
- raw/PII non-regression check.
- commands run and results.
- residual M4-M6 risks.

- [ ] **Step 4: Commit**

```powershell
git add docs\sprints\sprint8-local-raw-receiver-monitor\README.md docs\task.md docs\sprints\sprint8-local-raw-receiver-monitor\milestones\M3-ingestion-queue-sqlite-concurrency\review.md
git commit -m "Sprint8 M3: docs: record milestone review"
```

## Final Acceptance

M3 is complete only when:

- all M3 tasks above are implemented.
- `dotnet build CopilotAgentObservability.slnx` succeeds.
- `dotnet test CopilotAgentObservability.slnx` succeeds.
- `POST /v1/traces` still returns success only after commit.
- queue full, commit timeout, shutdown, and DB busy paths return the specified fixed status codes and sanitized bodies.
- `/health/live` and `/health/ready` exist and match the M3 readiness schema, including `503` / `not_ready` while projection worker is absent.
- raw-detail route remains absent.
- no raw prompt/response/tool content or PII is added to logs, repo-safe outputs, sprint records, or tests.

## Self-Review Notes

- Spec coverage: M3 covers DD1 queue/ack/error mapping, DD2 schema-version/migration foundation including empty projection table creation, DR2 concurrent DB reads, and DD5 ingestion readiness without falsely returning `ready`. M4 remains responsible for projection population, cursor APIs, projection worker behavior, and projection-lag readiness becoming fully active.
- Placeholder scan: every task has explicit files, commands, expected outcomes, and commit message.
- Type consistency: names used throughout are stable: `IngestionQueue`, `IngestionWriteRequest`, `IngestionWriterWorker`, `MonitorHealthState`, `IngestionCommitResult`.
