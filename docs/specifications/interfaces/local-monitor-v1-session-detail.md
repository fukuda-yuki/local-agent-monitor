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

For all four routes, the common same-origin guard rejects a browser
`Sec-Fetch-Site` value other than `same-origin` or `none`, and rejects a
nonempty `Origin` other than the request's exact scheme/host/port
(case-insensitive). Absence of either header is allowed. Its GET and HEAD
response is exact `403` with `{"error":"csrf_rejected"}`;
HEAD keeps the GET representation length and emits no entity bytes. No CORS
header is emitted.

Precedence is:

```text
method
-> path identifier grammar
-> complete closed query grammar
-> same-origin guard
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
| Cross-site detail request | `403 csrf_rejected` |
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

Schema token: `local-monitor-session-summary.response.v2`. It is sole-current;
there is no v1 selector, negotiation path, or fallback.

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
`{state,started_at,ended_at,last_seen_at,duration_ms}`. `last_seen_at` is an
observation timestamp independent of native lifecycle: when start is absent,
headings and context show it explicitly as **最終観測**. An active stored status
with `timing.state=not_observed` is labeled **状態未観測**. No observed timestamp
completes a Session or supplies its duration. When Session timing is
`recorded`, `started_at` and `last_seen_at` are non-null. An active recorded
Session has both `ended_at` and `duration_ms` null; a completed recorded Session
has both non-null and duration is at least zero. `capture` is exactly
`{state,notes,coverage}` with state `complete|partial|not_observed|invalid` and
an ordinal-sorted distinct closed note array. `coverage` contains exactly one
entry, in this order, for `instruction`, `source`, `model`, `version`, `timing`,
`tokens`, `cache`, `skill`, `tool`, `subagent`, `error`, and `retry`. Each entry
is exactly `{signal_family,state}` and state is
`recorded|complete_zero|not_observed|source_unsupported|capture_gap|certification_pending|inconsistent|projection_invalid`.
`complete_zero` requires complete owner proof of an explicit zero. The UI uses
these facts directly and never infers coverage from counts, nulls, neighboring
events, Session completeness, or another signal family.

`executions` contains `0..256` objects ordered by recorded start descending,
then source ordinal ascending, then execution ID ascending. Missing time sorts
after recorded; invalid time sorts after missing. Each object property order is
`execution_id, node_id, latest, source_ordinal, source, model, lifecycle, status, timing, tokens,
activity, child_count`.

`source_ordinal` is the nonnegative persisted source order used for tie-breaking;
clients validate order with this number, never with the displayed source string.

`latest` is an explicit Boolean on every execution. An empty array has no
latest execution. A nonempty array has exactly one `latest:true`, selected by
the canonical order above. Clients consume the Boolean and never infer latest
from array position, time, lifecycle, ID, or another fact.

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

Schema token: `local-monitor-session-timeline.response.v2`. It is sole-current.

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
tokens, child_count, has_more_children, collapsed_children, content_parts,
source_references`.

`relationship_authority` is `exact|explicit|unknown`. `kind` is one of
`execution|agent|skill|tool|subagent|event|error|retry|permission|unknown_relation_group`.
`name` is `{state,text}` with state `recorded|not_observed|invalid`.
`lifecycle`, `status`, `timing`, `activity`, and `tokens` use the Summary closed
shapes. `content_parts` is an ordinal-sorted subset of the six accepted part
tokens and is availability metadata, not raw content.
`has_more_children` is explicit and is never inferred by comparing the page
with `child_count`. `collapsed_children` is exactly `{state,count}` with state
`complete|partial|unavailable`; unavailable has a null count and the other
states have an exact nonnegative count. `source_references` is exactly
`{state,references}`. Recorded contains `1..16` exact closed
`{source_kind,source_identity,trace_id,span_id,event_id}` rows; every
recorded row has at least one non-null exact identity among `source_identity`,
`trace_id`, `span_id`, and `event_id`; `source_kind` alone is not identity.
Every non-recorded state carries an empty array. Capture/observation time is
not promoted to source time.

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

