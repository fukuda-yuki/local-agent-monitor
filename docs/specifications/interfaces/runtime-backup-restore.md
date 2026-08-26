# Runtime Backup And Restore Interface

Status: Accepted for Issue #88 (2026-07-23)

This specification defines the Local Monitor runtime backup and restore v1
contract. It is independent from the repository-safe sanitized evidence export
defined by Issue #85.

Local Monitor v1 presents this as a focused flow opened from Unified Settings,
not permanent navigation. Archive is separate reversible metadata and never a
backup/restore operation. Node/Repository/Compare AI operational content and
deterministic Compare snapshots are 24-hour non-backed-up state under their
own accepted contracts; this interface does not create alternate ownership for
them. The minimal #165/#166 comparison-expiry tombstone from
[the route transport contract](local-monitor-v1-route-transport.md) is part of
that excluded comparison operational namespace, not one of the Retention
tombstones inventoried by this backup. Its exclusion is the exact staging-copy
projection in section 5; prose classification alone does not exclude a SQLite
table from `database.sqlite`.

## 1. Scope and profile

The fixed bundle profile is `local-runtime-backup`. A bundle contains the
complete supported Local Monitor SQLite restore unit, including raw telemetry,
Session content, policy state, Retention-owned tombstones, mutation/audit state,
projections, and component versions captured in one SQLite snapshot, minus only
the exact profile-owned staging exclusions defined by this contract.

The bundle is private local runtime data. It is explicitly:

- raw-bearing;
- not repository-safe;
- operator-owned at a caller-selected path;
- outside the Issue #89 retention cleanup inventory; and
- never automatically uploaded, synchronized, encrypted, scheduled, or
  deleted.

Every manifest, preview, and successful result contains the fixed warnings
`raw_content_included`, `not_repository_safe`, and
`retention_backup_not_purged`. The last warning is the Issue #90
`retention_backup_not_purged` contract: delete-now does not purge or inventory
backup files. Backup ownership does not add a sixth Retention catalog store
kind.

V1 does not back up cloud resources, provider credentials, encryption keys,
executables, release packages, setup mutation authority, or caller-owned
files. It provides no raw replay or import API.

## 2. Restore unit and external-state policy

The SQLite database is the only restorable byte member. Product-owned state
outside SQLite is handled explicitly as follows.

| State | V1 policy | Backup behavior | Restore prerequisite |
| --- | --- | --- | --- |
| Local Monitor PID/state/log files | Ephemeral, host-specific | Fixed exclusion; presence only, no path or bytes | Start wrapper rematerializes state; logs are not restored |
| Release executable/configuration secrets | Deployment/secret input | Fixed exclusion; never read | Install a compatible Local Monitor and re-provide secrets |
| `setup/ownership-ledger.v1.json`, plans, setup backups, journals | Host-bound configuration ownership | Fixed exclusion; the manifest reports only `present`/`absent`, never paths, target values, or bytes | Rerun `setup status` and a new setup workflow on the destination; restored DB never claims setup ownership |
| `proposal-apply/apply-root-map.json` | Durable host-bound apply-root configuration | The exact canonical stable root-map file is excluded; its bytes and configured paths never enter the manifest or bundle | Reconfigure the destination apply roots |
| Other `proposal-apply/` private drafts, snapshots, journals, or unknown state | Required companion state for an active or unresolved proposal apply | Any such entry fails `external_runtime_state_active`; malformed root-map, reparse, or unreadable state fails `external_runtime_state_unsafe` | Resolve/recover/finish proposal apply before backup |
| Active `sensitive_bundle` or `analysis_sdk_directory` Retention item, unresolved or orphan/mismatched external capture reservation/journal, or legacy external-bundle blocker | Raw store outside SQLite and source-host recovery authority | Backup fails `external_raw_store_active` without reading or emitting its private locator | Complete/abandon the capture or delete/expire the item through its owner. A recorded legacy blocker has no v1 clearance operation and requires a future profile that explicitly carries/adopts it. |
| Prior runtime backup files | Operator-owned backup policy A | Not inventoried in the manifest, included, or purged. A bounded direct-sibling safety scan may read only the strict archive envelope and canonical manifest needed to distinguish a prior v1 backup from unknown runtime state; it never extracts or opens the database member. | Operator retains/deletes separately |

An empty `proposal-apply/` directory, its exact empty `drafts/` scaffold, and a
directory containing only that scaffold plus the exact, canonical, terminal
`apply-root-map.json` shape are allowed. The service
validates only that closed configuration-file shape; it neither inventories nor
emits configured path values. Empty, malformed, duplicate, non-canonical,
unknown, reparse-bearing, or unreadable root-map state fails closed. A terminal deleted external
raw item is allowed because its tombstone and denied lifecycle are in SQLite
and its bytes are no longer a restore dependency. A terminal `complete` file
reservation or `sealed` SDK-directory reservation is allowed only when its exact
`store_instance_id/store_kind/source_item_id` tuple resolves to that deleted
item. A terminal file reservation must also retain the production
`store_kind=sensitive_bundle` and `source_item_id=capture_id` identity, and a
terminal capture-journal row must join through that exact complete reservation
to the deleted sensitive-bundle item; a row cannot borrow an unrelated deleted
Retention item. An orphan or mismatch is still active external authority. Any unknown product-owned
regular file directly under the database runtime directory fails
`external_runtime_state_unknown`, except the database and its SQLite sidecars,
the documented ephemeral files/directories above, `setup/`,
`proposal-apply/`, `sanitized-exports/`, and `runtime-backups/`. The
product-owned `raw-replays/` parent is allowed only when it is an exact
non-reparse empty directory after Retention external-state validation; any child
or unreadable entry remains unknown/unsafe external state. The scanner never
follows a symlink, junction, mount reparse point, or other reparse entry.
Two or more caller-selected direct-sibling v1 backups therefore remain
backupable, while a malformed, non-canonical, or CRC-invalid archive envelope
or manifest, or an unrelated regular file, still fails closed. Database-member
corruption is rejected by inspect/preview/restore, not by this intentionally
envelope-only runtime-root classification. This recognition does not add backup
inventory, emit a backup list, or authorize cleanup of an operator backup.

The manifest contains a closed `external_state` array in the table order. Each
entry contains only `kind`, `source_state`, `included=false`, `consistency`, and
`restore_action`. It contains no absolute/local path, credential, setup target,
or private locator. This inventory makes DB-only consistency explicit while a
realistic default runtime with no active companion file remains backupable.

| `kind` | Closed `source_state` | `consistency` | `restore_action` |
| --- | --- | --- | --- |
| `ephemeral_runtime` | `present` or `absent` | `ephemeral` | `restart_rematerializes` |
| `setup_storage` | `present` or `absent` | `host_bound` | `rerun_setup` |
| `proposal_apply` | `configured`, `empty`, or `absent` | `configuration_only` | `reconfigure_apply_roots` |
| `operator_backups` | `not_inventoried` | `operator_owned` | `retain_or_delete_separately` |

## 3. Archive contract and limits

Contract names are fixed:

| Contract | Value |
| --- | --- |
| Bundle schema | `local-runtime-backup.v1` |
| Bundle profile | `local-runtime-backup` |
| Manifest schema | `local-runtime-backup-manifest.v1` |
| Canonical JSON | `local-runtime-backup-canonical-json.v1` |
| Archive | `local-runtime-backup-zip-store.v1` |
| Checksum | `sha256.v1` |
| Restore preview | `local-runtime-restore-preview.v1` |
| Restore result | `local-runtime-restore-result.v1` |
| Receipt component | `runtime_backup` version `1` |

The ZIP has exactly two entries in this exact order:

1. `manifest.json`
2. `database.sqlite`

Both entries use ZIP store mode, DOS epoch `1980-01-01T00:00:00`, zero
external attributes, no extras, no comments, no data descriptor, no ZIP64,
and no trailing bytes. Entry names are ASCII exact matches. Therefore
traversal, absolute, drive/UNC/device/URI, backslash, alternate separator,
duplicate/case-alias, directory, symlink-attributed, and forbidden-extra
members are rejected structurally before extraction.

Limits are:

