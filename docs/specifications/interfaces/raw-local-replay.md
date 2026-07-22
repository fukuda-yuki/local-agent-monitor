# Raw Local Replay

This specification defines Issue #87's explicit local raw replay profile. It is
normative for raw replay export, archive inspection, isolated replay, and their
Local Monitor surfaces. It does not change the frozen `sanitized-evidence` v1
contract.

## Boundary and versions

The only profile is `raw-local-replay`. It is opt-in, local-only, raw-bearing,
not repository-safe, not shareable, and never an upload or CI-artifact profile.
The following identifiers are closed v1 values:

| Contract | Value |
| --- | --- |
| bundle schema | `raw-local-replay-bundle.v1` |
| manifest | `raw-local-replay-manifest.v1` |
| canonical JSON | `raw-local-replay-canonical-json.v1` |
| ZIP framing | `raw-local-replay-zip-store.v1` |
| checksum | `sha256.v1` |
| export control | `raw-local-replay-export-control.v1` |
| replay control | `raw-local-replay-control.v1` |
| replay result | `raw-local-replay-result.v1` |
| normalization | `raw-measurement-normalization.v1` |
| projection | `raw-replay-monitor-projection.v1` |
| dashboard | `raw-replay-dashboard.v1` |
| credential scan | `raw-replay-credential-scan.v1` |

The JSON Schema 2020-12 structural contracts are
[`control.schema.json`](../contracts/raw-local-replay/v1/control.schema.json),
[`manifest.schema.json`](../contracts/raw-local-replay/v1/manifest.schema.json),
and [`result.schema.json`](../contracts/raw-local-replay/v1/result.schema.json).
The control schema is a closed union. Export control contains exactly
`schema_version`, `profile`, `created_at`, `selection`,
`include_session_content`, `sanitized_only`, `preview_digest`, and `consent`.
Replay control contains exactly `schema_version`, `profile`, `replay_id`,
`archive_sha256`, `normalization_version`, `projection_version`,
`dashboard_version`, `sanitized_only`, `preview_digest`, and `consent`. Replay
preview binds the archive hash, pinned target versions, and process-local expiry;
commit independently validates the remaining closed control fields and consumes
the digest once. The public replay result wrapper exposes only success/error/idempotent state and the no-raw receipt; raw
artifacts and retained namespace paths are never result fields.

`sanitized-evidence`, its control parser, scanner, archive inspector, and import
surfaces reject this profile. Raw replay never adds a raw carrier to a normal
sanitized bundle. `--sanitized-only` rejects raw replay preview, export, import,
replay, status, and download without reading or writing raw data.

## Warning, preview, and confirmation

Every export and replay is a two-step operation. Preview is non-mutating and
returns the exact selected counts/range, raw classification, content/filter
states, source and target versions, known missing capabilities, expected output
hashes where applicable, and a lowercase SHA-256 `preview_digest`. The warning
is always:

> Raw local replay data can contain prompts, responses, tool data, personal
> data, and secrets. Secret detection is incomplete. Keep it local.

Commit requires all of:

- profile exactly `raw-local-replay`;
- `warning_acknowledged: true`;
- confirmation phrase exactly `I UNDERSTAND THIS IS RAW LOCAL DATA`;
- the unexpired preview digest for the exact current request/snapshot;
- `sanitized_only: false`.

Export preview digests use length-framed UTF-8 fields under domain
`copilot-agent-observability/raw-local-replay-preview/v1`. Replay preview digests
use domain `copilot-agent-observability/raw-local-replay-import-preview/v1` and
bind the archive SHA-256, the three pinned target versions, and expiry. Export
recomputes the snapshot and rejects `preview_changed`; replay previews expire
after ten minutes, are process-local, and are single-use. A confirmation cannot
commit another bundle, selection, profile, or target version.
Export commit validates the closed control, consent, lowercase SHA-256 digest
shape, and safe output name before acquiring a raw snapshot; only the exact
preview comparison requires a fresh snapshot.

