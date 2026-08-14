# Local Repository Catalog Executable Closure

Status: **Accepted current authority — DC156-12 through DC156-19**

This specification provides the accepted closure for the eight former
production blockers summarized by the
[Local Repository Catalog and Session Assignment](local-repository-catalog.md)
historical decision inventory. The base contract remains authoritative for
DC156-01 through DC156-11. This file supersedes its former locator-parser-only
readiness statement and older Issue comments that describe the eight groups
below as unresolved.

The Local Monitor v1 Contract Index registers this authority; the complete
`local_repository_catalog:1` contract is `READY_FOR_IMPLEMENTATION`.

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
8. performs one atomic queue-plus-Retention heartbeat immediately before
   publication and adopts the renewed queue lease;
9. resolves every exact Session context and applies observations, automatic
   Repository creation, assignment revisions/history, and queue completion in
   one final immediate transaction.

Digest verification, persisted provenance capture, and payload-only parsing do
not hold the final writer transaction, so the periodic heartbeat can extend
long-running preparation. The periodic heartbeat owner serially performs the
explicit pre-publication heartbeat with its latest queue lease and exits only
after that handoff result. Finalization begins only after the worker adopts a
successful handoff lease, and the final transaction re-reads the exact
persisted provenance before using the prepared payload.
Session lookup, DB-dependent planning, publication, and queue completion remain
one atomic snapshot and must finish within the renewed 30-second queue fence.
If the handoff heartbeat or the final exact queue/Retention fence fails, no
Repository domain row is committed.

Lease renewal is permitted only before expiry, only with an exact current token,
and sets expiry to renewal time plus exactly 30 seconds. After its
`BEGIN IMMEDIATE` succeeds, the heartbeat acquires the exact admitted Retention
operation grant's publication scope, then owns one trusted clock sample before
any queue or Retention proof or update. Caller timestamps cannot advance or
backdate either queue or Retention authority. A token mismatch or expired token
cannot renew, publish, or complete work. In the same transaction, the heartbeat
renews every due admitted Retention operation
grant through the strict current revision/readability/source/receipt/coverage
predicate and to the separate two-minute duration defined by
[Raw Store And Normalization](../layers/raw-store-normalization.md). A live
operation grant outside the renewal deadline leaves Retention authority
unchanged without rereading those current proofs. Queue and due Retention
renewals commit all or none; neither renewal re-admits a different raw item.

The worker never discovers another raw record at execution time. Terminal
`input_unavailable`, no Repository or assignment claim, and no fabricated
digest apply when a new admission proves the fixed raw input authoritatively
unavailable. A periodic heartbeat that is busy leaves both leases unchanged;
the worker may continue only while its current queue lease remains live and
may retry renewal on the next interval. Current revision/readability/source/
receipt/coverage authority loss during a due Retention renewal cancels the
current attempt and returns the still-owned queue row to retry. A busy
pre-publication handoff likewise publishes nothing and returns the attempt to
retry. The same current-proof drift while the operation grant is not due does
not cancel the attempt: periodic and pre-publication heartbeats may extend only
the queue lease, and finalization uses the latest adopted queue lease with the
still-usable admitted grant. Neither failure case converts the
admitted grant into terminal input evidence or shortens it: the grant remains
consumable to its published expiry even if the original item expiry passes
while the grant is active. A raw digest mismatch produces terminal
`catalog_payload_digest_mismatch`.

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
composition entry used by #134. It opens one SQLite connection and one deferred
read transaction for the complete operation.

### Ownership and direct-fact carrier

- #160 owns archive meaning.
- #161 owns `local_archive:1` storage, schema validation, archive queries,
  state-machine validation, mutation, public archive routes, and archive backup
  validation.
- #156 owns catalog SQL, exact current assignment, virtual-scope composition,
  the complete Repository catalog read, direct-fact boundary validation,
  effective archive eligibility/reason composition, and Repository target
  existence.
- #134 consumes one completed `ILocalRepositoryScopeSnapshotService` result and
  issues no catalog or archive SQL.
- #161 issues no catalog SQL and opens no second connection inside either
  handoff.

The service accepts the #134-owned
`ILocalRepositorySessionSnapshotContributor` and this #161-owned direct-fact
contributor:

```csharp
internal interface ILocalArchiveFactSnapshotContributor
{
    ValueTask<LocalArchiveFactContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryArchiveInput input,
        CancellationToken cancellationToken);
}

internal enum LocalArchiveState
{
    Active,
    Archived,
}

internal sealed record LocalArchiveSessionFact(
    string SessionId,
    LocalArchiveState State,
    long Revision);

internal sealed record LocalArchiveRepositoryFact(
    string RepositoryId,
    LocalArchiveState State,
    long Revision);

internal sealed record LocalArchiveFactContribution(
    IReadOnlyList<LocalArchiveSessionFact> Sessions,
    IReadOnlyList<LocalArchiveRepositoryFact> Repositories);
```

Retain `LocalRepositoryArchiveInput(SessionIds, RepositoryIds)`. #156 freezes
both input collections before the archive phase:

- `SessionIds` is the complete, canonical, ordinally sorted exact set returned
  by the #134 contributor, maximum 10,000 under the existing bound;
- `RepositoryIds` is the complete, canonical, ordinally sorted full catalog,
  not merely assigned or candidate Repository IDs.

Immediately after the catalog phase, and before the requested-Repository check,
archive input construction, or #161 contributor call, #156 validates and
freezes the complete Repository row sequence. Repository IDs must be canonical
and strictly increasing under `StringComparer.Ordinal`; this proves uniqueness
as well as the SQL ordering contract. A noncanonical, duplicate, or
non-strictly ordered catalog row fails with the existing fixed
`InvalidOperationException("local_repository_catalog_snapshot_invalid")`, and
the archive contributor is not called. The resulting one frozen Repository
sequence is reused for archive input, direct-fact exact-set validation,
assignment composition, and Repository projection; no later code rebuilds a
weaker set.

#161 returns exactly one fact for every ID in both collections. A missing
`local_archive_current` row materializes as `Active, revision 0`. Output order
is not semantic; exact set identity is. Reversed and independently shuffled
Session and Repository fact collections are valid and produce the same snapshot
values and record ordering. #156 joins each copied fact by its exact canonical
ID; it never zips a fact collection positionally with an input ID collection.

### Direct-fact validity and fail-closed behavior

#156 copies each returned fact into its own frozen representation and validates
both collections independently before any composition. A contribution is valid
only when all of the following hold:

- the contribution, both lists, and every item are non-null;
- collection cardinality equals the corresponding exact input cardinality;
- every ID is a canonical lowercase UUIDv7 string;
- every ID belongs to the corresponding input set exactly once;
- there is no missing, extra, duplicate, or same-count substituted ID;
- `State` is a defined `LocalArchiveState` value;
- the state/revision pair is exactly one of:

```text
Active, 0
Active, positive even revision
Archived, positive odd revision
```

The parity check is a carrier-integrity invariant. It does not authorize #156
to query archive tables or validate event chains. It prevents a contributor
from returning `Active,1` and causing an actually archived target to fail open.
`Archived,0`, archived/even, active/positive-odd, negative revision, and an
undefined enum value are invalid.

Any invalid contribution throws one fixed internal
`InvalidOperationException("local_archive_fact_contribution_invalid")`; the
message contains no target or row data, and no partial snapshot is returned.
Cancellation is checked while freezing each collection and before composition.

The contributor-owned `IReadOnlyList` instances are hostile mutable carriers,
not trusted storage. For each collection, #156 captures `Count` exactly once,
requires the exact expected count, then reads each indexed item exactly once
into a new #156-owned fact record. From that point onward validation, lookup,
reason selection, and snapshot construction use only the owned copies; #156
does not reread `Count`, an indexer, an enumerator, or a contributor-owned item.
This closes time-of-check/time-of-use behavior from a list that mutates after
its first read or alternates values on repeated reads.

### Snapshot fields and exact composition

The internal returned records are:

```csharp
internal sealed record LocalRepositoryCatalogSnapshot(
    string RepositoryId,
    string DisplayName,
    long Revision,
    string? CurrentLocatorId,
    long AssignmentConflictCount,
    LocalArchiveState ArchiveState,
    long ArchiveRevision);

internal sealed record LocalRepositoryScopeSessionSnapshot(
    string SessionId,
    ILocalRepositorySessionSnapshotRow Session,
    long AssignmentRevision,
    LocalRepositoryScopeAssignmentState AssignmentState,
    LocalRepositoryScopeAssignmentAuthority AssignmentAuthority,
    string? RepositoryId,
    IReadOnlyList<string> CandidateRepositoryIds,
    bool IsAllScopeMember,
    bool IsUnassignedScopeMember,
    bool IsRequestedScopeMember,
    LocalArchiveState ArchiveState,
    long ArchiveRevision,
    bool IsEffectivelyEligible,
    string? ArchiveExclusionReason);
```

