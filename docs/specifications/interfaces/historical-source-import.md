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

The versioned producer contract is
[`historical-import/v1`](../contracts/historical-import/v1/). Its schemas,
profiles, fail-closed examples, synthetic downstream handoff, and merge cases
are normative. Issue #79 adds the separate
[`historical-import-workflow/v1`](../contracts/historical-import-workflow/v1/)
public orchestration family. The strict producer
`historical-import-preview/v1` shape is unchanged and is not used as the
workflow response DTO. Both synthetic contract families validate shape only;
neither is evidence that a producer format is supported.

Issue #76 adds no adapter, importer, background scan, raw-source copy,
persistence migration, or synthetic trace. Issue #79 owns the importer,
workflow UI/API/CLI, and dedicated additive persistence described below; this
does not expand source support or authorize reading undocumented producer
storage.

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
separately versioned `include_content` workflow requires all of the following
before any content read; workflow v1 does not expose that choice:

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

### Trusted post-admission seam

The workflow never accepts an adapter result, candidate batch, profile tuple,
source snapshot, exact binding, or support flag from an HTTP/CLI request. A
source-specific adapter first performs its own bounded probe and parser work,
then production composition may construct one typed trusted admission only
after all of these checks succeed:

1. exact profile/adapter/application/format/fixture/fingerprint/golden-test
   tuple lookup in the active registry;
2. strict adapter-result and candidate-batch schema validation;
3. full candidate semantic validation, including sparse value/provenance
   parity and canonical ordering;
4. `fixture_only_not_source_support_evidence = false`;
5. an adapter-owned stable source snapshot digest for the complete selected
   record set; and
6. validation of any optional source-specific exact Session binding against an
   already-existing local Session.

The typed seam carries immutable parsed values, not a JSON discriminator or a
source path. It cannot be activated by configuration, environment variable,
file extension, candidate-like request JSON, the checked-in synthetic fixture,
or a test marker in production. Tests may inject an internal admission whose
fixture marker is false to exercise generic transaction behavior; that proves
only the workflow machinery. A later #77/#78 compatibility promotion owns the
production construction of this typed value and must not cause #79 to reparse
the source.

The generic candidate batch has no exact-binding field. A source-specific
admission may optionally carry one exact existing-Session target for a
candidate with basis `native_id`, `explicit_link`, or `exact_trace_id`. #79
stores that relationship in its own component and may expose a navigation-only
link. It does not update the target Session or create a Session, Run, Event,
trace, span, timestamp, or historical Session identity. Without that separate
trusted input, the observation is `distinct_unbound`.

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
`candidate_count` is zero, `diagnostics` contains exactly one fixed failure
code, and `content_risk` is `not_read`. An unknown
application version, missing format version, unknown schema fingerprint,
fixture hash mismatch, malformed record, record-limit failure, or adapter
exception is unsupported/malformed; it is never downgraded to a warning that
permits candidates.

A future exact fixture-bound success sets `detection_state = detected`,
`source_reference_state = provided`, carries an exact non-null
`source_application_version`, sets `support_authorized = true`, names the exact
bound source-format profile, emits a positive `candidate_count` that equals the
separate candidate batch count, and has an empty diagnostics array.
`candidate_count` is bounded to 0 through 1000, and a complete positive batch
contains 1 through 1000 candidates. Contract tokens are 1 through 128
characters; exact application and format versions are 1 through 64 characters.
Producer/admission application and format versions use the strict SemVer 2.0.0
lexical form: numeric core and numeric prerelease identifiers have no leading
zero, and prerelease/build identifiers are non-empty dot-separated tokens. The
private source-selection application version remains the existing exact
metadata token because it is detector input before producer admission, not a
claim of supported SemVer. Every regex-constrained token rejects CR/LF, and
fixed opaque IDs, hashes, and digests also pin their exact string length so a
terminal line break cannot be accepted through end-anchor behavior.
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
   observation to the existing Session as a navigation-only relationship, but
   strong identity, hierarchy, timing, lifecycle, and live/OTLP values remain
   authoritative. The binding is fixed when that exact admitted-source
   observation is first created. A later duplicate or conflicting repeat does
   not add, replace, or remove its relationship, and is not shown as a merge
   candidate.