- archive entries: exactly 2;
- manifest bytes: at most 1 MiB;
- database member: at most 512 MiB;
- total uncompressed bytes: at most 513 MiB;
- archive bytes: at most 513 MiB;
- SQLite tables/component versions/count entries: at most 256 each;
- JSON depth: at most 32.

A zero-byte archive is `archive_invalid` (`400` over HTTP), never
`bundle_too_large`; positive lengths above the archive ceiling are
`bundle_too_large` (`413`).

CLI reads are length-checked before allocation and again while streaming. HTTP
inspection is additionally bounded by the configured Kestrel request limit.
Compression, integer overflow, short read, extra output, CRC/ZIP corruption,
or any limit violation fails with no extraction publication.

Inspection opens the archive once with write/delete sharing denied. Raw ZIP
layout validation seeks through bounded headers and names, then extraction and
the complete-archive digest reuse that same immutable handle; it never loads a
513 MiB archive into one byte array or reopens a path between those steps.

## 4. Canonical manifest

`manifest.json` is UTF-8 without BOM and is byte-identical to the v1 canonical
serializer. Object fields have this exact order:

1. `schema_version`
2. `bundle_schema_version`
3. `bundle_profile`
4. `created_at`
5. `source_application_version`
6. `source_platform`
7. `snapshot`
8. `backup_window`
9. `component_versions`
10. `row_counts`
11. `projection_cursors`
12. `retention`
13. `external_state`
14. `files`
15. `warnings`
16. `compatibility`

UTC timestamps use round-trip `O` format with offset zero. Map keys are unique
and ordinal-sorted. Lowercase SHA-256 is exactly 64 hexadecimal characters.
The sole `files` row is
`{"path":"database.sqlite","size":<bytes>,"sha256":"<digest>"}`.
The manifest does not contain its own checksum. The command/result separately
returns the SHA-256 of the complete ZIP.

`snapshot` records `method=sqlite_online_backup`, the source journal mode,
`integrity_check=ok`, `foreign_key_check=ok`, and one opaque snapshot ID derived
from the database checksum. `component_versions` records every row from the
standard `schema_version` table plus `retention_component_versions`, with
component names unique after merge. `row_counts` records every non-SQLite table
present in the canonical bundle projection, ordered by table name. The exact
`local_comparison_expiry_tombstones` table is removed from that projection
before inventory and therefore has no row-count entry. `projection_cursors`
records only bounded sanitized cursor/high-water integers or null; it never
records raw IDs.

`backup_window` binds the online snapshot to explicit bounded observations. Its
exact field order is `started_at`, `completed_at`,
`projection_cursors_at_start`, and `projection_cursors_at_end`. The UTC start is
captured before the starting sanitized cursor vector and host external-state
preflight; the UTC completion is captured after the ending cursor vector and
the matching host external-state revalidation. `started_at <= completed_at`.
`projection_cursors` remains the exact vector read from the captured snapshot,
not either live vector. For every comparable monotonic cursor,
`start <= snapshot <= end`; null means unavailable and is never replaced with
zero. All three vectors use the same bounded, ordinal-canonical, raw-ID-free
contract.

`retention` contains counts by the five closed store kinds and seven closed
states, tombstone count, earliest/latest original `captured_at`,
earliest/latest original `expires_at`, sorted `policy_id`/`policy_version`
pairs, and `backup_non_purge_warning_code=retention_backup_not_purged`.
Creating or restoring a backup never rewrites `captured_at`, `expires_at`,
policy ID/version, tombstone timestamps, deletion timestamps, or an item's
retention clock.

`compatibility` contains reader minimum/maximum `1`, required component/version
pairs, and `migration_policy=supported_older_only`. Integrity checks prove
structural consistency, not author identity, signature, provenance, malware
safety, or repository safety.

## 5. Online backup

Before any schema write, the service validates the DB component vector, minimum
shapes, integrity, executable SQLite object allowlist, and external-state
policy. A future, unknown, or malformed component therefore remains
byte-for-byte unchanged. For a supported vector with no alert engine it creates
the exact engine-v2 schema directly; for exact engine v1 it applies the
byte-preserving v1-to-v2 migration; exact v2 is validated. It then ensures
`runtime_backup` v1 and `pricing` v1 in their fixed order before snapshot
capture. A partial/malformed/future engine or pricing-without-engine vector is
rejected, not repaired.
Declared Monitor v10 additionally requires the source-attribution evidence
authority and durable reconciliation queue minimum shapes. Issue #154 advances
the current Monitor component to v11. Exact v10 is its only supported
predecessor; v11 additionally requires the append-only interpretation
supersession/head/current-revision/receipt authority and base-observation
update/delete/parent-cascade guards. The online snapshot, manifest table
counts, inspection, preview, and restore preserve every pending source and
Skill queue row; backup never consumes reconciliation work.
Issue #154 additionally includes immutable trace-version base observations,
interpretation supersession ledger/heads, trace compatibility revisions and
the complete independent `skill_projection:1` namespace. A captured Skill
queue lease is data, not permission to resume as the old worker: restore
converts `leased` to `pending` for the same generation, clears lease owner and
expiry, and preserves attempt count. Backup never consumes or reconstructs a
generation frontier.
It opens the source with pooling
disabled and a bounded busy timeout, creates a same-directory private temporary
SQLite file, and invokes `SqliteConnection.BackupDatabase`. It never copies a
live database file or its `-wal`/`-shm` sidecars.
The live source and every current or installed database use normal SQLite
locking and change detection, including while restore owns its lease. SQLite
`immutable=1` is restricted to closed, service-owned snapshot/staging files
whose sidecars are absent; it is never selected merely because a live path
happens to have no sidecar at one instant.

### Comparison expiry-tombstone staging projection

When the #165/#166 comparison-expiry owner is first installed, its canonical
migration creates the exact `local_comparison_expiry_tombstones` table and
immutable guards; later ordinary startup validates rather than recreates it.
A source backup preflight requires that exact object and streaming-valid rows;
a missing, renamed, malformed or guard-incomplete source object fails closed. A
supported pre-#166 component vector has no such object and does not run this
projection. #166 must canonically identify the first component vector that
installs it before implementation.

For an installed owner, immediately after `BackupDatabase` completes and before
the destination is opened read-only or any integrity check, table/count/cursor/
retention inventory, database hash, manifest construction or archive write, the
runtime-backup owner performs this exact operation:

1. Prove that the private owner-marked staging path and open connection are the
   completed backup destination and are not the canonical source path. Pooling
   remains disabled; no SQL from this operation is issued to the source
   connection.
2. Open only that staging database read/write, start one SQLite transaction and
   run the #166-owned exact schema/row/immutable-guard validator. Validation is
   streaming and does not materialize the lifetime tombstone set.
3. Execute the fixed statement
   `DROP TABLE "local_comparison_expiry_tombstones"` against the staging
   connection. SQLite removes only that table and its table-owned automatic
   index/triggers. No dynamic object name, row copy or best-effort delete is
   permitted.
4. In the same transaction, query `sqlite_schema` and require zero table,
   index or trigger rows whose `name` or `tbl_name` is the exact tombstone table
   name. Commit only after that proof.

Any validation, DROP, residue check or commit failure rolls back and fails the
backup; normal owned-staging cleanup then removes the private destination and no
manifest/archive is published. The live source table and rows remain untouched.
Only after the committed projection does the service reopen the destination
read-only and perform the existing quick check, foreign-key check, inventory,
hash and archive sequence.

The resulting canonical `local-runtime-backup.v1` database member contains no
tombstone table or row, and its manifest contains no corresponding row-count,
cursor, retention or external-state carrier. Inspection rejects a bundle that
contains that table. Restore preview and restore accept this one profile-owned
schema omission without relaxing any other exact-object check. After atomic
restore installation, and under the existing restore/startup lease, the #166
owner creates and validates an empty table before any comparison read or HTTP
readiness. Failure blocks readiness; no old tombstone is reconstructed. A
missing table in an ordinary live source with no accepted restore projection is
still corruption and is not silently repaired.

This rule excludes only `local_comparison_expiry_tombstones`. It does not
authorize exclusion of future #166 comparison snapshot, result or evidence
tables. Before any such operational table ships, #166 must amend this contract
with its exact object names, validation, foreign-key-safe staging drop order,
manifest absence and restore rematerialization/absence behavior. Without that
amendment the new table is not backup-excluded and #166 integration remains
blocked.

