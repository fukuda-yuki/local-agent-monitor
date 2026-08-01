# Local Repository Catalog Executable Closure

Status: **Accepted current authority — DC156-12 through DC156-19**

This specification closes the eight production blockers retained by
[Local Repository Catalog and Session Assignment](local-repository-catalog.md).
The base contract remains authoritative for DC156-01 through DC156-11. This
file supersedes only:

- the base document's temporary statement that only the locator parser is
  executable;
- its `Required decisions before production implementation` section; and
- older Issue comments that describe the eight groups below as unresolved.

After this contract is registered in the Local Monitor v1 Contract Index, the
full `local_repository_catalog:1` implementation is `READY`. It remains a
pre-release single path: no old catalog schema, compatibility reader, dual
writer, fallback parser, heuristic binding, or backfill obligation is created.

All exact identity, no-heuristic, raw-local, sanitized-only, archive, Retention,
backup, frozen API/SSE, and Issue #152 boundaries from the base contract remain
unchanged.

## DC156-12 — automatic Repository creation and binding lifecycle

An admitted exact GitHub locator automatically resolves to one local Repository.
There is no durable `unbound locator` state.

For every distinct admitted `locator_sha256` in one reconciliation operation:

1. open the catalog write transaction with SQLite `BEGIN IMMEDIATE`;
2. look up the immutable locator owner by `(kind, locator_sha256)`;
3. when an owner exists, bind the observation context to that Repository and
   locator without changing Repository display name, revision, archive state,
   or locator head;
4. when no owner exists, create one Repository, one observed locator, and its
   current locator head in the same transaction;
5. recompute every affected Session assignment and append any required
   assignment revision/history before commit;
6. complete the reconciliation queue item in that same transaction.

Automatic IDs are locally generated canonical lowercase UUIDv7 values. A
missing locator creates exactly one Repository at revision 1. Its display name
is the validated original-casing `display_repository` from the first admitted
context in this deterministic order:

```text
raw_record_id
resource_span_ordinal
scope_span_ordinal
span_ordinal
scope_kind: resource before span
attribute_ordinal
attribute_key by UTF-8 byte order
```

Later observations never automatically rename the Repository. A user may rename
it through the accepted mutation route.

`local_repository_history.action` is extended with `create_observed`.
Repository history records exactly one cause:

- `user_operation` plus the exact `Idempotency-Key`; or
- `source_context` plus exact `context_identity_sha256`.

`create_observed` requires the second form. Existing manual actions require the
first. No row has both or neither.

Manual create and automatic admission serialize through the same immediate
write transaction and unique locator fingerprint. Commit order is authoritative:

- manual create first: automatic admission reuses the exact owner;
- automatic creation first: manual create returns `locator_conflict`;
- no create-or-get response or retry heuristic is added to the manual route.

If one operation admits multiple distinct missing locators, each exact locator
is created once. The affected Session then resolves to conflict; creation of
one candidate does not suppress another. An archived exact Repository is reused
without restore or duplication.

Invalid, duplicate, shadowed, or read-denied input never creates a Repository.

## DC156-13 — source/context identity, storage split, and byte framing

### Physical source occurrence versus Session context

The executable schema separates one physical OTLP attribute occurrence from
each exact Session context to which it applies.

`session_repository_observations` is the immutable physical source table. It
contains:

- `observation_id` UUIDv7 primary key;
- unique `source_identity_sha256`;
- positive opaque `raw_record_id` with no cross-component foreign key;
- `raw_payload_sha256` when the payload was available;
- exact resource/scope/span/attribute ordinals;
- `scope_kind = resource | span`;
- exact attribute key and value classification;
- nullable parsed locator fields;
- source surface/version and observed timestamp.

It does not contain `session_id`, `repository_id`, `locator_id`, or assignment
authority.

`session_repository_observation_contexts` is the immutable context table. It
contains:

- `context_id` UUIDv7 primary key;
- foreign key to the physical observation;
- unique `context_identity_sha256`;
- exact existing `session_event_id` and `session_id` foreign keys;
- exact Trace ID and Span ID;
- `admission_state`;
- nullable `repository_id` and `locator_id`;
- nullable `observed_label_candidate`;
- `observed_at`.

