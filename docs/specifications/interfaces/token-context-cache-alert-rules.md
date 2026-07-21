# Token, Context, And Cache Alert Rules

## Scope And Version

This specification defines the source-neutral Issue #82 rule pack consumed by
the frozen Issue #80 `alert.snapshot.v1` evaluator. The pack contains exactly
five compiled `IAlertRule` implementations. Every rule has version `1`; a
formula, threshold, eligibility, grouping, rounding, or evidence change
requires a new rule version.

The pack does not map producer fields. An adapter may declare the capabilities
below only after the Issue #61 source/version manifest establishes the field
semantics and authority. The current GitHub Copilot VS Code and Claude Code
manifests declare model/token/cache fields `unknown`, so real snapshots from
those sources remain capability-suppressed until a separately reviewed adapter
mapping supplies verified facts. Observed numeric zero is a value; an absent
metric is missing and is never replaced with zero.

## Stable Snapshot Names

All rules require capability `llm-call-classification`. Additional capability
names are:

| Capability | Meaning |
| --- | --- |
| `input-token-count` | `input-tokens` is an authoritative per-call input/context token count. |
| `output-token-count` | `output-tokens` is an authoritative per-call output token count. |
| `cache-read-token-count` | `cache-read-tokens` is an authoritative per-call cache-read count; unavailable/unknown differs from observed zero. |
| `model-identity` | `model-id` is present on each eligible call and identifies the effective model without exposing prompt content. |
| `token-semantics-version` | `input-token-semantics-version`, and where required `output-token-semantics-version` or `cache-read-token-semantics-version`, identify the verified normalization semantics. |
| `tool-schema-generation` | `tool-schema-generation` identifies a stable system/tool-schema generation. |
| `effective-context-limit` | `effective-context-limit` is an explicit per-call prompt/context limit; no model default is inferred. |
| `effective-context-limit-authority` | `effective-context-limit-authority` identifies the verified limit authority. |
| `effective-context-limit-version` | `effective-context-limit-version` identifies the authority/model-limit revision. |

Stable numeric metrics use unit `tokens`: `input-tokens`, `output-tokens`,
`cache-read-tokens`, and `effective-context-limit`. Stable metadata-token
comparable keys are `model-id`, `input-token-semantics-version`,
`output-token-semantics-version`, `cache-read-token-semantics-version`,
`tool-schema-generation`, `effective-context-limit-authority`, and
`effective-context-limit-version`.

Only `llm_call` signals with status `success` are eligible. Error, cancelled,
and unknown calls are excluded. Missing required per-call metrics or grouping
keys exclude that call unless a rule below fixes a stricter first-turn
boundary; missing required pack capabilities suppress the whole rule through
the Issue #80 engine. `unbound`/`partial` snapshots suppress with
`incomplete-snapshot`; any snapshot containing `historical_summary_only`
suppresses with `historical-input`. These suppressions do not alter the frozen
Issue #61 completeness reason set.

Groups never combine different model IDs or semantics versions. Growth rules
also split on tool-schema generation. Limit rules additionally split on the
exact limit value, authority, and version. Thus a model, schema, semantics, or
limit revision cannot manufacture growth across its boundary.

Rule arithmetic uses exact .NET `decimal` values. No intermediate rounding is
performed. Medians sort exact values; an even median is the arithmetic mean of
the two middle values. Receipt decimal serialization remains the Issue #80
canonical invariant `G29` representation.

Every match records exact evidence for the calls actually evaluated. Observed
values are numeric and include `eligible-turn-count`, `included-turn-count`,
`excluded-turn-count`, and the rule formula values needed to reproduce the
decision. Counts use unit `turns`, ratios use unit `ratio`, and utilization uses
unit `fraction`. Raw prompts, responses, system/tool bodies, model-generated
text, paths, and comparable-key values never enter a receipt.

## Rule Registry

### `high-initial-context-utilization` version `1`

Required capabilities are `llm-call-classification`, `input-token-count`,
`model-identity`, `token-semantics-version`, `effective-context-limit`,
`effective-context-limit-authority`, and
`effective-context-limit-version`. Scope is trace and the evaluation window is
`first-eligible-turn-per-model-limit-group`.