The destination is closed and reopened read-only. `PRAGMA quick_check` must be
the single row `ok`; `PRAGMA foreign_key_check` must be empty. Version, count,
cursor, retention, and manifest facts are read from this one destination, not
from later source reads. The captured database's own Retention catalog must also
prove that no non-deleted `sensitive_bundle` or `analysis_sdk_directory` item,
unresolved or orphan/mismatched external capture reservation/journal, or legacy
external-bundle blocker requires external
bytes or source-host recovery. Host proposal/external state is checked before and
after snapshot; a change, active companion state, or unreadable state aborts
publication. The archive is created and inspected completely before
publication. CLI publication uses a same-directory unique `.partial` and
no-overwrite atomic rename. The completed partial is durably flushed before
inspection and atomic rename, and the published file is durably flushed before
success. Failure removes only the exact owned partial/staging files and never
reports partial success.

Every raw-bearing online snapshot, archive partial, and non-restore inspection
stage first receives an exact same-directory `runtime-backup-transient-owner.v1`
marker with a random lowercase-hex basename binding. The fixed marker contains
no path or raw data and is flushed before raw bytes are created. Normal cleanup
removes exact SQLite sidecars/raw bytes first and the marker last. Startup
recovers database-local markers; the next operation touching a caller-selected
archive/output directory recovers markers there. Recovery is bounded, no-follow,
and applies its inventory ceiling only to matching owner-marker namespace
entries, not to unrelated siblings in the same directory. It deletes nothing
for a missing, malformed, nonregular, reparse-bearing, or exclusively active
marker. A lookalike raw file without the exact marker is unowned and preserved.
HTTP upload bytes remain on one delete-on-close handle.

Backup is a read operation on captured business data. Its sanitized receipt may
be appended to the live `runtime_backup_receipts` table after successful
publication; that receipt is intentionally not part of the snapshot it
describes.

## 6. Runtime-backup persistence component

`runtime_backup` v1 is a component-owned migration in the standard
`schema_version` table. Restore recognizes the current Wave 3
`historical_instruction_analysis` v1, `historical_import` v1, and
`sanitized_import` v1 components. Issue #95 appends `pricing` v1 after the
unchanged runtime-backup owner. The fixed migration tail is
`historical_instruction_analysis` -> `historical_import` -> `sanitized_import`
-> `runtime_backup` -> `pricing`, preserving #79 -> #86 -> #88 as an unchanged
subsequence before #95. The runtime-backup owner itself does not reserve or
change Session 14, current Monitor v11 (or its exact supported v10
predecessor), Retention 1, or a Retention store kind. The separate #154 Monitor
v10-to-v11 migration owns the source-compatibility change described above.

### Session 14 component compatibility

The current runtime-backup component vector pins `session:14` and includes the
newly promoted `local_archive:1` and `skill_invocation_snapshot:1`; every other
currently supported component entry is unchanged:

```text
alert_engine:2, alert_lifecycle:1, doctor:1, first_trace_navigation:1,
historical_import:1, historical_instruction_analysis:1,
local_archive:1, local_repository_catalog:1, monitor:11, pricing:1, retention:1,
runtime_backup:1, sanitized_import:1, session:14,
skill_invocation_snapshot:1, skill_projection:1
```

The registered migration order is now:

```text
monitor -> session -> local_repository_catalog -> local_archive -> retention
-> skill_projection -> skill_invocation_snapshot -> doctor -> alert_engine
-> alert_lifecycle -> first_trace_navigation
-> historical_instruction_analysis -> historical_import -> sanitized_import
-> runtime_backup -> pricing
```

Session versions `1..13` are exact supported older versions and preview as
`session:n->14`; `14` is current. Version `15+`, gaps, duplicates,
partial/mixed shapes, unknown Session objects, `session:13` with v14 fact
columns, or `session:14` without the exact fact pair/CHECK are incompatible. If
the component is absent, every Session-reserved object must be absent and
staging creates empty v14. Session remains in the same migration slot.

Backup create requires the exact current-v14 schema, immutable fact pairs, and
aggregate equality before online copy. Inspect/preview compares manifest vector
and actual objects; valid older `1..13` sources, subject to the catalog/pricing
exact-v13 legacy-parent restrictions below, schedule the ordered staging
migration, while contradictions return `restore_incompatible`. Staging uses the
Session owner's same atomic classifier, Retention-authorized content helper,
and one injected staging-time snapshot. Current v14 sources are validated but
never reclassified from content or wall time. Round trip preserves both fact
columns, Session status, and `ended_at` bytes exactly; table-row accounting is
unchanged because v14 adds no table.

`skill_projection:1` is an independent Local Monitor component, not a Retention
kind and not part of `runtime_backup`'s historical Wave 3 tail. Its dependency
position is after Monitor v11/source interpretation and Retention, and before
`skill_invocation_snapshot:1` and the future Workspace projection component:

```text
monitor v11/source base observations
  -> interpretation ledger/head
  -> retention
  -> skill_projection
  -> skill_invocation_snapshot
```

An exact supported older database or backup without the component initializes
an empty `skill_projection:1` and discards the recognized obsolete pre-release
Skill tables/Skill-only marker without copying rows. A partial, newer or
unknown intermediate Skill namespace fails before mutation. This transition
does not delete Session, raw, source-observation, span, Retention or unrelated
component data. Validation requires contiguous interpretation revisions,
matching heads/trace revisions, exact frontier hashes, consistent
generation/queue state pairs, valid desired/current pointers and unique OTel
claim keys. It also validates every SDK claim's exact local Session/Event
foreign key, existing `(source_adapter, source_event_id)` identity, source
surface equality, complete mandatory-application/adapter/normalization/payload-
schema/fingerprint tuple, payload digest and claim uniqueness. Registry
rejection restores the row as non-current rather than rewriting it. The owning
rules are
[Source Compatibility Reconciliation](../layers/source-compatibility-reconciliation.md)
and [Skill Projection](../layers/skill-projection.md).

`skill_invocation_snapshot:1` is likewise independent, is not a Retention kind,
and is registered immediately after `skill_projection:1` and before the future
`local_workspace_projection:3`. Restore staging accepts exact v1 through its supported
v1-to-v2 step and exact v2 through the direct v2-to-v3 step for their atomic
v2-to-v3 migration; runtime readers accept only v3. Its complete contract is owned by
[Skill Invocation Snapshot](skill-invocation-snapshot.md). It owns invocation
index/metadata and equality receipts only. Session Event content remains the
sole raw owner and carries the historical payload document exactly once;
snapshot backup adds no body/path copy, raw column, ZIP member, sanitized
carrier, or empty sanitized marker. OTel-only `not_captured` observations have
no snapshot row to back up.

Runtime backup uses three deliberately distinct Workspace validation contexts.
Live publication (`create`, including online publication) loads the canonical
embedded #154 registry history, acquires the host/private publication gate, and
refreshes with that immutable current-generation authority before validating or
copying the database. Archive `inspect` is structural only: it proves the exact
v3 objects, normalized facts, source identities, and Retention graph/lifetimes,
but does not reinterpret a historical SDK tuple against the executable's current
registry or claim that the tuple is currently authorized. Preview and restore
staging load the same canonical registry implementation used by Local Monitor
and rerun v1/v2-to-v3 or current-v3 Workspace projection with one fixed authority
before comparison or atomic swap. A missing, incomplete, or invalid registry
history is `restore_incompatible` before the first database mutation; it is never
treated as an empty generation. Revoked SDK claims remain stored but cannot
produce a current Workspace fact.

The canonical component DDL artifact is exact UTF-8 without BOM, LF only, one
final LF, 9,213 bytes, and SHA-256
`502f787c28b13363826aeccde96979ed22dc89c8ee137593922b106528935d7c`.
It contains exactly these two tables and eight triggers, and no component-stamp
statement:

```text
skill_invocation_snapshots
skill_invocation_snapshot_receipts

skill_invocation_snapshot_rows_update_rejected
skill_invocation_snapshot_rows_delete_rejected
skill_invocation_snapshot_rows_replacement_rejected
skill_invocation_snapshot_receipts_update_rejected
skill_invocation_snapshot_receipts_delete_rejected
skill_invocation_snapshot_receipts_replacement_rejected
skill_invocation_snapshot_session_event_update_rejected
skill_invocation_snapshot_session_event_delete_rejected
```