It is unique on `(observation_id, session_event_id)`. A resource attribute may
therefore have one physical observation and multiple exact context rows. Source
identity and context identity are never interchangeable.

The executable `admission_state` set is:

```text
admitted
shadowed
invalid_locator
invalid_type
duplicate_key
```

There is no independent v1 label-only source. `label_only` is removed from the
pre-release executable schema.

### Source identity framing

`source_identity_sha256` hashes this exact byte sequence:

```text
ASCII("local-repository-source-observation\0v1\0")
U64BE(raw_record_id)
U32BE(resource_span_ordinal)
U32BE(scope_span_ordinal or 0xffffffff for resource scope)
U32BE(span_ordinal or 0xffffffff for resource scope)
BYTE(0x01 for resource, 0x02 for span)
U32BE(attribute_ordinal)
U32BE(attribute_key UTF-8 byte length)
UTF8(exact attribute_key)
```

`raw_record_id` is 1 through `Int64.MaxValue`. Ordinals are 0 through
`Int32.MaxValue`. No signed decimal text, BOM, Unicode normalization, or trailing
byte is added.

### Context identity framing

`context_identity_sha256` hashes:

```text
ASCII("local-repository-observation-context\0v1\0")
32 raw bytes of source_identity_sha256
U32BE(36) + ASCII(canonical lowercase session_id)
U32BE(36) + ASCII(canonical lowercase session_event_id)
16 raw bytes decoded from lowercase trace_id
8 raw bytes decoded from lowercase span_id
```

The context is always span-bound, including when the source occurrence came
from resource attributes.

Same identity plus byte-identical persisted fields is a no-op. Same identity
with any different field is `catalog_identity_conflict`, rolls back the whole
operation, and becomes terminal reconciliation failure.

### Durable user-operation fingerprint

All catalog mutation fingerprints use:

```text
ASCII("local-repository-operation\0v1\0")
U32BE(9)
```

followed by these named fields in order:

```text
method
route_template
operation
target_id
expected_revision
display_name
canonical_locator
session_action
repository_id
```

Each field is framed as:

```text
U16BE(ASCII field-name length)
ASCII field-name
BYTE(0x00 null, 0x01 present)
when present: U32BE(value UTF-8 byte length) + UTF8(value)
```

Values are already validated semantic values. Method is uppercase ASCII;
route template is the exact contract literal; IDs are canonical lowercase UUID;
revision is unsigned decimal ASCII without leading zeroes; display name is NFC;
locator is canonical. No absent value is encoded as an empty string.

## DC156-14 — exact Session join

The catalog never creates or heuristically locates a Session. A context is
admitted only through the existing exact Session Event source-identity
authority.

For each raw OTLP span containing an effective locator occurrence, the exact
lookup tuple is:

```text
source_adapter = "otel-exact"
source_event_id = "<lowercase trace_id>/<lowercase span_id>"
type = "otel.span"
trace_id = the same lowercase trace_id
source_surface = the resolved github-copilot-cli or github-copilot-vscode surface
```

The lookup uses the Session component's unique
`(source_adapter, source_event_id)` authority and returns the immutable
`session_event_id` and `session_id`.

- exactly one matching row: create the context row;
- no row: make the queue item `waiting_session` and commit no catalog domain
  rows for that raw record;
- more than one row or a tuple mismatch: terminal
  `catalog_session_identity_conflict` and no catalog domain rows.

All relevant contexts from one raw record must resolve before any observation,
Repository, locator, assignment revision, or history row from that raw record
is committed. The exact Session lookup and catalog writes use the same SQLite
connection and transaction snapshot. Session assignment is never inferred from
Trace time, source label, Repository locator, Repository name, path, or
cardinality.

A resource-level occurrence is contextualized once for each exact matching span
Event. Repeated contexts for the same Session and locator are retained as exact
provenance but collapse to one assignment candidate.

## DC156-15 — automatic assignment revision history

The effective automatic candidate set is the distinct sorted set of exact
Repository IDs from admitted context rows. The assignment semantic state is:

```text
state: assigned | unassigned | explicitly_unassigned | conflict
authority: automatic | manual | none
repository_id: present only for assigned
conflicting_repository_ids: present only for conflict
```

