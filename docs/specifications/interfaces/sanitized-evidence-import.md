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
`alert_receipt`. Each public preview/commit boundary copies caller-owned archive
bytes exactly once into a private snapshot. Inspection, member reads, archive
SHA-256, preview binding, and transaction-local reinspection all use that same
snapshot even if the caller subsequently mutates its array. The importer invokes
the exact Issue #85 archive inspector before opening the database or reading any
member for migration or persistence, and commit invokes the same inspection
again inside its write transaction.
Inspector exceptions and hostile duplicate manifest-map keys fail closed with
a fixed archive or manifest error; no exception or partially parsed map escapes
the boundary.

V1 rejects archive traversal, absolute or noncanonical paths, duplicate names
or record identities, symlink/external attributes, comments/extra fields/data
descriptors, compression, zip-bomb/ratio attempts, entry or byte limits,
forbidden entries, malformed or noncanonical JSON, producer-contract
violations, checksum/inventory/order/count mismatches, and scanner failures.
For every member, including `manifest.json`, the inspector recomputes CRC32 from
the exact stored data and requires it to match both local and central headers.
Each raw filename byte sequence must strict-decode as UTF-8 and exact-reencode
to the same bytes; malformed, truncated, replacement-decoded, or differently
encoded names fail closed, while canonical Unicode names remain valid.
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
- exact eligible, new, updated, skipped, rejected, duplicate, conflict, and
  graph-state-update counts;
- bounded conflict identities and incoming/existing canonical SHA-256 values;
- the exact manifest-declared `missing`/`external` dependency count and a
  bounded declaration list as `manifest_declaration_count` and
  `manifest_declarations`, separately from the exact current destination state
  in `unresolved_reference_count` and `unresolved_references`;
- expected record, origin, node, declaration, graph-state-update, edge,
  history, and raw-retention-item effects;
  and
- `can_commit`.

Count invariants are fixed: `new_records + updated_records + skipped_records +
rejected_records = eligible_records`; `duplicate_records` is the exact-identical
subset of `skipped_records`; and `conflict_records` is the identity/content-
conflict subset of `rejected_records`. V1 never overwrites an existing record,
so `updated_records` is normally zero. `graph_state_updates` is separate and
counts only existing global graph nodes promoted from unresolved to defined.

The preview identifier is the archive SHA-256. The preview digest binds the
validated archive, migration chain, exact record classifications, graph
projection, and current imported-record state. It contains no database path,
raw body, credential, local path, or server path. Validation failure returns a
fixed error code and no partial preview. A same-ID/different-bytes conflict
returns a successful non-committable preview; it never silently selects a
winner. Conflict, manifest-declaration, and unresolved detail arrays each
contain at most 256 rows; their separate count fields remain exact when
additional rows are omitted. Previewing an already imported archive is not a
history shortcut: the same full receipt/record/origin/graph/declaration
integrity matrix required by replay commit is validated first, and corruption
returns `import_integrity_failed`.

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
  finding relations. Every anchor, evidence, and provenance token lives in a
  carrier-specific opaque namespace distinct from #58 actual session/trace
  identity and can never re-resolve against a later #58 definition.
- #80 records define the alert/evaluation identity. Each evidence node retains
  the exact canonical tuple of kind, evidence ID, session, trace, span, turn,
  event, tool call, and observed time; evidence-context nodes are scoped to that
  tuple rather than keyed by a bare child ID. Receipt-level session/trace links
  are the only #80 links allowed to use #58 actual-ID namespaces.
- manifest `known_missing_evidence` entries remain explicit `missing` or
  `external` record references.

An incoming edge resolves only when its exact kind and exact source ID already
has a definition in the validated bundle or imported sanitized-evidence store.
Otherwise its import-time resolution remains explicit `external`/`missing`.
Global graph nodes record only `defined` or `unresolved`. A separate per-import
declaration row preserves each incoming unresolved node's exact `missing` or
`external` state. A later exact definition promotes the global node and is
reported as one graph-state update, but it never rewrites prior declarations or
edge resolution. Repository, workspace, time, array position, similar text,
hash prefix, or raw-store proximity never resolves an edge. Every record has an
immutable origin link carrying archive hash, entry path, source snapshot ID,
source Monitor version, and import receipt ID. Graph, declaration, and origin
inserts are in the same transaction as records and history. Edge ordinals are
zero-based within their source record and therefore do not change when another
record is added or reordered in the archive. A global record also retains its
immutable first-import receipt link so missing history cannot be mistaken for a
new import after restart.

