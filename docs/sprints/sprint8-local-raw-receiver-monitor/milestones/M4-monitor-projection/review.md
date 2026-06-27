# Sprint8 M4 Review — Monitor Projection And Opt-in Raw Access

Reviewer: Claude (orchestrator). Implementation by Claude (Codex sandbox on this
machine is read-only — see the `codex-delegation-policy` memory; Codex was used
for read-only adversarial review at the plan and implementation checkpoints).
Outcome: **Accepted.**

## Summary

M4 turns the empty M3 projection tables into a populated, sanitized monitor:
a `MonitorProjectionBuilder` (`Telemetry/Monitoring/`), an idempotent persistence
projection path, a `ProjectionWorker` (startup catch-up, `SQLITE_BUSY` retry,
failure isolation), the sanitized cursor read API (`/api/monitor/ingestions`,
`/api/monitor/traces`), activated projection-lag readiness (`/health/ready` can
now reach `ready`), and the off-by-default opt-in raw-detail route
`GET /traces/{rawRecordId}/raw`.

## Source-of-truth comparison

- `docs/specifications/layers/raw-store-normalization.md` — projection worker,
  startup catch-up, retry / failure state, multi-trace fan-out (one
  `monitor_traces` row per `trace_id`), the missing-trace rule (ingestion row
  only, no trace row, no stall), and the projection-tables-only cursor read are
  implemented as specified. The allowlist columns are unchanged from M3.
- `docs/specifications/layers/telemetry-ingestion.md` — the cursor API response
  shape (`after`/`limit`, `next_cursor` via a `limit + 1` probe, `raw_record_id`
  vs projection-id cursor domains), the readiness `status`⇒HTTP mapping, and the
  `degraded_reasons` token set (including the M4 additions `loopback_unbound`,
  `db_unavailable`, `writer_not_running`, `projection_status_unknown`, plus the
  degraded `ingestion_backpressure` / `projection_lag`) were pinned before coding
  and match the implementation.
- `docs/specifications/security-data-boundaries.md` — the raw-detail route is the
  only raw / PII surface: present only with `--enable-raw-view` (absent ⇒ `404`),
  strict same-origin (cross-site `Sec-Fetch-Site` / foreign `Origin` ⇒ `403`),
  `Cache-Control: no-store`, payload rendered as HTML-encoded inert text, never
  logged. `/api/monitor/*` stay sanitized even with the flag on.
- `docs/decisions.md` D020 / D019 — opt-in raw, two-writer concurrent-DB model,
  and the `Telemetry/Monitoring/` split (deferred from M1 to M4) are honored.
- No source-of-truth conflict found. New public surface (cursor response shape,
  readiness tokens, missing-trace rule) was promoted into the specs in Task 0
  before implementation.

## Dependency direction

`Telemetry <- Persistence.Sqlite <- LocalMonitor` preserved. The projection
builder lives in `Telemetry`; the persistence projection methods and read DTOs in
`Persistence.Sqlite`; the worker, read seam (`IMonitorProjectionStore`), and HTTP
surface in `LocalMonitor`. `LocalMonitor` references only `Telemetry` and
`Persistence.Sqlite`, never `ConfigCli`. New types stay `internal` with the
existing `InternalsVisibleTo` friend lists. The shared `RawMeasurementNormalizer`
/ `MeasurementRow` contracts were not changed (the 300 Config CLI tests stay
green).

## Correctness highlights

- **Idempotency / fan-out:** `INSERT OR IGNORE` into `monitor_ingestions` (keyed
  on `raw_record_id`); the `monitor_traces` aggregate upsert runs only when the
  ingestion insert is new, so re-processing (restart catch-up, crash mid-batch)
  never double-counts, and a multi-trace export still aggregates every trace.
- **Missing trace:** a record with no `trace_id` is projected into
  `monitor_ingestions` only and never poisons / stalls the worker.
- **Cursor:** `limit + 1` probe gives a correct terminal `next_cursor` (an
  exactly-full final page returns `null`); the ingestion cursor uses
  `raw_record_id` for filter / order / `next_cursor` so a divergence from the
  projection-row id cannot skip or repeat rows. List reads never load
  `payload_json`.
- **Readiness:** `ready` requires loopback bind, open DB, completed migration,
  running writer, running projection worker, a successful projection status read,
  the writer accepting/committing, and lag `0`; momentary backpressure or
  sub-threshold lag ⇒ `degraded` (`200`); sustained stall, `projection_lag_exceeded`,
  missing worker, unknown projection status, migration failure, or fatal ⇒
  `not_ready` (`503`). Live lag is derived from the oldest-unprocessed timestamp,
  so a stalled worker shows growing lag.

## Raw / PII non-regression

- Projection tables and the cursor APIs carry only allowlist columns — a
  reflection test pins the DTO members and a builder test proves synthetic raw /
  PII markers never serialize into a projection. API tests assert markers are
  absent from `/api/monitor/*` even with `--enable-raw-view` on.
- The only raw surface is the flag-gated route, same-origin enforced, `no-store`,
  inert-rendered (a stored `<script>` shows as `&lt;script&gt;`), and raw is never
  logged (logging providers are cleared).
- Error / readiness bodies are fixed strings excluding the DB path, Windows user
  name, and raw exception text (asserted).

## Codex review checkpoints

Read-only Codex adversarial review (driven by Claude via `codex-companion`) ran at
two checkpoints:

- **Plan** — four rounds, five findings, all folded in before coding: multi-trace
  fan-out, `limit + 1` cursor terminal, missing-trace non-poisoning,
  `raw_record_id` cursor domain, and synthetic-fixture acceptance wording. Final
  plan verdict: **approve**.
- **Implementation** — two `[high]` findings, both fixed: (1) readiness ignored
  the `loopback_bound` / `db_open` / `writer_running` gates (a writer crash with
  migration still complete could falsely report `ready`); (2) projection status
  could go stale (startup / sustained status-read failure) leaving a false
  zero-lag `ready`. Fix commit `Sprint8 M4: fix: gate readiness on required
  checks and projection-status freshness`. Re-review verdict: **approve, no
  material findings.**

## Commands run

- `dotnet build CopilotAgentObservability.slnx` ⇒ **0 errors, 0 warnings**.
- `dotnet test CopilotAgentObservability.slnx` ⇒ **421 passing, 0 failing,
  0 skipped** (300 Config CLI + 121 LocalMonitor), above the M3 baseline of 371.

## Residual risk (M5–M6)

M4 is not a fully shippable monitor. Deferred: the Razor Web UI (Overview / Live
Ingestions / Traces / Diagnostics) and the SSE notification stream with gap
recovery (M5); CSRF on state-changing actions (none exist yet — M5); the full DR6
threat-model negative-test sweep and readiness-under-saturation matrix, raw
non-logging audit, and live VS Code HTTP/protobuf evidence at the monitor (M6,
the Sprint8 completion gate). A permanently un-projectable record (e.g. a builder
failure on a validly-decoded payload) is retained and retried; persistent
projection failure surfaces as growing lag / `projection_status_unknown` rather
than silent loss.
