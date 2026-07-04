# Sprint8 M3 Review — Ingestion Queue And SQLite Concurrency

Reviewer: Claude (orchestrator). Implementation by Claude (see note on Codex
delegation below). Outcome: **Accepted.**

## Summary

M3 replaces the M2 synchronous `SemaphoreSlim` write path with an ack-backed
bounded ingestion queue, a single SQLite writer worker, an additive
`schema_version` migration that creates the empty `monitor_ingestions` /
`monitor_traces` projection tables, graceful shutdown that drains accepted work,
and the first real `/health/live` and `/health/ready` endpoints. `/health/ready`
deliberately never returns `ready` in M3 because the projection worker is M4.

## Source-of-truth comparison

- `docs/specifications/layers/telemetry-ingestion.md` — ingestion error mapping
  (queue full `503`, commit timeout `504`, shutdown `503`, DB busy `503`),
  readiness thresholds (ingestion-stall `10`, projection-lag `60`), CLI flags +
  env fallbacks, machine-readable readiness body, and `status⇒HTTP` mapping all
  implemented as specified. `projection_worker_missing` was added to the
  `degraded_reasons` enumeration in this spec before implementation.
- `docs/specifications/layers/raw-store-normalization.md` — single writer, WAL +
  `busy_timeout`, additive `schema_version` migration, and the projection-table
  **allowlist column schema** (pinned in this spec for M3 before coding) are
  implemented. Projection tables are created empty; population is M4.
- `docs/decisions.md` D020 / replan DD1–DD6 — HTTP `2xx` only after commit,
  idempotent additive migration (failure ⇒ not-ready), `/v1/traces`-only,
  readiness non-2xx under sustained saturation — all honored.
- No source-of-truth conflict was found. The non-busy persistence failure path
  (`500`/`persistence_failed`) preserves M2 behavior and is documented in the
  plan; the spec's fixed-error list (busy/timeout/shutdown/queue-full) is
  unchanged.

## Dependency direction

`Telemetry <- Persistence.Sqlite <- LocalMonitor` is preserved.
`CopilotAgentObservability.LocalMonitor.csproj` references only `Telemetry` and
`Persistence.Sqlite`; it does not reference `ConfigCli`. New types stay
`internal` with `InternalsVisibleTo` for the test assembly. The persistence layer
gained only `RawTelemetryStore.CreateMonitorSchema` (additive); the writer seam
(`IRawTelemetryWriter` + adapter) lives in `LocalMonitor`, so Persistence's
surface did not grow toward the monitor.

## Health / readiness contract

- `/health/live` ⇒ fixed `200`.
- `/health/ready` ⇒ machine-readable body with `status` / `checks` /
  `degraded_reasons`; `not_ready` ⇒ `503`. In M3 it is always `not_ready`:
  ingestion-healthy ⇒ `projection_worker_missing`; sustained backpressure at/after
  the stall threshold ⇒ `ingestion_stalled`; failed migration ⇒ `migration_failed`;
  fatal worker error ⇒ `fatal_error`. Threshold + clock are injectable, so default
  and override threshold tests assert without wall-clock sleeps.
- Commit-timeout responses also start the stall window, so a hung writer emitting
  repeated `504`s is reflected in readiness (fix from Codex review).

## Raw / PII non-regression

- Projection tables carry only the sanitized allowlist columns; no `payload_json`,
  `resource_attributes_json`, or PII (`user.id` / `user.email`) columns — asserted
  by a dedicated test.
- The worker persists already-validated `RawTelemetryRecord` values only; no raw
  body / path / query / exception text is logged.
- Error response bodies are fixed strings; a test asserts a failure body excludes
  the DB path, the Windows user name, and raw exception text.
- The raw-detail route `GET /traces/{rawRecordId}/raw` remains absent (`404`).

## Commands run

- `dotnet build CopilotAgentObservability.slnx` ⇒ **0 errors, 0 warnings**.
- `dotnet test CopilotAgentObservability.slnx` ⇒ **371 passing, 0 failing,
  0 skipped** (300 Config CLI + 71 LocalMonitor), above the M2 baseline of 322.

## Codex review checkpoints

Per the repository workflow, Codex was used for read-only adversarial review at
checkpoints (the local Codex sandbox cannot write files on this machine, so Claude
implemented and Codex reviewed — see the `codex-delegation-policy` memory). Two
findings were accepted and fixed:

1. Worker did not complete the ack / stayed alive on an *unexpected* (non-busy /
   non-failed) writer exception ⇒ added a per-request catch-all (request
   isolation). Commit `Sprint8 M3: fix: isolate unexpected ingestion writer errors`.
2. Commit-timeout path returned `504` without starting the readiness stall window
   ⇒ records backpressure on timeout. Commit `Sprint8 M3: fix: surface commit
   timeouts in readiness stall window`.

The earlier "writer not wired into the host" finding was the planned Task 4
wiring and was resolved there.

## Residual risk (M4–M6)

M3 is not a shippable monitor. Deferred: projection population + the
`ProjectionWorker` (and projection-lag readiness becoming active), the
`/api/monitor/*` cursor API, the Web UI + SSE, the opt-in raw-detail route and
its same-origin / CSRF controls, the full DR6 negative security matrix, and live
VS Code HTTP/protobuf evidence (Sprint8 completion gate). `/health/ready` will
flip its healthy row to `200` / `ready` only when M4 adds the running projection
worker and projection-lag calculation.
