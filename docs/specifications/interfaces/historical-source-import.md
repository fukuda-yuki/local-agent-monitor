# Historical Source Import Specification

## Status and authority

This specification freezes the Issue #76 policy and the contracts handed to
Issues #77, #78, and #79. It applies the source-capability semantics in D056,
the Session normalization contract in
[Raw Store And Normalization](../layers/raw-store-normalization.md), the raw
boundary in [Security And Data Boundaries](../security-data-boundaries.md), and
the retention contracts in
[Retention Mutation](retention-mutation.md). When those sources impose a
stricter rule, the stricter rule wins.

The versioned machine-readable contract is
[`historical-import/v1`](../contracts/historical-import/v1/). Its schemas,
profiles, fail-closed examples, synthetic downstream handoff, and merge cases
are normative. The synthetic candidate batch validates shape only. It is not
evidence that either producer format is supported.

Issue #76 adds no adapter, importer, background scan, raw-source copy,
persistence migration, or synthetic trace. It does not authorize reading
undocumented producer storage.

## Goals and non-goals

The contract has four goals:

1. classify historical sources by authority and risk;
2. require explicit, bounded consent before any local-source probe;
3. make adapter output provenance-preserving and fail-closed;
4. give #79 deterministic deduplication, merge, completeness, and retention
   rules without allowing it to infer producer support.

Historical evidence is additive weak evidence. It may contribute only fields
allowed by an active, fixture-bound source profile. It cannot establish or
replace trace identity, span identity, event identity, hierarchy, lifecycle,
timing, duration, time-to-first-token, agent identity, repository, workspace,
or source location.

The following are explicitly out of scope:

- repository-wide, home-directory-wide, startup, scheduled, or background
  discovery;
- private caches, databases, application internals, or undocumented storage;
- best-effort parsing, permissive schema matching, and guessed versions;
- path, repository, timestamp proximity, text, prompt, model, or token-count
  correlation;
- synthesized sessions, runs, events, traces, spans, parents, durations, or
  timestamps;
- source-content capture without separate informed consent;
- copying an external source file into `raw_record` or treating a caller-owned
  file as a retention cleanup target.

## Source tiers

| Tier | Definition | v1 authority | Current availability |
| --- | --- | --- | --- |
| A: product-owned and versioned | A product-owned raw OTLP record or an exported run/session artifact with a published schema version, exact ownership, integrity evidence, and stable source identity. | A structurally verified saved raw OTLP record may be authoritative only for fields actually present in that record. It is normalized through the existing raw OTLP contract and does not become automatically `full`. | Existing product-owned raw OTLP records are eligible under their existing contract. No historical raw-store export or Session bundle format is defined here; those artifact forms remain unavailable until a versioned export contract and fixtures exist. |
| B: producer-documented local history | A producer-documented local container or official exact file reference whose record format is admitted only by a source-specific profile. | Weak, field-scoped historical summary evidence. It never supplies identity, hierarchy, timing, lifecycle, or an explicit event binding. | The GitHub Copilot CLI and Claude Code profiles are detection-only and unsupported in v1 because no record-format fixture/fingerprint evidence is available. |
| C: undocumented or heuristic | Private state, workspace caches, guessed paths, scraped databases, or any format admitted by observation or heuristics rather than a fixture-bound profile. | None. | Permanently excluded unless a later product-policy decision reclassifies a specific documented source. |

Tier A does not mean complete or safe by location alone. The reader must verify
the product-owned catalog identity, schema version, integrity binding, and
source record before using a value. An arbitrary OTLP-looking file is not Tier
A. An export becomes Tier A only through its own versioned contract; Issue #79
must not invent one.

Tier C includes VS Code `workspaceStorage`, chat-session caches, Codex private
storage/cache/databases, Claude or Copilot internal databases not covered by a
published format, and other same-machine files discovered by exploratory
scanning. User ownership of the machine does not make those stores supported.

## Tier B source decisions

The source profile documents under `historical-import/v1/profiles` are the
machine authority for these decisions.

