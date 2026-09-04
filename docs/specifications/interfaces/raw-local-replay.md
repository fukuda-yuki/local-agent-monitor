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
D069 pins the three target identifiers above and binds their exact derived
hashes. Therefore these v1 targets retain their pre-Issue-151 byte semantics:
normalization derives source only from each record's direct `client.kind`,
retains the prior `service.name` unknown-attribute treatment, and projection
retains the prior record-local primary-contribution summary. They do not adopt
the current live trace resolver or cross-record evidence aggregation. Changing
those semantics requires separately accepted new normalization, projection,
and dashboard target versions and a new hash contract; this specification
defines no such versions.

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

The provider first resolves the whole selected ID set and projects its length
preflight from metadata only, then acquires one Retention catalog v1 composite
`operation` handle. Only after that hidden handle is published does its fixed
consumption operation materialize raw records. Any missing, denied, expired,
stale, busy, or changed member fails the whole snapshot. No partial bundle is
success. When `include_session_content` is true, Session IDs are required and
exact Session Event content is materialized under that same all-or-none
operation handle; otherwise the manifest records it as known missing. Export
preserves original raw IDs, receive timestamps, Session event IDs/timestamps,
and observed source/adapter/schema/content provenance. It never exports catalog
tokens, leases, private locators, unrelated runtime state, or external provider
configuration.

A valid zero-member selection is the explicit Retention `Empty` result and may
build only the unchanged empty preview/archive bytes. It creates no lease,
Retention authority, hidden handle, expiry notification, source token,
mapper/use reference, or terminal call; it is never a vacuous grant. Every
nonempty selection retains its one composite operation handle through its named
terminal result. Member use references remain open
through every raw access, graph/credential validation, archive/preview
construction, and private staging that needs them. A seal arm keeps them through
its candidate construction; a completion-without-raw arm first discards raw and
closes every use reference. No representative member, disposal lifetime, or
current-row recheck authorizes publication.

Every post-grant branch that cannot reach its named seal—including raw-derived
archive, credential, post-materialization bounds/inspection, reservation, and
file-publication failures—discards raw/private staging, closes use references,
and calls the same handle's `TryCompleteWithoutRaw()`. Only
`completed_without_raw` may publish the original fixed safe result. HTTP
terminal `lost|busy` aborts with zero response; direct CLI terminal `lost|busy`
instead serializes that mode's unchanged complete failure DTO (`RawReplayPreview`
or `RawReplayResultView`), emits `snapshot_read_denied|snapshot_store_busy`, and
returns nonzero.

The same SQLite snapshot must project the UTF-8 byte length of every selected
raw payload and Session-content value before payload materialization or any
operation-lease insert. A raw member above 30 MiB or Session-content member
above 8 MiB is `entry_too_large`; a selected source-byte aggregate above
128 MiB is `archive_too_large`. These are lower-bound preflight gates only:
canonical-member and complete-archive limits remain authoritative after
materialization. A preflight failure creates no lease and changes no Retention
catalog state.

An explicitly named raw-record ID that does not exist is a missing member and
fails the snapshot. Trace and Session lists remain OR filters: an unmatched
value contributes no member, while every member that does resolve must pass the
same all-or-none lease boundary. This distinction preserves the documented
within-axis OR semantics without fabricating a source member.

### Skill invocation Session-content carrier

Issue #158 does not add a raw-replay profile or a second raw member. Under the
existing #87 explicit-consent authority, `include_session_content=true` may
select the sole `session_event_content` carrier of a `skill.invoked` Event as
one existing `session-content/content-NNNNNN.json` member when the exact Session
is selected. `payload_sha256` is SHA-256 over the exact received UTF-8 byte
slice of the normalized `event.payload` JSON value token, including its object
whitespace, property order, escapes, and duplicate spelling. `payload_bytes`
is the byte length of that same slice. Neither value covers the full request,
upstream SDK `data` buffer, decoded value graph, content document, body, or
path.

The sole `session_event_content` raw carrier is exactly these bytes, with no
BOM or LF and with this property order:

```text
UTF8('{"schema_version":"session-event-content.skill-invoked.v1",'
     '"payload_utf8_base64":"')
|| strict RFC4648 base64 of the exact payload-token bytes
|| UTF8('"}')
```

