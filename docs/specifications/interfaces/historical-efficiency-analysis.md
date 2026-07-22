# Historical Efficiency Analysis Specification

## Status and authority

This specification freezes the Issue #74 deterministic historical-efficiency
analysis contract consumed by Issue #75. The only analysis input is the exact
canonical `historical-evidence.repository-safe.v1` representation produced and
persisted by Issue #72. The analyzer does not scan Session, raw telemetry,
finding, retention, or import stores and does not reinterpret the raw-local
representation.

The driver registry is `historical-efficiency-driver-registry.v1`; every rule
has driver version `1`. The receipt schema is
[`historical-efficiency-receipt.v1`](../contracts/historical-efficiency/v1/historical-efficiency-receipt.schema.json).
A formula, threshold, cohort key, minimum sample, evidence rule, ordering rule,
quality gate, summary, or mitigation change requires a new driver version. A
receipt-shape or canonical-byte change requires a new receipt schema version.

This boundary adds no HTTP route, database schema, migration, UI, source
adapter, historical import, monetary-cost field, proposal, apply action, or
effect verdict. The fixed mitigations are review directions, not verified
recommendations or model/provider changes.
The optional AI narrative layer is omitted and classified `not_applicable` for
Issue #74. This lane does not add or change any #91 surface-registry row; the
existing `historical-analysis` placeholder remains owned by Issue #75.

## Input integrity and bounds

`HistoricalEfficiencyAnalyzerV1` accepts one Issue #72 extraction envelope. It
uses only `RepositorySafeBytes` and `RepositorySafeSha256`, requires the hash to
match the exact canonical bytes, deserializes through the Issue #72 strict
reader, and requires representation and schema version
`historical-evidence.repository-safe.v1`. Invalid, noncanonical, mismatched, or
raw-local input fails the complete analysis with fixed code
`invalid_historical_efficiency_input`; it is never repaired, truncated, or
partially analyzed.

The Issue #72 bounds therefore remain the complete input bounds: at most 200
included Sessions, 401 excluded decisions, 256 evidence groups per included
Session, 16 exact references per group, and 67,108,864 canonical bytes. The
analyzer performs no network, provider, clock, random, file, or out-of-band
store read. All arithmetic uses checked .NET `decimal`; overflow maps to the
same fixed invalid-input failure rather than wrapping or dropping a category.

An explicit numeric zero remains an observed value. An absent group, scalar,
capability, model, duration, or quality reference remains absent. Missing
values never become zero, pass, success, or improvement.

## Canonical evidence and scalar rules

Evidence references are copied byte-for-byte from the repository-safe Issue
#72 dataset, de-duplicated, and sorted by Session token, trace token, nullable
span token, nullable turn index, then relative position. Source Session tokens
sort ordinally. Every driver includes all metric references used by its
formula, including all cohort references used to calculate a median or
percentile. Quality references are separate and include all exact quality
groups for the driver's source Sessions.

Token observations are keyed by the complete evidence-reference tuple. At one
reference, total tokens are the exact `total_token` value when present;
otherwise they are `input_token + output_token` only when both components are
present. The total component takes precedence so input/output are never added
to an already observed total. Conflicting duplicate components invalidate the
input. A Session token total is available only when at least one exact total is
available and every `turn_rollup` reference has an available total. Unknown
components and unknown turns are not zero-filled.

Medians sort exact decimal values. An even median is
`lower + (upper - lower) / 2`. Percentile 75 uses nearest rank: sorted element
`ceil(0.75 * n)` with a one-based rank. Ratios require a positive denominator
and receive no intermediate rounding.

Comparative token and duration cohorts use this exact key:

```text
(source_surface, source_version, adapter_version, single distinct model_ref)
```

A Session without exactly one distinct model reference is not admitted to a
comparative cohort. Distinct cohort keys are evaluated separately. The receipt
records fixed comparison notes when the extraction contains mixed source
surface, source version, adapter version, or model dimensions; the analyzer
never compares across those boundaries.

## Versioned driver registry

Registry and receipt order is exactly the table order below.

