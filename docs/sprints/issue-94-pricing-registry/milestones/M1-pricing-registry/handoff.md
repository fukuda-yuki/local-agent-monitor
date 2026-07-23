# Issue #94 Pricing Handoff

## Delivered Authority

- `CopilotAgentObservability.Pricing` is an independent dependency-free domain.
- `pricing.registry.v1` is the canonical reviewed registry document.
- `pricing.catalog-snapshot.v1` is the canonical full calculation catalog:
  bundled first, local overrides in caller order, with document/entry order
  preserved and no sorting.
- `pricing.estimate-request.v1` is the explicit input contract.
- `pricing.estimate.v1` canonical bytes are the immutable calculation result.
- `PricingCatalogSnapshotConsumer` is the strict bounded reload authority for
  exact catalog bytes, and `PricingEstimateConsumer` is the strict bounded
  reload authority for estimate bytes. #95 must not copy or weaken parsing,
  identity, catalog-digest, or byte-recalculation rules. Public hashes are
  integrity keys, not authenticity.
- `pricing-estimate-<sha256>` identifies the exact input, selected registry
  provenance, full catalog SHA-256, components, reasons, calculation time, and
  predecessor.

The domain contains no SQLite component or migration. It changes no existing
route, UI, alert, notification, Issue #80 receipt/lifecycle, source-capability
manifest, or legacy dashboard wire field.

## #95 Consumer Boundary

A #95 consumer must:

1. obtain exact provider/model/billing-mode/pricing-route and quantities from a
   separately authorized source; registry presence grants no capture or billing
   inference authority;
2. preserve `estimated`, `partial`, and `not-estimable` without displaying a
   missing amount/category as zero;
3. retain exact registry source kind/ID/version/entry key/reference,
   canonical model, billing mode, route, effective period, currency, coverage,
   reasons, and source provenance;
4. append recalculation output with `supersedes_estimate_id`, retaining the
   older record;
5. display only post-calculation half-even currency-minor-unit rounding while
   keeping exact component and total decimals;
6. keep currencies separate and perform no conversion;
7. treat local overrides as separately labeled input and prevent private
   contract data from entering repository-safe output/evidence;
8. specify its own persistence, retention, access, mutation, HTTP/UI, and
   migration behavior before implementation.
9. persist the exact canonical `pricing.catalog-snapshot.v1` bytes alongside
   estimate bytes and reload them through `PricingCatalogSnapshotConsumer`.
   Reconstruction, current/latest catalog substitution, and document/entry
   sorting are not allowed.

Snapshot production and consumption share the 4 MiB / 64-document ceiling; the
consumer also enforces depth 32. Estimate production and consumption share the
1 MiB ceiling; the consumer also enforces depth 32 and compares its
`catalog_sha256` plus byte-for-byte recalculation against the supplied snapshot.
A digest alone does not establish producer identity or authorize private
override content. Public source references are at most 4,096 UTF-16 code units
and follow the canonical lowercase-HTTPS lexical and single-decode safety rules,
including rejection of exact `home.arpa` and its subdomains. All admitted
strings must be well-formed UTF-16, and request collections are snapshotted once
before validation.

Rate, multiplier, and fractional-credit normalized scale is at most six.
Static magnitude bounds do not guarantee representability: every component and
the aggregate is dynamically exact-checked and fails closed without rounding.

Registry v1 accepts only USD with two minor units. Supporting another currency
profile requires a later contract version rather than widening the v1 parser.

The existing static dashboard `sprint4-m2-v1` calculator is incompatible with
the canonical missing-category rule and remains non-authoritative. #95 must not
silently copy a canonical result into `estimated_cost`; it needs an explicit
schema/compatibility decision.

Current #61 manifests do not authorize positive model/token production for the
target surfaces. Synthetic tests prove the calculation domain, while a real
consumer must keep positive production estimates unavailable until an exact
reviewed source/version grants all required fields.

## Reviewed Bundled Boundary

- GitHub `GPT-5 mini`: exact `credit_consuming_interaction`, USD token
  components only, conservative review-date start. Completion and next-edit
  routes cannot select it. The included entry means zero incremental cash cost
  only.
- Anthropic `claude-sonnet-4-6`: exact direct
  `anthropic_api_tokens` + `standard_global`, conservative dated-document
  boundary. US-only inference, Batch, cloud, and subscription routes cannot
  select it. Inclusive output is billed once; a reasoning subset is not an
  extra component.
- Codex App: always `not-estimable: subscription_or_contract_unknown` for
  supported v1 modes.

## Issue #91 Handling

Issue #94 had no entry in
`docs/specifications/contracts/validation-matrix/v1/future-surface-registry.json`,
so this work leaves that file unchanged. That registry contains only
`not_available` surfaces and must not receive an `active` state unsupported by
its schema.

The Issue #91 matrix contract also forbids evidence against a dirty branch or
moving name. #94 therefore records its exact clean integrated candidate
`7e688fecdeecd81013f3c9097719d45e412245f4` in the sprint evidence without
inventing a future-registry activation or an unowned matrix row. If a later
matrix owner decides a standalone domain row is required, the proposed row is:

- row ID: `91-P-094`;
- surface: `pricing-estimation-domain`;
- operation: strict registry admission, exact effective lookup, deterministic
  estimation, and canonical recalculation;
- invariant: unknown/missing/unsupported never becomes zero, an overlap has one
  explicit supersession winner, exact replay is byte-identical, and
  recalculation appends a predecessor-linked record;
- evidence: the exact-candidate Pricing focused suite plus required repository
  validation recorded in `evidence.md`;
- environment: repository-safe synthetic quantities and embedded reviewed
  public price records only; no provider account, invoice, credential,
  runtime fetch, private contract, or genuine user content.

An active UI/alert/live-provider row belongs to #95 or its owning issue, not
this calculation-only domain.
