# Telemetry Ingestion Specification

## Scope

Telemetry ingestion covers OTel configuration samples and accepted client sources.
It does not define raw store schema, candidate generation, or dashboard rendering.

## Supported Sources

Required:

- VS Code GitHub Copilot Chat。
- GitHub Copilot CLI。
- OpenTelemetry Collector as relay。

Optional:

- Codex App / app-server。
- Issue #51 Session events from `copilot-sdk-stream` or
  `copilot-compatible-hook`, accepted by the installed Local Monitor only.

Reference-only:

- Claude Code examples。
- Visual Studio client family。

## Collection Profiles

Collection profile selection is a public interface.
The profile selector is:

```text
CAO_COLLECTION_PROFILE
```

The required profile values are defined in
[../interfaces/collection-profiles.md](../interfaces/collection-profiles.md).

Telemetry ingestion must support:

- `raw-only`
- `docker-desktop-langfuse`
- `docker-desktop-collector-langfuse`
- `wsl2-docker-langfuse`
- `wsl2-docker-collector-langfuse`
- `remote-managed-langfuse`
- `remote-managed-collector`
- `raw-local-receiver`

`raw-only` is the minimum profile and does not require a live receiver.
`docker-desktop-langfuse` is the standard full profile.
`raw-local-receiver` is a required support target to be implemented in Sprint7
and is split from other routing profiles because it introduces a long-running
local process.

## Langfuse Direct Path

Default direct endpoint:

```text
http://localhost:3000/api/public/otel
```

Trace-specific endpoint:

```text
http://localhost:3000/api/public/otel/v1/traces
```

Langfuse requires Basic Auth.
Credentials are passed through local environment variables or user-level config, never repository files.

## Collector Relay Path

Collector relay is required for profiles that include `collector`.

Default local receiver:

```text
http://localhost:4318
localhost:4317
```

Collector may attach Langfuse authorization headers so clients do not store Langfuse credentials.
The repository example handles trace pipeline only.
Masking, sampling, TLS, SSO, and shared operation require separate product / security decisions.

## WSL2 Docker Engine Path

For `wsl2-docker-langfuse` and `wsl2-docker-collector-langfuse`, Docker Engine
runs inside WSL2 while VS Code, GitHub Copilot CLI, or Codex App runs on
Windows. The client endpoint must therefore be reachable from Windows, not only
from inside the WSL2 distro.

Generated samples use:

```text
http://<windows-reachable-wsl2-host>:3000/api/public/otel
http://<windows-reachable-wsl2-host>:4318
```

Use `localhost` when WSL2 localhost forwarding exposes published container
ports to Windows. If forwarding is unavailable, resolve the current WSL2 distro
address during live validation and keep that machine-specific value out of
repository files.

## Raw Local Receiver Path

The `raw-local-receiver` profile sends telemetry directly to a repository-hosted
local receiver instead of Langfuse.

Initial host model:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- serve-raw-local-receiver --db data\raw-store.db --url http://127.0.0.1:4319
```

The initial required path is a repository-local foreground `dotnet run`
process. IIS, IIS Express, packaged exe, tray app, and Windows Service hosting
are not part of the initial required path.

Initial receiver requirements:

- bind to loopback-only local development endpoints unless a later security
  decision allows broader exposure.
- accept OTLP HTTP telemetry from supported clients through the standard OTLP
  HTTP signal paths, including `/v1/traces`.
- accept OTLP HTTP protobuf trace payloads on `/v1/traces` because VS Code
  Copilot Chat uses HTTP/protobuf for `otlp-http` unless configured for gRPC.
- JSON OTLP trace payloads may be accepted for synthetic local validation, but
  JSON support does not replace the protobuf requirement for VS Code direct
  validation.
- accept trace telemetry as the first required signal; metrics and event-like
  telemetry may be accepted when supported by the receiver implementation, but
  unsupported signals must fail clearly and must not be treated as successful
  ingestion.
- persist raw telemetry as local runtime data for the raw data loop.
- write either to the SQLite raw store or to a raw OTLP file that can be passed
  to `ingest-raw`.
- avoid changing normalized measurement, candidate, or dashboard dataset
  contracts.
- avoid committing raw receiver output.

The local receiver is not implemented through Aspire AppHost by default.

Initial HTTP behavior:

- `POST /v1/traces` returns success only after a raw telemetry record is
  persisted.
- methods other than `POST` fail with a deterministic method error.
- `/v1/metrics`, `/v1/logs`, and other unsupported paths fail clearly and must
  not write raw records.
- invalid payloads fail clearly and must not write raw records.
- unsupported content types fail clearly and must not write raw records.

Generated `raw-local-receiver` configuration must point clients at the local
receiver endpoint and must not include Langfuse credentials, Collector headers,
remote endpoints, or repository-stored secrets.

Live validation for this profile must record:

- date and environment.
- receiver command and local bind address.
- collection profile value.
- client kind.
- non-secret endpoint shape.
- raw store path or raw OTLP file path, recorded as local runtime output.
- trace id or raw record identifier.
- confirmation that Langfuse was not required.
- confirmed and unconfirmed telemetry signals.

## Local Ingestion Monitor Receiver

The Local Ingestion Monitor is a separate long-running ASP.NET Core (Kestrel)
process that receives `raw-local-receiver` telemetry and surfaces a local
ingestion-health UI. It is distinct from the Config CLI
`serve-raw-local-receiver` foreground receiver and runs **side-by-side** with
it: the Config CLI receiver keeps `http://127.0.0.1:4319`, and the monitor
defaults to `http://127.0.0.1:4320` with `--port` / `--url` override. The Config
CLI receiver is not removed or deprecated. The monitor default avoids `4317` /
`4318` (the Collector profile's OTLP gRPC / HTTP ports — see Collector Relay
Path and the `ConfigSamples` constants) and `4319` (the Sprint7 CLI receiver),
so all three can coexist on loopback; `4320` is the next free port. The
`raw-local-receiver` profile output defaults to the CLI-receiver endpoint
(`4319`); to send VS Code telemetry to the monitor instead, generate the
environment with `config-cli profile-vscode-env --profile raw-local-receiver
--target monitor`, which emits
`OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320` (use `--endpoint` for a
non-default monitor port). The monitor must fail with a deterministic error if
its port is already bound rather than silently sharing it.

