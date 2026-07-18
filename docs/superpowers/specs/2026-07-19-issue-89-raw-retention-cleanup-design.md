# Issue #89 Raw Retention Cleanup Design

## Goal

Implement one versioned retention catalog and store-specific cleanup adapters so
every current product-owned raw item has a deterministic capture, expiry, read
denial, durable cleanup, retry, restart-recovery, and physical-removal lifecycle.
The implementation preserves current Session workspace v1 wire shapes and does
not add pin or delete-now behavior owned by Issue #90.

The immutable kickoff and inventory base are both
`11d6c587903f6ea97026d815f608231efea08d65`. Issue #89 closeout must compare the
final candidate with this inventory base and classify every newly introduced
raw-bearing store before claiming completion.

## Sources and precedence

The Issue #89 body, its internal tasks 89-A through 89-F, acceptance criteria,
and close condition govern this work. Current product requirements and
specifications still describe expiry-only Session retention and therefore must
be updated before implementation. The detailed canonical contract belongs in:

- `docs/requirements.md` for the product-level cleanup requirement;
- `docs/spec.md` for system behavior and the versioned read surface;
- `docs/specifications/layers/raw-store-normalization.md` for persistence,
  inventory, lifecycle, worker, and adapter behavior;
- `docs/specifications/layers/telemetry-ingestion.md` for runtime scheduling,
  status routes, and active-operation exclusion;
- `docs/specifications/interfaces/canvas-session-workspace.md` for the frozen v1
  compatibility projection;
- `docs/specifications/security-data-boundaries.md` for raw access, private
  locators, diagnostics, and no-leak constraints;
- `docs/architecture.md` and `docs/decisions.md` for ownership and the durable
  lifecycle decision.

## Architecture

For each configured Local Monitor runtime database, exactly one logical
retention catalog resides in the same physical SQLite database as `raw_records`,
Session, and analysis tables. It is an independent versioned component, not an
extension of the monitor projection or Session schema. One
`RetentionCatalogStore` owns schema/migration, items/tombstones, capture and
delete journals, queue state, access and deletion leases, retry metadata,
receipts, and adapter coverage. No source component creates a parallel catalog.

DB-backed source write plus catalog registration, and source delete plus receipt
and tombstone, enlist the same SQLite connection and transaction. File-backed
producers receive the catalog database through explicit injected configuration;
they never infer it from cwd, repository, a source path, or neighboring files.
If the catalog is unavailable, bundle publication and SDK operation fail before
creating raw files. `store_instance_id` is an adapter-owned namespace inside
this catalog, not authority to discover another database or filesystem root.

Each product-owned raw capture is registered in the catalog in the same SQLite
transaction as its raw database write. File-backed items use a durable journal:
reserve catalog identity, create an exclusively owned staging target, write and
flush exact members, publish atomically, then mark the item readable. A crash at
any stage has one deterministic forward recovery path.

The catalog, not aggregate Session columns or source-row presence, is the read
authority. Read availability requires positive proof of an unexpired readable
catalog revision, an exact matching source item, and an active read lease.
Expiry sets irreversible read denial before cleanup is queued. Physical deletion
retains a catalog tombstone so expired or deleted Session content never regresses
from the established `410` contract to an incorrect `404` or `not_captured`.

## Retention item identity and data model

Every catalog item contains at least:

- opaque stable `item_id`;
- `store_instance_id`, closed `store_kind`, and exact `source_item_id`;
- owning component and an encrypted/private or otherwise non-public exact source
  locator when a file adapter needs one;
- exact Session/Event/trace/evidence reference when the source has one;
- authoritative `captured_at` and nullable finite `expires_at`;
- `policy_id`, `policy_version`, lifecycle `state`, and monotonic `revision`;
- irreversible nullable `read_denied_at`;
- queue time, deterministic priority/order key, lease owner/expiry;
- bounded attempt count, next retry time, fixed error code, and retry-exhausted
  flag;
- deletion receipt time and adapter coverage version.

The unique ownership key is
`(store_instance_id, store_kind, source_item_id)`. Queueing is an idempotent
state transition on `item_id`; repeated scans never create duplicate work.
Private locators are never returned by diagnostics or repository-safe evidence.

