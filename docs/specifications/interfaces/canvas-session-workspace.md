# Canvas Session Workspace Interface

## Scope

This specification freezes the Issue #51 Session foundation for the installed
Local Monitor. It defines Session ingestion, identity and merge rules,
normalized storage, sanitized reads, raw-content reads, and the Canvas capture
boundary. It is additive to the existing OTLP receiver and monitor projection.

Issue #51 supersedes earlier Canvas statements that categorically prohibited a
new telemetry input, schema, API field, or session-to-trace correlation only for
the interfaces and tables defined here. Existing Canvas bounded-action,
loopback, token, raw-data, and `session.send()` constraints remain unchanged.

## Identity And Source Uniqueness

- Local Session, Run, and Event IDs are UUIDv7 strings.
- A native session ID is a source-provided identifier and is never treated as a
  local Session ID.
- Source event uniqueness uses, in priority appropriate to the adapter, the SDK
  event ID, the canonical hash of a Hook event, or the exact OTel trace/span
  identity.
- Repository, workspace, timestamp, tool name, transcript path, and temporal
  proximity are not identity evidence.

Sessions may merge only when at least one exact condition holds:

1. the native session ID is identical;
2. an explicit resume or handoff linkage connects the sessions; or
3. an ingested event and OTel evidence carry the byte-for-byte identical trace
   context.

Repository and timestamp proximity must never merge sessions.
`client_kind` never participates in Session binding or merge. An exact
`gen_ai.conversation.id` may bind/enrich only when it is byte-for-byte equal to
an already-recorded native session ID; this is the identical-native-ID rule,
not a separate heuristic. Otherwise OTel evidence remains `unbound`.

## Completeness

The normalized Session uses exactly one of these values:

| Value | Contract |
| --- | --- |
| `unbound` | OTel-only and not linked to a native session ID. |
| `partial` | A native ID exists, but the lifecycle or input family is incomplete. |
| `rich` | Instruction, lifecycle, and SDK/Hook or OTel evidence exist, but some content or terminal evidence is missing. |
| `full` | Surface-required start-to-end evidence exists, there is no unsupported version or ingest gap, and OTel enrichment is exact-linked. |

Missed events are not reconstructed. In particular, opening Canvas after a
session has already started lowers completeness when earlier evidence was not
captured.

### Completeness input facts and decision order

The `v1` source-capability contract supplies declarations, not inferred Session
facts. The normalizer evaluates native identity; exact trace context and trace
signal; lifecycle/input, content, and terminal evidence; source-version,
ingest, Hook-only, historical-summary, span-kind, schema-drift, and
source-enabled facts. It returns only the four values above and the fixed
reason-code set below. The reasons are de-duplicated and ordered exactly as
listed, never by arrival order:

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
and emitted in the canonical schema order above, never arrival order. Thus an
`unbound` base plus a `rich` reason remains `unbound`, and a `partial` base plus
a `rich` reason remains `partial`; `historical_summary_only` can never reach
`full`. `historical_summary_only` and `schema_drift_detected` are future
adapter-handoff `partial` reasons with no distinct current #51 calculator
boolean; they must not be conflated with `unsupported_source_version`.

This calculation has no heuristic merge and creates no synthetic span. Missing
required provenance maps only to fixed reasons: missing event/trace-span
identity to `missing_trace_context`, missing capture/content state to
`content_capture_disabled`, and missing adapter/version/fingerprint/
normalization version to `schema_drift_detected`.

The contract is an adapter handoff requirement. Task 15 adds only the v1 ingest
envelope provenance fields defined below; it does not change sanitized read
DTOs, the raw-content route, Session/Run/Event identity, or Issue #49 Agent
ownership.

## Session Event Ingest

The installed Local Monitor exposes:

```text
POST /api/session-ingest/v1/events
```

The request requirements are:

- loopback Local Monitor boundary and Host-header validation;
- `Content-Type: application/json`;
- custom version header `X-CAO-Session-Event-Version: 1`;
- request body at most **1 MiB (1048576 bytes)**;
- `events` batch length from **1 through 100**, inclusive;
- schema version `1` only;
- `source_adapter` is `copilot-sdk-stream`, `copilot-compatible-hook`, or
  `claude-code-hook`;
- `source_surface` is `copilot-sdk`, `copilot-cli`, `vscode`,
  `hook-unknown`, or `claude-code`.

The v1 envelope has these fields:

