# Sanitized Evidence Export

This specification freezes the Issue #85 export-only contract. Import (#86),
raw replay (#87), and runtime backup/restore (#88) consume its framing rules but
remain separate profiles and implementations.

## Versions and bounds

| Contract | Exact value |
|---|---|
| bundle schema | `sanitized-evidence-bundle.v1` |
| profile | `sanitized-evidence` |
| manifest | `sanitized-evidence-manifest.v1` |
| canonical JSON | `sanitized-evidence-canonical-json.v1` |
| archive | `sanitized-evidence-zip-store.v1` |
| checksum | `sha256.v1` |
| scanner | `repository-safe-scanner.v1` |
| producer validation | `sanitized-evidence-producers.v1` |
| compatibility | reader major `1` through `1` |
| maximum archive entries | 256, including `manifest.json` |
| maximum total uncompressed bytes | 134,217,728, including `manifest.json` |
| maximum request or bundle read | 134,217,728 bytes |
| maximum canonical carrier | 8,388,608 bytes |
| maximum records | 255 |
| maximum dependencies per record | 256 |
| maximum values per selection list | 256 |
| maximum agent or processing versions | 256 each |
| maximum forbidden markers | 64 |
| maximum identifier / marker length | 256 / 4,096 UTF-16 code units |

The bounds are conservative fixed limits. Writers do not expose configuration
for them in v1.

## Authority boundary and control request

The public CLI and HTTP surfaces consume one strict
`SanitizedExportControlRequest`: an explicit UTC creation time, selection
filters, and source-body markers for a supplemental fail-closed creation scan.
The public request has no snapshot, records, canonical bytes, capabilities, or
dependency fields. Unknown fields are rejected, so a caller cannot inject a
carrier into creation.

Preview and create capture one immutable snapshot through the application-wired
`ISanitizedExportSnapshotProvider`. Only the owning local stores may implement
that provider. Its snapshot includes the producer-owned canonical bytes and is
passed to the pure selection/bundle service as `SanitizedExportRequest`.
Preview and create validate the same snapshot, dependency closure, producer
contract, scanner, manifest, and size rules. A missing provider fails closed as
`snapshot_provider_unavailable`; syntax-valid bytes alone never establish
owner/store provenance.

The source snapshot uses the Issue #58 nullable label names exactly:
`repository_name`, `workspace_label`, and `repo_snapshot`. It also records source
agent versions, processing versions, completeness, content state, retention
state, and the following explicit receipt capabilities:

- `instruction_findings` (#59);
- `alert_receipts` (#80);
- `historical_instruction_analysis` (#73);
- `historical_efficiency_analysis` (#74);
- `alert_center` (#84).

Capability values are `available`, `missing`, or `unavailable`.
`instruction_findings` and `alert_receipts` may be `available` only when the
snapshot contains at least one corresponding valid carrier; a present carrier
requires `available`. Historical instruction analysis (#73), historical
efficiency analysis (#74), and Alert Center (#84) are not v1 carriers and their
capabilities must be exactly `unavailable`. Caller assertions never authorize
an absent or future producer.

The authority-provided capability states describe the complete captured snapshot. The
manifest and preview capability states describe the selected dependency closure:
an included #59/#80 carrier is `available`, an absent one is `missing`, and all
three future capabilities remain `unavailable`.

## Closed producer carriers

V1 accepts only canonical JSON and the following exact record types. Unknown
record types, versions, profiles, fields, property order, encodings, or path
forms fail before selection succeeds. Arbitrary JSON, free text, CSV, HTML, and
future #73/#74/#84 carriers are not accepted.

| `record_type` | exact path | exact producer contract |
|---|---|---|
| `repository_metadata_projection` | `repository-metadata/{record_id}.json` | `repository-metadata-projection.v1`, the #58 repository-safe projection below |
| `instruction_finding_handoff` | `instruction-findings/{record_id}.json` | #59 `instruction-finding-handoff.v1`, including canonical findings, candidate association, derived IDs, templates, safe references, and ordering |
| `alert_receipt` | `alert-receipts/{record_id}.json` | #80 `alert.receipt.v1` with `sanitized-alert-receipt.v1`, validated and byte-compared with `alert.canonical-json.v1` |

The #58 projection has the exact canonical property order
`schema_version`, `record_id`, `session_id`, `trace_id`, `source_surface`,
`repository_name`, `workspace_label`, `repo_snapshot`, `observed_at`,
`completeness`, `content_state`, `retention_state`. Its schema version is
`repository-metadata-projection.v1`; `record_id`, `session_id`, and
`source_surface` are nonempty safe tokens; `trace_id`, `repository_name`,
`workspace_label`, and `repo_snapshot` are nullable safe tokens; and
`observed_at` is canonical UTC. Every value after `schema_version` must exactly
equal the corresponding record-envelope value.

For #59, the envelope `record_id` is the invariant decimal
`analysis_run_id`; fields that the carrier does not represent (`session_id`,
`trace_id`, `source_surface`, and all three repository labels) must be null. For
#80, envelope `record_id`, session, trace, source surface, and observation time
must equal `alert_id`, `session_id`, `trace_id`, `source_surface`, and
`last_observed_at`; the three repository labels must be null. These rules make
the public envelope a selector over producer-authorized carriers, not an
independent authority that can contradict them.

## Selection and dependency resolution

Selection dimensions are session IDs, trace IDs, source surfaces, repository
names, workspace labels, half-open UTC date range, and receipt types. Values
within a dimension are ORed; specified dimensions are ANDed. Session and trace
IDs form one exact-ID union. Records are ordered by observation time, record
type, and record ID using ordinal comparison.

Dependencies resolve only by the exact `(record_type, record_id)` pair in the
same immutable snapshot. Resolved required dependencies are included even when
outside the initial filter. An unresolved required dependency is `missing`. An
explicit external dependency is always `external`, even when a matching
snapshot identity exists, and is never added to the bundle. Repository,
workspace, path, time proximity, or another ID is never used to resolve it.
Selected rows, dependency closure, and manifest rows are joined only by exact
`(record_type, record_id)` identity; duplicate selected, excluded, or dependency
paths fail deterministically.

## Archive and checksum conventions

The archive is ZIP with no compression. `manifest.json` is the first entry;
payload entries follow in ordinal path order. Every entry uses the fixed DOS
timestamp `1980-01-01 00:00:00` and zero external attributes. Entry paths are
relative, forward-slash separated, unique, and exactly match the closed
record-type prefix plus `.json` grammar above. ZIP version fields are `2.0`;
flags are zero except the UTF-8 filename bit when required.

`files` lists every payload entry in ordinal path order with its exact byte
length and lowercase SHA-256. `manifest.json` is intentionally excluded from
`files`, so the manifest has no self-checksum. The result separately reports the
lowercase SHA-256 of the complete archive. Consumers verify the exact file set,
entry order, local and central ZIP names, timestamps, storage method, sizes,
checksums, and record identities before accepting the artifact. A ZIP preamble,
end-of-central-directory comment, local or central extra field, per-entry
comment, data descriptor, multi-disk field, or trailing byte is invalid.
Inspection recomputes `record_counts`, total uncompressed bytes, and unique
`(record_type, record_id)` inventory from verified entries and cross-checks the
manifest.

All time values are canonical UTC with seven fractional digits. Input ordering
does not affect canonical arrays or object members. `created_at` is the only
caller-supplied volatile field; the same snapshot, selection, markers, and
`created_at` produce byte-identical manifest and archive bytes.

## Repository-safe scanner and publication

Before manifest creation, every selected and dependency entry is scanned for:

- raw content field names, including raw OTLP, prompt/response/system prompt,
  tool argument/result, source/file body, and raw analysis fields;
- authorization, credential, token, password, secret, API key, and private-key
  patterns;
- email identity and absolute POSIX roots, recognized Windows-drive,
  backslash or forward-slash UNC/device, WSL-mounted, common Unix home/system,
  and file-URI path forms;
- source-supplied forbidden body markers in plain, JSON-escaped, HTML-entity,
  percent-encoded, Base64 UTF-8, and lowercase SHA-256-prefix-12 forms;
- invalid canonical JSON and unexpected/duplicate entries.

Marker comparison is ordinal case-insensitive. Marker transformations and
their total expansion are bounded by the v1 marker count and length limits.
Markers supplement producer and generic scanner validation during preview and
create only; they never establish repository safety. They are not persisted,
so later inspection does not claim to repeat or independently prove that scan.

Exact producer validation plus the generic versioned scanner are the
repeatable authorities recorded in `repository_safe_validation`. This is a
bounded negative scanner, not an enterprise DLP claim. Any match or
scanner error fails the operation before archive success. File publication
writes a same-directory `.partial` file and atomically renames it only after the
complete in-memory archive passes. Failure removes the partial file and never
returns archive bytes or a successful result.

The versioned scanner evaluates only the declared plain, JSON-escaped,
HTML-entity, percent-encoded, Base64 UTF-8, and lowercase SHA-256-prefix forms,
one bounded transformation at a time. It does not recursively compose
transformations or claim compression, encryption, arbitrary binary-container,
fuzzy-classification, process-memory, network-traffic, or deleted-storage
coverage. The Issue #91 synthetic corpus is executable compatibility evidence,
not privacy, legal, or secure-erasure certification.

## CLI and HTTP surfaces

The Config CLI uses the shared control request and result DTOs:

```text
config-cli sanitized-export preview --request <request.json>
config-cli sanitized-export export --request <request.json> --output <bundle.zip>
config-cli sanitized-export result --bundle <bundle.zip>
```

CLI `result` verifies the frozen producer/profile/archive/schema/inventory/
checksum and generic scanner contract. CLI request and bundle reads reject a
sentinel byte beyond the fixed limit before allocation. CLI output paths are
explicit local operator inputs. `preview` and `export` require a trusted
snapshot provider wired by the host application; without one they fail as
`snapshot_provider_unavailable`. `result` does not require a provider and does
not claim source/store provenance.

The Local Monitor exposes synchronous routes:

```text
POST /api/sanitized-export/v1/previews
POST /api/sanitized-export/v1/exports
GET  /api/sanitized-export/v1/exports/{export_id}
GET  /api/sanitized-export/v1/exports/{export_id}/archive
```

These foundation routes accept only the control request. The host must wire a
trusted owner/store provider that captures one stable snapshot for each preview
or export operation. The database path is otherwise used only to derive the
server-controlled sibling export directory. Until the #58/#59/#80 owners expose
and wire their producer/store adapters, production composition uses the
fail-closed unavailable provider and does not publish a bundle.

POST routes require JSON, same-origin context, and `x-monitor-csrf`. Reads reject
cross-site requests. Existing loopback binding and Host validation apply. Every
response is `no-store`; CORS is not enabled. The request DTO has no output-path
field. The server publishes under the database sibling `sanitized-exports`
directory and exposes only the SHA-256 export ID and a relative download route.
Status is process-local and synchronous; no persistence schema or migration is
added. An archive left after restart is not rediscovered as a current result.
Before download, the server performs a bounded stored-file read, verifies the
expected length and complete-archive SHA-256 kept in process state, and reruns
strict inspection. A changed, oversized, truncated, or invalid stored file is
not returned.

HTTP status mapping is fixed: successful preview/result/download is `200`,
successful create is `201`, malformed JSON is `400`, cross-site or missing CSRF
is `403`, missing result is `404`, oversized input is `413`, non-JSON input is
`415`, scanner/selection/contract rejection is `422`, and publication or
snapshot-provider unavailability is `503`. Error bodies contain only
`{"error":"<fixed_code>"}`.

## Allowed and forbidden content

Allowed record types are only the exact #58 repository metadata projection,
#59 handoff, and #80 sanitized alert receipt described above. The manifest,
checksums, producer-authorized IDs/evidence references, nullable #58 labels,
versions, state distributions, counts, and date range are allowed.

Raw OTLP, raw Session content, prompt/response/system prompt, tool bodies,
source/file bodies, credentials, authorization values, PII, local sensitive
paths, and raw analysis Markdown are forbidden. V1 does not sign, encrypt,
upload, import, replay, back up, or restore anything.

Canonical schemas and the synthetic golden request/archive are under
`docs/specifications/contracts/sanitized-evidence/v1/` and
`tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SanitizedExport/`.

## Validation matrix transition

Issue #91 validation uses active evidence rows `91-E-085` and `91-S-085` after
this surface is implemented. The shared `future-surface-registry.v1` schema
permits only `not_available` entries. Integration must therefore remove this
surface's future entry, or supersede the registry through a versioned canonical
transition; it must not write `active` into the v1 registry or inherit a pass
from the former placeholder. The machine-readable handoff is
`contracts/sanitized-evidence/v1/issue-91-validation-handoff.json`.
