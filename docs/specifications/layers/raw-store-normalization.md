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

Additive repository metadata columns on `monitor_traces` (Sprint16, monitor
projection schema version 3):

| Column | Type | Notes |
| --- | --- | --- |
| `repository_name` | TEXT NULL | sanitized display label derived only from `vcs.repository.name`; `repo.name` is ignored |
| `workspace_label` | TEXT NULL | sanitized display label derived only from `workspace.name`; not an absolute path |
| `repo_snapshot` | TEXT NULL | sanitized branch / commit / snapshot label derived only from `repo.snapshot` |

Additive cache / status rollup columns on `monitor_traces` (Sprint18, monitor
projection schema version 4, D044):

| Column | Type | Notes |
| --- | --- | --- |
| `cache_read_tokens` | INTEGER NULL | trace-level cache-read sum; follows the same root-`invoke_agent`-else-`chat` no-double-count branch as the token headline |
| `cache_creation_tokens` | INTEGER NULL | trace-level cache-creation sum; same branch rule |
| `trace_status` | TEXT NULL | `ok` (no error spans) / `unrecovered` (last span by `start_time`, fallback `span_ordinal`, is an error) / `recovered` (an error occurred but a later span succeeded) |

Token-usage convention (pinned; verified against live Copilot CLI and VS Code
Copilot Chat emissions):

- `gen_ai.usage.input_tokens` counts the **full prompt including cache-read
  tokens** (`cache_read_tokens` is a subset of `input_tokens`), and
  `total_tokens = input_tokens + output_tokens`. All cache displays assume this
  inclusive convention: uncached input = `input_tokens − cache_read_tokens`
  (clamped at 0), cache read rate = `cache_read_tokens / input_tokens`. A span
  without cache attributes is treated as 0 cache read. Sources that emit an
  exclusive input convention (input without cache reads) are out of scope; no
  per-source dual path is added.

Display rules the monitor UI derives from these columns:

- **Token headline (実消費)**: the overview KPI headline shows 実消費トークン =
  uncached input + output (= `total_tokens − cache_read_tokens`, clamped at 0),
  because agent sessions resend the full history every turn and the
  cache-inclusive total is dominated by cache reads. The previous-period
  comparison uses the same 実消費 basis. The cache-inclusive total and the
  cache-read sum remain visible as a secondary breakdown line on the same card.
  Pre-v4 rows with NULL cache columns count as fully uncached in this headline
  (documented limitation, D044 no-backfill).
- **Cache-rate basis line**: the キャッシュ読取率 KPI card shows its numerator /
  denominator (`読取 cache_read ÷ 入力 cache-aware input`) so the rate is
  verifiable from the displayed numbers.
- **Effective-input conversion**: the overview KPI and the trace-detail cache
  column show an "実効入力換算" figure computed as
  `cache_read_tokens x 0.1 + uncached input tokens` (cache reads are weighted at
  0.1x as a cost approximation).
- **Input-token guideline line**: the error-analysis-mode "入力トークンの推移"
  chart draws a dashed guideline at **128K input tokens** per turn; bars that
  exceed it are highlighted. This is a fixed display guideline, not a
  configurable threshold and not part of the readiness contract.

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
- **repository metadata** (`repository_name`, `workspace_label`,
  `repo_snapshot`): stored only after the existing unsafe-value guard and
  truncation used for monitor projection display labels. Values that look like
  paths, emails, secrets, tokens, credentials, or other unsafe free-form content
  are dropped, not stored verbatim.

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

- the store keeps a single `schema_version` entry (`component = 'monitor'`) that tracks the monitor projection schema as a whole, including ingestion / trace projection and span projection. There is no separate span-projection schema version entry; the span-projection phase is versioned together with the monitor schema.
- existing Sprint8-processed `raw_records` are re-projected for spans and the
  new `monitor_traces` rollup columns. Span-projection progress is tracked
  independently of `monitor_ingestions` via `monitor_ingestions.span_projected_at`, so a record that was already projected
  for ingestion/trace but not yet for spans is detected and not hidden as
  backlog 0.
- mandatory upgrade test from a Sprint8-populated DB verifies backfill
  correctness.
- Sprint16 raises the monitor projection schema to version 3 with nullable
  `monitor_traces` repository metadata columns. Existing projected rows are not
  automatically backfilled for these columns; they remain `NULL` until new
  telemetry is ingested or the database is regenerated by an explicit separate
  operation.
- Sprint18 raises the monitor projection schema to version 4 with the nullable
  `monitor_traces` cache / status rollup columns above (D044). The same
  no-backfill rule applies: pre-v4 rows keep `NULL`, are excluded from
  cache-rate denominators on the overview endpoint, and read as "unknown" in
  the trace-list status filter.