Schema token: `local-monitor-session-node.response.v2`. It is sole-current.

Property order is `schema_version, workspace_revision, session_id, execution,
node, parent_path, related, content`.

`execution` is the exact Summary execution-header object. `node` is the exact
Timeline item plus `technical_references` and one `metadata` object, where
technical references is
`{source_kind,source_identity,trace_id,span_id,event_id}` and every unavailable
value is explicit null. `parent_path` is an ordered root-to-parent array of
exact Timeline items. Cycles or cross-Session/cross-execution edges make the
authority unavailable rather than being repaired.

`metadata.kind` equals `node.kind`; every variant is closed and there is no
dictionary, extension bag, inferred value, or alternate metadata object.
Execution, agent, and unknown-relation-group metadata contain only their exact
kind. The remaining variants are:

- Tool: `kind,caller,lifecycle,status,exit,mcp_server_identity,mcp_server_name,
  mcp_tool_name,input,result,error,retry,recovery,child_activity,
  source_references`. Caller/lifecycle/status, the three content availabilities,
  retry/recovery node sets, child activity, and sources are state-bearing facts.
  `exit` is a closed availability/state fact and never fabricates a numeric exit
  code. Outcome/status is admitted only within exact OTel status or Hook
  lifecycle authority. The client-supplied 64-lowercase-hex MCP server hash is
  the only exact server identity currently available; a literal MCP server name
  is explicitly unavailable. Tool name is admitted only from an exact source.
  Per-Tool retry/recovery remains non-recorded or source-unsupported unless its
  exact edge exists.
- Skill: `kind,current_valid_state,source,trigger,inventory_reference,
  historical_snapshot_reference` only. Current-valid state is
  `current|stale|invalid|certification_pending|unavailable`; each other member
  is a state-bearing exact fact. Per-invocation inventory reference remains
  explicitly unavailable until its owner supplies it. Current file/body/path is
  not added.
- Sub-agent: `kind,lifecycle,input,activity,tokens,children,source_references`.
  Lifecycle is a closed object with separate state-bearing
  `selected,started,completed,failed,deselected` facts. Input availability,
  activity, token components, child count, and
  sources remain independently explicit; `SubagentStart.agent_id` is not input,
  and SDK token facts are not inferred.
- Error: `kind,error_code,message,status,source_references`.
- Permission: `kind,decision,wait,source_references`. Decision and wait remain
  explicit unavailable/not-observed/source-unsupported facts when absent.
- Event: `kind,event_name,source_time,content,source_references`. Hook capture
  time is never source time.
- Retry: `kind,attempt,target,recovered,source_references`.

Every non-recorded scalar/reference fact has a null value or empty reference
array as defined by the schema. Proximity, names, lifecycle correlation, and
other families never fill it.

`related` property order is `retry,recovery,children`. Each value is an
ordinal-stable array of exact Timeline items, bounded to 200; a larger relation
set is `workspace_too_large`, not silently truncated. Only persisted exact or
explicit edges are returned.

`content` has the six part names in the accepted part order. Each value is
`{state,available}` with state
`available|not_captured|expired|deleted|read_denied|oversized|invalid`.
Availability is true only for `available`. An explicitly unsupported producer
message with no stored content or retention owner has `not_captured` raw
availability; its persisted unsupported event contributes the existing
`source_unsupported` capture note, displayed as an unsupported-format reason.
It never grants a content read or invalidates other Session evidence.
Skill body/path/current-file are not
represented here and remain exclusively on the #158 routes.

## Exact raw content response

