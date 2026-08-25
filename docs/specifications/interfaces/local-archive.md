# Local Archive v1 Interface

## 1. Status and authority

Status: `READY_FOR_IMPLEMENTATION`

This tracked specification is the singular executable owner for the D082 / Issue
#161 Local Archive v1 contract. It owns `local_archive:1` storage, mutation and
read semantics, the direct archive-fact contributor implementation, the exact
raw-default public archive wire, runtime-backup validation, and registration
boundaries.

D081 remains the owner of Repository catalog SQL, exact assignment, direct-fact
carrier validation, transaction-bound Repository existence, and the only
effective archive eligibility/reason composition. The canonical D081 contracts
are [Local Repository Catalog and Session Assignment](local-repository-catalog.md)
and [DC156-12–19 executable closure](local-repository-catalog-executable.md).
This document consumes those seams and does not redefine them.

## 2. Normative reading

Every schema token, literal, byte sequence, ordering, precedence rule, bound,
error mapping, transaction fence, component dependency, and registration
boundary below is normative. Implementations fail closed where this document
does not authorize adoption, repair, fallback, retry, aliasing, inference, or
partial output.

## 3. Scope

Sections 4–10 define the complete Local Archive v1 executable contract. Product
meaning remains owned by #160; this specification defines the #161 mechanism
that realizes that meaning without changing frozen v1 surfaces or sanitized
evidence boundaries.

## 4. Ownership boundary

- #160 owns product meaning: reversible metadata, no cascade, no ingest restore,
  no retention extension, and direct archived access. Archived targets are
  excluded from default Repository lists, default Session lists, Compare, and
  Repository-range AI; direct Session access and explicit single-Session AI
  remain available under their accepted rules.
- #124 owns Session 14 and its migration/backup compatibility.
- #156/D081 owns catalog SQL, exact current assignment, full Repository
  catalog, Repository existence, direct-fact carrier validation, and the only
  effective eligibility/reason composition.
- #161/D082 owns archive schema, archive current/history validation, exact
  Session existence queries, archive mutation/read/list application, the
  direct-fact contributor implementation, public archive wire, and archive
  runtime-backup validation.
- #134 consumes one completed #156 snapshot. It maps/serializes Workspace reads
  and performs no Session/catalog/archive merge or SQL.

#161 never returns precomposed eligibility from its contributor. #156 alone
computes:

```text
session_archived =
    direct Session state == Archived

assigned_repository_archived =
    exact current assigned RepositoryId is non-null
    AND that direct Repository state == Archived

IsEffectivelyEligible =
    NOT session_archived AND NOT assigned_repository_archived

ArchiveExclusionReason =
    session_archived             ? "session_archived" :
    assigned_repository_archived ? "repository_archived" :
                                   null
```

Candidate Repositories do not exclude conflict, unassigned, or explicitly
unassigned Sessions. Requested-scope membership and effective eligibility are
independent.

## 5. Exact `local_archive:1` schema

### 5.1 Canonical SQL

The following fence is the complete logical artifact
`local_archive.schema.v1.sql`. Names, literals, clauses, collations, checks,
foreign keys, index direction, trigger predicates, and abort tokens are exact.