A manual override remains authoritative while present. New automatic evidence
is retained, but it does not change effective assignment revision until
`resume_automatic` removes the override.

The assignment-state fingerprint is SHA-256 over:

```text
ASCII("local-repository-assignment-state\0v1\0")
U32BE length + UTF8(state)
U32BE length + UTF8(authority)
nullable repository_id using the DC156-13 null/present frame
U32BE(candidate count)
for every candidate Repository ID sorted by canonical UUID bytes:
  U32BE(36) + ASCII(repository_id)
```

Observed display labels are deliberately excluded from assignment revision.

`session_repository_assignment_history.action` is extended with
`automatic_reconcile`. Every history row stores:

- `cause_kind = user_operation | source_reconciliation`;
- exact user idempotency key or reconciliation fingerprint, never both;
- previous/new revision;
- previous/new assignment-state fingerprint;
- previous/new state and authority;
- previous/new nullable Repository ID;
- `occurred_at`.

A revision increments exactly once and one history row is appended only when
the effective assignment-state fingerprint changes. Candidate-set changes while
remaining conflict therefore increment revision; additional duplicate evidence
does not. New automatic evidence under a manual override does not increment.

Manual `assign`, `explicitly_unassign`, and `resume_automatic` use their existing
action names and the user operation cause. `resume_automatic` records the exact
automatic result visible in the same transaction. A semantic no-op creates no
history row and no revision.

Restore validates revision 0 as the empty base, a contiguous one-step chain,
matching before/after fingerprints, and equality between the chain head and the
current override/resolver result.

## DC156-16 — reconciliation frontier, cursor, queue, retry, and recovery

The component adds:

```text
local_repository_reconciliation_state
local_repository_reconciliation_queue
```

`local_repository_reconciliation_state` has one row for projector
`local-repository-catalog-v1` and stores the last discovered `monitor_spans.id`.
Discovery scans projected spans in ascending ID order, groups by exact
`raw_record_id`, inserts missing queue rows, and advances the cursor in one
transaction. Queue uniqueness is:

```text
(raw_record_id, projector_version)
```

New ingestion may enqueue the same fixed raw-record frontier in the monitor
projection transaction; the unique key makes cursor discovery and direct
enqueue identical no-ops rather than two paths.

Each queue row stores:

- UUIDv7 queue ID;
- positive `raw_record_id`;
- `input_evidence_kind = payload_sha256 | input_unavailable`;
- nullable payload digest, required only for `payload_sha256`;
- projector version `local-repository-catalog:1`;
- reconciliation fingerprint;
- state;
- attempt count;
- nullable lease token/expiry;
- nullable fixed terminal reason;
- created/updated timestamps.

The reconciliation fingerprint hashes:

```text
ASCII("local-repository-reconcile\0v1\0")
U64BE(raw_record_id)
U32BE length + UTF8(input_evidence_kind)
U32BE length + UTF8(exact digest or literal "unavailable")
U32BE length + UTF8(projector_version)
```

The closed queue state is:

```text
pending
waiting_session
leased
completed
input_unavailable
failed_terminal
```

A worker:

1. leases one row for 30 seconds with a random 32-byte token;
2. acquires the existing Retention operation lease for the fixed raw record;
3. verifies the raw payload digest when one is recorded;
4. parses only that persisted input frontier;
5. resolves all exact Session contexts;
6. applies source observations, automatic Repository creation, assignment
   revisions/history, and queue completion in one immediate transaction.

It never discovers additional raw records at execution time. Missing/read-denied
raw input produces terminal `input_unavailable`, no Repository/assignment claim,
and no fabricated digest. A missing exact Session Event produces
`waiting_session`, no domain writes, and becomes eligible again five seconds
after `updated_at`. An expired lease becomes `pending`; restart performs that
transition before leasing work. Duplicate execution replays the same source and
context identities and cannot duplicate domain rows.

A raw record that would exceed either hard Session bound below fails the whole
queue item terminally with `catalog_cardinality_exceeded` and publishes no
partial catalog rows:

- 128 distinct automatic Repository candidates per Session;
- 128 distinct observed label candidates per Session.

No scan-on-read or route-triggered reconciliation exists. Backup restores
`leased` as `pending`; other terminal states remain terminal.