| Source | Profile and adapter | Allowed reference | Observed detector version | Supported application/format versions | Active allowlist | Decision |
| --- | --- | --- | --- | --- | --- | --- |
| GitHub Copilot CLI session state | `github-copilot-cli-session-state` / `github-copilot-cli-history-v1` | The user-selected documented root, bounded to `session-state/{sessionId}/events.jsonl` after opt-in | `1.0.71` | Empty | Empty | Official documentation identifies the container, but not a stable serialized event schema. Detection and an unsupported preview are allowed; record content is not read. |
| Claude Code transcript | `claude-code-transcript` / `claude-code-history-v1` | An exact `hook.transcript_path` supplied by an official Hook flow, or one exact user-selected file | `2.1.215` | Empty | Empty | Official Hook documentation identifies the transcript reference, but not a stable transcript JSONL schema. Detection and an unsupported preview are allowed; record content is not read. |

For both profiles, the supported application-version set is empty, the
supported source-format set is empty, and the active field allowlist is empty.
An observed detector version proves only that the bounded container/reference
was observed for that executable version. It does not authorize parsing, does
not name a source-format version, and is never used as a compatibility range.

The frozen D056 policy ceiling for any later supported Tier B profile is this
ordered, closed set:

1. `model_tokens.model`
2. `model_tokens.input_tokens`
3. `model_tokens.output_tokens`
4. `model_tokens.total_tokens`
5. `model_tokens.cache_tokens`
6. `model_tokens.reasoning_tokens`
7. `retry_attempt.retry`
8. `retry_attempt.attempt`
9. `errors.present`
10. `errors.code`

The active allowlist for a supported format may be a strict subset of this
ceiling. No adapter or importer may expand it.

## Consent, detection, and preview

The workflow is ordered and fail-closed:

1. The user explicitly chooses one named historical source.
2. The product displays the exact reference policy, intended bounded probe,
   content posture, current support state, and the fact that import has not
   occurred.
3. The user consents to that one probe. Consent is not reusable for another
   source, root, file, Session, or content-capture choice.
4. The source adapter performs only the profile's bounded detection operation.
5. The product shows a deterministic preview from the adapter result.
6. A commit control is available only when a supported fixture-bound profile
   produced at least one eligible candidate. Commit requires a separate
   explicit confirmation owned by #79.

No filesystem probe occurs before step 3. The Copilot detector may inspect only
the explicitly selected documented root and the fixed
`session-state/{sessionId}/events.jsonl` container shape; it does not traverse
other roots. The Claude detector receives only an official exact Hook reference
or one exact user-selected file; it does not search for transcripts. A
malformed-source result in the current unsupported state means the bounded
reference/container shape could not be established without opening record
content.

`metadata_only` is the only current capture choice. Because both source
profiles are unsupported, the detector must not open or parse record bodies,
must report `content_risk = not_read`, and must emit zero candidates. A future
`include_content` choice requires all of the following before any content read:

- an active fixture-bound supported format;
- a separate explicit content-consent screen describing raw/PII risk;
- a preview that identifies exact item counts without displaying content;
- the existing secret-filter and raw-content boundary;
- the retention mapping in this specification.

Content consent does not authorize scanning or retention of the whole source
file.

## Source-profile activation

The current empty support sets are intentionally promotable by #77 or #78 as a
source-specific compatibility revision under this already-frozen policy. That
promotion is a technical compatibility revision, not a new product decision,
only when every condition below is met:

- the change is confined to one named source profile and its adapter fixtures;
- the source application version is exact, never a guessed range;
- the source format has an explicit name and version;
- an anonymized source fixture is committed and its lowercase SHA-256 is bound
  in the profile;
- a deterministic schema fingerprint is bound in the profile;
- golden tests prove accepted, rejected, malformed, and drifted inputs;
- the activated field allowlist is a subset of the frozen policy ceiling;
- candidate output conforms to `historical-candidate-batch/v1`;
- unsupported versions, unknown fingerprints, malformed records, and partial
  parses still produce zero candidates.