Known credential material (authorization headers, bearer tokens, private-key
markers, or provider-key fixture patterns) produces the generic warning code
`credential_material_detected` and rejects export/replay. No matched value is
returned or logged. This scanner is only a narrow rejection guard and never
makes the remaining raw data safe.

## Exact export selection

An export selection contains at least one of these axes:

- exact Session IDs;
- exact trace IDs;
- positive raw-record IDs;
- allowed raw source values (`raw-otlp`, `collector-output`,
  `langfuse-export`);
- UTC `start_inclusive` and/or UTC `end_exclusive` receive time.

Lists contain at most 256 unique canonical values. Values are ORed within one
axis and populated axes are ANDed. Date bounds are half-open. Raw records are
selected in ascending original raw-record ID order. Trace selection uses the
exact `monitor_spans.raw_record_id` relationship. Session selection uses only
an exact `session_runs.trace_id` to `monitor_spans.trace_id` relationship for
the named Session. Repository, workspace, path, time proximity, prompt text,
generic adapter label, and similarity never select or merge data.

The provider resolves the whole selected ID set and materializes raw records
under one Retention catalog v1 composite `operation` lease. Any missing, denied,
expired, stale, busy, or changed member fails the whole snapshot. No partial
bundle is success. When `include_session_content` is true, Session IDs are
required and exact Session Event content is materialized in the same all-or-none
operation boundary; otherwise the manifest records it as known missing. Export
preserves original raw IDs, receive timestamps, Session event IDs/timestamps,
and observed source/adapter/schema/content provenance. It never exports catalog
tokens, leases, private locators, unrelated runtime state, or external provider
configuration.

An explicitly named raw-record ID that does not exist is a missing member and
fails the snapshot. Trace and Session lists remain OR filters: an unmatched
value contributes no member, while every member that does resolve must pass the
same all-or-none lease boundary. This distinction preserves the documented
within-axis OR semantics without fabricating a source member.

## Archive and manifest

The archive reuses #85's frozen generic framing without reusing its sanitized
schema: ZIP Store only, `manifest.json` first, payload entries in ordinal path
order, DOS epoch timestamp, fixed external attributes, UTF-8 flag off, no ZIP
comments, data descriptors, preamble, duplicate/local-central name mismatch, or
trailing bytes. Publication is sibling `.partial` then atomic rename only after
strict self-inspection. A failure leaves no successful artifact.

Closed payload paths are `records/record-NNNNNN.json` and
`session-content/content-NNNNNN.json`; filenames never contain source IDs,
traces, Sessions, timestamps, source labels, paths, or prompt-derived text.
Limits are 256 total ZIP entries, 255 payload entries, 128 MiB total
uncompressed/read bytes, 30 MiB per raw-record member, 8 MiB per Session-content
member, and 1 MiB per control/manifest. These are raw-profile-specific v1 bounds
and do not alter #85's 8 MiB sanitized carrier bound.

Canonical record members contain the original record, its source observation,
truthful capture/filter state, and no local path. The manifest contains exact
schema/profile/serialization/archive/checksum versions; raw classification;
source/capture/adapter/schema versions; record/content counts and UTC date
range; content/filter states; complete ordered file inventory with size and
lowercase SHA-256; target normalization/projection/dashboard versions; expected
normalized, projection, and dashboard hashes; and sorted known-missing codes.
Known-missing codes use only lowercase ASCII letters, digits, and underscores.
Manifest record/content counts remain exact physical member counts; replay
result counts are logical canonical inputs after byte-identical source-ID
duplicates collapse. Manifest is excluded from its own inventory. Archive
SHA-256 is external to the archive.

## Isolated replay and deterministic outputs

Import is available only through the loopback Local Monitor raw-replay API. It
enforces Host validation, same-origin reads, CSRF on writes, no CORS, and
`Cache-Control: no-store`; raw replay is never placed below `/api/monitor/*`.
The complete archive is strictly inspected before durable staging begins.

Replay materializes a new, product-owned, isolated file namespace. It never
writes `raw_records`, Session tables, projections, or evidence in the live
database; never merges into the source database; never performs heuristic
Session merging; and never calls an external model or regenerates an AI result.
Original raw IDs and timestamps remain unchanged in staged canonical records.