```sql
CREATE TABLE local_archive_current(
  target_kind TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(target_kind)='text' AND target_kind IN ('session','repository')),
  target_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(target_id)='text'
      AND length(target_id)=36
      AND target_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND target_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(target_id,'-',''))=32),
  state TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(state)='text' AND state IN ('active','archived')),
  revision INTEGER NOT NULL
    CHECK(typeof(revision)='integer'
      AND revision BETWEEN 1 AND 9223372036854775807),
  archived_at TEXT COLLATE BINARY NULL
    CHECK(archived_at IS NULL OR (
      typeof(archived_at)='text'
      AND length(archived_at)=33
      AND archived_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
      AND substr(archived_at,1,4) BETWEEN '0001' AND '9999'
      AND substr(archived_at,6,2) BETWEEN '01' AND '12'
      AND substr(archived_at,12,2) BETWEEN '00' AND '23'
      AND substr(archived_at,15,2) BETWEEN '00' AND '59'
      AND substr(archived_at,18,2) BETWEEN '00' AND '59'
      AND CAST(substr(archived_at,9,2) AS INTEGER) BETWEEN 1 AND
        CASE CAST(substr(archived_at,6,2) AS INTEGER)
          WHEN 2 THEN CASE
            WHEN CAST(substr(archived_at,1,4) AS INTEGER)%4=0
             AND (CAST(substr(archived_at,1,4) AS INTEGER)%100<>0
               OR CAST(substr(archived_at,1,4) AS INTEGER)%400=0)
            THEN 29 ELSE 28 END
          WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30
          ELSE 31
        END)),
  updated_at TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(updated_at)='text'
      AND length(updated_at)=33
      AND updated_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
      AND substr(updated_at,1,4) BETWEEN '0001' AND '9999'
      AND substr(updated_at,6,2) BETWEEN '01' AND '12'
      AND substr(updated_at,12,2) BETWEEN '00' AND '23'
      AND substr(updated_at,15,2) BETWEEN '00' AND '59'
      AND substr(updated_at,18,2) BETWEEN '00' AND '59'
      AND CAST(substr(updated_at,9,2) AS INTEGER) BETWEEN 1 AND
        CASE CAST(substr(updated_at,6,2) AS INTEGER)
          WHEN 2 THEN CASE
            WHEN CAST(substr(updated_at,1,4) AS INTEGER)%4=0
             AND (CAST(substr(updated_at,1,4) AS INTEGER)%100<>0
               OR CAST(substr(updated_at,1,4) AS INTEGER)%400=0)
            THEN 29 ELSE 28 END
          WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30
          ELSE 31
        END),
  PRIMARY KEY(target_kind,target_id),
  CHECK((state='active' AND revision%2=0 AND archived_at IS NULL)
     OR (state='archived' AND revision%2=1 AND archived_at IS NOT NULL))
);

CREATE TABLE local_archive_events(
  event_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY
    CHECK(typeof(event_id)='text'
      AND length(event_id)=36
      AND event_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND event_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(event_id,'-',''))=32),
  target_kind TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(target_kind)='text' AND target_kind IN ('session','repository')),
  target_id TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(target_id)='text'
      AND length(target_id)=36
      AND target_id GLOB '????????-????-7???-[89ab]???-????????????'
      AND target_id NOT GLOB '*[^0-9a-f-]*'
      AND length(replace(target_id,'-',''))=32),
  action TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(action)='text' AND action IN ('archive','restore')),
  previous_revision INTEGER NOT NULL
    CHECK(typeof(previous_revision)='integer'
      AND previous_revision BETWEEN 0 AND 9223372036854775806),
  new_revision INTEGER NOT NULL
    CHECK(typeof(new_revision)='integer'
      AND new_revision BETWEEN 1 AND 9223372036854775807),
  occurred_at TEXT COLLATE BINARY NOT NULL
    CHECK(typeof(occurred_at)='text'
      AND length(occurred_at)=33
      AND occurred_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
      AND substr(occurred_at,1,4) BETWEEN '0001' AND '9999'
      AND substr(occurred_at,6,2) BETWEEN '01' AND '12'
      AND substr(occurred_at,12,2) BETWEEN '00' AND '23'
      AND substr(occurred_at,15,2) BETWEEN '00' AND '59'
      AND substr(occurred_at,18,2) BETWEEN '00' AND '59'
      AND CAST(substr(occurred_at,9,2) AS INTEGER) BETWEEN 1 AND
        CASE CAST(substr(occurred_at,6,2) AS INTEGER)
          WHEN 2 THEN CASE
            WHEN CAST(substr(occurred_at,1,4) AS INTEGER)%4=0
             AND (CAST(substr(occurred_at,1,4) AS INTEGER)%100<>0
               OR CAST(substr(occurred_at,1,4) AS INTEGER)%400=0)
            THEN 29 ELSE 28 END
          WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30
          ELSE 31
        END),
  CHECK(new_revision=previous_revision+1),
  CHECK((action='archive' AND new_revision%2=1)
     OR (action='restore' AND new_revision%2=0)),
  FOREIGN KEY(target_kind,target_id)
    REFERENCES local_archive_current(target_kind,target_id)
    ON UPDATE RESTRICT ON DELETE RESTRICT
);

CREATE INDEX IX_local_archive_current_archived_page
  ON local_archive_current(target_kind,state,archived_at DESC,target_id DESC);

CREATE UNIQUE INDEX IX_local_archive_events_target_revision
  ON local_archive_events(target_kind,target_id,new_revision);

CREATE TRIGGER local_archive_current_identity_update_rejected
BEFORE UPDATE OF target_kind,target_id ON local_archive_current
WHEN NEW.target_kind IS NOT OLD.target_kind
  OR NEW.target_id IS NOT OLD.target_id
BEGIN
  SELECT RAISE(ABORT,'local_archive_current_identity_immutable');
END;

CREATE TRIGGER local_archive_current_delete_rejected
BEFORE DELETE ON local_archive_current
BEGIN
  SELECT RAISE(ABORT,'local_archive_current_delete_rejected');
END;

CREATE TRIGGER local_archive_current_insert_replacement_rejected
BEFORE INSERT ON local_archive_current
WHEN EXISTS(
  SELECT 1 FROM local_archive_current
  WHERE target_kind=NEW.target_kind AND target_id=NEW.target_id)
BEGIN
  SELECT RAISE(ABORT,'local_archive_current_replacement_rejected');
END;

CREATE TRIGGER local_archive_events_update_rejected
BEFORE UPDATE ON local_archive_events
BEGIN
  SELECT RAISE(ABORT,'local_archive_events_append_only');
END;

CREATE TRIGGER local_archive_events_delete_rejected
BEFORE DELETE ON local_archive_events
BEGIN
  SELECT RAISE(ABORT,'local_archive_events_append_only');
END;

CREATE TRIGGER local_archive_events_insert_replacement_rejected
BEFORE INSERT ON local_archive_events
WHEN EXISTS(
  SELECT 1 FROM local_archive_events
  WHERE event_id=NEW.event_id
     OR (target_kind=NEW.target_kind
         AND target_id=NEW.target_id
         AND new_revision=NEW.new_revision))
BEGIN
  SELECT RAISE(ABORT,'local_archive_events_append_only');
END;
```

### 5.2 Exact artifact bytes and object inventory

Extraction is exact:

1. take the characters between the opening newline of the first `sql` fence
   above and the newline immediately following the final `END;`;
2. normalize CRLF and lone CR to LF;
3. encode strict UTF-8 without BOM;
4. retain exactly one final LF and perform no other normalization.

```text
artifact: local_archive.schema.v1.sql
bytes: 6994
sha256: D33265FFBF06A5087D2B83354B6FDD5CC35ECE74907FC988B6123EC2ECEEFB95
line_endings: LF
bom: absent
final_lf_count: 1
```

Owned objects are exactly:

```text
tables
  local_archive_current
  local_archive_events

indexes
  IX_local_archive_current_archived_page
  IX_local_archive_events_target_revision

triggers
  local_archive_current_identity_update_rejected
  local_archive_current_delete_rejected
  local_archive_current_insert_replacement_rejected
  local_archive_events_update_rejected
  local_archive_events_delete_rejected
  local_archive_events_insert_replacement_rejected
```

The shared `schema_version` row is not an owned third table. After exact
Session/catalog dependencies, all ten objects, and the empty-row invariant are
validated in the same non-deferred write transaction, stamp exactly:

```sql
INSERT INTO schema_version(component,version) VALUES('local_archive',1);
```