The exact stamp is inserted separately and last:

```sql
INSERT INTO schema_version(component,version)
VALUES('skill_invocation_snapshot',1);
```

Session 14 retains its one unchanged core fingerprint. The compile-time child
registry activates only for BINARY-exact component text
`skill_invocation_snapshot` with SQLite integer version `1`. When active, the
Session validator requires both and only both registered child tuples: the two
`skill_invocation_snapshot_session_event_*_rejected` names above,
`type=trigger`, exact target `session_events`, and SQL equal under the existing
Session canonical SQL tokenizer to the registry's delimiter-free installed
`sqlite_schema.sql` values. The executable DDL ends `END;`, whereas each
installed value ends `END`; a terminal-delimiter mismatch is incompatible.
After proving the exact pair, Session filters only that pair before computing
the unchanged Session-14 parent fingerprint. The Retention trigger exemption
is unchanged. Parent success never substitutes for the mandatory complete
child validator.

The registry artifact
`session-child-trigger-extensions-r0001.json` is exactly 1,019 bytes with
SHA-256
`0b5f7782a9686791c2ce9bcff8638dccf1de44833303c0932f05e2ae57259c64`.
The installed-SQL golden is exactly 979 bytes with SHA-256
`546fe44ec0cbdf21b7c55c99f35b1ce30f749ddae4e0e63e3fb02b3ffa9fb251`.
Neither backup nor restore derives a registry entry from database objects or
accepts an unregistered child trigger.

Install is one transaction. Only after exact Session 14, Retention 1, and
`skill_projection:1` validation, and after all earlier components in the fixed
order above are current, it creates both tables and all eight triggers, inserts
the exact stamp last, reruns Session validation with the active child registry,
runs the complete child namespace and empty-graph validator, and commits once.
Any failure rolls back every object and the stamp together. There is no
`INSERT OR REPLACE`, adoption, repair, pending-install bypass, dual Session
validator, or object-only/stamp-only committed state.

Component absence means no stamp and no object whose name or target table is
in the component's reserved namespace, including either Session-target child
trigger, and zero `skill_projection_sdk_claims` rows. An exact supported older
source or backup that is wholly absent installs the empty current component
only after all parents have reached and passed their current validators, then
restores no snapshot rows or SDK claims. A stamp-only,
object-only, partial, extra, case-aliased, changed-SQL, wrong-target,
wrong-storage-type, duplicate or non-integer stamp, current stamp with an
invalid graph, future version, or reserved collision fails closed before
mutation. None is adopted, cleaned, renamed, repaired, or treated as an older
snapshot schema.

Source backup preflight, source restore inspection before any carrier
materialization, post-migration staging, the existing pre-swap staging fence,
and installed validation all run the same complete validator. Besides exact
schema, column, constraint, foreign-key,
append-only trigger, namespace, component-vector, manifest-version, and exact
two-table row-count checks, it proves every row as one closed graph:

- the receipt uniquely selects its snapshot, and every snapshot has exactly
  one receipt, one Session, and one exact `skill.invoked` Event with
  `source_adapter=copilot-sdk-stream`, `source_surface=copilot-sdk`, receipt-
  equal source Event identity, immutable provenance, null local parent/status/
  match-kind and Session terminal outcome/version, and
  `content_state=available`;
- the stored `native_session_id` selects exactly one
  `(session_id,'copilot-sdk',native_session_id)` binding whose unchanged kind is
  `native`, `explicit_resume`, or `explicit_handoff`; `trace_context`, zero,
  or more than one exact selection is invalid, while unrelated/mixed bindings
  are neither enumerated nor reconstructed;
- a null outer native Run has null Event `run_id`; a nonnull outer native Run
  selects exactly one `copilot-sdk` natural-key Run inside the selected Session
  and the Event links that Run. An available snapshot links the same Run; a
  nonavailable snapshot keeps its derived `run_id`/trace/span null even when
  the Event has valid outer identity. Zero or more than one row for a required
  natural key, a cross-Session row, or any link contradiction is invalid;
- every state has one exact Event-content/Retention owner and exact immutable
  provenance, payload/document digests and lengths, capture/create times,
  classification state/reason, snapshot, and receipt. Only
  `available/none` has a #154 SDK claim plus name and body/path facts; every
  nonavailable state has no claim and all mandated derived fields null. In the
  reverse direction, every `skill_projection_sdk_claims` row is referenced by
  exactly one available snapshot whose Session/Event/source/provenance/payload
  tuple and receipt are equal; an orphan, duplicate reference, or claim beside
  an absent/empty snapshot component is incompatible; and
- a live graph has the exact canonical content row and readable Retention item.
  An owner-valid expired/read-denied/cleanup transition may retain that row
  only in the Retention-authorized transitional graph. Content absence is
  valid only with the exact deleted Retention item and tombstone. Every other
  content/Retention/claim combination is incompatible.

The validator also proves that Event-content `captured_at`, Retention-item
`captured_at`, snapshot `captured_at`/`created_at`, and receipt `created_at` are
one exact `write_at`; Event-content and Retention `expires_at` are equal; an
available #154 claim's `created_at` is the same `write_at`; and the Session
creation/update and independent event-time relations remain valid. Restore
never resamples, normalizes, or repairs any of these bytes.

For each receipt, validation reconstructs the exact 29-field semantic frame in
ascending field-ID order with the fixed `NULL`, `UTF8`, `BOOL`, `UINT64`,
`UTC_TIME`, and `SHA256` encodings and compares its lowercase SHA-256 to
`request_fingerprint_sha256`. The fields are, in order:

```text
UTF8("skill-invocation-snapshot-receipt") || 00 || UTF8("v1") || 00
|| U16BE(29) || fields in ascending field ID

field = U16BE(field_id) || kind u8 || U32BE(payload_length) || payload
NULL=00/zero bytes; UTF8=01; BOOL=02/one byte 00|01;
UINT64=03/eight-byte unsigned big-endian;
UTC_TIME=04/33 canonical ASCII bytes; SHA256=05/32 raw digest bytes
```

```text
source_adapter, source_event_id, source_surface, native_session_id,
run_native_id, source_parent_event_id, source_ephemeral, producer trace_id,
producer span_id, occurred_at, literal skill.invoked,
source_application_version, adapter_version, normalization_version,
payload_schema, schema_fingerprint, payload_sha256, payload_bytes, state,
reason, name, source, trigger, body_sha256, body_utf8_bytes,
definition_path_sha256, definition_path_utf8_bytes,
literal application/json, content_document_sha256
```

The canonical golden frame is exactly 726 decoded bytes and hashes to
`5698c710512676dab263596e169be6e73746525a695f67b7929866fbc502cfb7`.

Nullable values use the frame's `NULL` kind, never an empty substitute. The
reconstruction excludes server UUIDs, write/expiry times, Retention ownership,
claim IDs, and response bytes. Validation requires receipt source identity to
equal the Event, snapshot and graph; a stored fingerprint is never accepted as
self-authenticating and no response status/header/entity is restored or
replayed.

Artifact and raw-content validation are byte exact. `payload_schema` is
`github-copilot-sdk.skill-invoked.v1`; `schema_fingerprint` is the SHA-256 of
the exact 980-byte canonical producer schema,
`8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c`.
The sole `session_event_content` value has exact `content_kind=application/json`
and bytes:

```text
UTF8('{"schema_version":"session-event-content.skill-invoked.v1",'
     '"payload_utf8_base64":"')
|| strict RFC4648 base64 of the exact received payload-token bytes
|| UTF8('"}')
```

The validator permits no BOM, LF, alternate alphabet, base64 whitespace, JSON
property reordering, or reconstructed payload. It decodes once with strict
bounds; proves exact decoded `payload_bytes` and `payload_sha256`, complete
payload reclassification, the whole-document `content_document_sha256`,
Event-content and Retention ownership/timestamp equalities, and for an
available row the exact strict-UTF-8 body/path lengths and digests and #154
claim equality. It proves the same closed nullability matrix for every
nonavailable row. A raw-bearing runtime backup therefore carries the canonical
document once through Session Event content and carries only links, metadata,
classification, and the equality receipt in the snapshot tables.