`not_captured` is not stored as an item lifecycle state. It is an aggregate
result when no raw item ever existed. No synthetic retention row is created for
an absent raw item.

## Canonical lifecycle

The closed item lifecycle is:

- `expiring`: readable, finite `expires_at`, automatic expiry enabled;
- `retained_by_policy`: readable, automatic expiry stopped by an explicit
  versioned policy decision;
- `expired_pending_deletion`: read denied, eligible for durable queueing;
- `deletion_queued`: read denied, durable work available;
- `deleting`: read denied, owned by one bounded worker lease;
- `deleted`: read denied, source absence verified and receipt persisted;
- `deletion_failed`: read denied, retry metadata or retry-exhausted terminal
  metadata persisted.

Allowed transitions are forward-only:

```text
expiring -> expired_pending_deletion -> deletion_queued -> deleting -> deleted
                                                       -> deletion_failed
deletion_failed -> deletion_queued                    (retry eligible only)
deleting -> deletion_queued                            (cancellation before any
                                                        durable delete-step intent only)
expiring <-> retained_by_policy                       (Issue #90 mutation only)
```

Issue #89 implements the automatic-expiry path and the extension seam for the
Issue #90 policy mutation; it does not expose or invoke the policy mutation.
After `read_denied_at` is set, no transition, retry, repair, restart, clock
change, or missing source may restore readability. Retry exhaustion remains
`deletion_failed` with a terminal flag rather than adding an ambiguous state.
The guarded `deleting` to `deletion_queued` transition requires proof that no
delete-step intent exists and atomically surrenders the deletion lease. Once an
intent exists, cancellation or lease loss preserves `deleting` and its durable
cursor so recovery can only continue forward.

The persisted lifecycle is exactly those seven values. Inventory categories
(`required_cleanup`, `retained_by_policy`, `not_applicable`, `blocked`),
aggregate states (`not_captured`, `mixed`), capture phases, `retry_exhausted`,
and diagnostic conditions are separate domains. The repeated token
`retained_by_policy` is always domain-qualified.

File capture progress lives in `retention_capture_journal` with the closed
phases `reserved`, `staging`, `published_pending_catalog`, and `complete`.
The journal reserves the final item ID, exact owned target identity, owner
marker, and exact member/child identities. No item is readable or cleanup-
eligible before completion. Publication verification creates the catalog item
in `expiring` and completes the journal. Startup may continue the exact
journaled publication, finalize an exact already-published owned target, or
remove an exact exclusively owned unpublished staging target. It never adopts
an unjournaled target. For SDK staging, exclusive empty-child creation and its
owner marker complete capture; subsequent SDK writes are protected by an active
operation lease.

## Timestamp and policy rules

The default policy is `raw-default-90d` version 1. Its authoritative expiry is
`captured_at + 90 days`. Existing Session `captured_at`/`expires_at` values are
preserved exactly. Raw records use a valid `received_at`; analysis runs use a
valid `requested_at`. Missing or invalid legacy timestamps are classified
`blocked` and read denied; they are never replaced with startup, import,
restore, or current time.

For a new Sensitive Bundle, `captured_at` is the catalog capture-journal
reservation timestamp, committed before product-controlled staging begins. For
a new per-analysis SDK child, `captured_at` is the owning analysis run's valid
`requested_at`, copied into the catalog reservation before the child is created.
Filesystem create/write time, publication/completion time, reconciliation time,
and current time are never timestamp authorities. A missing or invalid required
timestamp fails capture closed before publication.

Sensitive Bundles retain their existing explicit seven-day policy as
`sensitive-bundle-7d` version 1. This store-specific policy does not change the
90-day default. Clock rollback cannot undo an already persisted expiry. Clock
forward may make additional items eligible but cannot skip the durable queue or
adapter verification.

## Immutable current-store inventory

