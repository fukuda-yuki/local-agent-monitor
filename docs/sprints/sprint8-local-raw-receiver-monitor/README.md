# Sprint8: Local Raw Receiver Monitor

Sprint8 (issue #25) builds a **Local Ingestion Monitor**: a single ASP.NET Core
process that receives OTLP HTTP/protobuf telemetry directly from VS Code GitHub
Copilot Chat, persists it to the SQLite raw store, produces sanitized monitor
projections, and surfaces a local browser UI for confirming that ingestion is
healthy. By default it shows **sanitized metadata only**; under an explicit
`--enable-raw-view` opt-in it also lets the local user view their own raw
prompt / response / tool content (and PII attributes) for self-debugging,
loopback-only.

It is **not** a Langfuse replacement. By default it confirms: receiver started,
telemetry received, raw store persisted, trace projection succeeded, and no
ingestion / projection errors. Raw / prompt / response / tool content is shown
only under the explicit opt-in described in the Safety Boundary below.

## Decision

Initial shape is a **local modular monolith** (`CopilotAgentObservability.LocalMonitor`,
ASP.NET Core / Kestrel, loopback-only bind), reusing shared modules extracted
from the existing Config CLI. See [`../../decisions.md`](../../decisions.md)
D019 (shared extraction) and D020 (monitor scope, opt-in raw view, DR6 trust
model) and issue #25 for the architecture decisions and safety boundary.

## Scope

- Extract shared OTLP / raw-telemetry / normalization / SQLite components so the
  monitor and the Config CLI share one implementation (M1).
- ASP.NET Core receiver host with deterministic HTTP errors and a request body
  size limit (M2).
- Bounded-channel ingestion queue with a single SQLite writer worker and
  graceful shutdown (M3).
- Sanitized `monitor_ingestions` / `monitor_traces` projections with cursor
  pagination — list retrieval must not load all raw payloads (M4).
- Local Web UI (Overview / Live Ingestions / Traces / Diagnostics) + SSE (M5).
- Security and live VS Code validation (M6).

## Non-goals

- Replacing Langfuse. (Raw / prompt / response / tool content is not shown in
  default views; it is available solely as a loopback-only, off-by-default
  `--enable-raw-view` opt-in for the local user — see Safety Boundary.)
- Remote / shared deployment, multi-user auth.
- IIS as the initial required host, Windows Service, packaged exe, tray app.
- Aspire AppHost orchestration; PostgreSQL migration.
- Breaking the normalized measurement, candidate, or dashboard dataset schemas.

## Safety Boundary

The receiver may receive raw prompt, response, system prompt, tool
arguments/results, source paths, identity attributes, and credential-like
strings. The monitor follows an explicit **single-trusted-local-user** threat
model (D020 / DR6): the local user viewing their own data on the loopback UI is
intended, not a threat.

Always-on controls (cross-machine / other-origin): loopback-only bind,
`Host`-header validation (anti DNS-rebinding), CORS disabled, request body size
limit, no raw body / path / query / exception detail in logs, no DB full path or
Windows user name in responses. Default views / API / SSE carry sanitized
metadata only and PII is excluded by default. Captured content (raw view and
default UI) is rendered as escaped, inert text via the UI framework's default
output encoding (never `Html.Raw` / live markup), so stored markup displays as
text and does not execute; this local single-user tool does not add a heavier
CSP / anti-XSS apparatus on top of that.

Opt-in raw / PII view: off unless launched with `--enable-raw-view`. The only
raw surface is the server-rendered route `GET /traces/{rawRecordId}/raw` (no JSON
raw API; `/api/monitor/*` and SSE stay sanitized). Without the flag the route is
absent (`404`); with it, the route is loopback-only and same-origin enforced
(cross-site ⇒ `403`, `Cache-Control: no-store`), blocking browser-mediated
exfiltration, and raw / PII is still never logged or written to repository-safe
outputs. There is no bearer-token mechanism.

Accepted risk: `--enable-raw-view` is permitted in any launch mode (including
unattended / background), so raw / PII is reachable on loopback for the full
process lifetime — a product-owner-accepted risk on this single-user machine.
Same-local-user-process loopback reads are out of scope, as is any display-side
defense-in-depth beyond the required inert text rendering (no CSP / sanitizer
backstop). The repository-safe output boundary (`docs/requirements.md` §8) and
static-dashboard non-exposure (§9) are unchanged. Full model:
[`../../specifications/security-data-boundaries.md`](../../specifications/security-data-boundaries.md).

## Milestones

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Shared Component Extraction | Extract `Telemetry` + `Persistence.Sqlite` projects; keep Config CLI behavior and tests green. | **Implemented** |
| M2 ASP.NET Core Receiver Host | LocalMonitor project, Kestrel loopback, `POST /v1/traces`, request size limit, deterministic HTTP errors. | **Implemented** |
| M3 Ingestion Queue + SQLite Concurrency | Bounded channel, single writer worker, WAL, schema-version additive migration (creates empty projection tables), first `/health/*` endpoints, graceful shutdown. | **Implemented** |
| M4 Monitor Projection | `monitor_ingestions` / `monitor_traces`, ProjectionWorker, startup catch-up, retry/failure state, sanitized default projections + cursor API + opt-in raw access. | **Implemented** |
| M5 Web UI + SSE | Overview / Live Ingestions / Traces / Diagnostics; SSE event stream with reconnect/gap recovery. | **Implemented** |
| M6 Security + Live Validation | DR6 threat-model negative tests (non-loopback, Host validation, cross-origin raw read, opt-in gating, CSRF), readiness non-2xx under saturation, raw non-logging, restart recovery, real VS Code validation. | Verification complete; **live validation BLOCKED** (human-gated) |

## Current Status

M1–M5 are implemented and M6 verification is complete, but Sprint8 is **not**
complete: the M6 real VS Code Copilot Chat live validation is **blocked**
(human-gated) and recorded as a blocker in
[`milestones/M6-security-live-validation/live-validation.md`](milestones/M6-security-live-validation/live-validation.md).
The product owner accepted this blocker on 2026-06-25 and closed the work item in
this state; Sprint8 stays formally open until a human runs the live validation.

M6 added (tests only; no production change — the existing M3–M5 implementation
satisfied every assertion):

- DR6 negative security matrix at the HTTP level: default `/`, `/ingestions`,
  `/traces`, `/diagnostics`, `/api/monitor/*` never return raw / PII even with
  `--enable-raw-view`; the raw route is absent without the flag (`404`) and
  cross-site / foreign-origin with it is `403`; cross-origin `POST /events` is
  refused (SSE is GET-only); non-loopback `Host` is rejected (`400`); raw markers /
  DB path / user name never appear in error responses.
- HTTP-level readiness failure semantics: momentary backpressure / commit-timeout
  and sub-threshold projection lag stay `200` (`degraded`); sustained stall,
  `projection_lag_exceeded`, and `projection_status_unknown` are `503` with the
  pinned body.
- Same-DB restart recovery (projection catch-up, reaches `ready`).

Verified ready-state for live validation (only the real-VS-Code emission is
missing): `profile-vscode-env --profile raw-local-receiver --target monitor` emits
`OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320` + `http/protobuf`; a
real-process synthetic OTLP run confirmed `POST /v1/traces` → `200`,
`/api/monitor/ingestions` → 1 item, `/health/ready` → `200`.

Validation (M6):

- `dotnet build CopilotAgentObservability.slnx`: 0 errors, 0 warnings.
- `dotnet test CopilotAgentObservability.slnx`: 445 passing, 0 failing, 0 skipped
  (300 Config CLI + 145 LocalMonitor).

Implemented in M5 (see
[`milestones/M5-web-ui-sse/plan.md`](milestones/M5-web-ui-sse/plan.md) and
`review.md` in the same folder):

- Razor Pages `/`, `/ingestions`, `/traces`, `/diagnostics` served by the existing
  `MonitorHost`, as thin sanitized readers over `IMonitorProjectionStore` +
  `MonitorHealthState`. Default views render allowlisted columns only (no raw / PII);
  the `raw` link appears only with `--enable-raw-view` and still points to the M4
  opt-in route. Pages use framework default output encoding (no `Html.Raw`).
- `GET /events`: a `text/event-stream` notification-only stream. After the
  projection worker newly projects records it emits one `projection` event with
  `data: {}` — never raw payloads, trace ids, raw record ids, or PII. Subscriptions
  register synchronously before a `: connected` comment flush (no missed events);
  reconnecting clients recover gaps via `/api/monitor/*` cursors.
- `wwwroot/monitor.js`: on each notification re-reads the sanitized cursor APIs from
  its last-seen cursor; never inserts raw payloads into the DOM. Served (with
  `monitor.css`) via the static web assets manifest, loaded explicitly so it works
  in the default Production environment.

Validation (M5):

- `dotnet build CopilotAgentObservability.slnx`: 0 errors, 0 warnings.
- `dotnet test CopilotAgentObservability.slnx`: 433 passing, 0 failing, 0 skipped
  (300 Config CLI + 133 LocalMonitor; above the M4 baseline of 421).
- Live smoke (`dotnet run`, default Production env): all four pages return
  `200 text/html`; `/monitor.js` and `/monitor.css` are served.

Implemented in M4 (see
[`milestones/M4-monitor-projection/plan.md`](milestones/M4-monitor-projection/plan.md)
and `review.md` in the same folder):

- `MonitorProjectionBuilder` (`Telemetry/Monitoring/`) turns one raw record into
  sanitized projection values (allowlist only; no raw content or PII), fanning out
  one `monitor_traces` contribution per non-blank `trace_id`; a trace-id-less
  record is projected into `monitor_ingestions` only and never stalls projection.
- Idempotent persistence projection: `INSERT OR IGNORE monitor_ingestions` plus a
  guarded `monitor_traces` aggregate upsert (exactly-once per raw record),
  projection backlog/oldest status, cursor-paginated sanitized reads (projection
  tables only, `limit + 1` terminal probe, `raw_record_id` vs projection-id cursor
  domains), and a by-id raw fetch.
- `ProjectionWorker`: a second writer (projection tables only) with startup
  catch-up, `SQLITE_BUSY` retry, non-busy failure isolation (raw retained), and
  health updates; it waits for the ingestion writer's migration first.
- `/health/ready` now returns `ready` (caught up), `degraded` (`200`; momentary
  backpressure or sub-threshold projection lag), or `not_ready` (`503`; sustained
  stall, `projection_lag_exceeded`, missing worker, migration failure, fatal).
- `GET /api/monitor/ingestions` / `GET /api/monitor/traces`: sanitized,
  cursor-paginated (`after`/`limit`, `next_cursor`), `400` on invalid query.
- Opt-in `GET /traces/{rawRecordId}/raw`: present only with `--enable-raw-view`
  (absent ⇒ `404`); same-origin enforced (cross-site ⇒ `403`),
  `Cache-Control: no-store`, raw rendered as HTML-encoded inert text.

Validation (M4):

- `dotnet build CopilotAgentObservability.slnx`: 0 errors, 0 warnings.
- `dotnet test CopilotAgentObservability.slnx`: 421 passing, 0 failing, 0 skipped
  (300 Config CLI + 121 LocalMonitor; above the M3 baseline of 371).

Implemented in M3 (see
[`milestones/M3-ingestion-queue-sqlite-concurrency/plan.md`](milestones/M3-ingestion-queue-sqlite-concurrency/plan.md)
and `review.md` in the same folder):

- Threshold options `--ingestion-stall-threshold-seconds` (default 10) and
  `--projection-lag-threshold-seconds` (default 60), with `CAO_MONITOR_*` env
  fallbacks.
- Ack-backed bounded ingestion queue (`System.Threading.Channels`, capacity 1024,
  full ⇒ deterministic non-blocking reject) and a single `IngestionWriterWorker`
  that owns all SQLite writes; `POST /v1/traces` returns `2xx` only after commit.
- Fixed ingestion errors: queue full `503`/`queue_full`, commit timeout
  `504`/`commit_timeout` (internal 5s, not a public option), shutdown
  `503`/`shutting_down`, DB busy `503`/`persistence_busy`, non-busy persistence
  failure `500`/`persistence_failed`; bodies stay sanitized.
- Additive `schema_version` migration that creates the empty `monitor_ingestions`
  / `monitor_traces` projection tables (allowlist columns; no raw/PII), preserving
  `raw_records`; WAL + `busy_timeout` retained for concurrent external readers.
- First real `/health/live` (`200`) and `/health/ready` (machine-readable body).
  In M3 `/health/ready` is always `503` / `not_ready`: ingestion-healthy ⇒
  `projection_worker_missing`; sustained stall ⇒ `ingestion_stalled`; migration
  failure ⇒ `migration_failed`; fatal worker error ⇒ `fatal_error`. It never
  falsely returns `ready` before the M4 projection worker exists.

Validation (M3):

- `dotnet build CopilotAgentObservability.slnx`: 0 errors, 0 warnings.
- `dotnet test CopilotAgentObservability.slnx`: 371 passing, 0 failing,
  0 skipped (300 Config CLI + 71 LocalMonitor; above the M2 baseline of 322).

See
[`milestones/M1-shared-component-extraction/plan.md`](milestones/M1-shared-component-extraction/plan.md)
for the accepted plan (challenge-reviewed via `/codex:adversarial-review`),
[`pre-implementation-review.md`](pre-implementation-review.md) for the original
static review, and [`handoff-fix-worklist.md`](handoff-fix-worklist.md) for the
validated findings (B1–B3, T4–T7, NU1903).

Implemented in M1:

- New `CopilotAgentObservability.Telemetry` class library (`Otlp/`,
  `RawTelemetry/`, `Normalization/`) holding the OTLP protobuf/JSON converters,
  attribute converter, raw ingestor, raw record model, measurement normalizer,
  and measurement sanitizer.
- New `CopilotAgentObservability.Persistence.Sqlite` class library holding the
  SQLite raw store (relocated as-is; behavior unchanged).
- Dependency direction `Telemetry <- Persistence.Sqlite <- ConfigCli`;
  extracted types stay `internal` with `InternalsVisibleTo`.
- T4 fix: the duplicated OTLP attribute conversion in `RawOtlpIngestor` now
  calls the shared `OtlpAttributeConverter`.
- NU1903 high-severity package vulnerabilities resolved: `MessagePack` 2.5.302
  (AppHost), `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 (Persistence.Sqlite).

Validation:

- `dotnet build CopilotAgentObservability.slnx`: 0 errors, 0 warnings,
  0 NU1903 vulnerable-package entries across all five projects.
- `dotnet test CopilotAgentObservability.slnx`: 291 passing, 0 failing,
  0 skipped (Config CLI external behavior unchanged).

Deferred (carried into later milestones, recorded in D019):

- M3/M4 storage/readiness/projection contracts — M2 is an internal,
  non-shippable subset. It intentionally exposes no `/health/*` placeholders and
  no raw-detail route.
- T5 / T6 store behavior (schema-once / single writer / projection query) —
  M3/M4; the store was relocated without behavior changes.
- T7 single-threaded accept loop — superseded by the M3 channel/worker model.
- `Telemetry/Monitoring/` (monitor summary sanitization) — created in M4.

Still unconfirmed (inherited from Sprint7):

- Live VS Code GitHub Copilot Chat telemetry against the receiver, and the
  VS Code / extension version evidence (Sprint8 M6).