Run model (initial required path):

```powershell
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\raw-store.db --url http://127.0.0.1:4320
```

Receiver requirements:

- bind to loopback only; reject non-loopback bind URLs; validate the `Host`
  header on every request (anti DNS-rebinding).
- accept OTLP HTTP protobuf trace payloads on `POST /v1/traces`; JSON OTLP trace
  payloads may be accepted for synthetic local validation.
- accept only `/v1/traces`. `/v1/metrics`, `/v1/logs`, other paths, and
  non-`POST` methods fail with a deterministic HTTP status and write no raw
  record.
- enforce a request body size limit (default `31457280` bytes = 30 MiB,
  configurable — see *Request body size limit* below); a request body larger than
  the limit fails with `413` / `request_too_large` and writes no raw record.
- isolate each request; one failed or malformed request must not stop the host.
- return HTTP `2xx` only after the raw record is committed (the queue / single
  writer model is specified in
  [raw-store-normalization.md](raw-store-normalization.md)). Fixed ingestion
  errors: queue full `503`, commit timeout `504`, shutdown `503`, DB busy after
  retry `503`.
- never write raw payloads, request bodies, paths, query strings, or exception
  detail to logs; error responses exclude the DB full path, the Windows user
  name, and raw exception messages.

Request body size limit:

- default **`31457280` bytes (30 MiB)**, configurable via the CLI flag
  `--max-request-body-bytes <bytes>` with env fallback
  `CAO_MONITOR_MAX_REQUEST_BODY_BYTES`. A non-positive / non-numeric value is a
  deterministic startup error.
- a request body **up to and including** the limit is accepted; a body **larger
  than** the limit fails with `413` / `request_too_large` and writes no raw
  record.
- the default is deliberately generous: the monitor targets a single local user
  whose realistic risk is an accidental oversized payload, so the limit guards
  against memory exhaustion without dropping real OTLP trace batches. The value
  is a public accept/reject boundary, pinned here rather than chosen at
  implementation time.
- mandatory boundary tests cover an **at-limit** payload (accepted) and an
  **over-limit** payload (`413`, no raw record), using an overridden small limit
  so fixtures stay tiny.

Health endpoints:

- `GET /health/live` — the process is responsive.
- `GET /health/ready` — loopback bind, DB open, migration complete, writer and
  projection worker running, no fatal error, the writer can accept/commit, and
  projection lag within a bounded threshold.
- **Sustained** queue-full / commit failure / projection-lag-exceeded ⇒
  `/health/ready` returns a **non-2xx HTTP status** (`503`), not merely a body
  flag, because many readiness probes read only the status. Momentary
  backpressure ⇒ a `2xx` "degraded" response.

Readiness thresholds (concrete defaults, all configurable):

- **ingestion stall**: the writer unable to accept/commit (queue full or commit
  failing) **continuously for ≥ `ingestion-stall-threshold-seconds`** (default
  `10`) ⇒ `not_ready` / `503`; shorter backpressure ⇒ `degraded` / `200`.
- **commit failure / migration**: a non-busy commit error is retried; commits
  failing past the stall window, or a failed migration, ⇒ `not_ready` / `503`.
- **projection lag**: the age in seconds of the oldest unprocessed `raw_records`
  row. Lag **≥ `projection-lag-threshold-seconds`** (default `60`) ⇒ `not_ready`
  / `503`; lag above zero but under the threshold ⇒ `degraded` / `200`.