| Item granularity | Classification at kickoff | Exact authority and treatment |
| --- | --- | --- |
| One `session_event_content` row | `required_cleanup` | Catalog ID, exact `event_id`, joined exact Session event provenance. Delete content only; retain Event/Session metadata and tombstone. |
| One `raw_records` row including `payload_json` and `resource_attributes_json` | `required_cleanup` | Catalog ID, exact raw record primary key, store instance, and ingestion owner receipt. Delete raw row; retain sanitized projections and tombstone. |
| One analysis-run raw aggregate | `required_cleanup` | Catalog ID and exact run ID. In one transaction delete exact run events and null raw result/error fields; retain run metadata, safe summary, and tombstone. |
| `monitor_analysis_safe_summaries` | `retained_by_policy` | Existing exact run reference and allowlist contract. Never regenerate after raw deletion. |
| Sanitized monitor projections and Session/Event metadata | `retained_by_policy` | Existing primary/reference keys and allowlist contracts. They never authorize raw reconstruction. |
| New product-created Sensitive Bundle | `required_cleanup` after ownership hardening | Catalog-generated ID independent of path/basename, exact manifest digest, exclusive owner marker, exact created-member journal, private locator. Delete exact members only. |
| Legacy/arbitrary-location Sensitive Bundle mode | `blocked` at kickoff; retired and `not_applicable` in the final candidate | Never path-scan or recursively delete. Exact indexed/presented artifacts may be imported only with positive ownership proof. |
| Shared SDK-root write/read mode | `blocked` at kickoff; retired and `not_applicable` in the final candidate | The configured root is never a deletion target. New runs use an exact catalog-owned child only. |
| New per-analysis SDK child directory | `required_cleanup` after redesign | Exact run/catalog ID, exclusive marker, private locator, active-operation lease. Dispose SDK session/client before deleting only that child. |
| Caller-supplied raw OTLP input file | `not_applicable` | Caller-owned input; never delete it because it was ingested. |
| Receiver-created raw OTLP output file | `not_applicable` at kickoff | HEAD implements SQLite receiver storage only. Reclassify during final delta inventory if file output appears. |
| External blob/object store | `not_applicable` at kickoff | No implementation exists. Reclassify before any such store ships. |

The final-candidate inventory covers store modes and exact items reachable from
a product-owned catalog, database record, or deterministic product index. It is
not a machine-filesystem inventory and grants no discovery authority. Before
close, arbitrary-location bundle creation/read/resume/enumeration/cleanup and
shared SDK-root write/read modes are removed from all final-candidate paths.
They then become `not_applicable` as retired store modes; this does not claim
historical filesystem residue is absent.

An exact legacy artifact is importable only from an existing product-owned
record or explicitly presented manifest with positive product identity, exact
member/child identity, and exclusive ownership proof. Failure leaves that exact
known item `blocked` and it blocks close until its product reference is retired
or ownership is safely reconciled. Unindexed residue is not a known current
item: it is never scanned, adopted, cataloged, or deleted and is disclosed as
non-purged legacy residue. “No blocked current item” means no final-candidate
store mode or exact product-indexed/imported item is blocked; it does not claim
global filesystem absence.

The closed retention v1 `store_kind` registry is exactly:

- `session_event_content`;
- `raw_record`;
- `analysis_run_raw`;
- `sensitive_bundle`;
- `analysis_sdk_directory`.

Sanitized projections, Session/Event metadata, safe summaries, receipts, and
tombstones are retained outputs/catalog metadata, not raw store kinds. Caller-
owned inputs are excluded. Nonexistent receiver-file and external-blob stores
appear only in the final-delta watchlist. Issues #79, #87, and #88 receive the
extension contract but no placeholder table, adapter, queue row, or store kind.

## Sensitive Bundle ownership hardening

New bundle creation uses a catalog-generated ID and creates a dedicated child
with exclusive-create semantics. It refuses a pre-existing target and never
overwrites a member. The journal records exact members and their ownership
identity without exposing paths publicly. The manifest/owner marker is deleted
last; the directory is removed only if empty, non-reparse, and still matches the
recorded identity. An added, replaced, reparse-linked, or unowned member makes
the item `deletion_failed` with a fixed ownership error and blocks closeout
until reconciled without deleting the unexpected member.

## SDK staging ownership hardening

The configured SDK `BaseDirectory` becomes a parent only. Each analysis run
reserves an exact catalog item and an exclusively owned child before SDK start;
the SDK receives that child as its base directory. The analysis run holds an
active-operation lease. Cleanup can act only after the session/client is
disposed and the lease is released or deterministically expired. The parent and
pre-#89 shared contents are never recursively swept.

