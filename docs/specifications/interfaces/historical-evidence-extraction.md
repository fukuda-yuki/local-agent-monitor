# Historical Evidence Extraction Specification

## Status and authority

This specification freezes the Issue #72 bounded historical evidence dataset
consumed independently by Issues #73 and #74. It consumes the Session identity
and completeness contract, the Issue #58 repository/workspace label contract,
and the Issue #59 evidence-reference token contract without changing any of
them. The machine-readable contract is
[`historical-evidence/v1`](../contracts/historical-evidence/v1/).

Issue #72 owns deterministic selection, eligibility, extraction, canonical
serialization, and immutable persistence only. It does not discover or import
history, run an LLM, produce recommendations, add UI, infer Session links, or
create proposals/effects.

## Frozen bounds and rationale

| Value | v1 value | Rationale |
| --- | --- | --- |
| default session count | 50 | Existing Session list default. |
| maximum session count | 200 | Existing Session list maximum; reuses the repository's already-supported bounded read size. |
| raw-local descriptor | first line, 160 Unicode scalar values | Existing instruction-evidence descriptor boundary. |
| references per evidence group | 16 | Existing bounded accepted-evidence limit used by repository Doctor contracts. |
| evidence groups per session | 256 | Matches the repository's existing bounded manifest-member ceiling and prevents one included Session from dominating the dataset. |
| explicit Session IDs | 200 | Cannot exceed the maximum selected Session count. |
| snapshot metadata candidates | 401 | At most the requested maximum plus one matching suffix entry, unioned with at most 200 explicitly requested existing Sessions. |
| canonical payload bytes | 67,108,864 per representation | Bounds serialization, persistence, and read allocation independently of SQLite text limits. |

The date range has no invented default. Both endpoints are optional, but
`maximum_session_count` is always present and bounded, so an omitted date range
cannot create an unbounded output. `from` is inclusive and `to` is exclusive;
when both are present `from < to`.

## Selection and coherent snapshot

`HistoricalEvidenceSelectionV1` carries nullable repository/workspace/task/
experiment labels, nullable UTC range endpoints, distinct explicit Session
IDs, distinct source-surface filters, `maximum_session_count`, and
`sanitized_only`. At least one scope selector other than the count and posture
must be present. Labels compare ordinally and are never derived from prompt,
path, CWD, time proximity, or free text. Repository and workspace labels are
only the already-normalized #58/#51 fields.

When any non-ID selector is present, explicit IDs request additional decision
metadata but do not narrow the matching query; an explicitly requested Session
that fails another filter is recorded as `filter_mismatch`. When IDs are the
only selector, only those Sessions match.

`HistoricalEvidenceApplicationServiceV1` is the only production creation/read
boundary. Normal Local Monitor construction registers it with a
`SqliteHistoricalEvidenceSnapshotSourceV1` over the accepted Session tables,
the existing retention-authorized `ISessionStore.ReadContentAsync` boundary,
and the paired SQLite dataset store. It adds no HTTP route. Issues #73/#74
request or reopen datasets through this service and never scan history.

One extraction opens one `IHistoricalEvidenceSnapshotLeaseV1`. Its snapshot ID,
Session metadata, and evidence indexes belong to one coherent revision-fenced
read snapshot. Included-body reads use the captured exact event IDs but are
separately re-authorized against the current retention state before content is
materialized. The lease returns the canonical most-recent matching
suffix of at most `maximum_session_count + 1`, unioned with metadata for every
explicitly requested existing Session (at most 200) and de-duplicated by local
Session ID. It also returns the exact non-negative count of earlier matching
Sessions omitted from lease metadata. That count excludes any Session returned
through the explicit-ID union. Total lease metadata is therefore at most 401
rows. Selection completes against metadata before any evidence/body read. The
extractor re-applies every filter defensively and invokes `ReadEvidenceAsync`
only for the final included Session IDs; an implementation that reads an
excluded or metadata-omitted Session violates this contract.