- configuration surface: CLI flags `--ingestion-stall-threshold-seconds` and
  `--projection-lag-threshold-seconds`, with env fallbacks
  `CAO_MONITOR_INGESTION_STALL_THRESHOLD_SECONDS` and
  `CAO_MONITOR_PROJECTION_LAG_THRESHOLD_SECONDS`.

Readiness body (machine-readable, returned on both `200` and `503`):

```json
{
  "status": "ready | degraded | not_ready",
  "checks": {
    "loopback_bound": true,
    "db_open": true,
    "migration_complete": true,
    "writer_running": true,
    "projection_worker_running": true,
    "ingestion_accepting": true,
    "projection_lag_seconds": 0,
    "projection_backlog": 0,
    "span_projection_lag_seconds": 0,
    "span_projection_backlog": 0,
    "projection_failure_count": 0
  },
  "degraded_reasons": []
}
```

- `status` ⇒ HTTP mapping: `ready` and `degraded` ⇒ `200`; `not_ready` ⇒ `503`.
- `projection_backlog` / `projection_lag_seconds` describe trace projection work.
  `span_projection_backlog` / `span_projection_lag_seconds` describe the
  independent per-span backfill queue. Span projection backlog does not gate
  readiness by itself, but it must be visible while upgraded databases are still
  being backfilled. `projection_failure_count` is cumulative for projection
  failures in the running process and is diagnostic only.
- `degraded_reasons` enumerates active conditions. **not_ready (`503`) tokens:**
  `loopback_unbound` (not bound to loopback), `db_unavailable` (DB not open),
  `migration_failed`, `fatal_error`, `ingestion_stalled` (backpressure continuous
  past the ingestion-stall threshold), `writer_not_running` (the ingestion writer
  is not running), `projection_lag_exceeded` (projection lag `≥` the projection-lag
  threshold), `projection_worker_missing` (no projection worker running yet — so an
  ingestion-healthy monitor still reports `not_ready` rather than falsely claiming
  `ready`), and `projection_status_unknown` (the worker is running but has not yet
  produced a successful backlog/lag read — startup or a sustained status-read
  failure — so lag is unknown and ready is withheld). **degraded (`200`) tokens:**
  `ingestion_backpressure` (momentary sub-threshold backpressure),
  `projection_lag` (projection lag above zero but under the threshold), and
  `span_projection_backlog` (trace projection is current, but per-span projection
  or upgrade backfill still has queued work).
- the `ready` state (`200`, empty `degraded_reasons`) is active once a projection
  worker is running, migration is complete, the writer can accept/commit, and
  projection lag is `0`.
- mandatory tests cover the default thresholds **and** a configured override,
  asserting both the HTTP status and the body `status` / `degraded_reasons`.

(This readiness contract is monitoring correctness, not display hardening; it is
pinned here deliberately so external probes and the M3 / M6 tests have a stable
contract.)

Monitor read API (sanitized, cursor pagination):

- `GET /api/monitor/ingestions`, `GET /api/monitor/traces`, and
  `GET /api/monitor/traces/{traceId}/spans` return sanitized projections only —
  the per-table allowlist columns defined in
  [raw-store-normalization.md](raw-store-normalization.md). They read the
  projection tables, never `raw_records.payload_json`, and never return raw
  prompt / response / tool content or PII.
- query parameters: `after` (exclusive cursor; omitted ⇒ from the start) and
  `limit` (default `50`, maximum `200`). A non-numeric or out-of-range `limit`
  fails with a deterministic `400`.
- response body: `{ "items": [ ...sanitized rows... ], "next_cursor": <id|null> }`,
  `items` ordered by ascending cursor key. `next_cursor` is non-null **only when
  more rows exist beyond the page** (determined by probing one row past `limit`)
  and is then the last returned item's cursor key; otherwise `null`. A final page
  whose size is exactly `limit` therefore returns `next_cursor: null` and never
  requires an extra empty fetch to discover the end.
- cursor keys: `/api/monitor/ingestions` uses `raw_record_id` (the
  `monitor_ingestions` cursor key); `/api/monitor/traces` uses the projection-row
  id; `/api/monitor/traces/{traceId}/spans` uses the `monitor_spans`
  projection-row id. Each endpoint's filter, ordering, and `next_cursor` use the
  one key, so a divergence between a projection-row id and `raw_record_id` cannot
  skip or repeat rows.

`/api/monitor/traces` rows include the rollup columns added by Sprint9:
`input_tokens`, `output_tokens`, `total_tokens`, `turn_count`,
`agent_invocation_count`, `duration_ms`, `primary_model`, plus the Sprint16
repository metadata fields `repository_name`, `workspace_label`, and
`repo_snapshot` when present (see
[raw-store-normalization.md](raw-store-normalization.md) for the full schema).