There is no seed row, stored active revision-0 row, receipt, operation ID,
payload BLOB, view, generated column, `STRICT`, `WITHOUT ROWID`,
partial/expression index, or compatibility namespace. A stamp/object mismatch,
case alias, changed normalized SQL, missing/extra reserved object, duplicate or
non-integer stamp, or version other than integer 1 fails closed; it is never
adopted, repaired, renamed, or dropped.

Runtime equality uses the existing `SqliteOwnedSchemaAuthority` exact compiled
object equality. The SHA-256 golden detects source drift but is not persisted in
SQLite and never substitutes for runtime object validation.

## 6. State, history, and mutation

Let `M = 9,223,372,036,854,775,807`.

### 6.1 Logical and stored facts

- No current row means exactly `active`, revision `0`, `archived_at=null`,
  `updated_at=null`.
- Stored current rows have revision `1..M`.
- Archived is an odd revision with non-null `archived_at`.
- Active stored state is a positive even revision with null `archived_at`.
- Every stored current has a complete event chain starting `archive 0->1`.
- Actions alternate and every event advances by exactly one.
- The unique head revision/action/state agrees with current.
- `current.updated_at == head.occurred_at`.
- Archived additionally has `archived_at == head.occurred_at`; active has null.
- Revision is the only history order authority. Timestamp and event UUID order
  may move backward and do not reorder history.

Every D082-owned archive API read or mutation validates the complete relevant
scalar/current/head facts required by sections 6 and 8 before returning its
route entity. Mutation additionally performs the complete history/head checks
in section 6.3; startup and runtime backup stream complete chains. This rule is
only for D082 archive API/store operations. It does not widen the D081
`ILocalArchiveFactSnapshotContributor`: section 7.1 reads and validates direct
current rows only, never event history or a head query. Corruption never becomes
active/revision zero, a revision conflict, or a partial archive API page.

### 6.2 Per-target classification

| Class | Exact condition | Write/result |
| --- | --- | --- |
| `apply` | `current.revision == expected`, state differs, and revision `< M` | append one event and advance current once |
| `no_op` | `current.revision == expected` and state already equals desired | no write; current fact succeeds |
| `semantic_retry` | `expected < M`, current revision is `expected+1`, state equals desired, and unique head has the same action and exact `expected -> current` revisions | no write; freshly serialize current fact |
| `revision_exhausted` | `current.revision == expected == M` and state differs | no write; unavailable 503 |
| `stale` | every other valid combination | no write; revision conflict 409 |

Only the adjacent head qualifies. Semantic retry has no TTL/count limit while
that head remains current. Any later restore/rearchive makes the old request
stale. No-op is not semantic retry. No response bytes are durably replayed.

### 6.3 Session batch precedence

Freeze request order for response and a separately sorted canonical ID order for
locking, proof, validation, and writes. For all 1..200 targets:

1. prove all exact parents; any missing target returns `404 target_not_found`
   before an archive current/event read;
2. validate all complete current/history/head facts; any contradiction returns
   `503 archive_store_unavailable`;
3. classify all targets; any `stale`, or any mixture containing both `apply`
   and `semantic_retry`, returns `409 revision_conflict`;
4. otherwise any `revision_exhausted` returns
   `503 archive_store_unavailable`;
5. `apply + no_op`, `semantic_retry + no_op`, all apply, all no-op, and all
   semantic retry succeed.

One captured UTC instant is used by every applied target. Each gets a distinct
canonical UUIDv7 event. No-op/retry facts retain timestamps. A successful DTO
is serialized in memory before commit, discarded if commit fails, and emitted
only after commit. A response transport loss after commit is recovered by a
fresh semantic retry.

The project-boundary seam is fixed and introduces no Persistence-to-LocalMonitor
dependency:

```csharp
internal delegate ReadOnlyMemory<byte> LocalArchiveSuccessEntityWriter(
    LocalArchiveMutationSuccess success);

internal enum LocalArchiveAction
{
    Archive,
    Restore,
}

internal enum LocalArchiveTargetKind
{
    Session,
    Repository,
}

internal sealed record LocalArchiveMutationTargetSuccess(
    string TargetId,
    LocalArchiveState State,
    long Revision,
    string? ArchivedAt,
    string? UpdatedAt);

internal sealed record LocalArchiveMutationSuccess(
    LocalArchiveAction Action,
    LocalArchiveTargetKind TargetKind,
    IReadOnlyList<LocalArchiveMutationTargetSuccess> Targets);

internal sealed record LocalArchiveMutationSucceeded(
    ReadOnlyMemory<byte> Entity);
```

The action/kind enums are closed persistence contracts. `Targets` is a newly
allocated store-owned array in original request order, never a caller carrier;
each target is the final semantic fact needed for exact bytes. `ArchivedAt` and
`UpdatedAt` are null or already-validated canonical `O`-format storage strings;
the exact state/revision/timestamp invariant in section 5 applies. Thus no
writer can require a post-commit database or clock read.

The Local Monitor route supplies the canonical JSON writer delegate. After all
writes and before commit, the store invokes it exactly once, copies the returned
memory once into a store-owned byte array, and then attempts commit. A writer
exception or empty entity rolls back and maps to `archive_store_unavailable`;
the bytes are discarded on writer/commit failure and the neutral succeeded
envelope is returned only after commit. This includes apply, no-op, and semantic
retry success without claiming that every success wrote. There is no response
receipt, request key, writer retry, post-commit reread, or reserialization.

Cancellation has an exact commit fence. The store checks cancellation
immediately before invoking the writer and again after copying the entity but
before calling SQLite commit. Cancellation before the writer means the writer is
not called; cancellation after the writer or during an unsuccessful commit
discards the owned entity. Any cancellation/exception before SQLite commit
returns successfully causes rollback and returns no entity. Once commit returns
successfully, the durable result wins: there is no cancellation recheck, and
the store returns the owned `LocalArchiveMutationSucceeded` envelope even if a
post-commit/pre-response checkpoint cancels the request token. A request abort
during HTTP emission is transport loss, not a domain rollback or cancellation
result; a fresh semantic retry recovers the committed fact.