### Local Repository catalog component

The registered `local_repository_catalog:1` component is ordered
immediately after Session and before `local_archive`. The relevant dependency
order is:

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

Its namespace contains every catalog, immutable locator/head, observation,
reconciliation cursor/queue, manual override, assignment revision/history,
Repository history, opaque raw provenance and durable operation-receipt table
defined by [Local Repository Catalog and Session Assignment](local-repository-catalog.md)
and its [DC156-12–19 executable closure](local-repository-catalog-executable.md).
An exact supported older backup without the component initializes an empty v1
catalog. A present partial, newer or unknown table/enum namespace fails closed.
Validation recomputes locator fingerprints and verifies unique ownership,
heads, observation references, overrides, contiguous revisions, exact receipt
bytes and append-only completeness.

A current `local_repository_catalog:1` parent is valid only with exact Session
14. Read-only legacy preflight recognizes one exception only: exact catalog v1
with exact Session 13 and the complete respective legacy shapes. Catalog v1
with Session `1..12`, a partial/mixed Session shape, or any other parent vector
is incompatible before mutation. For that one legacy pair, staging runs the
Session 13-to-14 owner migration first, then invokes the current catalog v1
schema/row validator before any downstream consumer; the catalog never
validates against or opens a child migration while its parent is still v13.

The component can restore without source raw content; retained catalog-owned
metadata survives and provenance availability resolves to `unknown` or
`expired` without reconstructing raw. The whole component is excluded from
sanitized evidence export/import. The complete registered DC156-01–19 contract
is `READY_FOR_IMPLEMENTATION`; backup registration and restore follow that
exact component shape without an intermediate schema.

### Local Archive component

The registered `local_archive:1` component is an ordinary member of the one
SQLite restore unit. Its executable schema, state machine, public wire, and
canonical SQL artifact are owned by [Local Archive v1](local-archive.md). It is
ordered immediately after `local_repository_catalog:1` and before Retention;
`SupportedComponents` contains exactly `local_archive:1`, and
`MigrationOrder` contains `local_archive` in that position. A declared archive
component requires exact `session:14` and `local_repository_catalog:1`, even
when every stored target is a Session. A declared archive with Session 13 has
no legacy-parent exception and is `restore_incompatible`.

Archive-absent migration preserves the complete Session-14 compatibility
matrix above:

- when both catalog and archive are wholly absent, every otherwise valid exact
  Session `1..13`, Session-absent, or Session-14 source first reaches Session
  14, then creates empty catalog 1, then installs empty archive 1;
- a declared catalog 1 accepts exact Session 14 and only the existing read-only
  legacy exception of exact Session 13 with complete legacy shapes. For that
  exception, staging migrates Session first, validates catalog 1 against
  Session 14, and then installs empty archive 1;
- declared catalog 1 with Session `1..12` or Session-absent is incompatible
  before mutation; and
- declared archive 1 always requires declared catalog 1 and exact Session 14.

These are archive-absent installation paths. They do not create a dual archive
parent, compatibility reader, or alternate component order, and every other
D079 dependency and descendant-preservation rule remains unchanged.

Archive absence is exact: there is no `local_archive` stamp and no reserved
archive object, matched ASCII case-insensitively by either object `name` or
`tbl_name`. Only after Session and catalog migration/validation may staging
install the exact empty v1 namespace and report `local_archive:0->1`. Current
means one exact integer v1 stamp, both tables, both indexes, all six triggers,
their exact normalized SQL, valid scalar rows, complete chains and heads,
valid parents, manifest component version, and exact row counts for
`local_archive_current` and `local_archive_events`.

Stamp-only, object-only, partial, missing, extra, case-aliased, changed-SQL,
wrong-target-table, duplicate/non-integer/other-version, reserved-object-
without-declaration, view, virtual-table, hidden/generated-column,
partial-index, or expression-index state is invalid. It is never adopted,
cleaned, repaired, renamed, or migrated as an archive predecessor.

Owned-namespace discovery adds the ASCII case-insensitive prefixes
`local_archive_` and `IX_local_archive_` and examines table, index, trigger, and
view names plus every object's target table name. When and only when archive 1
is declared, the executable trigger allowlist adds these exact
`(name, target table, normalized SQL)` definitions from the archive owner:

```text
local_archive_current_identity_update_rejected     local_archive_current
local_archive_current_delete_rejected              local_archive_current
local_archive_current_insert_replacement_rejected  local_archive_current
local_archive_events_update_rejected               local_archive_events
local_archive_events_delete_rejected               local_archive_events
local_archive_events_insert_replacement_rejected   local_archive_events
```

At source preflight, after staging migration, at the existing pre-swap staging
fence, and during installed validation, archive validation:

1. requires exact SQLite storage types and canonical bytes for every current
   and event scalar;
2. rejects an event without current state and current state without at least
   one event;
3. streams by `(target_kind,target_id,new_revision)` and requires the first
   transition to be `archive 0->1`, every revision increment to be contiguous,
   actions to alternate, and no gap or duplicate;
4. requires head/current revision, action/state, `updated_at`, and
   `archived_at` equality while allowing timestamps to move backward because
   revision is the authority;
5. proves every Session current target against Session and every Repository
   current target through the D081 synchronous Repository-existence authority,
   using nonempty exact-transaction pages of at most 200 IDs, no total target
   cap, and no all-parent-ID materialization; and
6. requires `component_versions.local_archive == 1` and exact manifest
   `row_counts` entries for both archive tables.

Validation repeats these semantic invariants even when the source was written
with CHECK or foreign-key enforcement disabled. Complete current-parent proof
plus complete chain proof covers every event parent.

Archive has two distinct ensure insertion points. `MigrateStaging` runs the
archive ensure after catalog ensure and any conditional legacy restored-lease
normalization, but before Retention initialization, in the same staging
non-deferred transaction. `EnsureCurrentBackupTail` runs archive ensure after
catalog ensure and before runtime-backup/pricing ensure in the current-database
transaction; that path performs neither restored-lease normalization nor
Retention initialization. Component-shape validation holds one supplied
deferred transaction across catalog and archive validation, so the D081
Repository proof observes that exact source or staging database.

There is no archive ZIP member, merge, overlay, target remap, orphan drop,
repair mode, queue, lease normalization, collision resolver, or synthesized
event. Source current state and complete event history replace destination
bytes as part of the whole database. Incoming data after installation cannot
restore a target. Sanitized evidence export/import contains neither archive
namespace nor archive carrier, including for an empty archive component.

Because every valid `sanitized_import` v1 schema is created only after
`historical_import` v1 in the same transaction, a declared `sanitized_import`
component without `historical_import` is an incompatible forged vector rather
than a supported migration source.
A current or post-migration `pricing` component without Session 14,
`alert_engine` v2, or `runtime_backup` v1 is an incompatible forged vector.
Read-only legacy preflight recognizes pricing v1 with an older Session parent
only for exact Session 13 and complete exact legacy shapes; pricing v1 with
Session `1..12` is incompatible before mutation. For the exact v13 pair,
staging migrates Session first and only then applies the current pricing parent
and row validation.

Current backup component vectors record `alert_engine` v2 and `pricing` v1.
An exact older P1 source may omit pricing and may declare alert-engine v1; both
are supported upgrade sources. Restore upgrades alert-engine v1 to v2 before
pricing-dependent startup and preserves each v1 evaluation/receipt/suppression
canonical byte. Future alert-engine/pricing versions, a pricing namespace
without `pricing=1`, an engine-v2 table with a v1 declaration, or a manifest
that disagrees with either component is incompatible before mutation.

