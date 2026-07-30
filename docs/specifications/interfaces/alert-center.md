# Alert Center read and UI contract

Status: Issue #84 v1 implementation contract plus Issue #95 compatibility
Owner: Issue #84
Schemas: `alert.center.v1`, additive `alert.center.v2`

Local Monitor v1 disposition: the machine contracts remain frozen. The human
page is a raw-default focused compatibility flow reached from Unified Settings
or exact context; it is not permanent navigation and is not registered by a
sanitized-only host.

## Purpose and authority

The Alert Center is a sanitized local read surface over accepted alert-domain
contracts. It does not evaluate rules or own alert state.

- Alert identity, severity, evidence, observed values, effective thresholds,
  source metadata, and completeness come only from canonical owner-store bytes
  accepted by the matching `AlertCenterReceiptConsumerV1` or
  `AlertCenterReceiptConsumerV2`.
- Rule titles, descriptions, evaluation windows, thresholds, and required
  capabilities come only from the frozen #81/#82 or #95 registered rule
  descriptors with the exact receipt rule ID and separate version.
- Current state, revision, history, and allowed mutations come only from the
  #83 `alert.lifecycle.v1` store and HTTP API.
- Repository/workspace labels and evidence availability are exact joins to the
  existing trace projection or exact Session. Absence or disagreement is
  displayed, never guessed.

The surface MUST NOT create another lifecycle, recompute a rule formula in the
browser, infer an alert from telemetry, merge Sessions, emit notifications,
create recommendations, or add an export carrier. The #85 sanitized-export
`alert_center` capability remains `unavailable`.

## Explicit production evaluation

The baseline does not run the #80 evaluator during ingestion. Issue #84 adds an
upper-layer, user-triggered server composition seam over the typed #80
evaluate-and-append contract. It does not reinterpret or rewrite #80 canonical
bytes, schema, or rule results:

```text
POST /api/alert-center/v1/evaluations
```

The strict request is:

```json
{
  "schema_version": "alert.center.evaluation-request.v1",
  "session_id": "019b...",
  "trace_id": "exact-trace-id"
}
```

The request body is `application/json`, closed to exactly those three members,
and limited to 4,096 bytes by both declared and streamed length. Duplicate or
unknown members, non-canonical/non-v7 Session IDs, unsafe trace IDs, trailing
JSON, query parameters, and alternate media types fail before evaluation.

Both IDs are mandatory and exact. The server loads only that UUID Session and
trace projection, verifies that a persisted Session run or event explicitly
owns the trace ID, and rejects not-found or mismatched pairs. It does not pick a
latest Session/trace or match by repository/time. The route is same-origin,
CSRF-protected, no-store, and is the only production trigger owned by this
surface. GET and page rendering remain read-only; there is no ingestion hook,
timer, background evaluation, or browser-side evaluator.

The coordinator builds one `alert.snapshot.v1` from exact sanitized projection
facts, invokes a registry containing the frozen #81 and #82 rule packs with
their defaults, and appends the exact #80 result through `IAlertEngineStore`.
It loads every persisted monitor-span row for the selected trace and the exact
#61 `SourceCompatibilityRow` selected by each distinct span `raw_record_id`.
The trace projection's span count must be positive and exactly equal the
persisted monitor-span row count; zero or partially projected span state
rejects as `alert_center_trace_incomplete` before source lookup or evaluation.
Every such source observation must exist and must have one source surface and
application version that exactly agrees with the selected Session partition.
Missing/versionless rows reject as `alert_center_source_partition_missing`;
mixed or disagreeing rows reject as
`alert_center_source_partition_ambiguous`. Session event provenance does not
stand in for this persisted #61 observation.
The configuration version is `alert-center-default-v1`. Repeating the same
request over the same persisted facts is byte/ID stable and uses the #80
store's exact idempotent append.

Capability construction is conservative:

- the current frozen #61 manifests do not authorize the generic monitor
  projection to promote any #81/#82 required capability. Even a projected span
  status is not authoritative tool-status coverage for an exact source/version
  partition; treating absent/unknown spans as successes would invent coverage;
- all required capabilities therefore remain `unknown` or `unavailable` in the
  current production coordinator. Canonical tool arguments, ownership keys,
  retry classification/attempt/key, permission duration, file identity/range,
  token-semantics authority, tool-schema generation, context-limit authority,
  and tool-status coverage all require a named #61 manifest plus a reviewed
  source/version adapter before promotion;
- partial/mixed/missing data is never converted to zero or a comparable key;
  the frozen evaluator therefore emits exact `missing_required_capability` or
  rule-specific suppressions instead of an alert;
- source surface/version come from one consistent exact Session partition
  attached to the requested trace. Missing version, multiple versions, mixed
  surfaces, or ambiguous ownership reject the request before evaluation with
  no write. `unknown` and `mixed` are not valid evaluation partitions and no
  nearest-source guess is allowed.

The input hash is also bound to the complete selected monitor/source fact set.
Each monitor span, exact Session event, and #61 source observation becomes a
deterministically ordered `status: unknown` signal with no metrics or
comparable keys. This records row presence without promoting semantics. Its
opaque evidence identity is respectively `monitor-span-row-v1:{row_id}`,
`session-event-row-v1:{event_uuid}`, or
`source-observation-row-v1:{row_id}`.

