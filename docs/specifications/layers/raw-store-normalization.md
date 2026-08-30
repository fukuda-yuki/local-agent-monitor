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
- derive `client_kind` with the exact trace-scoped source resolver defined in
  `telemetry-ingestion.md`, aggregating all supplied raw records before emitting
  a trace row; derive task and experiment attributes when present.
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
- source attribution evidence is stored privately per raw record and trace in
  `source_trace_attribution_observations`, together with an idempotent trace
  entry in `source_trace_attribution_reconciliation_queue`. A validated adapter
  batch commits raw, Retention, source-schema evidence, attribution evidence,
  and queue work in one transaction. The supported direct raw-store writers
  (`ingest-raw` and `raw-local-receiver`) use the same attribution writer and
  atomically commit raw, Retention, attribution evidence, and queue work; they
  do not fabricate a source-schema observation. Boolean evidence is
  OR-aggregated across Resource blocks and records, so record order, batching,
  and duplicates cannot select a first/last value. The projection worker
  reconciles queued traces and contributing ingestions to the current aggregate
  resolution, including null when a conflict or unknown appears.
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

Monitor schema v10 adds
`source_trace_attribution_observations(raw_record_id, trace_id,
cli_candidate_observed, vscode_candidate_observed,
unknown_candidate_observed, relevant_evidence_observed)` and
`source_trace_attribution_reconciliation_queue(trace_id)`. The evidence table
intentionally has no foreign key to `raw_records`: retained diagnostic evidence
survives raw retention, while migration completeness is checked separately.
Runtime evidence insertion and queueing are atomic with the owning raw write
and its Retention registration. Queue removal is atomic with successful
projection reconciliation; a failed transaction retains the queue entry for an
idle-pass retry. Empty-queue passes open no write transaction.

The v10 startup transition reads only raw rows whose canonical Retention
catalog entry currently authorizes reading and whose ownership receipt still
matches. It inserts evidence idempotently and recomputes only the two
`client_kind` columns. An ingestion row is changed only when its raw row is
present and parseable. A trace row is changed only when projected span count
equals its complete span membership, every contributing ingestion has
completed span projection, and every contributing raw row is present,
parseable, represented by evidence, and has the exact ordinal/trace/span
identity of its projected spans. Otherwise prior attribution is left
untouched. Historical migration does not enqueue reconciliation. It never
changes spans, counts, tokens, timing, dispositions, projection timestamps,
Skill rows, Sessions, SSE, or public response shapes; a second startup is
state/byte idempotent.

A database declaring Monitor v10 must already have the exact attribution table
constraints, named trace index, and reconciliation queue primary key. Every
Monitor initializer rejects a malformed authority or future Monitor version
before journal-mode changes, schema repair, or any other database mutation.
A database declaring an older Monitor version must contain none of those three
v10-owned object names; even an exact empty or populated v10-shaped object is an
incomplete version vector and is rejected before mutation rather than adopted
or cleared.

Runtime reconciliation updates only queued `monitor_traces`, the ingestion rows
owned by their contributing raw records, and exact `otel-exact` / `otel.span`
Session event/run `source_surface` values. It never changes Session identity,
binding, match kind, native IDs, content, or other event/run fields. The Session
writer re-reads the authoritative trace family inside its write transaction so
a delayed exact OTel enrichment cannot reintroduce a stale surface after the
queue was consumed. Each worker pass reconciles before new projection; failure
aborts that pass, and all changes in one pass produce at most one SSE
notification.

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

Additive rollup columns on `monitor_traces` (Monitor Agent Execution View):

| Column | Type | Notes |
| --- | --- | --- |
| `input_tokens` | INTEGER NULL | trace-level (sum of root `invoke_agent` usage, or fallback sum of `chat` spans) |
| `output_tokens` | INTEGER NULL | trace-level |
| `total_tokens` | INTEGER NULL | trace-level |
| `turn_count` | INTEGER NULL | count of `chat` / LLM spans |
| `agent_invocation_count` | INTEGER NULL | count of `invoke_agent` spans |
| `duration_ms` | REAL NULL | trace duration |
| `primary_model` | TEXT NULL | most-used model |

Additive repository metadata columns on `monitor_traces` (Canvas Repository Metadata, monitor
projection schema version 3):

| Column | Type | Notes |
| --- | --- | --- |
| `repository_name` | TEXT NULL | sanitized display label derived from resource-scoped `vcs.repository.name`, or only when absent from the allowlisted canonical GitHub HTTPS `vcs.repository.url.full` repository segment; `repo.name` is ignored |
| `workspace_label` | TEXT NULL | sanitized display label derived only from `workspace.name`; not an absolute path |
| `repo_snapshot` | TEXT NULL | sanitized branch / commit / snapshot label derived only from `repo.snapshot` |

