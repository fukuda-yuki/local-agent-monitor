# Local Repository Catalog and Session Assignment

## Status and authority

This specification pins the accepted Local Monitor v1 Repository catalog,
locator, provenance, Session-assignment, scope, mutation, and backup base from
design decisions DC156-01 through DC156-11. The exact current DC156-12 through
DC156-19 authority is the
[executable closure](local-repository-catalog-executable.md); where an earlier
gap statement below differs, that closure controls.

Issue ownership is split as follows:

- Issue #155 owns the Repository identity and assignment contract.
- Issue #156 owns the catalog, locator parser, provenance admission, assignment
  resolver, catalog mutations, internal scope snapshot, direct archive-fact
  validation/composition, and Repository target-existence authorities.
- Issue #133 specifies the composite Workspace read.
- Issue #134 is the sole implementation owner of
  `GET /api/local-monitor/v1/repositories`.
- Issue #160 owns archive meaning.
- Issue #161 owns archive storage, schema validation, archive queries,
  state-machine validation, mutation, public archive routes, and archive backup
  validation. It supplies complete direct Session/Repository archive facts to
  #156 and does not compose assignment-dependent eligibility.

The combined DC156-01 through DC156-19 contract is
`READY_FOR_IMPLEMENTATION`. The executable closure fixes the former production
gaps without authorizing inferred behavior, compatibility paths, fallback
readers, or permissive parsers.

Related canonical contracts:

- [Local Monitor v1 information architecture](local-monitor-v1-ia.md)
- [Local Monitor v1 security](local-monitor-v1-security.md)
- [Local Monitor v1 contract index](local-monitor-v1-contract-index.md)
- [Telemetry ingestion](../layers/telemetry-ingestion.md)
- [Raw store and normalization](../layers/raw-store-normalization.md)
- [Runtime backup and restore](runtime-backup-restore.md)
- [Security and data boundaries](../security-data-boundaries.md)

## Scope and non-goals

The catalog provides exact local Repository identity, immutable GitHub locator
history, exact provenance for Repository observations, deterministic Session
assignment, manual assignment overrides, and virtual Repository scopes.

The following are not Repository identity or assignment authorities:

- display names;
- observed labels;
- filesystem paths or current working directories;
- prompt or response content;
- timestamps or temporal proximity;
- cardinality;
- source names not explicitly approved by this specification;
- the legacy `RepositoryMetadataDiagnostics` display-label diagnostic;
- future source-capability fields that have not been accepted into this
  contract.

Repository IDs, locator IDs, observation IDs, Session IDs, Trace IDs, Span IDs,
and raw-record identities remain exact opaque identities. The catalog MUST NOT
join or repair them heuristically.

Issue #152's unknown attribute-key drift limitation remains unresolved.
Source-capability manifests from #125 and source attribution from #151 do not
authorize new Repository attribute keys and do not resolve #152.

The catalog MUST NOT:

- add a compatibility reader, dual reader/writer, permissive parser, or
  fallback path;
- silently create a binding from a name, path, timestamp, label, or source
  cohort;
- treat a conflict as an assignment;
- convert missing assignment facts to zero or to an inferred Repository;
- add archive, Retention, or deletion state to Repository catalog rows;
- physically delete or transfer a historical locator;
- expose raw database keys or raw-record IDs through the management API;
- map or serialize `GET /api/local-monitor/v1/repositories`.

## Locator grammar and fingerprint

### Input envelope

A v1 GitHub locator is between 1 and 512 UTF-8 bytes inclusive. The v1 grammar
is ASCII-only.

The parser MUST NOT trim or normalize its input. It MUST reject:

- leading or trailing whitespace;
- ASCII control characters, space, TAB, CR, or LF;
- non-ASCII input;
- percent encoding;
- backslash;
- NUL;
- a trailing slash;
- a query;
- a fragment;
- a port;
- a password;
- an additional path segment.

The parser accepts exactly these six forms:

```text
https://github.com/{owner}/{repository}
https://github.com/{owner}/{repository}.git
ssh://git@github.com/{owner}/{repository}
ssh://git@github.com/{owner}/{repository}.git
git@github.com:{owner}/{repository}
git@github.com:{owner}/{repository}.git
```

