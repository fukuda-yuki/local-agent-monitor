# Historical Instruction Analysis Interface

Issue #73 analyzes one persisted Issue #72 extraction and produces historical
instruction findings through the unchanged Issue #59 finding contract. This
interface owns the local runner, terminal states, post-validation, provenance,
repository-safe historical receipt, and the exact handoff consumed by Issue
#75.

The analysis never queries Session, trace, raw telemetry, instruction-finding,
repository, workspace, or source-import stores. Issue #72 remains the sole
history boundary. Efficiency analysis, UI, proposal/apply behavior, adoption,
effect measurement, and source import are outside this interface.

## Versions

The fixed versions are:

```text
historical-instruction-analysis.request.v1
historical-instruction-analysis.receipt.v1
historical-instruction-analysis.read.v1
historical-instruction-analysis.prompt.v1
```

The nested finding carrier remains exactly:

```text
instruction-finding-handoff.v1
```

Issue #73 adds no finding category, verdict, template, candidate field, hash
rule, or parser. Exact handoff bytes must be accepted by
`InstructionFindingHandoffConsumerV1.Validate` before a successful or
zero-finding result is committed or returned.

## User-triggered request and provenance

Each explicit start appends a new positive historical analysis run ID. A retry
is another start and therefore another run; it never overwrites an earlier
failure or result.

Start attempts a repository-safe #72 projection. If the #72 owner already
rejects persisted bytes, start still appends the run with the closed unavailable
projection; the running recheck terminates as `extraction_invalid`. Corrupt #72
bytes are never returned or interpreted to populate the projection.

The immutable request records:

- request schema version;
- Issue #72 extraction ID and exact raw-local SHA-256 expected by the caller;
- bounded model and provider identifiers;
- lowercase 64-hex configuration SHA-256, computed without credentials;
- timeout in the inclusive range `1..3,600,000` milliseconds;
- exact prompt-template version;
- canonical UTC request time.

Model and provider identifiers use the bounded metadata-token grammar and may
not contain whitespace, control characters, URI/path separators, credentials,
or free text. They must also pass the shared sensitive-carrier gate; normalized
credential shapes including `api-key` / `api_key`, `sk-...`, and
`github_pat_...` are rejected even when they match the token grammar.
Configuration bytes, provider base URLs, API keys, environment
values, and exception text are never stored. The prompt-template version must
equal `historical-instruction-analysis.prompt.v1`; silent fallback to another
template is forbidden.

## State machine

The exact machine states are:

```text
queued
running
succeeded
zero_findings
no_eligible_sessions
content_unavailable
stale_extraction
extraction_invalid
invalid_citation
provider_partial
provider_failed
timed_out
canceled
```

Only `queued -> running -> <one terminal state>` is valid. A terminal row is
immutable. `succeeded` requires one or more validated findings.
`zero_findings` requires a provider-complete response and the canonical empty
#59 handoff. Neither is interchangeable with `no_eligible_sessions`, an
unavailable raw-local boundary, timeout, provider failure/partial output, stale
input, or rejected citation.

`stale_extraction` means only that the extraction is absent or that a valid
persisted extraction does not match the requested ID/checksum.
`extraction_invalid` means the #72 owner rejected malformed, oversized,
checksum-invalid, noncanonical, or semantically invalid persisted data.
`timed_out` is used only when the run-owned timeout source is the first owned
cancellation cause; `canceled` is used when caller cancellation is first.
Provider self-cancellation before either source fires is `provider_failed`.
No partial output is published for cancellation or failure.

`provider_partial` covers both an explicit incomplete provider response and a
provider failure after one or more submissions. Partial submissions are not
published as findings and do not create a successful handoff.

## Bounded provider input

After entering `running`, the runner reopens the exact extraction through
`HistoricalEvidenceApplicationServiceV1.Get`. It verifies the extraction ID,
raw-local checksum, paired representation, and canonical persisted bytes before
constructing provider input.

The provider receives only:

- an independently deserialized view of the exact persisted Issue #72
  raw-local canonical bytes, plus a separate byte-array copy;
- its already-bounded included/excluded Sessions, capability/completeness
  distribution, evidence groups/references, and allowed descriptors;