Base64 has no whitespace and uses no alternate alphabet. `content_kind` is
exact `application/json`; `content_document_sha256` is SHA-256 over the complete
canonical document bytes. The canonical document byte length is exactly
`84 + 4 * ceil(payload_bytes / 3)`. No secret filter, body/path copy, second
Retention item, snapshot raw column, normalized JSON, or direct Skill/generic
JSON HTTP exposure of the base64 exists. The raw-replay archive remains the
sole deliberate HTTP archive carrier and is not a Skill read-route exception.

Body/path digests and byte counts are over exact strict UTF-8 bytes after JSON
unescape. Validation does not strip BOM, normalize Unicode or line endings,
trim, change path case, or repair invalid scalars. Available snapshot rows have
the exact body/path facts. A nonavailable snapshot row still has its raw payload
slice/document, payload digest/length, Event, one Retention item, state/reason,
snapshot, and receipt, but no fabricated body/path/claim facts.

`RawReplaySessionContent.ContentJson` is the exact canonical document string;
the existing canonical archive JSON writer may escape that string only as
required by its enclosing record. Its enclosing facts are exact
`SourceAdapter=copilot-sdk-stream`, `ContentKind=application/json`,
`ContentState=available`, `MatchKind=null`, the Event's exact application,
adapter, schema-fingerprint, and normalization evidence,
`SecretFilterState=not_applied_raw_capture`, and
`SecretFilterVersion=raw-replay-credential-scan.v1`. It never claims
`session_secret_filter_applied`. The archive adds no snapshot, receipt, claim,
selected native Session ID, configured root, or discovery fact.

The existing same-snapshot length preflight applies before raw materialization
or operation-lease insertion. A canonical Skill document above the existing
8 MiB Session-content member limit is exact `entry_too_large` and is never
truncated. Only after every size, count, and aggregate preflight and one
all-or-none Retention operation-grant admission may the provider enter one
authorized raw-replay read transaction/snapshot. In that snapshot, before
selecting `session_event_content.content_json`, it validates every nonraw
Session/snapshot/Event/receipt/claim/Retention ownership, identity, link,
classification-state, and stored digest/length fact. Failure exits without
selecting or materializing raw.

Only after that complete graph proof may the provider select and materialize
the canonical document under the still-usable exact grant. Before private
staging or publication it proves the raw-dependent document grammar and strict
base64, decoded payload length and digest, complete document digest, artifact
fingerprint, reclassification, and credential-scan facts. There is no nested
or second snapshot and no validation-after-publication arm. The existing
128 MiB archive limit, error precedence, and fixed tokens remain unchanged.
`--sanitized-only` rejects at the raw-replay control boundary before Session
content lookup or Retention lease admission.

`raw-replay-credential-scan.v1` has one schema-specific input arm for this
canonical carrier; its pattern set, matching rules, timeout-means-match rule,
public token, and version literal do not change. After the bounds, grant,
nonraw-graph, and document proofs, the strict bounded payload parser decodes
`payload_utf8_base64` once. The existing v1 matcher scans both the exact decoded
UTF-8 payload-token text and every decoded JSON property name and string value
at every depth, including required, optional, array, and unknown-member strings,
so JSON escapes cannot hide a match. A match or matcher timeout returns only
`credential_material_detected`, publishes or stages nothing, and emits or logs
no payload, string, path, match, or exception detail. This scan is not a secret
filter and does not make the archive safe.

Strict archive inspection repeats the bounds, document/schema, decoded-domain
credential, and source-evidence checks for each externally supplied canonical
Skill member before replay-preview publication, import, or durable staging.
Isolated replay preserves the exact Session-content record only as raw source
evidence. It never reconstructs or writes a live Session/Event,
`skill_invocation_snapshot`, receipt, #154 claim, Retention item, producer,
discovery service, or Skill route, and it never treats an archived registry
label as current admission. Equal source identity with different canonical
document bytes remains `source_id_conflict`; an identical retry remains the
existing idempotent result. Sanitized evidence export/import carries no such
member or placeholder.

This carrier leaves all four existing Retention terminal modes unchanged:

- preview uses safe completion through the same handle's
  `TryCompleteWithoutRaw()`;
- retained GET/POST result publication uses the same safe completion;
- memory-only transient publication uses
  `PreparePut` -> `TrySealRawReplayTransientPublication()` -> `CommitPut`;
