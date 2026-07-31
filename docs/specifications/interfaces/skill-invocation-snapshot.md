# Skill Invocation Snapshot Interface

Status: **Accepted foundation; production v2 activation blocked**

This specification is the detailed authority for Issues #119, #157, and #158.
It defines the frozen Session-ingest v1 correction, the accepted Skill-only v2
transport boundary, the Skill invocation snapshot component, historical
content reads, and explicit current-file reads.

The accepted DC158-01 through DC158-11 direction is binding. Production v2
parsing, persistence, and route registration remain blocked until the exact
wire, registry, digest, classification, and receipt decisions in
[Implementation decision gate](#implementation-decision-gate) are fixed in the
canonical contract. The blocked fields must not be filled from serializer
defaults, runtime SDK reflection, existing v1 behavior, property encounter
order, or implementation convenience.

This interface is additive to the frozen
[Canvas Session Workspace v1](canvas-session-workspace.md). It does not change
the shape, property order, bytes, availability, or semantics of
`/api/monitor/*`, `/api/session-workspace/*` v1, Session ingest v1, or SSE.

## Scope and authority

The ownership split is exact:

- Issue #154 is the single current-valid Skill claim and projection authority.
- This interface owns the `skill_invocation_snapshot:1` component and the
  raw-local historical/current-file reads for an exact SDK Skill event.
- The existing Session subsystem owns Local Session, Run, Event, and
  `session_event_content` identity.
- The existing Retention catalog owns the sole raw-content item, access and
  operation leases, read denial, pin/delete-now interaction, and physical
  cleanup.
- Issue #134 consumes the finished authorities for Workspace reads; it does not
  read these tables directly or create a second Skill projection.
- Issue #140 consumes this interface for the final Skill inspector.
- Issues #162/#163 may consume an exact historical snapshot only through their
  explicit bounded AI snapshot authority.

Historical Skill content and the current discovered file are different
authorities. Neither substitutes for the other.

## Current execution status

The frozen-v1 correction is independently executable:

- `skill.invoked` remains unsupported in Session ingest v1;
- `skill.started` and `skill.completed` remain supported in Session ingest v1;
  and
- the existing exact-source replay guard remains in force.

The following are not independently executable:

- a production `/api/session-ingest/v2/events` route;
- a partial Event/content writer;
- the SDK arm of the current-valid Skill claim authority;
- the snapshot component migration or writer;
- historical Skill-content or current-file HTTP reads; and
- production host registration of any v2/snapshot service.

A v2 route must not return `204` until all seven persistence authorities commit
atomically.

## Current-valid Skill claims

### Source arms

The Issue #154 Skill claim authority has exactly two source arms:

```text
otel_trace_span
sdk_session_event
```

The `otel_trace_span` arm is bound by:

- exact trace ID and exact span ID;
- the current SourceCompatibility resolution;
- the current #154 compatibility revision and generation; and
- the exact retained OTel input frontier used by that generation.

The `sdk_session_event` arm is bound by:

- exact Local Monitor Session ID;
- exact persisted `session_events.event_id`;
- exact producer source event ID;
- exact source adapter and source surface;
- exact source application version;
- exact adapter version;
- exact normalization version;
- exact payload schema;
- exact producer schema fingerprint; and
- exact payload digest, whose byte domain remains unresolved under
  [implementation-gate item 5](#implementation-decision-gate).

The complete SDK claim binding and current-read predicate remain owned by
[Skill Projection](../layers/skill-projection.md). This summary does not narrow
that authority or select the blocked payload-digest byte domain.

The SDK arm is current-valid only when the current compatibility registry
accepts this complete tuple:

```text
(
  source_application_version,
  adapter_version,
  normalization_version,
  payload_schema,
  schema_fingerprint
)
```

The existence of raw content or a snapshot row does not make an unsupported
tuple valid and does not revive an invalid or stale claim.

### Cross-arm merge

An SDK observation and an OTel observation may merge into one invocation claim
only when the producer supplied both an exact trace ID and an exact span ID and
both values equal the OTel claim.

The following never merge the arms:

- trace ID without span ID;
- Local Session or Run identity alone;
- Skill name or definition path;
- timestamp or ordinal;
- proximity or arrival order; and
- expected cardinality.

Without the exact trace-and-span link, both rows remain positive observations.
When one Session has both arms and they cannot be exactly deduplicated, the
aggregate Skill invocation count is JSON `null` and its existing state is
`certification_pending`. The observations are not added together and missing
is not converted to zero.

An OTel-only claim has no snapshot row. Its snapshot projection is:

```text
snapshot_id = null
snapshot_state = not_captured
```

No fake Event ID or placeholder snapshot row represents `not_captured`.

## Frozen Session ingest v1 correction

This accepted correction supersedes the previously documented inverse clause
in [Canvas Session Workspace v1](canvas-session-workspace.md). The shared
Canvas authority now lists `skill.started` and `skill.completed` in the exact
supported-v1 event set and treats `skill.invoked` as unsupported. This restores
the frozen pre-#119 event semantics. It does not change the route, header/body
versions, envelope/event wire shape, size/batch limits, error entity bytes, or
workspace response bytes.

The installed route remains:

```text
POST /api/session-ingest/v1/events
```

Its existing contract remains byte- and behavior-frozen:

- `X-CAO-Session-Event-Version: 1`;
- body `schema_version = 1`;
- body maximum `1,048,576` bytes;
- batch length `1..100`;
- existing adapter/surface enums;
- existing envelope/event property sets and required/nullable rules;
- existing five-value `session_events.content_state` vocabulary;
- existing validation order, status mapping, error entity bytes, queue behavior,
  commit timeout, and `204` behavior; and
- existing `/api/session-workspace/*` v1 response shape, property order, and
  bytes.

The supported v1 event set preserves its pre-#119 membership. In particular:

```text
capture.started
assistant.usage
session.usage_info
session.start
session.started
session.shutdown
session.task_complete
user.message
assistant.message
assistant.turn_end
tool.execution_start
tool.execution_complete
subagent.started
subagent.completed
subagent.failed
subagent.selected
subagent.deselected
skill.started
skill.completed
SessionStart
UserPromptSubmit
PreToolUse
PermissionRequest
PostToolUse
PostToolUseFailure
SubagentStart
SubagentStop
Stop
StopFailure
SessionEnd
```

`skill.invoked` is not a supported v1 event. A syntactically valid
`skill.invoked` v1 event follows the existing unsupported-event behavior:

- persist only the existing unsupported Event metadata;
- use `content_state = unsupported`;
- create no `session_event_content` row;
- increment the unsupported-event counter only for a newly admitted exact
  source event; and
- do not infer or redirect the event to v2.

The generic exact-source replay guard remains unchanged. For an already
persisted `(source_adapter, source_event_id)`:

- an identical replay creates no second Event or content row;
- it does not overwrite or backfill existing content;
- it does not increment the unsupported-event counter again; and
- a conflicting replay remains fail closed under the Session store authority.

There is no retry, redirect, or fallback from v2 to v1.

## Additive Skill-only Session ingest v2

### Settled transport boundary

The sole SDK Skill event transport is:

```text
POST /api/session-ingest/v2/events
```

The accepted transport constraints are:

- raw-default host only;
- `Content-Type: application/json`;
- `X-CAO-Session-Event-Version: 2`;
- body `schema_version = 2`;
- maximum request body `8,388,608` bytes, inclusive;
- `source_adapter = copilot-sdk-stream`;
- `source_surface = copilot-sdk`;
- `events.length = exactly 1`;
- `event.type = skill.invoked`;
- `payload_schema = github-copilot-sdk.skill-invoked.v1`;
- mandatory `source_application_version`;
- mandatory `adapter_version`;
- mandatory `normalization_version`;
- mandatory `schema_fingerprint`;
- invalid outer UTF-8 rejects the request before any write;
- duplicate or unknown outer envelope/event properties reject the request
  before any write;
- success is `204` only after the complete atomic commit; and
- no v1 retry, fallback, compatibility writer, permissive parser, or dual
  transport exists.

The exact outer envelope/event property inventory, nullability, order
requirements, SDK field mapping, and full v2 error registry are not settled.
No example envelope is canonical until those decisions are fixed.

### Skill payload

Payload property names are case-sensitive.

Required properties:

```text
name
path
content
```

Optional properties:

```text
allowedTools
description
model
pluginName
pluginVersion
source
trigger
```

The expected scalar/collection forms and accepted bounds are:

| Field | Accepted bound |
|---|---|
| `content` | string; `0..1,048,576` strict UTF-8 bytes after JSON unescape |
| `path` | string; `1..4,096` strict UTF-8 bytes after JSON unescape |
| `name` | string; `1..200` Unicode scalar values and at most `800` UTF-8 bytes |
| `description` | string; `0..4,096` Unicode scalar values and at most `16,384` UTF-8 bytes |
| `allowedTools` | array of at most `64` strings; each `1..128` Unicode scalar values and at most `512` UTF-8 bytes |
| `model` | string; at most `256` UTF-8 bytes |
| `pluginName` | string; at most `256` UTF-8 bytes |
| `pluginVersion` | string; at most `256` UTF-8 bytes |

`trigger` is exactly one of:

```text
user-invoked
agent-invoked
context-load
```

`source` is exactly one of:

```text
project
inherited
personal-copilot
personal-agents
custom
plugin
builtin
remote
```

Payload faults differ from outer faults:

- duplicate payload property: persist the Event and a snapshot classified
  `malformed` / `duplicate_property`;
- unknown payload property: persist the Event and a snapshot classified
  `malformed` / `unknown_property`; and
- invalid payload field type: persist the Event and a snapshot classified
  `malformed` / `invalid_field_type`.

Those rows may be persisted only by the complete seven-authority transaction.
The deterministic winner when multiple payload faults coexist remains part of
the implementation decision gate.

If decoded `content` or `path` cannot be represented as strict UTF-8, including
an unpaired surrogate after JSON unescape, the snapshot is `binary`.
Replacement-character repair is forbidden.

## Snapshot component

The independently versioned component is:

```text
skill_invocation_snapshot:1
```

The real snapshot table is:

```text
skill_invocation_snapshots
```

`snapshot_id` is a Local Monitor-generated UUIDv7. A real row is unique by:

```text
UNIQUE(session_id, event_id)
```

`event_id` references the existing local `session_events.event_id`. A producer
event ID alone never searches for or binds a Local Session.

The accepted component row contains these fields:

```text
snapshot_id
session_id
event_id
claim_id
run_id
trace_id
span_id
name
source
trigger
state
reason
content_item_id
payload_sha256
payload_bytes
body_sha256
body_utf8_bytes
definition_path_sha256
definition_path_utf8_bytes
source_application_version
adapter_version
normalization_version
payload_schema
schema_fingerprint
captured_at
created_at
```

`run_id`, `trace_id`, `span_id`, and body/path digests and sizes are nullable
only where the final accepted state mapping permits them. The exact nullability
matrix is not inferred before the classification decision is fixed.

Persisted `state` is exactly one of:

```text
available
malformed
missing
binary
oversized
```

The following are derived read states and are never persisted as snapshot
states:

```text
not_captured
expired
projection_invalid
```

Persisted `reason` is exactly one of:

```text
none
name_missing
body_missing
definition_path_missing
duplicate_property
unknown_property
invalid_field_type
name_invalid
body_unicode_invalid
path_unicode_invalid
body_oversized
path_oversized
path_invalid
```

The state/reason sets are closed. The precise multi-fault precedence,
`path_invalid` classification, and Skill-name normalization/validation mapping
remain blocked decisions and must not depend on JSON property encounter order.

Snapshot metadata remains after raw-content expiry or deletion. Body/path text
does not.

## Exact text and digest domains

Historical body and path digests use the exact decoded producer values:

```text
SHA256(exact strict UTF-8 bytes after JSON unescape)
```

Current-file body digest uses:

```text
SHA256(exact file bytes read from the validated handle)
```

Body/path digest inputs perform none of the following:

- secret filtering;
- BOM stripping;
- CRLF/LF conversion;
- Unicode normalization;
- trimming;
- path case conversion; or
- replacement-character repair.

Consequently these pairs produce different digests:

- CRLF and LF;
- NFC and NFD;
- BOM-present and BOM-absent;
- different path casing; and
- trailing-newline-present and trailing-newline-absent.

Historical path digest preserves the producer's exact spelling and casing.
Digest equality and string-prefix equality do not establish filesystem
containment.

The accepted decision does not yet define the byte domain for
`payload_sha256` and `payload_bytes`, nor the exact
`session_event_content` stored document/byte layout that carries body and path.
The implementation must not use raw JSON spelling, a canonical serializer,
secret-filtered v1 content, or runtime object serialization as an implicit
answer.

## Atomic persistence and equality replay

One accepted v2 event commits all of the following in one SQLite transaction:

1. exact Session/Event identity validation;
2. `session_events` insert;
3. exact `session_event_content` insert;
4. Retention ownership and item insert;
5. #154 SDK claim or invalid-claim state;
6. `skill_invocation_snapshots` insert; and
7. durable idempotency/equality receipt insert.

Event and snapshot must not be written in separate transactions. There is no
outbox, eventual repair, v1 intermediate write, or best-effort claim update.

Retention creates and returns `content_item_id` from the existing ownership
key:

```text
(store_instance_id, session_event_content, event_id)
```

The client and snapshot service do not generate this value. Pin, unpin, and
delete-now target this same `session_event_content` item; there is no second
Skill Retention item and no Skill-specific TTL.

Replay identity is the existing exact source Event identity. Replay behavior is:

- every Event field, payload digest/size, content item, claim, and snapshot
  field identical: return the stored result without a second write; and
- any mismatch: roll back the complete transaction and return the fixed
  conflict result.

The exact durable receipt key, request fingerprint framing, stored response
bytes/headers, and conflict status/entity bytes are not settled. A production
writer must not invent them or claim insert-or-identical completion without
them.

## Retention-authorized reads

The raw-content owner is the existing
[`session_event_content` Retention item](../layers/raw-store-normalization.md#retention-catalog-v1).

### Metadata read

Snapshot metadata GET composes only the snapshot index, Retention state, and
the single #154 current-valid Skill read/diagnostic authority.
`projection_validity` is supplied by or derived through that #154 authority;
the snapshot service does not reconstruct it with direct snapshot, registry,
generation, or claim-table SQL. The metadata read does not open body/path
content and therefore does not require a raw-content access lease.

### Historical content read

Historical content GET:

1. acquires an access lease for the exact `session_event_content` item;
2. rechecks read denial after lease acquisition;
3. reads the exact stored historical body/path;
4. constructs the complete response bytes in memory; and
5. releases the lease only after response-byte construction.

A denied, expired, missing, stale, or busy content item returns no partial body
or path.

### Current-file read

Current-file POST:

1. acquires an operation lease for the historical
   `session_event_content` item;
2. reads the historical path while the operation lease is active;
3. calls `ServerSkillsApi.DiscoverAsync`;
4. opens the current file through the platform-specific validated-handle path;
5. performs the bounded read and digest comparison;
6. constructs the complete response bytes; and
7. releases the operation lease only after response-byte construction.

Loss or expiry of the operation lease before response construction succeeds
discards the result. A worker or route must not publish bytes acquired outside
the valid lease.

## Raw-local HTTP interface

These routes exist only in the raw-default Local Monitor:

```text
GET  /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}
GET  /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/content
POST /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/current-file-read
```

An invalid UUID, a snapshot belonging to a different Session, and a missing
snapshot are indistinguishable `404` results.

### Metadata response

The property order is:

```text
schema_version
snapshot_id
session_id
claim_id
event_id
name
source
trigger
invoked_at
run_id
trace_id
span_id
projection_validity
snapshot_state
snapshot_reason
body_sha256
body_utf8_bytes
definition_path_sha256
definition_path_utf8_bytes
captured_at
source_application_version
adapter_version
payload_schema
```

`projection_validity` is exactly:

```text
current
invalid
stale
```

The exact mapping from current registry/generation/snapshot facts to those
three values remains in the implementation decision gate.

### Historical-content response

The property order is:

```text
schema_version
snapshot_id
content_kind
body
definition_path
body_sha256
definition_path_sha256
captured_at
```

`content_kind` is exactly:

```text
historical_snapshot
```

Historical body/path are the captured producer values. They are raw-local
content and are never returned by metadata-only readers.

### Current-file request and response

The closed request object is:

```json
{"schema_version":"local-skill-current-file-read.request.v1"}
```

The request maximum is `128` bytes. Unknown or duplicate properties are
rejected. The route requires same-origin and CSRF.
The exact accepted request media type, optional parameter handling, and response
media-type header bytes remain in the implementation decision gate. The
presence of a JSON example does not authorize an implementation to copy v1
media-type behavior.

The response property order is:

```text
schema_version
snapshot_id
content_kind
comparison
historical_body_sha256
current_body_sha256
current_body_utf8_bytes
body
read_at
```

`content_kind` is `current_file`. `comparison` is exactly `same` or `changed`.
The response does not return a current filesystem path.

### Errors and response bytes

The raw-local error registry is:

```text
invalid_request
skill_snapshot_not_found
skill_snapshot_content_unavailable
skill_snapshot_expired
skill_projection_not_current
skill_current_file_not_discovered
skill_current_file_missing
skill_current_file_unsafe
skill_current_file_binary
skill_current_file_oversized
skill_current_file_raced
csrf_rejected
request_too_large
unsupported_media_type
persistence_busy
local_monitor_ui_unavailable
```

The status mapping is:

| Status | Cases |
|---:|---|
| `400` | invalid request |
| `403` | CSRF rejection |
| `404` | snapshot not found or current file missing |
| `409` | projection invalid/not current, file not discovered, unsafe target, or file race |
| `410` | historical content expired |
| `413` | request too large |
| `415` | unsupported media type |
| `422` | historical snapshot unavailable because it is malformed/binary/oversized, or current file is binary/oversized |
| `503` | persistence busy or Local Monitor UI unavailable |

Every error entity uses exact UTF-8 bytes without BOM, indentation, or trailing
newline:

```json
{"error":"<fixed_code>"}
```

Every response in `/api/local-monitor/v1/*` has
`Cache-Control: no-store`, including success, `404`, `405`, `409`, `413`,
`415`, and CSRF rejection. Error responses never echo raw input, body/path,
credentials, PII, local inner exceptions, or provider payloads. Logs never emit
those values.

The explicitly authorized successful historical-content response returns the
captured `body` and `definition_path` fields above. The explicitly authorized
successful current-file response returns its `body` field above. No metadata
response or other error/success family receives that raw-content authority.

The accepted decision fixes the property order but does not yet supply literal
`schema_version` values for the three success response families or the complete
nullable/present-field matrix. Those bytes must be fixed before route
implementation; this specification does not invent them.

## Current-file discovery authority

Repository remote locators are not filesystem roots. Issue #156 is not a
dependency of current-file discovery.

Startup configuration is the sole discovery-root authority:

```text
SkillDiscovery.ProjectPaths       maximum 16
SkillDiscovery.SkillDirectories   maximum 32
```

Each configured path:

- is an absolute local path;
- is at most `4,096` UTF-8 bytes;
- is handle-validated at startup;
- is deduplicated by OS-native path identity; and
- is not a network or device namespace.

The validated canonical root list has one `discovery_revision`. The exact
root-list framing/hash algorithm is not yet fixed and must not be inferred from
path strings or collection order.

`ServerSkillsApi.DiscoverAsync` receives the configured `projectPaths` and
`skillDirectories` arrays.

The historical Event path parent, inferred Session CWD, Repository remote URL,
prompt, workspace label, timestamp, and discovery result outside the configured
roots never create or extend a root.

Source eligibility is:

| Source | Current-file eligibility |
|---|---|
| `project`, `inherited` | discovery result `projectPath` has the same filesystem identity as a configured project path |
| `custom` | discovery result is below a configured Skill directory |
| `personal-copilot`, `personal-agents`, `plugin` | the owning root is explicitly configured as a Skill directory |
| `builtin`, `remote` | unavailable for v1 current-file read |

A discovery candidate must satisfy three independent matches before any current
file can be opened:

1. its finalized normalized name equals the historical snapshot's finalized
   normalized name under the same normalization contract;
2. its closed `source` token equals the historical snapshot source using
   ordinal comparison; and
3. its producer-facing discovery path matches the exact historical producer
   path under the final producer-path-to-discovery comparison contract.

Only the path supplied by that accepted discovery result proceeds through the
configured-root handle walk. The resulting handle/root relation proves current
filesystem identity. The historical snapshot currently records a producer path
and path digest, not a filesystem identity, so it must not be described as
already proving volume/file ID or device/inode equality.

The exact name normalization, producer-path comparison, and filesystem-identity
proof representation are unresolved and remain in the implementation decision
gate. If no exact result exists, the route returns
`skill_current_file_not_discovered`. The service never opens the historical path
directly.

## No-follow and race-safe current-file read

### Common rules

- no user-supplied path is accepted;
- the historical path and current discovery inventory must identify the same
  target;
- the final target must be a regular file;
- read at most `1,048,577` bytes (`1 MiB + 1`);
- a file larger than `1 MiB` returns no body;
- accepted content is strict UTF-8;
- binary, oversized, unsafe, or raced reads return no partial/truncated body;
  and
- any identity or metadata change discards every byte already read.

### Windows

- accept drive-qualified absolute paths only;
- reject UNC paths, network shares, `\\?\`, `\\.\`, and device paths;
- reject alternate data streams; the drive colon is the only permitted colon;
- open every segment handle-relative from the authorized root;
- reject reparse points, junctions, symbolic links, and mount points at every
  segment;
- obtain the final volume serial and file ID;
- recheck file ID, size, and last-write time before and after the read; and
- confirm that the final path obtained from the open handle is inside the
  authorized root.

### Unix

- accept absolute paths only;
- traverse from the authorized root with `openat`;
- open intermediate segments with `O_NOFOLLOW | O_DIRECTORY`;
- open the final segment with `O_NOFOLLOW | O_RDONLY`;
- use `fstat` to prove a regular file;
- reject a device/mount crossing; and
- recheck device, inode, size, and mtime before and after the read.

Any race returns `skill_current_file_raced` after discarding the read bytes.

## Migration, backup, and restore

The component order around this feature is:

```text
retention
skill_projection
skill_invocation_snapshot
local_workspace_projection
```

`skill_invocation_snapshot:1` depends on:

- a compatible Session Event/content component;
- Retention; and
- `skill_projection:1`.

An older backup with no `skill_invocation_snapshot` component initializes an
empty v1 component. A declared partial component or a version newer than `1`
fails closed.

The snapshot component contributes only index/metadata and its component-owned
equality receipts. Historical body/path bytes are backed up exactly once by the
existing Session Event content owner. No second copy or Skill raw-content
carrier is added.

Restore validates:

- parent Session and Event existence;
- exact `skill.invoked` Event type;
- exact content-item ownership;
- exact claim linkage;
- unique `(session_id, event_id)`;
- digest and size consistency for an available row;
- expiry/tombstone shape;
- payload schema/version consistency; and
- insert-or-identical collision behavior.

An OTel-only `not_captured` observation has no row and therefore no snapshot
backup row.

The runtime backup remains the private raw-bearing profile defined by
[Runtime Backup and Restore](runtime-backup-restore.md). It is not a sanitized
export.

## Receiver-only and sanitized export composition

When the Local Monitor starts with `--sanitized-only`, none of the following is
registered:

```text
/api/session-ingest/v2/events
Skill snapshot writer
Skill current-file service
/api/local-monitor/v1/.../skill-invocations/*
```

There is no fallback to v1. OTel Skill claims may remain available under their
existing sanitized projection, but their snapshot state is `not_captured`.

The complete `skill_invocation_snapshot` namespace is excluded from sanitized
evidence export and import, including:

- snapshot ID;
- payload/body/path digest and byte size;
- path provenance;
- content availability;
- current-file result;
- body; and
- definition path.

No empty carrier is emitted. The frozen
[sanitized evidence export](sanitized-evidence-export.md) carrier set is not
widened. Any future inclusion requires a new carrier and version.

These rules refine the closed raw-local boundary in
[Local Monitor v1 Security](local-monitor-v1-security.md) and
[Security and Data Boundaries](../security-data-boundaries.md).

## AI boundary

Historical Skill content may reach Issue #163 only through an explicit,
immutable, bounded Issue #162 scope. Current-file content is never added
automatically.

Skill body/path and provider request/response payloads do not enter:

- normal logs;
- provider metadata;
- frozen machine APIs or SSE;
- repository-safe evidence;
- static artifacts; or
- committed test/live-validation artifacts.

Provider unavailability does not make Repository, Session, Skill metadata,
inspector, or deterministic Compare core unusable.

## Implementation decision gate

Production v2 parser/writer, `skill_invocation_snapshot:1` migration, raw-local
routes, and host registration remain `BLOCKED_DECISION` until all items below
are fixed in canonical specifications.

1. **Exact v2 outer envelope/event wire**
   - complete property inventory and order requirements;
   - required/nullable rules;
   - exact SDK `Id`, `ParentId`, `Timestamp`, `AgentId`, `Ephemeral`, and `Data`
     mapping;
   - exact Local `native_session_id`, Run, parent Event, trace ID, and span ID
     representation; and
   - exact header-to-persisted provenance mapping.
2. **Complete v2 status/error contract**
   - validation precedence;
   - every HTTP status/error token;
   - exact UTF-8 entity bytes; and
   - the exact insert-or-identical mismatch result.
3. **Repository-owned producer schema and registry seed**
   - checked-in canonical schema artifact bytes;
   - fingerprint algorithm and domain;
   - initial accepted 64-lowercase-hex fingerprint;
   - compatibility-registry seed; and
   - registry revision behavior for this tuple.
4. **Durable equality receipt**
   - operation/equality key;
   - fingerprint framing and byte domains;
   - stored/replayed result bytes and headers; and
   - conflict behavior.
5. **Payload and content storage bytes**
   - exact `payload_sha256` input bytes;
   - exact `payload_bytes` meaning;
   - exact `session_event_content` document/byte layout;
   - exact relation between that layout, body/path bytes, and payload digest;
     and
   - backup validation of those bytes.
6. **Deterministic classification**
   - total multi-fault state/reason precedence;
   - exact nullable-field matrix by state/reason;
   - exact `path_invalid` rule;
   - exact Skill-name normalization/validation;
   - exact mapping of registry/generation/snapshot facts to
     `projection_validity = current | invalid | stale`, exposed by or derived
     through the single #154 current-valid Skill read/diagnostic authority and
     never reconstructed through ad hoc snapshot SQL; and
   - exact historical-read error mapping for every non-available state.
7. **Remaining literal wire identities**
   - success-response `schema_version` tokens;
   - success-response nullable/present-field rules;
   - canonical `discovery_revision` root-list framing/hash;
   - exact accepted current-file POST media type and parameter handling;
   - exact success/error response media-type header bytes; and
   - exact `405` entity bytes, error identity, headers, and route/method
     precedence.
8. **Historical-to-discovery identity proof**
   - finalized normalized-name bytes and comparison;
   - exact historical producer-path-to-`DiscoverAsync` result-path comparison,
     including separator, casing, and Unicode treatment on each platform;
   - the configured-root identity and relative target representation passed to
     the handle walker;
   - whether the proof is transient or persisted and, if persisted, its exact
     component fields and backup treatment; and
   - exact Windows volume/file-ID and Unix device/inode proof representation at
     the service boundary.

The frozen-v1 correction remains ready and must not wait for these decisions.
No blocked value may be inferred from installed SDK behavior, runtime
reflection, v1 DTOs, JSON serializer defaults, encounter order, secret-filtered
content, or a compatibility fallback.

## Required deterministic validation after gate closure

The frozen-v1 tests below are executable now. Every v2, snapshot, raw-local
route, discovery, migration, and composition test below is conditional on full
closure of the implementation decision gate; a missing literal must not be
replaced by a test-local assumption.

### Frozen v1

- `skill.invoked` is unsupported and creates no v1 content row.
- `skill.started` and `skill.completed` remain supported.
- v1 header/schema/enums/1 MiB/batch/property/error/response bytes remain exact.
- identical supported and unsupported replays create no duplicate content,
  backfill, or counter increment.
- conflicting replay fails closed.

### V2 parser and transport after gate closure

- exact accepted request returns `204` only after atomic commit.
- wrong header/schema/adapter/surface/count/type/payload schema/provenance
  rejects with zero writes.
- unknown/duplicate outer property and invalid outer UTF-8 reject with zero
  writes.
- payload duplicate/unknown/type faults persist the exact Event and exact
  classified snapshot in the same transaction.
- body size `8,388,607`, `8,388,608`, and `8,388,609` pins the boundary.
- no v1 fallback route or writer is invoked.

### Claims, snapshot, and transaction

- exact SDK claim-validity tuple positive and negative cases.
- exact trace+span cross-arm merge positive and negative cases.
- trace-only, name, path, time, ordinal, and cardinality never merge.
- unmerged dual positives yield count `null` / `certification_pending`.
- OTel-only produces no snapshot row.
- stale/invalid projection cannot be revived by snapshot availability.
- failure injection before/after every transaction step proves zero partial
  rows.
- identical replay is a no-op and a mismatched replay rolls back every
  authority.

### State, digest, and bounds

- every persisted and derived state/reason.
- final deterministic multi-fault precedence.
- unpaired-surrogate handling without replacement repair.
- content at `0`, `1,048,576`, and `1,048,577` bytes.
- path at `1`, `4,096`, and `4,097` bytes.
- name/description/allowedTools/model/plugin bounds.
- CRLF/LF, NFC/NFD, BOM/no-BOM, casing, and trailing-newline digest
  distinctions.

### Retention and raw-local routes

- available, expired, pinned, delete-now, active-access-lease, and
  active-operation-lease cases.
- operation-lease loss/expiry publishes no body.
- exact metadata/content/current-file property order and bytes.
- invalid UUID, wrong Session, and missing snapshot return the same `404`.
- full status/error/no-store/CSRF/media/body-limit/`405` matrix.

### Current file

- same, changed, missing, and not-discovered outcomes.
- every source/root eligibility category.
- historical path never becomes a discovery root.
- traversal/root escape/symlink/junction/reparse/mount/device/UNC/ADS
  rejection.
- file identity/size/mtime races discard bytes.
- binary/oversized current file returns no partial body.

### Migration, backup, and composition

- component-absent older backup initializes empty v1.
- complete round trip includes available and expired metadata without copying
  body/path twice.
- partial/newer/invalid parent/content/claim/digest/uniqueness fails closed.
- OTel-only remains row-free.
- receiver-only host registers no v2 writer/current-file/raw-local route.
- sanitized export/import contains no snapshot namespace or empty carrier.
- frozen `/api/monitor/*`, `/api/session-workspace/*`, and SSE byte regression
  tests remain unchanged.

## Live-validation evidence boundary

Live SDK/VS Code validation is deferred until deterministic implementation and
automated validation complete on one exact integrated candidate SHA.
Repository-safe live evidence may retain only:

- whether exact trace-and-span linkage existed;
- discovery source/root category;
- `same`, `changed`, or `missing` outcome;
- state-transition names and bounded counts; and
- runtime/schema version.

It must not retain Skill body, definition path, Event/Session/trace/span
identifier, credential, raw payload, runtime database, or user-specific local
configuration value.
