# Local Monitor v1 Repository Collection Success and Cursor Contract

Status: **Accepted current authority**

Schema name: `local-monitor-repositories.response.v1`

Implementation owner: **#134**

This specification is the sole success-response and cursor authority for
`GET /api/local-monitor/v1/repositories`. It composes the common request,
transport, error, and registration rules in
[`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md)
with the catalog/scope authority owned by #156 and the direct archive facts
owned by #161. Neither #134 nor #161 adds catalog SQL or another scope reader.

The executable Draft 2020-12 schema is
[`repository-collection.response.schema.json`](../contracts/local-monitor-v1/repository-collection.response.schema.json).
The schema owns closed shapes, types, required members, enums, and scalar/array
bounds. This prose and the byte goldens own serialization order and semantic
rules that JSON Schema cannot express.

## Success envelope

A success response is compact strict UTF-8 JSON without BOM, indentation,
trailing whitespace, or newline. Members are never omitted. Envelope order is:

```text
schema_version, workspace_revision, repositories, all_session_count,
unassigned_active_session_count, archived_repository_count, next_cursor
```

- `schema_version` is exactly `local-monitor-repositories.response.v1`.
- `workspace_revision` is a 64-character lowercase hexadecimal revision for
  the coherent catalog/scope/archive snapshot.
- `repositories` contains 0..200 cards ordered by canonical `repository_id`
  ascending under ordinal comparison. Display-name equality and renames do not
  affect order or page boundaries.
- The three envelope counts are nonnegative and describe the same snapshot,
  independently of the current page.
- `next_cursor` is null unless a `limit+1` lookahead row exists. A non-null
  value is the 135-character cursor below for the last emitted card.
- The complete Repository success response ceiling is exactly 8,388,608 UTF-8 entity bytes.
  A complete entity of exactly 8,388,608 bytes is accepted;
  8,388,609 bytes is the first rejected size. #134 fully buffers and measures the complete entity
  before publishing status, headers, or body bytes. If the
  complete entity exceeds the ceiling, the server returns the Repository GET
  transport contract's exact `409 {"error":"workspace_too_large"}` and
  publishes no partial success body.

The exact empty response bytes are:

```json
{"schema_version":"local-monitor-repositories.response.v1","workspace_revision":"0000000000000000000000000000000000000000000000000000000000000000","repositories":[],"all_session_count":0,"unassigned_active_session_count":0,"archived_repository_count":0,"next_cursor":null}
```

## Repository card

Card property order is exactly:

```text
repository_id, display_name, archive_state, archive_revision,
active_session_count, last_observed_at, assignment_conflict_count,
repository_revision
```

`repository_id` is the canonical local Repository UUIDv7. `display_name` is
the catalog-owned safe display value. `archive_state` is `active|archived`.
`archive_revision` and all counts are nonnegative integers.
`repository_revision` is a 64-character lowercase hexadecimal digest revision.

`active_session_count` and `last_observed_at` use only exact assigned Sessions
that the single #156/#161 scope snapshot reports effectively eligible.
`last_observed_at` ignores missing/malformed timestamps, compares valid
instants, and emits canonical UTC `yyyy-MM-ddTHH:mm:ss.fffffff+00:00` or null.
Conflict Sessions remain unassigned. `assignment_conflict_count` counts exact
candidate membership without creating an assignment.

## Opaque cursor

Raw-default startup creates a random in-memory 32-byte
`repository_cursor_key`; restart invalidates prior cursors. The filter frame is
the exact concatenation:

```text
ASCII("local-monitor-repository-filter\0v1\0archive_scope\0")
ASCII(canonical archive_scope)
00
ASCII("limit\0")
U16BE(effective limit)
```

`archive_scope` is exactly `active_only` or `include_archived`; effective
`limit` is 1..200 after applying the default 50. The frame contains no raw
query spelling. `filter_binding = HMAC-SHA256(repository_cursor_key, frame)`.

Before canonical unpadded base64url encoding, the cursor is exactly 101 bytes:

```text
offset  size  field
0       1     version 0x01
1       32    filter_binding
33      36    canonical lowercase Repository UUIDv7 ASCII position
69      32    HMAC-SHA256(repository_cursor_key,
                    ASCII("local-monitor-repository-cursor\0v1\0") + bytes[0..68])
```

It is exactly 135 ASCII characters after encoding. It contains no display
name, locator, raw filter value, or unkeyed digest. Malformed syntax,
noncanonical encoding, tampering, wrong filter/limit, another process key, or
a non-UUIDv7 position returns exact `400 {"error":"invalid_cursor"}`.
Unrelated query grammar errors remain `invalid_request`.

The `more-page.json` golden uses key bytes `00..1f`,
`archive_scope=include_archived`, effective `limit=1`, and position
`018f0000-0000-7000-8000-000000000101` to freeze the deterministic cursor.

## Golden bytes

`empty.json`, `final-page.json`, and `more-page.json` are normative byte
examples. They use only synthetic identities and contain no locator, path,
owner, search value, or raw Repository ID as a cursor.
