# Pricing Estimation Interface

Issue #94 owns a source-neutral, deterministic pricing registry and estimation
domain. It supports GitHub Copilot and Claude Code routes for which the caller
can supply an explicit billing mode and exact usage evidence. Codex App remains
not estimable in v1. The domain is a calculation authority, not an invoice,
subscription, purchase, quality, effect, alert, or UI authority.

## Versioned Contracts

| Contract | Version | Purpose |
| --- | --- | --- |
| Registry document | `pricing.registry.v1` | Versioned bundled or local-override pricing entries |
| Catalog snapshot | `pricing.catalog-snapshot.v1` | Canonical ordered registry documents bound to an estimate |
| Estimate request | `pricing.estimate-request.v1` | Exact session/source/billing/usage input |
| Estimate record | `pricing.estimate.v1` | Immutable deterministic result and strict consumer input |
| Canonical JSON | `pricing.canonical-json.v1` | Stable UTF-8 serialization and identity input |
| Display rounding | `pricing.display-rounding.v1` | Round-half-even at the selected currency's minor units, after estimation only |

The JSON Schema 2020-12 registry shape is
[`../contracts/pricing/v1/pricing-registry.schema.json`](../contracts/pricing/v1/pricing-registry.schema.json).
The bundled reviewed registry is
[`../contracts/pricing/v1/pricing-registry.bundled.json`](../contracts/pricing/v1/pricing-registry.bundled.json).
Runtime estimation never fetches either source URL.

## Registry Document

A document contains:

- `$schema`, `schema_version`, `registry_version`, `source_kind`, `source_id`,
  `source_label`, `last_reviewed_date`, `stale_after_date`, and
  `source_references`;
- one or more immutable entries;
- `source_kind` exactly `bundled` or `local_override`;
- a unique non-secret source ID and explicit source label.

Each entry contains:

- `entry_id`, `revision` from 1 through 2,147,483,647, and optional
  `supersedes_entry_key`;
- provider, canonical model ID, zero or more exact aliases, billing mode, and
  exact pricing route;
- nullable input, output, cache-read, 5-minute cache-write, 1-hour
  cache-write, reasoning, request, and credit rates;
- nullable request-to-credit multiplier;
- exact `USD` currency and two minor units (the only currency profile admitted
  by registry v1; another currency requires a later contract version);
- inclusive `effective_from_utc` and exclusive nullable
  `effective_to_utc`, serialized with an exact UTC `Z` suffix;
- public `source_reference`, `last_reviewed_date`;
- explicit `included_zero_incremental_cost`;
- bounded limitations.

Source references are public-style absolute HTTPS URIs of at most 4,096 UTF-16
code units. They must be well-formed UTF-16 and begin with exact lowercase
`https://`. Explicit user information (including empty user information),
query, fragment, raw whitespace/control/line/paragraph separators, raw
backslash, IP literals, terminal-dot or single-label hosts, localhost and
`.localhost`, exact `home.arpa`, and `.local`, `.internal`, `.lan`, `.home`, and
`.home.arpa` suffixes are rejected. Percent escapes must be well formed. One
percent-decoding pass is the v1 inspection boundary; the decoded value cannot
contain control/line/paragraph separators or backslash, and decoded path
segments cannot contain traversal, email, or the repository credential shapes
used for safe labels and provenance. Source-reference validation errors are
fixed and never echo the rejected value.

An entry key is `<source_id>:<entry_id>@<revision>`. An entry is never mutated
in place. A correction or rate change appends a revision or a new document and
names the exact prior key in `supersedes_entry_key`. A local override may append
a new non-overlapping exact tuple without a predecessor. When it overlaps an
existing lookup space/effective period, it must name the exact bundled or
earlier override key it replaces; source kind alone gives no precedence.
Supersession always points backward in catalog order. Within the same
`source_id` and `entry_id`, the replacement revision must be greater than its
target revision; a new entry ID or a distinct local-override source may start
at revision 1.
Active overlaps without one unique supersession chain make the catalog invalid.
Every supersession preserves the exact canonical model and alias set so an
older alias cannot bypass the replacement. Document source IDs are unique
within one catalog. Document/source/entry review dates are required; a source
or entry review date cannot follow its document review date, and an entry
review date equals its selected source-reference review date.

Present rate and multiplier values are decimal values from `0.000001` through
`1000000`, inclusive, with normalized fractional scale no greater than six
(insignificant trailing zeroes do not increase the normalized scale). Token rates are per 1,000,000
tokens. Request rates are per request. Credit rates are per credit. A
request-to-credit multiplier may be combined only with a credit rate and makes
the request count the required quantity. An included-plan entry contains no
rates and is the only route allowed to return an exact zero incremental cost.
Token billing modes contain token rates only; legacy-request mode contains
exactly a request rate or a credit rate with an optional request multiplier.
Claude token routes reject a separate reasoning rate because output is
inclusive. A non-included rate or multiplier can never be zero.