Scheme and host comparisons are ASCII case-insensitive. The SSH or SCP user is
exact lowercase `git`.

### Owner and Repository segments

The owner matches:

```text
[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?
```

It is 1 to 39 ASCII characters and cannot begin or end with `-`.

After the locator form is recognized, the parser first detects and removes
exactly one terminal lowercase `.git` transport suffix from the raw Repository
token. Uppercase or mixed-case variants such as `.GIT` are not transport
suffixes and remain part of the token. The parser then validates the resulting
suffix-free logical Repository segment against:

```text
[A-Za-z0-9._-]{1,100}
```

The suffix-free segment must contain 1 to 100 characters. The exact values `.`
and `..`, and an empty segment, are invalid. Consequently:

- a 100-character logical Repository followed by the `.git` transport suffix
  is accepted when the whole locator remains within the 512-byte input limit;
- `..git` strips to `.` and is rejected;
- `...git` strips to `..` and is rejected;
- only one terminal lowercase `.git` is removed.

### Canonical locator

The canonical locator is:

```text
github.com/{ascii-lower-owner}/{ascii-lower-repository}
```

The parser preserves the validated original owner casing for `display_owner`.
`display_repository` is the suffix-free validated logical Repository segment
in its original casing.

### Fingerprint

`locator_sha256` is lowercase 64-character hexadecimal SHA-256 over these
literal UTF-8 bytes:

```text
"local-repository-locator\0v1\0github_repository\0"
+ canonical_locator
```

There is no BOM, trailing LF, Unicode normalization, implicit length prefix, or
other byte transformation.

The locator parser and fingerprint remain an independently implementable pure
slice. The complete combined DC156-01 through DC156-19 contract is
`READY_FOR_IMPLEMENTATION`; implementing this slice alone creates no catalog
table or route.

## Storage component

The independent runtime component is:

```text
local_repository_catalog:1
```

### Common scalar rules

- Local IDs are canonical lowercase UUIDv7 values stored as `TEXT COLLATE
  BINARY`.
- Digests are lowercase 64-character hexadecimal values stored as `TEXT
  COLLATE BINARY`.
- Timestamps are UTC with exactly seven fractional digits and the `+00:00`
  suffix.
- Revisions are SQLite `INTEGER` values.
- Foreign keys use `ON UPDATE RESTRICT ON DELETE RESTRICT`.
- The component MUST NOT use `NOCASE` collation.
- Application validation and schema validation both enforce complete UUID,
  timestamp, digest, and Unicode-scalar rules.

### Logical tables

The accepted logical component contains the following tables. This section
pins their required data and invariants; the executable closure fixes the exact
production lifecycle and referential shape.

#### `local_repositories`

- `repository_id` primary key;
- `display_name`;
- `revision`, at least 1;
- `created_at`;
- `updated_at`.

#### `local_repository_locators`

- `locator_id` primary key;
- `repository_id` foreign key;
- `kind`, closed to `github_repository`;
- `canonical_locator`;
- `locator_sha256`;
- `source`, closed to `observed` or `manual`;
- `display_owner`;
- `display_repository`;
- `created_at`;
- unique `(kind, locator_sha256)`.

#### `local_repository_locator_heads`

- primary key `(repository_id, kind)`;
- current `locator_id`;
- `updated_at`.

The head identifies the current locator without changing or deleting historical
locator rows.

#### `session_repository_observations`

- `observation_id` primary key;
- unique `source_identity_sha256`;
- `context_identity_sha256`;
- exact existing `session_id` foreign key;
- nullable `repository_id`;
- nullable `locator_id`;
- `admission_state`;
- nullable safe display label;
- source surface;
- nullable source application version;
- an internal raw-record provenance reference;
- nullable exact Trace ID;
- nullable exact Span ID;
- resource, scope, span, and attribute ordinals;
- exact attribute key;
- `observed_at`.

`admission_state` is closed to:

```text
admitted
shadowed
label_only
invalid_locator
invalid_type
duplicate_key
```

Only `admitted` requires both `repository_id` and `locator_id`. Other states do
not make an assignment claim.

V1 has no accepted observed-label source. Responses always carry the exact
`"observed_label_candidates":[]`; no candidate is stored or inferred from a
locator segment, display name, filesystem path, Repository name, workspace
label, or adjacent metadata. DC156-17 in the executable closure owns that
invariant.