The replay ID is 8-64 lowercase ASCII letters/digits/hyphen/underscore and is
opaque. It deterministically maps to one retained namespace. The namespace is a
Retention catalog v1 `sensitive_bundle` item under `sensitive-bundle-7d` v1,
using the existing reserve -> staging -> published_pending_catalog -> complete
capture journal, operation leases, queue, cleanup worker, deletion adapter,
retry, and recovery. Caller-owned input files are not cleanup targets. There is
no new store kind, migration, catalog, worker, or cleanup path.

An identical retry of the same replay ID, archive SHA-256, and pinned versions
returns the existing result and creates no second namespace. The same replay ID
with a different archive/options/version is `replay_id_conflict`. Duplicate
source IDs with byte-identical canonical records are one idempotent input;
duplicate source IDs with different bytes/provenance are
`source_id_conflict`. Any conflict or staging failure publishes no readable
namespace and never partially overwrites an existing one.

Replay preserves the manifest's adapter/schema version evidence and verifies it
against the canonical member summaries; source version labels are evidence, not
a receive allowlist. Values outside the closed source compatibility/content
state vocabularies and unknown or mismatched normalization/projection/dashboard
versions fail closed. The closed literal `unknown` preserves missing source
provenance; recognized drift/unsupported state labels remain evidence and do not
cause replay to invoke or trust a source adapter.
Canonical normalized rows are sorted by trace identity and canonical bytes;
monitor projections are sorted by source raw ID and trace identity; the replay
dashboard is a versioned deterministic projection of those outputs and contains
no generation clock. Each artifact is canonical UTF-8 JSON with LF termination. Result provenance records archive
SHA-256, replay ID, source versions, target versions, counts, the three output
hashes, idempotent-retry state, and `external_model_invocations: 0`; it contains
no raw body, credential, path, or private retention identity.

## Public surface and fixed errors

The Local Monitor v1 routes are:

- `POST /api/raw-replay/v1/export-previews`;
- `POST /api/raw-replay/v1/exports`;
- `GET /api/raw-replay/v1/exports/{exportId}`;
- `GET /api/raw-replay/v1/exports/{exportId}/archive`;
- `POST /api/raw-replay/v1/replay-previews` with `application/zip`;
- `POST /api/raw-replay/v1/replays`;
- `GET /api/raw-replay/v1/replays/{replayId}`.

Errors are fixed `{"error":"<code>"}` bodies. Invalid request/profile/schema is
400; cross-origin/CSRF/consent/sanitized-only is 403; missing result is 404;
stale preview, replay/source ID conflict, and version mismatch are 409; request
or archive bounds are 413; corrupt/checksum/inventory/credential failures are
422; busy/unavailable/publish failures, including a missing explicitly named
snapshot member, are 503. Error text, DTOs, headers, logs,
and repository-safe evidence never echo raw values, IDs selected from the raw
store, credentials, private filenames, or local paths.

Config CLI owns export-only local commands:

```text
config-cli raw-replay preview --database <monitor.db> --request <request.json>
config-cli raw-replay export --database <monitor.db> --request <request.json> --output <raw-local-replay.zip>
config-cli raw-replay result --bundle <raw-local-replay.zip>
```

The export request contains the preview digest and consent for `export`; replay
is deliberately not a direct CLI database operation because import must pass
through a running loopback Local Monitor.

## Validation and evidence

Synthetic fixtures cover profile separation, preview/consent, sanitized-only,
exact selection and lease failure, identity/timestamp/version preservation,
determinism, idempotent retry and conflicts, strict ZIP/checksum/inventory and
size/path negatives, credential rejection, isolation/no live mutation, zero
external-model calls, retention recovery/cleanup leasing, and no-leak errors.
Actual local replay evidence may record only safe hashes, counts, versions, and
statuses. Content-enabled live capture remains `blocked_external` until an
operator separately authorizes it; no validation enables
`OTEL_LOG_USER_PROMPTS=1`.
