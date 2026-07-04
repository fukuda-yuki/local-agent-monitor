# Sprint8 M4 Monitor Projection And Opt-in Raw Access Implementation Plan

> **For agentic workers:** Steps use checkbox (`- [ ]`) syntax for tracking.
> Implement task-by-task: write the test, watch it fail, implement, watch it
> pass, then commit.

**Goal:** Populate the sanitized `monitor_ingestions` / `monitor_traces`
projection tables M3 created empty, add a `ProjectionWorker` with startup
catch-up and failure isolation, expose the cursor-paginated `/api/monitor/*`
read API (no payload load), activate projection-lag readiness so `/health/ready`
can finally return `ready` / `degraded` / `not_ready`, and add the off-by-default
opt-in raw-detail route `GET /traces/{rawRecordId}/raw`.

**Architecture:** A second hosted `BackgroundService` — `ProjectionWorker` —
reads unprocessed `raw_records` (rows with no matching `monitor_ingestions` row),
builds sanitized projection values with a new `MonitorProjectionBuilder`
(`Telemetry/Monitoring/`, the `Monitoring/` split D019 deferred to M4), and
commits them through an idempotent persistence upsert. It is a *separate writer*
from M3's `IngestionWriterWorker`: `raw_records` keeps its single ingestion
writer, the projection tables get their single projection writer, and the two
serialize through WAL + `busy_timeout` with the projection writer retrying on
`SQLITE_BUSY` (exactly the model pinned in `raw-store-normalization.md`). Each
projection pass records backlog + the oldest-unprocessed timestamp into
`MonitorHealthState`, so the readiness endpoint computes live projection lag
without touching the DB on the health hot path. The HTTP layer gains a sanitized
read seam for `/api/monitor/ingestions` / `/api/monitor/traces` (projection
tables only — never `payload_json`) and, only when launched with
`--enable-raw-view`, the server-rendered `GET /traces/{rawRecordId}/raw` route.

**Tech Stack:** .NET 10, ASP.NET Core / Kestrel minimal APIs, `BackgroundService`,
`Microsoft.Data.Sqlite`, `System.Text.Encodings.Web.HtmlEncoder`, xUnit, the
existing `Telemetry` and `Persistence.Sqlite` internal assemblies.

---

## Source Of Truth

Use the repository instruction order from `AGENTS.md`. The governing current
product sources for M4 are:

1. `docs/requirements.md` — Local Ingestion Monitor goal, opt-in raw viewing,
   live-validation requirements.
2. `docs/spec.md` — Public Interfaces: `/api/monitor/ingestions`,
   `/api/monitor/traces`, `/health/ready`, the raw-detail route, sanitized-only
   guarantee.
3. `docs/specifications/layers/raw-store-normalization.md` — projection-table
   allowlist schema, projection worker, catch-up, retry/failure state, cursor
   query, opt-in raw access, projection-lag readiness.
4. `docs/specifications/layers/telemetry-ingestion.md` — readiness thresholds,
   readiness body, status⇒HTTP mapping, degraded reasons.
5. `docs/specifications/security-data-boundaries.md` — raw-detail route contract
   (flag gating, same-origin, `Cache-Control: no-store`, inert text rendering),
   sanitized `/api/monitor/*` + SSE, no raw/PII in logs or repo.
6. `docs/decisions.md` D020 / D019 — accepted scope, opt-in raw, two-worker DB
   model, the `Telemetry/Monitoring/` split deferred to M4.

Sprint-local files under `docs/sprints/` are planning evidence only. If this plan
conflicts with the source of truth above, stop and surface the conflict before
editing code.

## Scope

M4 owns:

- the sanitized `MonitorProjectionBuilder` (`Telemetry/Monitoring/`): one raw
  record ⇒ sanitized projection values, reusing `RawMeasurementNormalizer` for
  classification (`tool_call_count`, `error_count`, allowlisted attributes) plus
  a span count. **Only** the allowlist columns in
  `raw-store-normalization.md` are produced; raw prompt / response / tool content
  and PII (`user.id` / `user.email`) are never read into a projection value.
- persistence projection access on `RawTelemetryStore`: list-unprocessed (for the
  worker), an **idempotent** projection upsert (one new `monitor_ingestions` row
  per `raw_record_id`; `monitor_traces` aggregated exactly once per raw record),
  projection backlog / oldest-unprocessed status, cursor-paginated sanitized
  reads that never select `payload_json`, and a by-id raw fetch for the raw route.
