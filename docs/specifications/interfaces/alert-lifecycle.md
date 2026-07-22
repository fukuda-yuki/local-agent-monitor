# Alert Lifecycle Interface

## Scope

This specification defines Issue #83: the versioned lifecycle state/event
model, append-only persistence, derived current state, mutation/read API,
optimistic concurrency, idempotency, explicit reevaluation handoff, and audit
contract for immutable Issue #80 alert receipts.

Concrete alert rules, source parsing, Alert Center UI/aggregation,
notifications, and heuristic predecessor discovery are outside this interface.
The Issue #80 receipt bytes and the `alert_engine` component-owned tables remain
immutable.

## Fixed v1 contracts

| Contract | Version/value |
| --- | --- |
| Lifecycle state/event/API | `alert.lifecycle.v1` |
| Sanitized export profile | `sanitized-alert-lifecycle.v1` |
| SQLite component | `schema_version(component='alert_lifecycle', version=1)` |
| Actor label | `local_user` |
| History default/maximum | `50` / `100` events |
| Comment maximum | `256` Unicode scalar values |
| Reason-code maximum | `64` lowercase metadata-token characters |
| Idempotency key | `aid1_` plus 43 unpadded base64url characters |

Unknown versions, states, actions, fields, or error codes fail closed. V1 is
single-alert only; batch mutation is not supported.

## State and event model

The closed state vocabulary is `open`, `acknowledged`, `dismissed`, `resolved`,
and `superseded`. A receipt with no lifecycle event is lazily projected as
`open` at revision `0`; reading it does not create a row. Every successful
mutation appends exactly one event at the next revision. Current state and
revision are derived from the highest event revision and are never maintained
in a mutable current-state table.

| Action | Caller | Allowed current state | Result |
| --- | --- | --- | --- |
| `acknowledge` | user | `open` | `acknowledged` |
| `dismiss` | user | `open`, `acknowledged` | `dismissed` |
| `resolve` | user | `open`, `acknowledged` | `resolved` |
| `reopen` | user | `dismissed`, `resolved` | `open` |
| `supersede` | trusted internal producer | `open`, `acknowledged`, `dismissed`, `resolved` | `superseded` |
| `source_deleted` | trusted internal producer | any state | unchanged |

`superseded` is terminal. A user cannot create a supersede or source-deletion
event. Invalid transitions return `alert_invalid_transition` without an event.
An explicit reopen creates a new revision on the same receipt; a new rule or
configuration receipt starts independently at lazy `open` revision `0`.
Dismissed or resolved state is never inherited by a new receipt.

## Persistence and concurrency

The lifecycle component owns only `alert_lifecycle_events`. Each row stores a
globally unique event ID, immutable `alert_id`, monotonically increasing
per-alert revision, action, previous/new state, canonical occurred time, fixed
local actor label, bounded reason code and optional sanitized comment,
idempotency key and canonical request hash, optional explicitly supplied old
and new alert IDs, and a bounded result code. Rows are append-only: application
code exposes no update/delete operation and database triggers reject update or
delete.

The store verifies the referenced `alert_id` in the Issue #80
`alert_receipts` table but never updates an engine-owned row. Mutation runs in
one immediate transaction, compares `expected_revision` with the derived
revision, validates the transition, and inserts the next event. A mismatch or
losing concurrent writer returns `alert_revision_conflict`; no last-write-wins
path exists.

Initialization is additive and transactional. Fresh databases and databases
with the accepted `alert_engine` v1 component are supported. Existing
`schema_version` rows and all non-lifecycle tables/rows are preserved. Missing,
newer, or definition-mismatched lifecycle components fail closed with
`alert_lifecycle_store_unavailable`; they are not repaired or downgraded.

## Idempotency

Every mutation requires one canonical idempotency key. The request hash covers
the schema version, route alert ID, action, expected revision, reason code,
sanitized comment, actor, and any explicit supersession IDs. Repeating the same
key with the byte-equivalent canonical request returns the exact prior result,
even when its original expected revision is now stale, and appends no event.
Reusing the key for any different request returns
`alert_idempotency_conflict` and appends no event.

