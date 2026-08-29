# Local Monitor v1 Repository Session Compare

This is the sole Issue #166 owner transport. It composes #165's deterministic
formula/snapshot contract with existing Local Monitor identity, archive,
Workspace, security, and human-route authorities. It adds no alias, version
negotiation, generic query API, chart, AI field, verdict, score, ranking,
recommendation, or narrative.

## Operations

Exactly these five operations exist in raw-default composition:

```text
POST /api/local-monitor/v1/repositories/{repositoryId}/comparisons/preview
POST /api/local-monitor/v1/repositories/{repositoryId}/comparisons
GET  /api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}
GET  /api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}/rows
GET  /api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}/evidence
```

All IDs are canonical lowercase UUIDv7. POST operations accept only POST;
reads accept GET and HEAD. Repository mismatch is indistinguishable from
absence. `/historical-analysis` remains unchanged.

## POST requests

POST requires strict UTF-8 canonical closed JSON, exact
`application/json; charset=utf-8`, no BOM/trailing data, at most 16,384 UTF-8
entity bytes, same origin, and the existing CSRF header. Property order is
`schema_version`, `cohorts`, `include_archived`, then Create-only
`selection_sha256`, `preview_revision`; cohort order is `a`, `b`.

Each cohort has 1..199 canonical Session IDs. The total requested occurrences
`a + b <= 200` is parser-owned because JSON Schema cannot express this
cross-array sum; later parser tests MUST enforce it. Duplicate/overlap are logical Preview exclusions,
not parser repair. Preview is coherent and non-persisting. Requested metadata
preserves cohort/request ordinal; resolved results canonicalize `a`, then `b`,
then Session-ID ordinal bytes. The exclusion enum is exactly
`session_not_found`, `repository_mismatch`, `duplicate`, `cohort_overlap`,
`session_archived`, `repository_archived`, `projection_unavailable`,
`unsupported_selection`, `workspace_too_large`. Logical invalidity is 200 with
`valid:false`; archive exclusions may remain valid only with two nonempty
included cohorts.

`include_archived:false` excludes both a directly archived Session and an exact
assigned Session whose Repository is archived. `include_archived:true` restores
either candidate when it is otherwise valid. D081 remains the frozen authority:
simultaneous causes retain `session_archived` as the primary reason while both
archive states and revisions remain visible.

⚠️ Task 2 obligation:
`LocalMonitorV1ComparisonPreviewRequestParserTests.RejectsAggregateOccurrenceCountAbove200`
MUST reject the schema-valid 101+100 request. Task 1 deliberately carries this
forward; independent per-cohort schema validity is not aggregate parser proof.

Create repeats the coherent read from the exact selection and requires
byte-equal `selection_sha256` and `preview_revision`; browser facts are never
trusted. Changed authority/projection is `409 comparison_preview_stale`.
Success is 201 with `Location` equal to the canonical human
`/repositories/{repositoryId}/comparisons/{comparisonId}` route.

## Read queries

Rows query order is `family`, `q`, `after`, `limit`. `family` is required and
`skill|tool|subagent`. `q` is absent or, after the existing Form-KC/trim/
collapsed-whitespace/invariant-lowercase normalization, 1..200 Unicode scalars
and at most 800 strict UTF-8 bytes. `after` is absent or an opaque process-keyed
cursor. `limit` is 1..100, default 50. Matching is ordinal substring over the
frozen display name and opaque row key; paging exposes the complete union with
no top-N ranking.

Evidence query order is `result_ordinal`, `field_key`, `after`, `limit`.
`result_ordinal` is positive. `field_key` is absent or `value`,
`available_count`, `median`, `minimum`, `maximum`, `total`,
`absolute_difference`, `relative_difference_percent`, `condition`, `count`,
`duration_ms`, `input_tokens`, `output_tokens`, `total_tokens`, `cache_read`,
`cache_creation`, `new_input`, `error_count`, or `retry_count`. `after` is
absent or opaque. `limit` is 1..200, default 100. Cursors bind Repository,
comparison, query fields, and last stored ordinal; mismatch/tamper is
`invalid_cursor`.

## Schemas and success shapes

The seven closed Draft 2020-12 schemas are:

```text
local-monitor-comparison-preview.request.v1
local-monitor-comparison-preview.response.v1
local-monitor-comparison-create.request.v1
local-monitor-comparison-create.response.v1
local-monitor-comparison-read.response.v1
local-monitor-comparison-rows.response.v1
local-monitor-comparison-evidence.response.v1
```