- `ProjectionWorker` hosted service: startup catch-up of pre-existing unprocessed
  rows, steady-state projection, `SQLITE_BUSY` retry, projection-failure
  isolation (raw is never lost; the row is retried), and health updates
  (`projection_worker_running`, backlog, oldest-unprocessed timestamp).
- readiness activation in `MonitorHealthState`: projection worker running,
  projection lag (age of the oldest unprocessed `raw_records` row) and backlog,
  and the full `ready` / `degraded` / `not_ready` decision over **both** the
  ingestion-stall and projection-lag thresholds. M4 is the milestone where
  `/health/ready` can return `200` / `ready`.
- the sanitized cursor read API: `GET /api/monitor/ingestions` and
  `GET /api/monitor/traces` (`?after=<id>&limit=<n>`, `next_cursor`), projection
  tables only.
- the opt-in raw-detail route `GET /traces/{rawRecordId}/raw`: registered only
  with `--enable-raw-view` (absent ⇒ `404`); same-origin enforced
  (`Sec-Fetch-Site` / `Origin` cross-site ⇒ `403`); unknown id ⇒ `404`;
  `Cache-Control: no-store`; raw payload rendered as HTML-encoded inert text; raw
  never logged.
- the spec refinement that pins the new public surface (cursor response shape,
  degraded-state reason tokens, the now-active `ready` row) before coding.

M4 does **not** own:

- the Razor Web UI (Overview / Live Ingestions / Traces / Diagnostics) and the
  SSE notification stream — those are M5. M4 ships the JSON cursor API and the
  single server-rendered raw route the M5 UI will build on, nothing more.
- CSRF tokens / state-changing actions — M4 adds no state-changing endpoint, so
  CSRF (which the spec ties to state-changing actions) is introduced with the M5
  UI. The raw route is a `GET` guarded by same-origin.
- the full DR6 negative-test matrix sweep and live VS Code validation — M6.

## Two-writer DB model (design note, spec-backed)

M3 established a single writer for `raw_records`. M4 adds a **second** writer that
owns only the projection tables. This is not a regression of the single-writer
rule: `raw-store-normalization.md` pins *both* "a single ingestion writer worker
owns all writes [to `raw_records`]" *and* "the projection worker retries on
`SQLITE_BUSY`" — the retry requirement exists precisely because the projection
worker is a distinct writer. The two coexist via the already-configured WAL +
`busy_timeout = 5000` connection options (`RawTelemetryStoreConnectionOptions.MonitorWriter`);
SQLite serializes the two commit paths at the file lock, `busy_timeout` absorbs
the wait, and the projection worker additionally retries on a returned busy code.
Both workers commit short transactions; there is no cross-connection nested
transaction. External readers (`normalize-raw`, dashboard, diagnosis) keep
reading under WAL.

## Idempotency model

- `monitor_ingestions.raw_record_id` is `UNIQUE`. Projection inserts use
  `INSERT ... ON CONFLICT(raw_record_id) DO NOTHING`. The `monitor_traces`
  aggregate update is applied **only when** the ingestion insert actually
  inserted a new row (`changes() == 1`), inside the same transaction. A
  re-processed raw record (after a crash mid-batch or a restart catch-up) is a
  no-op and never double-counts.
- "Unprocessed" = `raw_records.id NOT IN (SELECT raw_record_id FROM monitor_ingestions)`.
- **Multi-trace fan-out (revised per Codex review).** `monitor_traces` is "one row
  per `trace_id`" (`raw-store-normalization.md`), and `RawMeasurementNormalizer`
  already emits one `MeasurementRow` per `traceId` in a payload. So a raw record
  produces **one** `monitor_ingestions` row (keyed on `raw_record_id`, its
  `trace_id` column carrying the record's primary trace reference, `span_count` =
  spans in the whole payload) **and one `monitor_traces` upsert per normalized
  `trace_id` in the payload** — not just the primary trace. Collapsing a
  multi-trace export to its primary trace would silently drop the 2nd+ traces and
  under-count `span_count` / `tool_call_count` / `error_count`, so it is **not**
  done. The `changes() == 1` guard below still makes every contribution of a raw
  record apply exactly once, so multi-trace fan-out does not double-count on
  re-processing.
