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

After registration in the Local Monitor v1 Contract Index, the complete
`local_repository_catalog:1` implementation is `READY`.

This is one pre-release path. No old catalog schema, compatibility reader, dual
writer, alternate queue trigger, fallback parser, heuristic binding, migration
shim, or backfill obligation is introduced.

All exact-identity, no-heuristic, raw-local, sanitized-only, archive, Retention,
backup, frozen API/SSE, and Issue #152 boundaries from the base contract remain
unchanged.

## DC156-12 — automatic Repository creation and binding lifecycle

An admitted exact GitHub locator automatically resolves to one local Repository.
There is no durable `unbound locator` state.

For every distinct admitted `locator_sha256` in one reconciliation operation:

1. open the catalog write transaction using SQLite `BEGIN IMMEDIATE`;
2. look up the immutable locator owner by `(kind, locator_sha256)`;
3. when an owner exists, bind the exact observation context to that Repository
   and locator without changing Repository display name, revision, archive
   state, or locator head;
4. when no owner exists, create one Repository, one observed locator, and its
   current locator head in the same transaction;
5. recompute every affected Session assignment and append any required
   assignment revision/history;
6. complete the reconciliation queue item in that same transaction.

Automatic Repository, locator, history, observation, context, queue, and event
IDs are locally generated canonical lowercase UUIDv7 values.

A missing locator creates exactly one Repository at revision 1. Its display
name is the validated original-casing `display_repository` segment from the
first admitted context in this deterministic order:

```text
raw_record_id
resource_span_ordinal
scope_span_ordinal
span_ordinal
scope_kind: resource before span
attribute_ordinal
attribute_key by UTF-8 byte order
```

This display-name rule is Repository materialization only. It does not create a
Session observed-label candidate. Later observations never automatically rename
the Repository; rename remains an explicit user mutation.

`local_repository_history.action` is extended with:

```text
create_observed
```

Repository history records exactly one cause:

- `user_operation` plus the exact `Idempotency-Key`; or
- `source_context` plus exact `context_identity_sha256`.

`create_observed` requires `source_context`. Existing manual actions require
`user_operation`. No history row has both causes or neither cause.

Manual create and automatic admission serialize through the same immediate
write transaction and the unique locator fingerprint. Commit order is
authoritative:

- manual create commits first: automatic admission reuses that exact owner;
- automatic create commits first: manual create returns `locator_conflict`;
- manual create remains create-only and never becomes create-or-get.

If one operation admits multiple distinct missing locators, each exact locator
is created once. The affected Session then resolves to conflict; one candidate
does not suppress another. An archived exact Repository is reused without
restore or duplication.

Invalid, duplicate, shadowed, read-denied, or unsupported candidate input never
creates a Repository.

## DC156-13 — source/context identity, storage split, and byte framing

### Physical source occurrence versus exact Session context

The executable schema separates one physical OTLP attribute occurrence from
each exact Session context to which that occurrence applies.

`session_repository_observations` is the immutable physical source table. It
contains:

- `observation_id` UUIDv7 primary key;
- unique `source_identity_sha256`;
- positive opaque `raw_record_id` with no cross-component foreign key;
- exact `raw_payload_sha256`;
- exact resource/scope/span/attribute ordinals;
- `scope_kind = resource | span`;
- exact attribute key and value classification;
- nullable parsed locator fields;
- source surface/version and observed timestamp.

It does not contain `session_id`, `repository_id`, `locator_id`, assignment
authority, or an observed display label.

`session_repository_observation_contexts` is the immutable context table. It
contains:

- `context_id` UUIDv7 primary key;
- foreign key to the physical observation;
- unique `context_identity_sha256`;
- exact existing `session_event_id` and `session_id` foreign keys;
- exact Trace ID and Span ID;
- `admission_state`;
- nullable `repository_id` and `locator_id`;
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

There is no independent v1 label-only source. The base document's provisional
`label_only` state and nullable safe-display-label column are not part of the
executable v1 schema.

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
`Int32.MaxValue`. No signed decimal text, BOM, Unicode normalization, separator,
or trailing byte is added.

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