| Category / rule source | Required Issue #72 capability/group | Grouping and minimum sample | Formula and threshold | Fixed mitigation |
| --- | --- | --- | --- | --- |
| `token_volume` / `historical-efficiency/token-volume@1` | `token_rollup`; complete per-Session token scalar | comparative cohort; 4 Sessions | For each Session, `session_total`. Match when `session_total > nearest_rank_p75` and `session_total / median >= 1.50`; median must be positive. | `review_high_token_sessions` |
| `context_growth` / `historical-efficiency/context-growth@1` | `turn_rollup` + `token_rollup(input_token)` | Session + trace; maximal contiguous nondecreasing run; 3 turns | Order by positive `turn_index`; a missing input or turn-index gap breaks the run. First input must be positive. Match when `last_input / first_input >= 1.75`. | `bound_conversation_context` |
| `cache_inefficiency` / `historical-efficiency/cache-inefficiency@1` | `turn_rollup` + `token_rollup(input_token)` + `cache_rollup(cache_read_token)` | Session + trace; 3 eligible turns before first-turn exclusion | An eligible turn has observed nonnegative input and cache-read values. Exclude the first eligible turn. Included input must total at least 10,000 tokens. Match when `sum(cache_read) / sum(input) < 0.20`. | `review_cache_reuse` |
| `retry_overhead` / `historical-efficiency/retry-overhead@1` | `retry_chain` | canonical reference sequence; 1 chain | Attempt count is the number of distinct ordered exact references, never a caller scalar. Match at 2 or more attempts; overhead is `attempts - 1`. Byte-identical chain identities are counted once. | `review_retry_trigger` |
| `tool_call_volume` / `historical-efficiency/tool-call-volume@1` | `repeated_tool_call` with Issue #72 exact call capability | one exact repeated-call group; 3 calls | Call count is the number of distinct exact references in that group. Match at 3 or more. This is exact repeated-call volume, not an estimate of all tool calls. | `review_repeated_tool_calls` |
| `tool_failure_overhead` / `historical-efficiency/tool-failure-overhead@1` | unavailable in historical-evidence v1 | unavailable | Historical-evidence v1 has generic `error_span` groups but no exact tool-call classification/status denominator. The category is always `unavailable` with `exact_tool_failure_status_unavailable`; generic errors are never relabeled as tool failures. | `review_tool_failure_evidence` |
| `permission_wait` / `historical-efficiency/permission-wait@1` | `permission_wait` with numeric unit `seconds` | Session + trace; 1 exact wait | Match when exact maximum wait is at least 30 seconds or exact total wait is at least 60 seconds. Groups with another/missing unit or scalar are absent, not zero. | `review_permission_plan` |
| `subagent_fanout` / `historical-efficiency/subagent-fanout@1` | `subagent_fan_out` with Issue #72 exact ownership capability | Session + trace; 2 exact ownership groups | Fan-out is the count of distinct group identities. Match at 2 or more. Repository-safe group identity remains opaque; ownership is never inferred from hierarchy/order. | `review_subagent_scope` |
| `duration_outlier` / `historical-efficiency/duration-outlier@1` | exact `duration_observations` | comparative cohort; 4 Sessions | Per-Session duration is the maximum exact duration observation. Match when `session_duration > nearest_rank_p75` and `session_duration / median >= 1.50`; median must be positive. | `review_duration_outlier` |
| `model_mix_observation` / `historical-efficiency/model-mix-observation@1` | exact `model_observations` | extraction; 2 distinct model refs | Match when two or more distinct opaque model refs occur. This rule is observational and its verdict can never exceed `weak`. | `separate_model_cohorts` |

`token_volume` and `duration_outlier` can emit one driver for each matching
subject Session in a partition. `context_growth` emits one driver for each
qualifying maximal run. `cache_inefficiency`, `permission_wait`, and
`subagent_fanout` emit at most one driver per Session/trace. Retry and repeated
tool rules emit one driver per canonical source group. Model mix emits at most
one extraction-level driver. `tool_failure_overhead` emits none in v1.

## Coverage and no-driver behavior

The receipt contains exactly one `category_coverage` row per registry category
in registry order. Its state is one of:

- `matched`: one or more driver receipts were emitted;
- `no_match`: at least one complete minimum sample was evaluated and no
  threshold matched;
- `insufficient`: some required capability/evidence exists, but every possible
  evaluation lacks a required metric, dimension, or minimum sample;
- `unavailable`: no included Session supplies the required capability, or the
  registry explicitly marks the category unavailable.

Reason codes are fixed and ordered:

```text
exact_tool_failure_status_unavailable
missing_required_capability
missing_required_metric
mixed_evaluation_dimension
minimum_sample_unmet
no_threshold_match
```

`observed_sample_count` counts the largest complete sample evaluated for that
category; it never counts missing metrics. `eligible_session_count` counts only
Sessions with every required Issue #72 Boolean capability. Coverage also
preserves the Issue #72 completeness, source-kind, and capability
distributions, excluded count, and truncation state.