`/api/monitor/traces/{traceId}/spans` returns sanitized per-span rows from
`monitor_spans` for the given `trace_id`. Same `after`/`limit` contract as the
other cursor endpoints. Each row includes operation, category, tool/MCP name,
agent name, model, token counts (including `cache_read_tokens` /
`cache_creation_tokens`), status, error type, timing, and hierarchy
(`parent_span_id`, `span_ordinal`). No raw content or PII.

`GET /api/monitor/overview?period=today|7d|30d` returns the sanitized period
aggregate for the overview dashboard (D044): `{period, range, kpi, per_model,
hourly_tokens}`. `kpi` carries numeric aggregates only: `tokens_total`,
`tokens_previous_period`, `tokens_change_pct`, `uncached_tokens_total`,
`uncached_tokens_previous_period`, `uncached_tokens_change_pct`
(実消費 = uncached input + output; see
[raw-store-normalization.md](raw-store-normalization.md) token-usage
convention), `cache_read_tokens_total`, `cache_aware_input_tokens`,
`effective_input_tokens`, `cache_compression_pct`, `cache_read_rate_pct`
(rates over the cache-aware input sum only; `null` when no cache-bearing row
is in the window), `error_trace_count`, and `trace_count`. Unsupported
`period` values return `400 invalid_query`. `GET /api/monitor/trace-list`
serves the filtered trace list from the same rollup columns. Both stay inside
the sanitized `/api/monitor/*` boundary (see
[../security-data-boundaries.md](../security-data-boundaries.md)).

The Sprint10 monitor **design views** (Flow Chart, Cache Explorer, timeline
filter/sort, themed trace-detail UI) are **client-side presentation over this
existing endpoint only** — they add no new endpoint, query parameter, or response
field. Every field they need (`parent_span_id`, category, agent/tool/MCP names,
models, the per-span token counts above, `finish_reasons`, `conversation_id`,
status / `error_type`, and span timing) is already in this sanitized row. See
[../security-data-boundaries.md](../security-data-boundaries.md) for the
sanitized-only consumption invariant and the out-of-scope items (Cache Explorer
raw prefix-diff, `conversation_id` cross-trace stitching).

The Sprint11 GitHub Copilot app Canvas adapter is a consumer of the same Local
Monitor surface and may be used with the normal raw-default monitor. Canvas
actions read `GET /api/monitor/traces`,
`GET /api/monitor/traces/{traceId}/spans`, and `/health/ready` as needed, then
return bounded LLM-oriented DTOs rather than raw monitor payload dumps. The
documented actions are `monitor_health()`,
`list_recent_traces({ limit, status?, model? })`,
`get_trace_summary({ traceId })`, `get_trace_span_tree({ traceId })`, and
`get_cache_summary({ traceId })`; `list_recent_traces.limit` is bounded to
`1..50` for Canvas action output. Sprint11 adds no telemetry input, SQLite
schema, projection column, endpoint, query parameter, response field, raw route,
normalized dataset field, candidate record field, or dashboard contract.
Sprint16 supersedes only the schema / projection / response-field part of that
statement for the three sanitized repository metadata fields
`repository_name`, `workspace_label`, and `repo_snapshot`; it does not add a
new telemetry input, raw route, query parameter, normalized dataset field,
candidate record field, or dashboard contract.
Issue #51 further supersedes the no-new-input/schema statement only for the
separate Session ingest, Session storage, workspace reads, and exact-link OTel
enrichment defined in
[Canvas Session workspace](../interfaces/canvas-session-workspace.md).
`--sanitized-only` remains an optional Local Monitor metadata-only mode, not a
Canvas requirement.

Sprint11 M5 adds an optional UI-to-Copilot analysis trigger (D029). `open()`
returns an extension-owned loopback helper page (per-launch token) that proxies
sanitized `GET /api/monitor/traces?limit=50` for a trace dropdown and
exposes an "Analyze selected trace with Copilot" button. The button posts to a
token-protected `POST /analyze` route that calls `session.send({ prompt })` with
an instruction referencing only the selected trace id, optional span id, focus
(`latency` / `tokens` / `cache` / `errors`), and action names. Raw details remain
local Monitor UI data and are not copied into Canvas action responses, logs,
committed files, or static artifacts. No monitor payload is embedded in the
trigger instruction. M5 adds no telemetry input, schema, endpoint, query
parameter, response field, raw route, or dependency.
That statement describes the Sprint11 M5 trigger scope; it does not prohibit the
separate Issue #51 Session interfaces.