The exact evidence tuple is
`(profile_id, adapter_id, source_application_version, source_format_name,
source_format_version, source_fixture_sha256, source_schema_fingerprint,
golden_test_id)`. No tuple member may be wildcarded or inferred. Updating a
profile to bind such evidence and implementing the adapter does not authorize
new source locations, fields, merge behavior, content behavior, or retention
behavior. Any such expansion is a product-policy change and must update the
higher source of truth first.

## Adapter-result contract

Every Tier B probe returns `historical-adapter-result/v1`. The result names the
exact profile, adapter, surface, source tier, detection state, source-reference
state, application version when known, authorization state, format profile,
candidate count, content risk, repository-safe posture, and fixed diagnostics.

The current fail-closed diagnostic registry is:

| Code | Meaning | Required result |
| --- | --- | --- |
| `historical_source_reference_required` | The profile's exact source reference was not provided. | Do not probe, do not read content, and emit zero candidates. |
| `historical_source_format_unsupported` | Detection succeeded, but no exact fixture-bound supported format matches. | Do not parse record content and emit zero candidates. |
| `historical_source_malformed` | The bounded container/reference shape is invalid, or a future supported parser cannot validate the entire selected record set. | Discard all tentative output and emit zero candidates. No partial batch is allowed. |

When `support_authorized` is false, `source_format_profile` is `none`,
`candidate_count` is zero, `diagnostics` contains at least one fixed failure
code, and `content_risk` is `not_read`. An unknown
application version, missing format version, unknown schema fingerprint,
fixture hash mismatch, malformed record, record-limit failure, or adapter
exception is unsupported/malformed; it is never downgraded to a warning that
permits candidates.

A future exact fixture-bound success sets `support_authorized = true`, names
the exact bound source-format profile, emits a positive `candidate_count` that
equals the separate candidate batch count, and has an empty diagnostics array.
`content_risk` is `source_read_metadata_only` when record bodies were read only
to emit allowlisted summaries, or `source_read_include_content` only after the
separate content-consent gate. A true support flag is not sufficient evidence;
#79 still validates the complete profile evidence tuple and the candidate
batch marker.

Diagnostics, previews, logs, tests, screenshots, and repository artifacts must
remain repository-safe. They contain no source paths, raw payloads, prompts,
responses, tool arguments/results, credentials, PII, exception text, or
content-derived labels.

## Candidate and provenance contract

A supported adapter may hand #79 only a complete
`historical-candidate-batch/v1`; adapters do not write Session tables. A
candidate carries only the one-or-more normalized leaves actually observed and
admitted by the active strict-subset allowlist, source-record identity scoped
to the admitted source format, completeness, and field-level provenance. It
must not populate an absent ceiling field with zero, false, null, an empty
string, a value from another record, or any other synthetic default.

`candidate_key` and `source_record_key` are internal, local-only opaque tokens
with the closed forms `hc_` plus 32 lowercase hexadecimal characters and `hr_`
plus 32 lowercase hexadecimal characters, respectively. They are not event,
trace, or span identities. The source adapter creates them only after mapping
the admitted source identity to a non-reversible repository-safe token; it may
not embed or derive a reversible value from a path, repository, prompt,
response, tool data, credential, or PII. Production keys are omitted from
previews, diagnostics, logs, screenshots, repository artifacts, and other safe
serialization. A source-record key is stable for the same admitted format,
record identity, and local import store, but it need not correlate across
stores. Checked-in fixtures use fixed synthetic tokens only.

Every populated field has its own provenance entry containing:

- field name;
- actual adapter ID and source surface;
- exact source application version;
- exact source format name and version;
- bound source-fixture SHA-256 and schema fingerprint;
- source-record key defined by that admitted format; this is the D056 source
  event identity within that source format, never a normalized Session event
  identity;
- `capture_content_state` using the existing closed source-capture values;
- normalization version.

The populated normalized leaves and `field_provenance[].field` have exact
one-to-one correspondence. There are between one and ten populated leaves,
with no duplicates, and the provenance array emits those fields in
policy-ceiling order. An extra provenance row, a missing provenance row, an
empty `values` object, an empty field-family object, or any non-allowlisted leaf
rejects the complete batch.