Repository rows receive their exact direct Repository fact. Session rows
receive their exact direct Session fact. Timestamps are not added to this
bounded seam; #161 direct/list routes own complete archive current facts.

For each Session, after exact assignment resolution, #156 computes:

```text
session_archived = session_fact.state == Archived

assigned_repository_archived =
    exact current RepositoryId is non-null
    AND repository_fact[RepositoryId].state == Archived

IsEffectivelyEligible =
    NOT session_archived AND NOT assigned_repository_archived

ArchiveExclusionReason =
    session_archived              ? "session_archived" :
    assigned_repository_archived ? "repository_archived" :
                                   null
```

Consequences are exact:

- both direct facts archived -> ineligible, reason `session_archived`;
- restoring only the Session -> still ineligible, reason
  `repository_archived` on a fresh snapshot;
- restoring only the Repository -> still ineligible, reason
  `session_archived` on a fresh snapshot;
- manual and automatic exact assignment use the same predicate;
- conflict, unassigned, and explicitly-unassigned Sessions have no exact
  current assigned Repository and ignore every candidate Repository archive
  fact;
- `IsRequestedScopeMember` is computed solely from the requested
  all/repository/unassigned scope;
- `IsEffectivelyEligible` is computed solely from archive facts and is never
  ANDed with `IsRequestedScopeMember`;
- consumers implement `active_only` by requiring both membership and effective
  eligibility; `include_archived` may retain membership while exposing the
  direct facts and reason. This preserves exact archived Repository routes.

### Exact Repository target existence authority

#156 owns one stateless internal authority:

```csharp
internal interface ILocalRepositoryTargetExistenceAuthority
{
    IReadOnlyList<string> ReadExisting(
        SqliteConnection openConnection,
        SqliteTransaction exactTransaction,
        IReadOnlyList<string> canonicalRepositoryIds,
        CancellationToken cancellationToken);
}
```

The core is deliberately synchronous. Runtime-backup source, staging, and
installed validators are synchronous, while this is one bounded local SQLite
statement. HTTP callers invoke the same synchronous authority inside their
existing transaction; no asynchronous wrapper is another authority, and there
is no asynchronous dual interface.

The concrete SQLite implementation is owned in the Repository persistence
namespace and follows this exact precedence:

1. reject null arguments using the normal BCL null guards;
2. require `openConnection.State == ConnectionState.Open`, a non-null active
   `exactTransaction.Connection`, and
   `ReferenceEquals(exactTransaction.Connection, openConnection)`; otherwise
   throw fixed internal
   `InvalidOperationException("local_repository_target_existence_transaction_invalid")`;
3. require count `1..200`; copy each item once to a private array; require every
   item to be canonical lowercase UUIDv7 and each adjacent item to be strictly
   increasing under `StringComparer.Ordinal`; otherwise throw fixed internal
   `ArgumentException("local_repository_target_ids_invalid", nameof(canonicalRepositoryIds))`;
4. check cancellation;
5. execute exactly one query on the supplied transaction;
6. validate and freeze the complete returned subset before return.

The SQL command is one dynamically parameterized equivalent of:

```sql
SELECT repository_id, typeof(repository_id)
FROM local_repositories
WHERE repository_id IN ($repository_id_000, ..., $repository_id_NNN)
ORDER BY repository_id COLLATE BINARY;
```

Every placeholder is bound as exact text. There are 1..200 parameters and no
string interpolation of values. The authority performs no schema probe, PRAGMA,
second query, retry, alternate lookup, or N+1 read. It does not open, begin,
commit, roll back, or dispose a connection or transaction.

An actual SQLite exclusive-lock contention is attempted exactly once. The
authority propagates the original `SqliteException` with primary error code
`5` (`SQLITE_BUSY`) or `6` (`SQLITE_LOCKED`) unchanged; it does not wrap, map,
sleep, retry, replace the command, or reopen anything. The caller's supplied
connection remains open, `exactTransaction.Connection` remains reference-equal
to that connection, and the authority neither commits, rolls back, nor disposes
the transaction.

The result is a newly frozen read-only list that is:

- canonical, distinct, and strictly ordinally increasing;
- an exact subset of the frozen input;
- composed only of `repository_id`, with no Repository fields.

