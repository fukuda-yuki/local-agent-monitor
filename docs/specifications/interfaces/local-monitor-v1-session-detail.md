# Local Monitor v1 Session Detail Backend Contract

Status: accepted by the user's 2026-08-26 Issue #134 backend instruction.

This document is the sole exact response and read-semantics authority for the
four raw-default Session-detail APIs. It composes with
`local-monitor-v1-route-transport.md`, Session v14, the #156/#161 Repository
scope snapshot, #154 Skill projection, Retention, and the existing Skill
snapshot routes. It does not change the Repository or Session collection
contracts, frozen `/api/monitor/*`, frozen `/api/session-workspace/*` v1, or
SSE.

## Routes and common transport

```text
GET /api/local-monitor/v1/sessions/{sessionId}/summary
GET /api/local-monitor/v1/sessions/{sessionId}/timeline
  ?workspace_revision=&execution_id=&parent_node_id=&after=&limit=
GET /api/local-monitor/v1/sessions/{sessionId}/nodes/{nodeId}
  ?workspace_revision=
GET /api/local-monitor/v1/sessions/{sessionId}/nodes/{nodeId}/content
  ?workspace_revision=&part=
```

The routes exist only in raw-default composition. Every response has
`Cache-Control: no-store`; no response has CORS, `ETag`, `Location`,
`Set-Cookie`, or reflected request data. Successful JSON is compact strict
UTF-8 with no BOM, indentation, trailing whitespace, or newline and has
`Content-Type: application/json; charset=utf-8`.

Only GET and HEAD are accepted. HEAD selects the exact GET status, headers,
media type, and representation `Content-Length`, but emits zero entity bytes.
Every other method is exact `405`, has `Allow: GET, HEAD`, and uses the fixed
JSON error representation. `OPTIONS` is not a CORS preflight response.
Each complete JSON success entity is bounded to 8,388,608 UTF-8 bytes; overflow
is `workspace_too_large` and no partial JSON is returned.

After the common Host guard, precedence is:

```text
method
-> path identifier grammar
-> complete closed query grammar
-> exact Session existence
-> workspace revision recomputation/equality
-> exact execution/node membership
-> cursor decoding and position membership
-> Retention content admission and lease
```

An invalid Host wins before method dispatch. A wrong method wins before path,
query, identity, or data lookup. A malformed identifier or query wins before
resource lookup. A valid absent Session is not found. For timeline/node/content,
a stale revision wins after Session resolution and before child resolution, so
no old/new fact mixture discloses whether a child exists in the new snapshot.

All errors are exact compact JSON `{"error":"<fixed_code>"}`.

| Condition | Status/code |
| --- | --- |
| Invalid Host | `400 invalid_host` |
| Wrong method | `405 method_not_allowed` |
| Malformed path ID, unknown/duplicate/missing query, invalid value | `400 invalid_request` |
| Invalid/tampered/restarted/filter-mismatched timeline cursor | `400 invalid_cursor` |
| Valid absent Session | `404 session_not_found` |
| Valid absent/mismatched execution | `404 execution_not_found` |
| Valid absent, wrong-Session, wrong-execution, or mismatched node | `404 node_not_found` |
| Revision mismatch | `409 workspace_snapshot_stale` |
| More than 256 executions, more than 4,096 nodes, or bounded response overflow | `409 workspace_too_large` |
| Content was not captured | `404 raw_content_not_captured` |
| Content expired | `410 raw_content_expired` |
| Content was deleted | `410 raw_content_deleted` |
| Content read is denied | `403 raw_content_read_denied` |
| Content exceeds 1,048,576 UTF-8 bytes | `413 raw_content_too_large` |
| Retention access lease is lost before response completion | `409 raw_content_lease_lost` |
| SQLite remains busy after the bounded policy | `503 persistence_busy` |
| Required authority is unavailable or inconsistent | `503 local_monitor_ui_unavailable` |

## Closed query grammar

Names and values are case-sensitive. Unknown keys, empty keys/values,
duplicates, percent-encoded unreserved characters, whitespace, raw `+`, or
noncanonical identifiers are `invalid_request`.