4. Without an explicit existing binding, the candidate remains distinct and
   unbound. Similar repository, timestamp, path, text, model, or token values
   do not create a relationship.
5. A missing candidate field never overwrites a captured field.
6. A navigation-only Session binding never authorizes cross-store field
   comparison. If the historical value differs from a Session or live-capture
   value, preserve both independently; do not overwrite the Session, mutate
   the observation, or manufacture a cross-store conflict receipt.

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

Workflow v1 ends at that metadata-only rule. The table below is the policy
prerequisite for a later separately versioned content contract, not an active
workflow-v1 branch or an extension of its fixed error registry. If a future
supported profile and explicit content consent allow event content, that later
contract applies this exact mapping:

| Import value | #89 mapping |
| --- | --- |
| Store kind | One exact `session_event_content` item for the exact imported Session event. The external source file itself is never a catalog item. |
| Policy | `raw-default-90d` version 1. |
| Authoritative capture time | The original valid source capture/receive/request time admitted by the source-specific format. Import time, current time, file time, and reconciliation time are forbidden. |
| Initial readable lifecycle | `expiring`, when expiry is in the future and the exact ownership receipt is valid. |
| Already expired | Commit irreversible read denial, then advance through the #89 allowed states to `deletion_queued` in the same successful #79 transaction. No readable window is created. |
| Missing/invalid authoritative time | Reject content capture. Workflow v1 rejects `include_content` earlier with `historical_import_request_invalid`; a later content contract must freeze its own no-leak failure before activating this mapping. |
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

#79 consumes only an adapter-owned probe result and, after a future profile
promotion, the typed trusted admission above. It never opens or reparses a
producer source outside that adapter, discovers a source, or infers support.
It validates the exact profile evidence tuple at preview and again immediately
before commit.

For current profiles, the workflow converts the unchanged producer
`historical-import-preview/v1` handoff into the richer
`historical-import-workflow-preview/v1` projection. The canonical current
fixture has:

- `adapter_state = unsupported` and `source_badge = unsupported`;
- `counts.eligible = { availability: available, value: 0 }`;
- total, unsupported, malformed, duplicate, conflict, new-observation,
  new-Session, new-Event, merge-candidate, excluded, and source-time values set
  to `availability = unavailable` with null values because record bodies were
  not read;
- `completeness_ceiling = none`, no completeness reasons, and no candidate
  missing-capability claim;
- `content_risk = not_read`;
- `retention_impact.disposition = not_applicable` and zero created items;
- `commit_allowed = false`; and
- `rejection_code = historical_import_no_eligible_candidates`.

Unavailable is not zero. In particular, the current detector cannot count
source records, unsupported/malformed records, dates, new Sessions, or new
Events. `partial` / `historical_summary_only` applies only after an observation
has been admitted; it is not attached to a zero-candidate preview. No
confirmation is issued and no import, observation, candidate, provenance,
completeness, Session, Run, Event, or retention row is written. A safe preview
receipt may be retained only for its bounded read/expiry lifecycle.

An unsupported or malformed producer result never causes #79 to try another
parser. Detector version, extension, JSON/JSONL shape, partial parse,
repository, path, and caller-supplied candidate-like JSON are never support
evidence. `fixture_only_not_source_support_evidence = true` is always rejected
at production admission. The checked-in positive workflow fixtures may use
synthetic internal admission to prove DTO and transaction shape, but cannot be
used by HTTP/CLI production composition or a live validation row.

## Workflow contract family

Every public workflow object carries
`contract_version = historical-import-workflow/v1` and one exact
`schema_version`. The machine-readable structural source of truth contains:

| Schema | Purpose |
| --- | --- |
| `source-selection.schema.json` | private preview request; preview returns the sanitized opaque selection ID |
| `preview.schema.json` | safe preview, counts, capabilities, merge candidates, exclusions, and retention impact |
| `confirmation-request.schema.json` | explicit preview/digest/snapshot confirmation request |
| `confirmation.schema.json` | issued confirmation binding and remaining expiry |
| `import-request.schema.json` | request/idempotency/confirmation/preview/snapshot commit binding |
| `import-status.schema.json` | queued/running/terminal progress projection |
| `import-result.schema.json` | final transaction, idempotency, observation, duplicate, conflict, and retention result |
| `import-history.schema.json` | bounded terminal operation history |
| `observation-list.schema.json` | bounded historical-only observation list |
| `observation-detail.schema.json` | sanitized historical observation capability/detail projection |
| `error.schema.json` | exact one-property fixed-code HTTP failure envelope |

All schemas are closed and reject unknown or duplicate properties. Workflow
JSON is bounded to 1,048,576 bytes before materialization. Opaque identifiers
use their schema-pinned prefixes: source selection `hss_`, preview `hip_`,
confirmation `hic_`, request `hir_`, idempotency `hik_`, operation `hop_`,
observation `hob_`, merge candidate `hmc_`, record result `hrr_`, and cursor
`hoc_`, followed by 32 lowercase hexadecimal characters. A digest is
`sha256:` followed by 64 lowercase hexadecimal characters; a snapshot version
is `hsv_` followed by a positive decimal integer. These tokens are not source,
Session, Event, trace, path, or time identities. Production opaque IDs use 128
bits from a cryptographically secure random source and encode no timestamp or
input value; only checked-in synthetic fixtures use fixed values.

An availability-wrapped count has exactly `availability` and `value`.
`available` requires a non-negative integer; `unavailable` requires null.
Availability is evidence-derived and must never be changed merely to make a UI
total easier to render. An unavailable date range has null start/end. Import
time and operation ID time/order are never reported as source time.

Public workflow projections omit the internal `hc_` candidate key and `hr_`
source-record key. They use only `hob_`, `hmc_`, or `hrr_` safe local tokens.
The exact idempotency value and its stored hash are omitted from status,
result, history, logs, and observation reads.

## Preview request and consent

`source-selection.schema.json` is the private request shape accepted by
`POST /api/historical-import/v1/previews` and Config CLI `preview`. It selects
exactly one known profile/adapter/surface, requires
`consent_granted = true`, and currently requires
`requested_capture = metadata_only`.

The closed source-reference arms are:

- GitHub Copilot CLI: one canonical absolute selected root, one bounded
  `session_id`, and one exact application version. The adapter may inspect only
  the fixed `session-state/{sessionId}/events.jsonl` metadata shape.
- Claude Code: `official_hook` or `explicit_user_selection`, one canonical
  absolute exact reference, and one exact application version. The adapter may
  inspect only that file metadata.

Canonical means host-native and fully qualified. URI/UNC/device/platform-
foreign forms, Windows network volumes, alternate streams, reserved device
segments, noncanonical aliases, and target/ancestor links are rejected before
adapter I/O. A native Unix path already in the process mount namespace is local
for v1 after link/type checks; path syntax cannot identify its opaque backing,
and the workflow makes no broader network-storage proof.

The literal root/reference is sensitive local control input. It is never
echoed. An actionable preview may retain it only as ephemeral private database
state in its `historical_import_previews` row for commit re-probe until the
earlier of five-minute expiry, a non-actionable preview decision, or a
terminal `succeeded`, `rejected`, or `failed` attempt after the exact
confirmation is accepted.
This bounded state exists so CLI stages may run in separate
processes and a Local Monitor restart does not silently replace the selected
source. It is never
copied to the sanitized preview, operation/history/observation/conflict rows,
logs, or evidence. A non-actionable current preview discards the literal
immediately.

Expiry is enforced from the persisted absolute instant. While the Local
Monitor is running, a scheduled cleanup erases the private locator and the
separately stored trusted probe/candidate state at that instant. Each workflow
process also sweeps expired private state before any read or action; therefore
a database that remained dormant past expiry is purged before it can be used
again. Expired state is never usable to confirm, re-probe, or commit.