Additive cache / status rollup columns on `monitor_traces` (Local Monitor UI Redesign, monitor
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
  are dropped, not stored verbatim. `vcs.repository.name` is authoritative. Its
  URL/path-separator/token-like shapes are also dropped before truncation, and
  an unsafe value does not activate a fallback. Only an absent name permits the
  exact GitHub HTTPS allowlist defined in `telemetry-ingestion.md`; only its
  sanitized repository segment is stored. The raw URL and owner are never
  stored in the monitor projection.

Issue #58 repository metadata diagnostics do not change the monitor projection
schema and do not backfill rows. The `/diagnostics` request selects only bounded
raw-record IDs and payload byte counts, then reads each payload through the
Retention catalog `access` gate. The parsed body is request-local and discarded
after producing key/count/scope/classification rows, one fixed metadata status,
and label/fallback booleans. Attribute values, raw URLs, owners, identities,
credentials, PII, and local paths are neither persisted nor emitted by this
diagnostic path.

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
- existing Local Ingestion Monitor-processed `raw_records` are re-projected for spans and the
  new `monitor_traces` rollup columns. Span-projection progress is tracked
  independently of `monitor_ingestions` via `monitor_ingestions.span_projected_at`, so a record that was already projected
  for ingestion/trace but not yet for spans is detected and not hidden as
  backlog 0.
- mandatory upgrade test from a Local Ingestion Monitor-populated DB verifies backfill
  correctness.
- Canvas Repository Metadata raises the monitor projection schema to version 3 with nullable
  `monitor_traces` repository metadata columns. Existing projected rows are not
  automatically backfilled for these columns; they remain `NULL` until new
  telemetry is ingested or the database is regenerated by an explicit separate
  operation.
- Local Monitor UI Redesign raises the monitor projection schema to version 4 with the nullable
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
- the former prompt-bearing dashboard and trace-list do not remain raw-store
  readers. `/` is Repository selection; exact one-segment `/traces` and
  `/traces/` are empty no-store `404` for every method, case variant and query.
  `/traces/{traceId}` and its exact technical descendants retain their owners,
  and `/api/monitor/trace-list` remains a frozen sanitized machine endpoint.
- the installed metadata-only dashboard / trace-list fallback is pre-v1
  history. Local Monitor v1 `--sanitized-only` composes a receiver-only host:
  no Razor Pages, human static assets, human routes, or
  `/api/local-monitor/v1/*` are registered. Accepted frozen machine APIs retain
  their contracts, PII is excluded, and no cacheable raw response is generated.
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
The sole filter exception preserves an independently validated, exact root
`totalTokens` JSON number only for `subagent.completed`, only when its token is
canonical unsigned decimal and its value is in `0..2147483647` inclusive. Its
ceiling is a local admission/security limit, not producer semantics. Its
authority ends inside the filtered `session_event_content.content_json`: it
does not populate `session_runs.total_tokens`, any public DTO or aggregate, and
it does not authorize migration, backfill, replay repair, or a runtime-backup
variant. Missing or rejected candidates stay absent; unrelated safe content
continues to be stored, and every other secret-key and credential-value rule is
unchanged.
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
Automatic physical deletion is Issue #89 scope; user-controlled pin, unpin, and
delete-now are Issue #90 scope. The full write/read shape is defined in
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

## Historical Import Persistence

Issue #79 adds one independent additive SQLite component in the existing Local
Monitor database:

```text
schema_version(component='historical_import', version=1)
```

It does not bump or extend the monitor, Session, Doctor, alert, analysis, or
retention component versions and adds no responsibility to
`RawTelemetryStore.cs`. The v1 component owns exactly these tables:

- `historical_import_previews`
- `historical_import_confirmation_bindings`
- `historical_import_operations`
- `historical_import_observations`
- `historical_import_observation_fields`
- `historical_import_observation_provenance`
- `historical_import_conflicts`

The migration creates all seven empty tables and the component-version row in
one transaction. It is idempotent only for a complete, shape-valid v1 schema.
A stamped partial v1 or a newer component version is rejected without repair,
renumbering, downgrade, or table mutation. There is no fabricated v0 fixture:
fresh-database and additive installation into a real existing pre-#79 database
are the supported migration cases. Local Monitor initializes the component
before mapping historical-import routes; failure fails host construction rather
than changing D051 readiness fields or thresholds.

`historical_import_previews` stores the opaque selection/preview IDs, workflow
version/digest, producer and database decision fingerprints, bounded sanitized
preview projection, expiry/state, and one private exact locator needed for a
commit re-probe. For an actionable preview only, the row also retains the
trusted probe without a nested candidate copy and one separate metadata-only
candidate batch. The locator is ephemeral local-sensitive database state, not
a public projection: its authority ends exactly at five minutes and it is
deleted when a preview is non-actionable, expires while the service is active,
or reaches any terminal attempt after exact confirmation. Invalid caller
bindings create no operation and do not consume an otherwise live preview.
Startup sweeps expired private locator/probe/candidate columns before any
workflow access and reschedules each still-live actionable preview at its
original absolute expiry, so a database that was dormant across expiry cannot
reuse private state. This bounded private state lets CLI
preview/confirm/commit run in separate processes and survive a Local Monitor
restart; it is never copied to operation/history/observation/conflict rows,
logs, or evidence. A current zero-candidate preview discards the locator
immediately. Confirmation bindings store the exact preview/snapshot binding
and only the digest of any private confirmation material. Operation rows own
the hashed idempotency key, exact request fingerprint, monotonic
queued/running/terminal state and version, optional final result/history
projection, and fixed failure code; public reads never return the idempotency
key or its hash. Startup recovery terminalizes only queued/running rows whose
bound preview has expired and cannot replace an unexpired live owner or an
already committed terminal row.

An observation is identified by its local opaque observation ID and has one
unique exact admitted-source identity over profile, adapter, application
version, format name/version, fixture hash, schema fingerprint, normalization
version, and internal source-record key. The internal candidate/source-record
keys are never public columns in a workflow projection. Observation-field and
provenance rows are keyed by `(observation_id, field)` and preserve the exact
one-to-one policy order. An optional exact existing-Session binding is a
relationship owned by this component only; no Session table is inserted or
updated. The relationship is immutable after the exact admitted-source
observation is first inserted; duplicate/conflicting repeats cannot add or
replace it. Conflict rows contain only the observation, fixed conflicting field
names, and existing/incoming canonical fingerprints. They never store or
return conflicting values, source locators, or raw content.

Commit first persists `queued` and `running`, then uses one `BEGIN IMMEDIATE`
transaction for confirmation consumption, idempotency/application result,
successful operation transition, every new observation,
field/provenance row, exact-binding relationship, duplicate decision, and
sanitized conflict receipt. An injected or real failure at any stage rolls the
entire commit back. Exact duplicates are no-op decisions inside the same
transaction. Same-record conflicts preserve the existing observation and add
only their sanitized receipt. There is no partial observation/provenance write,
last-write-wins update, background repair, retry worker, or import-owned
deletion queue.

Deterministic source/profile/candidate/fixture/expiry/consumption revalidation
before that domain transaction becomes `rejected` / `not_started`. A
post-operation pre-domain store failure uses the same no-transaction outcome
with a fixed store code. Only a domain-transaction attempt that cannot commit
becomes `failed` / `rolled_back` with
`historical_import_transaction_failed`; the separate terminal status write
contains no domain result or partial counts.

Workflow v1 is metadata-only: observations have
`content_state=not_captured`, so the commit does not open or write
`session_event_content`, `raw_records`, or `retention_items`. The seven-table
component is not a retention store kind. Future content remains blocked until
an admitted contract can write one existing `session_event_content` item and
its #89 catalog row in the same transaction using the original authoritative
source time; it must not extend this component with raw content or add a new
retention store kind.

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

### Issue #90 lifecycle interaction

Issue #90 uses the existing catalog policy seam and adds no lifecycle edge,
parallel state machine, worker, queue entity, or physical-delete path. `pin`
changes `expiring` to `retained_by_policy`; `unpin` changes the derived pin
state back through the same seam. `pin_state` is derived from lifecycle state:
`retained_by_policy` is `pinned`, and every other represented catalog item is
`unpinned`; 90-B stores no second pin column.

The mutation application service separates `PREVIEW-STAGE` rejections from
`COMMIT-STAGE` outcomes. Preview is deterministic and read-only. A confirmed
mutation evaluates the exact target set again and commits all allowed changes
or none. For an expired-at-unpin result, the service recalculates expiry from
the original `captured_at`, recorded `policy_id`, and recorded
`policy_version`; it never starts a new TTL. It irreversibly denies reads and
executes the allowed #89 transitions sequentially inside one `BEGIN IMMEDIATE`
transaction: `retained_by_policy -> expiring` when the item is pinned, then
`expiring -> expired_pending_deletion`, then
`expired_pending_deletion -> deletion_queued`. An expiring item uses the final
two transitions. Delete-now uses the same guarded sequence, including the pin
seam when needed. Each executed transition increments the item revision exactly
once, and the result version is calculated from the final revisions. An item
already in `deletion_queued` is an actionable idempotent result with no second
transition.

The 90-B persistence deliverables are restart-safe preview records,
confirmation bindings with only the SHA-256 hash of the full token, per-step
workflow idempotency rows, operation/result receipts, and append-only audit
events. State, read denial, sequential #89 transitions, confirmation
consumption, idempotency result, operation correlation, and audit are one
all-or-none durable transaction. The existing #89 worker alone performs later
physical deletion; worker or adapter failure is observed through the existing
item state and retry metadata and cannot restore readability.

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

New-read admission requires a matching readable catalog revision and exact
source item. Post-commit consumption instead requires the immutable admitted
capability plus its exact active read lease and never rereads the current
catalog revision or lifecycle. Expiry first commits irreversible
`read_denied_at`, then queues cleanup. A failed retry, restart, clock change,
repair, or source absence never restores new-read eligibility. Queueing is
idempotent by `item_id`; scan/claim
order is `expires_at ASC, item_id ASC`, with finite v1 limits (100 items, 30 s
scan, 2 workers, 5 attempts). Deletion requires an exact source identity,
adapter-owned ownership receipt, expected revision, and deletion lease. No
repository, workspace, path, trace, timestamp proximity, or prompt similarity
may identify a deletion target. SQLite source deletion and the `deleted`
tombstone/receipt are atomic. File deletion is journaled, forward-only, and
only mutates exact owned members after identity/marker/digest validation.

Before every cleanup batch mutates leases or item state, its `BEGIN IMMEDIATE`
transaction verifies the exact five v1 adapter-coverage rows. A mismatch writes
only the singleton `worker_error_code=retention_adapter_coverage_mismatch`,
returns no work, and prevents channel/adapter dispatch; item, lease, journal,
and tombstone state remains unchanged. The error remains diagnostic state after
coverage is restored; a later eligible cycle may proceed. Per-claim coverage
validation remains required as a stale external-caller fence.

The shared read foundation is initialized explicitly for a newly owned database
or adopted explicitly from an existing v1 catalog; ordinary reads never create
or migrate a database. Adoption validates the catalog component version and
database identity and reports only `retention_catalog_unavailable` on a missing
or invalid catalog. Admission holds one `BEGIN IMMEDIATE` SQLite transaction
while it validates catalog state and receipt, enumerates metadata-only
candidates, prepares the hidden handle/notification/cleanup resources, and
creates the bounded access/operation lease. Except for the fixed generic Session
adapter below, it never selects a raw content column or publishes a value.
Retention owns the authoritative clock: single and fixed-batch admission take
their initial sample immediately after that
transaction begins; selected-batch admission takes its initial sample after
metadata candidate selection while the transaction remains held. Every ordinary
mode samples again immediately before the all-or-none lease insert and commit so
an expiry boundary crossed during admission is denied. The generic Session
adapter instead performs its inaccessible content selection before the final
clock/item/source/type recheck described below. Caller timestamps are
scheduling evidence only and cannot advance or backdate admission, denial, or
the full two-minute lease expiry. A committed lease remains hidden until its
store-backed handle-publication fence wins; except for the generic Session
adapter's inaccessible precommit buffer, raw materialization then occurs only in
the separate fixed consumption operation described below. An empty,
metadata/shape, expiry-boundary, stale-revision, preparation, publication, or
failed-commit result returns no raw value. SQLite busy/locked returns `Busy`
without an unauthorized publication.

All production reads of `raw_records` that materialize `payload_json` or
`resource_attributes_json` use this boundary. Multi-record raw reads acquire
one composite lease inside that same transaction: a denied, stale, missing, or
busy member returns no partial handle/value and no synthetic marker. Callers
keep the published composite handle and its use references through the actual
raw use and one terminal arm; disposal alone is not publication authority.

Issue #154 Skill reprojection is one such composite operation. Its generation
persists the exact ordered raw identity/digest frontier before a worker runs.
The worker acquires an operation lease for every frontier member and holds it
through response-free projection publication. Publication rechecks the queue
lease and every Retention lease in the same transaction as current-pointer
assignment. It never searches for a different/current raw set, shortens a
frontier after expiry, or publishes partial rows. Authoritative expiry,
deletion or read denial produces `input_unavailable` and no current Skill
claim. The complete participant and retry contract is
[Skill Projection](skill-projection.md).

The separately owned `skill_invocation_snapshot:1` component is an independent
index/metadata and equality-receipt owner over exact Session Event content; it
is not a raw store kind and does not copy historical body/path bytes. The sole
v2 writer must commit Session Event identity/content, Retention ownership,
snapshot metadata, #154 SDK claim or invalid-claim state, and equality receipt
as the one seven-authority transaction defined by
[Skill Invocation Snapshot](../interfaces/skill-invocation-snapshot.md).
No partial Event, content, Retention item, snapshot, claim state or receipt may
survive failure. An OTel-only `not_captured` observation has no snapshot row,
and a retained snapshot cannot create or resurrect a stale/invalid #154 claim.
Its active decision, implementation, registration, and release status is owned
solely by [Skill Invocation Snapshot](../interfaces/skill-invocation-snapshot.md);
this Retention owner neither reopens nor closes those gates. No compatibility,
fallback, scan-on-read or second raw-content carrier is authorized.

An access or operation lease whose shared-read transaction commits before the
item expiry boundary keeps its bounded lease duration across that boundary.
Expiry still denies every new read and queues cleanup at the exact policy
timestamp, but deletion claim remains quiescent until the admitted lease is
released or expires. An admission that reaches item expiry at its final sample
inserts no lease; ordinary admission has selected no raw. The generic Session
adapter may hold only its inaccessible internal buffer at that point; a failed
final clock/item/source/type recheck rolls back its uncommitted lease and
discards that buffer.

### Read admission, grant consumption, renewal, and release

The Retention catalog is the single owner of three distinct read predicates:
`row_readable` for new admission, `grant_usable` for post-commit consumption
of an admitted lease, and renewal eligibility for extending an operation
grant. No consumer defines a parallel predicate.

`row_readable(at)` requires exact current source/ownership/receipt/revision
proof, `read_denied_at IS NULL`, and either `retained_by_policy` or
`expiring AND at < expires_at`. `retained_by_policy` disables automatic
expiry: its original `expires_at` is immutable historical metadata and is
never nulled, reset, extended, or used by itself to deny a read or renewal.
Only `expiring` rows expire by clock. The state-only classifier derives its
disposition from lifecycle state and `read_denied_at` alone: readable,
already denied, lifecycle denied, or expired expiring; historical expiry never
classifies a pinned row as expired.

New single-read admission evaluates in this exact first-result order:

1. item absent;
2. expected revision mismatch;
3. already denied;
4. nonreadable lifecycle;
5. expired expiring row;
6. source proof busy;
7. source missing;
8. source invalid/mismatched;
9. lease conflict;
10. capability/selector result;
11. admit.

Earlier results are never preempted by later proof evaluation. Every denial
mutation is an exact revision/state compare-and-swap: the expiry denial
matches item ID, admitted revision, `state='expiring'`, `expires_at <= now`,
and null denial; the missing/invalid-source denial matches item ID, admitted
revision, readable lifecycle, and null denial. A zero-row denial update
returns stale denial without retry or broader mutation. Stale callers,
selector null, lease conflict, and capability mismatch deny without lifecycle
mutation.

Admission publishes one read grant carrying the admitted ownership key, item,
admission revision, lease kind/owner/generation, a privately copied 32-byte
source token, and the immutable expiry recorded at admission. The grant also
owns one synchronized published lease expiry; this is its only mutable value
and is protected by its publication lock. The admitted expiry is diagnostic
evidence only after renewal. `grant_usable(at)` is the immutable admitted catalog/store/item/source
capability plus the exact live lease item/kind/owner/generation with persisted
expiry equal to the published expiry and `at` before that expiry. It never
rereads current item lifecycle, item expiry, read denial, or revision:
cleanup, pin, unpin, or delete advancing the item after admission does not
revoke the committed grant. Its exact consumption proof is:

```sql
SELECT EXISTS(
    SELECT 1
    FROM retention_leases AS lease
    WHERE lease.item_id=$retention_read_item_id
      AND lease.lease_kind=$retention_read_lease_kind
      AND lease.owner=$retention_read_lease_owner
      AND lease.generation=$retention_read_lease_generation
      AND lease.expires_at=$retention_read_lease_expires_at
      AND lease.expires_at>$at
)
AND <exact admitted store/source identity and current source owner-token match>;
```

The final arm is a closed switch over the five v1 store kinds and contains no
`retention_items.state`, `revision`, `read_denied_at`, or item `expires_at`
predicate. Admission selectors bind the admitted revision inside the same
immediate transaction; post-commit consumers call only `grant_usable`.

Renewal applies only to operation leases that are due at the fixed one-minute
deadline and still `grant_usable`; access leases never renew. A live operation
grant outside that deadline returns `not_due` unchanged without rereading
current item revision, readability, source/ownership receipt, or adapter
coverage. A due renewal additionally requires current item revision equal to
the immutable admission revision, current `row_readable`, exact current source/
ownership receipt, and the complete exact five-row adapter-coverage set, then
commits exactly `renewal_at + 2 minutes` with no item-expiry cap.
`renewal_at` is the one trusted catalog/queue-owner clock sample taken after the
caller's `BEGIN IMMEDIATE` succeeds; a caller-supplied timestamp cannot override
it or resurrect authority that expired while waiting for the transaction.
An expiring renewal may commit only when its state remains `expiring`, denial is
null, and that authoritative commit sample is strictly before item `expires_at`;
at or after item expiry it returns `not_renewed` without changing the old grant.
A pinned renewal ignores historical item expiry while its exact current
admission proofs hold.
Acquisition and successful renewal always receive the full two-minute
duration, even when it crosses an expiring item's policy expiry. A failed due
proof or renewal never shortens the existing grant: the admitted lease
remains consumable to its published expiry. Pin/unpin/delete/cleanup revision
changes make an admitted grant nonrenewable but do not revoke it. Release
matches only item/kind/owner/generation and never current item revision,
state, or expiry.

Renewal prebuilds and synchronously arms one dormant, monotonically numbered
next expiry-notification generation while holding the transaction and grant
publication scope, then compare-and-swap updates the exact persisted lease
expiry. Construction, arming, checked-add, update, or commit failure publishes
no new expiry/generation, rolls back with exact `not_renewed` and no exception,
disposes the dormant resource, and leaves the old row, published expiry, and
armed notification unchanged. A dormant callback may
record `due` but cannot touch the handle before activation. After commit, still
under the publication scope, one infallible compare-and-swap publishes the new
expiry/generation, activates the replacement, and invalidates/disposes the old
notification. If the replacement was already due, activation loses the whole
handle and exact-releases the renewed lease or transfers it to mandatory cleanup
instead of publishing an unguarded generation. A callback may mark a handle lost
only when its generation is still current and its time is at or after the
published expiry; every old or racing callback is a stale no-op. Composite
renewal prebuilds/arms, commits, publishes, activates, and swaps every member in
semantic frontier order all-or-none; an already-due member loses and releases
the complete composite.

The #158 current-file consumer never renews its operation lease. Its one
notification remains bound to the original admitted two-minute expiry and is
never rescheduled. A terminal result won strictly before expiry disposes that
notification; a racing or later callback is a stale no-op and cannot tag loss,
cancel work, retract the terminal result, or prevent the already-authorized
runtime seal/send. A notification or terminal sample at or after expiry wins
`lost` and cancels only that Retention-tagged current-file work.

SQLite transaction order is always `BEGIN IMMEDIATE` first. Before acquiring
any grant publication lock, Retention constructs a lock-only permutation in
ascending order by the exact immutable admitted lease tuple:

```text
(
  store_instance_id  -- ordinal, case-sensitive,
  item_id            -- ordinal, case-sensitive,
  lease_kind_rank    -- access=0, operation=1, deletion=2,
  owner              -- ordinal, case-sensitive,
  generation         -- signed integer numeric order
)
```

The tuple is simultaneously the sole global publication-lock sort key and the
publication-lock identity for every composite Retention scope. One admitted
lease tuple must resolve to one in-memory publication state and lock authority
for the lifetime of that handle. A wrapper, batch, renewal, terminal path,
cleanup path, or owner adapter must not manufacture another independently
lockable `RetentionReadGrant` for the same exact tuple. The following rules are
invariant:

1. An owner-persisted semantic frontier ordinal does not override the
   publication-lock order.
2. Owner/caller/selector order remains the semantic frontier for selection,
   returned values, output serialization, and digests, as well as all
   owner-visible processing.
3. Retention stores the permutation between semantic frontier order and
   publication-lock order and never reorders the owner-visible result.
4. Before the first publication lock, Retention rejects a duplicate exact lease
   tuple. A duplicate object reference to the same grant and a distinct object
   carrying the same exact tuple are both duplicate members and are both
   rejected at that point unconditionally; proving alias uniqueness does not
   admit either. It also rejects a generation that is not a positive canonical
   persisted integer. Signed integer numeric order is the sort comparator; it
   does not make a zero, negative, or noncanonical persisted generation valid.
   A contradictory duplicate grant or any case in which alias uniqueness
   cannot be proven fails at the same point.
   The tuple is a total order over the members of a composite scope: no two
   distinct publication locks in one scope may compare equal.
5. Retention computes the complete publication-lock permutation before the
   first publication lock. While publication locks are held, it never
   discovers, adds, or reorders a member or its publication lock, and it
   performs no `await`, HTTP, or file I/O. The exact live-lease,
   persisted-expiry, and admitted-capability proofs that bind an already-locked
   member's published state remain inside their publication scope.
6. Retention acquires every publication lock in ascending publication-lock
   order and releases them in the exact reverse acquisition order. No path
   acquires these locks in the inverse order.
7. A single-member scope follows the same rule trivially.
8. #154 and any future owner may retain a persisted semantic frontier ordinal
   as graph or replay authority, but never as a second lock-order authority.

A composite renewal updates the caller queue fence and every due Retention
lease in one transaction, commits all or none, and then publishes every
in-memory expiry while all publication locks are still held.

Golden cross-expiry timeline: admit an `expiring` item at `T1 - 1 tick` and
publish lease expiry `admission + 2 minutes`; cleanup at `T1` commits
`expired_pending_deletion` then `deletion_queued` with equal denial/queue
timestamps and total revision +2; consumption at lease expiry − 1 tick still
succeeds despite the current state/revision/denial while the deletion claim
stays quiescent; consumption at the exact lease expiry fails; the claim then
becomes eligible and `deleting` advances the original item revision total to
+3.

### Raw-read publication and terminal authority

Every nonempty access/operation raw read separates admission, consumption, and
caller-visible publication. Candidate enumeration is metadata-only. A valid
zero-member request returns the owner's exact empty value with disposition
`Empty`; it creates no lease, hidden handle, expiry notification, source token,
use reference, or terminal operation. `Empty` is not a granted or lifecycle-
denied read.

After metadata candidate selection and exact source/owner/receipt/revision
proofs, every ordinary admission takes one final clock sample for the complete
frontier and derives the common checked `sample + 2 minutes` lease expiry before
insertion. The generic Session adapter takes the same final sample and proofs
after its inaccessible content selection but before commit. If any expiring
member crossed its item expiry, no member lease commits: only those crossed
expiring members take their exact `DenyAndQueue` transition at that sample,
pinned or unexpired siblings remain unchanged, and the Session exception also
discards its buffer. Checked-add overflow rolls the whole transaction back as
`SelectorUnavailable` with no denial mutation. Before any lease may commit,
Retention constructs every hidden handle, dormant expiry notification, and
mandatory cleanup-ownership record. Preparation failure is
`SelectorUnavailable` and rolls back without a lease or raw value. Otherwise
the complete frontier commits all-or-none at the common derived expiry.

After the all-or-none lease insert commits, the handle remains hidden; except
for the generic Session adapter's inaccessible internal buffer, no raw selector
has run and no selected value exists. Retention activates/arms every prebuilt
expiry notification. Any activation/arm failure marks every hidden member lost,
disposes every prepared notification, and synchronously exact-releases the
complete committed frontier in one transaction; release contention transfers
the exact tuples to mandatory hidden-lease cleanup. It exposes no exception,
handle, value, or partial authority.

After successful activation, Retention runs one store-backed publication fence:
`BEGIN IMMEDIATE`, then the Monitor publication scope, then—while both are
held—a fresh Retention-owned clock sample, proof of the exact live lease item/
kind/owner/generation and persisted expiry, and proof of the immutable admitted
store/source/owner-token capability. The notification and fence atomically
compete on the hidden handle state. Only a strictly pre-expiry equal proof
publishes the handle. Missing/mismatched facts, equal/after expiry,
cancellation, notification loss, or SQLite begin/query/commit contention or
failure publish no handle or value, return `Busy` for the store failure arm, and
synchronously exact-release the committed lease or transfer it to the mandatory
hidden-lease cleanup record. A batch publishes, loses, and cleans every member
together.

After any such transfer, no caller owns or may use the hidden handle, lease, or
notification. The prebuilt mandatory cleanup owner disposes remaining
notification resources and retries every exact lease tuple until deletion or a
cleanup-produced stale no-op; it never returns authority to the caller.

Raw selection is a separate consumption transaction under that published
handle. It proves `grant_usable`, binds the selected source row to the immutable
admitted capability before reading raw columns, buffers the complete owner-
bounded value, and proves `grant_usable` again before publishing the value to a
fixed mapper. The mapper must hold a committed-handle use reference across
every buffer access. Expiry, cancellation, or release closes new references and
drains existing references before the buffer is zeroed. A post-grant query,
content, mapper, or type contradiction is `ConsumptionUnavailable`, discards
the value, and retains the same handle for terminal completion.

The generic Session raw-content path is the sole stronger pre-publication
adapter. Inside one Session-owned `BEGIN IMMEDIATE`, it proves the exact Event
identity/type before lease or content access; missing or `skill.invoked` exits
before either operation. Only a non-Skill Event may insert its exact access
lease and select content in that same transaction. No value escapes until the
Session owner takes its final Retention clock sample, rechecks the exact item,
source, and Event type, commits the transaction, and both the hidden-handle and
value-publication fences win. A concurrent type change cannot commit between
policy admission and selection. This is not a general selector/delegate or
Skill-content authority.

The complete internal read taxonomy is closed:

- `Empty`: valid zero-member result; no handle or terminal call;
- `LifecycleDenied`: only admission lifecycle/source/ownership denial;
- `SelectorUnavailable`: pre-grant metadata, shape, checked-time, or hidden-
  resource preparation failure;
- `ConsumptionUnavailable`: post-grant query/content/mapper contradiction;
- `LeaseLost`: committed authority lost at handle/value publication; and
- `Busy`: SQLite contention.

Historical, generic Session, raw trace/detail/page, and analysis-run HTTP reads
use access handles. #158 current-file and every existing operation-scoped raw
HTTP read use operation handles; current-file keeps the fixed nonrenewing arm
above. Every caller-visible raw HTTP owner fully buffers its exact entity without
starting the response, then calls exactly one terminal operation on the same
handle:

- `TrySealRawResponse()` for a raw-derived entity; or
- `TryCompleteWithoutRaw()` after discarding all raw buffers and closing every
  use reference for an already-determined safe/nonraw entity.

The only successful internal results are `sealed` and
`completed_without_raw`. Missing/mismatched lease facts, cancellation, or a
Retention-owned sample at or after the published expiry returns `lost`;
SQLite begin/query/commit contention or failure returns `busy`. `lost|busy`
authorizes no substitute status, header, or entity: the HTTP owner discards the
already-buffered entity, closes every use reference, and aborts the transport
with zero response. Ordinary disposal/release is never publication authority.

Response-free Skill Projection, Local Repository, analysis-result publication,
migration, and diagnostic consumers retain their existing atomic publication or
commit fence and prove the exact live grant at that fence. They never substitute
disposal lifetime for publication authority; a lost grant publishes nothing and
the nonpublishing consumer completes/releases the same handle fail closed.

Both terminal methods first perform one lock-free per-handle
`open -> terminal_attempt_in_progress` compare-and-swap while holding neither
SQLite nor a Monitor publication scope. Only that winner may synchronously
enter `BEGIN IMMEDIATE`, then acquire the existing Monitor publication scope,
prove the exact live lease and persisted expiry equal to the published expiry,
recheck the claimed terminal state, and sample the Retention clock exactly
once. No caller time or validate-then-seal split is accepted. A renewal
committed before the claim is observed remains visible to the terminal proof;
renewal after observing the claimed state is denied without publication.
Cancellation, release, or expiry may win `lost`. No path acquires the Monitor
scope without the transaction first, and neither scope crosses `await` or HTTP
I/O.

`TrySealRawResponse()` sets an irreversible sealed-pending terminal/refcount
state only while the complete `grant_usable` proof remains true strictly before
expiry, commits the transaction, and then publishes `sealed`. It drops the
database and publication scopes before permitting one already-buffered send or
discard. It extends no lease and permits no later raw read, renewal, or reuse.
Final release runs exactly once after send/discard and deletes the exact lease
or accepts cleanup's stale no-op.

`TryCompleteWithoutRaw()` applies the same proof and clock sample, marks the
handle completed-pending, deletes the exact lease in that transaction, commits,
publishes `completed_without_raw`, and only then permits the fixed safe result.
A transaction that cannot begin or complete rolls back its database changes,
makes any pending or claimed terminal attempt irreversibly `failed`, and returns
`busy`; it cannot be retried, reopened, renewed, or reused. Its idempotent final
release still runs exactly once.

`RetentionBatchReadLease<T>` owns one composite terminal state over the
complete semantic member frontier. A terminal call wins the composite CAS,
opens one transaction, acquires every member publication scope in ascending
publication-lock order, proves every exact live lease against one clock sample,
and moves the composite plus all members to sealed-pending or completed-pending
atomically. The commit then publishes every final result all-or-none.
Completion deletes all member leases in that transaction; sealing retains them
only for exact final release. Any member loss or mismatch loses the whole
entity; any transaction failure rolls back the partial rows, leaves the claimed
composite irreversibly failed, and is `busy`. Loss or failure discards the
complete buffered entity and final release deletes every exact member lease, or
accepts cleanup's stale no-op, exactly once in semantic frontier order. Every
publication-lock acquisition uses ascending publication-lock order and releases
the locks in exact reverse acquisition order. Renewal, cleanup, and expiry race
the same composite CAS and publication-lock-ordered scopes. Partial or first-
member-only terminal authority does not exist. Returned values, serialization,
digests, and owner-visible processing remain in semantic frontier order.

Raw-local replay exposes two additional one-shot terminal operations over this
same state machine:

- `TrySealRawReplayTransientPublication()` permits exactly one memory-only
  transient-store commit after a successful capacity reservation; and
- `TrySealRawReplayFilePublication()` returns a single-use ticket for exactly
  one same-directory non-overwrite move of an already validated staged file.

Preview and retained-result safe outputs use `TryCompleteWithoutRaw`. Every
post-grant branch that cannot reach its named seal first discards raw/staging
state and completes without raw. A transient reservation is prepared before
the seal and committed infallibly after it; no transient-store lock crosses the
Retention terminal scope. A CLI file is completely written, flushed, and
inspected before the seal; only the sealed ticket may perform the final move.
All losing arms cancel/delete their private staging and publish no raw/staged
path or byte and no reusable authority. Prescribed fixed safe CLI failure bytes
remain gated by successful completion or the exact terminal-loss mapping.

### Snapshot equality-replay validation exception

The validation-only `SkillInvocationSnapshotReplayValidator` is the sole
exception to ordinary lease-backed raw consumption. It is reachable only after
an exact #158 equality-receipt fingerprint hit and returns only `valid`,
`fingerprint_mismatch`, `invalid`, or `busy` to the replay coordinator.

`ValidateAsync` opens one validation-only `BEGIN IMMEDIATE`, rechecks the
receipt, samples one nonpersisted Retention-owned `validation_at` only for an
equal fingerprint, and evaluates `row_readable(validation_at)` inside that same
snapshot. Its reserved lock serializes cleanup, and the validation-only
transaction ends without committing a mutation. A readable exact owner graph
may select and fully reclassify/digest-check the canonical Session content
document without inserting a lease. An owner-valid expired/read-denied/cleanup
row that still retains raw selects no raw and is `invalid`. An exact deleted
item selects no raw and may be `valid` only after proving the absent content row
and exact tombstone graph. Source, owner, receipt, or graph contradiction is
`invalid`; SQLite contention is `busy`. The validator performs zero writes and
exposes no raw value.

Canonical Session content selection and digest checking remain in semantic
frontier order. This validation-only exception acquires no grant publication
lock and therefore constructs no publication-lock permutation.

`ValidateInTransactionAsync` is the sole race-entry variant. It receives #158's
already-open exact connection and `BEGIN IMMEDIATE` transaction, rechecks the
receipt/fingerprint, and invokes the same validation core. It opens no
connection or nested transaction and never commits or rolls back; #158 owns the
enclosing zero-write rollback for every 204/409/503 mapping, and that existing
reserved lock supplies the same cleanup serialization. A different
fingerprint returns `fingerprint_mismatch` without a clock sample. Raw values
remain stack-confined, are never returned/cached/logged/measured, and are
released before the transaction ends. Receipt miss/difference and normal reads
cannot call either entry point, and this exception cannot serve first writes,
routes, projection, analysis, migration, backup export, or another component.

Sanitized projections, Session/Event metadata, safe summaries, receipts, and
tombstones are retained outputs, not raw store kinds. Caller-owned input files,
unimplemented receiver files, and external blobs are not cleanup targets. The
reusable migration corpus is `retention-closeout-corpus-v1`; it verifies the
supported retained store kinds and cannot treat future stores as covered.

### Analysis SDK directory capture and cleanup (D3)

`CopilotAnalysis:BaseDirectory` is a configured parent, not an SDK-owned
directory and not a cleanup target. For each analysis run, the catalog must,
before any filesystem or SDK operation, reserve one opaque generated child
directly below that exact canonical parent. The child is the only directory
passed to the SDK: both the SDK base directory and runtime working directory
are that child. The SDK client uses empty mode. The prompt-free inventory-probe
Session allows only the existing exact source-qualified run-scoped custom raw-
analysis tool declarations. With explicitly retained
`--skill-discovery-directory` roots, the distinct execution Session preserves
those exact declarations and adds exactly `builtin:skill` and
`builtin:task_complete`. Skills then freeze non-retained names in the
distinct execution Session's `DisabledSkills`; the execution inventory rejects
enabled non-retained Skills, drift, disable failure, unverifiable paths, or
collisions before prompt send. Every retained inventory path passes native
retained-root opener/lease proof before prompt, and every later invocation path
is re-proved when invoked;
configuration discovery and custom instructions remain disabled and no plugin
or instruction directory is supplied. A retained Skill's `allowed-tools`
metadata cannot widen the exact allowlist. With no roots, Skills remain
disabled. Wildcard custom, every other built-in, MCP, plugin, ambient
instruction/config discovery, and environment capabilities are not enabled.
Production sends the ordinary requested prompt with `AgentMode.Autopilot` and
does not force a retained Skill invocation; deterministic T0b alone resolves
and invokes the exact admitted retained command through
`executionSession.Rpc.Commands.InvokeAsync` and sends its exact prompt-producing
result with `AgentMode.Autopilot`. The Session working directory and large-tool-output spill
directory are the same child, and every unexpected permission request is denied
without user interaction. Its
`analysis_sdk_directory` item uses the exact persisted owning
`monitor_analysis_runs.requested_at` value as `captured_at`; no start,
reservation, filesystem, recovery, or current time may replace it.

The reservation binds the catalog instance, analysis run, exact requested-at
text and UTC ticks, and private ownership token. Its private ownership marker
is kind-bound to `analysis_sdk_directory` and that same binding; it is not a
generic marker and cannot authorize another kind, run, parent, or child.
Activation atomically creates the item and an operation lease after the
marker-proven child is quiescent. Activation uses the catalog's single trusted
clock sample taken immediately after its `BEGIN IMMEDIATE` succeeds. Renewal
takes its authoritative sample after `BEGIN IMMEDIATE` succeeds and after the
publication scopes are acquired in ascending publication-lock order, before
proof and compare-and-swap.
An owner/caller timestamp cannot move the policy boundary or resurrect an
expired lease. The operation lease is held and renewed for the complete SDK use,
including Session and Client disposal, and is released only after that use ends.
A lost or unavailable lease prevents SDK use and later mutation.

Reserved-child preparation and abandonment are serialized by the catalog writer
transaction. After restart or failed activation, abandonment may delete the
exact reserved child only when it is an ordinary empty directory or an ordinary
marker-only directory whose marker bytes and digest exactly match that
reservation; it then deletes the exact reserved row. Unexpected entries,
invalid markers, reparse points, and non-directory material are preserved while
the unusable reservation is abandoned. Failure before exact child-shape and
marker proof completes returns conflict without changing either child or
reservation. Filesystem deletion and the catalog commit are not one atomic
boundary: an indeterminate failure after exact-child deletion begins returns
conflict, never stale success, and leaves only a forward-recoverable reservation
paired with its exact marker-only, empty, or absent child; a reservation paired
with markerless unexpected material that appeared after exact marker proof and
deletion; or a reservation that the commit already removed. In the post-proof
raced-material state, conflict retains the exact reserved row and preserves the
unexpected material. The next successful serialized open or abandonment must
preserve that material while retiring only that exact row, so a subsequent open
can create fresh authority. Every other forward-recoverable state must likewise
be safely recovered or finished by the next successful serialized operation.
Stale no-op is reserved for a capability proven stale before mutation. The
configured parent and every sibling are never mutated.

After quiescence and before deletion, the catalog snapshots only that exact
owned child. The immutable snapshot and the first delete intent are one
transaction. It contains at most 256 canonical child-relative members and at
most 128 MiB of file content, with every file's exact size and digest and a
persisted deletion order. Cleanup validates the marker and snapshot before
each mutation, deletes snapshot files and directories in that order, deletes
the marker last, then deletes the now-empty child last. It never enumerates,
deletes, adopts, or otherwise mutates the configured parent, any sibling, or
any non-snapshot member.

Ownership/setup and SDK failures expose only the fixed sanitized messages
`Local analysis ownership could not be established.` and `SDK analysis failed.`
respectively. These messages and retention diagnostics must not disclose
paths, raw data, ownership tokens, credentials, secrets, or exception text.
Production composition of the five-adapter retention worker is D4 scope and is
not implied or changed by this D3 directory-owner contract.

### Sensitive Bundle capture and cleanup

A sensitive bundle is created only after the explicitly bound catalog has been
validated and the candidate set contains extractable sensitive fragments. Its
capture lifecycle is `reserved` → `staging` → `published_pending_catalog` →
`complete`. The catalog records an immutable, bounded capture plan before any
bundle member is created; its authoritative capture time is the reservation
time. `complete` is the only phase that creates the readable retention item.
The final bundle is published only after every planned member has been written
and verified. Completion is idempotent for the same plan and proof.

Each plan has at most 256 members and at most 128 MiB of file content. It
contains only canonical relative member names, the planned member kind, exact
size and digest for every file, and a fixed deletion order. A private ownership
marker binds the catalog instance, capture identity, authoritative reservation
time, and private ownership token. Cleanup may delete only the exact final
bundle whose marker and planned members still verify; it deletes members in the
persisted order, with the ownership marker last, and deletes the now-empty
bundle directory last. An unexpected, replaced, missing-before-progress, or
reparse-bearing member, an unexpected extra member, or a stale lease is not
adopted or deleted and transitions to the corresponding bounded failure or
lease-lost result.

Capture recovery is restart-safe and forward-only. It resumes a complete,
owned staging or publish-pending capture; an empty reservation may be
abandoned. An incomplete, collided, replaced, or otherwise unprovable capture
is blocked without deleting the unproven material.

When the explicitly configured sensitive-output path is itself a bundle written
by the preceding v1 writer, migration examines that exact root only and never
searches its parent or descendants for other bundles. Adoption requires the
canonical v1 manifest, its exact self-delete target, the complete bounded
evidence index, the seven-day created/expiry relation, no extra members, and no
reparse point. Before filesystem mutation, the catalog reserves a generated
child and journals the migration. The old root is moved through its exact
sibling staging location into that child, the requested path is restored as the
new bundle parent, and the legacy manifest is replaced by the non-disclosing
current manifest before completion. Recovery deterministically resumes either
rename window. The resulting item retains the original `created_at_utc` and
`expires_at_utc`. A root with legacy bundle evidence that cannot satisfy these
proofs is preserved and durably classified as an ownership blocker; it is never
guessed, recursively scanned, or deleted.

The final sensitive-bundle catalog item uses `sensitive-bundle-7d` v1 and is
otherwise subject to the common irreversible read-denial, queue, lease, retry,
and deletion-failure rules above. Capture, recovery, deletion diagnostics, and
repository-safe evidence expose only bounded state/error information and
opaque references; they never expose raw fragments, member names, source or
delete locations, ownership tokens, or absolute/local paths.

## Validation

Use synthetic fixtures for automated tests.
Live Copilot execution is manual validation and must record environment, settings, trace id or equivalent identifier, confirmed items, and unconfirmed items.