### 6.4 D082 archive API transaction and failure mapping

POST parent proof, chain read, classification, writes, pre-commit serialization,
and commit use one connection and one `BEGIN IMMEDIATE` transaction. GET uses
one deferred read transaction.

- For D082 archive routes only, SQLite primary code 5 or 6 before successful
  commit is fixed
  `503 persistence_busy`; no retry/fallback.
- Schema/current/event/chain/head contradiction, unexpected archive
  guard/constraint failure, `SQLITE_CORRUPT`, `SQLITE_NOTADB`, revision
  exhaustion, and every other non-busy non-cancellation route-local store or
  parent-authority failure are fixed `503 archive_store_unavailable`.
- Cancellation observed before a successful commit remains cancellation,
  rolls back, and emits no entity. After successful commit, section 6.3's
  durable-success fence applies and product code does not reclassify it.
- Every failed mutation appends no event and changes no current row.
- No body contains a target, row, path, SQLite/framework message, inner
  exception, or request echo.

This section does not map contributor failures from section 7.1, does not select
Workspace public error bytes, and does not govern restore validation. D081/#156
owns composite-snapshot disposal/internal failure handling; D084/#134 will own
the eventual Workspace HTTP mapping. Section 9 separately maps backup/restore
incompatibility.

Archive never changes Session status/completeness/assignment, cascades between
Repository and Session, extends Retention, substitutes for pin/delete-now, or
changes from incoming Session/Event/Trace/assignment data.

## 7. D081 seams consumed exactly

### 7.1 Direct-fact contributor

#161 implements, but does not redefine:

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

The input contains the exact sorted #134 Session set (existing 10,000 bound)
and exact sorted full Repository catalog. D081 owns the interface, exact-set/
parity validation, and later composition; D082 owns this concrete reader. It
processes Session chunks first, then Repository chunks, using consecutive
ordinal chunks of 1..200 IDs. For every nonempty kind/chunk it executes exactly
one command through the supplied revocable Archive capability and its existing
transaction:

```sql
SELECT target_kind, typeof(target_kind),
       target_id, typeof(target_id),
       state, typeof(state),
       revision, typeof(revision)
FROM local_archive_current
WHERE target_kind = $target_kind
  AND target_id IN ($target_id_000, ..., $target_id_NNN)
ORDER BY target_id COLLATE BINARY;
```

`$target_kind` is exact text `session` or `repository`; every ID placeholder is
bound as exact text. Rows must have storage types text/text/text/integer, repeat
the requested exact kind, have a canonical chunk-member ID, be distinct and
strictly ordinally increasing, and carry a valid state/revision parity pair.
Any wrong-kind, non-text/noninteger, noncanonical, duplicate, out-of-chunk, or
out-of-order row fails the whole contribution with no partial result. Missing
rows materialize as `Active,0`. It returns exactly one fact per input ID and no
timestamp, membership, eligibility, or reason.

There is no query for an empty single kind, full-table/event/history query,
N+1 lookup, probe, retry, fallback, second connection/transaction, or 201-ID
command. Cancellation is checked before each command and while freezing rows;
every cancellation/failure returns no contribution and propagates unchanged
through the revocable capability into the #156-owned D081 phase runner. #156
owns transaction disposal, no-partial-snapshot behavior, and its existing
single internal busy classification. D082 selects no Workspace/public mapping
for this path; D084/#134 must define that mapping when it maps the completed
Workspace result. Section 6.4 does not apply.
Output order is nonsemantic. Reversed and independently shuffled fact lists are
valid because #156 joins only by exact canonical ID and never zips positions.

Final D081 does not select an empty-input SQL shape. D082 owns this concrete
implementation choice: when both input sets are empty, the contributor uses
exactly one bounded fixed archive-table read to satisfy the existing
capability's required-read lifecycle, then returns two empty lists:

```sql
SELECT target_kind, typeof(target_kind),
       target_id, typeof(target_id),
       state, typeof(state),
       revision, typeof(revision)
FROM local_archive_current
WHERE 0
ORDER BY target_kind COLLATE BINARY, target_id COLLATE BINARY;
```

It performs no zero-ID `IN ()`, full-table fallback, or second read.

#156 copies and validates both complete collections before composition:

- non-null carrier/lists/items;
- exact corresponding cardinality and set, canonical lowercase UUIDv7,
  uniqueness, no missing/extra/substitution;
- defined state;
- exactly `Active,0`, `Active,positive even`, or `Archived,positive odd`.

Undefined state, negative revision, `Archived,0`, archived/even, or
active/positive-odd throws only
`InvalidOperationException("local_archive_fact_contribution_invalid")`.

Both returned `IReadOnlyList` instances are hostile carriers. For each list,
#156 reads `Count` exactly once, reads every indexed item exactly once into a
new #156-owned record, and thereafter never rereads the carrier, an item,
`Count`, an indexer, or an enumerator. Validation and composition use only those
owned copies, so mutation or alternating values after the first pass cannot
change the result.

### 7.2 Synchronous Repository existence

The only Repository parent authority is:

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

Its D081 precedence remains exact:

1. normal BCL null guards;
2. require an open connection and active owning transaction with
   `ReferenceEquals(transaction.Connection, connection)`, otherwise
   `InvalidOperationException("local_repository_target_existence_transaction_invalid")`;
3. freeze count `1..200`, canonical lowercase UUIDv7, strictly ordinally
   increasing input, otherwise
   `ArgumentException("local_repository_target_ids_invalid", nameof(canonicalRepositoryIds))`;
4. cancellation;
5. exactly one parameterized query on the supplied transaction:

```sql
SELECT repository_id, typeof(repository_id)
FROM local_repositories
WHERE repository_id IN ($repository_id_000, ..., $repository_id_NNN)
ORDER BY repository_id COLLATE BINARY;
```

6. freeze a canonical/distinct/strictly ordered exact subset, otherwise
   `InvalidOperationException("local_repository_target_existence_result_invalid")`.

It opens/commits/disposes/retries/probes nothing. An actual exclusive-lock
contention is attempted exactly once; the original `SqliteException` with
primary code 5 or 6 propagates unchanged. It is never wrapped, mapped, slept,
retried, replaced, or reopened, and the caller connection remains open while
the supplied transaction remains uncommitted and reference-equal to it. There
is no async sibling or wrapper authority.

D081's exact consumers are Repository direct GET, Repository mutation, and
runtime-backup validation. D082 additionally uses the same authority for
archived-list parent proof; that is a D082 consumer extension, not a change to
D081 ownership:

- direct Repository GET supplies one ID in the same deferred read transaction;
  returned-set inequality is `404 target_not_found` before archive read;
- Repository POST supplies one ID in the same immediate mutation transaction;
  returned-set inequality is the same 404 before current/head read;
- a Repository archived-list page sorts all at-most-201 IDs, calls only
  nonempty chunks of at most 200 on the same read transaction, validates the
  lookahead too, and requires the union of returned chunks to equal the complete
  page ID set; any empty/partial inequality is
  `503 archive_store_unavailable`, with no items or cursor emitted;
- runtime backup keyset-pages every distinct Repository current target in
  nonempty ordinal pages of at most 200 with no total cap or all-ID
  materialization; any inequality is `restore_incompatible`.

The #161-owned Session parent authority is one reusable stateless concrete
reader, not a second #156 interface:

```csharp
internal IReadOnlyList<string> ReadExisting(
    SqliteConnection openConnection,
    SqliteTransaction exactTransaction,
    IReadOnlyList<string> canonicalSessionIds,
    CancellationToken cancellationToken);
```

After normal BCL null guards, it requires an open connection and active owning
transaction with reference-equal connection; otherwise it throws
`InvalidOperationException("local_archive_session_target_existence_transaction_invalid")`.
It freezes `Count` once and each item once, requiring 1..200 canonical,
distinct, strictly ordinally increasing IDs; otherwise it throws
`ArgumentException("local_archive_session_target_ids_invalid", nameof(canonicalSessionIds))`.
It then checks cancellation and executes exactly one parameterized query on the
supplied transaction:

```sql
SELECT session_id, typeof(session_id)
FROM sessions
WHERE session_id IN ($session_id_000, ..., $session_id_NNN)
ORDER BY session_id COLLATE BINARY;
```

Every placeholder is bound as exact text. The result is frozen, canonical,
distinct, strictly ordinally increasing, and an exact subset of input; non-text,
noncanonical, duplicate, out-of-input, or out-of-order rows throw only
`InvalidOperationException("local_archive_session_target_existence_result_invalid")`.
It
performs no schema probe, retry, N+1 lookup, second query/connection, or
connection/transaction lifecycle action. Direct GET and Session mutation/batch
require complete equality or return 404 before archive reads. A Session list
page, including its lookahead, requires union equality or returns 503 with no
partial entity/cursor. Backup pages require equality or are
`restore_incompatible`, with chunks at most 200 and no total cap. An empty page
calls neither parent authority. Silently sending a 201-ID page as one call is
forbidden.

## 8. Exact public wire

### 8.1 Paths, posture, methods, and headers

Exact literal paths only:

```text
GET  /api/local-monitor/v1/archive
POST /api/local-monitor/v1/archive-actions
GET  /api/local-monitor/v1/archived-items
```

Case variants, trailing/double slash, extra segment, or another literal path are
not aliases and fall through the existing unmatched no-store 404.

GET routes accept GET only. POST accepts POST only. Unsupported non-HEAD methods,
including OPTIONS, return 405 and the JSON `method_not_allowed` entity. HEAD
also returns 405 but has **zero entity bytes** as required by HTTP, while
retaining the exact owned headers. `Allow` is exactly `GET` or `POST` for the
matched path; it is absent from non-405 archive responses.

Product-owned response headers are:

```text
Content-Type: application/json; charset=utf-8
Cache-Control: no-store
```

`Location`, `ETag`, `Set-Cookie`, and every `Access-Control-Allow-*` header are
absent. OPTIONS is an ordinary 405, never a CORS preflight. Product code sets no
success-specific cache validator or redirect. Transport-managed `Date`,
`Server`, non-HEAD `Content-Length`, and connection framing are not otherwise
part of the byte contract. Every non-HEAD entity is compact strict UTF-8, no
BOM, no trailing LF.

D080's HEAD representation rule is exact. On a valid-Host matched archive path,
HEAD is 405 with the route's `Allow`, JSON Content-Type, no-store,
`Content-Length: 30` for `{"error":"method_not_allowed"}`, and zero entity
bytes. Invalid-Host HEAD is 400 with no `Allow`, the same Content-Type/no-store,
`Content-Length: 24` for `{"error":"invalid_host"}`, and zero entity bytes.

With raw-default, loopback/Host validation is the global first request
decision, before exact path or method dispatch. A malformed/non-loopback Host
therefore receives exact `400 invalid_host`, including for HEAD as above. After
valid Host, exact machine-path dispatch precedes the later human fallback. A
matched archive route then uses the existing exact
`MonitorHost.IsCrossSiteRequest` decision; cross-site/mismatched-origin requests
map to `403 csrf_rejected`. POST also requires exactly one effective
`x-monitor-csrf: local-monitor` value.

Under `--sanitized-only`, archive routes, application, contributor, and human
DI registrations are absent. With a valid loopback Host every archive path and
method receives the existing empty no-store 404 before archive route/adaptor
code. No archive JSON error, explanation page, or metadata fallback exists.
Runtime-backup component validation remains an independent non-human authority.