- the immutable run provenance and prompt-template version;
- a run-local closed submission carrier.

It receives no store, query service, filesystem path, repository handle, or
history-search tool. The provider view and byte array share no mutable object
with the owner-validated extraction retained for post-validation. Provider
mutation of a list element or nested value therefore cannot change the #72
facts against which citations and recurrence are derived. Tests must prove a
completed run can use a persisted extraction after its original
Session/finding sources are unavailable and that hostile provider mutation
cannot enter the #59/#73 result.

If the extraction is absent or its expected checksum differs, the state is
`stale_extraction` and the provider is not called. If the extraction is
`sanitized_only`, the state is `content_unavailable` and the raw-bearing
provider is not called. Descriptor-unavailable Sessions may still contribute
their bounded non-descriptor evidence groups; their descriptor/content state
stays explicit in the bounded provider input. An empty included set is
`no_eligible_sessions`. The `sanitized_only` check precedes the empty-set check,
so an empty sanitized extraction is `content_unavailable`. Owner validation
failure while reopening stored #72 data is `extraction_invalid`.

## Provider submission

The provider may submit only closed data:

- one exact anchor trace ID shared by every submission in the run;
- one Issue #59 category, assessed verdict, and extractor source;
- exact raw-local evidence references from the unique Session owning that
  anchor: at least one anchor ref, plus only mapped non-anchor refs whose trace
  relation is valid under the unchanged #59 contract;
- one or more exact Issue #72 supporting group IDs.

It cannot submit a title, summary, explanation, instruction, target, rule text,
source excerpt, or arbitrary metadata. A complete response with no submissions
is valid.

The runner resolves exactly one included Session that owns an anchor-position
reference for the provider anchor trace. Zero or multiple owning Sessions reject
the result. It builds the final #59 evidence index only from mapped groups in
that Session: anchor-position refs must use the provider anchor trace and
non-anchor refs must use another trace, exactly as #59 requires. Submitted
final refs must resolve to that same Session and include an anchor ref. Other
Sessions never contribute final finding refs; they participate only in the
independent recurrence check below. This is a named #59-compatible v1 extension
inside #73 evidence, not broader historical search or a change to #59 storage.

`turn_rollup` supplies
`turn`, `error_span` and `retry_chain` supply `error_or_retry_span`, and
`user_correction` supplies `instruction_span`. Other groups neither count nor
enter the historical recurrence projection and do not invent a #59 evidence
kind.

## Post-validation and recurrence

Before invoking the #59 producer, the runner rejects the whole result when:

- an anchor or evidence reference is absent from the included Issue #72 group
  index;
- a supporting group ID is absent, excluded, or belongs to a different
  extraction;
- the provider anchor has zero or multiple owning Sessions, a final evidence
  ref belongs to another Session, no anchor ref is submitted, or a ref violates
  #59's anchor/non-anchor trace relation;
- an evidence reference cannot resolve uniquely to its included Session,
  trace, span/turn, and relative position;
- submissions use different anchors;
- a category, verdict, or extractor source is outside Issue #59;
- a provider response is null, structurally invalid, or incomplete.

Historical support is derived, never trusted from the provider. For each
submitted category, the runner evaluates every owning Session independently
using only exact references and #59 evidence kinds from that Session's resolved
supporting groups. The unchanged #59 producer applies the same category-specific
minimum to a Session-local draft. A Session counts toward recurrence only when
that same-category draft remains `supported`.

Unrelated, unmapped, or redundant groups are excluded from the recurrence
count. For each grounded Session, the persisted support groups are the
deterministic minimal ordered set whose removal would make the unchanged #59
category check no longer supported. When no Session is grounded, the runner
instead retains the deterministic ordered subset of submitted groups that
contains the exact resolved provider evidence references; unrelated submitted
groups remain excluded. This fallback is evidence projection, not recurrence.

