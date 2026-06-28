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
`agent_invocation_count`, `duration_ms`, `primary_model` (see
[raw-store-normalization.md](raw-store-normalization.md) for the full schema).

`/api/monitor/traces/{traceId}/spans` returns sanitized per-span rows from
`monitor_spans` for the given `trace_id`. Same `after`/`limit` contract as the
other cursor endpoints. Each row includes operation, category, tool/MCP name,
agent name, model, token counts, status, error type, timing, and hierarchy
(`parent_span_id`, `span_ordinal`). No raw content or PII.

Raw / PII exposure follows the Local Ingestion Monitor boundary in
[../security-data-boundaries.md](../security-data-boundaries.md): raw body
(tool call arguments / results, sub-agent instructions / responses, system
prompt) and PII (`user.id` / `user.email`) are shown **by default**
(server-rendered, inert text) on raw-bearing routes. The trace-detail page
renders a bounded inline raw preview and links to `GET /traces/{rawRecordId}/raw`
for the full single-record payload. `/api/monitor/*` and SSE never carry raw /
PII. The `--sanitized-only` flag restores metadata-only mode (raw-bearing routes
return `404`, PII is excluded). Raw / PII is never logged or committed.

Live validation for the monitor records the same evidence as the
`raw-local-receiver` profile, plus the monitor port, the VS Code / GitHub
Copilot extension version, and whether `--sanitized-only` was set.

## Resource Attributes

Required:

```text
user.id
user.email
team.id
department
client.kind
experiment.id
```

Recommended `client.kind` values:

```text
vscode-copilot-chat
copilot-cli
codex-app
```

Recommended:

```text
repo.name
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

## Codex App Boundary

Codex App / app-server OTel routing config belongs in user-level `~/.codex/config.toml`.
Project-local `.codex/config.toml` is not a routing source of truth.

## Aspire AppHost Boundary

The Aspire AppHost is retained for build coverage and historical local dashboard connectivity context.
Do not add Langfuse, Collector, Config CLI, raw local receiver, ServiceDefaults, Web app, DB, Redis, or Worker resources to AppHost by default.
If resources are added later, define MCP exposure and sensitive telemetry exclusions before implementation.