Successful evaluation returns
`alert.center.evaluation-result.v1` with the evaluation ID, ordered receipt IDs,
ordered suppression facts (rule/version/code/missing capabilities), and ordered
rejected matches. It returns no normalized snapshot, raw span, prompt, response,
tool body/argument, local path, lifecycle comment, or secret. Receipt reads then
flow through the same bounded GET DTO below.

At this revision, every successful production POST is suppression-only:
`receipt_ids` is empty and the ordered ten-rule registry produces exactly ten
suppression facts. Rules applicable to the exact source partition use
`missing_required_capability`; non-applicable rules use
`source_not_applicable`. An explicitly versioned exact `raw-otlp` partition is
non-applicable to all ten frozen rules and therefore produces ten
`source_not_applicable` facts. The default receiver records `raw-otlp` with a
null application version, so its normal production request fails the version
partition gate with `alert_center_source_partition_missing` and appends
nothing; the adapter version is not substituted as source version.
Positive receipt/read/UI states are exercised only with canonical synthetic
owner-store fixtures and are automation evidence, not source-live proof. A
future source adapter may produce receipts only after its exact #61 capability
manifest and source/version mapping are reviewed and versioned; the Alert
Center coordinator itself does not promote capabilities.

Evaluation errors use the same fixed no-leak body family with these additions:

| HTTP | Code |
| --- | --- |
| 400 | `alert_center_invalid_request` |
| 403 | `cross_origin_forbidden`, `csrf_required` |
| 404 | `alert_center_session_not_found`, `alert_center_trace_not_found`, `alert_center_trace_not_owned` |
| 409 | `alert_center_source_partition_missing`, `alert_center_source_partition_ambiguous`, `alert_center_trace_incomplete`, `alert_center_store_conflict`, `alert_center_contract_rejected` |
| 413 | `request_too_large` |
| 415 | `unsupported_media_type` |
| 503 | `alert_center_store_busy`, `alert_center_store_unavailable` |

## Read surface

`GET /api/alert-center/v1/alerts` returns one bounded snapshot used by the
Alert Center page and the overview integration. The route is available in
raw-default and `--sanitized-only` modes because every response is sanitized
metadata. It returns `Content-Type: application/json` and
`Cache-Control: no-store`.

Allowed query members are:

| Member | Contract |
| --- | --- |
| `alert_id` | exact 64-character lowercase hexadecimal ID |
| `session_id` | exact opaque receipt Session ID |
| `trace_id` | exact opaque receipt trace ID |
| `severity` | `critical`, `warning`, or `info` |
| `state` | `open`, `acknowledged`, `dismissed`, `resolved`, or `superseded` |
| `rule_id` | exact rule token |
| `source_surface` | exact source token |
| `repository` | exact repository label accepted unchanged by the existing sanitized free-form guard |
| `workspace` | exact workspace label accepted unchanged by the existing sanitized free-form guard |
| `completeness` | `unbound`, `partial`, `rich`, or `full` |
| `period` | `today`, `7d`, or `30d`; default `30d` |
| `from`, `to` | inclusive UTC dates in `yyyy-MM-dd`; both required together and mutually exclusive with `period`; maximum 366 days |
| `offset` | integer `0..1000000`; default `0` |
| `limit` | integer `1..100`; default `50` |