Pricing validation calls the shared `PricingSchemaV1.IsValid/ValidateRows`
authority; #88 does not copy its object list or DDL. Every canonical BLOB family
reloads through its owner:
`PricingCatalogSnapshotConsumer`, `CostConfigurationConsumerV1`,
`CostConfigurationPreviewConsumerV1`, `CostConfigurationCommitConsumerV1`,
`CostRecalculationRequestConsumerV1`, and `PricingEstimateConsumer` against its
referenced exact catalog. Stored scalar projections, IDs/digests,
preview/commit request-result bytes and SHA, mandatory one-to-one
commit-to-head relation, commit-request-to-preview-digest scalar equality,
configuration/head/run/target/result/event
sequences, predecessors, and active head ledgers must agree with strict
reserialization. The exact `PricingSchemaV1` trigger definitions—including the
transient-preview owner-delete exception—and manifest row counts must match.
Any mismatch, extra/missing owned object, consumer rejection, or cross-row
inconsistency fails before source/staging mutation. A valid backup/restore
round-trip preserves every pricing table row and canonical byte exactly.
At most 32 preview rows may be present; each has the exact 15-minute expiry
relation. A past expiry is not archive corruption because time may pass while
the database is offline. Backup/restore preserves it byte-for-byte, and the
pricing owner deletes expired preview rows during post-migration startup before
HTTP readiness.

Local Monitor startup is two-phase under one non-waiting restore lease. Before
any owning store opens, phase one recovers exact owned transient/restore state
and rejects malformed, unknown, future, or dependency-invalid component-version
vectors without requiring every component to exist. Existing owning stores then
run their canonical migrations, creating exact alert-engine v2 when absent or
applying the exact v1-to-v2 upgrade that preserves every v1 canonical byte.
Phase two adds and validates `runtime_backup` v1 and then `pricing` v1 before
the HTTP host is built. The two
final ensures share one transaction so a current executable cannot leave a
runtime-backup-only installed database. This sequencing does not replace or
relax the full read-only shape, executable-object, integrity, and external-state
preflight required before a backup or restore migration.

The only component table is `runtime_backup_receipts`. It stores a UUIDv7
operation ID, operation kind (`backup` or `restore`), lowercase artifact
digest, fixed result code, UTC occurrence time, resurrection count, and whether
pre-restore backup was created. It stores no archive bytes, manifest, raw
content, file/directory path, private locator, setup target, credential, token,
or exception text. UUID text is lowercase canonical `D` form with version 7 and
an RFC variant; timestamps are exact 33-byte UTC
`yyyy-MM-ddTHH:mm:ss.fffffff+00:00`; SHA-256 is 64 lowercase hex; counters are
integer `0..2147483647`; and the boolean is integer `0/1`. A backup row must be
`backup_succeeded` with zero reintroduction and no pre-restore backup; a restore
row must be `restore_succeeded`. DDL checks and streaming row validation enforce
the same contract, including rows inserted while SQLite CHECK enforcement was
disabled. Rows are append-only: exact update/delete triggers and a duplicate-ID
`BEFORE INSERT` guard reject `INSERT OR REPLACE` replacement before an existing
receipt can be removed. Unknown version/table shape or invalid row blocks
migration and readiness.

## 7. Inspect and restore preview

Inspection is untrusted-bundle validation. It validates the raw ZIP layout,
canonical manifest, exact file inventory, checksum/size, SQLite header,
`quick_check`, foreign keys, manifest-to-database component versions, row
counts, projection cursors, and retention summary. Validation occurs in a
private sibling staging file only after structural archive validation.
It also validates the staged database's own external-store catalog and rejects
an otherwise canonical/checksum-valid DB-only bundle that claims a live
`sensitive_bundle` or `analysis_sdk_directory` item, unresolved or orphan/mismatched
external capture reservation/journal, or legacy external-bundle blocker. No destination state can
make such a bundle compatible.

External-authority table discovery is ASCII case-insensitive and runs before
and after staging migration. It does not assume `retention_items` already
exists: any reservation, capture/legacy journal, or blocker row whose required
catalog pairing table is absent remains active authority and fails closed.

Before any source or staging write, read-only inspection rejects every SQLite
view and virtual table, every generated/hidden column, every expression index,
and every partial index outside the exact version-bound product-owned index
allowlist. Every trigger outside the corresponding exact trigger allowlist is
also rejected. An allowlisted index or trigger
must have its exact name, target table, and normalized SQL definition. Supported
older schemas may omit triggers that their production migration creates, but
may not substitute or redefine them. This same validation runs again against
the installed database, so untrusted archive code cannot execute through the
restore-receipt write.

Writable-schema objects hidden under the SQLite-reserved `sqlite_*` namespace
are accepted only for the exact built-in table and auto-index shapes. The
`doctor_`, `alert_`, `historical_instruction_analysis_`,
`historical_import_`, `sanitized_import_`, `runtime_backup_`, `pricing_`,
`first_trace_`, `local_archive_`, `IX_local_archive_`, and
`skill_invocation_snapshot` namespaces accept only exact objects owned by a
declared component; absent or extra objects fail closed. Archive and snapshot
discovery also examine each object's target-table name, so a trigger or index
cannot escape a reserved namespace by changing only its own name. The snapshot
allowlist includes the exact six component-table triggers and, only with the
exact v1 stamp, the two Session-target child triggers fixed above.

Schema metadata is guard-first for every table, index, and trigger before a
component-specific validator runs: object/table/column identifiers are limited
to 512 stored bytes, an object definition to 65,536 stored bytes, and the object
inventory to 1,024. Retention summary, coverage, and migration-source TEXT
metadata is limited to 1,024 stored bytes per field (enough for a valid
256-character source identifier at four UTF-8 bytes per character);
receipt/token BLOBs used by coverage must retain their exact 32-byte production
shape. The value guard runs before coverage and migration even for a supported
bundle that does not yet declare the Retention component. Guard discovery uses
SQLite's case-insensitive identifier semantics and then scans the actual table
name, so a case alias cannot be skipped before a production migrator resolves
it. Integrity pragmas are reduced inside SQLite to bounded integer results;
corruption diagnostics and foreign-key table names are never materialized by
the application.

If a component is absent from `schema_version`, its reserved table/trigger
namespace must also be absent. Case aliases such as an undeclared
`RUNTIME_BACKUP_RECEIPTS`, doctor-prefixed objects, alert engine/lifecycle
objects, historical-instruction/import/sanitized-import objects, pricing
objects, first-trace navigation objects, or any archive table/index/trigger
name or archive-targeting object, snapshot table/trigger name, or either
snapshot child trigger targeting `session_events` are `restore_incompatible`;
a production migrator never
adopts or overwrites the collision.

Restore preview additionally compares with the destination database and
returns:

- source/current component versions and explicit migration steps;
- compatible/incompatible state and fixed reason;
- overwrite/new-target state;
- captured and expiry date ranges;
- raw/not-repository-safe/non-purge warnings;
- source external-state inventory and destination prerequisites;
- archive/database checksums;
- projection cursors and counts;
- `monitor_stop_required=true` and `restart_required=true`; and
- resurrection risk count, digest, and confirmation requirement.

Preview is its own terminal branch. It performs immutable inspection and the
existing bounded compatibility/migration preview. After ordered staging
migration and complete Session, catalog, archive, Retention, Skill projection,
snapshot, and remaining component validation, it runs D079 `CompareRetention`
against the unchanged destination and reports the terminal count/digest,
non-terminal reintroduction count/confirmation digest, and whether
resurrection confirmation is required. It then emits only the preview result,
cleans its owned inspection artifacts, and stops. Preview never calls either Retention
reconciliation mutation, mutates the destination, creates a safety backup,
appends a restore receipt, creates or prepares a swap journal, or swaps a
database.

A source component newer than the executable's supported version, an unknown
component, malformed version vector, missing required source shape, or blocked
older migration is `restore_incompatible`. Before invoking any production
migrator, restore performs a read-only preflight of every component version and
the exact minimum shape required to route its migration. An incompatible
future-Monitor or other future-component staging database remains byte-for-byte
unchanged; in particular, restore does not call Retention initialization first
and allow its Monitor DDL to mutate a rejected candidate. Missing older
supported components are created only in staging after this gate. Preview never
mutates the destination.

An exact Monitor v9 archive contains none of the three v10-owned attribution
names: the evidence table, its named trace index, and the durable queue. Any
empty, populated, exact-shaped, or malformed collision is
`restore_incompatible` during read-only preflight and is never advertised as a
supported migration. For an exact v9 source, staging creates the evidence
authority and durable queue. Source-attribution evidence may be persisted from
a raw payload only when its canonical Retention entry still authorizes reading
and its ownership receipt matches; this includes authorized unprojected backlog
so its first v10 projection can use that evidence. Rewriting existing projected
Monitor attribution additionally requires complete, exact ordinal/trace/span
projection membership. Historical migration leaves the queue empty.
Incomplete, missing, expired, or read-denied evidence retains the archived
projection values. A restored v10 database must pass the current
authority-shape gate, and a repeated current startup is state/byte idempotent.