## DC156-17 — complete management wire bytes and bounded display provenance

### Accepted observed label

V1 adds no additional source attribute key for display labels. The sole observed
label candidate is the validated original-casing `display_repository` segment
from an admitted exact locator context. It is display-only and never identity or
assignment evidence.

Candidates are NFC, deduplicated by exact UTF-8 bytes, sorted by UTF-8 byte
order, and returned completely. The hard bound is 128 distinct values per
Session as enforced by DC156-16. No value is synthesized from path, CWD,
prompt, workspace label, unrelated metadata, or an unapproved attribute.

### Common success and error bytes

Every JSON success or error response has exactly:

```text
Content-Type: application/json; charset=utf-8
Cache-Control: no-store
```

No v1 response carries `Location` or `ETag`. Mutation receipts replay status,
these two contract headers, and entity bytes. JSON is compact UTF-8 without BOM
or trailing LF.

Request schema versions are:

```text
local-repository-create.v1
local-repository-update.v1
local-session-repository-action.v1
```

Response schema versions are:

```text
local-repository.v1
local-repository-locators.v1
local-session-repository-assignment.v1
```

Exact successes:

| Route | Status | Entity |
|---|---:|---|
| `POST /api/local-monitor/v1/repositories` | 201 | `local-repository.v1` |
| `PATCH /api/local-monitor/v1/repositories/{repositoryId}` | 200 | `local-repository.v1` |
| `GET /api/local-monitor/v1/repositories/{repositoryId}/locators` | 200 | `local-repository-locators.v1` |
| `POST /api/local-monitor/v1/session-repository-actions` | 200 | `local-session-repository-assignment.v1` |
| `GET /api/local-monitor/v1/sessions/{sessionId}/repository-assignment` | 200 | `local-session-repository-assignment.v1` |

Repository response property order is:

```text
schema_version
repository_id
display_name
revision
created_at
updated_at
```

PATCH request values unrelated to the selected operation remain exact JSON
`null`.

A Repository may own at most 128 immutable locator rows. A new 129th locator
returns `locator_limit_reached`; moving the head to an existing historical
locator remains allowed. Locator GET returns the complete array ordered by:

```text
is_current descending
created_at ascending
locator_id by canonical UUID bytes
```

There is no truncation or pagination in v1.

Locator item property order remains:

```text
locator_id
kind
canonical_locator
display_owner
display_repository
source
is_current
created_at
provenance
```

`provenance` is exactly JSON `null` for a manual locator. For an observed
locator it is one object describing the immutable context that created the
locator, with property order:

```text
source_surface
source_application_version
trace_id
span_id
observed_at
source_content_availability
```

Later observations do not enlarge this DTO; full provenance stays in catalog
observation/context tables.

Session action presence rules are exact:

- `assign`: `repository_id` is a canonical UUID;
- `explicitly_unassign`: `repository_id` is null;
- `resume_automatic`: `repository_id` is null.

Assignment response property order remains:

```text
schema_version
session_id
assignment_revision
state
authority
repository_id
conflicting_repository_ids
observed_label_candidates
updated_at
```

Allowed state/authority combinations are:

```text
assigned / automatic / repository_id present
assigned / manual / repository_id present
unassigned / none / repository_id null
explicitly_unassigned / manual / repository_id null
conflict / automatic / repository_id null
```

`conflicting_repository_ids` is empty unless state is `conflict`; otherwise it
is the complete distinct set sorted by canonical UUID bytes, maximum 128.
`observed_label_candidates` is the complete distinct UTF-8-sorted set, maximum
128.

The complete error/status map is:

| HTTP | Error codes |
|---:|---|
| 400 | `invalid_request`, `invalid_locator` |
| 403 | `csrf_rejected` |
| 404 | `repository_not_found`, `session_not_found` |
| 405 | `method_not_allowed` |
| 409 | `revision_conflict`, `locator_conflict`, `locator_limit_reached`, `idempotency_conflict` |
| 413 | `request_too_large` |
| 415 | `unsupported_media_type` |
| 503 | `persistence_busy`, `local_monitor_ui_unavailable` |

Error entity bytes remain exactly `{"error":"<fixed_code>"}`. Under
`--sanitized-only`, these human routes are absent and #168's empty 404/no-store
behavior applies instead of `local_monitor_ui_unavailable`.