- `workspace_revision`: exactly 64 lowercase hexadecimal characters. Required
  by timeline, node, and content; prohibited on summary.
- `execution_id`: canonical lowercase local UUIDv7. Optional on timeline.
- `parent_node_id`: exact `node-` plus 32 lowercase hexadecimal characters.
  Optional on timeline and requires `execution_id`.
- `after`: canonical unpadded base64url timeline cursor. Optional on timeline.
- `limit`: canonical ASCII decimal integer, `1..200`; omission means `100`.
- `part`: required on content and exactly one of `instruction`, `tool_input`,
  `tool_result`, `error_message`, `subagent_input`, or `event_content`.

Summary accepts no query. Node accepts only `workspace_revision`. Content
accepts only `workspace_revision` then `part`. Timeline generated query order is
`workspace_revision`, `execution_id`, `parent_node_id`, `after`, `limit`.

## Exact Summary response

Schema token: `local-monitor-session-summary.response.v1`.

Top-level property order is `schema_version, workspace_revision, session,
executions, technical_references`.

`session` property order is `session_id, status, completeness, assignment,
archive, instruction, source, model, version, timing, tokens, activity,
capture`.

`assignment`, `archive`, `source`, `model`, `tokens`, and the five activity
facts reuse the exact Session collection object graphs and property order.
`version` uses the same `{state,values}` closed fact shape as source/model.
`instruction` is exactly
`{state,label,additional_count,content_available}`. State is
`recorded|not_observed|not_captured|expired|invalid`; label is nonempty text only
for recorded, additional count is a nonnegative integer only when it is
authoritatively known, and content availability is true only when an exact raw
reference is currently Retention-admissible. `timing` is exactly
`{state,started_at,ended_at,last_seen_at,duration_ms}`. `capture` is exactly
`{state,notes}` with state `complete|partial|not_observed|invalid` and an
ordinal-sorted distinct closed note array.

`executions` contains `0..256` objects ordered by recorded start descending,
then source ordinal ascending, then execution ID ascending. Missing time sorts
after recorded; invalid time sorts after missing. Each object property order is
`execution_id, node_id, source, model, lifecycle, status, timing, tokens,
activity, child_count`.

`node_id` is the execution-root node. `source` and `model` are explicit nullable
strings. `lifecycle` is `selected|started|completed|failed|deselected|unknown`.
`status` is `active|completed|failed|unknown`. `timing` is
`{state,started_at,ended_at,duration_ms}` with state
`recorded|missing|invalid`; missing/invalid never produces zero duration.
For `recorded`, `started_at` is non-null. An active recorded interval has both
`ended_at` and `duration_ms` null; a completed recorded interval has both
non-null and duration is at least zero. For `missing` or `invalid`, all three
time values are null.
`tokens` and `activity` reuse the Session collection closed fact shapes at
execution scope. `child_count` is an exact nonnegative integer.

`technical_references` is exactly `{native_session_ids,trace_ids}`. Both are
ordinal-sorted distinct arrays of sanitized identifiers. No local path, raw
content, prompt, response, Tool payload, Skill body, or locator appears.

## Exact Timeline response

Schema token: `local-monitor-session-timeline.response.v1`.

Property order is `schema_version, workspace_revision, session_id,
execution_id, parent_node_id, items, next_cursor`.

When `execution_id` is absent, `parent_node_id` is absent and the page contains
execution-root nodes. When `execution_id` is present and `parent_node_id` is
absent, the page contains that execution's top-level nodes. With both present,
the page contains exact children. Unknown-parent nodes are children of one
persisted execution-specific `unknown_relation_group` node; they are never
re-parented to a nearby or same-name node.

Each item property order is `node_id, execution_id, parent_node_id,
relationship_authority, kind, name, lifecycle, status, timing, activity,
tokens, child_count, content_parts`.

`relationship_authority` is `exact|explicit|unknown`. `kind` is one of
`execution|agent|skill|tool|subagent|event|error|retry|permission|unknown_relation_group`.
`name` is `{state,text}` with state `recorded|not_observed|invalid`.
`lifecycle`, `status`, `timing`, `activity`, and `tokens` use the Summary closed
shapes. `content_parts` is an ordinal-sorted subset of the six accepted part
tokens and is availability metadata, not raw content.