`recurring_count` is the exact number of independently grounded distinct
Sessions. `support_kind` is `insufficient_support` for zero, `single_session`
for exactly one, and `recurring` for two or more. Supporting Session/group IDs,
evidence refs, and source/completeness distributions describe the retained
exact support projection and may therefore be non-empty when
`recurring_count` is zero. For grounded projections the recurring count equals
the supporting Session count; the zero-grounded fallback has exactly one
supporting Session because every retained exact reference belongs to the unique
provider-anchor Session. No other non-empty support projection may have a zero
recurring count. A provider-assessed `supported` submission with
fewer than two grounded Sessions is passed to #59 as `weak`, which makes it
ineligible. Exact/resolved citations with zero grounding are not
`invalid_citation`; structurally invalid or unresolved references remain so.
Provider-assessed `weak` and `incomplete` verdicts are preserved and never
upgraded, including at zero grounding. Candidate eligibility is copied from
the resulting #59 receipt and additionally requires `recurring_count >= 2`;
the receipt/read validators reject an otherwise canonical supported/eligible
finding with zero or one grounded Session. Issue #73 has no promotion shortcut.

Source-version, source-surface, source-kind, and completeness distributions are
derived only from the retained exact support projection. Mixed values remain
explicit in the historical finding projection; missing or partial evidence is
never interpreted as zero or full evidence.

## Repository-safe receipt

`historical-instruction-analysis.receipt.v1` contains:

- historical analysis run ID;
- extraction ID and raw-local SHA-256;
- terminal state and immutable model/provider/config/timeout/template
  provenance;
- extraction truncation and content-availability flags;
- exact Issue #72 repository-safe completeness/source distributions;
- for each emitted #59 finding: finding ID, final verdict, candidate
  eligibility, support kind (`recurring`, `single_session`, or
  `insufficient_support`), supporting
  Session tokens, supporting group IDs, recurring count, source/version/kind
  and completeness distributions, and exact repository-safe evidence refs;
- canonical #59 handoff SHA-256 and trusted-store linkage for successful or
  zero-finding states.

Supporting Session values and evidence references use the already-paired Issue
#72 repository-safe tokens. Group IDs are the frozen Issue #72 deterministic
IDs. The receipt never contains a raw Session/trace/span ID or a raw-local
descriptor.

The fixed gap summary, suggested next-time instruction, optional target hint,
and candidate linkage remain in the nested #59 receipt/candidate. Issue #75
joins them by `finding_id`; it does not infer, rewrite, or upgrade them.

## Persistence

The Issue #73 owner stores the immutable request/state and the repository-safe
dataset projection independently of outcome. The projection preserves
truncation, sanitized/content flags, and completeness/source distributions for
`queued`, `running`, and every terminal state. Only for
`succeeded` or `zero_findings`, canonical receipt and #59 handoff bytes with
separate lowercase SHA-256 values. Terminal completion of both carriers is one
transaction. Reads verify size, checksum, canonical reserialization, request
provenance, state invariants, finding linkage, and the exact #59 consumer before
returning a DTO.

`historical-instruction-analysis.read.v1` is the exact read DTO for #75. Its
shared consumer validates schema/run/request provenance, timestamp and state
invariants, the safe dataset projection, success-receipt linkage, and canonical
#59 bytes. A non-success state has no receipt/handoff but retains the projection.
`content_unavailable` requires `sanitized_only=true` and
`content_available=false`; `no_eligible_sessions` requires a non-sanitized,
content-capable projection with empty dataset distributions. Provider-stage
terminal states require the non-sanitized content-capable projection and a
positive Session total. `sanitized_only=false` with
`content_available=false` is the unavailable projection and requires all three
distribution lists to be empty.

The read boundary derives the Session total from completeness counts. That
total must equal the source-kind total, and every capability count must be no
greater than it. These relationships are revalidated independently of #72 so
a forged or corrupted #73 row cannot expose contradictory owner facts to #75.

Persistence is insert/new-run plus compare-and-set transitions. There is no
retry rewrite, partial-result promotion, or insert-or-replace path.

This is a new standalone persistence component at version 1. It records the
shared `schema_version` row `historical_instruction_analysis = 1` and owns only
objects named `historical_instruction_analysis_*`, beginning with
`historical_instruction_analysis_runs`. It does not bump the monitor, Session,
or another existing component version. On first creation the owner row is
written last, in the same transaction as its owned objects. An owned object
without the owner row, a future owner version, or a malformed owned schema is
rejected without mutation.

