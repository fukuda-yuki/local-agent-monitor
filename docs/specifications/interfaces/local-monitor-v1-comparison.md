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

Each cohort has 1..199 canonical Session IDs and the total requested
occurrences is at most 200. Duplicate/overlap are logical Preview exclusions,
not parser repair. Preview is coherent and non-persisting. Requested metadata
preserves cohort/request ordinal; resolved results canonicalize `a`, then `b`,
then Session-ID ordinal bytes. The exclusion enum is exactly
`session_not_found`, `repository_mismatch`, `duplicate`, `cohort_overlap`,
`session_archived`, `repository_archived`, `projection_unavailable`,
`unsupported_selection`, `workspace_too_large`. Logical invalidity is 200 with
`valid:false`; archive exclusions may remain valid only with two nonempty
included cohorts.

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
selection hash, and preview revision. Resolved entries expose archive,
source/model/version/completeness, metric coverage, Session revision, and
projection revision. Read covers immutable identity/receipt/cohorts, the nine
stored sections (`scope`, `tokens`, `input_tokens`, `time_execution`, `skill`,
`tool`, `subagent`, `error_retry`, `conditions`), and stored result rows. Named
row paging covers Skill/Tool/Sub-agent. Evidence covers frozen inclusion state,
consumed value/revision, and optional opaque execution/node references. Missing
is not zero. No raw content, path, locator, prompt/response, Tool payload, Skill
body, or inferred identity is returned.

## Response and errors

Every response is completely buffered before publication, strict UTF-8 JSON,
`Cache-Control: no-store`, exact `Content-Length`, no CORS, and at most
8,388,608 UTF-8 entity bytes. No partial success is published. HEAD has the
GET-equivalent status, headers, and content length with zero body.

Error bytes are exactly `{"error":"<code>"}` with no newline. Codes are only:

```text
invalid_host
invalid_request
invalid_cursor
csrf_rejected
method_not_allowed
comparison_selection_invalid
comparison_preview_stale
workspace_too_large
comparison_not_found
comparison_expired
persistence_busy
```

Precedence is host, method, framing/media/size/path/query, same-origin, CSRF for
POST, cursor, lookup/Repository binding, expiry, selection/staleness, workspace
size, persistence. Known expiry is 410; unknown/mismatched identity is 404.
Errors and logs never echo request values or identifiers. Comparison lifetime
is exactly 24 hours and operational comparison state remains excluded from
backup.