## Reevaluation, supersession, and source deletion

Issue #80 deterministically gives the same alert ID for the same
rule/version/input/configuration, so an unchanged reevaluation reuses the
existing lifecycle and creates no lifecycle event.

A trusted producer may explicitly call the supersession seam with both
`old_alert_id` and `new_alert_id`. Both immutable receipts must exist and be
different. The store appends a supersede event only to the exact old alert. It
never searches or infers a predecessor or group from rule ID, Session, trace,
repository, workspace, time, content, or similarity. The new receipt remains
lazy `open` revision `0` regardless of the old receipt state.

Raw retention/deletion never deletes or rewrites the sanitized alert receipt
or lifecycle events. A future retention producer may explicitly call the
source-deletion callback seam for one alert ID. That callback appends a
`source_deleted` audit event while preserving state and receipt readability;
v1 does not infer this callback from Session, trace, time, or rule metadata.

## Sanitized audit values

Reason codes use the bounded lowercase metadata-token grammar. Comments are
optional inert text and are never populated automatically from evidence,
prompts, responses, tool arguments/results, or exception messages. Input with
control characters, markup delimiters, URI/path forms, email-like PII, or
credential/token markers is rejected as `alert_comment_not_sanitized`; it is
not truncated or partially persisted. Logs and error responses never include a
comment, evidence body, provider exception, path, or database text.

## HTTP API

The loopback Local Monitor exposes:

- `GET /api/alerts/v1/{alert_id}/lifecycle`
- `GET /api/alerts/v1/{alert_id}/lifecycle/history?limit=<1..100>`
- `POST /api/alerts/v1/{alert_id}/lifecycle/actions`

The POST body is strict JSON containing exactly `schema_version` =
`alert.lifecycle.v1`, `action`, `expected_revision`, `reason_code`, and optional
`comment`. It requires
`Content-Type: application/json`, same-origin request metadata,
`x-monitor-csrf: local-monitor`, and an `Idempotency-Key` header. Unknown or missing fields,
invalid enum values, and bodies over 64 KiB fail closed. User HTTP actions are
exactly acknowledge, dismiss, resolve, and reopen; internal supersede and
source-deletion seams are not HTTP routes.

All lifecycle responses use `Cache-Control: no-store`, strict bounded DTOs,
canonical UTC timestamps, and no arbitrary message/detail field. History is
ordered by revision descending and returns at most the requested bounded
limit. The fixed status mapping is:

| Condition | HTTP/code |
| --- | --- |
| Success | `200` |
| Invalid body/version/action/comment/idempotency key | `400` / bounded `alert_*` code |
| Invalid host | `400 invalid_host` |
| Cross-origin or missing CSRF | `403 cross_origin_forbidden` / `403 csrf_required` |
| Missing receipt | `404 alert_not_found` |
| Invalid transition | `409 alert_invalid_transition` |
| Stale revision | `409 alert_revision_conflict` |
| Idempotency-key reuse mismatch | `409 alert_idempotency_conflict` |
| Wrong content type | `415 unsupported_media_type` |
| Oversized body | `413 request_too_large` |
| Busy/unavailable lifecycle store | `503 alert_lifecycle_store_busy` / `503 alert_lifecycle_store_unavailable` |

Lifecycle initialization and route failures are route-local. They do not
change ingestion acceptance, monitor liveness, readiness, or unrelated route
behavior.

## Required proofs and handoff

Tests pin the transition table, lazy revision zero, append-only derivation,
optimistic concurrency, exact replay and mismatched idempotency, immutable
receipt bytes, explicit-only supersession, no dismissal inheritance, explicit
source-deletion callback, comment leakage rejection, bounded history order,
fresh database creation, supported engine-v1 upgrade/coexistence, broken/newer
schema refusal, strict API DTOs, loopback/same-origin/CSRF/no-store controls,
and route-local 503 behavior.

Issue #84 consumes `alert.lifecycle.v1`, revision-descending bounded history,
and `sanitized-alert-lifecycle.v1`. Concrete rule packs and retention producers
remain responsible for calling the trusted seams with exact IDs; Issue #83
ships no end-to-end producer that guesses these relationships.