## Supported Provider And Billing Modes

Provider and billing-mode tokens are closed:

| Provider | Billing modes |
| --- | --- |
| `github_copilot` | `github_ai_credits`, `github_legacy_requests`, `plan_included`, `custom_enterprise`, `unknown` |
| `claude_code` | `anthropic_api_tokens`, `cloud_provider_api_tokens`, `subscription`, `custom_enterprise`, `unknown` |
| `codex_app` | `subscription`, `custom_enterprise`, `unknown` |

Estimate requests also admit the closed provider token `unknown`, which always
returns `unsupported_provider_route`; registry entries cannot use it.

The caller, explicit user configuration, or a future capability-authorized
adapter selects the billing mode and pricing route. The estimator never derives
either from model name, source surface, plan marketing name, account state, or
price-table availability. `pricing_route` is an exact provider-defined
applicability boundary rather than a fuzzy location label. Bundled v1 uses
`credit_consuming_interaction` for GitHub and `standard_global` for direct
Anthropic API. Completion/next-edit, US-only inference, Batch, and cloud routes
do not match those entries.

`github_ai_credits`, `github_legacy_requests`, `anthropic_api_tokens`,
`cloud_provider_api_tokens`, and `plan_included` may resolve an exact registry
entry. Unsupported provider/mode combinations fail closed.
`subscription`, `custom_enterprise`, and `unknown` do not resolve prices:

- subscription allocation: `subscription_allocation_unknown`;
- custom or enterprise contract: `custom_contract`;
- unknown mode: `unknown_billing_mode`;
- all Codex App v1 routes:
  `subscription_or_contract_unknown`.

Pricing-route tokens are closed in v1:

- GitHub: `credit_consuming_interaction`, `legacy_request`,
  `code_completion`, `next_edit_suggestion`, `subscription_or_contract`, and
  `unknown`;
- Claude: `standard_global`, `us_only_inference`, `batch`,
  `cloud_provider_configured`, `subscription_or_contract`, and `unknown`;
- Codex App: `subscription_or_contract` and `unknown`.

Recognizing a route token does not make it estimable. Only an exact applicable
registry tuple can produce components. `code_completion` and
`next_edit_suggestion` are intentionally recognizable so they return
`unsupported_provider_route` instead of being mistaken for the
credit-consuming route.

Provider/mode/route admission is exact: GitHub AI credits use
`credit_consuming_interaction`; GitHub legacy requests use `legacy_request`;
GitHub included rules use credit-consuming, completion, or next-edit routes;
Claude direct API tokens use standard-global, US-only, or Batch; Claude cloud
API tokens use `cloud_provider_configured`; subscription/custom modes use
`subscription_or_contract`; unknown modes use `unknown`. Cross-provider or
cross-mode route combinations return `unsupported_provider_route`.

## Estimate Request

The request carries:

- `schema_version`;
- caller-supplied `calculation_time_utc`;
- optional `supersedes_estimate_id`;
- source surface/version and opaque session identifier;
- exact session-effective timestamp;
- provider, exact source model ID, explicit billing mode, exact pricing route;
- source completeness (`unbound`, `partial`, `rich`, or `full`) and its fixed
  source reasons;
- request-level provenance for session time, provider, model, billing mode, and
  pricing route;
- nullable input/output/cache-read/cache-write-5m/cache-write-1h/reasoning
  token quantities, request count, and credit count.

Every present quantity is non-negative and carries:
`source_adapter`, `source_version_or_schema_fingerprint`,
`source_event_or_trace_span_id`, `capture_content_state`, and
`normalization_version`. Absence is different from a present zero. No category
is derived from total tokens or another category. Token and request quantities
are integral. Credit quantities are decimal because provider credit accounting
can be fractional.

All token/credit quantities are at most `1000000000000000000`; request count is
at most `1000000000000`. A positive fractional credit is at least `0.000001`.
Credit quantity normalized fractional scale is at most six. Rate/quantity
bounds do not by themselves guarantee that every product or aggregate fits a
System.Decimal coefficient and scale. Every component multiplication and the
single exact aggregate are checked dynamically; a result that cannot be
represented exactly fails closed and is never rounded or truncated.

Incomplete-source reasons use the exact Issue #61 order and ceilings:

| Reason | Maximum completeness |
| --- | --- |
| `missing_native_session_id` | `unbound` |
| `missing_trace_context` | `rich` |
| `trace_signal_disabled` | `rich` |
| `content_capture_disabled` | `rich` |
| `unsupported_source_version` | `rich` |
| `ingest_gap` | `rich` |
| `hook_only` | `rich` |
| `historical_summary_only` | `partial` |
| `unknown_span_kind` | `rich` |
| `schema_drift_detected` | `partial` |
| `planned_source_not_enabled` | `unbound` |

Reasons are unique, bounded to the 11 fixed codes, and canonicalized to this
order before identity calculation. `full` has no reasons. A non-full status may
have no more-specific reason when that is the source contract's canonical base
state; any present reason cannot exceed its completeness ceiling.

Request admission is repository-safe and bounded before calculation. Provider,
billing-mode, pricing-route, completeness, and reason tokens are closed.
Source/provenance identifiers are bounded safe tokens. Model IDs and registry
labels are bounded text with controls, rooted/traversal-like paths, and
credential-bearing URL-like values rejected. A predecessor, when present, is
exactly `pricing-estimate-` plus 64 lowercase hexadecimal characters.
Every admitted string must be well-formed UTF-16; valid surrogate pairs remain
valid text and an unpaired surrogate fails closed. The engine snapshots every
caller-owned request collection exactly once before validation and uses only
that immutable snapshot for validation, calculation, and identity.

## Exact Selection

Selection is ordinal and case-sensitive:

1. Validate the catalog and provider/mode/pricing-route tuple.
2. Match the exact canonical model ID or one declared exact alias.
3. Select entries whose effective interval contains the session timestamp.
4. Apply only explicit active supersession links.
5. Require exactly one surviving entry.

No trimming, normalization, case folding, fuzzy match, latest-price fallback,
nearest-date fallback, or cross-provider alias is allowed. A known model with
no applicable interval returns `outside_effective_range`; an unmatched exact
model returns `unknown_model`.

## Calculation

For each non-null selected rate, the corresponding quantity is required and a
separate component is emitted. Token components use:

`quantity * rate / 1,000,000`

Request components use:

`request_count * request_rate`

Direct credit components use:

`credit_count * credit_rate`

A request-to-credit component uses:

`request_count * request_credit_multiplier * credit_rate`

All operations use decimal arithmetic. The engine performs no intermediate
rounding. Each multiplication and the complete aggregate must be exactly
representable within the v1 decimal contract or the calculation fails closed.
`amount` is the exact sum of estimated components in the one selected currency.
Display rounding is a declared projection only: round-half-even to the
registry currency minor units after the complete exact amount is known.
Currency conversion and multi-currency summation are prohibited.
The executable display projection accepts zero through six minor units and
does not mutate the canonical estimate.

For direct Anthropic API standard-global entries, `output_tokens` is the
inclusive billed output quantity. A standalone thinking/reasoning count is a
subset and must never be added to output as a second charge. The bundled entry
therefore has a null reasoning rate; reasoning-only evidence cannot support an
estimate when inclusive output is missing.

Every rate-backed category is listed under coverage as required, estimated, or
missing. A missing category emits a null component amount and
`missing_token_category`; it is never replaced by zero. A present exact zero
produces a zero component and counts as estimated.

## Status And Reasons

Status is:

- `estimated`: every required category is present, source completeness is
  `full`, and the registry is current;
- `partial`: at least one component is estimable, but one or more categories
  are missing, source completeness is not `full`, or the registry is stale;
- `not-estimable`: no monetary component can be supported or the
  provider/model/mode/effective-period route is unavailable.

Reason tokens are ordered and deduplicated:

- `unknown_model`;
- `unknown_billing_mode`;
- `subscription_allocation_unknown`;
- `subscription_or_contract_unknown`;
- `custom_contract`;
- `missing_token_category`;
- `unsupported_provider_route`;
- `partial_source`;
- `registry_out_of_date`;
- `outside_effective_range`.

A stale registry does not select a later price or fetch the network. It adds
`registry_out_of_date` and prevents a full `estimated` status.

## Immutable Output And Recalculation

The output contains:

- `schema_version`, lowercase 64-hex `catalog_sha256`, deterministic
  `estimate_id`, optional
  `supersedes_estimate_id`, caller-supplied calculation time;
- `status`, exact nullable amount, currency;
- independent ordered components and coverage;
- ordered reasons;
- registry schema/version/source kind/source ID/entry key/source reference,
  canonical model ID, matched source model ID, billing mode, effective period,
  and last-reviewed provenance;
- source provenance and completeness;
- canonical and display-rounding versions.