Children are ordered by time authority group (`recorded`, `missing`, `invalid`),
then exact start instant ascending for recorded values, source ordinal
ascending, and node ID ascending. Pagination is keyset-only and returns no
duplicate or dropped item across pages for one revision.

The cursor is exactly 119 bytes before canonical unpadded base64url encoding
and exactly 159 ASCII characters after encoding:

```text
offset  size  field
0       1     version 0x01
1       32    HMAC-SHA256(key, filter frame)
33      1     time group: recorded=0, missing=1, invalid=2
34      8     signed UTC ticks, big-endian; zero unless group=recorded
42      8     nonnegative source ordinal, unsigned big-endian
50      37    canonical ASCII node ID
87      32    HMAC-SHA256(key,
                    ASCII("local-monitor-timeline-cursor\0v1\0") + bytes[0..86])
```

Because 119 bytes leave two payload bytes in the final base64 quantum, the
159th character is exactly one of `AEIMQUYcgkosw048`; every other otherwise
base64url character has nonzero padding bits and is noncanonical.

The filter frame is
`ASCII("local-monitor-timeline-filter\0v1\0")` followed by the canonical
Session ID, workspace revision, execution ID or empty, parent node ID or empty,
and effective decimal limit, each terminated by one zero byte. The cursor
carries no raw value, name, path, Tool/Skill text, or unkeyed digest. A restart
invalidates it. Changing this layout requires a new schema version.

## Exact Node response

Schema token: `local-monitor-session-node.response.v1`.

Property order is `schema_version, workspace_revision, session_id, execution,
node, parent_path, related, content`.

`execution` is the exact Summary execution-header object. `node` is the exact
Timeline item plus `technical_references`, where technical references is
`{source_kind,source_identity,trace_id,span_id,event_id}` and every unavailable
value is explicit null. `parent_path` is an ordered root-to-parent array of
exact Timeline items. Cycles or cross-Session/cross-execution edges make the
authority unavailable rather than being repaired.

`related` property order is `retry,recovery,children`. Each value is an
ordinal-stable array of exact Timeline items, bounded to 200; a larger relation
set is `workspace_too_large`, not silently truncated. Only persisted exact or
explicit edges are returned.

`content` has the six part names in the accepted part order. Each value is
`{state,available}` with state
`available|not_captured|expired|deleted|read_denied|oversized|invalid`.
Availability is true only for `available`. Skill body/path/current-file are not
represented here and remain exclusively on the #158 routes.

## Exact raw content response

Schema token: `local-monitor-node-content.response.v1`, carried in the exact
response header `X-Local-Monitor-Schema-Version`. A successful response is
`200`, `Content-Type: text/plain; charset=utf-8`, no-store, and the entity is
the exact decoded UTF-8 string value with no wrapper, BOM, added newline, or
normalization. It is rendered only as inert text by consumers.

The route binds Session, workspace revision, node, part, carrier store kind,
source item identity, and immutable Retention owner identity. It acquires the
existing access lease, reads the complete value, rejects invalid UTF-8 or more
than 1,048,576 bytes, re-proves the committed lease through response
completion, and only then completes success. It never streams a partial value.
Expiry, deletion, read denial, malformed carrier, lease loss, or binding drift
fails closed. Raw values never enter URL, error, log, telemetry, diagnostics,
repository artifact, or exception text.

## `local_workspace_projection` v4

V4 is the sole runtime reader shape. Exact v1 migrates to v2, exact v2 to v3,
and exact v3 to v4. There is no v3 reader, dual reader, compatibility fallback,
or read-time migration.

V4 adds owned tables for execution headers, nodes, node edges, and node content
references. The projection persists only sanitized facts and raw availability
references; raw content remains in its owner store. Exact table definitions and
indexes are frozen by `local_workspace_projection:4` schema tests.

Execution identity is SHA-256 over
`ASCII("local-workspace-execution-id\0v1\0")` plus a length-framed exact source
kind and source identity. The first 16 bytes are converted to a lowercase UUID
`D` string after setting RFC variant and version-7 bits. No timestamp semantic
is read from those bits.