Stored canonical JSON is rendered only through framework encoding or DOM
`textContent`; it is never executed as markup or script.

## Transaction And History

Commit accepts the exact archive bytes plus the preview digest. It completes a
strict archive preflight before database open/BEGIN/schema access, opens one
SQLite transaction, revalidates the archive, recomputes dedup/conflict and the
preview digest inside that transaction, and then either commits every record,
origin, graph node/edge/declaration, count, and history row or commits nothing.
A changed preview returns `preview_changed`. A conflict returns
`record_conflict`. Busy or store failure returns a fixed unavailable error and
leaves no visible partial import or receipt.

The import ID is the archive SHA-256. Recommitting the same archive returns the
existing receipt with `idempotent_replay=true` only after the stored history,
exact canonical records, per-import origins/declarations, and deterministic
graph nodes/edges are complete and consistent. Missing, extra, or mismatched
owned rows fail as `import_integrity_failed`; replay never repairs them or
returns success from history alone. A successful replay creates no second
history, record, origin, node, edge, or declaration. A different archive creates
its own history/origin/declaration links only after every existing record or
graph node consulted for duplicate, conflict, definition, resolution, or
promotion validates its immutable first-import receipt. The closure starts from
every append-only import-history receipt rather than trusting only mutable
current-node state/definer links, so rolling back a promoted node or deleting an
edge-free owned node cannot hide its owner. A defined node also validates the
receipt that owns its defining record, and runtime validation requires unresolved
nodes to have no defining record. Missing, corrupt, or
semantically inconsistent owner state fails as `import_integrity_failed` and is
neither repaired nor adopted into the new receipt. History is append-only, ordered
by imported time then import ID, bounded to 100 rows per read, and contains only
sanitized metadata, explicit counts, hashes, versions, and fixed states.

## Persistence And Migration

Issue #86 owns the independent SQLite schema component
`schema_version.component = 'sanitized_import'`, version 1. It owns only the
`sanitized_import_*` tables and indexes. A commit adds or validates the component
inside the same immediate transaction as archive reinspection and import. Fresh
databases and databases with the supported monitor v7 / Session v13 version
vector add this component without changing those component versions or data.
A stale digest, record conflict, integrity failure, invalid structure, or failed
foreign-key check rolls back component tables, indexes, and version stamp as
well as import rows. A stamped future version or absent stamp with pre-existing
import tables is rejected without adoption.

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

JSON output uses the shared preview/result/history projection. Each projection
uses the count invariants above and includes `graph_state_updates`. Exit `0` means
success (including a non-committable conflict preview), `2` means validation or
conflict, and `3` means store/I/O or replay-integrity unavailable. Paths and
input fragments are never echoed.

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
scanner rejection, and `503` busy/unavailable/transaction/integrity failure.

The Japanese UI provides file selection, preview, explicit commit, progress,
result/error, provenance badge, and bounded import history. Preview renders the
bounded conflict identity/hash list, manifest declaration list, destination
unresolved kind/ID/state list, source
versions/date, capability and completeness/content/retention maps, processing
versions, migration version/step/chain/hash/lossiness, and the explicit absence
of origin/signature/authorization/source-store attestation. Commit remains
disabled until a committable preview succeeds. Selecting another file clears
the bound preview. A received fixed non-success HTTP response is a definitive
rejection and says the import was not committed; only a missing/malformed
response or transport interruption is reported as an ambiguous outcome that
requires history refresh. The UI does not persist bundle bytes/digests in
browser storage and never injects captured values as HTML.

## Explicit Exclusions

V1 never imports raw OTLP, Session content, raw analysis, source/file bodies,
prompts/responses/tool bodies, a SQLite database, backup/restore material, #72
historical datasets, #83 lifecycle state, #73/#74/#84 future carriers, or an
unknown export profile. It provides no background scan, automatic import,
cloud upload, signing/origin attestation, encryption, heuristic reconciliation,
conflict overwrite, backup restore, or embedded-content execution.