The adapter must never fill a missing provenance member from the import clock,
filesystem metadata, repository context, another record, or a detector
version. A missing required member rejects the entire batch. Raw paths are
never candidate provenance.

Historical candidates contain no trace ID, span ID, parent ID, event ID,
duration, TTFT, start/end time, agent ID, repository, workspace, or source
path. If a future admitted format contains an explicit identity already known
to the Session subsystem, that identity is retained in a separate exact-binding
input defined by the source-specific adapter contract; it is not synthesized
inside this generic candidate and it cannot be derived from similarity.

## Deduplication, merge, and conflict precedence

Deduplication and merge are deterministic and field-scoped. The normative case
order and expected results are in
[`fixtures/handoff/merge-cases.json`](../contracts/historical-import/v1/fixtures/handoff/merge-cases.json).

1. An exact repeat of the admitted format identity, source-record key,
   normalization version, normalized values, and field provenance is a no-op.
2. The same exact source record with conflicting normalized content preserves
   the existing observation and records a sanitized conflict. It never silently
   replaces the earlier value.
3. With an explicit existing binding, the importer may attach a historical
   observation to the bound event, but strong identity, hierarchy, timing,
   lifecycle, and live/OTLP values remain authoritative.
4. Without an explicit existing binding, the candidate remains distinct and
   unbound. Similar repository, timestamp, path, text, model, or token values
   do not create a relationship.
5. A missing candidate field never overwrites a captured field.
6. A historical value that conflicts with a strong or already captured value
   is retained only as provenance-scoped conflict evidence; it does not
   overwrite the strong value.

There is no fuzzy duplicate, last-write-wins, newest-file-wins, import-time
precedence, or synthetic parent fallback.

## Completeness

Every accepted Tier B historical candidate and every Session result whose only
additional evidence is Tier B historical evidence has completeness ceiling
`partial` and required reason `historical_summary_only`. Historical evidence
alone never reaches `rich` or `full`. Attaching historical observations to an
otherwise stronger Session cannot weaken that Session's independently proven
completeness, but it also cannot be cited as satisfying lifecycle, content,
input-family, or exact-linked OTel requirements.

Unsupported, malformed, or zero-candidate inputs create no Session/Run/Event
row and therefore create no completeness state.

## Retention mapping

Metadata-only historical summaries are sanitized retained output, not raw
retention items. They set `content_state = not_captured`, create no
`session_event_content` or `raw_record` item, and do not place the caller-owned
source file in the retention catalog.

If a future supported profile and explicit content consent allow event content,
the importer applies this exact mapping:

| Import value | #89 mapping |
| --- | --- |
| Store kind | One exact `session_event_content` item for the exact imported Session event. The external source file itself is never a catalog item. |
| Policy | `raw-default-90d` version 1. |
| Authoritative capture time | The original valid source capture/receive/request time admitted by the source-specific format. Import time, current time, file time, and reconciliation time are forbidden. |
| Initial readable lifecycle | `expiring`, when expiry is in the future and the exact ownership receipt is valid. |
| Already expired | Commit irreversible read denial, then advance through the #89 allowed states to `deletion_queued` in the same successful #79 transaction. No readable window is created. |
| Missing/invalid authoritative time | Reject content capture with `historical_import_authoritative_time_required`; a metadata-only preview may remain available, but no content is stored. |
| Pin state | Never automatic. `retained_by_policy` is assigned only by the existing explicit #90 pin workflow. |

All content passes the existing secret filter before storage. Exact catalog
ownership and receipt validation are mandatory. Imported content uses the same
catalog-gated reads, irreversible denial, durable cleanup, retry, recovery,
and physical-removal behavior as other #89 items. Import does not purge backups
and must preserve the #90 `retention_backup_not_purged` warning where that
workflow applies.

After import, #90 may preview and mutate only the exact Session/item targets
defined by its contract. It must not locate imported items by path, source
record key, repository, time, or content similarity. Delete-now, pin, and unpin
remain explicit preview/confirmation workflows; import adds no alternate
lifecycle or mutation path.

## Child implementation handoffs

### #77 handoff — GitHub Copilot CLI