Node identity is `node-` plus the first 16 bytes of SHA-256 rendered lowercase
hex. Each kind has a distinct ASCII domain. The frame contains only exact
source kind and exact source identity. Execution-root and unknown-relation-group
nodes use their execution's exact source identity under their own domains.
Display name, timestamp, array index, nearby record, cardinality, and Tool or
Skill name never participate.

Parent authority is persisted as exactly `exact`, `explicit`, or `unknown`.
Only an exact source FK/trace parent is exact; only a source-authored link is
explicit. All other cases are unknown. Time authority is exactly
`recorded|missing|invalid`, with canonical UTC ticks only for recorded values.

The v3-to-v4 migration is one SQLite transaction: exact v3 validation, create
v4 objects, deterministic bounded backfill, semantic validation, component
stamp update last, and commit. Failure rolls back objects, rows, and stamp.
Rerun is idempotent. Partial/future shapes fail closed. Startup, Retention
cleanup, Skill registry publication, online backup, restore staging, and
restore publication use the existing Workspace transaction participant and
publication gate and refresh v4 before publish.

## One coherent detail snapshot and revision

The existing Repository scope snapshot coordinator is refactored, not
duplicated. A detail request holds one host-scoped publication read lease,
opens one SQLite connection, and uses one coherent read transaction. The
target-Session contributor, #156 catalog read, #161 archive contributor,
current-valid Skill authority, Retention state, executions, nodes, and raw
availability all run inside that boundary. It never obtains a collection
snapshot and then reads detail on another connection/transaction.

Collection remains bounded to 10,000 Session rows. Detail uses exact
`session_id` predicates and reads at most 256 executions, 4,096 nodes, 200
related rows, and six content references per node. It never materializes all
10,000 Sessions as detail projections.

`workspace_revision` is lowercase SHA-256 of a domain-separated canonical
length-framed sequence containing at least the Session projection row,
assignment/archive revisions, Run/Event/Span facts, execution/node/edge
projection rows, current-valid Skill facts plus registry generation identity,
Retention content states, raw content availability references, and their owner
revisions. Summary returns it. Timeline/node/content recompute it within their
one coherent snapshot and compare before child resolution. A mismatch is the
fixed stale error and no response combines facts from different revisions.

## Single current-valid Skill aggregation

`SkillProjectionReadService` owns one transaction-capable current invocation
projection returning Session membership, execution/node facts, name/search
facts, and aggregate state from one result set. Collection `q`,
`summary.skill`, `has_skill`, Session Summary, execution facts, and node facts
consume this result. The prior OTel-only aggregate reader and separate SDK
search reader cease to be independent semantic authorities.

An OTel-only invocation is admitted only by the current resolved trace
generation and is counted. An SDK-only invocation is admitted without
trace/span when its exact Session/Event, snapshot/claim/receipt/Retention graph
and current registry tuple are valid and is counted. One OTel and one SDK claim
deduplicate only when both exact producer trace ID and exact producer span ID
match and then count once. Trace-only, Session, name, path, time, membership, or
cardinality joins are prohibited. If a Session has positive observations on
both arms and any cannot be paired by that exact tuple, its Skill aggregate is
`{state:"certification_pending",count:null}`; those unmatched names do not
satisfy `q` or `has_skill` and their execution/node fact is pending rather than
current-valid. Stale, invalid, expired, unavailable, or pending observations
never become current by inference.

## Contract artifacts and regression gates

Draft 2020-12 schemas freeze the three JSON responses. Golden fixtures freeze
empty/minimal/full JSON bytes, error bytes, HEAD lengths, content headers, and
timeline cursor bytes. Tests assert property order in addition to schema
validity. `TestData/LocalMonitorV1SessionDetail/transport-contract.json` is the
literal executable table for route methods, query grammar, error precedence,
status/error bytes, success/error headers, ceilings, and content parts; route
tests consume it without deriving expected bytes from runtime serializers.
The sibling literal `query-grammar.json` freezes rejection classes and the
canonical generated query order independently of the route implementation.
Existing Repository/Session collection schemas, fixtures, cursors, and exact
bytes are immutable regression inputs.
