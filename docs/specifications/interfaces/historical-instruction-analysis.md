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

The immutable request records:

- request schema version;
- Issue #72 extraction ID and exact raw-local SHA-256 expected by the caller;
- bounded model and provider identifiers;
- lowercase 64-hex configuration SHA-256, computed without credentials;
- positive timeout in milliseconds;
- exact prompt-template version;
- canonical UTC request time.

Model and provider identifiers use the bounded metadata-token grammar and may
not contain whitespace, control characters, URI/path separators, credentials,
or free text. Configuration bytes, provider base URLs, API keys, environment
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
invalid_citation
provider_partial
provider_failed
timed_out
```

Only `queued -> running -> <one terminal state>` is valid. A terminal row is
immutable. `succeeded` requires one or more validated findings.
`zero_findings` requires a provider-complete response and the canonical empty
#59 handoff. Neither is interchangeable with `no_eligible_sessions`, an
unavailable raw-local boundary, timeout, provider failure/partial output, stale
input, or rejected citation.

`provider_partial` covers both an explicit incomplete provider response and a
provider failure after one or more submissions. Partial submissions are not
published as findings and do not create a successful handoff.

## Bounded provider input

After entering `running`, the runner reopens the exact extraction through
`HistoricalEvidenceApplicationServiceV1.Get`. It verifies the extraction ID,
raw-local checksum, paired representation, and canonical persisted bytes before
constructing provider input.

The provider receives only:

- the exact persisted Issue #72 raw-local dataset and canonical bytes;
- its already-bounded included/excluded Sessions, capability/completeness
  distribution, evidence groups/references, and allowed descriptors;
- the immutable run provenance and prompt-template version;
- a run-local closed submission carrier.

It receives no store, query service, filesystem path, repository handle, or
history-search tool. Tests must prove a completed run can use a persisted
extraction after its original Session/finding sources are unavailable.

If the extraction is absent or its expected checksum differs, the state is
`stale_extraction` and the provider is not called. If the extraction is
`sanitized_only`, the state is `content_unavailable` and the raw-bearing
provider is not called. Descriptor-unavailable Sessions may still contribute
their bounded non-descriptor evidence groups; their descriptor/content state
stays explicit in the bounded provider input. An empty included set is
`no_eligible_sessions`.

## Provider submission

The provider may submit only closed data:

- one exact anchor trace ID shared by every submission in the run;
- one Issue #59 category, assessed verdict, and extractor source;
- one or more exact raw-local anchor evidence references;
- one or more exact Issue #72 supporting group IDs.

It cannot submit a title, summary, explanation, instruction, target, rule text,
source excerpt, or arbitrary metadata. A complete response with no submissions
is valid.

The runner builds the #59 evidence index only from exact references present in
the included raw-local groups for the selected anchor. `turn_rollup` supplies
`turn`, `error_span` and `retry_chain` supply `error_or_retry_span`, and
`user_correction` supplies `instruction_span`. Other groups may support the
historical recurrence projection but do not invent a #59 evidence kind.

## Post-validation and recurrence

Before invoking the #59 producer, the runner rejects the whole result when:

- an anchor or evidence reference is absent from the included Issue #72 group
  index;
- a supporting group ID is absent, excluded, or belongs to a different
  extraction;
- an evidence reference cannot resolve uniquely to its included Session,
  trace, span/turn, and relative position;
- submissions use different anchors;
- a category, verdict, or extractor source is outside Issue #59;
- a provider response is null, structurally invalid, or incomplete.

Historical support is derived, never trusted from the provider. Supporting
Session IDs are the distinct Sessions owning the resolved supporting groups.
A recurring claim requires at least two distinct Sessions. An assessed
`supported` submission with one distinct Session is passed to #59 as `weak`,
which makes it ineligible. Provider-assessed `weak` and `incomplete` verdicts
are preserved and never upgraded.

The #59 producer remains responsible for category-specific anchor evidence
minimums and may further downgrade a proposed `supported` verdict. Candidate
eligibility is copied from the resulting #59 receipt; Issue #73 has no
promotion shortcut.

Source-version, source-surface, source-kind, and completeness distributions are
derived from the exact supporting Sessions. Mixed values remain explicit in
the historical finding projection; missing or partial evidence is never
interpreted as zero or full evidence.

## Repository-safe receipt

`historical-instruction-analysis.receipt.v1` contains:

- historical analysis run ID;
- extraction ID and raw-local SHA-256;
- terminal state and immutable model/provider/config/timeout/template
  provenance;
- extraction truncation and content-availability flags;
- exact Issue #72 repository-safe completeness/source distributions;
- for each emitted #59 finding: finding ID, final verdict, candidate
  eligibility, support kind (`recurring` or `single_session`), supporting
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

The Issue #73 owner stores the immutable request/state and, only for
`succeeded` or `zero_findings`, canonical receipt and #59 handoff bytes with
separate lowercase SHA-256 values. Terminal completion of both carriers is one
transaction. Reads verify size, checksum, canonical reserialization, request
provenance, state invariants, finding linkage, and the exact #59 consumer before
returning a DTO.

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
stale extraction, and partial provider output. #75 must validate the canonical
handoff bytes with the shared #59 consumer and preserve every final verdict and
eligibility value.

## Privacy and validation matrix

Focused fixtures cover:

- A: the same category grounded in two distinct Sessions, eligible only after
  #59 category minima also pass;
- B: the same apparent pattern grounded in one Session, retained as weak and
  ineligible;
- provider-complete zero findings;
- timeout, provider failure after zero submissions, and provider partial after
  accepted submissions;
- unresolved group/reference citation;
- stale checksum, empty dataset, and sanitized-only/content-unavailable input;
- mixed source/completeness distribution;
- a bounded raw-local descriptor marker never enters the receipt or handoff;
- path/credential-like provenance and path-like repository-safe distribution
  values are rejected rather than persisted;
- canonical #59 consumer compatibility.

The A/B expected judgement is the Issue #73 human-review record: recurrence is
supported only for A; B must not be presented as a Recommended-equivalent
candidate. Any later change to that judgement requires an explicit versioned
contract update rather than a fixture-only relabel.