Raw / PII exposure follows the Local Ingestion Monitor boundary in
[../security-data-boundaries.md](../security-data-boundaries.md): raw body
(tool call arguments / results, sub-agent instructions / responses, system
prompt) and PII (`user.id` / `user.email`) are shown **by default**
(server-rendered, inert text) on raw-bearing surfaces. The trace-detail page
renders a bounded inline raw preview and links to `GET /traces/{rawRecordId}/raw`
for the full single-record payload by default. The dashboard (`/`) and trace-list
(`/traces`) pages additionally render a single representative user-prompt label
per trace (server-side extracted from the trace's raw OTLP payload, truncated,
inert text) so a trace is identifiable by what the user asked (D032); only that
short label is raw and `/api/monitor/*` / SSE still never carry it. The Sprint12
Flow Chart / Span Tree views are plain DOM over the sanitized spans API (the
Cytoscape / dagre vendored dependency is removed, D033). `/api/monitor/*` and SSE
never carry raw / PII. The `--sanitized-only` flag restores metadata-only mode:
the trace-detail page still returns the sanitized tab shell, omits the raw
section and full raw links, the dashboard / trace-list prompt label is omitted (a
shortened TraceId is shown), `GET /traces/{rawRecordId}/raw` returns `404`, and
PII is excluded. Raw / PII is never logged or committed.

Live validation for the monitor records the same evidence as the
`raw-local-receiver` profile, plus the monitor port, the VS Code / GitHub
Copilot extension version, and optionally whether `--sanitized-only` was set.

## Source capability semantic contract v1

`contracts/source-capabilities/v1/source-capability-manifest.schema.json` is
the JSON Schema 2020-12 structural contract. Each JSON file in its `manifests/`
directory declares the capability of one source surface. A manifest's
`contract_version` must match the schema major (`v1`); unknown properties are
rejected. The composite `otel-http+copilot-compatible-hook` manifest label
denotes registered paths only. It is not provenance for an observed field.

For each normalized value, the producing adapter records the actual
contributing adapter ID that supplied the field, such as `otel-http`,
`copilot-compatible-hook`, or `copilot-sdk-stream`, rather than a registry
classification; it must never record the composite
`otel-http+copilot-compatible-hook` manifest label as per-field provenance.
It also records a required source version or schema fingerprint, source event
ID or trace/span identity, capture/content state, and normalization version. If
any required provenance component is absent, the value is not promoted to
authoritative evidence and does not trigger inference. The absence maps only to
the fixed reasons: absent source event/trace-span identity is
`missing_trace_context`; absent capture/content state is
`content_capture_disabled`; and absent actual adapter, source version/schema
fingerprint, or normalization version is `schema_drift_detected`.

The field-family authority and precedence contract is:

| Field family | Authoritative evidence | Limited fallback | Precedence rule |
| --- | --- | --- | --- |
| identity, hierarchy, timing | available OTel identity/hierarchy/timing, only when the existing exact-link rule is met | none for binding | weak never overwrites strong; missing values do not overwrite |
| native lifecycle, explicit event identity | Hook/SDK native lifecycle/explicit event identity | none | weak never overwrites strong; missing values do not overwrite |
| model/token, retry, error summary | declared primary source with complete provenance | historical summary allowlist-only: `model_tokens.*`, `retry_attempt.*`, and `errors` | historical data may fill an absent allowlisted field but never identity, hierarchy, timing, lifecycle, or explicit event identity |

Repository, workspace, and timestamp are context only; they are never identity
evidence. This contract neither permits a heuristic Session merge nor creates a
synthetic span. It preserves Issue #51 exact identity and Issue #49 ownership.

Later adapter work must use this handoff checklist: validate the exact v1
schema and matching manifest version; declare only observed capabilities; emit
per-field actual-adapter provenance; apply the authority table without
overwrite/inference; calculate the fixed completeness statuses/reasons in the
canonical order; keep raw/sanitized boundaries; and obtain a new contract major
before a breaking vocabulary or semantic change. This checklist is not an
authorization to change the existing receiver, adapter, persistence, migration,
HTTP, proxy, or UI DTO in Issue #61.

## Session Event Ingestion And Enrichment

Issue #51 adds a Session event input beside, not inside, the OTLP receiver:

```text
POST /api/session-ingest/v1/events
```

This is the only new Session event write interface. It accepts schema v1 JSON
from adapters `copilot-sdk-stream` and `copilot-compatible-hook`, with source
surfaces `copilot-sdk`, `copilot-cli`, `vscode`, and `hook-unknown`. It requires
`X-CAO-Session-Event-Version: 1`, accepts batches of 1..100 events, and enforces
a 1 MiB request limit. It returns `204` only after commit. Fixed error mapping,
the envelope/event shape, and read interfaces are defined in
[Canvas Session workspace](../interfaces/canvas-session-workspace.md).

The installed Local Monitor also provides:

```text
hook-forward --endpoint <loopback-url> --timeout-ms 250 \
  [--source claude-code \
    [--source-version <metadata-token>] \
    [--schema-fingerprint <64-lowercase-hex>]]
```