`include_content` is reserved in the producer contract but rejected by
workflow v1 with `historical_import_request_invalid` before any source probe.
`--sanitized-only` does not grant consent and does not alter this rule;
it keeps the metadata-only workflow available.

## Preview snapshot and digest

Preview evaluates one coherent source-and-database decision snapshot. Its
digest is SHA-256 over a length-framed domain
`copilot-agent-observability/historical-import-preview/v1` and the exact:

1. workflow and schema versions;
2. safe source-selection identity;
3. hash of canonical adapter-result bytes;
4. hash of canonical candidate-batch bytes or an explicit absent marker;
5. trusted complete-source snapshot digest or an explicit unsupported marker;
6. exact profile evidence tuple and fixture-marker decision;
7. ordered candidate decisions, existing observation fingerprints, optional
   exact-binding targets, and retention impact; and
8. safe preview projection excluding `preview_digest` and the volatile
   `expires_after_seconds` remainder.

The persisted absolute expiry remains part of the private preview binding.
`expires_after_seconds` is recalculated on a read and therefore is not digest
input; changing only that remaining-duration projection does not refresh or
extend the five-minute expiry.

Length framing is used; delimiter concatenation, trimming, case folding, and
re-serialization with different options are forbidden. `snapshot_version` is
the persisted positive version of this decision snapshot. Preview and
confirmation expire exactly five minutes after preview creation. The public
contract reports remaining seconds rather than using source time.

A positive production preview is possible only when every admission check
succeeds, has an available positive eligible count, `evidence_status =
production`, `commit_allowed = true`, and a null rejection code. Its count
decisions classify each candidate against the same database snapshot as new,
exact duplicate, or conflicting. Only a new candidate may be an exact-bound
merge candidate; the relationship of an existing observation is immutable.
Date/new-Session/new-Event counts
remain unavailable unless a future higher-authority contract explicitly
supplies those facts; generic candidates never do.

## Confirmation and stale revalidation

Confirmation is a distinct user action after the complete preview is shown.
The request supplies the exact preview ID, digest, snapshot version, and
`decision = confirm`. It supplies no source path, candidate bytes, mutable
counts, or override. The server issues an opaque confirmation ID bound to the
same preview and the unextended five-minute expiry. A zero-candidate,
unsupported, malformed, expired, fixture-only, or otherwise non-actionable
preview returns a fixed conflict and no confirmation.

Commit performs checks in this order before writing an observation:

1. resolve an exact prior idempotency result for the same commit request;
2. validate request and idempotency-key syntax;
3. load the exact preview ID/digest/snapshot version;
4. reject an expired or already terminal preview;
5. load the exact unexpired confirmation binding;
6. durably create one `queued` operation and advance it to `running`;
7. resolve the private selection and run the same source adapter again;
8. revalidate the adapter result, profile evidence tuple, fixture marker,
   candidate batch, stable complete-source snapshot, and optional exact
   bindings;
9. recalculate duplicate/conflict/new decisions against the current database;
10. compare the complete digest/snapshot decision with preview; and
11. begin the commit transaction, atomically repeat its preview, confirmation,
    operation, expiry, and database-decision checks, and write only if every
    value still matches.

A changed source snapshot is `historical_import_source_changed`; any other
preview/source/database/binding decision mismatch is
`historical_import_preview_stale`. Neither condition is a partial success, and
both require a new preview and confirmation. The importer does not overwrite a
stale decision, retry another parser, or refresh the old preview in place.

A syntactically valid but wrong preview ID/digest/snapshot binding,
confirmation ID/binding, or idempotency reuse is rejected before an operation
exists. Such a caller error creates no `hop_`, does not consume confirmation,
and does not erase an otherwise live preview; the original valid
confirmation/request may still use it until its fixed expiry. Once the exact
live preview and confirmation binding has been accepted and the durable
operation exists, success, deterministic revalidation rejection, or failure is
terminal for that preview and erases its private state.