#77 may implement the bounded opt-in detector, unsupported adapter result, and
zero-candidate preview for profile `github-copilot-cli-session-state`. It must
use only the user-selected documented root and fixed session-state container,
must not read record content while the profile is unsupported, and must not
scan private or adjacent storage.

If #77 obtains a redistributable anonymized fixture and exact format evidence,
it may promote only its source profile and adapter as the source-specific
compatibility revision described above. That promotion is not a new product
decision. It must bind the exact fixture SHA/fingerprint and golden tests,
activate only ceiling fields proven by the fixture, preserve zero-candidate
failure for every unbound version/fingerprint, and leave all merge, content,
retention, and source-location policy unchanged.

### #78 handoff — Claude Code

#78 may implement the exact-reference opt-in detector, unsupported adapter
result, and zero-candidate preview for profile `claude-code-transcript`. It may
accept only an official Hook `transcript_path` reference or one exact
user-selected file, must not read transcript content while the profile is
unsupported, and must not search private or adjacent storage.

If #78 obtains a redistributable anonymized fixture and exact format evidence,
it may promote only its source profile and adapter under the same technical
compatibility rule. It must bind the exact fixture SHA/fingerprint and golden
tests, activate only proven ceiling fields, and preserve fail-closed behavior.
An observed Claude Code executable version alone is insufficient.

The #77 and #78 adapters remain separate implementations and profiles. They do
not share a permissive parser, compatibility range, or producer-neutral
fallback.

### #79 handoff — import workflow

#79 consumes only a schema-valid adapter result and, after a future profile
promotion, a schema-valid candidate batch. It does not open, reparse, discover,
or infer support for a producer source. It must validate the exact profile
evidence tuple again before preview and commit.

For current v1 profiles, adapter results have zero candidates. #79 returns the
exact `historical-import-preview/v1` zero-candidate shape:

- `eligible_candidate_count = 0`;
- `commit_allowed = false`;
- `rejection_code = historical_import_no_eligible_candidates`;
- `content_risk = not_read`.

No confirmation or commit token may be issued for that preview, and no import,
Session, candidate, provenance, completeness, or retention row is written. An
unsupported or malformed producer result is not a signal for #79 to try a
different parser. #79 must not infer support from a detector version, file
extension, JSON/JSONL shape, successful partial parse, repository, path, or a
candidate-like payload supplied outside the admitted adapter boundary.

After an exact profile promotion, a positive preview has a positive
`eligible_candidate_count`, `commit_allowed = true`, and
`rejection_code = null` only when every candidate and profile evidence check
succeeds. Its `content_risk` preserves the adapter result. The generic preview
schema permits that future state while the checked-in zero-candidate fixture
and semantic tests continue to pin the current fail-closed state.

When a profile is later fixture-bound, #79 owns preview/confirmation/commit,
exact deduplication, field-level merge, sanitized conflict recording, and the
retention transaction. A production adapter must emit
`fixture_only_not_source_support_evidence = false`; #79 rejects `true`. The
checked-in synthetic candidate fixture proves only the interface shape and
therefore emits `true`. The boolean is not source-support evidence: the exact
profile evidence tuple remains mandatory even when it is `false`.

## Persistence and migration

Issue #76 has no persistence schema and no migration. #77 and #78 do not gain
write authority from their detectors. #79 owns any additive persistence change
needed for import receipts, exact provenance, conflicts, or preview state and
must follow the repository migration conventions. No migration or source
fixture may be fabricated before a version has actually shipped and been
validated.

## Required validation corpus

The v1 corpus contains:

- closed schemas for source profiles, adapter results, candidate batches,
  import previews, and merge cases;
- the two current unsupported source profiles;
- detected-unsupported, malformed, and missing-reference adapter examples;
- one synthetic candidate batch marked as non-support evidence;
- the exact zero-candidate #79 preview;
- deterministic deduplication, merge, completeness, and retention cases.

Repository tests must validate every JSON artifact against its schema, assert
the empty support/allowlist state, reject unknown properties and invalid types,
ensure the synthetic candidate contains no trace authority, and pin the exact
zero-candidate and merge decisions.