The forwarder reads one stdin JSON payload. Invalid input, network failure, and
timeout always exit `0`; stdout and stderr remain empty; the forwarder never
influences the agent Hook decision. The endpoint must be loopback.

Omitting `--source` selects the unchanged Copilot Hook path. Exact
`--source claude-code` selects Claude before stdin is interpreted; no payload
shape may select the source. Provenance flags are valid only in Claude mode,
which requires at least one trusted out-of-band value: the emitting application
version or an approved Hook schema fingerprint. The forwarder does not infer
either value from payloads, documentation labels, or inventory-only evidence.
Missing, unknown, or duplicate selector state, a provenance flag without the
Claude selector, or missing/invalid Claude provenance suppresses forwarding
while retaining exit `0` and silent output. Copilot Hook behavior is unchanged.

Copilot CLI and VS Code share PascalCase Hooks. When Hook evidence does not
identify the source exactly, ingestion records `hook-unknown`. Environment,
repository, tool names, transcript paths, and timestamps must not infer the
surface. OTel `client_kind` may only confirm whether `hook-unknown` is
`copilot-cli` or `vscode`; it never participates in Session binding or merge.

Canvas/App SDK capture uses `ctx.sessionId` as the native session ID and begins
at the first Canvas open. Persisted SDK events are sent to the Session event
endpoint; ephemeral usage is aggregated; reasoning and streaming deltas are not
persisted. Events missed before the first open are not reconstructed and lower
Session completeness.

OTel Session enrichment runs only after the existing monitor projection. It
uses the dedicated `session_projection_state` cursor. A byte-for-byte trace
context already recorded on an event may exact-link OTel evidence. Exact
`gen_ai.conversation.id` may bind/enrich only when byte-for-byte equal to an
already-recorded native session ID; otherwise OTel remains `unbound`.
`client_kind` never binds or merges Sessions. Session schema migration runs at
startup, and failure fails host construction, matching the analysis-store
migration. It does not change the existing OTLP receiver, trace/span schema,
monitor projection cursor, or readiness thresholds/body/status mapping.

This Issue #51 exception supersedes older absolute wording that Canvas adds no
telemetry input or correlation. It does not broaden unrelated Canvas actions,
raw-data, loopback/token, bounded-response, or `session.send()` contracts.

## Local Monitor Copilot Raw Analysis

## Retention scheduling and active-operation exclusion

Retention catalog v1 owns cleanup scheduling rather than the receiver or
projection worker. Raw HTTP reads, monitor raw-projection loads, analysis
tool-data loads, active analysis operations, Sensitive Bundle reads/resume/
enumeration, and SDK directory operations must acquire the appropriate catalog
access or operation lease before touching raw data. A deletion lease cannot
claim an item while such a lease is active. Late writes revalidate the expected
catalog revision and cannot recreate or re-enable a denied item.

The worker has a durable bounded queue, not an in-memory source of truth. It
promotes expired items idempotently, claims in expiry/item-id order, records
fixed sanitized error codes and retry metadata, and recovers only the exact
journaled source after restart. `deleting` remains forward-only after a delete
intent. Worker status and later retention diagnostics never expose raw values,
source IDs, private locators, paths, credentials, PII, or exception text.

The Local Monitor may start a Copilot SDK raw analysis run for a selected trace
in the normal raw-default posture. The raw-analysis feature itself does not add
telemetry input and does not change existing sanitized `/api/monitor/*` or SSE
contracts; Issue #51's separate Session input is not part of this analysis path.

Routes:

- `POST /traces/{traceId}/analysis` starts a local run. It accepts focus values
  `latency`, `tokens`, `cache`, `errors`, `tool-usage`, `agent-flow`, and
  `instruction-diagnosis`. The `instruction-diagnosis` focus applies the
  taxonomy and fixed per-finding output contract defined in
  [instruction diagnosis analysis](../interfaces/instruction-diagnosis-analysis.md)
  and is exposed in the Local Monitor drawer only (the Canvas helper focus
  set, D036, is unchanged). The
  request is same-origin and CSRF protected and does not contain raw payload.
  The monitor dispatches the run to the in-process .NET GitHub Copilot SDK
  analysis service.
- `GET /traces/{traceId}/analysis/runs/{runId}` reads local raw-derived result
  data and is raw-bearing (`Cache-Control: no-store`).
- `GET /traces/{traceId}/analysis/runs/{runId}/safe-summary` emits a
  repository-safe allowlist summary.

Supported process-internal .NET SDK tool names:

- `get_raw_trace`
- `get_raw_record`
- `get_raw_span_context`
- `get_trace_summary`
- `get_trace_span_tree`
- `get_cache_summary`
- `get_instruction_evidence`