Same identity plus byte-identical persisted fields is an idempotent no-op. Same
identity with any different persisted field is `catalog_identity_conflict`,
rolls back the entire reconciliation operation, and becomes terminal queue
failure.

### Durable user-operation fingerprint

Every catalog mutation fingerprint uses:

```text
ASCII("local-repository-operation\0v1\0")
U32BE(9)
```

followed by these named fields in this order:

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
ASCII(field-name)
BYTE(0x00 null, 0x01 present)
when present: U32BE(value UTF-8 byte length) + UTF8(value)
```

Values are already validated semantic values. Method is uppercase ASCII; route
template is the exact contract literal; IDs are canonical lowercase UUID;
revision is unsigned decimal ASCII without leading zeroes; display name is NFC;
locator is canonical. Null is never encoded as an empty string.

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
- no row: set the queue item to `waiting_session` and commit no catalog domain
  rows for that raw record;
- more than one row, or any tuple mismatch: terminal
  `catalog_session_identity_conflict` and no catalog domain rows.

Every relevant context from one raw record must resolve before any observation,
Repository, locator, assignment revision, or history row from that raw record
is committed. Exact Session lookup and catalog writes use the same SQLite
connection and transaction snapshot.

Session assignment is never inferred from trace time, source label, Repository
locator, Repository name, path, workspace, CWD, prompt, or cardinality.

A resource-level occurrence is contextualized once for each exact matching span
Event. Repeated contexts for the same Session and locator are retained as exact
provenance but collapse to one automatic assignment candidate.

## DC156-15 — automatic assignment revision history

The effective automatic candidate set is the distinct sorted set of exact
Repository IDs from admitted context rows.

The assignment semantic state is:

```text
state: assigned | unassigned | explicitly_unassigned | conflict
authority: automatic | manual | none
repository_id: present only for assigned
conflicting_repository_ids: present only for conflict
```

A manual override remains authoritative while present. New automatic evidence
is retained but does not change effective assignment or assignment revision
until `resume_automatic` removes the override.

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

`session_repository_assignment_history.action` is extended with:

```text
automatic_reconcile
```

Every assignment history row stores:

- `cause_kind = user_operation | source_reconciliation`;
- exact user idempotency key or reconciliation fingerprint, never both;
- previous/new revision;
- previous/new assignment-state fingerprint;
- previous/new state and authority;
- previous/new nullable Repository ID;
- `occurred_at`.

A revision increments exactly once, and one history row is appended, only when
the effective assignment-state fingerprint changes. Candidate-set changes while
remaining in conflict therefore increment revision. Additional duplicate
evidence does not. Automatic evidence arriving under a manual override does not
increment revision.

Manual `assign`, `explicitly_unassign`, and `resume_automatic` retain their
existing action names and use the user-operation cause. `resume_automatic`
records the exact automatic result visible in the same transaction. A semantic
no-op creates no history row and no revision.

Restore validates revision 0 as the empty base, a contiguous one-step chain,
matching before/after fingerprints, and equality between the chain head and the
current override/resolver result.

## DC156-16 — single reconciliation cursor, fixed frontier, queue, and recovery

The component adds:

```text
local_repository_reconciliation_state
local_repository_reconciliation_queue
```

There is exactly one enqueue/discovery path.

`local_repository_reconciliation_state` has one row for projector
`local-repository-catalog-v1` and stores the last discovered `monitor_spans.id`.
The discovery worker scans projected spans in ascending ID order, groups rows by
exact `raw_record_id`, inserts one queue row per missing raw-record frontier,
and advances the cursor in the same transaction. Failure rolls back both queue
inserts and cursor advancement.

No ingest-time direct enqueue, route-triggered enqueue, scan-on-read, fallback
cursor, or second discovery worker exists.

Queue uniqueness is:

```text
(raw_record_id, projector_version)
```

Each queue row stores:

- UUIDv7 queue ID;
- positive `raw_record_id`;
- `input_evidence_kind = payload_sha256 | input_unavailable`;
- nullable payload digest, required only for `payload_sha256`;
- projector version exactly `local-repository-catalog:1`;
- reconciliation fingerprint;
- state;
- non-negative attempt count;
- nullable 64-character lowercase hexadecimal lease token;
- nullable lease expiry;
- nullable fixed terminal reason;
- created/updated timestamps.

At discovery, a present raw row supplies its exact payload digest. An already
missing/read-denied raw row creates a terminal `input_unavailable` queue row and
never receives a fabricated digest.

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

The closed `failed_terminal` reason set is:

```text
catalog_identity_conflict
catalog_session_identity_conflict
catalog_cardinality_exceeded
catalog_payload_digest_mismatch
catalog_parse_failure
catalog_schema_violation
```

A worker:

1. atomically changes an eligible `pending` or due `waiting_session` row to
   `leased`;
2. increments attempt count once;
3. writes a cryptographically random 32-byte lease token as 64 lowercase
   hexadecimal characters;
4. sets lease expiry to current trusted time plus exactly 30 seconds;
5. acquires the existing Retention operation lease for the fixed raw record;
6. verifies the raw payload digest;
7. parses only that persisted raw-record frontier;
8. resolves every exact Session context;
9. applies observations, automatic Repository creation, assignment
   revisions/history, and queue completion in one immediate transaction.

Lease renewal is permitted only before expiry, only with an exact current token,
and sets expiry to renewal time plus exactly 30 seconds. A token mismatch or
expired token cannot renew, publish, or complete work.

The worker never discovers another raw record at execution time. Missing or
read-denied raw input produces terminal `input_unavailable`, no Repository or
assignment claim, and no fabricated digest. A raw digest mismatch produces
terminal `catalog_payload_digest_mismatch`.

A missing exact Session Event produces `waiting_session`, no domain writes, no
lease token, and becomes eligible exactly five seconds after `updated_at`.
There is no attempt-count ceiling; attempt count is diagnostic only. If the raw
input later becomes unavailable, the next attempt ends `input_unavailable`.

On startup, every expired `leased` row becomes `pending` and clears lease token
and expiry before new work is leased. A non-expired lease is not stolen.
Duplicate execution replays the same source/context identities and cannot
duplicate domain rows.

A raw record that would exceed 128 distinct automatic Repository candidates for
any Session fails the whole queue item terminally with
`catalog_cardinality_exceeded` and publishes no partial catalog rows.

Observed display-label candidates are not collected in v1 and therefore add no
second cardinality counter.

Backup restores `leased` as `pending`; other terminal states remain terminal.

## DC156-17 — complete management wire bytes and bounded provenance

### Observed label candidates

V1 has no accepted source attribute for an observed Repository label. Locator
segments, Repository display names, paths, CWD, prompts, workspace labels, and
other metadata are not label evidence.

Therefore:

```json
"observed_label_candidates":[]
```

is the only v1 value. The array is always present, empty, and byte-identical. No
production table or context row stores an observed label candidate. A future
positive label source requires a new accepted contract; it is not inferred from
`display_repository`.

### Common success and error bytes

Every JSON success or error response has exactly:

```text
Content-Type: application/json; charset=utf-8
Cache-Control: no-store
```

No v1 response carries `Location` or `ETag`. Mutation receipts replay status,
these two contract headers, and exact entity bytes. JSON is compact UTF-8
without BOM or trailing LF.

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

Exact successes are:

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
locator remains allowed.

Locator GET returns the complete array ordered by:

```text
is_current descending
created_at ascending
locator_id by canonical UUID bytes
```

There is no locator pagination or truncation in v1.

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

`provenance` is JSON `null` for a manual locator. For an observed locator it is
one object describing the immutable context that created that locator, with
this property order:

```text
source_surface
source_application_version
trace_id
span_id
observed_at
source_content_availability
```

Every property is present. `source_application_version` may be null; the other
properties are non-null. `source_content_availability` is one of the DC156-18
values. Later observations do not enlarge this DTO; complete provenance remains
in catalog observation/context tables.

Session action presence rules are exact:

- `assign`: `repository_id` is a canonical UUID;
- `explicitly_unassign`: `repository_id` is null;
- `resume_automatic`: `repository_id` is null.

Assignment response property order is:

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

Every property is always present. Allowed state/authority combinations are:

```text
assigned / automatic / repository_id present
assigned / manual / repository_id present
unassigned / none / repository_id null
explicitly_unassigned / manual / repository_id null
conflict / automatic / repository_id null
```

`conflicting_repository_ids` is empty unless state is `conflict`; for conflict
it is the complete distinct set sorted by canonical UUID bytes, maximum 128.
`observed_label_candidates` is always the empty array.

When logical assignment revision is 0 and no persisted assignment row exists,
`updated_at` is JSON null. For revision greater than 0 it is a non-null canonical
UTC timestamp.

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

Error entity bytes remain exactly:

```json
{"error":"<fixed_code>"}
```

Under `--sanitized-only`, these human routes are absent and #168's empty
404/no-store behavior applies instead of `local_monitor_ui_unavailable`.

## DC156-18 — durable raw-reference shape and availability

Every physical source observation stores:

```text
raw_record_id INTEGER NOT NULL CHECK(raw_record_id > 0)
raw_payload_sha256 TEXT NOT NULL
```

`raw_record_id` is an opaque retained provenance value. It has no SQLite
foreign key to the physically deletable raw table and is never exposed by a
management DTO.

The payload digest is the exact 64-character lowercase SHA-256 captured from the
admitted raw bytes. Raw deletion, expiry, or a backup that omits the raw
component does not delete or null either field and does not invalidate already
admitted catalog metadata.

Availability is derived at read time:

- `available`: the exact raw row exists, its digest matches, and Retention
  permits the read;
- `expired`: Retention proves expiry or deletion for that exact reference;
- `not_retained`: the accepted source-content authority proves the bytes were
  never retained;
- `unknown`: no current raw or Retention fact proves another state, including a
  catalog-only restore.

A raw row with the same numeric ID and a different digest is corruption, never
`available`. Current-schema restore preserves the opaque ID/digest exactly.
Restore with a raw row requires digest equality; restore without raw is valid.
No restore or read searches another row, path, backup, or current source for
replacement bytes.

Raw identity/digest never enter URLs, logs, response entities, sanitized
export/import, or repository-safe artifacts.

## DC156-19 — one coherent Repository/Session/archive read snapshot

`ILocalRepositoryScopeSnapshotService` is #156-owned and is the only public read
composition entry used by #134. It opens one SQLite connection and one read
transaction for the complete operation.

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
   revisions, and conflict counts for those exact IDs;
3. #161 contributor reads direct Session and Repository archive states for the
   exact collected IDs;
4. #156 composes virtual scope membership and effective archive eligibility;
5. #134 serializes only the completed returned snapshot.

The first read occurs immediately after beginning the transaction, so all later
queries share one SQLite snapshot. Calls are sequential; no overlapping reader
or command uses the connection. Cancellation or contributor failure disposes
the read transaction and returns no partial snapshot.

#134 does not issue catalog SQL. #161 does not issue catalog SQL. #156 does not
reimplement Session or archive semantics. The service returns one coherent,
revision-bearing result for Repository cards, repository/all/unassigned scopes,
Session paging, conflict counts, and archive exclusion reasons.

A `persistence_busy` condition is mapped once at the service boundary. No
independently timed fallback snapshot, client-side merge, N+1 Repository read,
or scan-on-read is permitted.

## Released implementation and validation gate

#156 may now implement the complete component after the already integrated
DC156-01 parser/fingerprint slice.

Required validation includes all base DC156-01 through DC156-11 fixtures plus:

- automatic create/reuse/manual-create race/archived reuse;
- physical-source versus multi-context identity bytes;
- exact Session Event join, waiting, and conflict failure;
- automatic revision/history fingerprints and no-op behavior;
- single queue discovery path, fixed frontier, waiting/retry, lease renewal,
  lease expiry, restart, and input unavailable;
- exact success/error bytes, no `Location`/`ETag`, complete arrays, hard bounds,
  and provenance shape;
- invariant empty observed-label array and absence of label storage;
- raw delete/restore/digest contradiction behavior;
- one-transaction Session/catalog/archive composition at 10,000 Sessions;
- runtime backup/restore of every new table, queue state, cursor, history,
  opaque raw provenance, and idempotency receipts;
- unchanged frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE
  bytes;
- absence of all human routes and carriers under sanitized-only;
- no claim that Issue #152 is resolved.