| Field | Required | Contract |
| --- | --- | --- |
| `schema_version` | yes | Integer exactly `1`. |
| `source_adapter` | yes | `copilot-sdk-stream`, `copilot-compatible-hook`, or `claude-code-hook`. |
| `source_surface` | yes | `copilot-sdk`, `copilot-cli`, `vscode`, `hook-unknown`, or `claude-code`. |
| `native_session_id` | yes | Nonblank string, 1..256 characters. |
| `events` | yes | JSON array with 1..100 entries. |
| `explicit_link` | no | The sole v1 wire representation of explicit resume/handoff linkage; shape below. |
| `source_application_version` | conditional | JSON null or an adapter-generated metadata token. Required for `claude-code-hook` when `schema_fingerprint` is absent; legacy Copilot envelopes may omit it. |
| `adapter_version` | conditional | Adapter-generated metadata token. Required for `claude-code-hook`; legacy Copilot envelopes may omit it. |
| `schema_fingerprint` | conditional | JSON null or exactly 64 lowercase hexadecimal characters. Required for `claude-code-hook` when `source_application_version` is absent; legacy Copilot envelopes may omit it. |
| `normalization_version` | conditional | Adapter-generated metadata token. Required for `claude-code-hook`; legacy Copilot envelopes may omit it. |

An adapter-generated metadata token matches
`^[A-Za-z0-9][A-Za-z0-9._+-]{0,255}$`. The receiver never derives these fields
from `payload`, content, a path, prompt/response text, tool input/output, or an
exception. Control characters, whitespace, path separators, URI separators,
and other free-form text are invalid. The four envelope values are copied
unchanged to every accepted event in the batch. They do not participate in
Session/Event IDs, binding, ownership, or content storage.

For `claude-code-hook`, `adapter_version` and `normalization_version` are
required and at least one of `source_application_version` or
`schema_fingerprint` is required. `claude-code-otel` is not valid on this
endpoint and remains an OTLP `/v1/traces` adapter. The composite registry label
`claude-code-otel+claude-code-hook` is never a persisted adapter value.

`explicit_link`, when present, is exactly:

```json
{
  "source_surface": "copilot-sdk|copilot-cli|vscode|hook-unknown|claude-code",
  "native_session_id": "nonblank 1..256 characters",
  "kind": "resume|handoff"
}
```

No other v1 field or inferred relationship represents explicit linkage.

The v1 envelope example is:

```json
{
  "schema_version": 1,
  "source_adapter": "claude-code-hook",
  "source_surface": "claude-code",
  "native_session_id": "claude-session-example",
  "source_application_version": "2.1.207",
  "adapter_version": "claude-hook-v1",
  "schema_fingerprint": null,
  "normalization_version": "session-normalization-v1",
  "explicit_link": {
    "source_surface": "claude-code",
    "native_session_id": "prior-claude-session",
    "kind": "resume"
  },
  "events": [
    {
      "source_event_id": "event-1",
      "type": "session.started",
      "occurred_at": "2026-07-11T10:00:00+09:00",
      "payload": {}
    }
  ]
}
```

Each v1 event has these fields:

| Field | Required | Contract |
| --- | --- | --- |
| `source_event_id` | yes | Nonblank string, 1..256 characters. |
| `type` | yes | String matching `^[A-Za-z][A-Za-z0-9._-]{0,127}$`. |
| `occurred_at` | yes | ISO-8601 timestamp with an explicit offset. |
| `payload` | yes | JSON object; scalar, array, and null are invalid. |
| `parent_event_id` | no | JSON null or a string 1..256 characters. |
| `run_native_id` | no | JSON null or a string 1..256 characters. |
| `trace_id` | no | JSON null or a string 1..128 characters. |

`copilot-sdk-stream` is valid only with source surface `copilot-sdk`.
`copilot-compatible-hook` is valid only with `copilot-cli`, `vscode`, or
`hook-unknown`. Adapter/surface mismatch is
`400` / `invalid_session_event_request`.

An unknown but syntactically valid event `type` is stored with normalized status
`unsupported`, increments `unsupported_event_version_count`, and prevents
`full` completeness. The normalizer must not guess a mapping. Event `payload`
is raw-bearing local runtime data and is not returned by sanitized reads.

The endpoint returns `204` only after the complete batch is committed. A
rejected or failed batch does not report success. Error responses use only the
fixed shape:

```json
{ "error": "<code>" }
```

The fixed failure mapping is:

| Status | Error code | Condition |
| ---: | --- | --- |
| `400` | `invalid_session_event_request` | Invalid v1 request other than an unsupported version. |
| `400` | `unsupported_session_event_version` | Unsupported header or body schema version. |
| `413` | `request_too_large` | Body exceeds 1 MiB. |
| `415` | `unsupported_media_type` | Request is not JSON. |
| `503` | `session_event_queue_full` | Session event queue is full. |
| `503` | `session_store_busy` | SQLite remains busy after retry. |
| `504` | `session_event_commit_timeout` | Batch commit does not finish before the commit timeout. |

