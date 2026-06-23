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
| M4 Monitor Projection | `monitor_ingestions` / `monitor_traces`, ProjectionWorker, startup catch-up, retry/failure state, sanitized default projections + opt-in raw access. | Pending |
| M5 Web UI + SSE | Overview / Live Ingestions / Traces / Diagnostics; SSE event stream with reconnect/gap recovery. | Pending |
| M6 Security + Live Validation | DR6 threat-model negative tests (non-loopback, Host validation, cross-origin raw read, opt-in gating, CSRF), readiness non-2xx under saturation, raw non-logging, restart recovery, real VS Code validation. | Pending |

## Current Status

M1 (Shared Component Extraction), M2 (ASP.NET Core Receiver Host), and M3
(Ingestion Queue + SQLite Concurrency) are implemented. M3 is still **not** a
shippable monitor: projection population, the `/api/monitor/*` cursor API, the
Web UI / SSE, the opt-in raw-detail route, the full DR6 negative security matrix,
and live VS Code evidence remain M4–M6.

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