### 8.2 Request admission precedence

For raw-default:

1. global loopback/Host validation and no-store;
2. exact machine-path classification; unmatched paths fall through;
3. method and `Allow`;
4. same-origin; POST CSRF;
5. complete strict query grammar and every `invalid_request` decision, followed
   only for an admitted `after` lexeme by cursor decoding/`invalid_cursor`;
6. POST media/content-encoding, declared length, and streamed 65,536-byte limit;
7. strict UTF-8 without BOM, JSON depth 8, exact closed fields, schema/action/
   kind/ID/revision/distinctness/cardinality;
8. one transaction and complete target existence;
9. D082 route-specific validation: GET/list current/head facts and POST complete
   current/history/head facts;
10. classification and batch precedence;
11. all writes and pre-commit canonical serialization;
12. commit;
13. entity emission.

A storage busy failure at the point encountered wins because the later semantic
stage was never proven. There is no retry.

### 8.3 GET queries and list order

`GET /archive` requires exactly one of each:

```text
target_kind=session|repository
target_id=<canonical lowercase UUIDv7>
```

`GET /archived-items` requires `target_kind` exactly once. `after` and `limit`
are optional and each may occur at most once. Field order is nonsemantic.
Unknown or duplicate query fields, an empty/malformed `target_kind`, an
empty/malformed `target_id` or `limit`, and percent-encoded aliases of canonical
field names/ordinary ASCII values are `400 invalid_request`. A duplicate
`after` field is also `invalid_request`.

One raw `after` value is admitted to cursor decoding only when it is nonempty
ASCII matching exactly `[A-Za-z0-9_-]+`. Raw `%` escapes (including aliases such
as `%2D`), `+`, `/`, `=`, whitespace, quotes, non-ASCII, or any other character
fail lexical query admission as `400 invalid_request`; the cursor decoder is not
called. The parser completes all query-level validation before cursor decoding,
so an invalid/missing kind or invalid limit wins `invalid_request` even when an
admitted `after` lexeme would later decode as an invalid cursor. Only after kind,
limit, field multiplicity, and the raw `after` lexeme are valid does a decoded
length/frame/timestamp/UUID/canonical-reencoding/cross-kind failure become
`400 invalid_cursor` under section 8.6.

Absent limit is 50. Present limit is raw canonical decimal
`[1-9][0-9]{0,2}` in `1..200`; signs, exponent, whitespace, leading zero, 0,
or 201 are invalid.

List contains current archived rows only, ordered:

```text
archived_at DESC, target_id DESC
```

The query requests `limit+1`. When more rows exist, emit the first `limit` and
set `next_cursor` to the cursor of the **last emitted item**, not the lookahead
item. Resume predicate for the same kind is:

```text
archived_at < cursor.archived_at
OR (archived_at = cursor.archived_at AND target_id < cursor.target_id)
```

An empty page is HTTP 200 with `items:[]` and `next_cursor:null`.

### 8.4 POST media, body, and DTO

POST has no semantic query fields; a bare empty query delimiter is equivalent
to no fields. Accept exactly one Content-Type:

```text
application/json
application/json; charset=utf-8
```

Media and parameter names compare ASCII case-insensitively. Quoted charset,
duplicate/unknown parameter, another charset, missing/duplicate Content-Type,
or any Content-Encoding is `415 unsupported_media_type`.

Maximum body is 65,536 bytes inclusive, enforced both against declared length
and streaming input. 65,537 is 413. JSON is strict UTF-8 without BOM, no comments
or trailing commas, maximum parser depth 8. Root/item property order is
nonsemantic, but every required property occurs exactly once and unknown or
duplicate properties fail.

Exact shape:

```json
{"schema_version":"local-archive-action.v1","action":"archive","target_kind":"session","targets":[{"target_id":"01890f65-4c31-7f42-8a7d-111111111111","expected_revision":0}]}
```

- `schema_version` exact;
- action exact `archive|restore`;
- kind exact `session|repository`;
- Session target count 1..200; Repository exactly 1;
- target IDs canonical lowercase UUIDv7 and distinct;
- expected revision raw JSON token `0|[1-9][0-9]*` in `0..M`; string,
  negative, `-0`, fraction, exponent, or overflow is invalid.

`Idempotency-Key` is neither required nor interpreted, rejected, persisted, or
echoed. It cannot turn semantic retry into response replay.

### 8.5 Success bytes

Response property order is fixed as shown.

Active revision-zero direct GET:

```json
{"schema_version":"local-archive.response.v1","target_kind":"session","target_id":"01890f65-4c31-7f42-8a7d-111111111111","state":"active","revision":0,"archived_at":null,"updated_at":null}
```

Archived direct GET:

```json
{"schema_version":"local-archive.response.v1","target_kind":"session","target_id":"01890f65-4c31-7f42-8a7d-111111111111","state":"archived","revision":1,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}
```

Apply, no-op, and semantic retry are HTTP 200. Target objects repeat in original
request order:

```json
{"schema_version":"local-archive-action.response.v1","action":"archive","target_kind":"session","targets":[{"target_id":"01890f65-4c31-7f42-8a7d-111111111111","state":"archived","revision":1,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}]}
```

List:

```json
{"schema_version":"local-archived-items.response.v1","target_kind":"session","items":[{"target_id":"01890f65-4c31-7f42-8a7d-111111111111","state":"archived","revision":1,"archived_at":"2026-08-09T12:34:56.1234567+00:00","updated_at":"2026-08-09T12:34:56.1234567+00:00"}],"next_cursor":null}
```

### 8.6 Cursor

Decoded frame, no trailing NUL:

```text
UTF8("local-archive-cursor\0v1\0")
|| UTF8(target_kind) || 0x00
|| UTF8(archived_at) || 0x00
|| UTF8(target_id)
```

