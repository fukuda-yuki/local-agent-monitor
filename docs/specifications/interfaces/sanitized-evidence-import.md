# Sanitized Evidence Import Interface

This specification defines Issue #86's transactional import of the frozen
Issue #85 sanitized-evidence bundle. It is an import of repository-safe
evidence, not raw telemetry replay, database restore, or producer attestation.

## Accepted Input

V1 accepts only the complete frozen `sanitized-evidence` archive contract:

- `sanitized-evidence-manifest.v1`;
- `sanitized-evidence-bundle.v1`;
- `sanitized-evidence` bundle profile;
- `sanitized-evidence-canonical-json.v1` members;
- `sanitized-evidence-zip-store.v1` archive; and
- `sha256.v1` checksums.

The only accepted record types are the Issue #85 carriers owned by #58, #59,
and #80: `repository_metadata_projection`, `instruction_finding_handoff`, and
`alert_receipt`. The importer invokes the exact Issue #85 archive inspector
before reading any member for migration or persistence.

V1 rejects archive traversal, absolute or noncanonical paths, duplicate names
or record identities, symlink/external attributes, comments/extra fields/data
descriptors, compression, zip-bomb/ratio attempts, entry or byte limits,
forbidden entries, malformed or noncanonical JSON, producer-contract
violations, checksum/inventory/order/count mismatches, and scanner failures.
Any compression is forbidden, so there is no accepted compression ratio.
Archive bytes are bounded to 128 MiB, entries to 256 including the manifest,
records to 255, and an individual canonical record to 8 MiB; the #59 carrier
retains its lower 1 MiB consumer limit.

Unknown/future manifest, bundle, profile, serialization, scanner, producer, or
compatibility versions fail closed as `schema_unsupported`. V1 has no older
published bundle predecessor. The deterministic migration chain is therefore
the lossless identity projection
`sanitized-evidence-bundle.v1 -> sanitized-import-store.v1`, version 1. Its
fixed step name and SHA-256 chain hash are included in every preview and import
receipt. A future older-bundle migration requires an explicitly specified,
tested chain; it must never be inferred, lossy, or permissive.

Successful structural validation proves internal canonical consistency and
the repository-safe scanner result only. It does not prove bundle origin,
signature, authorization, source-store provenance, or that a source claim was
true.

## Preview Contract

`sanitized-import-preview.v1` is computed without writing import state. It
contains:

- the archive SHA-256 and a deterministic preview digest;
- manifest/profile/bundle versions and source Local Monitor/agent versions;
- source creation/date range, selection, labels, record counts, capabilities,
  completeness/content/retention distributions, and processing versions;
- compatibility state and the fixed migration version/name/hash;
- exact new, duplicate, and conflict counts;
- bounded conflict identities and incoming/existing canonical SHA-256 values;
- manifest-declared `missing`/`external` dependencies and exact graph
  unresolved counts;
- expected record, origin, node, edge, history, and raw-retention-item effects;
  and
- `can_commit`.

The preview identifier is the archive SHA-256. The preview digest binds the
validated archive, migration chain, exact record classifications, graph
projection, and current imported-record state. It contains no database path,
raw body, credential, local path, or server path. Validation failure returns a
fixed error code and no partial preview. A same-ID/different-bytes conflict
returns a successful non-committable preview; it never silently selects a
winner.

## Identity, Deduplication, And Conflict

Record identity is the exact ordinal pair `(record_type, record_id)`. The local
stable record identifier is the domain-separated SHA-256 of that pair. Content
identity is the manifest-verified SHA-256 of the exact canonical member bytes.

- absent identity: import the record;
- same identity and same canonical hash/bytes: duplicate, skip the record;
- same identity and different canonical hash/bytes: conflict, reject the whole
  import; and
- any approximate, repository-, workspace-, timestamp-, label-, content-, or
  graph-neighborhood match: prohibited.

The importer stores canonical sanitized evidence in component-owned tables. It
does not update Session, monitor, raw, instruction-finding-owner, alert-engine,
or alert-lifecycle rows and therefore cannot overwrite stronger evidence. A
duplicate may add an exact bundle-origin link only when the containing import
commits successfully.

## Evidence Graph And Provenance

The import projection preserves exact source identifiers. Domain-separated
local node and edge IDs are storage keys, while `node_kind`, `source_id`,
`record_type`, and `record_id` retain the source identity verbatim.

- #58 records define their exact session and trace nodes when present.
- #59 records define the analysis run, finding, candidate, and candidate-to-
  finding relations, and preserve opaque session/trace/span/turn references.
- #80 records define the alert/evaluation identity and preserve exact session,
  trace, span, turn, event, and tool-call evidence references.
- manifest `known_missing_evidence` entries remain explicit `missing` or
  `external` record references.