Normative examples and serializers use schema property order and explicit
nullability. Preview covers requested/included/excluded metadata, cohort counts,
selection hash, and preview revision. Resolved included and excluded entries
expose every provable archive, source/model/version/completeness, metric
coverage, Session revision, and projection revision fact. Metadata is null only
when an exact in-Repository candidate cannot be proved. Its archive fields are
`archive_state`, `session_archive_revision`,
`assigned_repository_archive_state`, `assigned_repository_archive_revision`,
and `archive_exclusion_reason`. `source_application_versions` and
`adapter_versions` are separate canonical distinct ordinal arrays;
normalization versions enter neither. Recorded empty sets are `[]`, while
unavailable authority is null. Every metadata fact enters `preview_revision`,
with null and empty collections distinctly framed. Read covers immutable
identity/receipt/cohorts, the nine
stored sections (`target`, `tokens`, `input_token_breakdown`,
`time_and_execution`, `skills`, `tools`, `subagents`, `errors_and_retries`,
`conditions`), and stored result rows. Their exact labels are `対象`,
`トークン`, `入力トークンの内訳`, `時間・実行量`, `スキル`, `ツール`,
`サブエージェント`, `エラー・再試行`, `比較条件`. Each result and named row
publishes the existing stored ordered facts losslessly as the closed `values`
collection. Each item is exactly `{ "key": <bounded-token>, "value":
<bounded-string> }`: key is 1..128 lowercase token characters and value is
1..16,384 characters in schema and, independently, strict UTF-8 1..16,384
bytes enforced by the owner parser/serializer. JSON Schema `maxLength` counts
characters and is not byte-bound proof. Existing `not_available` and `*_unavailable_states`
strings pass through unchanged; no state field or mapping is invented. Scalar facts include `session_count`, `available_count`, `median`,
`minimum`, `maximum`, `total`, `absolute_difference`, and
`relative_difference` keys with their existing stored prefixes. The client
never recomputes them and this transport introduces no formula vocabulary.

Each version dimension is independently bounded to 63 distinct ordinal tokens;
tokens match `^[A-Za-z0-9][A-Za-z0-9._+-]{0,255}$` and are therefore 1..256
strict UTF-8 bytes from the Session ingest version-token alphabet. The
coherent SQLite read observes at most 64 values per dimension solely to detect
overflow. A 64th exact value is `409 workspace_too_large`, never truncation.
Invalid legacy tokens fail that Session's projection closed. A proven empty
dimension is an explicit empty set, not unsupported authority.

Condition evidence for either version dimension uses
`set-sha256:<64 lowercase hex>:count:<decimal>`. Its SHA-256 is
domain-separated by the condition key and length-framed over the canonical
ordinal-distinct exact values. Exact values remain frozen in membership facts
and published in the condition distribution; the evidence summary keeps the
existing 200-character evidence bound.

Named row paging covers Skill/Tool/Sub-agent. Evidence covers frozen inclusion state,
consumed value/revision, and optional opaque execution/node references. Missing
is not zero. No raw content, path, locator, prompt/response, Tool payload, Skill
body, or inferred identity is returned.

Evidence `session_location` is the server-authored canonical human Session URL
for the frozen references on that evidence item. It is Session-only when neither
reference exists, otherwise uses canonical query order `execution`, then `node`;
execution-only, node-only, and execution-plus-node are all valid. The client
validates this relative location and renders it verbatim. It never reconstructs
a location from the separately returned opaque `execution_id` or `node_id`.

## Response and errors

Every response is completely buffered before publication, strict UTF-8 JSON,
`Cache-Control: no-store`, exact `Content-Length`, no CORS, and at most
8,388,608 UTF-8 entity bytes. No partial success is published. HEAD has the
GET-equivalent status, headers, and content length with zero body.

Error bytes and statuses are exactly:

```text
400 `invalid_host` `{"error":"invalid_host"}`
400 `invalid_request` `{"error":"invalid_request"}`
400 `invalid_cursor` `{"error":"invalid_cursor"}`
403 `csrf_rejected` `{"error":"csrf_rejected"}`
405 `method_not_allowed` `{"error":"method_not_allowed"}`
409 `comparison_selection_invalid` `{"error":"comparison_selection_invalid"}`
409 `comparison_preview_stale` `{"error":"comparison_preview_stale"}`
409 `workspace_too_large` `{"error":"workspace_too_large"}`
404 `comparison_not_found` `{"error":"comparison_not_found"}`
410 `comparison_expired` `{"error":"comparison_expired"}`
503 `persistence_busy` `{"error":"persistence_busy"}`
```

Each byte sequence above has no trailing newline.

Precedence is host, method, framing/media/size/path/query, same-origin, CSRF for
POST, cursor, lookup/Repository binding, expiry, selection/staleness, workspace
size, persistence. Known expiry is 410; unknown/mismatched identity is 404.
Errors and logs never echo request values or identifiers. Comparison lifetime
is exactly 24 hours and operational comparison state remains excluded from
backup.

Exact security/transport rule: POST request entity is strict UTF-8, at most
16,384 bytes, exact `application/json; charset=utf-8`, same-origin, and requires
the existing CSRF header. Exact read rule: GET and HEAD only; HEAD has
GET-equivalent status/headers/content length and zero body. Exact publication
rule: every response is fully buffered strict UTF-8 JSON, at most 8,388,608
bytes, `Cache-Control: no-store`, exact `Content-Length`, and no CORS. Exact
no-echo rule: errors and logs MUST NOT echo any request value, Repository ID,
Session ID, comparison ID, cursor, search value, field key, or locator.