## Durable queue, worker, and recovery

`expired_pending_deletion` is durable backlog even if no worker is available.
Promotion to `deletion_queued` is database-backed and idempotent. An in-memory
channel may wake workers but is never the source of work. Scans are bounded by
item count and elapsed time and order by `expires_at`, then item ID.

Access/operation leases and deletion leases are distinct tables or a required
discriminator with separate invariants. No access lease begins at/after expiry
or after read denial. A deletion lease is acquired only without a conflicting
access lease. Renewal requires item ID, owner, lease generation, and expected
revision; loss prevents every later destructive or result-recording step.

Before a non-transactional file mutation, the worker revalidates exact identity
and ownership and commits a delete-step intent containing the exact member/child
identity and continuation cursor. `attempt_count` increments once with this
intent; contention/cancellation before it consumes no attempt. File absence
before an intent is `unexpected_source_missing`. Absence after a matching intent
and prior positive ownership proof is the idempotent uncertain delete/receipt
crash-window result and advances only that journal cursor. Absence alone never
proves success. SQLite deletion and receipt are atomic; prior absence without a
receipt is unexpected missing.

Cancellation before delete-step intent releases the lease and returns to
`deletion_queued` only through the guarded lifecycle transition above.
Cancellation/process loss after intent leaves `deleting` and the durable cursor;
lease expiry permits forward recovery. Transient failure
records `deletion_failed`, fixed error, attempt count, and retry time. Eligible
non-exhausted retry uses CAS back to `deletion_queued`; exhaustion remains
`deletion_failed` with `retry_exhausted=true`.

Startup reconciliation handles incomplete capture journals, expired leases,
`deleting` items, partial file journals, source-deleted/catalog-not-finalized,
and catalog-finalized/source-still-present windows. Recovery inspects only the
exact source reference already stored for that item. A source missing without a
durable prior deletion receipt is a fixed failure, not assumed success.

Shutdown cannot abandon an unbounded lease or acknowledge success before the
receipt commits. Scan, claim, active-worker, retry, lease, file-member, and byte
limits are named versioned policy constants pinned by the canonical spec and
tests, with a durable continuation cursor and no sleep-based assertion.

## Deterministic legacy database backfill

Retention v1 migration is all-or-nothing. It creates or validates one immutable
database-resident `store_instance_id`, enumerates only recognized tables in the
open database, and registers exact Session content keys/foreign ownership,
`(store_instance_id, raw_record_id)` plus an immutable ingestion-owner receipt,
and `(store_instance_id, run_id)` plus exact run-owned raw fields/events.
Recognized schema membership, store identity, and exact primary/foreign keys are
the ownership basis; trace, path, repository, process, and timestamp are not.

Existing Session capture/expiry, raw `received_at`, and analysis `requested_at`
are copied exactly. Missing/invalid identity or timestamp aborts and rolls back
the entire migration; no `now`, partial catalog, or synthetic lifecycle state is
written. Migration failure fails the host/command before raw reads or new
captures are enabled and returns only a fixed sanitized code. Reopen observes
either exact pre-migration schema/data/version or an idempotently complete v1
catalog. All committed Session and Monitor historical fixtures must pass two
fresh restarts with zero supported-input backfill blockers.

## Atomicity, rollback, and deletion order

SQLite-backed capture and catalog registration share one transaction. SQLite
adapter deletion and catalog result share one transaction whenever the source
and catalog are in the same database. Cross-file deletion is journaled and
forward-only.

Deletion order is normative:

1. CAS to irreversible read denial and durably queue.
2. Reject new reads and active-operation acquisitions.
3. Quiesce or deterministically cancel the bounded active operation, then verify
   that no conflicting access/operation lease remains.
4. Atomically claim a bounded deletion lease only while no conflicting lease
   exists, then revalidate catalog revision, store instance,
   adapter kind, exact source ID/locator, ownership marker/digest, expiry, and
   active operation state.
5. Delete only the adapter-owned raw source in store-specific order.
6. Verify exact source absence.
7. Persist a deletion receipt and `deleted` tombstone.
8. Release the lease.