#### `session_repository_manual_overrides`

- `session_id` primary key;
- `state`, closed to `assigned` or `explicitly_unassigned`;
- nullable `repository_id`;
- `revision`, at least 1;
- `updated_at`.

`repository_id` is present only for `assigned`.

#### `session_repository_assignment_revisions`

- `session_id` primary key;
- `revision`, at least 0;
- `updated_at`.

This row survives `resume_automatic`, so deleting an override cannot erase the
Session assignment revision authority.

#### `session_repository_assignment_history`

This is append-only. It contains:

- `action`, currently closed to `assign`, `explicitly_unassign`, or
  `resume_automatic`;
- previous revision;
- new revision;
- nullable Repository ID;
- operation key;
- `occurred_at`.

#### `local_repository_history`

This is append-only. It contains:

- `action`, closed to `create`, `rename`, `add_locator`, or
  `replace_locator`;
- previous revision;
- new revision;
- nullable locator ID;
- operation key;
- `occurred_at`.

#### `local_repository_operation_receipts`

This is append-only. It contains:

- operation key primary key;
- request fingerprint;
- status code;
- content type;
- exact response entity bytes;
- fixed response headers covered by replay;
- `created_at`.

### Append-only enforcement

Every append-only table has SQLite triggers that reject `UPDATE` and `DELETE`.
The receipts are retained for the entire catalog lifetime and do not expire.

### Required indexes

The component includes indexes supporting these ordered keys:

```text
local_repository_locators
  (repository_id, created_at, locator_id)

session_repository_observations
  (session_id, observed_at, observation_id)
  (repository_id, session_id)
  (raw_record_id, source_identity_sha256)

session_repository_manual_overrides
  (repository_id, session_id)

session_repository_assignment_history
  (session_id, new_revision)

local_repository_history
  (repository_id, new_revision)

local_repository_operation_receipts
  (created_at, operation_key)
```

The `raw_record_id` spelling in the required observation index represents the
accepted need to index the raw provenance reference. DC156-18 in the executable
closure fixes its opaque nullable shape and referential lifetime.

## Observation admission

### Approved attributes

v1 reads exactly these two attribute keys:

```text
vcs.repository.url.full
copilot_chat.repo.remote_url
```

No other field is approved as a Repository locator. A field that a future
manifest might approve is ignored until a later current contract explicitly
adds it. Implementations MUST NOT infer Repository identity from #152 drift
candidates, source names, or display labels. These locator-key rules do not
choose an observed-label source, scope, precedence, deduplication rule, order,
or collection bound.

An approved value must be an OTel scalar string. Arrays, bytes, numbers, and
booleans produce `invalid_type`.

If an attribute container has the same exact key more than once, that key
produces `duplicate_key`, even when all duplicate values are equal.

### Span/resource precedence

Admission evaluates each span context as follows:

1. When at least one approved key is present in span attributes, only span
   candidates are evaluated and corresponding resource candidates are
   `shadowed`.
2. When no approved key is present in span attributes, resource candidates are
   evaluated.
3. An invalid or duplicate approved key at the higher-precedence scope does not
   fall back to a resource value.
4. Two approved keys in the same effective scope that parse to the same
   canonical locator contribute one assignment candidate while retaining one
   provenance observation per key.
5. Approved keys in the same effective scope that parse to different canonical
   locators are both admitted and make the Session resolver report conflict.
6. Different effective locators from different spans also make the Session
   resolver report conflict.

### Source identity

The accepted source-identity field sequence is:

```text
ASCII("local-repository-observation\0v1\0")
+ length-prefixed raw_record_id
+ U32BE(resource_span_ordinal)
+ U32BE(scope_span_ordinal)
+ U32BE(span_ordinal or 0xffffffff)
+ one-byte scope discriminator
+ length-prefixed exact attribute key
```

The resulting SHA-256 is stored as `source_identity_sha256`.

The exact length-prefix width, raw-record identity byte encoding, scope
discriminator, and physical-source/context split are fixed by DC156-13 in the
executable closure. Source identity and context identity are not
interchangeable.

If a source identity already exists and every persisted field is identical,
admission is a no-op. If any persisted field differs, the entire admission
transaction fails as a corruption conflict.

