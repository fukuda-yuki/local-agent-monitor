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
| compatibility | reader major `1` through `1` |
| maximum archive entries | 256, including `manifest.json` |
| maximum total uncompressed bytes | 134,217,728, including `manifest.json` |

The bounds are conservative fixed limits. Writers do not expose configuration
for them in v1.

## Source-neutral request

The shared service consumes one immutable `SanitizedExportRequest`: an explicit
UTC creation time, an immutable source snapshot, selection filters, and source
body markers for the fail-closed scanner. Every candidate record carries its
exact canonical bytes. The service does not deserialize and reserialize #59 or
#80 receipts.

The source snapshot uses the Issue #58 nullable label names exactly:
`repository_name`, `workspace_label`, and `repo_snapshot`. It also records source
agent versions, processing versions, completeness, content state, retention
state, and the following explicit receipt capabilities:

- `instruction_findings` (#59);
- `alert_receipts` (#80);
- `historical_instruction_analysis` (#73);
- `historical_efficiency_analysis` (#74);
- `alert_center` (#84).

Capability values are `available`, `missing`, or `unavailable`. An absent #73,
#74, or #84 producer is never inferred or represented as available.

## Selection and dependency resolution

Selection dimensions are session IDs, trace IDs, source surfaces, repository
names, workspace labels, half-open UTC date range, and receipt types. Values
within a dimension are ORed; specified dimensions are ANDed. Session and trace
IDs form one exact-ID union. Records are ordered by observation time, record
type, and record ID using ordinal comparison.

Dependencies resolve only by the exact `(record_type, record_id)` pair in the
same immutable snapshot. Resolved dependencies are included even when outside
the initial filter. An unresolved required dependency is `missing`; an explicit
external dependency is `external`. Repository, workspace, path, time proximity,
or another ID is never used to resolve it.

## Archive and checksum conventions

The archive is ZIP with no compression. `manifest.json` is the first entry;
payload entries follow in ordinal path order. Every entry uses the fixed DOS
timestamp `1980-01-01 00:00:00` and zero external attributes. Entry paths are
relative, forward-slash separated, unique, and limited to the allowlisted
record-type prefixes and `.json`, `.csv`, or `.html` extensions.

`files` lists every payload entry in ordinal path order with its exact byte
length and lowercase SHA-256. `manifest.json` is intentionally excluded from
`files`, so the manifest has no self-checksum. The result separately reports the
lowercase SHA-256 of the complete archive. Consumers verify the exact file set,
entry order, timestamps, storage method, sizes, and checksums before accepting
the artifact.

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
- email identity and recognized Windows-drive, UNC/device, WSL-mounted,
  common Unix home/system, and file-URI path forms;
- source-supplied forbidden body markers in plain, JSON-escaped, HTML-entity,
  percent-encoded, Base64 UTF-8, and lowercase SHA-256-prefix-12 forms;
- invalid canonical JSON and unexpected/duplicate entries.

This is a bounded negative scanner, not an enterprise DLP claim. Any match or
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

The Config CLI uses the shared request and result DTOs:

```text
config-cli sanitized-export preview --request <request.json>
config-cli sanitized-export export --request <request.json> --output <bundle.zip>
config-cli sanitized-export result --bundle <bundle.zip>
```

CLI `result` verifies the frozen archive/profile/schema/inventory/checksum and
scanner contract. CLI output paths are explicit local operator inputs.

The Local Monitor exposes synchronous routes:

```text
POST /api/sanitized-export/v1/previews
POST /api/sanitized-export/v1/exports
GET  /api/sanitized-export/v1/exports/{export_id}
GET  /api/sanitized-export/v1/exports/{export_id}/archive
```

These foundation routes accept the immutable source snapshot in the request;
they do not select or rediscover records from the Local Monitor database. The
host database path is used only to derive the server-controlled sibling export
directory. Snapshot construction and source authorization remain caller-owned
until a later source adapter is specified; v1 therefore guarantees deterministic
selection and validation of supplied canonical records, not independent proof
that the caller supplied a complete database snapshot.

POST routes require JSON, same-origin context, and `x-monitor-csrf`. Reads reject
cross-site requests. Existing loopback binding and Host validation apply. Every
response is `no-store`; CORS is not enabled. The request DTO has no output-path
field. The server publishes under the database sibling `sanitized-exports`
directory and exposes only the SHA-256 export ID and a relative download route.
Status is process-local and synchronous; no persistence schema or migration is
added. An archive left after restart is not rediscovered as a current result.

HTTP status mapping is fixed: successful preview/result/download is `200`,
successful create is `201`, malformed JSON is `400`, cross-site or missing CSRF
is `403`, missing result is `404`, oversized input is `413`, non-JSON input is
`415`, scanner/selection/contract rejection is `422`, and publication failure is
`503`. Error bodies contain only `{"error":"<fixed_code>"}`.

## Allowed and forbidden content

Allowed record types are sanitized Session projection, normalized measurement
dataset, exact #59 handoff bytes, optional exact #80 receipt bytes, sanitized
candidate/driver receipts when their owning contracts exist, and sanitized
dashboard data. The manifest, checksums, IDs/evidence references, nullable #58
labels, versions, state distributions, counts, and date range are allowed.

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