After section 8.3 admits the raw unpadded base64url lexeme, decode it and require
byte-for-byte re-encoding. The `session` frame is exactly 102 decoded bytes /
136 encoded ASCII bytes. The `repository` frame is 105 / 140. An admitted
lexeme with an undecodable base64url length, any other decoded length/frame,
invalid timestamp/UUID/kind, noncanonical re-encoding, or cross-kind reuse is
`400 invalid_cursor`. Disallowed alphabet/padding characters never reach this
stage and remain `invalid_request`.

Golden session cursor:

```text
bG9jYWwtYXJjaGl2ZS1jdXJzb3IAdjEAc2Vzc2lvbgAyMDI2LTA4LTA5VDEyOjM0OjU2LjEyMzQ1NjcrMDA6MDAAMDE4OTBmNjUtNGMzMS03ZjQyLThhN2QtMTExMTExMTExMTEx
```

### 8.7 Closed errors

| HTTP | Code | Exact non-HEAD entity bytes |
| ---: | --- | --- |
| 400 | `invalid_host` | `{"error":"invalid_host"}` |
| 400 | `invalid_request` | `{"error":"invalid_request"}` |
| 400 | `invalid_cursor` | `{"error":"invalid_cursor"}` |
| 403 | `csrf_rejected` | `{"error":"csrf_rejected"}` |
| 404 | `target_not_found` | `{"error":"target_not_found"}` |
| 405 | `method_not_allowed` | `{"error":"method_not_allowed"}` |
| 409 | `revision_conflict` | `{"error":"revision_conflict"}` |
| 413 | `request_too_large` | `{"error":"request_too_large"}` |
| 415 | `unsupported_media_type` | `{"error":"unsupported_media_type"}` |
| 503 | `archive_store_unavailable` | `{"error":"archive_store_unavailable"}` |
| 503 | `persistence_busy` | `{"error":"persistence_busy"}` |

HEAD uses the exact status/header/content-length/zero-entity rule in section
8.1. Valid GET query grammar precedes target proof. For POST, complete target
absence precedes archive state/revision reads.

## 9. Runtime backup/restore

### 9.1 Exact component vector and order

Current D082 vector adds exactly:

```text
local_archive:1
```

Relevant current order:

```text
monitor
session:14
local_repository_catalog:1
local_archive:1
retention:1
skill_projection:1
skill_invocation_snapshot:1       # only when separately released
local_workspace_projection:2      # only when separately released
```

`SqliteRuntimeBackupService.SupportedComponents` adds
`["local_archive"] = 1`; `MigrationOrder` inserts `"local_archive"` immediately
after `"local_repository_catalog"` and before `"retention"`.

A declared `local_archive:1` requires exact `session:14` and
`local_repository_catalog:1`, even if it stores only Session targets.
`local_archive:1 + session:13` is incompatible.

D082 preserves the complete D079 older/absent Session migration matrix when the
archive is wholly absent; it does not narrow that matrix to Session 13:

- exact Session `1..13` preview/migrate to 14 in the existing ordered steps;
- an absent Session component with a wholly absent Session namespace creates
  empty Session 14;
- exact current Session 14 remains current;
- when both catalog and archive are wholly absent, every otherwise D079-valid
  Session `1..13`, Session-absent, or Session-14 vector first reaches Session 14,
  then creates empty catalog 1, then installs empty archive 1;
- a **declared** catalog 1 accepts exact Session 14, plus only D079's one
  read-only legacy parent exception of exact Session 13 with complete legacy
  shapes. Session `1..12` or Session-absent with declared catalog 1 is
  incompatible before mutation. For the Session-13 exception, staging migrates
  Session first, validates catalog 1 against Session 14, then installs empty
  archive 1;
- a declared archive 1 has no legacy-parent exception: it always requires exact
  Session 14 and declared catalog 1.

Other D079 component dependencies and descendant-preservation rules remain
unchanged. These are archive-absent installation paths, not a dual archive
parent or archive compatibility reader.

### 9.2 Absent/current/invalid

- Absent older: no archive stamp and no case-insensitive reserved object by
  `name` or `tbl_name`; after parent migration/validation, install exact empty
  v1 and record `local_archive:0->1`.
- Current: exact v1 stamp, ten objects, normalized SQL, scalar rows, chains,
  heads, parents, manifest component version, and both table row counts.
- Invalid: stamp-only, object-only, partial/missing/extra/case alias/changed SQL/
  wrong table, duplicate/noninteger/other version, reserved object without
  declaration, view/virtual/hidden/generated/partial/expression object, or
  unknown vector. Return `restore_incompatible`; do not adopt or clean it.

Owned namespace discovery adds ASCII case-insensitive prefixes
`local_archive_` and `IX_local_archive_`, examining table/index/trigger/view
names and target table names. The executable trigger allowlist adds exactly the
six `(name,target table,normalized SQL)` definitions, and only when archive 1 is
declared.

### 9.3 Streaming row, chain, and parent validation

At source preflight, after staging migration, before swap, and installed
validation:

1. validate exact SQLite storage types and canonical bytes for every current and
   event scalar;
2. reject event without current and current without at least one event;
3. stream by `(target_kind,target_id,new_revision)`, require `archive 0->1`,
   contiguous increments, alternating action, no gap/duplicate;
4. require head/current revision, action/state, `updated_at`, and `archived_at`
   equality;
5. allow backward timestamps; revision remains authority;
6. prove every Session current target against Session in nonempty ID pages at
   most 200 on the exact read transaction;
7. prove every Repository current target with the D081 synchronous authority in
   nonempty ordinal pages at most 200 on that same transaction;
8. impose no total target-count cap and do not materialize all parent IDs;
9. require manifest `component_versions.local_archive == 1` and exact
   `row_counts` entries for `local_archive_current` and
   `local_archive_events`.

Because every valid event has a composite foreign key to current, complete
current parent proof plus complete chain proof covers event parents. A source
may have been written with CHECK/foreign keys disabled, so streaming validation
must repeat semantic invariants.

