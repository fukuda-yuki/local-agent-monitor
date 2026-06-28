# Raw Store And Normalization Specification

## Scope

This layer converts raw telemetry into repository-local deterministic datasets.
It does not require Langfuse UI.

## Input

Accepted input:

- saved raw OTLP JSON file。
- SQLite raw store created by `ingest-raw`。
- SQLite raw store populated by the `raw-local-receiver` profile。

Raw payloads may include prompt, response, tool arguments / results, path information, identity-bearing attributes, and credential-like strings.
Raw payloads must not be committed.

## Raw Store

Default local path:

```text
data/raw-store.db
```

`data/` is local runtime data.
The SQLite store is not a shared operational database.

Rejected for current default storage scope:

- PostgreSQL as default raw telemetry store。

The `raw-local-receiver` profile owns local receiver behavior.
This layer owns deterministic storage and normalization after raw telemetry is
available.

## Commands

```text
config-cli ingest-raw <raw.json> --db <raw-store.db>
config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
```

`normalize-raw` may read either a raw store or a raw OTLP JSON file.
At least one output option must be provided by commands that require output.

The local receiver may write directly to the SQLite raw store or produce a raw
OTLP file that can be passed to `ingest-raw`.
Either path must preserve the same normalization output contract.
Receiver-created raw stores and raw OTLP files are local runtime data. They
must remain outside repository-safe outputs, and tests must use synthetic
fixtures rather than captured raw Copilot payloads.

## Normalized Measurement Responsibilities

Normalization must:

- preserve trace-level reference IDs.
- derive `client_kind`, task and experiment attributes when present.
- classify common logical categories such as LLM call, tool call, permission, file operation, shell command, error, user interaction.
- handle unknown span names without failing only because span names drift.
- produce unknown span / attribute evidence for collection health.
- avoid copying raw prompt / response / tool arguments / tool results into repository-safe outputs.

The output contract is defined in [../interfaces/measurement-dataset.md](../interfaces/measurement-dataset.md).

## Local Ingestion Monitor Storage And Projection

The Local Ingestion Monitor reuses this raw store. It adds sanitized projection
tables and concurrency requirements on top of the existing `raw_records` store
without changing the normalization output contract.

Schema and migration:

- a `schema_version` table plus an idempotent, additive migration that adds the
  `monitor_ingestions` and `monitor_traces` projection tables to an existing
  `raw_records`-only database.
- migration failure ⇒ `/health/ready` reports not-ready.
- `normalize-raw` and the existing raw-store / raw-OTLP-file contracts remain
  compatible; the projection tables are additive.

Concurrency (single writer, concurrent external readers):

- a single ingestion writer worker owns all writes; HTTP `2xx` is returned only
  after the writer commits.
- WAL mode, `busy_timeout`, and read transactions allow `normalize-raw`,
  dashboard generation, and diagnosis (the prompt self-improvement loop) to read
  the same database while the monitor runs.
- the projection worker retries on `SQLITE_BUSY`.

Sanitized projections:

- `monitor_ingestions` / `monitor_traces` use a per-table allowlist schema and
  carry sanitized metadata only.
- raw prompt / response / tool content is never copied into the projection
  tables, list responses, or the SSE stream.
- PII attributes (`user.id` / `user.email`) are excluded from the default
  projections.
- a projection worker processes unprocessed `raw_records`, catches up on
  startup, and does not lose raw on projection failure (retry / recorded failure
  state).
- a raw record with no non-empty `trace_id` is still projected into
  `monitor_ingestions` (its `trace_id` column is nullable) but contributes **no**
  `monitor_traces` row (consistent with "one row per `trace_id`"); it must not
  remain unprocessed or inflate projection lag.
- a single raw record whose payload carries multiple `trace_id`s fans out to one
  `monitor_traces` row per `trace_id`; it is not collapsed to a primary trace.
- the cursor read API (`GET /api/monitor/ingestions` / `GET /api/monitor/traces`)
  reads the projection tables only — never `raw_records.payload_json` — and its
  request / response / cursor shape is pinned in
  [telemetry-ingestion.md](telemetry-ingestion.md).
- projection lag (the age in seconds of the oldest unprocessed `raw_records`
  row) ≥ `projection-lag-threshold-seconds` (default `60`) ⇒ `/health/ready`
  returns `503`; lag above zero but under the threshold ⇒ a `degraded` `2xx`. The
  readiness body schema and the full threshold / configuration surface are
  defined in [telemetry-ingestion.md](telemetry-ingestion.md).

Projection table allowlist schema:

The additive migration creates these tables **empty**; row population,
aggregation, and cursor queries are owned by the projection worker milestone.
Each table is a per-table allowlist: only the columns below are stored, all
sanitized. Raw `payload_json`, raw `resource_attributes_json`, prompt / response
/ tool content, and PII (`user.id` / `user.email`) are never columns here. Field
names that overlap the normalized measurement dataset follow
[../interfaces/measurement-dataset.md](../interfaces/measurement-dataset.md)
semantics. Later milestones may add columns only additively (bump
`schema_version` + `ALTER TABLE ADD COLUMN`); existing columns are stable.

`monitor_ingestions` (one row per ingested `raw_records` row; drives the live
ingestion list and the `/api/monitor/ingestions` cursor):

| Column | Type | Notes |
| --- | --- | --- |
| `id` | INTEGER PK | projection row id |
| `raw_record_id` | INTEGER NOT NULL UNIQUE | references `raw_records.id`; cursor key and idempotency guard |
| `received_at` | TEXT NOT NULL | ISO-8601, copied from `raw_records` for ordering without reading the payload |
| `source` | TEXT NOT NULL | `raw-otlp` / `collector-output` / `langfuse-export` |
| `trace_id` | TEXT NULL | trace-level reference |
| `client_kind` | TEXT NULL | sanitized client classification |
| `span_count` | INTEGER NULL | count only, no span content |
| `projected_at` | TEXT NOT NULL | ISO-8601 time the projection worker wrote the row |

`monitor_traces` (one row per `trace_id`; drives the traces list and the
`/api/monitor/traces` cursor):

| Column | Type | Notes |
| --- | --- | --- |
| `id` | INTEGER PK | projection row id |
| `trace_id` | TEXT NOT NULL UNIQUE | trace-level reference and aggregation key |
| `client_kind` | TEXT NULL | sanitized client classification |
| `experiment_id` | TEXT NULL | measurement-dataset semantics |
| `task_id` | TEXT NULL | measurement-dataset semantics |
| `task_category` | TEXT NULL | measurement-dataset semantics |
| `agent_variant` | TEXT NULL | measurement-dataset semantics |
| `prompt_version` | TEXT NULL | measurement-dataset semantics |
| `span_count` | INTEGER NULL | count only |
| `tool_call_count` | INTEGER NULL | count only |
| `error_count` | INTEGER NULL | count only |
| `first_seen_at` | TEXT NULL | ISO-8601, earliest ingestion for the trace |
| `last_seen_at` | TEXT NULL | ISO-8601, latest ingestion for the trace |
| `projected_at` | TEXT NOT NULL | ISO-8601 time the projection worker wrote/updated the row |

`monitor_spans` (one row per span; drives the agent-execution detail view and
the `/api/monitor/traces/{traceId}/spans` cursor):

| Column | Type | Notes |
| --- | --- | --- |
| `id` | INTEGER PK | projection row id |
| `raw_record_id` | INTEGER NOT NULL | references `raw_records.id` |
| `trace_id` | TEXT NOT NULL | trace-level reference |
| `span_id` | TEXT NULL | span-level reference |
| `parent_span_id` | TEXT NULL | hierarchy reference |
| `span_ordinal` | INTEGER NOT NULL | intra-record ordering for idempotency |
| `operation` | TEXT NULL | `invoke_agent` / `chat` / `execute_tool` / `execute_hook` |
| `category` | TEXT NULL | `llm_call` / `tool_call` / `agent_invocation` / `hook` / `error` / `unknown` |
| `tool_name` | TEXT NULL | sanitized (guard + max length) |
| `tool_type` | TEXT NULL | `function` / `extension` (MCP) |
| `mcp_tool_name` | TEXT NULL | sanitized (guard + max length) |
| `mcp_server_hash` | TEXT NULL | client-provided hash only |
| `agent_name` | TEXT NULL | sanitized (guard + max length) |
| `request_model` | TEXT NULL | model identifier |
| `response_model` | TEXT NULL | model identifier |
| `input_tokens` | INTEGER NULL | per-span |
| `output_tokens` | INTEGER NULL | per-span |
| `total_tokens` | INTEGER NULL | per-span |
| `reasoning_tokens` | INTEGER NULL | per-span |
| `cache_read_tokens` | INTEGER NULL | per-span |
| `cache_creation_tokens` | INTEGER NULL | per-span |
| `status` | TEXT NULL | `ok` / `error` |
| `error_type` | TEXT NULL | class token only (guard + max length) |
| `finish_reasons` | TEXT NULL | comma-separated enum tokens |
| `conversation_id` | TEXT NULL | reference id |
| `duration_ms` | REAL NULL | computed from span start / end |
| `start_time` | TEXT NULL | ISO-8601 |
| `end_time` | TEXT NULL | ISO-8601 |
| `projected_at` | TEXT NOT NULL | ISO-8601 |

