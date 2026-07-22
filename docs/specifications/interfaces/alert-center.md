# Alert Center read and UI contract

Status: Issue #84 implementation contract
Owner: Issue #84
Schema: `alert.center.v1`

## Purpose and authority

The Alert Center is a sanitized local read surface over accepted alert-domain
contracts. It does not evaluate rules or own alert state.

- Alert identity, severity, evidence, observed values, effective thresholds,
  source metadata, and completeness come only from a canonical
  `alert.receipt.v1` receipt accepted by `AlertReceiptConsumerV1`.
- Rule titles, descriptions, evaluation windows, thresholds, and required
  capabilities come only from the frozen #81 and #82 registered rule
  descriptors with the exact receipt rule ID and version.
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