Candidate ordering is ascending by `started_at` when present, otherwise
`last_seen_at`, then canonical lowercase Session UUID ordinal. All returned
matching candidates are ordered first. If the count exceeds the requested
maximum, the most recent suffix is retained and output remains ascending.
Returned overflow Sessions receive `window_truncated`. Metadata-omitted earlier
Sessions are represented by the exact bounded scalar
`truncated_session_count = omitted_earlier_matching_session_count +
returned_matching_overflow`; their IDs are not listed. `truncated_before` is
true exactly when that count is positive. An omitted earlier match may be
followed only by returned ineligible rows, so `truncated_before` does not imply
a returned `window_truncated` decision. Excluded rows sort by canonical raw
Session UUID ordinal, and the repository-safe form preserves that raw order
after tokenization. There is no latest-Session guessing or relation inference.

An explicit ID absent from the coherent snapshot produces an excluded decision
with `missing_session_reference`. Metadata candidates that do not match every
specified filter produce `filter_mismatch`. Those decisions never authorize a
body read.

## Eligibility and capabilities

Each existing included or excluded decision records the complete bounded
metadata projection: canonical UUIDv7 Session ID, repository/workspace labels,
UTC start/end/last-seen timestamps, every exact source surface, every distinct
`(surface, source_application_version, adapter_version)` provenance tuple,
completeness and its canonical Issue #51/#61 reasons, evidence source kind,
content state, and evidence-backed model and duration observations. A model or
duration observation carries the exact evidence reference that proves it;
unknown stays absent. A missing explicit Session alone has null metadata.
Reason ceilings are revalidated for every included and excluded existing
decision.

Closed evidence source kinds are `live_otel`, `saved_raw`, and
`historical_summary`. `unbound` Sessions and Sessions with no exact evidence
reference are excluded. `partial`, `rich`, and `full` Sessions may be included;
their capability state remains honest. `historical_summary` always includes
`historical_summary_only`, cannot be represented above `partial`, and cannot
claim raw descriptor, exact repeated-call, or exact ownership capability.

Capabilities are a fixed Boolean vector: turn/token/cache rollup, error span,
retry chain, repeated tool call, permission wait, sub-agent fan-out,
raw-local descriptor, quality reference, source comparison, and instruction
finding reference. Dataset distribution counts completeness, source kind, and
each capability independently. Missing stays false/absent; it is never
synthesized as zero.

## Evidence groups and exact references

The closed group kinds are `turn_rollup`, `token_rollup`, `cache_rollup`,
`error_span`, `retry_chain`, `repeated_tool_call`, `permission_wait`,
`subagent_fan_out`, `user_correction`, `quality_reference`,
`source_difference`, and `instruction_finding`.

Every group has a deterministic ID, exactly one included Session, 1..16 exact
references, and optional allowlisted scalar facts. A source reference may name
the Session plus an exact trace and optional span/turn. The reference must
resolve in that Session's immutable evidence index. Missing trace/span/turn,
cross-Session references, malformed input, or a missing Issue #59 finding ID
rejects the complete extraction; invalid evidence is never silently dropped.

`repeated_tool_call` requires either exact call ID or a producer-supplied
canonical hash. `subagent_fan_out` requires an exact ownership ID. The
extractor never manufactures either value from name, content, order, or time.
`instruction_finding` embeds the exact existing `InstructionFindingReceiptV1`
and, when eligible, its exact `InstructionRuleCandidateV1`. It uses the actual
Issue #59 `InstructionEvidenceReferenceV1` type and validators. The group
reference set must equal the receipt reference set exactly; each representation
retains its own canonical order because opaque-token order need not equal raw-ID
order. The candidate must name that receipt. A both-null span/turn reference, invalid anchor relation, token-only
membership, or parallel historical reference vocabulary is rejected.

Groups sort by Session order, closed kind order, then group ID. References sort
by Session, trace, nullable span, nullable turn, and Issue #59 relative-position
order. Duplicate groups or references collapse only when byte-identical;
conflicting duplicate identities reject the extraction.