Retention v1 preflight requires the production ancillary reservation, member,
capture/legacy journal, and blocker column sets. Staging then runs the
production Retention initializer idempotently even when the declared version is
already v1, so malformed ancillary tables cannot be installed as
`database_ready`.

## 8. Tombstones, reconciliation, and explicit non-terminal reintroduction

The comparison identity is the exact retention tuple
`store_instance_id/store_kind/source_item_id`. Current terminal tombstones and
every current irreversible `read_denied_at` state are authoritative over the
archive. Before swap the service deterministically reconciles those exact
current lifecycle/ownership/revision/timestamp rows and their mutation audit
receipts into staging. For a current `deleted` item it also proves the staged
raw source is physically absent, deleting only the exact receipt-bound SQLite
source when necessary. The installed database must retain the current
tombstone/read denial and must not contain readable/restored raw bytes.

If exact lifecycle, ownership receipt, item identity, source removal, or audit
reconciliation cannot be proven transactionally, restore fails
`restore_tombstone_reconcile_failed`. Confirmation can never override this
failure or authorize dropping a current tombstone. No content, item ID, source
ID, locator, or path is emitted. Preview reports only the reconciliation count
and digest.

Reconciliation is keyset-paged and applies a semantic materialization limit
before reading any relevant SQLite value into managed memory. A relevant TEXT
or BLOB cell is limited to 1,048,576 stored bytes, including TEXT bytes after
an embedded NUL, and a row to a 2,097,152-byte stored-value budget. Exceeding any limit fails
`restore_tombstone_reconcile_failed`; it never truncates, substitutes, or
partially reconciles the row.

A separate opt-in applies only to a non-terminal reintroduction: the current
catalog item remains readable/non-terminal but its exact receipt-bound SQLite
source is physically absent, while staging would reintroduce that source. This
cannot weaken a tombstone because none exists. Preview returns only
`non_terminal_reintroduction_count`, a sorted-identity digest, and a
confirmation digest derived from the archive SHA-256, current comparison
digest, count, and domain
`local-runtime-restore-non-terminal-reintroduction-confirmation.v1`. Restore may
proceed only when both `--allow-resurrection` and the exact current digest are
provided. A changed archive or destination invalidates confirmation. The opt-in
is recorded in the sanitized receipt count and never resets original capture,
policy, expiry, or TTL data.

Without that paired opt-in, non-terminal reintroduction fails
`restore_resurrection_blocked`. The service never silently merges away,
recreates, or weakens a current tombstone or read denial.

## 9. Offline restore, atomic swap, and rollback

There is no HTTP restore endpoint. Every CLI path argument must already be a
host-native, fully qualified local file path. Relative, drive-relative,
current-drive-rooted, URI, UNC/network, device, foreign-platform lexical, and
reparse-ancestor paths are rejected before filesystem I/O. Windows rejects DOS
device basenames (`NUL`, `COM1.txt`, and the complete reserved set) in every
segment plus trailing-dot/space aliases. Unix rejects embedded Windows
separators and native character/block devices, FIFOs, sockets, and symlinks by
no-follow native type inspection. CLI restore first proves the monitor is
stopped by acquiring bounded exclusive database ownership and rejecting active
SQLite write/sidecar state with `monitor_must_be_stopped`.

A private sibling `<database>.runtime-restore.lock` lease spans recovery, staging,
pre-restore backup, swap, installed validation, and receipt persistence. Normal
Local Monitor initialization acquires the same non-waiting lease, so a new
monitor cannot start during restore. This lease is the portable ownership proof
for product processes. For an existing target, restore also performs a normal
SQLite locking probe, checkpoints and rejects remaining `-wal`/`-shm` state,
and on Windows holds a read/delete-sharing database guard through the atomic
swap. On Windows, the sharing guard rejects incompatible non-product handles.
On Unix, no non-product SQLite connection state is part of the supported
restore boundary; the operator must close every non-product database client.
The normal SQLite probe detects conflicting exclusive ownership but does not
claim to enumerate shared, reserved, or idle connections. The exact original
target hash and external state are checked again immediately before swap.

The state machine is:

1. recover owned state, acquire offline destination ownership, and complete
   destination current-component preflight and external-state validation;
2. perform bounded structural ZIP validation and compute the exact archive
   hash;
3. create and flush `runtime-restore-journal.v2` in its `staging` phase before
   any staging file, binding a random operation nonce to one exact bounded
   sibling staging basename, the archive digest, and the unchanged target
   identity/hash;
4. extract the manifest and database only through that journal-bound sibling,
   then complete manifest/source-database preflight;
5. apply supported staging migrations in fixed order: ordered Session
   `1..12 -> ... -> 13` and exact atomic `13 -> 14`, catalog ensure/validation,
   archive validate-or-empty-v1 installation, Retention, Skill projection,
   snapshot validate-or-empty-v1 installation, then all later components.
   Alert engine reaches v2 without reserializing a v1 row before
   lifecycle validation, and the fixed tail remains
   `historical_instruction_analysis -> historical_import -> sanitized_import
   -> runtime_backup -> pricing`;
6. completely revalidate the staging archive namespace and full database;
7. run D079 `CompareRetention` against the unchanged destination;
8. when any non-terminal reintroduction exists, require both
   `--allow-resurrection` and the exact current confirmation digest before any
   reconciliation;
9. run `ReconcileTerminal` into staging, including exact
   ownership/lineage/source-removal proof for current `deleted` or
   `read_denied_at` authority;
10. run `ReconcileNonTerminal` into staging only for the admitted exact
    non-terminal reintroduction set;
11. create and validate a private pre-restore `local-runtime-backup` of the
    unchanged destination by default when it exists;
12. append the sanitized operation-bound restore receipt inside staging,
    checkpoint and close its SQLite transaction, completely revalidate the
    staging archive/full database, and flush it;
13. replace the journal with its `prepared` phase and exact staged hash;
14. revalidate unchanged destination identity/hash and external state;
15. atomically replace the target while retaining an exact sibling rollback
    file, or atomically rename for a new target;
16. read-only validate the installed database, exact expected archive,
    component, cursor, Retention, invariant, and Doctor facts;
17. durably replace the journal with `installed` and then its hash-bound
    `committed` phase without opening the target for write, remove the exact
    rollback file first and the journal last, and report
    `restore_succeeded`.

Structural ZIP layout/size validation and archive hashing precede journal
creation. Manifest/database extraction and complete compatibility preflight
occur only after the journal durably binds that exact hash and staging
basename. The pre-swap live-destination gate rechecks only destination
identity/hash/external state; it does not invent another archive-fact read.

The D079 Retention subsequence is unchanged and exact. Missing or mismatched
non-terminal confirmation fails `restore_resurrection_blocked` before either
reconciliation. `ReconcileTerminal` always precedes `ReconcileNonTerminal`.
Any comparison or reconciliation contradiction is
`restore_tombstone_reconcile_failed`, creates no safety backup or receipt, and
leaves the destination byte-identical. Both reconciliations finish before
safety-backup creation and before the operation-bound staging receipt.

The private safety-backup step does not invoke the live-target mutation path of
normal backup. It read-only preflights and online-copies the unchanged target
into a private owned
snapshot, then applies the same fixed migration order as restore staging,
including Session, catalog, archive, Retention, Skill projection, snapshot, and
the remaining components. It runs the same current component/row validation
after each dependency reaches current. A current destination archive is
preserved byte-for-byte, including
current rows and complete history; a valid older destination with archive
wholly absent receives empty archive v1 only in the migrated private copy. The
safety-backup manifest, component vector, database hash, archive hashes, and
published bytes derive only from that validated copy. No receipt is appended to
the live target, which remains byte-identical through safety-backup construction
and every failure before atomic swap. Installed validation after swap is
read-only; only the journal records installed/committed state.