## DC156-18 — durable raw-reference shape and availability

Every physical source observation stores:

```text
raw_record_id INTEGER NOT NULL CHECK(raw_record_id > 0)
raw_payload_sha256 TEXT NOT NULL
```

The raw-record ID is an opaque retained provenance value. It has no SQLite
foreign key to the physically deletable raw table and is never exposed by a
management DTO. The payload digest is the exact lowercase SHA-256 captured at
admission.

Raw deletion, expiry, or a backup that omits the raw component does not delete
or null these fields and does not invalidate already admitted catalog metadata.
Availability is derived at read time:

- `available`: the exact raw row exists, its digest matches, and Retention
  permits the read;
- `expired`: the Retention authority proves expiry/deletion for that exact
  reference;
- `not_retained`: the accepted source-content authority proves bytes were never
  retained;
- `unknown`: no current raw or Retention fact proves another state, including a
  catalog-only restore.

A raw row with the same numeric ID and a different digest is corruption, never
`available`. Current-schema restore preserves the opaque ID/digest exactly.
Restore with a raw row requires digest equality; restore without raw is valid.
No restore or read searches another row, path, backup, or current source for
replacement bytes.

The source-content availability value in observed locator provenance is derived
by this single rule. Raw identity/digest never enter URLs, logs, response
entities, sanitized export/import, or repository-safe artifacts.

## DC156-19 — one coherent Repository/Session/archive read snapshot

`ILocalRepositoryScopeSnapshotService` remains #156-owned and is the only public
read composition entry used by #134. It opens one SQLite connection and one
read transaction for the complete operation.

The service accepts two internal contributors:

```text
ILocalRepositorySessionSnapshotContributor   (#134-owned)
ILocalArchiveEligibilitySnapshotContributor  (#161-owned)
```

It creates one internal `ILocalRepositoryReadTransaction` capability containing
the already-open connection and transaction. Contributors may execute only
their owning queries through that capability. They cannot open another
connection, begin/commit/rollback/dispose the transaction, or query catalog
tables outside #156.

The fixed call order inside the same snapshot is:

1. #134 contributor reads exact Session identities and bounded base rows;
2. #156 reads catalog assignments, candidate sets, Repository/locator heads,
   revisions, labels, and conflict counts for those exact IDs;
3. #161 contributor reads direct Session and Repository archive states for the
   exact collected IDs;
4. #156 composes virtual scope membership and effective archive eligibility;
5. #134 serializes only the completed returned snapshot.

The first read occurs immediately after beginning the transaction so all later
queries share one SQLite snapshot. Calls are sequential; no overlapping reader
or command uses the connection. Cancellation or any contributor failure rolls
back/disposes the read transaction and returns no partial snapshot.

#134 does not issue catalog SQL. #161 does not issue catalog SQL. #156 does not
reimplement Session or archive semantics. The service returns one coherent
revision-bearing result for Repository cards, repository/all/unassigned scopes,
Session paging, conflict counts, and archive exclusion reasons.

A `persistence_busy` condition is mapped once at the service boundary. No
independently timed fallback snapshot, client-side merge, N+1 Repository read,
or scan-on-read is permitted.

## Released implementation and validation gate

#156 may now implement the complete component after the already integrated
DC156-01 parser/fingerprint slice. Required validation includes all base
DC156-01 through DC156-11 fixtures plus:

- automatic create/reuse/manual-create race/archived reuse;
- physical-source versus multi-context identity bytes;
- exact Session Event join, waiting, and conflict failure;
- automatic revision/history fingerprints and no-op behavior;
- queue discovery, fixed frontier, waiting/retry, lease expiry, restart, and
  input unavailable;
- exact success/error bytes, no `Location`/`ETag`, full arrays, hard bounds, and
  provenance shape;
- raw delete/restore/digest contradiction behavior;
- one-transaction Session/catalog/archive composition at 10,000 Sessions;
- runtime backup/restore of every new table, queue state, cursor, history,
  opaque raw provenance, and idempotency receipts;
- unchanged frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE
  bytes;
- absence of all human routes and carriers under sanitized-only;
- no claim that Issue #152 is resolved.
