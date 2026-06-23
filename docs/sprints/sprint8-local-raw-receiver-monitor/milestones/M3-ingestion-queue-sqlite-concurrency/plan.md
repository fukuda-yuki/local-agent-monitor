# Sprint8 M3 Ingestion Queue And SQLite Concurrency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the M2 synchronous LocalMonitor write path with a bounded ingestion queue, single SQLite writer worker, additive schema migration, graceful shutdown, and the first real `/health/*` readiness surface.

**Architecture:** `MonitorHost` accepts `POST /v1/traces`, decodes and validates the payload, then enqueues an ack-backed write request into a bounded `System.Threading.Channels` queue. A hosted `IngestionWriterWorker` owns all SQLite writes and migration state; HTTP responses wait for commit ack so `2xx` is still returned only after raw-store commit. Health reads shared worker/migration/backpressure state and returns the pinned machine-readable readiness body.

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
- `schema_version` table and idempotent additive migration for the monitor schema baseline.
- WAL and `busy_timeout` retained for concurrent external readers.
- graceful shutdown that rejects new work and drains queued accepted work.
- first real `/health/live` and `/health/ready` endpoints.
- ingestion-stall readiness threshold configuration:
  `--ingestion-stall-threshold-seconds` and `CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS`.

M3 does not own:

- `monitor_ingestions` / `monitor_traces` sanitized projection tables, projection worker, cursor API, or projection lag calculation. Those are M4.
- `/api/monitor/*`, SSE, Razor UI, or diagnostics pages. Those are M4/M5.
- `GET /traces/{rawRecordId}/raw`. The raw-detail route remains absent in M3.
- the full DR6 negative matrix and live VS Code validation. Those remain M6 gates.

Because the product readiness body already includes projection fields, M3 returns a truthful partial state: `projection_worker_running` is `false`, `projection_lag_seconds` is `0`, and `projection_backlog` is `0` until M4 adds the projection worker. Do not fake a running projection worker.

## Target Files

- Modify `src/CopilotAgentObservability.LocalMonitor/MonitorOptions.cs` for ingestion threshold options and env fallback.
- Modify `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` to wire DI, hosted worker, queue, shutdown state, and health endpoints.
- Create `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionQueue.cs` for bounded channel enqueue behavior and ack model.
- Create `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionWriteRequest.cs` for decoded record + commit result coordination.
- Create `src/CopilotAgentObservability.LocalMonitor/Ingestion/IngestionWriterWorker.cs` for the single writer hosted service.
- Create `src/CopilotAgentObservability.LocalMonitor/Health/MonitorHealthState.cs` for readiness state and threshold evaluation.
- Modify `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs` for schema-version migration helpers, if the existing store cannot express them cleanly.
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
- `--ingestion-stall-threshold-seconds 3` overrides the default.
- `CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS=4` is used when the CLI flag is absent.
- non-positive and non-numeric values fail with deterministic parse errors.
- unknown projection threshold options are not accepted in M3 unless implementation explicitly models them as inert future options.

- [ ] **Step 2: Run the targeted tests and confirm failure**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorOptionsTests
```

Expected: new tests fail because the option and property do not exist.

- [ ] **Step 3: Implement the option**

Extend `MonitorOptions` with `IngestionStallThresholdSeconds`, default `10`, env fallback `CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS`, positive integer validation, and duplicate-option rejection matching the existing option parse style.

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
- enqueue returns `false` immediately when bounded capacity is full.
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

Use `Channel.CreateBounded<IngestionWriteRequest>` with a small internal default capacity such as `1024`, `BoundedChannelFullMode.Wait`, and a public `TryEnqueue` method implemented with `Writer.TryWrite` so full queues become deterministic `503` instead of request-thread blocking.

Each request should carry:

- `RawTelemetryRecord Record`
- `TaskCompletionSource<IngestionCommitResult>` created with `TaskCreationOptions.RunContinuationsAsynchronously`
- enqueue timestamp for health/backpressure accounting

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
- runs migration/schema setup before accepting ready state.
- reads queue requests sequentially.
- calls `RawTelemetryStore.Insert`.
- completes each ack with committed raw record id or typed failure.
- updates `MonitorHealthState` for writer running, migration complete, last commit success/failure, and queue-full/backpressure timestamps.

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
- commit timeout returns `504` / `commit_timeout`.
- shutdown returns `503` / `shutting_down`.
- DB busy after retry returns `503` / `persistence_busy`.
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
- await commit ack with a bounded timeout; on timeout write `504` `commit_timeout`.
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
- is additive only and preserves existing `raw_records`.
- does not add `monitor_ingestions` or `monitor_traces`; M4 owns those projection tables.

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
- `GET /health/ready` returns `200` + `status=ready` when loopback bound, DB open, migration complete, writer running, no fatal error, and ingestion accepting.
- momentary queue full/backpressure returns `200` + `status=degraded` + reason.
- sustained queue full or commit failure at/after default `10s` returns `503` + `status=not_ready` + `ingestion_stalled`.
- configured override threshold is honored.
- migration failure returns `503` + `migration_failed`.
- body contains `checks.loopback_bound`, `db_open`, `migration_complete`, `writer_running`, `projection_worker_running`, `ingestion_accepting`, `projection_lag_seconds`, and `projection_backlog`.

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

Use exact `status` values `ready`, `degraded`, `not_ready`; map `ready` and `degraded` to `200`, `not_ready` to `503`. Use `degraded_reasons` values from the spec: `ingestion_stalled`, `projection_lag_exceeded`, `migration_failed`, `fatal_error` where applicable. In M3, projection checks are present in the body but do not claim a running projection worker.

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
- `/health/live` and `/health/ready` exist and match the readiness schema.
- raw-detail route remains absent.
- no raw prompt/response/tool content or PII is added to logs, repo-safe outputs, sprint records, or tests.

## Self-Review Notes

- Spec coverage: M3 covers DD1 queue/ack/error mapping, DD2 schema-version/migration foundation, DR2 concurrent DB reads, and DD5 ingestion readiness. M4 remains responsible for projection tables and projection-lag readiness.
- Placeholder scan: every task has explicit files, commands, expected outcomes, and commit message.
- Type consistency: names used throughout are stable: `IngestionQueue`, `IngestionWriteRequest`, `IngestionWriterWorker`, `MonitorHealthState`, `IngestionCommitResult`.