The catalog is serialized as canonical `pricing.catalog-snapshot.v1` bytes
containing the bundled document first and local-override documents in caller
order. Entry and collection order is preserved; no document or entry sorting
occurs. `catalog_sha256` is lowercase SHA-256 of those exact canonical snapshot
bytes and is included in estimate identity. `estimate_id` is
`pricing-estimate-` plus lowercase SHA-256 of the canonical identity
projection. The identity also includes the complete request, selected entry
provenance, result components, reasons, calculation time, and predecessor ID.
The output is canonical UTF-8 JSON with fixed property/component/reason
ordering, invariant decimal form, and UTC timestamps. Repeating the exact
calculation produces byte-identical output. Recalculation changes the
calculation time and/or predecessor link, creating a new record that can
coexist with the original; no store or overwrite operation exists in #94.
Catalog construction rejects more than 64 ordered documents or a canonical
snapshot larger than 4 MiB. Estimate production rejects canonical output larger
than 1 MiB. These are producer limits, not consumer-only safeguards.

The dependency-free strict `pricing.estimate.v1` consumer accepts at most
1 MiB and depth 32, rejects duplicate/unknown fields, noncanonical decimal or
timestamp/property forms, invalid closed values and component/coverage/status
relationships, and an `estimate_id` that does not match recomputed canonical
content. It also requires the exact catalog snapshot used by the original
calculation and re-runs the calculation byte-for-byte. This binds every
registry display/provenance value, component category/unit/rate/quantity,
missing reason, and component-to-usage relation to that catalog and request.
The public SHA identity is an integrity key, not proof that client-supplied
bytes are authentic. The consumer returns defensive copies of all nested
collections. This gives #95 a reload boundary without adding persistence to
#94. The companion catalog-snapshot consumer accepts at most 4 MiB, depth 32,
and 64 ordered documents; it rejects duplicate/unknown fields, invalid
registry semantics, and any bytes that are not the exact canonical snapshot
serialization. #95 must persist the exact canonical catalog snapshot bytes
alongside canonical estimate bytes and reload them through this consumer.
Reconstruction is not an allowed persistence substitute. Substituting a
current catalog, sorting documents/entries, or trusting a caller-provided hash
is not allowed.

## Bundled Review Boundary

Bundled v1 contains deliberately narrow reviewed entries:

- GitHub Copilot `GPT-5 mini` AI-credit token rates, conservatively effective
  from the repository review date because the current page does not state an
  earlier effective date;
- GitHub Copilot `GPT-5 mini` explicit plan-included zero-incremental rule;
- Claude Code canonical API model `claude-sonnet-4-6` only when billing mode is
  explicitly `anthropic_api_tokens` and pricing route is exactly
  `standard_global`, using the Anthropic price list dated 2026-05-27 as the
  conservative local effective boundary.

The authoritative reviewed sources are:

- <https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing>
- <https://docs.github.com/en/billing/concepts/product-billing/github-copilot-billing>
- <https://github.com/features/copilot/plans>
- <https://www-cdn.anthropic.com/files/4zrzovbb/website/3684c2faafb97418665782cea0001f439f74b1d2.pdf>
- <https://platform.claude.com/docs/en/about-claude/models/model-ids-and-versions>
- <https://platform.claude.com/docs/en/build-with-claude/extended-thinking>

GitHub rates are USD per 1,000,000 tokens. With the reviewed plan value of one
AI credit = 0.01 USD, the table corresponds to 25 input, 2.5 cached-input, and
200 output credits per 1,000,000 tokens. The estimator records the USD token
components only and does not add a second credit component. GitHub
completion/next-edit routes do not consume AI credits and therefore cannot
select the bundled `credit_consuming_interaction` entry. The included rule
claims only zero incremental cash cost inside an explicitly established
allowance, not zero seat price or zero credit consumption.

No undeclared alias is bundled. Explicit cloud-provider prices require their
own reviewed entry or local override. Current #61 source-capability manifests
do not authorize model/token production for these surfaces, so synthetic tests
exercise positive estimates while production callers must supply separately
authorized exact provenance. Availability of a registry entry alone does not
authorize capture or infer a billing route.

## Compatibility And Exclusions

The legacy `dashboard-dataset` `sprint4-m2-v1` unit-price calculator predates
this contract and can treat a missing token side as zero. It remains unchanged
for wire compatibility and is explicitly non-authoritative for Issue #94.
Issue #95 must decide how to persist/project this new record without silently
mapping it into that legacy field.

Issue #94 adds no SQLite schema component or migration, HTTP/CLI/UI route,
budget rule, alert, notification, provider credential, invoice reconciliation,
purchase/plan change, currency conversion, quality score, or effect verdict.
It does not modify Issue #80 receipts/lifecycle or the Issue #91 future-surface
registry. The active Issue #94 domain is tracked by its owning validation
matrix; the future registry remains reserved for not-available surfaces only.