Schema token: `local-monitor-node-content.response.v2`, carried in both the
closed JSON entity and exact response header `X-Local-Monitor-Schema-Version`.
It is sole-current. A successful response is `200`, `Content-Type:
application/json; charset=utf-8`, no-store, and property order is
`schema_version,workspace_revision,session_id,node_id,part,state,
source_reference,text,utf8_byte_length,unicode_scalar_length,truncation`.
`state` is exactly `available`; `source_reference` is exactly
`{store_kind,source_item_id,revision}`; and `truncation` is exactly false.
`text` is the complete strict-UTF-8-decoded raw value represented as one inert
JSON string with no normalization or added newline. `utf8_byte_length` is the
selected text's exact pre-JSON UTF-8 byte length and
`unicode_scalar_length` is its Unicode scalar count. Consumers render only the
decoded string as inert text.

The route binds Session, workspace revision, node, part, carrier store kind,
source item identity, and immutable Retention owner identity. It acquires the
existing access lease, reads the complete value, rejects invalid UTF-8 or
selected text greater than 1,048,576 UTF-8 bytes, re-proves the committed lease through response
completion, and only then completes success. It never streams a partial value.
The complete JSON representation remains under the common 8,388,608-byte
success-entity ceiling.
Expiry, deletion, read denial, malformed carrier, lease loss, or binding drift
fails closed. Raw values never enter URL, error, log, telemetry, diagnostics,
repository artifact, or exception text.

## `local_workspace_projection` v5

V5 is the sole runtime reader shape. Exact v1 migrates to v2, exact v2 to v3,
exact v3 to v4, and exact v4 to v5. There is no v4 reader, dual reader, compatibility fallback,
or read-time migration.

V4 adds owned tables for execution headers, nodes, node edges, and node content
references. The projection persists only sanitized facts and raw availability
references; raw content remains in its owner store. Exact table definitions and
indexes are frozen by `local_workspace_projection:5` schema tests.

V5 removes the v4 `SubagentStart.agent_id` to `subagent_input` content mapping
and its `/agent_id` selector. `agent_id` is technical/native Run identity only;
it is never raw Sub-agent input, a label, prompt, source time, or relationship
proof. Exact v4 migration drops affected content-reference and content-
tombstone projection rows rather than reclassifying or retaining them. It does
not delete the source Event or Retention owner.

### Exact semantic aggregation source matrix

A lifecycle record becomes part of a semantic Tool or Sub-agent object only
through the following source/version-authorized carriers. Merely accepting an
event type does not authorize aggregation.

| Source family | Semantic object | Exact object carrier | Accepted lifecycle facts |
| --- | --- | --- | --- |
| Claude Hook | Tool | Claude Hook `tool_use_id`, preserved ordinally under an approved Hook version/fingerprint | `PreToolUse` starts the object; `PostToolUse` completes it; `PostToolUseFailure` fails it. All records must carry the same exact ID and native Session. |
| Claude Hook | Sub-agent | Claude Hook `agent_id`, preserved as technical/native Run identity under an approved Hook version/fingerprint | `SubagentStart` records `started`; matching `SubagentStop` records `completed`. It does not supply input, selected, failed, or deselected. |
| Session App/SDK | Tool | a start event's exact `source_event_id` plus a completion event's source-authored `parent_event_id` pointing to that start | Exact `tool.execution_start` and `tool.execution_complete` only. Same Run, name, or execution is insufficient without the authored edge. |
| Session App/SDK | Sub-agent | the adapter-declared event-specific native child `run_native_id`, identical across the lifecycle records | Exact `subagent.selected`, `subagent.started`, `subagent.completed`, `subagent.failed`, and `subagent.deselected` map independently to their same-named facts. The ID is not input. |
| OTel | Tool | exact OTel `trace_id` plus `span_id` on a span already authorized as a semantic Tool by the source adapter | Start/end and OTel status apply only to that span; an event name or `tool_name` alone is insufficient. |
| OTel | Sub-agent | exact trace/span or native identity only when the source adapter explicitly declares that span kind as a semantic Sub-agent | No current Claude OTel span mapping is authorized; unsupported spans remain ordinary nodes. |