Repository/workspace values are logical labels, not paths, and are not
truncated or rewritten. Any `/` or `\` separator, home-relative `~` prefix,
local/device-relative path form, email-like PII, or Bearer/credential/token-like
string fails the #84 guard, returns the fixed invalid query error, and is never
reflected in `snapshot.query`. Unknown, repeated,
malformed, or conflicting query members return `400` with
`alert_center_invalid_query`. `from` and `to` are converted to an exact
half-open UTC interval and echoed as inclusive dates. Filtering is ordinal and
exact. Unknown scope does not match a repository/workspace filter.

Successful responses have this conceptual shape:

```json
{
  "schema_version": "alert.center.v1",
  "generated_at": "2026-07-23T00:00:00.0000000Z",
  "query": {
    "from": "2026-06-24",
    "to": "2026-07-23",
    "offset": 0,
    "limit": 50
  },
  "snapshot_state": "complete",
  "omitted_receipt_count": 0,
  "coverage_state": "complete",
  "omitted_coverage_fact_count": 0,
  "total_count": 1,
  "alerts": [],
  "recurring_groups": [],
  "coverage": []
}
```

The production reader acquires at most 2,000 canonical receipts through #80's
stable alert-ID cursor before applying the requested time interval and other
filters. It does not inspect owner tables. If the #80 cursor indicates more
receipts, `snapshot_state` is `incomplete` and `omitted_receipt_count` is null
because the bounded contract does not invent an exact unseen count. The
returned alert page can still be inspected, but recurring groups MUST have
`aggregation_state: incomplete_snapshot` and MUST NOT be presented as a
supported recurring result. An invalid or non-canonical stored receipt fails
the complete request closed with `503 alert_center_store_unavailable`; it is
never silently omitted.

Coverage acquisition is independently bounded to at most 20 owner evaluation
pages, 2,000 evaluation projections, and 100 suppression facts. Reaching the
fact bound is conservatively incomplete; reaching a page/evaluation bound is
incomplete when the owner cursor has more data. Either case sets
`coverage_state` to `incomplete` and `omitted_coverage_fact_count` to null; a
bounded empty list is therefore never presented as proof that no suppressions
exist. A fully exhausted owner cursor uses `complete` and zero. These coverage
fields do not change receipt `snapshot_state`.

## Alert item

Each alert item contains these bounded sanitized fields:

- `alert_id`, `severity`, `initial_state`, `first_observed_at`,
  `last_observed_at`, and `summary` copied from the accepted receipt;
- `lifecycle` copied from #83 with `state`, `revision`,
  `last_occurred_at`, the allowed local-user actions for that exact state, and
  at most 100 transition projections in #83 revision-descending order. Each
  transition contains only revision, action, previous/state, occurred time,
  actor, reason code, optional old/new alert IDs, and result code. Lifecycle
  comments and idempotency keys are never copied into the Alert Center DTO;
- `rule` with receipt `rule_id`/`rule_version`, registry contract state,
  title, description, evaluation window, scope, required capabilities, and
  descriptor thresholds;
- `formula` as the frozen registry description and evaluation-window label;
  this is presentation metadata, not an executable expression;
- exact `observed_values` and `effective_thresholds` copied from the receipt;
- `source_surface`, `source_version`, and
  `capability_state: supported_at_evaluation`; receipt creation proves only
  that the receipt's required capabilities were available for that evaluation,
  not that a current adapter is still supported;
- `session_id`, optional `trace_id`, exact repository/workspace scope and its
  provenance/state;
- exact evidence references, availability/content state, and navigation URL;
- receipt completeness and reason codes;
- exact predecessor/successor alert IDs present in #83 lifecycle history.

If an exact rule ID/version is not registered, `rule.contract_state` is
`unknown_version` and presentation metadata is null. Receipt facts remain
visible and no nearest-version fallback is allowed.

Every persisted trace/Session repository and workspace value passes the same
#84 label guard as query input before projection. If any present scope value is
path-, PII-, credential-, or token-like, the whole scope projection is
`state: unknown` with every repository/workspace member null; the unsafe value
is never normalized, echoed, or used for filtering/recurrence.

Lifecycle actions are state-specific:

| State | Actions |
| --- | --- |
| `open` | `acknowledge`, `dismiss`, `resolve` |
| `acknowledged` | `dismiss`, `resolve` |
| `dismissed` | `reopen` |
| `resolved` | `reopen` |
| `superseded` | none |

The UI sends actions only to
`POST /api/alerts/v1/{alert_id}/lifecycle/actions` with the displayed expected
revision, a cryptographically random `aid1_` idempotency key, the #83 CSRF
header, a fixed sanitized reason code, and an optional bounded sanitized
comment. A `409 alert_revision_conflict` is a stale view: the UI announces the
conflict and reloads the read snapshot. The UI never updates lifecycle state
without a successful #83 response and subsequent read refresh.

## Exact scope and evidence navigation

Repository/workspace is resolved in this order without fuzzy matching:

1. exact trace projection identified by the receipt trace ID;
2. exact UUID Session identified by the receipt Session ID;
3. compare the non-empty labels when both authorities exist.

The scope state is `exact_trace`, `exact_session`, `exact_agreement`,
`unknown`, or `conflicting`. Conflicting values are displayed separately and
are ineligible for recurring aggregation. A non-UUID receipt Session ID is not
looked up as a UUID and remains unknown unless the trace projection supplies
scope.

Evidence navigation is based only on exact receipt references:

- a Session reference resolves only from a canonical UUID `session_id` and an
  exact persisted Session. It may omit `trace_id`; when a trace is present it
  must be owned by the Session, and receipt source-partition checking applies
  only to that exact trace. Session raw-retention state supplies the bounded
  content/expired state while the diagnostics link remains metadata-only;
- a generic trace reference with any accepted opaque evidence ID resolves only
  when its exact trace projection exists, its canonical UUID Session exists and
  owns it, and the receipt source partition agrees. The stricter
  `source-observation-row-v1:{row_id}` path remains supported and additionally
  verifies its exact persisted source row;
- a Session-event reference uses its canonical UUID `event_id` as the persisted
  identity even when `evidence_id` is an independent accepted opaque value. The
  exact Session, optional trace ownership, receipt source partition, event time,
  and child-ID tuple must all agree. The stricter
  `session-event-row-v1:{event_uuid}` evidence ID remains accepted only when its
  UUID is canonical and equals `event_id`;
- a span reference resolves through its persisted monitor-row identity; the
  resolved row and its `raw_record_id` source observation must also match the
  receipt Session, trace, span, source partition, and timestamp tuple before it
  is `available`. A `(trace_id, span_id)` match alone is not sufficient;
- an exactly resolved available span links to
  `/traces/{trace_id}?span={span_id}`;
- an available trace links to `/traces/{trace_id}`;
- an exact UUID Session or Session event links to
  `/diagnostics?session_id={session_id}`;
- turn and tool-call references without an accepted exact local resolver use
  `availability_state: unknown` and have no invented link;
- supported exact lookups that find no record use `missing`;
- an identity or tuple mismatch fails closed as `unknown` with no link;
- denied/expired Session content is `expired` while its sanitized reference can
  remain navigable.

The Alert Center page accepts exact `alert`, `session_id`, and `trace_id` URL
parameters. Trace detail and exact Session diagnostics link back with those
filters. This is navigation only; it does not change Session identity.

## Recurring aggregation

Recurring aggregation is deterministic and uses receipt facts plus exact scope.
The grouping key is the ordinal tuple:

```text
rule_id
rule_version
repository (exact value or null)
workspace (exact value or null)
source_surface
source_version
UTC observation date (last_observed_at)
requested from date
requested to date
```

A group is `supported` only when:

- every included alert has non-conflicting exact scope with at least one of
  repository/workspace present;
- the read snapshot is complete;
- at least two distinct receipt Session IDs are present.

The threshold is fixed at two distinct Sessions. One Session with multiple
receipts is `low_n`, not recurring. Unknown/conflicting scope is
`unsupported_scope`. Group output includes exact occurrence count, distinct
Session count, first/last observation, explicit date range, source/version,
completeness distribution, ordered alert IDs, ordered Session IDs, and exact
evidence references. Sessions are never merged by repository or time.

Recurring groups are a handoff-ready observation for #48/#59 consumers. They
do not create an instruction finding, candidate, recommendation, or automatic
action.

## Coverage and suppression facts

An engine suppression is not an alert. `coverage` contains exact frozen #80
suppression facts: evaluation ID, rule ID/version, suppression code, and exact
missing capability tokens. When the same evaluation has one or more accepted
receipts with consistent source/session facts, those facts are attached with
`context_state: exact_evaluation`; `source_surface`, `source_version`,
`session_id`, `trace_id`, and the UTC `observation_date` (`YYYY-MM-DD`) are
separate members. Otherwise those source/session/date members are null and
`context_state: unknown`. No repository, source, date, or Session is inferred
for a suppression-only evaluation.

Coverage facts are a separate bounded list acquired in stable #80 evaluation-ID
and suppression-ordinal order under the 20-page / 2,000-evaluation / 100-fact
limits above; the contract does not infer recency because a suppression has no
timestamp. Coverage is not altered by alert filters. The UI labels this
explicitly and announces incomplete acquisition without turning a capped empty
list into a no-suppression claim. It distinguishes
missing capability, source-not-applicable, rule-disabled, minimum-sample,
incomplete, and other frozen suppression codes without calling them alerts.

## UI behavior and accessibility

`GET /alerts` is the one Alert Center UI surface. It provides:

- severity/state/rule/source/repository/workspace/date/completeness filters;
- 100-item previous/next pagination that preserves the active filters, exposes
  the visible range and total count, and resets to the first page when a filter
  changes;
- rule/source choices derived from the union of current-page alert facts,
  filter-independent bounded coverage facts, and the active URL value. An
  active value remains selected when it yields zero rows or exists only beyond
  the current 100-item page;
- an alert table with severity/state, rule title/version, Session/trace,
  observed values versus effective thresholds, source/version/completeness,
  first/last observation, evidence count, and coverage note;
- a detail region with formula metadata, capability state, exact values,
  evidence links/availability, lifecycle history relationships, and allowed
  actions;
- recurring and suppression/coverage sections with their explicit support
  state;
- distinct loading, empty, API error, stale-revision, weak/incomplete,
  missing/expired/unknown evidence, mixed completeness, sanitized-only, and
  unsupported source/capability states.

A custom period requires both UTC dates, `from <= to`, and an inclusive span of
at most 366 days before the browser changes the URL or starts a read. Validation
is announced and the UI does not silently issue a default 30-day request while
the control says custom. Server and client use the same inclusive range rule.
Every filter, page, post-mutation refresh, and initial load has a monotonic
generation; a superseded response or failure cannot overwrite the newer URL,
selection, lifecycle, pagination, or status.

An `incomplete` snapshot is never rendered as a definitive zero-result or
latest-result claim. Its empty states say only that no match exists in the
acquired range, the overview labels a returned item as bounded rather than
latest, and recurring results remain `incomplete_snapshot` even if a malformed
consumer fixture supplies a stronger aggregation label.

Rows are keyboard-selectable with Enter/Space, selection and expanded detail
are announced, focus moves to the detail heading after an explicit row
selection, form controls have labels, status updates use an atomic live region,
and severity/state are never communicated by color alone. Captured values are
inserted as text; no live markup rendering is permitted.

For the active overview period, the overview page uses only bounded Alert Center
snapshot DTO reads to show the open alert count, critical/warning count
breakdown, source breakdown, top supported recurring rule, and latest critical
alert, with a link to its exact Alert Center selection. A source breakdown over
only the returned 100-item page is labeled as a visible-range breakdown. An
`incomplete` acquisition never claims an exact global count, zero, top, or
latest value. Period-toggle and SSE refreshes have a monotonic generation, so a
response for an older period cannot replace the current card title, values, or
links. This does not change `/api/monitor/overview`. Exact trace and Session
views link to filtered Alert Center URLs without changing their existing DTOs.

## Errors and security

Errors use the strict body
`{"schema_version":"alert.center.v1","error":"<code>"}` with no raw
exception text. Fixed mappings are:

| HTTP | Code |
| --- | --- |
| 400 | `invalid_host`, `alert_center_invalid_query` |
| 403 | `cross_origin_forbidden` |
| 503 | `alert_center_store_busy`, `alert_center_store_unavailable` |

Reads reject cross-site browser requests, require a loopback Host header via
the existing monitor middleware, and are always no-store. DTOs and logs MUST
NOT contain raw prompt/response/tool bodies, lifecycle comments, credentials,
PII, or machine paths. Repository/workspace labels are display metadata from
existing sanitized projections and MUST NOT be used to open local paths.

## Validation-matrix transition

Issue #84 activates only the local explicit-evaluation/read/UI surface. The #91 future placeholder
owned by #84 is removed by the integration owner; it is never relabeled
`active`. This branch contributes
`docs/sprints/issue-84-alert-center/validation-matrix.json` with these frozen
row IDs:

- `91-A-084`: automated DTO, filtering, aggregation, navigation, lifecycle,
  empty/error/stale, and raw-negative tests;
- `91-S-084`: sanitized-only, same-origin/no-store, accessibility, inert-text,
  and repository-safe artifact checks;
- `91-L-084`: repository-safe live UI evidence, classified honestly when a
  source adapter or content-capture authorization is unavailable.

No #85 export row or carrier is activated by this transition.

## Additive cost-receipt read compatibility

Issue #84 preserves every `/api/alert-center/v1/*`, `alert.center.v1` DTO,
filter, recurring rule, and v1 UI behavior. Issue #95 adds no client or public
Alert Center evaluation route; the trusted cost application invokes the Issue
#80 v2 evaluator only after an explicit recalculation.

`GET /api/alert-center/v2/alerts` is an additive no-store, same-origin,
loopback-only read using `alert.center.v2`. It acquires sealed owner-validated
v1/v2 receipt projections from the single version-aware #80 query store and
joins lifecycle through the unchanged #83 API/store. It does not parse
canonical bytes, query engine tables directly, re-evaluate rules, or infer
scope.

The exact v2 response is:

```text
schema_version = alert.center.v2
snapshot_id
acquisition_state = complete | incomplete
acquisition_cap_reason = null | owner_more | receipt_limit | retained_bytes_limit
acquired_receipt_count
match_count_state = exact | acquired_only
matched_item_count
query
items
visible_start_ordinal
visible_end_ordinal
has_previous
previous_cursor
next_cursor
recurring_state = complete | incomplete_snapshot
recurring_groups
coverage_state = complete | incomplete | unavailable
coverage
omitted_coverage_fact_count
```

`acquired_receipt_count` is the validated owner count before filtering.
`matched_item_count` is exact inside that acquired set. It is a global exact
count only when `match_count_state=exact`; incomplete acquisition uses
`acquired_only` and MUST NOT be presented as a global total, top, latest, or
zero claim.

Visible ordinals are one-based positions inside the complete filtered acquired
set and are both zero for an empty page. `has_previous` is true exactly when
the request begins after a validated member. `previous_cursor` is null on the
first and second pages; on the second page `has_previous=true` means the
previous request omits `cursor`. On later pages it is the exact cursor after
which the preceding page begins. The server deterministically replays page
boundaries from the start using the same limit and 16-MiB complete-response
bound, so this remains exact when a response-size bound shortened any page.
`next_cursor` is null exactly when no filtered acquired member remains after
`visible_end_ordinal`. A direct cursor reload therefore reconstructs the same
visible range and backward/forward navigation without client-only history.

`acquisition_cap_reason` is null if and only if acquisition is complete. When
more owner rows exist and multiple receipt-acquisition bounds coincide at the
same accepted-record boundary, the fixed winning rank is
`retained_bytes_limit`, then `receipt_limit`, then `owner_more`.
`retained_bytes_limit` means accepting the next validated receipt/projection
would exceed 64 MiB, `receipt_limit` means 2,000 validated receipts were
accepted, and `owner_more` means the twentieth owner page was consumed while
its cursor still reports more. Processing order, owner page size, and which
condition a caller happens to test first cannot change the token.

`recurring_groups` reuses the exact existing v1 recurring-group projection and
property order: `aggregation_state`, `rule_id`, `rule_version`, `repository`,
`workspace`, `source_surface`, `source_version`, `observation_date`, `from`,
`to`, `occurrence_count`, `distinct_session_count`, `first_observed_at`,
`last_observed_at`, `completeness_distribution`, `alert_ids`, `session_ids`,
`evidence_references`. It is computed from every matching validated
`receipt_v1` item in the full acquired and filtered snapshot before pagination,
never from the current page. `cost_receipt_v2` items do not participate. When
acquisition is complete, `recurring_state=complete` and each group uses the
unchanged v1 `supported | low_n | unsupported_scope` rule. When acquisition is
incomplete, `recurring_state=incomplete_snapshot`, `recurring_groups=[]`, and
the response makes no recurring or zero-recurring claim. The response never
emits an apparently supported group from a bounded prefix.

`items` is a closed discriminated union with properties in exact order
`receipt_kind`, `receipt_v1`, `cost_receipt_v2`. `receipt_kind` is exactly
`receipt_v1 | cost_receipt_v2`. For `receipt_v1`, `receipt_v1` is the exact
existing sealed v1 Alert Center item and `cost_receipt_v2` is null. For
`cost_receipt_v2`, `receipt_v1` is null and `cost_receipt_v2` is the exact
payload below. Both-null, both-present, a discriminator/payload mismatch, or an
unknown kind fails the whole page with `alert_center_store_unavailable`; no
member is skipped. This wrapper does not change one byte or field of the v1
route/DTO.

The `cost_receipt_v2` payload has properties in this exact order:

```text
alert_id
evaluation_id
rule_id
rule_version
severity
initial_state
lifecycle
first_observed_at
last_observed_at
summary
rule
formula
source_surface
source_version
completeness
completeness_reasons
source_cost_configuration_id
source_configuration_head_revision
source_configuration_catalog_sha256
configuration_version
configuration_hash
input_hash
scope
eligibility_digest
evidence
currency
aggregate_state
observed_amount
warning_threshold
critical_threshold
eligible_count
estimated_count
partial_count
not_estimable_count
missing_count
failed_count
unavailable_count
stale_count
coverage_numerator
coverage_denominator
coverage_basis_points
members
```

`rule` has exact property order `rule_id`, `rule_version`, `contract_state`,
`title`, `description`, `evaluation_window`, `scope_kind`. The first two equal
the receipt. Exact registry identity produces `contract_state=registered` and
the remaining metadata copied from `AlertRuleDescriptorV2`; `formula` is the
descriptor's exact non-null registered formula. If the exact ID/version is not
registered, `contract_state=unknown_version`, all four nullable presentation
members after it and top-level `formula` are null, while receipt facts remain
visible. There is no nearest-version fallback or client-authored label.

The alert/evaluation/rule/configuration/source identities, severity, fixed
initial state, lifecycle projection, times, summary, completeness, exact scope,
eligibility digest, monetary values, thresholds, counts, coverage, and input
hash are copied from the sealed #80 receipt and #83 lifecycle projection. The
configuration version is exact `cost.configuration.v1`. A receipt is a
successful monetary match, so `currency` is exact `USD`, `aggregate_state` is
`available`, observed amount and both thresholds are non-null canonical decimal
values, all counts and coverage values are non-null, and first/last observation
times are non-null. Arrays are present even when empty. `scope` is the exact
`AlertCostScopeV2` wire shape/order; its bounds are both null for Session and
both non-null for UTC-day/rolling-period. `lifecycle` is the same sealed
property order/nullability used by the existing v1 item; v2 adds no lifecycle
field or state. Every other top-level property above is non-null.

Each `evidence` item has exact property order `kind`, `evidence_id`,
`session_id`, `observed_at_utc`, `state`, `href`. The first four fields and
canonical array order equal the accepted receipt. `state` is
`available | missing | expired`. For `kind=session`, `href` is always the
non-null fixed same-origin Session href. For `kind=pricing_estimate`, it is the
fixed same-origin estimate href only when state is `available` and is null
otherwise.

Each `members` item preserves receipt member order and has exact property order:

```text
session_id
session_effective_at_utc
state
attempt_revision
attempt_result_kind
attempt_result_code
head_revision
estimate_id
catalog_sha256
registry_version
billing_mode
session_evidence_state
repository
workspace
scope_state
session_href
estimate_evidence_state
estimate_href
```

The receipt supplies fields through `billing_mode`; their nullable-state
relationships are the strict #80 member contract. The presentation resolver
supplies the remaining fields. `session_evidence_state` is always
`available | missing | expired`; repository/workspace are nullable accepted
labels, `scope_state` is `available | unavailable`, and `session_href` is the
non-null fixed same-origin Session href. `estimate_evidence_state` is null if
and only if `estimate_id` is null; otherwise it is
`available | missing | expired`. `estimate_href` is non-null only when the
estimate evidence state is `available`. Every nullable field is emitted as JSON
null, never omitted or replaced with empty text/zero.

The item contains no canonical pricing/receipt bytes, local override document,
source body, arbitrary provider text, credential, private contract/account/
invoice value, PII, path, or lifecycle comment. Source references are not part
of the alert receipt.

`coverage` is also a closed discriminated union with properties in exact order
`coverage_kind`, `suppression_v1`, `cost_suppression_v2`. `coverage_kind` is
exactly `suppression_v1 | cost_suppression_v2`, and exactly the matching payload
is non-null; the other payload is emitted as JSON null. Both-null,
both-present, discriminator/payload mismatch, or unknown kind fails the whole
page with `alert_center_store_unavailable`.

The additive `suppression_v1` payload has exact property order
`evaluation_id`, `suppression_ordinal`, `rule_id`, `rule_version`, `code`,
`missing_capabilities`, `context_state`, `source_surface`, `source_version`,
`session_id`, `trace_id`, `observation_date`. It is the existing sealed v1
coverage fact plus its owner suppression ordinal; existing v1 nullability and
context rules remain unchanged.

The `cost_suppression_v2` payload has properties in this exact order:

```text
evaluation_id
suppression_ordinal
rule_id
rule_version
code
source_cost_configuration_id
source_configuration_head_revision
source_configuration_catalog_sha256
configuration_version
configuration_hash
scope_kind
scope_id
scope_start_utc
scope_end_utc
eligibility_digest
currency
aggregate_state
eligible_count
estimated_count
partial_count
not_estimable_count
missing_count
failed_count
unavailable_count
stale_count
coverage_basis_points
first_observed_at
last_observed_at
```

Its values come only from the paired sealed v2 evaluation/suppression
projections. Configuration version is exact `cost.configuration.v1`; currency
is null or exact `USD`; bounds are both null for Session and both non-null for
UTC-day/rolling-period. Counts, coverage, and times are nullable exactly as
specified by the #80 snapshot/suppression contract, and every nullable property
is present as JSON null. Except for those conditional fields, every payload
property is non-null. The fact contains no member identity or canonical bytes.

Coverage acquisition uses at most 20 owner pages, 2,000 evaluations, 100
returned facts, and 8 MiB per owner page. Any cap sets `coverage_state` to
`incomplete`, retains only the validated acquired facts, and sets
`omitted_coverage_fact_count` to null because unseen facts are not counted. A
fully exhausted owner cursor sets `coverage_state=complete` and
`omitted_coverage_fact_count=0`. Owner busy/unavailable sets
`coverage_state=unavailable`, returns `coverage=[]`, and sets the omitted count
to null; it never returns partial facts. Thus a budget evaluation suppressed
for incomplete/empty/no-covered/coverage conditions remains visible even
though it has no receipt item.

#95 implements bounded `ICostAlertPresentationResolverV1` for #84. For each
exact member it returns the same Session ID/effective time, Session evidence
state `available | missing | expired`, nullable accepted repository/workspace
labels, and the fixed same-origin
`/costs?session_id=<canonical-encoded-session-id>` href. For each estimate
reference it returns `available | missing | expired` and the fixed same-origin
`/costs?session_id=<canonical-encoded-session-id>&estimate_id=<exact-encoded-estimate-id>`
href
only after strict catalog/estimate reload and exact Session/time ownership. It
does not require current-head membership because a historical receipt may cite
a predecessor. #84 receives only this sealed projection and does not query
pricing/Session tables directly. Missing, unsafe, conflicting labels become
null with explicit unavailable scope state and are never echoed, normalized,
or used as a path.

Its internal sealed call is
`Resolve(IReadOnlyList<AlertCostMemberV2> members,
IReadOnlyList<AlertEvidenceReferenceV2> evidence)`. Input must be in canonical
v2 order, contain at most 2,000 members and 4,000 evidence references, and be
consistent with the exact member/evidence cross-field rules; otherwise the
result is `unavailable`. `CostAlertPresentationResolutionV1` has exact property
order `state`, `members`, where state is
`success | busy | unavailable`. A successful member projection has exact
property order `session_id`, `session_effective_at_utc`,
`session_evidence_state`, `repository`, `workspace`, `scope_state`,
`session_href`, `estimate_id`, `estimate_evidence_state`, `estimate_href`.
Nullable fields are emitted explicitly; evidence states are
`available | missing | expired`, and scope state is
`available | unavailable`. Non-success returns an empty member array. The
sealed result is bounded to 8 MiB UTF-8 and cannot include a database/path/error
string. One over-bound result is `unavailable`, never a truncated projection.
On success, `members.Count` equals the input member count and output member
index `i` names the exact input member at index `i`; omission, duplication,
reordering, or an extra member makes the resolver result `unavailable`. The
Alert Center also requires one presentation state for every input evidence
reference before constructing the evidence array: the output member's Session
state maps to its one Session reference, and its nullable estimate state maps
to its one pricing-estimate reference. Resolver `busy` aborts the whole read as
`503 alert_center_store_busy`; resolver `unavailable`, including any
cardinality/order/evidence mismatch, aborts it as
`503 alert_center_store_unavailable`. Neither state produces a partial item,
snapshot, or cursor.

The existing v1 recurring aggregation processes receipt-v1 items only. A
receipt-v2 cost window already represents an exact aggregate and is never fed
through the "two distinct receipt Sessions" recurring rule. The v2 route
applies that unchanged v1 aggregation to the full acquired/filtered receipt-v1
set before pagination, as defined above.

Version-aware filters are closed to:

- v1 semantic filters `alert_id`, `session_id`, `trace_id`, `severity`, `state`,
  `rule_id`, `source_surface`, `repository`, `workspace`, `completeness`,
  `period`, `from`, and `to`;
- `receipt_kind=all | receipt_v1 | cost_receipt_v2`, default `all`;
- `scope_kind=all | session | utc_day | rolling_period`, default `all`;
- `currency=all | USD`, default `all`;
- `coverage_state=all | full | partial`, default `all`;
- `cursor=<opaque-v2-cursor>`, absent on page one; and
- `limit=1..100`, default 50.

Before decoding, the v2 route forms the cursor-excluded encoded filter query by
removing the sole `cursor` component from the raw query string (without the
leading `?`) and joining the remaining raw percent-encoded components in their
received order with `&`. Its UTF-8 length is at most 7,000 bytes. The cursor
component is canonical literal ASCII `cursor=<opaque-v2-cursor>`; percent
escapes in its value are invalid. Appending `&cursor=` and the maximum
1,024-character cursor to a 7,000-byte filter query therefore produces at most
8,032 query bytes, below the unchanged 8,192-byte total bound. This reserved
cursor budget applies on page one as well as continuation requests; a server
must not accept a filter query whose generated next cursor cannot be
resubmitted.

`offset` remains v1-only and is invalid on v2. Explicit `all` and omission
canonicalize identically. Every member occurs at most once. Session filter
retains the existing v1 opaque-ID guard and exact comparison; it is not reparsed
as UUIDv7. It matches v1's exact receipt Session or any exact v2 member accepted
local Session ID, never a representative Session. This preserves historical
canonical Guid versions and opaque v1 IDs.

Canonical resolved query property order is `alert_id`, `session_id`, `trace_id`,
`severity`, `state`, `rule_id`, `source_surface`, `repository`, `workspace`,
`completeness`, `from`, `to`, `receipt_kind`, `scope_kind`, `currency`,
`coverage_state`, `limit`; resolved dates replace `period`, all nullable values
are explicit, and cursor is excluded. `filter_digest` is lowercase SHA-256 of a
length-framed `alert-center-filter/v2` domain and those exact bytes.

| Filter | receipt v1 | cost receipt v2 |
| --- | --- | --- |
| alert/severity/lifecycle state/rule/source/completeness | exact item fact | exact item fact |
| Session | exact opaque receipt Session | any exact member |
| trace | exact optional receipt trace | never matches |
| repository/workspace | exact accepted v1 scope | exact resolved member scope; when Session or both labels are supplied, every member-level predicate must match the same Session |
| scope/currency/cost coverage | non-`all` never matches | exact cost fact |
| date | receipt `last_observed_at` | Session effective time, or aggregate-window overlap |

Missing, unsafe, conflicting, or unresolved member scope never matches.
Repository from one member and workspace from another cannot form a match.
`full` means 10,000 basis points; `partial` means 1..9,999 on a receipt that
passed its configured minimum. Suppressions are coverage facts, not receipt
items. Inclusive `yyyy-MM-dd` input resolves to
`[from 00:00:00Z,to+1 day 00:00:00Z)`. A cost Session scope matches iff its sole
effective time is inside; day/period matches iff
`scope_start < query_end && scope_end > query_start`.

After filtering, both kinds use the accepted UI order: severity rank
`critical < warning < info`, canonical `last_observed_at` descending, then
alert ID ordinal ascending. The cursor is
`alert-center-cursor-v2.` plus unpadded base64url canonical UTF-8 JSON with
property order `schema_version`, `snapshot_id`, `filter_digest`, `limit`,
`severity_rank`, `last_observed_at`, `alert_id`; it is 1..1,024 ASCII
characters, uses `schema_version=alert.center.cursor.v2`, and the UI treats it
as opaque. The next page begins strictly after
that tuple. Cursor validation precedence is fixed: malformed content or a
filter/limit mismatch is `400 alert_center_invalid_query`; then the server
recomputes the snapshot and a snapshot-ID mismatch is
`409 alert_center_snapshot_changed`; only when that ID matches must
`severity_rank`, `last_observed_at`, and `alert_id` equal exactly one member of
the recomputed filtered snapshot and the cursor must equal a page-end boundary
emitted by deterministic replay from page one under the same limit and complete
response-byte bound. A canonical member tuple that is not such a boundary, or a
canonical nonmember tuple, is `400 alert_center_invalid_query` and cannot skip
rows or create an undefined backward page. Filter/lifecycle mutation resets the
UI to page one.

`snapshot_id` is `alert-center-snapshot-` plus lowercase SHA-256 of a
length-framed `alert-center-snapshot/v2` domain containing the resolved query
excluding cursor, acquisition state/cap boundary, and—in owner alert-ID
order—each acquired alert ID, receipt kind, canonical-receipt SHA, complete
sanitized item-projection SHA, lifecycle state/revision/history/relationships,
resolved member scope, and evidence availability. It also binds coverage state
and ordered coverage-projection digests. Every returned DTO fact is therefore
page-stable.

Acquisition uses at most 20 owner pages, 2,000 validated receipts, and 64 MiB
combined retained canonical-receipt plus sanitized-projection bytes. One
transient owner page remains bounded to 100 records/8,388,608 canonical bytes
and is discarded after processing. A reached cap while more owner rows exist
yields incomplete with the cap reason selected by the fixed rank above; unseen
rows are never counted. A complete UTF-8 response is at most 16 MiB. The page
may stop before an item that would exceed that response bound, return fewer than
limit, and provide the last emitted cursor. One item that cannot fit alone is
`503 alert_center_response_too_large`; no empty-page cursor loop is emitted.

Empty, duplicate, unknown, malformed, non-USD, conflicting, or over-8,192-byte
query input returns `400 alert_center_invalid_query`.

The v2 error body is exactly
`{"schema_version":"alert.center.error.v2","error":"<fixed_code>"}`.
Status mapping preserves v1 and adds: invalid Host/query/cursor is 400,
cross-origin is 403, changed snapshot is
`409 alert_center_snapshot_changed`, and receipt-owner/lifecycle/presentation-
resolver busy, unavailable, or one oversize item is 503
(`alert_center_response_too_large` for the latter).
Coverage-owner busy/unavailable instead uses the successful
`coverage_state=unavailable` shape defined above.
Responses and errors are no-store and never include an identifier/value that
failed validation.

The `/alerts` page MUST use the v2 route to render both kinds. Cost receipts show
exact contextual links to each Session and `/costs`; lifecycle actions still
call `/api/alerts/v1/{alert_id}/lifecycle/actions`. Keyboard, focus, stale
generation, inert text, acquisition-bound, and error semantics remain the
accepted Alert Center rules. Recurring presentation reads only the response's
full-snapshot `recurring_state` and `recurring_groups`; it never aggregates the
visible page. Tests pin the unchanged v1 route bytes, mixed v1/v2
pagination/filtering, full-snapshot recurring results across multiple pages,
incomplete acquisition withholding recurring groups, exact multi-Session
navigation, exact registered/unknown rule metadata and formula, direct cursor
reload plus backward/forward navigation (including response-bound-shortened
pages), member-but-nonboundary cursor rejection, lifecycle mutation, v2 tamper
fail-closed behavior, and absence of a second evaluator/store/UI.
They also pin an exact 7,000-byte cursor-excluded percent-encoded filter query
with a maximum 1,024-character generated cursor: the continuation URI stays
within 8,192 bytes and its resubmission returns the strict next page without an
empty-page loop. A 7,001-byte cursor-excluded query is rejected with
`400 alert_center_invalid_query`.