Idempotency key: `(raw_record_id, span_ordinal)` UNIQUE — tolerates missing or
duplicate `span_id`.

Additive rollup columns on `monitor_traces` (Sprint9):

| Column | Type | Notes |
| --- | --- | --- |
| `input_tokens` | INTEGER NULL | trace-level (sum of root `invoke_agent` usage, or fallback sum of `chat` spans) |
| `output_tokens` | INTEGER NULL | trace-level |
| `total_tokens` | INTEGER NULL | trace-level |
| `turn_count` | INTEGER NULL | count of `chat` / LLM spans |
| `agent_invocation_count` | INTEGER NULL | count of `invoke_agent` spans |
| `duration_ms` | REAL NULL | trace duration |
| `primary_model` | TEXT NULL | most-used model |

Per-field sanitization policy:

- **free-form name fields** (`tool_name`, `mcp_tool_name`, `agent_name`, span
  `name`): stored only after passing the existing `MeasurementSanitizer`
  unsafe-value guard (rejects email / path / secret-like values), and truncated
  to a pinned max length. A value that fails the guard is dropped (the row keeps
  its other columns), not stored verbatim.
- **`error_type`**: the class token only (e.g. `timeout`, `ECONNREFUSED`,
  `TokenExpiredError`). Exception messages and free-form `error` /
  `exception.message` attributes are never copied. Values must be identifier-like
  tokens (`[A-Za-z0-9._]`) and are truncated to the pinned max length; malformed
  strings, paths, emails, and message text are dropped.
- **`finish_reasons`**: enum-like tokens (`stop`, `length`, …) from a fixed set;
  unknown string tokens pass the guard + max length. Malformed serialized arrays
  are dropped rather than stored as raw text.
- **`mcp_server_hash`**: stored as the client-provided hash only; the unhashed
  server name is never derived or stored.
- **reference ids** (`trace_id`, `span_id`, `parent_span_id`, `conversation_id`):
  treated as opaque reference ids per `requirements.md` §5 and §8.

Token rollup rule (no double count):

- per-turn tokens = the `chat` span's own `gen_ai.usage.*` (one turn = one
  `chat` / LLM span).
- per-trace total = the trace's root `invoke_agent` usage when present;
  otherwise the sum of `chat` spans (fallback when no agent-level total is
  emitted).
- if a trace has multiple root `invoke_agent` spans with usage, the trace-level
  token fields are the sum of those root `invoke_agent` usage fields.
- never add `invoke_agent` totals to `chat` per-call tokens. Sub-agent (child
  `invoke_agent`) usage is attributed to that sub-agent and rolled into the
  parent only through the parent's own agent-level total, not by re-summing
  child `chat` spans.
- token rollup is computed with a range-safe accumulator. Because the public
  projection rows expose nullable `int` token fields, a derived or summed token
  field that exceeds the `int` range is stored as `NULL` rather than wrapped.

Projection-version and backfill:

- a new `schema_version` entry tracks the span-projection version independently
  of the existing ingestion/trace projection version.
- existing Sprint8-processed `raw_records` are re-projected for spans and the
  new `monitor_traces` rollup columns. Span-projection progress is tracked
  independently of `monitor_ingestions`, so a record that was already projected
  for ingestion/trace but not yet for spans is detected and not hidden as
  backlog 0.
- mandatory upgrade test from a Sprint8-populated DB verifies backfill
  correctness.

Raw access (default-on):

- raw body (tool call arguments / results, sub-agent instructions / responses,
  system prompt) and PII (`user.id` / `user.email`) are shown **by default** on
  raw-bearing routes: the trace-detail page renders a bounded inline preview and
  links to the full single-record view, while `GET /traces/{rawRecordId}/raw`
  renders one full raw record. Both are server-rendered as inert text. There is
  no JSON raw API.
- `--sanitized-only` restores metadata-only mode: raw-bearing routes return
  `404`, PII is excluded. No cacheable raw response is generated.
- raw-bearing routes enforce same-origin (`Origin` / `Sec-Fetch-Site` ⇒
  cross-site `403`) and `Cache-Control: no-store`.
- raw / PII is never part of the projection tables, list responses, SSE
  notifications, `/api/monitor/*` JSON, or logs.
- the full raw / PII trust boundary and the route contract are defined in
  [../security-data-boundaries.md](../security-data-boundaries.md).

## Validation

Use synthetic fixtures for automated tests.
Live Copilot execution is manual validation and must record environment, settings, trace id or equivalent identifier, confirmed items, and unconfirmed items.