Responses and logs must not echo payload content, credentials, PII, local paths,
or raw exception messages.

## Installed Hook Forwarder

The installed Local Monitor provides this mode:

```text
hook-forward --endpoint <loopback-url> --timeout-ms 250
```

It reads exactly one JSON payload from stdin and forwards it to the Session
event ingest endpoint. It always exits `0` for invalid input, network failure,
or timeout, writes nothing to stdout or stderr, and never influences the agent
Hook decision. The endpoint must be loopback. No permissive alternate parser,
retry path, or agent-decision fallback is added.

GitHub Copilot CLI and VS Code use the same PascalCase Hooks contract.
Ambiguous Hook input is recorded as `hook-unknown`; its surface must not be
inferred from environment variables, repository metadata, tool names,
transcript paths, or timestamps. OTel `client_kind` may only confirm whether
`hook-unknown` is `copilot-cli` or `vscode`; it is never combined with
conversation ID for Session binding or merge.

## Canvas And SDK Capture

- App/SDK capture uses Canvas `ctx.sessionId` as the native session ID.
- Persisted SDK events are stored through the Session subsystem.
- Ephemeral usage is aggregated rather than persisted as event content.
- Reasoning and streaming deltas are not persisted.
- Capture begins at the first Canvas open. Earlier events are not reconstructed;
  their absence lowers completeness.

Issue #51 does not change the Issue #45 `session.send()` execution behavior or
transfer execution ownership to Local Monitor. Issue #49 Agent ownership
semantics are also unchanged.

## Sanitized Workspace Reads

All endpoints in this section return sanitized metadata and never return event
`payload` or raw content. Every field defined as nullable or optional in a v1
response is present with JSON `null`, not omitted.

```text
GET /api/session-workspace/sessions?limit=<1..200>
GET /api/session-workspace/sessions/{sessionId}
GET /api/session-workspace/resolve?source_surface=<enum>&native_session_id=<urlencoded>
GET /api/session-workspace/status
```

`GET /api/session-workspace/sessions` defaults `limit` to `50`, orders items
most-recent-first, and returns `{ "items": [...] }` only. Version 1 has no
pagination and no additional filters. Each list item has exactly these fields:

| Field | Contract |
| --- | --- |
| `session_id` | Local UUIDv7 string. |
| `status` | `active`, `completed`, `failed`, or `unknown`. |
| `completeness` | `unbound`, `partial`, `rich`, or `full`. |
| `completeness_reason_codes` | Canonically ordered Issue #61 reason-code array. |
| `source_surfaces` | Array of source-surface enum values. |
| `source_diagnostic` | Additive sanitized object defined by `source-schema-drift-claude-code.md`, or `null` when no observation is linked. |
| `binding_state` | `hook_only`, `otel_only`, or `exact_linked`. |
| `content_state` | Nullable aggregate capture state defined by `source-schema-drift-claude-code.md`; never a UI-derived fallback. |
| `repository` | Nullable string. |
| `workspace` | Nullable string. |
| `started_at` | Nullable ISO-8601 timestamp. |
| `ended_at` | Nullable ISO-8601 timestamp. |
| `last_seen_at` | ISO-8601 timestamp. |
| `raw_retention_state` | `expiring`, `expired_pending_deletion`, or `not_captured`. |

An invalid or out-of-range `limit` returns `400` with
`{ "error": "invalid_session_workspace_query" }`.

`GET /api/session-workspace/sessions/{sessionId}` returns exactly five
top-level fields (`human_evaluation` is the Issue #52 additive amendment; see
`canvas-session-workspace-ui.md`):

- `session`: the exact additive list-item shape above;
- `human_evaluation`: JSON `null`, or `{ "verdict": "expected"|"problem",
  "recorded_at": "<ISO-8601>" }` recorded through the Issue #52
  human-evaluation endpoint;
- `native_ids`: entries with `source_surface`, `native_session_id`,
  `binding_kind` (`native`, `explicit_resume`, `explicit_handoff`, or
  `trace_context`), and `observed_at`;
- `runs`: entries with `run_id`, `source_surface`, `native_run_id`, `trace_id`,
  `parent_run_id`, `model`, `status`, `started_at`, `ended_at`, `input_tokens`,
  `output_tokens`, and `total_tokens`;
- `events`: entries with `event_id`, `run_id`, `source_surface`, `type`,
  `occurred_at`, `parent_event_id`, `status`, and `content_state` (`available`,
  `not_captured`, `redacted`, `unsupported`, or
  `expired_pending_deletion`).

The detail response does not return `payload`. Fields described as nullable or
optional in the v1 response contract are present with JSON `null`, not omitted.
This applies to absent run native/trace/parent/model/timestamps/token
values and absent event run/parent/status values.