## Commit, progress, idempotency, and result

The commit request uses the exact fields in `import-request.schema.json`:
request ID, idempotency key, confirmation ID, preview ID/digest, and snapshot
version. One `hik_` value identifies one exact commit request. The store keeps
only its SHA-256 and the exact canonical request fingerprint.

The first valid commit creates one `hop_` operation and advances through
`queued`, `running`, and exactly one terminal state. A deterministic
revalidation, expiry, or consumption race after `running` is `rejected` with
`transaction_outcome = not_started`, zero processed/outcome counts, no result,
and its fixed cause. A failure after the domain transaction is attempted is
`failed` with `transaction_outcome = rolled_back`, zero processed/outcome
counts, no result, and only `historical_import_transaction_failed`. A
successful transaction is `succeeded` / `committed` and has the only available
result. A pre-domain store failure after the operation exists is terminal
`rejected` / `not_started` with its fixed store code because no domain
transaction began.

`import-status` is the only progress projection. Its lifecycle/version pairs
are exactly `queued`/1, `queued -> running`/2, and the corresponding three-step
terminal lifecycle/3. Its processed/count values cannot exceed total, pending
and unsuccessful operations keep all processed/outcome counts at zero, and
`result_available` is true only after a successful committed result exists. A
request may execute inline, but the UI renders the running state while commit
is outstanding and may recover the same status by operation ID after a
response loss. There is no import retry worker or source background scan.

Queued/running state is durable and readable. On startup, recovery considers
only a pending operation whose bound preview has reached its persisted expiry;
it records sanitized `failed` / `rolled_back` /
`historical_import_transaction_failed` state and no domain result. An
unexpired operation may belong to another live process and is never reclaimed.
If that process holds the domain writer transaction, recovery serializes after
it and the conditional transition cannot replace its committed terminal state.

The observation/provenance/conflict/confirmation/idempotency/result write is
one `BEGIN IMMEDIATE` transaction. Every new observation, all of its fields and
provenance, exact optional binding, duplicate/conflict decision, confirmation
consumption, operation result, and idempotency result commit together or none
does. On an injected, busy, I/O, constraint, or unexpected failure after that
transaction is attempted, all domain writes roll back; a separate sanitized
terminal operation records
`transaction_outcome = rolled_back` and the fixed
`historical_import_transaction_failed` code in a separate recovery-safe status
write, but it cannot expose or imply a partial observation.

Idempotency is exact:

- same `hik_` plus byte-identical canonical commit request returns the existing
  result with `idempotency_outcome = replayed` and performs no source or domain
  write;
- same `hik_` with any different request field returns
  `historical_import_idempotency_conflict` and does not consume confirmation;
- a different key against an already committed exact candidate batch produces
  the ordinary deterministic duplicate/no-op decisions, not duplicate rows;
  and
- concurrent applications serialize through `BEGIN IMMEDIATE` and the unique
  admitted-source identity.

An exact repeat is a record-level no-op. A same-source-record different-value
candidate is a record-level conflict: the existing observation is preserved,
only fixed conflicting field names and canonical fingerprints are recorded,
and other new candidates may commit in the same all-or-none transaction.
Unsupported/malformed input, profile/fixture/schema/provenance failure,
source/preview staleness, or transaction failure rejects the whole operation.
There is no partial parser batch, partial provenance row, or partial commit.

Successful results use safe observation IDs and safe `hrr_` record decisions.
They disclose available candidate/new-observation/duplicate/conflict counts,
while new Session/Event counts stay unavailable for the generic candidate
contract. Every admitted observation is `partial` with exactly
`historical_summary_only`, has `content_state = not_captured`, and lists the
missing capability families actually absent. Retention is
`not_applicable`, creates zero items, is never pinned, and has no deletion
state. Workflow-v1 schemas contain no captured-content or raw-retention branch.

## Public routes and CLI parity

The Local Monitor surface is:

| Route | Request / response |
| --- | --- |
| `GET /historical-import` | server-rendered workflow/history/observation page |
| `POST /api/historical-import/v1/previews` | private source-selection request -> workflow preview |
| `GET /api/historical-import/v1/previews/{preview_id}` | workflow preview |
| `POST /api/historical-import/v1/confirmations` | confirmation request -> issued confirmation |
| `POST /api/historical-import/v1/imports` | import request -> final import result; `Location` names status |
| `GET /api/historical-import/v1/imports/{operation_id}` | import status only |
| `GET /api/historical-import/v1/imports/{operation_id}/result` | final result only when available |
| `GET /api/historical-import/v1/history?limit=<1..100>` | terminal history, newest first |
| `GET /api/historical-import/v1/observations?limit=<1..100>&cursor=<hoc_...>` | historical observations only |
| `GET /api/historical-import/v1/observations/{observation_id}` | one historical observation detail |

All JSON routes return `Cache-Control: no-store`. POST routes require loopback
Host validation, same-origin, `x-monitor-csrf: local-monitor`, and
`application/json`; cross-site writes are rejected before parsing. GET routes
are sanitized. `--sanitized-only` keeps the page and all metadata-only routes
available.

Config CLI exposes `historical-import preview`, `confirm`, `commit`, `status`,
`result`, `history`, and `observations` over the same application DTOs. The
first three read the strict 1 MiB request file; reads use opaque IDs and bounded
limit/cursor options. CLI and HTTP may differ only in transport status/exit
mapping. They must return the same semantic result from the same service and
must not implement a second parser, decision engine, serializer, or store.

## UI contract

The existing two-item D042 sidebar is unchanged. `/diagnostics` contains an
explicit historical-import/integration card linking to `/historical-import`;
nothing is discovered or probed by page load. The page presents named source
cards and fixed source badges for live OTel, saved raw, Hook/SDK, historical,
and unsupported inputs. Selecting a historical card shows its exact probe
scope, current support, metadata-only posture, and risk before enabling the
one-probe consent control.

The ordered interaction is source selection -> consent -> preview -> explicit
confirmation -> progress -> result. The preview shows source/tier/adapter/
version/format, availability-aware source/date and candidate counts,
duplicates/conflicts/new observations, unavailable new Session/Event counts,
exact merge basis, completeness/missing capabilities, exclusions, and
retention impact. Unsupported current profiles keep confirmation/commit
disabled and explain the fixed rejection code. There is no hidden auto-import,
background scan, include-content toggle, or automatic analysis.

The page has visually explicit `live` and `historical` tabs. `live` consumes
the unchanged Session workspace v1 reads and links to existing Session detail.
`historical` consumes only #79 observation reads. The browser never unions,
coalesces, or sorts the two identity domains as one dataset. Historical rows
show their source badge, `partial` / `historical_summary_only`, content state,
and missing capabilities; historical-only trace controls are disabled. An
`attached_exact` row may show its server-generated same-origin Session
navigation target, but that target does not turn the observation into a
Session. Result may show an optional navigation-only historical-analysis link
when that separate surface exists; it never starts #48/#73 analysis.

All fixed/source metadata is rendered as inert text using Razor encoding or
DOM `textContent`; no workflow field is interpreted as HTML.

## Status and error mapping

Error responses have exactly `{ "error": "<fixed-code>" }`. They contain no
source selection, path, rejected value, raw body, internal candidate/source
key, confirmation/idempotency material, database path, or exception text.

| HTTP | Meaning |
| ---: | --- |
| `200` | every successful preview, confirmation, import, replay, status, result, history, or observation DTO |
| `400` | workflow `historical_import_request_invalid`, or transport `invalid_host` |
| `403` | transport `cross_origin_forbidden` or `csrf_required` |
| `413` | transport `request_too_large` |
| `415` | transport `unsupported_media_type` |
| `404` | fixed preview, operation, or observation not-found code |
| `409` | stale/source-changed/no-eligible, confirmation invalid/consumed, idempotency conflict, result unavailable, or profile/candidate/fixture rejection |
| `410` | fixed preview or confirmation expired code |
| `503` | store busy/unavailable or transaction failure |