## Raw-local and repository-safe forms

The extractor emits two forms from the same validated snapshot:

- `historical-evidence.raw-local.v1` contains canonical lowercase Session UUIDs
  and exact source trace/span identifiers. It may contain one bounded
  `raw_local_descriptor` only after first-line truncation and a closed-shape
  projection. Descriptor access is attempted only for raw-local posture when
  exact raw-descriptor capability is true, content state is `available`, the
  source is not `historical_summary`, and the existing retention read returns
  a granted lease. Denied, busy, expired, redacted, not-captured, unsupported,
  and sanitized cases do not materialize content.
- `historical-evidence.repository-safe.v1` replaces Session/trace/span IDs with
  the exact Issue #59 kind-specific `session-ref-*`, `trace-ref-*`, and
  `span-ref-*` tokens and omits descriptor text. It retains only descriptor
  state (`not_requested`, `unavailable`, `available`, `rejected_sensitive`).

When more than one raw descriptor candidate exists, any sensitive candidate
rejects the descriptor for that Session. Otherwise the extractor selects the
ordinally smallest distinct bounded first line so producer draft order cannot
change canonical output.

Raw identifier carriers use closed ASCII grammars; local Session identity is a
canonical UUIDv7. Repository-safe labels and identifier-like metadata are null,
fixed allowlisted values, or domain-separated opaque tokens. Relative, device,
home, UNC, and absolute paths; credentials; PII; and malicious carrier strings
reject before serialization rather than relying on a permissive blacklist.

`sanitized_only=true` never asks the snapshot lease for descriptor-bearing
content and both forms record `not_requested`. Repository-safe output contains
no raw body, credential, PII, absolute path, raw identifier carrier, source
location, or reversible sensitive key.

## Identity, bytes, and persistence

The extraction ID is `historical-extraction-` plus the first 16 SHA-256 digest
bytes over domain
`copilot-agent-observability/historical-extraction/v1` and length-framed
canonical snapshot ID plus canonical selection bytes. Group IDs use a separate
domain and the first 16 digest bytes over the canonical group identity.

Canonical JSON is UTF-8 without BOM, indentation, or trailing newline, with
the exact schema property order. Arrays use the orders above; timestamps are
UTC round-trip text; enums use lower snake case; null fields are present where
the schema requires them. Equal selection and snapshot input therefore produce
byte-equivalent raw-local and repository-safe payloads. The returned extraction
envelope and persistence rows carry separate lowercase `payload_sha256` values
for the exact canonical bytes of each representation. The checksum remains
outside the hashed payload to avoid a self-referential wire field.

`SqliteHistoricalEvidenceDatasetStoreV1` stores the two representations in one
transaction in `historical_evidence_datasets`, keyed by extraction ID and
representation. Writes are insert-or-identical. A conflicting rewrite fails.
A read verifies schema version, representation, checksum, strict JSON shape,
canonical reserialization, extraction ID, and that both forms share the same
snapshot/selection/decision/group identity before returning either form.
Serialization rejects a representation above 67,108,864 UTF-8 bytes. SQLite
reads query `length(CAST(payload_json AS BLOB))` before materializing text and
map every null, malformed, oversized, checksum-mismatched, or shape-invalid row
to fixed `InvalidPersistence` without inner detail.

## Consumer handoff

- Issue #73 consumes the raw-local form inside the local raw-analysis boundary,
  resolves citations from its exact indexes, and may submit only the existing
  #59 finding contract.
- Issue #74 consumes repository-safe scalar/capability distributions by
  default; any raw-local analysis requires the same explicit local raw boundary.
- Neither consumer may widen bounds, reinterpret missing as zero, infer joins,
  read an excluded body, or add fields/categories to #58/#59 contracts.
- Consumer contract tests construct both requests using only the persisted
  dataset and prove no out-of-band Session, raw telemetry, or finding-store read
  is needed.