For each group, choose its first successful call with an explicit positive
limit and complete grouping keys. If that exact first call has no observed
input metric, the group is unevaluable; a later input must not replace the
initial turn. Compute `initial-context-utilization = input / limit`.
Warning is inclusive at `0.50`; critical is inclusive at `0.80` and takes
precedence. Evidence is that exact first turn.

### `monotonic-context-growth` version `1`

Required capabilities are `llm-call-classification`, `input-token-count`,
`model-identity`, `token-semantics-version`, and `tool-schema-generation`.
Scope is trace and the evaluation window is
`maximal-contiguous-nondecreasing-run-per-group`.

Within each group ordered by signal sequence, observed time, then signal ID,
find maximal contiguous runs whose input count is nondecreasing. A decrease
ends a run. Evaluate runs of at least three turns and with first input greater
than zero. Compute `context-growth-ratio = last input / first input`. Warning
is inclusive at `1.75`; critical is inclusive at `2.50` and takes precedence.
One match is emitted for each qualifying maximal run, with evidence for every
turn in that run.

### `context-growth-with-output-collapse` version `1`

Required capabilities are `llm-call-classification`, `input-token-count`,
`output-token-count`, `model-identity`, `token-semantics-version`, and
`tool-schema-generation`. Scope is trace and the evaluation window is
`eligible-group-halves`.

An eligible call has observed input greater than zero and observed output
greater than or equal to zero. Missing values and non-success calls are
excluded; an observed zero output remains eligible. At least four remaining
calls are required. Let `h = floor(n / 2)`: the first `h` and last `h` calls
form the halves; for odd `n`, the one middle call is omitted.

For each call compute exact `output / input`. Compute:

- `context-half-growth-ratio = median(last-half input) / median(first-half input)`;
- `output-input-collapse-ratio = median(last-half output/input) / median(first-half output/input)`.

A zero first-half median output/input makes the collapse ratio undefined and
produces no match. Warning requires growth at least `1.50` and collapse at most
`0.50`. Critical requires growth at least `2.00` and collapse at most `0.30`
and takes precedence. Evidence includes both halves and omits the odd middle.

### `low-cache-read-ratio` version `1`

Required capabilities are `llm-call-classification`, `input-token-count`,
`cache-read-token-count`, `model-identity`, and `token-semantics-version`.
Scope is trace and the evaluation window is
`post-first-eligible-turn-per-cache-semantics-group`.

An eligible call has observed non-negative input and cache-read counts. At
least three eligible calls are required before excluding the first eligible
call. The included post-first calls must total at least `10000` input tokens.
Compute `cache-read-ratio = sum(cache read) / sum(input)` over included calls.
Observed zero cache read is real. Warning is strictly below `0.20`; critical is
strictly below `0.05` and takes precedence. Evidence contains only included
post-first calls; observed counts record the single first-turn exclusion plus
all other excluded calls.

### `near-context-limit-turn` version `1`

Required capabilities and grouping are the same as
`high-initial-context-utilization`. Scope is trace and the evaluation window is
`each-eligible-turn-per-model-limit-group`.

For every eligible exact call compute `context-limit-utilization = input /
limit`. Warning is inclusive at `0.75`; critical is inclusive at `0.90` and
takes precedence. Each qualifying call produces its own receipt with only that
turn as evidence.

## Suppression And Handoff

Each descriptor declares `minimum-sample-unmet`, `incomplete-snapshot`, and
`historical-input` in addition to the frozen engine suppression codes. Rules
return at most one deterministic suppression code when the eligibility/window
minimum is unmet: historical input first, then incomplete snapshot, then
minimum sample. A sufficient sample that does not cross a threshold returns no
match and no rule suppression. Missing required capabilities remain the engine-owned
`missing_required_capability` suppression.

The registry handoff to Issue #84 is the five exact rule ID/version pairs above
and the numeric-only observed values. Issue #84 must display immutable Issue
#80 receipts and must not recompute formulas. Limit-authority/model mapping and
real-source enablement remain adapter work outside Issue #82.