Before a transaction commits, normal rollback is allowed. After any physical
deletion, recovery only continues forward; it never creates a raw backup or
reconstructs deleted content. Partial failure never makes content readable.

## Cleanup exclusion and late-write fencing

Raw HTTP reads, monitor projection reads, analysis tool-data loading, active
analysis runs, Sensitive Bundle creation, and SDK directory use acquire bounded
item leases. No lease starts after expiry. Late projection or analysis writes
must carry the expected catalog revision and cannot recreate raw fields after
cleanup advances the item.

The extension seam for Issues #79, #87, and #88 is the closed adapter registry
plus active-operation lease contract. Issue #89 does not implement those future
stores. Any future store must register its own exact identity, capture
transaction/journal, timestamp authority, adapter, exclusion behavior, and
no-leak tests before it can be classified `required_cleanup`.

## Authoritative retention v1 reads and compatibility

The additive surfaces are `GET /api/retention/v1/status` and
`GET /api/retention/v1/sessions/{sessionId}`. Existing
`/api/session-workspace/*` v1 shapes remain exact. Authoritative Session
aggregate state is exactly `not_captured`, the seven canonical lifecycle states,
or `mixed`. Zero items/tombstones yields `not_captured`; one represented
lifecycle yields that state; multiple represented lifecycles yield `mixed`.
The DTO returns exact per-lifecycle counts plus readable/read-denied counts and
never collapses mixed state. Bounded item summaries expose only opaque ID, store
kind, canonical state, policy/version, lifecycle timestamps, bounded attempts,
retry-exhausted, and fixed error code—never source keys/IDs not already public,
paths, locators, raw exceptions, credentials, PII, or raw content.

The frozen v1 compatibility projection is:

| Canonical condition | v1 event `content_state` | v1 Session `raw_retention_state` | Raw content route |
| --- | --- | --- | --- |
| Never captured | existing `not_captured`, `redacted`, or `unsupported` | `not_captured` when no event was ever captured | existing `404` |
| Exact readable `expiring` or `retained_by_policy` item | `available` | `expiring` when any readable sibling exists | existing `200` |
| `expired_pending_deletion`, `deletion_queued`, `deleting`, any `deletion_failed`, or `deleted` | `expired_pending_deletion` | `expired_pending_deletion` when no readable sibling exists | exact existing `410` body |
| Previously captured but source/cross-reference is stale, missing without receipt, or repair-blocked | `expired_pending_deletion` | `expired_pending_deletion` when no readable sibling exists | fail-closed exact existing `410` body |
| `--sanitized-only` | existing metadata behavior | existing metadata behavior | route remains absent (`404`) |

The v1 lossy projection uses only this safe precedence: any proven readable
`expiring` or `retained_by_policy` item yields `expiring`; otherwise any ever-
captured/tombstoned/queued/deleting/deleted/failed item yields
`expired_pending_deletion`; otherwise `not_captured`. A deleted, missing,
failed, stale, or unknown item is never projected readable.

The expired route response remains byte-for-byte:

```json
{ "error": "raw_content_expired", "content_state": "expired_pending_deletion" }
```

## Sanitized diagnostics

The versioned status model provides pending, queued, deleting, failed,
retry-exhausted, orphan/unexpected-missing, and expired-but-readable violation
counts; oldest pending age; worker state and last successful run; and inventory
and adapter coverage version. Unknown or unavailable values remain null/unknown
and are never replaced with zero or healthy.

Only opaque item IDs, store kind, category/state, bounded counts/attempts,
timestamps, and fixed error codes are diagnostic-safe. Raw bodies, result/error
text, credentials, PII, source input paths, bundle delete paths, SDK paths,
absolute paths, and raw exceptions are forbidden in API responses, logs,
repository-safe evidence, and committed fixtures.

## Physical cleanup boundary

For SQLite, `deleted` means exact source mutation, fresh exact-key absence
verification, and catalog receipt/tombstone committed in the same transaction.
WAL checkpointing is database maintenance, not item lifecycle. After a bounded
successful batch and quiesced conflicting readers/leases, maintenance requests
a bounded `wal_checkpoint(TRUNCATE)`. Busy/failure records a fixed sanitized
maintenance error and retry time, never restores readability, never changes a
verified item from `deleted`, and never triggers unbounded retry or per-item
rewrite. Tests use controlled connections and cleared pools; they never emit raw
database bytes. No freelist/media/backup/snapshot/forensic purge or per-item
`VACUUM` is promised. File cleanup unlinks exact owned members and removes the
owned directory only when safely empty.