- Config CLI staged nonoverwrite file publication uses
  `TrySealRawReplayFilePublication()` -> the exact non-overwrite move.

Their existing fixed tokens, DTO/serialization order, lost/busy behavior,
resource cleanup, and exact terminal-release rules continue to apply.

## Archive and manifest

The archive reuses #85's frozen generic framing without reusing its sanitized
schema: ZIP Store only, `manifest.json` first, payload entries in ordinal path
order, DOS epoch timestamp, fixed external attributes, UTF-8 flag off, no ZIP
comments, data descriptors, preamble, duplicate/local-central name mismatch, or
trailing bytes. Each publication invocation owns a unique sibling partial file,
created exclusively, and atomically renames it only after strict self-inspection.
Cleanup may remove only that invocation-owned partial; a pre-existing or
concurrent sibling partial is never removed or replaced. A failure leaves no
successful artifact from that invocation; a losing concurrent invocation does
not remove the independently published artifact of the winner.

For a nonempty granted selection, Config CLI publication fully writes, flushes,
and inspects that hidden same-directory file before calling
`TrySealRawReplayFilePublication()`. Only `sealed` returns the single-use ticket
that permits the exact non-overwrite move to the already validated destination.
On Linux that move uses atomic `renameat2(RENAME_NOREPLACE)`; an unsupported
primitive or filesystem fails with `publish_failed`, without a check-then-rename
fallback. Concurrent publishers cannot both succeed for the same destination.
Pre-capture commit-control and `output_name_invalid` failures have no handle and
make no terminal call. After a nonempty grant, every raw-derived archive,
credential, post-materialization bound/inspection, `output_exists`, parent,
temporary-name, create, write, or flush failure discards the raw buffers, deletes
the invocation-owned partial, closes use references, and calls
`TryCompleteWithoutRaw()`. Only `completed_without_raw` may preserve that
pending failure DTO/token. Parent/temporary-name/create/write/flush failure is
exact `publish_failed`; staged-file inspection failure is exact
`publish_validation_failed`. Completion or seal `lost|busy` instead deletes
staging and replaces the pending error with the unchanged complete failure
`RawReplayResultView` plus `snapshot_read_denied|snapshot_store_busy`; no file or
replacement is published. After `sealed`, the single-use non-overwrite move is
the publication point; a move failure deletes the partial, exact-releases the
ticket/terminal reference, and is exact `publish_failed`. Move success also
exact-releases once. Every `CreateAndPublishAsync` failure serializes the
unchanged complete `RawReplayResultView` with its failure Preview and null
archive hash to stdout before its error token and nonzero exit.

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

At Local Monitor startup, sensitive-bundle capture recovery runs to completion
before raw-replay routes become reachable or cleanup workers begin. Recovery is
forward-only, drains every pending query batch, and is idempotent across repeated
restarts at every raw-replay capture checkpoint. The retained item expires
exactly at capture time plus seven days.
An active raw-replay operation lease excludes cleanup; after it is released,
cleanup resumes through the existing durable cursor and intent without
recreating an already deleted member. Cleanup deletes only the exact retained
child and preserves its parent, siblings, and caller-owned archive.

If a retained `manifest.json` or `input/archive.zip` member is temporarily
contended, replay status returns `replay_store_busy`. The read transaction rolls
back without returning a value, creating a lease, or changing catalog state;
after contention clears, the same read may be retried. SQLite busy/locked is the
same retryable disposition, while other catalog failures remain unavailable.

An identical retry of the same replay ID, archive SHA-256, and pinned versions
returns the existing result and creates no second namespace. The same replay ID
with a different archive/options/version is `replay_id_conflict`. Duplicate
source IDs with byte-identical canonical records are one idempotent input;
duplicate source IDs with different bytes/provenance are
`source_id_conflict`. Any conflict or staging failure publishes no readable
namespace and never partially overwrites an existing one.

A handle-derived retained-result read keeps its operation handle/use reference
through complete result validation and safe serialization, then discards raw
state, closes the use reference, and calls `TryCompleteWithoutRaw()`. Only
`completed_without_raw` may return the unchanged GET result or idempotent/racing-
existing POST result. Pre-admission denied/busy keeps the exact
`replay_store_denied|replay_store_busy` 503 mapping; post-admission `lost|busy`
aborts with zero response. Caller-supplied bundle-only Config CLI result reads
that acquire no Retention handle remain outside this rule.