Only this private safety copy, and only after every required terminal
reconciliation and non-terminal confirmation gate for the restore has passed,
uses the existing Retention restorable-coverage validator during that fixed
migration. A catalog-backed source proof of exact `Match` counts normally;
exact `Missing` preserves the already-accepted source absence. A receipt or
owner mismatch, a source without a catalog row, malformed schema/row/foreign
key state, or any other proof fails closed. This path never terminalizes an
item, synthesizes or copies raw bytes from incoming staging, or changes
lifecycle, receipt, revision, timestamps, expiry, or TTL. Ordinary startup,
adoption/backfill, normal backup, and ordinary reads retain their strict current
validators.

The snapshot component adds no writable-adoption, fixed-migration, or safety-
archive exception to this D079 Retention subsequence. A private safety copy
uses the same absent/current/future snapshot classification and the same
stamp-last empty install or complete current validation described above.

An extracted archive that declares the complete exact current component vector
and passes current schema, row, object, integrity, and foreign-key validation
does not rerun Retention writable adoption/backfill merely to reinitialize
current v1. Restore staging uses select-only current validation plus the
existing restorable-coverage validator, then performs the existing restore-only
lease normalization and reconciliation. Only exact source absence without
bytes survives: a present owner/receipt mismatch, an extra source without a
catalog row, or malformed state cannot be reclassified as `Missing`. Any absent
or older component that requires migration or adoption remains on strict
writable backfill.

The bounded sibling journal schema is `runtime-restore-journal.v2`. It binds one
UUID operation ID, archive digest, a derived random sibling staging basename,
whether the target existed, exact old/staged/rollback hashes, and the closed
phases `staging`, `prepared`, `installed`, and
`committed`. A `.commit` file is only the same-operation replacement candidate;
unknown, corrupt, reparse-bearing, or hash-mismatched control artifacts are
retained and fail closed.

The target is never opened for a SQLite write after swap, so it cannot acquire a
new rollback journal or WAL in the recovery window. Recovery nevertheless
rejects any target `-journal`/`-wal`/`-shm` before hashing or replacing the
target. Any failure before step 15 leaves the target byte-for-byte unchanged. Any
pre-swap domain result is returned only after its owned stage/journal cleanup is
verified; cleanup failure instead returns `restore_rollback_failed` and retains
the exact recovery controls. Any
failure after step 15 but before the flushed `committed` marker restores the
rollback file atomically and validates the old target before reporting
`restore_rolled_back`. If rollback cannot be
validated, the fixed result is `restore_rollback_failed`; recovery artifacts
remain private for operator repair and are never deleted or logged with paths.
Startup recognizes only the bounded #88 sibling names. A reserved-looking stage
or sidecar without a strictly valid owner journal is unowned, retained, and
fails closed. A valid `staging` journal may remove only its exact nonce-bound
stage and SQLite sidecars after proving the target is still the journal-bound
old target (or absent); a prepared/installed operation without a committed marker rolls back to the
old target (or absence for a new target). Only a flushed `committed` journal
with its exact operation-bound restore receipt and installed hash may recover
forward. Recovery revalidates those facts before deleting the exact rollback,
then deletes the journal last. If committed-state validation fails and the
exact verified rollback remains, recovery restores and validates the old target
instead. An interrupted response after journal deletion is already a committed
success. Recovery never guesses a path from archive content.

The documented installed-runtime sequence is exact and conditional: `stop.ps1`
must succeed; the extracted release's packaged
`app/config-cli/CopilotAgentObservability.ConfigCli.exe` performs restore; only
a restore process exit code captured immediately as zero permits
`start.ps1 -Mode Published -WaitReady`. The operator passes the intended `Url`,
`DbPath`, `InstallRoot`, and `SanitizedOnly` values explicitly because stop
removes ephemeral process state. A nonzero restore never invokes start. The
Published start succeeds only when `/health/ready` returns canonical `ready` or
accepted `degraded`; `not_ready` or an unreachable endpoint fails. The CLI's
database readiness and Doctor store checks do not replace this post-restart
HTTP evidence. No additional public restore wrapper is defined.

## 10. CLI contract

Commands are:

```text
config-cli runtime-backup create --database <monitor.db> --output <bundle.zip>
config-cli runtime-backup inspect --bundle <bundle.zip>
config-cli runtime-backup preview --bundle <bundle.zip> --database <monitor.db>
config-cli runtime-backup restore --bundle <bundle.zip> --database <monitor.db> [--pre-restore-output <bundle.zip>] [--allow-resurrection --confirmation <digest>]
```

All stdout results are one canonical JSON object. Stderr contains only one
fixed code on failure. Exit `0` is success, `2` invalid input, `3` blocked
confirmation/incompatible state, `4` monitor/external prerequisite unavailable,
and `5` I/O/store/archive/internal failure. Output never contains a raw value,
local path, private locator, credential, token, or exception message.

If `--pre-restore-output` is absent, the default is a generated file under the
destination database directory's private `runtime-backups/` directory. Result
JSON reports only `pre_restore_backup_created`, its digest, and basename, never
an absolute path. Existing outputs are never overwritten.

## 11. Loopback API and UI

Local Monitor exposes:

- `POST /api/runtime-backup/v1/backups` with exact JSON `{}`;
- `GET /api/runtime-backup/v1/backups/{backup_id}`;
- `GET /api/runtime-backup/v1/backups/{backup_id}/archive`; and
- `POST /api/runtime-backup/v1/previews` with `application/zip` body.

The UI route `/backup-restore` can create/download an online backup and inspect
a selected archive. It shows the raw/not-repository-safe/non-purge warnings,
compatibility, versions, counts/date ranges, external prerequisites,
resurrection state, and the exact offline CLI requirement. It cannot execute a
restore or upload elsewhere.

Because this surface reads or downloads raw-bearing runtime data, all four API
routes and `/backup-restore` are absent (`404`) in `--sanitized-only` mode. The
gate runs before request-body reads and backup-store access. The installed
pre-v1 host exposes the page through its legacy overview/diagnostics
affordance. Local Monitor v1 instead opens this focused flow from Unified
Settings and has no permanent sidebar.

All routes are loopback/Host-header validated, same-origin, `Cache-Control:
no-store`; POST requires `x-monitor-csrf: local-monitor`. Backup create accepts
no server output path. IDs are opaque archive digests. Downloads are available
only for archives produced in the process-owned `runtime-backups/` directory
and are revalidated before delivery. API/UI never returns a local path.

Fixed primary errors include:

| Code | HTTP |
| --- | --- |
| `request_invalid`, `archive_invalid`, `manifest_invalid` | `400` |
| `cross_origin_forbidden`, `csrf_required` | `403` |
| `backup_not_found` | `404` |
| `unsupported_media_type` | `415` |
| `request_too_large`, `bundle_too_large` | `413` |
| `restore_incompatible`, `restore_resurrection_blocked`, `restore_tombstone_reconcile_failed`, `external_raw_store_active`, `external_runtime_state_active`, `external_runtime_state_unknown`, `external_runtime_state_unsafe` | `422` |
| `snapshot_store_busy`, `snapshot_store_unavailable`, `publish_failed` | `503` |

There is intentionally no remote/non-loopback restore API.

## 12. Validation matrix transition

Issue #88 removes only the `backup-restore` future placeholder from the #91
future registry and publishes active rows at
`docs/sprints/issue-88-backup-restore/validation-matrix.json`:

- `91-B-088`: backup/WAL/manifest/checksum/online publication;
- `91-S-088`: archive attacks, tombstone/confirmation, atomic rollback,
  no-leak and external-store fail-closed cases; and
- `91-L-088`: genuine cross-machine restore, restart readiness, and Doctor.

Automated synthetic source/destination-directory testing is not second-machine
evidence. `91-L-088` remains `blocked_external` until a real second-machine run
exists. Matrix validation and leak scanning operate on sanitized receipts and
ledgers only; a raw backup is never copied into repository evidence.

The automated rows include Session v13-to-v14 restore and safety-snapshot
fixtures with Event content, Retention catalog/receipt children, installed
Skill projection descendants with Session-bound OTel invocation/inventory
claims and no unpromoted SDK claim rows, exact catalog-v1/pricing-v1 Session-13
legacy pairs, rejection of those children with Session 1..12, and proof that
the safety manifest/hash comes from the migrated private copy while the live
target remains byte-identical.