For a Claude Hook Sub-agent semantic object, `agent_type` from the authenticated
`SubagentStart` / `SubagentStop` retained content is the only current display
name/type authority. The projection records it only when the approved Hook
version/fingerprint yields one distinct, nonblank value no larger than 256
UTF-8 bytes across the object. Missing, conflicting, invalid, or oversized
values remain `name:not_observed`; Compare groups those objects under its fixed
unidentified Sub-agent row. `agent_id` and SDK `run_native_id` remain carrier
identities only and are never display names or types. No SDK Sub-agent display
name/type field is currently authorized. Every authoritative lifecycle
observation must carry the same valid value for the name to be recorded; one
missing, invalid, blank, non-text, oversized, or conflicting observation makes
the semantic object's name unavailable.

The sanitized projection stores no raw `agent_type` in source receipts. Each
Claude Hook Sub-agent source-reference revision input carries either a
versioned unavailable marker or a versioned, domain-separated SHA-256 digest
of the exact bounded value. Detail validation remains raw-free: it authenticates
the persisted name against every per-reference marker. Backup validation also
recomputes each marker from the authenticated retained Hook content, detecting
source/receipt drift without weakening the sanitized Detail boundary.

The carrier selector itself is versioned source authority. A same-shaped field
from an unsupported source/version is not an identity. Two same-name or
concurrent objects with different carriers remain distinct. Name, time,
proximity, count, and execution membership are never join keys. A carrier is
scoped only by the source-authorized native Session/Run fields named in the
matrix; execution membership is neither added to nor substituted for it. A
carrier that crosses its source-native scope fails closed. Unmatched lifecycle
records remain individual Event or Error nodes; they do not create a partial
semantic object or attach to the nearest object. Exact Error/Retry/Permission
records retain their existing kind when that is the source-authored meaning.

Sub-agent metadata exposes the five lifecycle facts separately as
`lifecycle:{selected,started,completed,failed,deselected}`. Each member is a
closed state-only fact. `recorded` means that exact lifecycle fact was observed;
every other state remains explicit and cannot be inferred from another member.
In particular, completion does not imply selection or deselection, failure does
not imply completion, and a terminal Session or parent Tool state changes none
of the five facts.

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

The v4-to-v5 migration is one SQLite transaction: exact v4 validation, remove
only the obsolete mapping rows and selector allowance, deterministic bounded
refresh, semantic validation, component
stamp update last, and commit. Failure rolls back objects, rows, and stamp.
Rerun is idempotent. Partial/future shapes fail closed. Startup, Retention
cleanup, Skill registry publication, online backup, restore staging, and
restore publication use the existing Workspace transaction participant and
publication gate and refresh v5 before publish.

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

Draft 2020-12 schemas freeze all four JSON responses. Golden fixtures freeze
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
`content-full.json` is the independent literal JSON success representation;
its 504-byte GET representation length is also the HEAD `Content-Length`, while
HEAD emits zero entity bytes. Expected bytes and length are not derived from a
production serializer.

The nonempty production fixture index is fixed as follows. `summary-full.json`
is the deterministic Session/Run/Event projection example;
`summary-nonrecorded-evidence.json` is the deterministic native-Session example
whose model, version, timing interval, tokens, and execution evidence are not
observed; `timeline-page.json` is the first one-item page over two exact Event
children and carries the production HMAC cursor for key bytes `00..1f`;
`node-full.json` is the production execution-root node including its exact
children; and `node-nested.json` is the production Event node including its
exact root-to-parent path. Their Session, execution, node, workspace revision,
and cursor identities are computed by the production authorities and are not
hand-authored placeholders. Normative source projection currently persists no
retry/recovery edge for this source shape, so these production node fixtures
keep those arrays empty; serializer unit coverage remains responsible for the
otherwise-valid explicit retry/recovery shape in
`node-related-serializer-only.json` until a source-backed authority can produce
it. That fixture is not production-route evidence and is named accordingly.