## Testing strategy

Implementation follows strict RED-GREEN-REFACTOR. Each new behavior is first
demonstrated by a test that fails for the expected missing contract. Tests use
committed synthetic historical fixtures and deterministic gates:

- real retention catalog v1 SQLite fixture with manifest provenance and digest;
- existing Session v1-v10 and monitor v1-v5 fixtures migrated through two fresh
  restarts, preserving original timestamps and rows;
- injected-statement failure proving schema/data/version rollback after reopen;
- exact v1 DTO enum and byte-compatible `410` consumer tests;
- `MutableTimeProvider` for expiry, lease, and retry eligibility;
- `Barrier`, `TaskCompletionSource`, and `ManualResetEventSlim` gates for worker
  races, never sleep or arbitrary retry;
- two-worker single-claim/single-delete, stale revision rejection, expired lease
  reclaim, cancellation, restart at every deletion crash window, and
  forward-only partial recovery;
- exact positive and negative adapter tests for Session, raw record, analysis,
  bundle, and SDK staging ownership;
- sanitized projection/safe summary preservation and no raw reconstruction;
- no-leak API/log/evidence tests covering raw, credential, PII, and path markers;
- bounded scan/batch/member/byte limits and deterministic ordering;
- final delta inventory artifact from `inventory_base_sha` to the final
  candidate.

Focused tests run after each task. Issue closeout runs, in order:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

## Close-readiness

Issue #89 is close-ready only when tasks 89-A through 89-F are complete; every
final-candidate raw store has exactly one inventory classification; every
`required_cleanup` store has a registered tested adapter; no known current item
is `blocked`; all capture paths are atomic or journaled; all raw read paths use
the catalog gate; legacy backfill is complete and restart-safe; physical
cleanup, diagnostics, and no-leak tests pass; and repository-safe handoffs for
Issues #90, #91, #79, #87, and #88 are recorded.

Repository close evidence is bounded to the final candidate and the named
`retention-closeout-corpus-v1`: every committed Session schema fixture from v1
through the pre-retention version, every committed Monitor schema fixture from
v1 through the pre-retention version, and the new retention-catalog-v1,
sensitive-bundle-v1, and sdk-directory-v1 fixtures. The final inventory names
each fixture and records its digest and restart result. A separate runtime
database with an unknown or blocked item remains fail-closed and reports that
instance's operational blocker; repository closeout does not claim the absence
of blockers in unexamined runtime instances.

Task 89-F creates `docs/sprints/issue-89-final-retention-inventory.md` containing
base/final SHAs, closed registry and coverage version, every final creator,
persistence/read/cleanup path, identity/ownership/timestamp/policy, capture
transaction/journal, exclusion, adapter order, classification, and test
references. It examines production source/schema/config/spec changes in the
commit range, all final raw producers/routes, and every named member of
`retention-closeout-corpus-v1`, never arbitrary runtime filesystem contents.
Closeout records zero unclassified stores, uncovered required stores, blocked
corpus items, gate-bypassing reads, and unregistered creation paths, together
with the exact corpus manifest, fixture digests, migration/restart results, and
final-candidate SHA. Nonexistent future stores remain a watchlist, never passed
coverage.

## Out of scope

Issue #89 does not implement:

- pin, unpin, or delete-now API/UI/audit behavior owned by Issue #90;
- future import, replay, or backup production implementations;
- placeholder adapters or stores for Issues #79, #87, or #88;
- cloud retention;
- sanitized aggregate deletion;
- legal hold, team policy, or remote administration;
- backup, snapshot, or offline-copy purge;
- secure hardware/media erase guarantees;
- automatic discovery, adoption, or deletion of unindexed arbitrary legacy
  filesystem residue;
- deletion of caller-owned raw input files.

Issue #89 supplies only the catalog, lifecycle, exact current-store adapters,
cleanup exclusion/lease extension contract, diagnostics, and repository-safe
consumer handoffs.