The transport codes above follow the repository-wide HTTP boundary and are
outside the historical-import workflow registry. They are returned before
workflow parsing/admission where applicable. The fixed workflow/domain codes
are:

- `historical_import_request_invalid`
- `historical_import_no_eligible_candidates`
- `historical_import_preview_not_found`
- `historical_import_preview_expired`
- `historical_import_preview_stale`
- `historical_import_source_changed`
- `historical_import_confirmation_invalid`
- `historical_import_confirmation_expired`
- `historical_import_confirmation_consumed`
- `historical_import_idempotency_conflict`
- `historical_import_operation_not_found`
- `historical_import_observation_not_found`
- `historical_import_profile_not_admitted`
- `historical_import_candidate_invalid`
- `historical_import_fixture_not_source_support_evidence`
- `historical_import_result_not_available`
- `historical_import_transaction_failed`
- `historical_import_store_busy`
- `historical_import_store_unavailable`

Config CLI adds only `historical_import_invalid_arguments` for command-line
grammar/arity failure before a workflow request exists. All other CLI failures
use the same domain code as HTTP.

The producer diagnostic codes remain the separate fixed registry above and
appear only as sanitized preview diagnostics/exclusions.

## Persistence and migration

Issue #76 has no persistence schema and no migration. #77 and #78 do not gain
write authority from their detectors. #79 owns
`schema_version(component='historical_import', version=1)` and exactly the
seven tables defined in
[Raw Store And Normalization](../layers/raw-store-normalization.md). The
component is additive and independent of Session/retention versions. Fresh and
actual pre-#79 database upgrades create all tables empty in one transaction;
stamped partial v1 and newer versions fail closed without repair/downgrade.
There is no fabricated v0 migration fixture.

The unique admitted-source identity, field/provenance parity, transaction
boundary, private-locator lifetime, and raw/retention prohibition in that layer
are normative. Except for the one bounded private locator column in an
actionable preview row, no #79 table may contain a source path. No #79 table may
contain a raw payload or conflicting value, and no public projection may expose
an internal key, parallel pin state, or deletion queue. Host startup
creates/validates this component before routes are mapped;
failure does not redefine readiness fields.
Config CLI may create or validate the component only after the existing file
proves the current Local Monitor owner marker and base-table set. It retains a
no-link lease for that exact file identity and verifies every SQLite connection
against the lease and canonical main-database path before use; unrelated,
missing, moved, or replaced databases fail store-unavailable without schema
mutation.

## Required validation corpus

The v1 corpus contains:

- closed schemas for source profiles, adapter results, candidate batches,
  producer previews, workflow requests/results/reads, and merge cases;
- the two current unsupported source profiles;
- detected-unsupported, malformed, and missing-reference adapter examples;
- one synthetic candidate batch marked as non-support evidence;
- the exact availability-aware zero-candidate #79 preview;
- synthetic trusted-admission shapes that cannot activate production support;
- deterministic deduplication, conflict, exact-binding, completeness, and
  metadata-only retention cases;
- fresh/additive migration, stamped-partial/newer rejection, restart read, and
  all-stage rollback cases; and
- API/CLI/UI/Playwright current-unsupported and injected-positive workflows.

Repository tests must validate every JSON artifact against its schema, assert
the empty support/allowlist state, reject unknown properties and invalid types,
ensure the synthetic candidate contains no trace authority, and pin the exact
zero-candidate and merge decisions. Positive machinery tests must inject only
the typed internal seam and must prove that fixture markers, HTTP/CLI candidate
payloads, and current real profiles remain rejected. Tests must also prove
source/database staleness, idempotent replay/conflict, transactional rollback,
exact duplicate/conflict precedence, no Session/Run/Event/retention writes,
private-key/path/raw no-leak, inert rendering, `--sanitized-only`, progress/
result/history, and the live/historical read-model separation.