When no driver crosses a threshold, the receipt is still valid with
`state=zero_drivers`, an empty `drivers` array, and all ten coverage rows.
Otherwise state is `succeeded`. Input validation failure produces no receipt.

## Quality availability and verdict

Quality is derived only from exact `quality_reference` groups. Status `fail`
is negative; status `pass` is positive. Unknown status is non-decisive and is
not converted to either result. For an evaluated Session, any fail wins;
otherwise one or more pass values with no unknown value is positive; otherwise
quality is missing.

Analysis-level and per-driver quality availability use the fixed states:

- `regression_observed`: any source Session has exact `fail` evidence;
- `available`: every source Session has decisive positive evidence;
- `partial`: at least one but not every source Session has decisive evidence,
  or an unknown quality status exists;
- `unavailable`: no source Session has decisive quality evidence.

The driver verdict is evidence strength, never an effect/improvement verdict:

1. `incomplete` when a source Session is `partial` or
   `historical_summary`, or when the matched evaluation excluded a required
   observed metric;
2. `weak` when quality is not `available`, a mixed source/version/model note
   applies, or the category is `model_mix_observation`;
3. `supported` otherwise.

Thus low token/duration or any other efficiency observation cannot override a
quality failure. There is no `improved`, `regressed`, `effect`, score, verified,
or candidate-eligibility field. Every fixed repository-safe summary ends with
the invariant that the observation does not establish improvement.

## Receipt identity, canonical bytes, and privacy

The receipt ID is `historical-efficiency-receipt-` plus the first 16 SHA-256
digest bytes over domain
`copilot-agent-observability/historical-efficiency-receipt/v1` and
length-framed registry version, extraction ID, and exact Issue #72
repository-safe payload SHA-256. Driver IDs use a separate domain and bind the
rule source, subject Session, ordered source Sessions, metric and quality
references, exact observed/cohort values, verdict, and comparison notes.

Canonical JSON is UTF-8 without BOM, indentation, or trailing newline.
Properties follow the DTO declaration order; enums are lower snake case;
arrays follow the orders in this specification; decimal numbers use the
canonical `System.Text.Json` representation. Re-analyzing exact input under
the same registry therefore produces byte-equivalent receipts and the same
lowercase `payload_sha256` envelope value.
Canonical receipt bytes are bounded by the same 67,108,864-byte ceiling as the
input dataset; an over-limit receipt fails the complete analysis.

The receipt contains only opaque Issue #72 tokens, fixed codes/text, bounded
numeric values, timestamps already present in Issue #72 only where needed by
the source distribution, and exact repository-safe evidence references. It
contains no raw/local identifier, prompt, response, tool body/name, model name,
source version text, repository/workspace label, path, credential, PII,
provider text, price, currency, or monetary-cost value. Mitigation code/text is
registry-owned fixed text and cites the same exact metric evidence as its
driver. No AI narrative/provider/model/prompt-version record exists in v1.

## Exact Issue #75 handoff

The in-process handoff is `HistoricalEfficiencyAnalysisV1`:

- `Receipt`: the strict `historical-efficiency-receipt.v1` DTO;
- `CanonicalBytes`: the exact canonical receipt bytes;
- `PayloadSha256`: lowercase SHA-256 of those bytes.

The DTO fields are frozen by the machine-readable schema. Issue #75 consumes
the DTO/bytes without recomputing formulas, thresholds, verdicts, coverage, or
quality. It maps `succeeded` and `zero_drivers` directly to its corresponding
successful analysis presentation. Queue/running/timeout/provider/stale/
invalid-citation/failed orchestration states remain Issue #75 responsibilities
and are not fabricated by this analyzer. An input/hash/canonicality failure is
a failed analysis with no success DTO. #75 must distinguish `supported`,
`weak`, and `incomplete`, show category coverage reasons, preserve exact
evidence references, and display mitigations as inert fixed text.

## Required tests and fixture review

Automated coverage must include:

- exact registry categories/order/versions/formulas/thresholds;
- byte-equivalent repeated analysis and receipt/driver identity;
- token component precedence, median, nearest-rank p75, and source/model
  partitioning;
- context/cache boundaries, explicit zero, missing input/cache, and low N;
- retry-chain duplicate protection, repeated-call threshold, permission unit,
  fan-out ownership, duration outlier, and model mix;
- partial/historical/mixed-source coverage, quality unavailable/partial/fail,
  zero-driver state, and exact evidence reference resolution;
- strict repository-safe-only input and no raw/PII/path/price/currency/cost
  leakage; and
- a committed canonical synthetic handoff fixture reviewed for formula,
  evidence, quality, coverage, mitigation, ordering, and repository safety.