These tools are not exposed as public HTTP routes and are separate from
repository-safe summary generation. `get_instruction_evidence` returns the
deterministic instruction-evidence extractor output defined in
[instruction diagnosis analysis](../interfaces/instruction-diagnosis-analysis.md),
including the bounded same-conversation `conversation_context` for
`instruction-diagnosis` when available (D047 / D048). The Local Monitor project references the
official `GitHub.Copilot.SDK` .NET package. Normal repository validation can
build and test the integration without requiring a signed-in Copilot SDK
runtime; live analysis validation still requires a signed-in Copilot session.
The SDK runner reads local configuration from `CopilotAnalysis:*`, including
optional BYOK provider settings and `CopilotAnalysis:TimeoutSeconds` (positive
integer; the send/wait execution timeout for one analysis run, default `60`,
independent of the Canvas options timeout hints). It must set a writable SDK
`BaseDirectory` instead of relying on runtime defaults. `CopilotAnalysis:BaseDirectory`
is the configured parent only: a run must never give that parent directly to
the SDK. Before any filesystem or SDK operation, the run reserves its
catalog-owned opaque child under that exact parent as defined in
[raw-store normalization](raw-store-normalization.md#analysis-sdk-directory-capture-and-cleanup-d3),
and gives only that child to the SDK. The operation lease remains held and is
renewed until both the SDK Session and Client have been disposed. The runner
must not persist provider API keys or raw provider errors containing
credentials. An ownership/setup failure records only the fixed message `Local
analysis ownership could not be established.`; any other SDK failure records
only `SDK analysis failed.`. Neither message, events, nor diagnostics may
contain a local path, raw value, credential, secret, or exception text.

## Local Ingestion Monitor Windows Startup

Windows users may register the Local Ingestion Monitor as a user-level Windows
Task Scheduler task through `scripts/local-monitor/`. This is an operational
startup surface for the existing monitor process, not a new telemetry source,
schema, API, or shared service.

Default task behavior:

- task name: `CopilotAgentObservability LocalMonitor`.
- trigger: current user logon.
- principal: current user, interactive logon, least privileges.
- action: PowerShell script wrapper that starts either the repository-local
  `dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db <db>
  --url http://127.0.0.1:4320` path or the installed published executable.
- multiple instances: ignore new.
- restart on failure: short bounded retry.
- network availability is not required.
- client routing settings are not modified.

Scheduled startup defaults:

```text
URL: http://127.0.0.1:4320
DB:  %LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\raw-store.db
Install root: %LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\
Logs/state:  %LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\
```

Startup registration is never implied by install. Users can install the
published app without registering a task, start it immediately without enabling
logon startup, or register/enable/disable/unregister the task later. Task
registration remains current-user only and must not write VS Code, Copilot CLI,
or Codex routing settings.

Windows users may separately persist the monitor routing environment for the
current Windows user through `install-user-env.ps1` and remove it with
`uninstall-user-env.ps1`. This writes user-scope environment variables through
the Windows user environment API (`HKCU\Environment`), broadcasts an
environment-change notification, and never uses `setx`. The setting applies
only to newly started processes; already-running VS Code, terminals, and
Copilot CLI processes must be restarted. The default endpoint is
`http://127.0.0.1:4320`, non-loopback URLs are rejected, and the script sets no
`client.kind` resource attribute because one global user environment is shared
by VS Code and Copilot CLI.

The startup wrapper must preserve the Local Monitor run boundary:

- only loopback HTTP URLs are accepted (`127.0.0.1`, `localhost`, or `::1`).
- `--sanitized-only` remains available for metadata-only always-on runs.
- wrapper logs record only operational facts such as start/stop time, process id,
  exit code, configured URL, health result, and deterministic error code.
- wrapper logs and state files do not contain raw monitor content, credentials,
  tokens, PII, or raw OTLP payloads.
- Task registration scripts do not write VS Code, Copilot CLI, or Codex routing
  configuration. Users can either apply per-shell Config CLI output or use the
  explicit user environment scripts for current-user default routing.

Validation:

- automated tests cover script existence, PowerShell parsing, stable defaults,
  generated task settings, user environment settings, status output,
  published-mode command construction, and log-boundary string checks.
- Windows integration validation should cover start/status/stop, duplicate
  start prevention, non-loopback URL rejection, occupied-port diagnostics, dry-run
  task registration output, actual task registration, logon trigger behavior,
  uninstall, and trace receipt from VS Code / Copilot Chat.

## Local Ingestion Monitor Release ZIP

The Local Ingestion Monitor may be distributed as a Windows x64 Release ZIP.
This is a packaging and startup surface for the same monitor process; it does
not add telemetry inputs, schemas, endpoints, API fields, raw routes, or a shared
service boundary.

Release ZIP requirements:

- GitHub Actions creates `local-monitor-win-x64.zip` through
  `scripts/local-monitor/package-release.ps1`.
- publish mode is `win-x64` self-contained folder publish. Initial support does
  not require single-file publish.
- the ZIP contains `app/`, `scripts/`, `README.md`, `manifest.json`, and notices.
- the ZIP scripts include `install-user-env.ps1` and `uninstall-user-env.ps1`
  for explicit current-user environment install / uninstall.
- the ZIP excludes runtime DB, logs, state, raw OTLP payloads, real user data,
  generated monitor output, credentials, and repository-forbidden data.
- using the ZIP on a user machine must not require `dotnet run`, `dotnet build`,
  `dotnet restore`, .NET SDK, .NET Runtime, or ASP.NET Core Runtime.
- install is per-user by default and copies the app to
  `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\`.
- runtime DB / logs / state remain under
  `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\` and are separate from
  the app install root.
- uninstall removes the installed app and task/state by default, but keeps DB
  and logs unless the user passes the explicit remove-data option.

Status output must report installed/not installed, running/stopped,
health/readiness, URL, DB path, log path, install root, app version, startup
registered/enabled state, task name, and sanitized-only mode.

## Resource Attributes

Expected collection metadata（収集期待 Resource Attributes）:

```text
user.id
user.email
team.id
department
client.kind
experiment.id
```

**2層モデル:** 上記 6 属性は収集時の expected collection metadata として維持する。repository-safe 自動欠落検証は `client.kind` と `experiment.id` のみを対象とし、`user.id` / `user.email` / `team.id` / `department` は欠落しても `missing-required-attribute` health row を生成しない。`team.id` / `department` は repository-safe dataset では未知 resource 属性として保持され、必須検証には含めない。PII / 組織属性の収集健全性は local monitor 側（loopback 既定表示）でのみ観察する。

`trace_id` は Resource Attribute ではなく source trace reference である。参照整合性のため欠落時に collection health row を出力してよいが、Resource Attribute 必須検証とは別枠で扱う。

Recommended `client.kind` values:

```text
vscode-copilot-chat
copilot-cli
codex-app
```

Recommended:

```text
vcs.repository.name
workspace.name
task.id
task.category
task.run_index
experiment.condition
prompt.version
repo.snapshot
agent.variant
skill.version
mcp.profile
cli.wrapper.version
```

Sprint16 uses only the existing recommended attributes `vcs.repository.name`,
`workspace.name`, and `repo.snapshot` as optional sources for the Local Monitor
projection fields `repository_name`, `workspace_label`, and `repo_snapshot`.
`repo.name` is not a repository label source for this surface.
These attributes are not required for repository-safe datasets, and their
absence must not create collection-health failures. Missing values remain null
in `/api/monitor/*`; Canvas helper UI displays `unknown repository` when it
cannot derive a repository label.

## Canvas Analysis Options

Sprint17 keeps the Canvas helper analysis trigger on the existing
`session.send()` path. The helper does not start the Local Monitor Copilot raw
analysis runner and does not call `/traces/{traceId}/analysis`.

Local Monitor exposes `GET /api/analysis/options` as sanitized configuration for
the Canvas helper. It returns analysis profiles, configured model metadata,
default profile/model, reasoning effort values, and timeout hints. Initial
profiles are:

| Profile | Timeout hint | Default reasoning |
| --- | ---: | --- |
| `fast` | 60s | `low` |
| `standard` | 180s | `medium` |
| `deep` | 600s | `high` |

Model metadata is derived from local `CopilotAnalysis:Models:*` configuration
and includes only id, display name, provider display name,
`supports_reasoning_effort`, and `is_default`. The endpoint must not return API
keys, provider base URLs, local SDK state paths, raw telemetry, PII, prompt or
response bodies, tool arguments/results, credentials, or tokens.

The Canvas helper proxies this endpoint through its token-gated
`GET /api/analysis/options` route. `POST /analyze` accepts requested
`profile`, `requestedModel`, `requestedReasoningEffort`, and
`requestedTimeoutSeconds`, validates them against the options response, includes
them in the generated bounded Copilot instruction, and returns dispatch metadata
only. These fields are requested controls; because `session.send()` has no
per-message model/reasoning/execution-timeout control, the UI and response must
not claim they were enforced. Final result metadata is out of scope unless a
later feature safely correlates it from observed telemetry.

## Codex App Boundary

Codex App / app-server OTel routing config belongs in user-level `~/.codex/config.toml`.
Project-local `.codex/config.toml` is not a routing source of truth.

## Aspire AppHost Boundary

The Aspire AppHost is retained for build coverage and historical local dashboard connectivity context.
Do not add Langfuse, Collector, Config CLI, raw local receiver, ServiceDefaults, Web app, DB, Redis, or Worker resources to AppHost by default.
If resources are added later, define MCP exposure and sensitive telemetry exclusions before implementation.
