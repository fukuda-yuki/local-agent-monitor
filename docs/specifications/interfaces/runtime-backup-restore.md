# Runtime Backup And Restore Interface

Status: Accepted for Issue #88 (2026-07-23)

This specification defines the Local Monitor runtime backup and restore v1
contract. It is independent from the repository-safe sanitized evidence export
defined by Issue #85.

## 1. Scope and profile

The fixed bundle profile is `local-runtime-backup`. A bundle contains the
complete supported Local Monitor SQLite restore unit, including raw telemetry,
Session content, policy state, tombstones, mutation/audit state, projections,
and component versions captured in one SQLite snapshot.

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
component names unique after merge. `row_counts` records every non-SQLite
table, ordered by table name. `projection_cursors` records only bounded
sanitized cursor/high-water integers or null; it never records raw IDs.

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
byte-for-byte unchanged. It then ensures `runtime_backup` component v1 exists.
It opens the source with pooling
disabled and a bounded busy timeout, creates a same-directory private temporary
SQLite file, and invokes `SqliteConnection.BackupDatabase`. It never copies a
live database file or its `-wal`/`-shm` sidecars.
The live source and every current or installed database use normal SQLite
locking and change detection, including while restore owns its lease. SQLite
`immutable=1` is restricted to closed, service-owned snapshot/staging files
whose sidecars are absent; it is never selected merely because a live path
happens to have no sidecar at one instant.

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
`sanitized_import` v1 components. Its fixed migration tail is
`historical_instruction_analysis` -> `historical_import` -> `sanitized_import`
-> `runtime_backup`, preserving the storage-owner integration order
#79 -> #86 -> #88. It does not reserve or change Session 13, Monitor 7,
Retention 1, or a Retention store kind.
Because every valid `sanitized_import` v1 schema is created only after
`historical_import` v1 in the same transaction, a declared `sanitized_import`
component without `historical_import` is an incompatible forged vector rather
than a supported migration source.

Local Monitor startup is two-phase under one non-waiting restore lease. Before
any owning store opens, phase one recovers exact owned transient/restore state
and rejects malformed, unknown, future, or dependency-invalid component-version
vectors without requiring every component to exist. Existing owning stores then
run their canonical migrations. Phase two adds and validates only
`runtime_backup` v1 before the HTTP host is built. This sequencing does not
replace or relax the full read-only shape, executable-object, integrity, and
external-state preflight required before a backup or restore migration.

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
`historical_import_`, `sanitized_import_`, `runtime_backup_`, and
`first_trace_` namespaces accept only exact objects owned by a declared
component; absent or extra objects fail closed.

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
objects, historical-instruction/import/sanitized-import objects, or first-trace
navigation objects are `restore_incompatible`; a production migrator never
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

1. structurally inspect archive;
2. create and flush `runtime-restore-journal.v2` in its `staging` phase before
   any staging file, binding a random operation nonce to one exact bounded
   sibling staging basename, the archive digest, and the unchanged target
   identity/hash;
3. extract the database to that journal-bound unique sibling staging file;
4. validate checksums, compatibility, integrity, foreign keys, retention
   invariants, terminal reconciliation, and non-terminal reintroduction policy;
5. apply supported component migrations to staging in the fixed integration
   order, including `historical_instruction_analysis` v1,
   `historical_import` v1, `sanitized_import` v1, then `runtime_backup` v1;
6. create and validate a pre-restore `local-runtime-backup` by default when the
   target exists;
7. append the sanitized, operation-bound restore receipt inside staging, close
   and checkpoint its SQLite transaction, then revalidate and flush staging;
8. replace the journal with its `prepared` phase and the exact staged hash;
9. atomically replace the target while retaining an exact sibling rollback
   file, or atomically rename for a new target;
10. validate the installed DB, exact expected component/cursor/Retention facts,
   invariants, and Doctor store again;
11. durably replace the journal with `installed` and then its hash-bound
    `committed` phase without opening the target for write, then
    remove the exact rollback file first and the journal last; and
12. report `restore_succeeded`.

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
target. Any failure before step 9 leaves the target byte-for-byte unchanged. Any
pre-swap domain result is returned only after its owned stage/journal cleanup is
verified; cleanup failure instead returns `restore_rollback_failed` and retains
the exact recovery controls. Any
failure after step 9 but before the flushed `committed` marker restores the
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
gate runs before request-body reads and backup-store access. Raw mode exposes
the page only through an existing overview/diagnostics affordance; it does not
add a third permanent navigation item.

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