An invalid `sessionId` UUID returns `400` with
`{ "error": "invalid_session_id" }`. A valid but missing Session returns `404`
with `{ "error": "session_not_found" }`.

`GET /api/session-workspace/resolve` returns one of:

```json
{ "binding_status": "bound", "session_id": "...", "completeness": "unbound|partial|rich|full" }
```

with `200`, or:

```json
{ "binding_status": "unbound" }
```

with `404`. `source_surface` uses the ingest surface enum. Resolution follows
the exact identity and merge rules above and never uses repository or timestamp
proximity.
`source_surface` must be an enum value and `native_session_id` must be a
URL-encoded nonblank string of 1..256 characters.
An invalid resolve request returns `400` with
`{ "error": "invalid_session_resolution_request" }`.

`GET /api/session-workspace/status` returns `schema_version`,
`normalizer_status` (`ready` or `degraded`),
`unsupported_event_version_count`, `projection_cursor`, and
`projection_backlog`. `schema_version` is the integer `1`;
`unsupported_event_version_count` and `projection_backlog` are nonnegative
integers; `projection_cursor` is JSON null or a nonnegative integer.

## Improvement Proposal Amendment

Issue #54 adds the proposal collection and mutation routes defined in
`canvas-improvement-proposals.md`. They are additive to this workspace API:
they do not change the list-item or Session-detail shapes above, Session
identity/merge rules, completeness, raw-content routes, or the Issue #45
`session.send()` behavior. Proposal text and evidence references are sanitized
local-runtime metadata; raw event content remains available only through the
separate raw event-content route below.

## Proposal Apply Amendment

Issue #55 adds the separately token-gated Canvas-helper and Local Monitor
apply contract in `canvas-proposal-apply.md`. It consumes an existing Issue #54
proposal but does not alter Session identity, Session list/detail shapes, raw
content routes, ingest semantics, or the proposal lifecycle. Apply drafts,
approval, snapshots, and audit are local runtime data; only opaque proposal /
Session references and sanitized state are workspace metadata.

## Effect Comparison Amendment

Issue #56 adds the objective-evaluation, application-receipt, candidate, and
effect-comparison routes defined in `canvas-effect-comparison.md`. Objective
evidence must bind to an existing Session/Run/trace and does not change Session
identity, merge, completeness, ingest, list/detail shapes, or raw-content
retention. Comparison cohort and effect rows are sanitized local-runtime
metadata; event payloads and raw trace content remain outside this interface.

## Raw Event Content Read

The raw-bearing content route is:

```text
GET /sessions/{id}/events/{eventId}/content
```

An available content response has this shape:

```json
{
  "event_id": "...",
  "content_kind": "...",
  "content": "...",
  "captured_at": "...",
  "expires_at": "..."
}
```

The route is same-origin, uses `Cache-Control: no-store`, and is absent under
`--sanitized-only` (`404`). Unknown Session/Event content also returns `404`.
After expiry it returns `410` with:

```json
{ "error": "raw_content_expired", "content_state": "expired_pending_deletion" }
```

Raw content is secret-filtered before separate storage and receives
`expires_at = captured_at + 90 days`. Expiry changes read behavior but does not
physically delete the stored row. Automatic physical deletion, pin, and
delete-now remain Issue #57 scope.

## OTel Enrichment

OTel enrichment runs after the existing monitor projection and uses a dedicated
cursor in `session_projection_state`. It may bind or enrich a Session from a
byte-for-byte trace-context match already recorded on an event. Exact
`gen_ai.conversation.id` may bind/enrich only when byte-for-byte equal to an
already-recorded native session ID. Otherwise the OTel evidence remains
`unbound`. `client_kind` never participates in Session binding or merge; it may
only confirm whether an ambiguous `hook-unknown` surface is `copilot-cli` or
`vscode`. An ingest gap, unsupported event version, inexact OTel linkage, or
missing surface-required evidence prevents `full` completeness.

The existing OTLP receiver, trace/span schema, monitor projection cursor, and
readiness contract are unchanged. Session schema migration runs during startup;
failure fails Local Monitor host construction, matching analysis-store startup
migration behavior. It does not add or alter readiness body fields, thresholds,
units, configuration names, or HTTP status mapping. Session normalization is
not added to `RawTelemetryStore.cs`.

## Pre-UI Gate And Non-Goals

Before any Issue #51 Session UI implementation, Issue #52 must capture the
current screen and obtain approval for the four-tab prototype. This is a
mandatory gate, not optional design evidence.

Issue #51 does not add direct apply, Compare, Agent graph behavior, automatic
physical raw cleanup, pin, delete-now, compatibility shims, dependencies, or
changes to Issue #45 `session.send()` or Issue #49 Agent ownership.