### 9.4 Registration and restore graph

There are two distinct insertion points; they must not be collapsed:

- `MigrateStaging`: run `LocalArchiveSchemaV1.Ensure` after catalog ensure and
  any conditional legacy restored-lease normalization, but before Retention
  initialization, in the same staging non-deferred transaction;
- `EnsureCurrentBackupTail`: run archive ensure after catalog ensure and before
  runtime-backup/pricing ensure, in that current-database transaction. This path
  does not perform restored-lease normalization or Retention initialization.

Re-resolve both symbols after the reviewed #124 implementation integrates,
because its current worktree is moving. `ValidateComponentShapes` validates
catalog and archive while one supplied deferred transaction remains active, so
Repository proof observes that exact source/staging database.

Preview is its own terminal branch: it performs immutable inspection and the
existing bounded compatibility/migration preview. After staging migration and
complete validation, D079's unchanged Retention comparison reports terminal
count/digest, nonterminal-reintroduction count/confirmation digest, and whether
resurrection confirmation is required. Preview emits only that result, cleans
its owned inspection artifacts, and stops. It never calls either reconciliation
mutation, mutates the target, creates a pre-restore safety backup, appends a
restore receipt, prepares a swap journal, or swaps a database.

```text
restore lease / recovery / offline destination ownership
  -> destination current-component preflight and external-state validation
  -> bounded structural archive validation and exact archive hash
  -> flushed hash-bound journal in staging phase
  -> journal-bound extraction and manifest/source-database preflight
  -> staging SQLite database
  -> ordered Session migration/validation
  -> catalog ensure/validation
  -> archive validate-or-empty-v1 installation
  -> Retention and later component migration
  -> complete staging archive validation
  -> D079 CompareRetention against the unchanged destination
  -> require allow-resurrection + exact confirmation for any nonterminal case
  -> ReconcileTerminal into staging
  -> ReconcileNonTerminal into staging
  -> private safety backup of the unchanged destination when it exists
  -> append operation-bound restore receipt inside staging
  -> checkpoint, complete staging archive/full-database revalidation, and flush
  -> prepared journal with exact staged hash
  -> revalidate unchanged destination identity/hash and external state
  -> atomic whole-database swap with rollback file
  -> read-only installed archive/full-database validation
       success -> journal installed -> journal committed -> cleanup -> success
       failure -> existing old-database rollback and rollback validation
```

Structural ZIP layout/size validation and archive hashing occur before the
journal exists. Manifest/database extraction and their complete compatibility
preflight occur only after the journal durably binds that exact hash and staging
basename. The pre-swap live-destination gate does not invent another archive
fact read: it rechecks destination identity/hash/external state, while the
staging archive was already completely revalidated before its prepared hash and
the destination archive was validated through the safety-backup path.

The Retention subsequence above is inherited unchanged from D079 and is ordered
exactly. Missing/mismatched nonterminal confirmation fails
`restore_resurrection_blocked` before either reconciliation. Current terminal
`deleted` or `read_denied_at` authority is applied first by
`ReconcileTerminal`, including its exact ownership/lineage/source-removal proof;
then `ReconcileNonTerminal` applies only the confirmed nonterminal
reintroduction set. Any comparison or reconciliation contradiction is exact
`restore_tombstone_reconcile_failed`, produces no safety backup or receipt, and
leaves the destination byte-identical. Both reconciliations complete before safety
backup creation and before the operation-bound staging receipt. D082 adds no
Retention state, confirmation field, digest, error, or alternate ordering.

The private safety-backup copy runs the same ordered migration and validation:
Session, catalog, archive, then Retention and the remaining components. A
current destination archive is preserved byte-for-byte in that copy, including
current rows and complete history; a valid older destination with the archive
wholly absent receives the selected empty archive-v1 installation only in the
migrated private copy. Its manifest/hash/archive bytes derive from that validated
copy, no receipt is written to the live destination, and the live destination
remains byte-identical until swap. After swap, target validation is read-only;
the journal, not another target write, records installed/committed state.

There is no archive ZIP member, merge, overlay, target remap, orphan drop,
repair mode, queue, lease normalization, collision resolver, or synthesized
event. Source current/events replace destination bytes with the whole database.
Incoming data after install cannot restore a target. Archive state/history,
including an empty namespace, is wholly absent from sanitized evidence
export/import.

## 10. DI, host, and no-UI boundary

After `CompleteMonitorInitialization` has installed/validated archive 1, the
raw-default composition registers exactly:

- one `SqliteLocalArchiveStore`/application;
- one `ILocalArchiveFactSnapshotContributor`;
- the already D081-owned singleton
  `ILocalRepositoryTargetExistenceAuthority`;
- three archive routes and their method adapter.

Do not register fake/default contributors. #156's lazy
`ILocalRepositoryScopeSnapshotService` consumes exactly one #134 contributor
and one #161 fact contributor. The concrete Repository existence authority is
one stateless singleton implementation. Runtime backup constructs/uses that
same implementation explicitly outside the human service provider; it does not
depend on raw-default DI and does not add a second implementation.
The #161-owned stateless `LocalArchiveSessionTargetExistenceAuthority` is passed
explicitly to the archive store and runtime-backup validator and is not exposed
as another scope or human-service interface.

Sanitized-only has no archive route, page, script, application, contributor, or
scope-service human registration. Component initialization/backup validation
still operates because runtime backup is a separate database authority.

Issue #161 adds **no UI**: no Razor page/model, static JavaScript/CSS, navigation,
Settings section, archive button, dialog, visual state, or sentence-level copy.
#145/#146/#167/#138 and #169 own those later surfaces. #161 also does not map
`GET /api/local-monitor/v1/repositories`; #134 owns it.

Frozen `/api/monitor/*`, `/api/session-workspace/*` v1, SSE, and existing
Repository management bytes remain unchanged.
