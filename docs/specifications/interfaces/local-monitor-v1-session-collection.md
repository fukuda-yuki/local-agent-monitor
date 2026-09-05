# Local Monitor v1 Session Collection Success Contract

Status: **Accepted current authority**

Schema name: `local-monitor-sessions.response.v1`

Implementation owner: **#134**

This specification is the sole authority for the successful response of
`POST /api/local-monitor/v1/sessions`. It composes the request, cursor, success
transport, error and registration rules in
[`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md)
with #133's semantic Session facts. It supersedes #133's former unimplemented
`GET /api/local-monitor/v1/sessions` and incomplete response prose. There is no
GET alias, compatibility reader, saved-search handle or fallback.

The executable Draft 2020-12 schema is
[`session-collection.response.schema.json`](../contracts/local-monitor-v1/session-collection.response.schema.json).
The schema owns closed shapes, types, required members, enums and scalar/array
bounds. This prose and the byte goldens own serialization order and the
cross-field semantic rules that JSON Schema cannot express.

## Envelope and pagination

A successful response is compact strict UTF-8 JSON without a BOM, indentation,
trailing whitespace or newline. Object members are never omitted. The envelope
property order is exactly:

```text
schema_version, workspace_revision, items, next_cursor
```

- `schema_version` is exactly `local-monitor-sessions.response.v1`.
- `workspace_revision` is exactly 64 lowercase hexadecimal characters and
  identifies one coherent collection snapshot.
- `items` contains 0..200 entries.
- `next_cursor` is `null` unless a `limit+1` lookahead row exists. When it
  exists, it is the section 9 cursor from the route transport contract for the
  last emitted item. Empty and final pages use `null`.
- Request `limit` is never returned.
- The complete success response ceiling is exactly 8,388,608 UTF-8 entity
  bytes. #134 fully buffers and measures the complete entity before publishing
  status, headers, or body bytes. If it exceeds the ceiling, the server returns
  the route transport contract's exact `409 workspace_too_large` error and
  publishes no partial success body.

The exact empty response bytes are:

```json
{"schema_version":"local-monitor-sessions.response.v1","workspace_revision":"0000000000000000000000000000000000000000000000000000000000000000","items":[],"next_cursor":null}
```

## Session item

Every item uses this exact property order:

```text
session_id, assignment, archive, label, status, completeness, source, model,
summary, tokens, timing, capture_notes, workspace_revision
```

`session_id` is the canonical local Session UUIDv7. `status` is
`active|completed|failed|unknown`; `completeness` is
`unbound|partial|rich|full`.

### Assignment and archive

`assignment` order is `state, authority, revision, repository_id,
candidate_repository_ids`. State is
`assigned|unassigned|explicitly_unassigned|conflict`; authority is
`automatic|manual|none`; revision is an integer at least zero;
`repository_id` is null or a UUIDv7; candidates contain 0..128 distinct UUIDv7
values sorted by ordinal comparison.

`archive` order is `state, revision, effectively_eligible, exclusion_reason`.
State is `active|archived`; revision is an integer at least zero; exclusion is
null or `session_archived|repository_archived`. `effectively_eligible` is true
if and only if exclusion is null.

### Facts, summaries and labels

Every fact state is exactly one of:

```text
recorded|not_observed|source_unsupported|capture_gap|certification_pending|
not_captured|expired|redacted|malformed|oversized|inconsistent|projection_invalid
```

`label` order is `state, text`. Text is non-null only for `recorded`, and is the
first nonempty instruction line after collapsing newlines to spaces, limited to
160 Unicode scalars.

`source` and `model` each use `state, values`. Values are distinct and ordinal-
sorted; source allows at most 5 values and model at most 16. An empty values
array never has state `recorded`.

`summary` order is `skill, tool, subagent, error, retry`. Each fact uses
`state, count`. Count is null unless recorded; a recorded count may be zero.

### Tokens

`tokens` order is:

```text
authority, state, available_execution_count, total_execution_count, input,
output, total, reasoning, cache_read, cache_creation, new_input,
cache_read_ratio_basis_points
```

Authority is `session_run|llm_span|mixed|none`. Counts are nonnegative
integers. Every component uses `state, value`; value is null or a nonnegative
integer, except cache-read ratio is limited to 0..10000.

`new_input` and `cache_read_ratio_basis_points` are recorded only when input and
cache-read are recorded and `0 <= cache_read <= input`; the ratio additionally
requires input greater than zero. Inconsistent inputs produce state
`inconsistent` and null derived values. Producer `total` is never reconstructed.
Session aggregate contributors are exact-linked LLM calls, including calls
whose usage is missing, and token-bearing Session Runs without an exact LLM
span. Non-LLM Runs with no usage do not enter the coverage denominator.
For each contributor and each component independently, a recorded Session Run
value is preferred; otherwise the first recorded exact-linked LLM span value
in source-identity order is used. Run and span values for the same call are
never added together. Authority is `mixed` when selected components come from
both sources. Each component is summed only when every applicable call has
that component; an unavailable full aggregate remains `capture_gap` with a null
value, independently of the recorded subtotal below.
Token aggregates publish `observed_components` for input, output, producer
total, reasoning, cache-read, cache-creation, and cache-read ratio. Each entry
contains `subtotal` (a state/value fact), `observed_call_count`,
`applicable_call_count`, and nullable `paired_input`. Recorded subtotals remain
visible with their coverage even when another call or component is missing.
Explicit zero is recorded; an absent component has no subtotal. The ratio uses
only calls containing both input and cache-read, reports their input denominator
in `paired_input`, and never uses the input total from a different call set.
The observed ratio is rounded to the nearest basis point, with midpoint rounding up.
Any pair with cache-read greater than input makes that ratio inconsistent;
zero input has no ratio. Arithmetic overflow has no subtotal. Parent Agent
rollups are not added to their child LLM calls.
`gen_ai.usage.cache_write.input_tokens` denotes input tokens written to cache
and maps to the existing cache-creation component. Retained normalized spans
are repaired through the ordinary retained-raw projection publication boundary.
A component absent from every call remains `not_observed`, including producer
total. The two execution counts describe applicable calls with any recorded
usage and all applicable calls respectively. Execution and node facts retain
their own authority and remain usable when a Session aggregate is unavailable.

### Timing, capture and revision

An optional `observed_activity` object (`started_at`, `ended_at`, `duration_ms`)
reports the envelope of valid exact-linked recorded Span intervals. It is absent
when no such interval is recorded. This observation range is independently
labelled and never supplies missing native Session lifecycle facts.

Explorer's primary token column shows input and output independently. Recorded
subtotals include component coverage. A leading recognised `current_datetime`
envelope is omitted only from the identifying instruction label; raw content
and evidence remain byte-preserved.

`started_at`, `ended_at`, and `duration_ms` remain native Session lifecycle facts.
`last_seen_at` is the latest persisted observation timestamp, not a Session
completion or duration. For ordinary OTel-only Sessions it comes from accepted
OTel observations and can be shown as **最終観測** when native start is absent.
Headings use native start first, otherwise this explicitly labeled observation.
Date filtering and ordering use the persisted accepted epoch, overridden by
last observation when native start is absent. The stored projection is unchanged. An active stored status without observed native timing is displayed
as **状態未観測**, never as proof of a running or completed Session.

`timing` order is `state, started_at, ended_at, last_seen_at, duration_ms`. Timestamps are
null or canonical UTC `yyyy-MM-ddTHH:mm:ss.fffffff+00:00`. Duration is null or a
nonnegative integer, and is recorded only when both endpoints are valid and
ordered.

`capture_notes` contains 0..16 distinct ordinal-sorted tokens from:

```text
raw_content_not_captured|raw_content_expired|source_unsupported|capture_gap|
certification_pending|projection_invalid|token_inconsistent|cache_inconsistent
```

The item `workspace_revision` is 64 lowercase hexadecimal characters over the
domain-separated canonical Session row, assignment/archive revisions and
states, consumed Run/Event/Skill/projection facts, and retention content states.
It is not the opaque collection revision.

## Golden bytes

The three test goldens are normative byte examples and are consumed verbatim by
#134:

- `empty.json`: empty terminal page;
- `final-page.json`: final page covering archived and unassigned facts,
  missing versus recorded zero, and token/cache inconsistency;
- `more-page.json`: non-final page covering assignment conflict and a non-null
  147-character cursor. Its fixed semantic request is the canonical all-scope,
  active-only request with no dynamic filters and explicit `limit=1`; the
  fixture's second matching candidate is the required `limit+1` lookahead.

They contain synthetic UUIDv7 values and no real user data.
