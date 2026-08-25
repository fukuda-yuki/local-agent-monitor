# Issue #134 Collection Milestone Design

Status: approved for implementation by the user's 2026-08-26 instruction.

## Goal and boundary

Close only the existing Repository and Session collection gaps. This change
does not implement Session summary, timeline, node metadata, node content, UI,
human primary-route activation, `/traces` retirement, Compare, or AI.

The implementation keeps the #171 Session success bytes and property order,
the #136 Session request/cursor transport, the #156/#161 single Repository
scope snapshot, and the #154/#158 Skill authorities. It adds no direct catalog
or archive SQL, no sanitized-only human route, no read-time schema fallback,
and no change to frozen `/api/monitor/*`, `/api/session-workspace/*` v1, or SSE.

## Repository collection contract

`GET /api/local-monitor/v1/repositories` retains the closed query names
`archive_scope`, `after`, and `limit`. `after` changes from a raw Repository ID
to a server-issued opaque cursor. The successful response retains schema name
`local-monitor-repositories.response.v1` and the existing envelope/item fields
and order; a new canonical specification, Draft 2020-12 schema, and exact empty,
final-page, and more-page fixtures freeze those bytes before runtime changes.

Repository cards are ordered by canonical `repository_id` ascending under
ordinal comparison. This stable immutable key is deliberately independent of
`display_name`, so equal names and a rename between page requests cannot move a
row across the page boundary. The cursor contains only a keyed filter binding,
the last emitted Repository ID, and a keyed tag. It contains no display name,
locator, raw filter value, or unkeyed digest.

At raw-default startup, the route creates a random 32-byte
`repository_cursor_key` in memory. A restart invalidates cursors. The semantic
filter frame binds canonical `archive_scope` and effective `limit`. The cursor
is exactly 101 bytes before canonical unpadded base64url encoding and exactly
135 ASCII characters after encoding:

```text
offset  size  field
0       1     version 0x01
1       32    HMAC-SHA256(key, repository filter frame)
33      36    canonical lowercase Repository UUIDv7 ASCII
69      32    HMAC-SHA256(key,
                    ASCII("local-monitor-repository-cursor\0v1\0") + bytes[0..68])
```

Malformed syntax, noncanonical encoding, tampering, wrong filter/limit,
another process key, or a non-UUIDv7 position returns exact
`400 {"error":"invalid_cursor"}`. Query grammar errors unrelated to `after`
remain `invalid_request`.

Each card's `active_session_count` and `last_observed_at` use only exact assigned
Sessions for which the #156/#161 snapshot reports effective eligibility.
`last_observed_at` ignores missing or malformed timestamps, compares valid
instants rather than strings, and emits canonical UTC or null. Conflict Sessions
remain unassigned; `assignment_conflict_count` continues to use exact candidate
membership without creating an assignment.

## Session accepted instant and timing

`local_workspace_projection` owns one accepted ordering instant per Session.
It selects the first valid value in this order:

```text
started_at -> created_at -> last_seen_at -> invalid-time group
```

Validity means a real `DateTimeOffset` value accepted by the existing Session
timestamp contract. Valid values are converted to an `Int64` UTC Unix epoch
millisecond. The projection stores `sort_group` (`0` valid, `1` invalid) and
`sort_epoch_ms` (the selected epoch millisecond, or exactly zero for invalid).
No SQLite text maximum, offset-string ordering, or exception-throwing parse is
used. Negative epoch milliseconds remain valid `Int64` values.

Session order remains `sort_group ASC, sort_epoch_ms DESC, session_id DESC`.
`from` is inclusive and `to` exclusive against this same accepted instant;
invalid-time rows do not match a non-null date bound. The existing #136 cursor
continues to carry that exact tuple.

Timing fields are parsed independently of the ordering fallback. Valid start
and end values are emitted in canonical UTC
`yyyy-MM-ddTHH:mm:ss.fffffff+00:00`. Malformed values do not throw. Duration is
recorded only for ordered valid endpoints; missing, malformed, and inconsistent
facts retain the existing closed state vocabulary and null values.

## Search projection and lifecycle

Projection v3 adds one normalized, bounded, local-workspace-owned search-fact
table. A fact contains Session ID, closed kind (`label`, `skill`, or `tool`),
NFKC plus invariant-lowercase text, exact authority/source identity, and the
source lifetime needed to prove current search eligibility. It never contains
full prompts, Skill bodies, Tool inputs/results/errors, paths, or response text.
The list DTO does not expose the table or the request q value.

`q` uses ordinal substring matching over the OR of:

- the retained first-instruction label fact;
- names returned by the #154 current-valid Skill authority only; and
- exact Tool names from accepted structural Tool observations.

Stale or invalid Skill claims never create facts. Label, Skill, and Tool facts
whose raw source is deleted, expired, no longer retained, or no longer current
are removed or rejected by the snapshot read and cannot match. Session ingest,
Session Event-content retention deletion, Skill-current projection change,
schema migration/backfill, startup rerun, and restore rerun all recalculate the
affected facts in the same owning transaction or before the new database is
published.

The scope snapshot contributor bulk-reads all search facts with one fixed
set-based query. Collection filtering remains bounded to at most 10,000
Sessions and never performs per-row detail or raw queries. The 10,000-Session,
combined search/filter, 200-row paging fixture asserts stable pages, a fixed
statement count, and an index-backed query plan.

## Projection v3 lifecycle

`local_workspace_projection` moves explicitly from v2 to v3. The migration is
one transaction and performs an exact v2 shape check, rebuilds the Session
projection where changed constraints require it, creates the search-fact table
and indexes, backfills all current retained facts, updates the component stamp
last, refreshes, and validates. Any failure rolls back schema, rows, and stamp.

Current v3 startup validates exact owned objects and reruns projection
deterministically. Supported v1 state continues through its existing v1-to-v2
step and then the exact v2-to-v3 step; there is no v2/v3 dual reader. Partial,
unknown, or future shapes fail closed.

Runtime backup declares v3 as current, preserves every v3 table, validates
search-source lifetime and canonical timestamp/epoch consistency, accepts exact
v2 only as a staging migration input, reruns projection before publish, and
proves backup/restore equality. Retention cleanup and current-valid Skill
changes remove obsolete facts. Migration rollback, rerun idempotence, backup,
restore, and retention behavior are exercised by tests.

## Verification

Implementation follows strict TDD. Focused tests cover every timestamp fallback,
malformed/all-missing rows, mixed offsets, date filtering across cursors, each
search fact and prohibited search body, stale/invalid/expired facts, Repository
cursor tamper/filter mismatch/equal-name/rename cases, exact schema/golden bytes
and headers, 10,000 Sessions, fixed statement counts/query plans, v2-to-v3
migration/rollback/rerun, backup/restore/retention, raw-default/sanitized-only,
and frozen endpoints. The final HEAD receives focused tests, build, and one
complete solution test run with exit code zero.