Session-content source identity is the ordinal tuple (`source_adapter`,
`source_event_id`), not the local Session `event_id`. Equal source identity with
different canonical bytes is `source_id_conflict`; equal local event IDs with
different source identities remain distinct inputs.

Replay preserves the manifest's adapter/schema version evidence and verifies it
against the canonical member summaries; source version labels are evidence, not
a receive allowlist. Values outside the closed source compatibility/content
state vocabularies and unknown or mismatched normalization/projection/dashboard
versions fail closed. The closed literal `unknown` preserves missing source
provenance; recognized drift/unsupported state labels remain evidence and do not
cause replay to invoke or trust a source adapter.
Canonical normalized rows are sorted by trace identity and canonical bytes;
monitor projections are sorted by source raw ID and trace identity, and span
ordinals are reassigned only after canonical span ordering; the replay dashboard
is a versioned deterministic projection of those outputs and contains no
generation clock. Nested trace-contribution collections are sorted by trace
identity and canonical bytes, and their top-level summary is selected from that
canonical order, so permuting equivalent multi-trace source containers does not
change derived projection or dashboard hashes. Each
artifact is canonical UTF-8 JSON with LF termination. Result provenance records archive
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

Export archives and replay-preview bytes are process-local transient data. The
two kinds share one bound of 8 entries and 256 MiB, expire 10 minutes after
creation, and are swept at least once per minute even while no request arrives.
Insertion evicts expired entries and then the oldest entries deterministically
until both limits hold. A missing or evicted export is 404; an expired or evicted
replay preview is `preview_expired`. Process shutdown clears the store.

For a nonempty grant, Preview buffers only the existing preview DTO/digest; it
creates no raw archive, content artifact, or transient entry. After discarding
its raw snapshot buffers and closing every use reference, it calls
`TryCompleteWithoutRaw()`; only
`completed_without_raw` may return the unchanged preview to Local Monitor or
direct CLI stdout. HTTP preview terminal `lost|busy` aborts with zero response.
Direct CLI `PreviewAsync` instead serializes its unchanged complete failure
`RawReplayPreview` to stdout, including the fixed warning/classification/profile/
zero/null fields, then emits `snapshot_read_denied|snapshot_store_busy` and
returns nonzero.

For a nonempty granted transient archive publication, the store first performs
`PreparePut` under its own lock. It validates complete buffers/bounds, disposed
state and existing expiry rules, then reserves capacity plus the deterministic
eviction plan without exposing an entry. Reservation accounting prevents a
competing insert from consuming that capacity; shutdown/dispose drains or
rejects reservations. After releasing the lock, the owner calls
`TrySealRawReplayTransientPublication()`. `lost|busy` discards the buffers,
closes use references, cancels the reservation, and aborts with zero response.
`sealed` permits exactly one infallible `CommitPut` that applies the reserved
evictions, samples the ten-minute entry expiry, and atomically inserts the sole
memory-only entry; no store lock is held across the Retention terminal
transaction. A `PreparePut` refusal creates no reservation and first requires
`completed_without_raw` before returning the unchanged `archive_too_large`;
terminal loss/busy aborts. For every successfully created reservation exactly
one cancel or commit and one terminal release occurs. Process termination before
commit publishes no response or entry.

Errors are fixed `{"error":"<code>"}` bodies. Invalid request/profile/schema is
400; a body with an unsupported media type is 415; cross-origin/CSRF/consent/
sanitized-only is 403; missing result is 404;
stale preview, replay/source ID conflict, and version mismatch are 409; request
or archive bounds are 413; corrupt/checksum/inventory/credential failures are
422; busy/unavailable/publish failures, including `replay_store_busy` and a
missing explicitly named snapshot member, are 503. Error text, DTOs, headers, logs,
and repository-safe evidence never echo raw values, IDs selected from the raw
store, credentials, private filenames, or local paths.

Provider failures cross the public boundary only through the closed mapping
`request_invalid`, `selection_limit_exceeded`, `entry_too_large`,
`archive_too_large`, `snapshot_store_busy`, `snapshot_member_missing`,
`snapshot_read_denied`, or `snapshot_store_unavailable`; unknown and missing
provider codes map to `snapshot_store_unavailable`. HTTP error bodies are
generated by the JSON serializer, never string interpolation, and CLI output
uses the same mapped code.

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