### Session binding and Retention

The catalog service is the only Session-binding owner. It admits evidence only
when it can bind to one exact, already-existing Session identity. It does not
create a Session and does not join by Repository locator, name, label, path,
source, or time.

The exact evidence, lookup, and database constraint that prove this join are
fixed by DC156-14 in the executable closure.

Before parsing a raw record, admission obtains a Retention operation lease for
that record. If the record is already read-denied or deleted, no new admission
occurs. Catalog-owned metadata from a completed admission remains after raw
content expires.

## Repository and assignment revisions

### Repository revision

- Create starts at revision 1.
- Rename increments the revision by exactly 1.
- Changing the current locator head increments the revision by exactly 1.
- A semantically identical operation is a no-op and leaves the revision
  unchanged.
- Create with an optional locator whose fingerprint is already owned produces
  `locator_conflict`.
- Create is not create-or-get and never silently returns an existing
  Repository.

### Session assignment revision

No row means logical assignment revision 0.

The revision increments by exactly 1 when effective assignment changes because
of:

- manual assign;
- explicit unassignment;
- resume automatic;
- exact newly admitted observation evidence.

`resume_automatic` deletes the manual override but preserves
`session_repository_assignment_revisions`. Calling it when the Session is
already automatic is a no-op and leaves the revision unchanged.

The append-only representation of automatic transitions and the three manual
actions is fixed by DC156-15 in the executable closure.

### Mutation evaluation order

Create is the only mutation without an expected revision. Every other mutation
requires `expected_revision`.

Evaluation order is fixed:

1. body, media type, UUID, property, and scalar validation;
2. idempotency receipt;
3. target existence;
4. expected revision;
5. unique locator or domain conflict;
6. commit.

## Immutable locator replacement

Adding or replacing a locator appends an immutable
`local_repository_locators` row and moves only the corresponding
`local_repository_locator_heads` row.

- No locator row is physically deleted.
- Old locators remain historical aliases.
- Every historical locator fingerprint remains permanently reserved to its
  original Repository.
- Setting the current locator to itself is a no-op.
- Moving the head back to a historical locator owned by the same Repository is
  a head change and increments the Repository revision.
- A locator fingerprint owned by another Repository produces
  `locator_conflict`.

This contract preserves observations made before a remote rename or
replacement.

## HTTP ownership and wire contract

### Route ownership

Issue #134 is the sole route owner for:

```text
GET /api/local-monitor/v1/repositories
```

That route composes catalog data with archive eligibility, Session and conflict
counts, last-observed time, and pagination. Issue #156 supplies internal
catalog and scope services only. It MUST NOT register, serialize, or shadow the
composite route.
Its exact success bytes, Repository ordering, pagination, and opaque cursor are
owned solely by
[`local-monitor-v1-repository-collection.md`](local-monitor-v1-repository-collection.md).

Issue #156 owns these routes under the exact DC156-17 wire contract:

```text
POST  /api/local-monitor/v1/repositories
PATCH /api/local-monitor/v1/repositories/{repositoryId}
GET   /api/local-monitor/v1/repositories/{repositoryId}/locators
POST  /api/local-monitor/v1/session-repository-actions
GET   /api/local-monitor/v1/sessions/{sessionId}/repository-assignment
```

### Common wire rules

- JSON is UTF-8 without BOM or indentation.
- Response property order is fixed.
- `schema_version` is always the first property.
- Duplicate and unknown JSON properties are rejected.
- Any UUID input that is not canonical lowercase UUID is indistinguishable from
  the corresponding missing Repository or Session target.
- Mutations require same-origin validation, CSRF validation, and a valid
  idempotency key.
- Every response, including success and every error, has
  `Cache-Control: no-store`.
- Error entity bytes are exactly:

```json
{"error":"<fixed_code>"}
```

The entity has no trailing newline.

### Repository create

Request property order:

```text
schema_version
display_name
github_locator
```

Example:

```json
{"schema_version":"local-repository-create.v1","display_name":"example","github_locator":null}
```

A successful create returns HTTP 201 with this property order:

```text
schema_version
repository_id
display_name
revision
created_at
updated_at
```

### Repository update

Request property order:

```text
schema_version
expected_revision
operation
display_name
github_locator
```

`operation` is closed to:

```text
rename
set_github_locator
```

The value unrelated to the selected operation is exactly JSON `null`.

### Locator read

Response property order:

```text
schema_version
repository_id
repository_revision
locators
```

Locator item property order:

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

This raw-local management route is the only route in this contract that returns
the canonical locator.

The exact `provenance` JSON shape, locator ordering, pagination/truncation
behavior, and cardinality limit are fixed by DC156-17 in the executable closure.

### Session action

Request property order:

```text
schema_version
session_id
expected_revision
action
repository_id
```

`action` is closed to:

```text
assign
explicitly_unassign
resume_automatic
```

### Assignment read and mutation response

Assignment response property order:

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

Conflict candidate membership is evidence, not assignment.
`observed_label_candidates` is always present and exactly empty under DC156-17;
there is no positive-item ordering, duplicate, or cardinality branch in v1.

### Body limits

- Repository create and update: 16 KiB each.
- Session action: 4 KiB.

The body limit applies before parsing and does not permit a fallback parser.

### Fixed error codes

The closed error set is:

```text
invalid_request
invalid_locator
repository_not_found
session_not_found
revision_conflict
locator_conflict
idempotency_conflict
csrf_rejected
request_too_large
unsupported_media_type
persistence_busy
local_monitor_ui_unavailable
```

DC156-17 in the executable closure fixes every success entity/status, fixed
header, error mapping, and bounded provenance/collection byte contract.

## Durable idempotency

A mutation accepts exactly this header value:

```text
Idempotency-Key: lrc1_<43 unpadded base64url characters>
```

The 43-character payload encodes 32 client-generated random bytes. The full
value is exactly 48 ASCII characters.

Receipts do not expire during the catalog lifetime and are included in runtime
backup.

The request fingerprint is a length-framed SHA-256 over validated semantic
values in this domain:

```text
"local-repository-operation\0v1\0"
method
route or operation
target ID
expected revision
NFC display name or canonical locator
Session action
Repository ID
```

The exact length-prefix width, null representation, integer representation, and
field discriminator bytes are fixed by DC156-13 in the executable closure and
MUST NOT be replaced with inferred framing.

For the same operation key and same fingerprint, retry exactly replays:

- status code;
- content type;
- each contract header, including `Location` or `ETag` when the completed
  route contract requires it;
- response entity bytes.

Transport-generated headers such as HTTP `Date` are not replay fields.

The same operation key with a different fingerprint produces a hard
`idempotency_conflict`. Receipt insertion and domain mutation occur in one
SQLite transaction.

## Assignment resolver and virtual scopes

### Effective assignment states

The resolver produces the following semantic outcomes:

- exactly one automatic candidate: assigned to that Repository;
- multiple distinct automatic candidates: conflict;
- no automatic candidate and no override: unassigned;
- manual `assigned`: assigned to the override Repository;
- manual `explicitly_unassigned`: explicitly unassigned.

Manual override is the assignment authority while present. Resuming automatic
removes that authority and re-evaluates exact admitted observations.

### Virtual scope membership

`all` initially includes every Session. #161 supplies direct archive facts;
#156 alone validates them and composes effective eligibility/reason. Requested
scope membership and effective eligibility remain independent facts.

`repository` includes only Sessions whose current resolver result is exactly
one Repository. A conflict Session belongs to none of its candidate Repository
scopes.

`unassigned` includes all three non-Repository outcomes:

```text
unassigned
explicitly_unassigned
conflict
```

The assignment state distinguishes those outcomes.

A Repository card's `assignment_conflict_count` counts conflict Sessions whose
exact candidate set contains that Repository. This count does not assert that
the Session belongs to the Repository.

### Single scope service

There is one service boundary:

```text
ILocalRepositoryScopeSnapshotService
```

- #156 implements the catalog and assignment core, validates direct archive
  facts, and composes effective eligibility/reason.
- #161 supplies complete direct Session/Repository archive facts to that same
  service.
- #134 consumes only the completed service.
- #134 and #161 MUST NOT issue direct SQL against catalog tables or create
  another Repository projection reader.