- **Missing / blank `trace_id` is not a poison pill (per Codex review).** The
  receiver accepts OTLP payloads with no non-empty `traceId`
  (`RawOtlpIngestor.FindTraceId` may be null; `MeasurementRow.TraceId` is
  nullable) but `monitor_traces.trace_id` is `NOT NULL`. So projection **always**
  writes the `monitor_ingestions` row (its `trace_id` column is nullable and holds
  the record's primary trace reference or null, with the payload-wide
  `span_count`) and **skips the `monitor_traces` fan-out for any contribution
  whose `trace_id` is null or blank**. A valid raw record with no trace id is thus
  projected exactly once and never stalls the worker, grows projection lag, or
  forces readiness off `ready`. In a mixed payload, only the non-blank trace ids
  fan out.

## Target Files

- Create `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorProjectionBuilder.cs`
  and `MonitorRecordProjection.cs` (sanitized projection value types).
- Modify `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`
  for projection read/write/status helpers and sanitized cursor reads.
- Create `src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs`.
- Create `src/CopilotAgentObservability.LocalMonitor/Projection/IMonitorProjectionReader.cs`
  and a `RawTelemetryStore`-backed adapter (read seam mirroring M3's writer seam).
- Modify `src/CopilotAgentObservability.LocalMonitor/Health/MonitorHealthState.cs`
  for projection running / lag / backlog and the full readiness decision.
- Modify `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` to register
  the projection worker + reader, map `/api/monitor/*`, conditionally map the raw
  route, and evaluate readiness with both thresholds.
- Modify `docs/specifications/layers/telemetry-ingestion.md` and
  `docs/specifications/layers/raw-store-normalization.md` (Task 0 spec pin).
- Tests (LocalMonitor.Tests; both test projects already see `Telemetry` /
  `Persistence.Sqlite` internals):
  - Create `MonitorProjectionBuilderTests.cs`.
  - Create `MonitorProjectionStoreTests.cs`.
  - Create `ProjectionWorkerTests.cs`.
  - Create `MonitorProjectionApiTests.cs`.
  - Create `MonitorRawViewTests.cs`.
  - Modify `MonitorHealthTests.cs`, `MonitorHostTests.cs`.

Keep dependency direction unchanged: `Telemetry <- Persistence.Sqlite <- LocalMonitor`;
`LocalMonitor` must not reference `ConfigCli`. New types stay `internal` with the
existing `InternalsVisibleTo` friend lists.

## Implementation Tasks

### Task 0: Pin The M4 Public-Interface Contract In Specs

**Files:**
- Modify: `docs/specifications/layers/telemetry-ingestion.md`
- Modify: `docs/specifications/layers/raw-store-normalization.md`

- [ ] **Step 1: Pin the cursor API response shape.** In `telemetry-ingestion.md`
  add, under the Local Ingestion Monitor Receiver section, the response contract
  for `GET /api/monitor/ingestions` and `GET /api/monitor/traces`:
  - query: `after` (exclusive cursor; omitted ⇒ from start), `limit` (default
    `50`, max `200`; out-of-range / non-numeric ⇒ deterministic `400`). The
    ingestions cursor is `raw_record_id`; the traces cursor is the projection-row
    id. Each endpoint's `WHERE` / `ORDER BY` / `next_cursor` use that one key.
  - body: `{ "items": [ ...sanitized rows... ], "next_cursor": <id|null> }`,
    ordered by ascending id. `next_cursor` is **non-null only when more rows exist
    beyond this page**, determined by reading `limit + 1` rows and returning at
    most `limit`: if the surplus row exists, `next_cursor` = the last *returned*
    item's cursor id; otherwise `null`. A final page whose size is exactly `limit`
    (i.e. `N % limit == 0`) must therefore return `next_cursor: null`, and the
    next fetch must never be an empty page caused by a stale cursor.
  - the item field sets are exactly the sanitized allowlist columns from
    `raw-store-normalization.md` (no `payload_json`, no PII).
  - restate that the list query never loads raw payloads.
- [ ] **Step 2: Pin the degraded-state reason tokens.** In `telemetry-ingestion.md`
  extend the `degraded_reasons` description to distinguish *not_ready* tokens
  (`migration_failed`, `fatal_error`, `ingestion_stalled`,
  `projection_worker_missing`, `projection_lag_exceeded`) from *degraded* (`200`)
  tokens (`ingestion_backpressure` = momentary sub-threshold backpressure,
  `projection_lag` = lag in `(0, threshold)`), and state that the `ready` row is
  active once a projection worker runs with lag `0`.
- [ ] **Step 3: Cross-reference in `raw-store-normalization.md`.** Note that the
  cursor query reads the projection tables only (never `raw_records.payload_json`)
  and points to the response shape pinned in `telemetry-ingestion.md`. Also pin
  the **missing-trace rule**: a raw record with no non-empty `trace_id` is still
  projected into `monitor_ingestions` (nullable `trace_id`) but contributes **no**
  `monitor_traces` row (consistent with "one row per `trace_id`"), and never stays
  unprocessed / inflates projection lag.
- [ ] **Step 4: Commit**

```powershell
git add docs\specifications\layers\telemetry-ingestion.md docs\specifications\layers\raw-store-normalization.md
git commit -m "Sprint8 M4: docs: pin monitor projection api and readiness contract"
```

### Task 1: Sanitized Monitor Projection Builder

**Files:**
- Create: `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorRecordProjection.cs`
- Create: `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorProjectionBuilder.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionBuilderTests.cs`

- [ ] **Step 1: Write builder tests.** With synthetic OTLP JSON fixtures assert:
  - a chat + tool + error fixture yields `client_kind`, `experiment_id`,
    `task_id`, `task_category`, `agent_variant`, `prompt_version` from resource
    attributes, plus `span_count`, `tool_call_count`, `error_count` consistent
    with `RawMeasurementNormalizer`.
  - a fixture containing raw prompt / response / tool-argument strings and
    `user.id` / `user.email` resource attributes produces a projection whose
    serialized form contains **none** of those raw substrings or PII values
    (allowlist-only).
  - unknown / drifting span names do not throw and still produce a row.
  - **a multi-trace payload (two `traceId`s in one export) produces one trace
    contribution per `traceId`**, each with its own counts, and the
    `monitor_ingestions` `span_count` equals the total spans across both traces.
  - **a payload with no non-empty `traceId` produces an ingestion projection with
    a null `TraceId` and an empty trace-contribution collection** (no trace row
    will be written), so a trace-id-less record is still projectable.
- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionBuilderTests
```

Expected: compile failure — the builder does not exist.

- [ ] **Step 3: Implement the builder.** `MonitorRecordProjection` holds the
  sanitized ingestion fields (`TraceId`, `ClientKind`, `SpanCount`) **and a
  collection of trace contributions** — one per normalized `trace_id` — each with
  (`TraceId`, `ClientKind`, `ExperimentId`, `TaskId`, `TaskCategory`,
  `AgentVariant`, `PromptVersion`, `SpanCount`, `ToolCallCount`, `ErrorCount`).
  `MonitorProjectionBuilder.Build(RawTelemetryRecord)` normalizes the payload via
  `RawMeasurementNormalizer` and maps **every returned row with a non-blank
  `trace_id`** to a trace contribution (not only the primary trace), mapping the
  allowlisted measurement fields and counting spans per trace with a small
  dedicated pass (do **not** change `MeasurementRow` or the shared normalizer
  contract — ConfigCli's 300 tests must stay green). Rows whose `trace_id` is null
  or blank are excluded from the contribution collection (see the missing-trace
  rule above). The ingestion-level `TraceId` is the record's primary trace
  reference (may be null) and `SpanCount` is the payload-wide span total. Read
  only allowlisted keys; never copy raw payload text or PII.
- [ ] **Step 4: Run builder tests.** Expected: pass.
- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.Telemetry\Monitoring tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorProjectionBuilderTests.cs
git commit -m "Sprint8 M4: feat: add sanitized monitor projection builder"
```

### Task 2: Persistence Projection Read/Write Access

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionStoreTests.cs`

- [ ] **Step 1: Write store tests.** Against a real temp SQLite DB seeded with
  `raw_records` via `Insert`:
  - `ListUnprocessedForProjection(limit)` returns rows in id order, bounded by
    `limit`, excluding already-projected rows.
  - `ApplyProjection` inserts one `monitor_ingestions` row; re-applying the same
    raw record is a no-op (no duplicate row, no double-counted `monitor_traces`).
  - a **single raw record carrying two `trace_id`s fans out to two
    `monitor_traces` rows**, and re-applying that record does not double-count
    either trace.
  - a **raw record with no `trace_id`** is projected: one `monitor_ingestions`
    row (null `trace_id`), **zero** `monitor_traces` rows, and afterwards
    `GetProjectionStatus` backlog is `0` (it does not stay unprocessed / poison
    the worker).
  - two raw records sharing a `trace_id` aggregate in `monitor_traces`
    (`span_count` / `tool_call_count` / `error_count` summed; `first_seen_at` =
    min, `last_seen_at` = max).
  - `GetProjectionStatus` returns backlog count and the oldest unprocessed
    `received_at`; `0` / `null` when all caught up.
  - `ListMonitorIngestions(after, limit)` / `ListMonitorTraces(after, limit)`
    paginate by id and (asserted via a SQL guard / explicit column list) never
    select `payload_json` or any PII column.
  - **cursor terminal:** with exactly `limit` rows projected, the first page
    returns `limit` items and a `null` next cursor (the `limit + 1` probe finds no
    surplus); with `limit + 1` rows the first page's cursor is non-null and the
    second page returns the remainder then `null`.
  - **cursor domain (ingestions):** with `monitor_ingestions.id` deliberately
    diverged from `raw_record_id` (seed projection rows out of `raw_record_id`
    order), `ListMonitorIngestions` paginates by `raw_record_id` without skipping
    or repeating rows — the `next_cursor` value and the `WHERE` filter are the same
    key space.
  - `GetRawRecordById(id)` returns the raw record; unknown id ⇒ null.
  - projection rows expose only allowlist columns (assert the read DTO has no
    payload / resource-attributes / PII members).
- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorProjectionStoreTests
```

Expected: compile failure — the methods do not exist.

- [ ] **Step 3: Implement store methods.** Add to `RawTelemetryStore`:
  - `ListUnprocessedForProjection(int limit)` ⇒ full `RawTelemetryRecord`s
    (payload included; in-process projection input only) for unprojected rows.
  - `ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt)`
    in one transaction: `INSERT ... ON CONFLICT(raw_record_id) DO NOTHING` into
    `monitor_ingestions` (always — `trace_id` nullable); if `changes() == 1`,
    upsert **each non-blank-`trace_id` trace contribution** into `monitor_traces`
    (`ON CONFLICT(trace_id)` summing counts, `MIN`/`MAX` seen-at, `COALESCE`
    first-non-null metadata). A record with no usable trace id commits the
    ingestion row and fans out zero trace rows — it is never left unprocessed. The
    single `changes()` guard makes the whole fan-out idempotent. Returns whether
    newly projected.
  - `GetProjectionStatus()` ⇒ `(int Backlog, DateTimeOffset? OldestUnprocessedReceivedAt)`.
  - `ListMonitorIngestions(long afterRawRecordId, int limit)` ⇒ sanitized read
    DTOs, explicit allowlist `SELECT`,
    `WHERE raw_record_id > @after ORDER BY raw_record_id LIMIT @limit + 1` — the
    cursor key, `WHERE`, `ORDER BY`, and emitted `next_cursor` are all
    `raw_record_id` (the spec's cursor key), so a divergence between
    `monitor_ingestions.id` and `raw_record_id` cannot skip/repeat rows.
  - `ListMonitorTraces(long afterId, int limit)` ⇒ sanitized read DTOs, explicit
    allowlist `SELECT`, `WHERE id > @after ORDER BY id LIMIT @limit + 1` (traces
    use the projection-row id).
  - both list methods read up to `limit + 1` rows, return at most `limit`, and
    report whether a surplus row existed so the host sets `next_cursor` per the
    Task 0 terminal contract.
  - `GetRawRecordById(long id)` ⇒ `RawTelemetryRecord?`.
  Reuse the existing `OpenConnection` (WAL + `busy_timeout`). Classify
  `SqliteException` 5/6 as busy at the worker seam (Task 3), not here.
- [ ] **Step 4: Run store tests.** Expected: pass.
- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.Persistence.Sqlite\RawTelemetryStore.cs tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorProjectionStoreTests.cs
git commit -m "Sprint8 M4: feat: add monitor projection store access"
```

### Task 3: Projection Worker

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/Projection/IMonitorProjectionReader.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Health/MonitorHealthState.cs`
  (add projection setters used by the worker; full readiness decision is Task 4)
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/ProjectionWorkerTests.cs`

- [ ] **Step 1: Write worker tests.** Driving a single pass deterministically
  (expose an internal `RunProjectionPassAsync()` the test calls directly; the
  `ExecuteAsync` loop only adds delay/cancellation), assert:
  - pre-existing unprocessed rows are projected on the first pass (startup
    catch-up).
  - newly inserted rows are projected on a later pass; already-projected rows are
    not reprocessed.
  - a busy projection store result is retried (the row stays unprojected this
    pass, projected on a later pass) and the raw record is never lost.
  - a non-busy projection failure isolates the row: the raw record remains in
    `raw_records`, the worker keeps running, and health records a projection
    failure.
  - after a pass, health reflects `projection_worker_running = true`, backlog,
    and oldest-unprocessed timestamp.
- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~ProjectionWorkerTests
```

Expected: compile failure — the worker does not exist.

- [ ] **Step 3: Implement the worker.** `IMonitorProjectionReader` wraps a
  `RawTelemetryStore` (`MonitorWriter` connection options) and exposes
  list-unprocessed / apply-projection / get-status, translating
  `SqliteException` 5/6 into a typed busy outcome (mirroring M3's writer seam).
  `ProjectionWorker : BackgroundService` sets `projection_worker_running = true`,
  runs `RunProjectionPassAsync` (build via `MonitorProjectionBuilder`, apply,
  then push `GetProjectionStatus` into health), retries busy on the next pass,
  isolates non-busy failures (record failure, keep raw, continue), and loops with
  an injectable `TimeProvider` delay (default poll ~`1s`). Clears running on stop.
  A pass is a no-op until `migration_complete` (the ingestion writer owns the
  additive migration in its `StartAsync`); the projection worker reads that health
  flag and skips projecting until the projection tables exist, so worker
  registration order is not relied on for correctness.
- [ ] **Step 4: Run worker tests.** Expected: pass.
- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\Projection src\CopilotAgentObservability.LocalMonitor\Health\MonitorHealthState.cs tests\CopilotAgentObservability.LocalMonitor.Tests\ProjectionWorkerTests.cs
git commit -m "Sprint8 M4: feat: add projection worker"
```

### Task 4: Activate Projection Readiness

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/Health/MonitorHealthState.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHealthTests.cs`

- [ ] **Step 1: Write health tests.** With an injected `TimeProvider` assert the
  full M4 decision table:
  - healthy, worker running, lag `0` ⇒ `200` / `status=ready` / empty
    `degraded_reasons`.
  - lag in `(0, projection-lag-threshold)` ⇒ `200` / `status=degraded` /
    `projection_lag`.
  - lag `≥` threshold (default `60`, and an override) ⇒ `503` /
    `status=not_ready` / `projection_lag_exceeded`.
  - projection worker not running ⇒ `503` / `not_ready` /
    `projection_worker_missing`.
  - momentary backpressure (sub-threshold) ⇒ `200` / `degraded` /
    `ingestion_backpressure`; sustained ⇒ `503` / `not_ready` /
    `ingestion_stalled` (M3 behavior preserved).
  - `migration_failed` / `fatal_error` remain blocking.
  - body `checks` report `projection_worker_running`, `projection_lag_seconds`,
    `projection_backlog` truthfully.
- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorHealthTests
```

Expected: failures — readiness has no `ready`/`degraded`/projection-lag logic yet.

- [ ] **Step 3: Implement the decision.** Add `SetProjectionWorkerRunning`,
  `SetProjectionStatus(backlog, oldestUnprocessedReceivedAt)`, and
  `RecordProjectionFailure` (if surfaced) to `MonitorHealthState`. Change
  `Evaluate(int ingestionStallThresholdSeconds, int projectionLagThresholdSeconds)`
  to compute `projection_lag_seconds = oldest is null ? 0 : max(0, floor(now - oldest))`,
  classify blocking vs degraded vs healthy, and emit `status` (`not_ready` if any
  blocking; else `degraded` if any degraded reason; else `ready`) with the
  matching HTTP via the existing `IsReady`. Keep the JSON body shape identical;
  fill projection fields with real values.
- [ ] **Step 4: Run health tests.** Expected: pass.
- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\Health\MonitorHealthState.cs tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorHealthTests.cs
git commit -m "Sprint8 M4: feat: activate projection readiness"
```

### Task 5: Wire Worker + Health Into Host And Add The Cursor API

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionApiTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`

- [ ] **Step 1: Write API + host tests.** Over a real Kestrel host:
  - posting N valid traces and waiting for projection yields
    `GET /api/monitor/ingestions` items with the sanitized fields, ascending id,
    and working `after` / `limit` paging; assert the **exact-multiple terminal**
    case (`N` a multiple of `limit` ⇒ the last full page reports `next_cursor:
    null` and a follow-up fetch is not required to discover the end).
  - `GET /api/monitor/traces` aggregates per trace, including a **multi-trace
    single export** producing one row per `trace_id`.
  - neither API body contains raw prompt / response / tool content or PII even
    when the payload carried them.
  - `limit` out of range / non-numeric ⇒ `400`.
  - once projection catches up, `GET /health/ready` returns `200` / `ready`
    (the first time in the project's history).
  - the M3 readiness/queue/error tests still pass (regression).
  - add a deterministic projection-wait test helper (poll `/api/monitor/ingestions`
    or `/health/ready` with a bounded timeout) rather than fixed sleeps.
- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorProjectionApiTests|FullyQualifiedName~MonitorHostTests"
```

Expected: API tests fail (routes absent); `ready` assertion fails.

- [ ] **Step 3: Implement host wiring.** In `MonitorHost.Build`: register the
  `ProjectionWorker` and a projection reader (with a `MonitorHostTestOptions`
  seam mirroring M3 so tests can inject a fake reader / disable the worker);
  evaluate readiness with **both** thresholds; map `GET /api/monitor/ingestions`
  and `GET /api/monitor/traces` (parse `after`/`limit`, sanitized DTO ⇒ JSON,
  `next_cursor`). Do **not** add CORS (CORS-off is the default and a control).
  Keep all error bodies sanitized.
- [ ] **Step 4: Run tests.** Expected: pass.
- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor tests\CopilotAgentObservability.LocalMonitor.Tests
git commit -m "Sprint8 M4: feat: add monitor projection cursor api"
```

### Task 6: Opt-in Raw-Detail Route

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorRawViewTests.cs`

- [ ] **Step 1: Write raw-view tests.** Assert the DR6 contract:
  - without `--enable-raw-view`: `GET /traces/{id}/raw` ⇒ `404` (route absent);
    `/api/monitor/*` unaffected.
  - with `--enable-raw-view`: a known id returns `200`, `Content-Type: text/html`,
    `Cache-Control: no-store`, and the raw payload **HTML-encoded as inert text**
    — a stored `<script>` / `<img onerror>` / `javascript:` payload appears
    escaped (`&lt;script&gt;`), not as live markup (assert no unescaped `<script>`
    in the body).
  - cross-site request (`Sec-Fetch-Site: cross-site`) ⇒ `403`; foreign `Origin`
    ⇒ `403`; a same-origin / `Sec-Fetch-Site: none` top-level navigation ⇒ `200`.
  - unknown id ⇒ `404`.
  - with the flag on, `/api/monitor/*` still returns **no** raw / PII.
  - the raw payload value does not appear in captured log output.
- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorRawViewTests
```

Expected: failures — the route is unconditionally absent today.

- [ ] **Step 3: Implement the route.** When `options.EnableRawView`, map
  `GET /traces/{rawRecordId}/raw`: enforce same-origin first
  (reject `Sec-Fetch-Site` ∈ {`cross-site`,`same-site`} or a foreign `Origin`
  with `403`), fetch by id (unknown ⇒ `404`), set `Cache-Control: no-store` and
  `Content-Type: text/html; charset=utf-8`, and write a minimal server-rendered
  page with the payload wrapped via `HtmlEncoder.Default.Encode(...)` inside
  `<pre>` (no `Html.Raw`, no Razor). When the flag is off, do not map the route
  (fallthrough ⇒ existing `404`). Never log the raw payload.
- [ ] **Step 4: Run raw-view tests.** Expected: pass.
- [ ] **Step 5: Commit**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\MonitorHost.cs tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorRawViewTests.cs
git commit -m "Sprint8 M4: feat: add opt-in raw detail route"
```

### Task 7: Restart Catch-up And Projection Isolation Integration

**Files:**
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`

- [ ] **Step 1: Add integration tests.**
  - start a host, ingest traces, stop it, start a **new** host on the same DB ⇒
    the projection worker catches up the rows ingested while no projection had
    run (assert via `/api/monitor/ingestions`).
  - a concurrent external read transaction over the same DB does not block or
    lose projection writes (extends the M3 concurrent-reader test to the
    projection tables).
  - error / readiness bodies still exclude DB full path, Windows user name, and
    raw exception text.
- [ ] **Step 2: Run LocalMonitor tests.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj
```

Expected: pass.

- [ ] **Step 3: Commit**

```powershell
git add tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorHostTests.cs
git commit -m "Sprint8 M4: test: cover restart catch-up and projection isolation"
```

### Task 8: Update Tracking And Final Validation

**Files:**
- Modify: `docs/sprints/sprint8-local-raw-receiver-monitor/README.md`
- Modify: `docs/task.md`
- Create: `docs/sprints/sprint8-local-raw-receiver-monitor/milestones/M4-monitor-projection/review.md`

- [ ] **Step 1: Update tracking docs.** Mark M4 implemented only after code and
  tests pass. Keep M5 / M6 pending; note that the Razor UI, SSE, CSRF on
  state-changing actions, the full DR6 negative-test sweep, and live VS Code
  evidence remain M5 / M6.
- [ ] **Step 2: Run required validation.**

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Expected: build `0` errors / `0` warnings; tests pass with a count above the M3
baseline of `371`.

- [ ] **Step 3: Write `review.md`** covering source-of-truth comparison,
  dependency direction, the now-active readiness contract, raw / PII
  non-regression (projections + APIs sanitized, raw route gated + inert),
  commands run and results, Codex review checkpoints, and residual M5 / M6 risk.
- [ ] **Step 4: Commit**

```powershell
git add docs\sprints\sprint8-local-raw-receiver-monitor\README.md docs\task.md docs\sprints\sprint8-local-raw-receiver-monitor\milestones\M4-monitor-projection\review.md
git commit -m "Sprint8 M4: docs: record milestone review"
```

## Final Acceptance

M4 is complete only when:

- all tasks above are implemented.
- `dotnet build CopilotAgentObservability.slnx` succeeds with `0` warnings.
- `dotnet test CopilotAgentObservability.slnx` succeeds, count above `371`.
- `monitor_ingestions` / `monitor_traces` are populated by the projection worker
  with sanitized allowlist columns only; catch-up and idempotent re-processing
  are proven.
- `GET /api/monitor/ingestions` / `GET /api/monitor/traces` paginate by cursor,
  never load raw payloads, and never return raw / PII.
- `GET /health/ready` returns `200` / `ready` when caught up, `200` / `degraded`
  for sub-threshold backpressure or projection lag, and `503` / `not_ready` for
  sustained stall, projection-lag-exceeded, missing worker, migration failure, or
  fatal error — asserted at default and override thresholds with both HTTP status
  and body.
- `GET /traces/{rawRecordId}/raw` is absent (`404`) without `--enable-raw-view`;
  with it, it is same-origin-enforced (`403` cross-site), `Cache-Control:
  no-store`, renders raw as inert escaped text, and returns `404` for unknown ids.
- tests use **synthetic** raw/PII marker fixtures (never real captured Copilot
  payloads) to prove non-leakage; those marker values — and any raw / PII — never
  appear in the projection tables, the JSON APIs, the SSE-bound surface, logs,
  repository-safe outputs, or sprint records. The only surface that may return raw
  / PII is the flag-gated raw-detail route at runtime.

## Self-Review Notes

- **Spec coverage:** M4 implements the `raw-store-normalization.md` projection
  worker / catch-up / retry / cursor / opt-in raw access, the
  `telemetry-ingestion.md` projection-lag readiness and readiness body, the
  `security-data-boundaries.md` raw-detail route contract (flag gating,
  same-origin, `no-store`, inert text) and sanitized `/api/monitor/*`, and the
  D020 / D019 `Telemetry/Monitoring/` split. Task 0 pins the only new public
  detail (cursor response shape + degraded tokens) before coding.
- **Out of scope, on purpose:** Razor UI + SSE (M5), CSRF on state-changing
  actions (M5, none exist yet), DR6 full negative sweep + live VS Code (M6).
- **Risk watch:** two-writer DB contention (mitigated by WAL + `busy_timeout` +
  projection retry, documented above); `monitor_traces` double-counting
  (prevented by the `changes()==1` idempotency guard, which also makes the
  multi-trace fan-out idempotent); raw leakage (prevented structurally —
  projection columns are allowlist-only and the only raw surface is the
  flag-gated route).
- **Codex adversarial review (plan):** two rounds, four findings, all folded in.
  Round 1 — [high] multi-trace payloads must fan out to one `monitor_traces` row
  per `trace_id` (not collapse to the primary trace); [medium] cursor
  `next_cursor` must use a `limit + 1` probe so an exactly-full final page reports
  `null`. Round 2 — [high] a record with a missing / blank `trace_id` must not
  poison projection (still write the `monitor_ingestions` row, skip the
  `monitor_traces` fan-out, never stall the worker); [medium] the ingestions
  cursor must use `raw_record_id` for `WHERE` / `ORDER BY` / `next_cursor`
  consistently (no `monitor_ingestions.id` vs `raw_record_id` domain mix). Tests
  for all four are added to Tasks 0/1/2/5.
- **Type stability:** stable names — `MonitorProjectionBuilder`,
  `MonitorRecordProjection`, `IMonitorProjectionReader`, `ProjectionWorker`, the
  `RawTelemetryStore` projection methods, and the `MonitorHealthState`
  projection setters.