An edge resolves only when its exact kind and exact source ID has a definition
in the validated bundle or the imported sanitized-evidence store. Otherwise it
remains explicit `external`/`missing`. Repository, workspace, time, array
position, similar text, hash prefix, or raw-store proximity never resolves an
edge. Every record has an immutable origin link carrying archive hash, entry
path, source snapshot ID, source Monitor version, and import receipt ID. Graph
and origin inserts are in the same transaction as records and history.

Stored canonical JSON is rendered only through framework encoding or DOM
`textContent`; it is never executed as markup or script.

## Transaction And History

Commit accepts the exact archive bytes plus the preview digest. It opens one
SQLite transaction, revalidates the archive, recomputes dedup/conflict and the
preview digest inside that transaction, and then either commits every record,
origin, graph node/edge, and history row or commits nothing. A changed preview
returns `preview_changed`. A conflict returns `record_conflict`. Busy or store
failure returns a fixed unavailable error and leaves no visible partial import
or receipt.

The import ID is the archive SHA-256. Recommitting the same archive returns the
existing receipt with `idempotent_replay=true` and creates no second history,
record, origin, node, or edge. A different archive containing identical
records creates its own history/origin links but skips identical record and
graph storage. History is append-only, ordered by imported time then import ID,
bounded to 100 rows per read, and contains only sanitized metadata, counts,
hashes, versions, and fixed states.

## Persistence And Migration

Issue #86 owns the independent SQLite schema component
`schema_version.component = 'sanitized_import'`, version 1. It owns only the
`sanitized_import_*` tables and indexes. Fresh databases and databases with the
supported monitor v7 / Session v13 version vector add this component in one
transaction without changing those component versions or data. A stamped
future version, absent stamp with pre-existing import tables, invalid table or
index shape, or failed foreign-key check is rejected before writes.

This is Wave 3 storage-owner sequence 2, after Issue #79. It intentionally does
not consume Session v14 or another shared migration number because sanitized
imports do not mutate Session identity. If #79 remains component-scoped,
migration renumbering after rebase is `not_applicable`; the primary integration
owner still rebases in `#79 -> #86 -> #88` order, audits shared files/version
vectors, and reruns fresh-database and supported-upgrade tests.

## Retention

Imported sanitized records, graph rows, provenance, and receipts are retained
outputs, not raw store kinds. Import creates exactly zero `retention_items` and
does not copy, extend, restore, or synthesize source raw-retention authority.
Source `retention_state_distribution` and #58 record labels are preserved as
source metadata only. The Issue #90 exact-target resolver treats an imported
sanitized reference as `retention_target_not_applicable`; deletion of source
raw data does not delete or recreate these sanitized import rows.

## Public CLI And Loopback Surfaces

Config CLI exposes:

```text
config-cli sanitized-import preview --database <monitor.db> --bundle <bundle.zip>
config-cli sanitized-import import --database <monitor.db> --bundle <bundle.zip> --preview-digest <sha256>
config-cli sanitized-import history --database <monitor.db> [--limit <1..100>]
```

JSON output uses the shared preview/result/history projection. Exit `0` means
success (including a non-committable conflict preview), `2` means validation or
conflict, and `3` means store/I/O unavailable. Paths and input fragments are
never echoed.

Local Monitor exposes:

- `GET /sanitized-import`;
- `POST /api/sanitized-import/v1/previews` with `application/zip` body;
- `POST /api/sanitized-import/v1/imports` with `application/zip` body and
  required `X-Sanitized-Import-Preview-Digest` header;
- `GET /api/sanitized-import/v1/imports?limit=<1..100>`; and
- `GET /api/sanitized-import/v1/imports/{import_id}`.

The loopback API inherits Host-header validation, rejects cross-site reads and
writes, requires `x-monitor-csrf: local-monitor` on both POST routes, accepts
only `application/zip`, streams into a bounded buffer, disables CORS, and
returns `Cache-Control: no-store`. Fixed status mapping is: `200` preview/list/
detail/idempotent result, `201` first commit, `400` invalid request/digest,
`403` Host/origin/CSRF, `404` unknown import, `409` conflict or changed preview,
`413` archive limit, `415` media type, `422` strict archive/schema/producer/
scanner rejection, and `503` busy/unavailable/transaction failure.

The Japanese UI provides file selection, preview, explicit commit, progress,
result/error, provenance badge, and bounded import history. Commit remains
disabled until a committable preview succeeds. Selecting another file clears
the bound preview. The UI does not persist bundle bytes/digests in browser
storage and never injects captured values as HTML.

## Explicit Exclusions

V1 never imports raw OTLP, Session content, raw analysis, source/file bodies,
prompts/responses/tool bodies, a SQLite database, backup/restore material, #72
historical datasets, #83 lifecycle state, #73/#74/#84 future carriers, or an
unknown export profile. It provides no background scan, automatic import,
cloud upload, signing/origin attestation, encryption, heuristic reconciliation,
conflict overwrite, backup restore, or embedded-content execution.