List membership, aggregates, and archive filtering are read from one SQLite
read transaction. DC156-19 in the executable closure fixes the complete
cross-Issue transaction, validation, and composition seam.

Archive meaning remains solely owned by #160/#161. This component has no
archive column.

#156 also owns the stateless `ILocalRepositoryTargetExistenceAuthority`.
DC156-19 in the executable closure alone fixes its synchronous caller-
transaction signature, bounds, one-query behavior, fail-closed validation, and
registration boundaries; #161 issues no catalog SQL.

## Raw expiry and provenance

After successful admission, these values are catalog-owned durable raw-local
metadata:

- canonical locator;
- locator fingerprint;
- display casing;
- safe observed label;
- source provenance reference.

Raw content expiry does not delete that metadata, and the catalog does not
reconstruct the raw body.

Provenance content availability is closed to:

```text
available
expired
not_retained
unknown
```

Management DTO provenance exposes only:

```text
source_surface
source_application_version
trace_id
span_id
observed_at
source_content_availability
```

It does not expose a raw database key or raw-record ID.

The durable internal raw reference allows restored catalog metadata to survive
when the source raw component is absent. DC156-18 in the executable closure
fixes its exact opaque shape and availability rules.

## Runtime backup and restore

The runtime backup vector places:

```text
local_repository_catalog:1
```

immediately after the Session component.

The current catalog v1 parent is exact Session v14. Runtime-backup read-only
legacy preflight accepts a present catalog v1 with an older Session parent only
for exact Session v13 and complete exact legacy shapes; catalog v1 paired with
Session v1..v12 is incompatible before mutation. For the exact v13 pair,
restore and safety-snapshot staging migrate Session first and only then invoke
the current catalog schema/row validator. A catalog validator never runs while
its parent remains v13.

The relevant restore order is:

```text
monitor
session
local_repository_catalog
local_archive
retention
skill_projection
skill_invocation_snapshot
local_workspace_projection
```