Raw access (default-on):

- raw body (tool call arguments / results, sub-agent instructions / responses,
  system prompt) and PII (`user.id` / `user.email`) are shown **by default** on
  raw-bearing routes: the trace-detail page renders a bounded inline preview and
  links to the full single-record view, while `GET /traces/{rawRecordId}/raw`
  renders one full raw record. Both are server-rendered as inert text. There is
  no JSON raw API.
- the dashboard (`/`) and trace-list (`/traces`) pages also render a single
  representative **user-prompt label per trace** by default (extracted
  server-side from the trace's raw OTLP payload, truncated, inert text) so a
  trace is identifiable by what the user asked (D032); only this short label is
  raw, all other columns stay sanitized metadata.
- `--sanitized-only` restores metadata-only mode: raw-bearing routes return
  `404`, the dashboard / trace-list prompt label is omitted (a shortened TraceId
  is shown), PII is excluded. No cacheable raw response is generated.
- raw-bearing routes enforce same-origin (`Origin` / `Sec-Fetch-Site` ⇒
  cross-site `403`) and `Cache-Control: no-store`.
- raw / PII is never part of the projection tables, list responses, SSE
  notifications, `/api/monitor/*` JSON, or logs.
- the full raw / PII trust boundary and the route contract are defined in
  [../security-data-boundaries.md](../security-data-boundaries.md).

## Session Storage And Normalization

Issue #51 adds a separate additive Session subsystem. It does not add Session
responsibilities to `RawTelemetryStore.cs` and does not change `raw_records`,
the normalized measurement schema, candidate schemas, dashboard dataset schema,
or the existing monitor projection tables/cursor.

The Session subsystem owns these additive tables:

- `sessions`
- `session_native_ids`
- `session_runs`
- `session_events`
- `session_event_content`
- `session_projection_state`

`sessions`, `session_runs`, and `session_events` use local UUIDv7 string IDs.
`session_native_ids` preserves source identity separately from local IDs.
`session_events` stores normalized event metadata; raw-bearing content is
secret-filtered and stored separately in `session_event_content`.
`session_projection_state` owns the dedicated post-monitor-projection OTel
enrichment cursor.

Source idempotency uses SDK event ID for `copilot-sdk-stream`, Hook canonical
hash for `copilot-compatible-hook`, or exact OTel trace/span identity for OTel
enrichment. A Session merge is permitted only for an identical native session
ID, explicit resume/handoff linkage, or exact trace context. Repository and
timestamp proximity never merge Sessions.

The normalizer assigns exactly one completeness state:

- `unbound`: OTel-only and not linked to a native session ID.
- `partial`: native ID exists but lifecycle or input family is incomplete.
- `rich`: instruction, lifecycle, and SDK/Hook or OTel evidence exist, but some
  content or terminal evidence is missing.
- `full`: surface-required start-to-end evidence exists, there is no unsupported
  version or ingest gap, and OTel enrichment is exact-linked.

The post-projection OTel enricher advances only its dedicated Session cursor.
It runs after existing monitor projection and must not advance or redefine the
existing monitor cursor/readiness contract. A byte-for-byte trace context
already recorded on a Session event may link OTel evidence. Exact
`gen_ai.conversation.id` may bind/enrich only when byte-for-byte equal to an
already-recorded native session ID; otherwise OTel remains `unbound`.
`client_kind` never participates in binding or merge and may only confirm
whether `hook-unknown` is `copilot-cli` or `vscode`. Inexact evidence does not
merge a Session or produce `full` completeness.

Session schema migration runs during Local Monitor startup. Any migration
failure fails host construction, matching the analysis-store migration; it is
not represented by a new readiness check. Existing readiness body fields,
thresholds, units, configuration names, and HTTP status mapping remain unchanged.

Raw event content receives `expires_at = captured_at + 90 days`. Expiry changes
the content read to `410` / `expired_pending_deletion`; the row remains stored.
Automatic physical deletion, pin, and delete-now are Issue #57 scope. The full
write/read shape is defined in
[Canvas Session workspace](../interfaces/canvas-session-workspace.md).

### Source capability semantic contract v1

The source-capability JSON Schema and manifests declare structure and available
capabilities; this section owns their normalization meaning. Field-family
authority is applied before persistence: exact available OTel identity,
hierarchy, and timing win for those families; Hook/SDK native lifecycle and
explicit event identity win for those families. Historical summaries are
allowlist-only (`model_tokens.*`, `retry_attempt.*`, `errors`) and cannot
create, merge, or replace identity, hierarchy, timing, lifecycle, or explicit
event identity. A weak value never overwrites a strong one, and a missing value
never overwrites any value.

Per-field provenance records the actual contributing adapter ID that supplied
the field, such as `otel-http`, `copilot-compatible-hook`, or
`copilot-sdk-stream`; the composite `otel-http+copilot-compatible-hook`
manifest label must never be per-field provenance. It also records source
version or schema fingerprint; source event or trace/span identity;
capture/content state; and normalization version. A value lacking any required
provenance is retained only as non-authoritative observed context where the
existing storage boundary permits it; it is not used to upgrade completeness,
bind identity, or infer a replacement. Repository/workspace/timestamp context
never supplies provenance or identity. No heuristic merge and no synthetic span
are permitted. Provenance absence uses the existing fixed reasons only: missing
source event/trace-span identity is `missing_trace_context`; missing capture/
content state is `content_capture_disabled`; missing actual adapter, source
version/schema fingerprint, or normalization version is `schema_drift_detected`.

### Deterministic completeness decision

Completeness is a pure calculation from declared surface requirements and these
observed facts: native session ID; exact trace context and enabled trace signal;
required lifecycle and input families; content-capture state and required
content; terminal evidence; supported source version; ingest continuity;
whether evidence is Hook-only or historical-summary-only; recognized span kind;
schema agreement; and whether the declared source is enabled. It neither
reconstructs missed events nor guesses a span or source field.

Status ranks are ordered `unbound < partial < rich < full`. First calculate the
base status: missing native ID is `unbound`; otherwise missing required
lifecycle, input, or SDK/Hook/OTel evidence-family fact is `partial`; otherwise
missing required content, terminal, exact-enrichment, or surface-required
evidence, or an unsupported source version or ingest gap, is `rich`; otherwise
it is `full`. A missing lifecycle/input fact does not introduce a twelfth
reason code.

Every schema reason has exactly one maximum status:

| Reason code | Maximum status | Why it cannot be higher |
| --- | --- | --- |
| `missing_native_session_id` | `unbound` | No native Session can bind the evidence. |
| `missing_trace_context` | `rich` | Exact-linked OTel enrichment is absent. |
| `trace_signal_disabled` | `rich` | Exact-linked OTel enrichment cannot be obtained. |
| `content_capture_disabled` | `rich` | Required captured content is unavailable. |
| `unsupported_source_version` | `rich` | It is an existing #51 full blocker after the `partial` checks. |
| `ingest_gap` | `rich` | With lifecycle/input present it is an existing #51 full blocker; a missing start remains a `partial` base fact. |
| `hook_only` | `rich` | Native Hook evidence may exist, but exact-linked OTel enrichment is absent. |
| `historical_summary_only` | `partial` | Allowlisted summaries cannot establish lifecycle or explicit event input. |
| `unknown_span_kind` | `rich` | The span cannot qualify as required exact enrichment. |
| `schema_drift_detected` | `partial` | Required declared input agreement is not established. |
| `planned_source_not_enabled` | `unbound` | A disabled planned source supplies no observed native Session input. |

For a valid fact/reason combination, final status is the minimum rank of the
base status and every present reason maximum. An unknown reason is invalid
schema drift and must be rejected, never ignored. Reasons are de-duplicated
and emitted in the canonical schema order below, never observation order. Thus
an `unbound` base plus a `rich` reason remains `unbound`, and a `partial` base
plus a `rich` reason remains `partial`; `historical_summary_only` can never
reach `full`. `historical_summary_only` and `schema_drift_detected` are future
adapter-handoff `partial` reasons with no distinct current #51 calculator
boolean; they must not be conflated with `unsupported_source_version`.

The output reason list is de-duplicated and emitted in this stable canonical
order, never observation order:

1. `missing_native_session_id`
2. `missing_trace_context`
3. `trace_signal_disabled`
4. `content_capture_disabled`
5. `unsupported_source_version`
6. `ingest_gap`
7. `hook_only`
8. `historical_summary_only`
9. `unknown_span_kind`
10. `schema_drift_detected`
11. `planned_source_not_enabled`

The fixed status vocabulary is exactly `unbound`, `partial`, `rich`, and
`full`. Completeness does not alter Issue #51 exact identity or Issue #49 Agent
ownership.

## Local Analysis Persistence

The Local Monitor adds local-only analysis tables for Copilot SDK raw analysis.
They are additive and do not change normalized measurement, candidate, dashboard
dataset, or `/api/monitor/*` contracts.

Tables:

- `monitor_analysis_runs`: one local run per raw analysis request. Stores trace
  id, optional raw record id/span id, focus, lifecycle status, timestamps, local
  raw-derived result markdown, and error message.
- `monitor_analysis_events`: local progress/event log for a run.
- `monitor_analysis_safe_summaries`: repository-safe allowlist summary for a run.

Raw analysis result markdown is local runtime data and must not be committed.
Repository-safe summary output must be generated from allowlisted metadata and
evidence references, not by copying arbitrary raw model output.

## Retention catalog v1

Issue #89 defines one separately versioned retention catalog in the same Local
Monitor SQLite database as the Session, monitor raw, and analysis data. It is
not an extension of `RawTelemetryStore.cs` and no source creates a parallel
catalog. Catalog/source SQLite writes and deletes share a connection and
transaction; file producers receive the catalog database by explicit injected
configuration and fail closed before creating raw files when it is unavailable.

The ownership key is exactly `(store_instance_id, store_kind, source_item_id)`.
An internal 32-byte ownership receipt uses SHA-256 over length-framed binary
UTF-8 domain `copilot-agent-observability/retention-owner-receipt/v1`, decoded
lowercase 32-hex store instance ID, closed store kind, canonical source identity,
authoritative timestamp text plus UTC ticks, store binding, and private 32-byte
source token. It uses no delimiter concatenation, trimming, case folding, or
normalization. Session binds canonical RFC4122/network-order event/session/run
GUIDs, kind, capture/expiry pairs, adapter, and source event ID; raw binds
positive record ID, received pair, and schema version; analysis binds positive
run ID, requested pair, and explicit null markers for optional record ID/span.
Comparison is fixed-time for exact 32-byte values. The primitive is not a raw
hash and exposes no token, receipt, raw value, path, credential, or secret.
`item_id` is opaque and stable. The closed v1 store-kind registry is
`session_event_content`, `raw_record`, `analysis_run_raw`, `sensitive_bundle`,
and `analysis_sdk_directory`. The closed lifecycle is `expiring`,
`retained_by_policy`, `expired_pending_deletion`, `deletion_queued`,
`deleting`, `deleted`, and `deletion_failed`. `not_captured` and `mixed` are
aggregate-only values and are never persisted item states. Inventory categories
are `required_cleanup`, `retained_by_policy`, `not_applicable`, and `blocked`.
The later approved retention-status detail positively allowlists
`inventory_category` on `RetentionItemSummary`; that detail is authoritative
over earlier generic DTO wording and the field is a closed inventory category,
never a locator or source identity.

`raw-default-90d` v1 applies to Session content, raw records, analysis raw, and
SDK directories. `sensitive-bundle-7d` v1 applies only to Sensitive Bundles.
Expiry is `captured_at + policy TTL`; Session timestamps are preserved exactly,
raw records use valid `received_at`, analysis uses valid `requested_at`, Bundle
uses its catalog reservation time, and SDK children use their owning analysis
request time. Missing or invalid legacy authority is blocked and read denied;
it is never replaced with current, import, restore, file, or reconciliation
time.

Reads require a matching readable catalog revision, exact source item, and an
active read lease. Expiry first commits irreversible `read_denied_at`, then
queues cleanup. A failed retry, restart, clock change, repair, or source absence
never restores readability. Queueing is idempotent by `item_id`; scan/claim
order is `expires_at ASC, item_id ASC`, with finite v1 limits (100 items, 30 s
scan, 2 workers, 5 attempts). Deletion requires an exact source identity,
adapter-owned ownership receipt, expected revision, and deletion lease. No
repository, workspace, path, trace, timestamp proximity, or prompt similarity
may identify a deletion target. SQLite source deletion and the `deleted`
tombstone/receipt are atomic. File deletion is journaled, forward-only, and
only mutates exact owned members after identity/marker/digest validation.

The immutable Issue #89 kickoff and inventory base are both
`11d6c587903f6ea97026d815f608231efea08d65`. The checked-in current-callsite
inventory is [issue-89-raw-read-callsite-inventory.md](../../sprints/issue-89-raw-read-callsite-inventory.md).
Sanitized projections, Session/Event metadata, safe summaries, receipts, and
tombstones are retained outputs, not raw store kinds. Caller-owned input files,
unimplemented receiver files, and external blobs are not cleanup targets. The
final closeout corpus is `retention-closeout-corpus-v1`; it must classify every
base-to-final raw-bearing creator and cannot treat future stores as covered.

## Validation

Use synthetic fixtures for automated tests.
Live Copilot execution is manual validation and must record environment, settings, trace id or equivalent identifier, confirmed items, and unconfirmed items.