## Exact Issue #75 handoff

Issue #75 receives one read model with:

```text
schema_version = historical-instruction-analysis.read.v1
run_id
state
requested_at / started_at / completed_at
extraction_id / extraction_sha256
model / provider / configuration_sha256 / timeout_ms / prompt_template_version
truncated_before / sanitized_only / content_available
dataset completeness/source distributions
historical finding support projections
canonical instruction-finding-handoff.v1 bytes when state permits
```

This supports the required UI distinctions: no eligible Sessions, mixed
source/completeness, truncated extraction, content unavailable, queued,
running, succeeded, zero findings, failed provider, timed out, invalid citation,
stale extraction, invalid extraction, canceled execution, and partial provider
output. #75 must validate the canonical
handoff bytes with the shared #59 consumer and preserve every final verdict and
eligibility value.

Normal Local Monitor construction initializes the component-v1 store and
registers a #73 composition beside the #72 application service. The read
boundary is always available; runner creation requires an explicitly supplied
`IHistoricalInstructionAnalysisProviderV1`. No provider, credential,
raw-content execution, HTTP route, background worker, or UI is enabled by
default. Automated fake-provider coverage is not genuine provider execution;
the latter remains `blocked_external` until separate live evidence exists.

The composition also receives the current host raw-execution permission.
Under `--sanitized-only`, read/status remains available but `CreateRunner`
fails closed before a run or provider call, even when the database contains a
raw extraction created by an earlier non-sanitized host. A persisted extraction
selection is evidence provenance, not authority to override the current
runtime mode.

## Privacy and validation matrix

Focused fixtures cover:

- A: the same category grounded in two distinct Sessions, eligible only after
  #59 category minima also pass;
- B: the same apparent pattern grounded in one Session, retained as weak and
  ineligible;
- an unrelated or category-under-minimum second Session cannot promote A to
  recurring and is absent from the support projection;
- an exact/resolved provider-assessed `supported` submission with zero grounded
  Sessions is retained as `insufficient_support`, `weak`, and ineligible;
  provider-assessed `weak` / `incomplete` remain unchanged at the same boundary;
- `scope_boundary_missing` is grounded only when one Session independently
  supplies both its required anchor Turn and anchor error/retry evidence;
- two distinct Sessions whose `ambiguity` minimum depends on one anchor Turn
  plus exact context each can produce a final supported/eligible recurrence,
  while context from a non-anchor Session cannot enter the final #59 finding;
- a canonical #59 supported/eligible handoff paired with a zero/one-grounded
  #73 receipt is rejected at receipt, store/read, and #75 consumer boundaries;
- provider attempts to replace evidence-group list elements or nested refs do
  not mutate the owner snapshot and cannot enter the #59/#73 result;
- a sanitized-only host with a preexisting raw extraction cannot construct a
  runner or invoke an explicitly supplied provider, while safe reads remain;
- provider-complete zero findings;
- exact timeout bounds (`3,600,000` accepted; `3,600,001` rejected), owned
  timeout, immediate provider self-cancellation, caller cancellation, provider
  failure after zero submissions, and provider partial after
  accepted submissions;
- unresolved group/reference citation;
- stale checksum, corrupt #72 persistence, empty dataset, and
  empty+sanitized/content-unavailable precedence;
- mixed source/completeness distribution;
- a bounded raw-local descriptor marker never enters the receipt or handoff;
- `sk-...`, `github_pat_...`, `api-key`, other path/credential-like provenance,
  and path-like repository-safe distribution
  values are rejected rather than persisted;
- canonical #59 consumer compatibility;
- exact read-consumer compatibility for queued, running, and every terminal
  state, rejection of unavailable/nonempty, provider-stage/empty, mismatched
  Session totals, and over-counted capability projections, plus provider-free
  production composition resolution.

The A/B expected judgement is the Issue #73 human-review record: recurrence is
supported only for A; B must not be presented as a Recommended-equivalent
candidate. Any later change to that judgement requires an explicit versioned
contract update rather than a fixture-only relabel.