The Repository catalog backup namespace contains every table in
[Logical tables](#logical-tables).

Restore behavior:

- an older backup without this component initializes an empty v1 catalog;
- a present but partial component fails;
- a newer component version fails;
- an unknown enum or table namespace fails.

Restore validation includes:

- UUID, timestamp, and digest formats;
- locator fingerprint recomputation;
- unique locator ownership;
- locator-head and Repository consistency;
- observation Repository/locator consistency;
- manual override state consistency;
- a contiguous revision chain;
- receipt fingerprint and exact response-byte consistency;
- append-only history and receipt duplicate/missing detection.

The catalog can restore when source raw content is not in the backup. Such
provenance resolves to `unknown` or `expired` according to the retained
authorities; raw content is not reconstructed.

The catalog is excluded from sanitized evidence export and import. No empty
carrier is emitted.

Production backup registration follows the exact schema/history/raw-reference
and component-order contract fixed by DC156-12 through DC156-18 in the
executable closure.

## Display and provenance bounds

### Manual display name

A manual display name:

- is NFC normalized;
- contains 1 to 200 Unicode scalar values;
- encodes to at most 800 UTF-8 bytes;
- has no leading or trailing Unicode whitespace;
- is not entirely whitespace;
- contains no CR, LF, TAB, C0 control, C1 control, bidi formatting/override/
  isolate control, or unpaired surrogate;
- preserves internal whitespace and does not collapse it.

### Automatic display name

An automatic display name uses the validated Repository segment's original
casing. It follows the same safety rules and is at most 100 ASCII characters.
DC156-12 in the executable closure fixes when it is materialized.

### Observed label candidate

An observed label candidate:

- must be a string;
- is NFC normalized;
- contains at most 200 Unicode scalar values;
- encodes to at most 800 UTF-8 bytes.

An invalid label becomes `null` without making an otherwise exact locator
invalid. These accepted scalar and safety bounds do not authorize a source key,
scope, precedence, deduplication rule, deterministic order, or collection
cardinality.

A label MUST NOT be synthesized from locator/display components, filesystem
paths, Repository names, workspace labels, prompts, or adjacent metadata.

### Provenance scalars

`source_surface` is closed to:

```text
github-copilot-cli
github-copilot-vscode
```

`source_application_version` is nullable. When present it contains 1 to 64
visible ASCII characters and no whitespace, control character, or path
separator.

- Trace ID is exactly 32 lowercase hexadecimal characters.
- Span ID is exactly 16 lowercase hexadecimal characters.
- Each ordinal is between 0 and 2,147,483,647 inclusive.
- Each timestamp uses the exact UTC seven-fraction format defined in
  [Common scalar rules](#common-scalar-rules).

## Security, composition, and frozen boundaries

Repository management is raw-local human UI functionality. It is registered
only in raw-default Local Monitor composition.

In `--sanitized-only` or receiver-only composition:

- Repository catalog management routes are not registered;
- no metadata-only fallback is registered;
- catalog data is not added to sanitized evidence export/import.

All `/api/local-monitor/v1/*` responses remain `Cache-Control: no-store`,
including 404, 405, 409, 413, 415, CSRF rejection, and service-unavailable
responses.

This component does not change:

- frozen `/api/monitor/*` v1 shapes, property order, or bytes;
- frozen `/api/session-workspace/*` v1 shapes, property order, or bytes;
- frozen SSE shape, ordering, or bytes;
- existing Session ingest v1;
- existing raw OTLP receiver contracts.

No raw prompt, response, tool payload, credential, PII, absolute local path, or
runtime database may be committed as fixture or evidence. Tests use small
synthetic identifiers and locators.

## Historical decision inventory

The former production gaps for automatic creation, identity framing, exact
Session joining, automatic revision history, reconciliation, management wire,
raw-reference durability, and one-transaction composition are all closed by
DC156-12 through DC156-19 in the executable closure. That exact current
authority replaces this historical inventory; implementations do not recreate
or infer any omitted alternative.

## Minimum validation for the released contract

### Locator parser/fingerprint slice

The locator slice requires fixed fixtures for:

- all six accepted forms;
- ASCII case-insensitive scheme and host;
- exact lowercase SSH/SCP user;
- owner and Repository segment bounds;
- suffix-before-validation ordering;
- a 100-character logical Repository plus `.git`;
- `..git` and `...git` rejection after suffix removal;
- exact one-time lowercase `.git` removal and `.GIT` preservation;
- every rejected delimiter, encoding, whitespace, control, non-ASCII, port,
  password, query, fragment, trailing slash, and extra segment;
- exact canonical locator bytes;
- exact domain-separated SHA-256 bytes.

### Schema/admission slice

After the relevant decisions are accepted, validation includes:

- schema validator and application validator agreement;
- append-only trigger rejection;
- two Repositories with the same display name remaining different exact
  identities;
- approved-key-only admission;
- observed-label scalar, NFC, Unicode-scalar, UTF-8 byte, and invalid-to-null
  safety bounds without treating those bounds as source or wire authority;
- duplicate-key rejection even for equal values;
- span/resource precedence and no invalid fallback;
- same-locator provenance preservation;
- cross-key and cross-span conflict;
- source-identity identical replay and corruption rollback;
- exact Session join, including explicit non-admission for missing and ambiguous
  exact matches;
- Retention operation-lease denial and raw-expiry survival;
- automatic assignment revision and history consistency;
- reconciliation retry and restart recovery.

### Mutation/read slice

After exact wire bytes are accepted, validation includes:

- property order and duplicate/unknown-property rejection;
- body limits and media-type handling;
- same-origin and CSRF checks;
- canonical UUID not-found equivalence;
- fixed error bytes without LF;
- no-store on every status;
- expected-revision ordering;
- manual assign, explicit unassignment, and resume-automatic revision changes
  and semantic no-ops;
- immutable locator history and permanent fingerprint reservation;
- idempotency replay byte equality and hard conflict;
- scope conflict exclusion and candidate conflict counts;
- a 10,000-Session scope/query fixture;
- absence of all #156/#134 human routes in sanitized-only and receiver-only
  composition;
- unchanged frozen `/api/monitor/*` v1, `/api/session-workspace/*` v1, and SSE
  response bytes.

### Backup/restore slice

After the storage contract is executable, validation includes:

- exact component-vector position and restore order;
- absent-older initialization;
- partial/newer/unknown failure;
- locator fingerprint recomputation;
- revision-chain and append-only validation;
- exact receipt response-byte replay;
- restore without source raw;
- sanitized export/import namespace exclusion.