A non-text, noncanonical, duplicate, out-of-input, or out-of-order returned row
throws fixed internal
`InvalidOperationException("local_repository_target_existence_result_invalid")`.
Cancellation or any failure returns no partial set. SQLite busy, corruption,
and other storage exceptions, plus the fixed transaction/result-integrity
exceptions above, propagate unchanged to the #161 service/backup boundary; the
#156 authority does not retry or map them. Cancellation remains cancellation.
#161 public callers map only SQLite busy/locked to fixed `persistence_busy`;
every other authority exception, including null/input/transaction/result
validation, corruption, and other storage failure, maps to fixed no-detail
`archive_store_unavailable`. An empty valid subset remains the semantic
`target_not_found` path, not corruption. Runtime-backup source/staging
validation maps every non-cancellation authority exception, or returned-set
inequality, to `restore_incompatible`. No framework error or target, row, or
SQLite detail is exposed.

#161 uses the authority as follows:

- `GET /api/local-monitor/v1/archive?target_kind=repository&target_id=...`
  supplies exactly one ID on its exact read transaction and requires returned-
  set equality before archive-current read; an empty subset is the D082-owned
  fixed `404 target_not_found` result;
- a Repository mutation supplies exactly one ID inside its existing
  `BEGIN IMMEDIATE` transaction and requires returned-set equality before any
  archive current/head read; an empty subset is the same fixed missing-target
  result before revision/state evaluation;
- runtime-backup validation keyset-pages distinct Repository archive target IDs
  in nonempty ordinal pages of at most 200 on the exact staging/read
  transaction, calls the authority once per page, and requires set equality;
- there is no overall target-count cap and no all-ID materialization;
- no call is made for an empty page.

The existing private dynamic `TargetExists` helper in catalog mutations is not
this authority and is not exposed: it is synchronous, one-target,
table/column-name driven, and lacks the bounded, frozen, and cancellation
contract.

### Connection, ordering, and registration boundaries

The service creates one internal `ILocalRepositoryReadTransaction` capability
containing the already-open connection and transaction. Contributors may
execute only their owning queries through that capability. They cannot open
another connection, begin, commit, roll back, or dispose the transaction, or
query catalog tables outside #156. Each contributor capability is revoked when
its owning phase ends; a retained capability cannot be used by a later phase.

The fixed call order inside the same snapshot is:

```text
#134 Session contributor
  -> #156 catalog reads
  -> #161 direct archive fact contributor
  -> #156 validation/composition
  -> return one complete snapshot
```

The first read occurs immediately after beginning the transaction, so all later
queries share one SQLite snapshot. Calls are sequential; no overlapping reader
or command uses the connection. Cancellation or contributor failure disposes
the read transaction and returns no partial snapshot. A `persistence_busy`
condition is mapped once at the service boundary. No independently timed
fallback snapshot, client-side merge, N+1 Repository read, scan-on-read, or
second snapshot is permitted.

#156 does not reimplement Session semantics, issue archive SQL, or validate
archive event chains; it validates the direct-fact carrier and alone composes
effective eligibility and its scalar reason. The service returns one coherent,
revision-bearing result for Repository cards, repository/all/unassigned scopes,
Session paging, conflict counts, and archive exclusion reasons.

Rename the lazy raw-default host dependency to
`ILocalArchiveFactSnapshotContributor`. Do not register a fake/default #134 or
#161 contributor. Register exactly one stateless
`ILocalRepositoryTargetExistenceAuthority` in the existing raw-default
Repository composition block; it is absent from sanitized-only human host
composition. The concrete implementation exposes one internal stateless
singleton instance. Runtime-backup initialization and validation consume that
same implementation explicitly outside the human-host service provider,
including sanitized receiver/runtime-backup posture; backup must not depend on
raw-default DI registration. #161 may consume the raw-default registration for
public archive reads and mutations.

### Explicit prohibitions

DC156-19 adds none of the following:

- `local_archive` tables, indexes, triggers, schema stamp, route, response, or
  backup component;
- archive timestamps on the #156 bounded snapshot seam;
- Repository archive columns in catalog tables;
- #161 catalog SQL, generic SQL capability, or separate catalog connection;
- precomposed #161 eligibility/reason;
- Repository-candidate archive filtering for non-assigned Sessions;
- dual reason, combined reason, null/error simultaneous-state behavior;
- cascade archive, ingest restore, raw deletion, Retention extension, pin, or
  delete-now behavior;
- compatibility reader, old/new carrier, permissive parser, fallback, retry,
  or scan-on-read;
- any public DTO/route/frozen API/SSE byte change;
- any sanitized-only human registration; or
- any Issue #152 completion claim.

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
