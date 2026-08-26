# Local Monitor v1 Route and Collection Transport

Status: **Accepted current authority**
Authority: Issue #136, PO136-A2b
Accepted: 2026-08-09

This specification owns the exact human-route grammar, browser URL state,
HTTP method/status/header behavior, the Repository collection GET transport,
and the Session Explorer collection request transport for Local Monitor v1. It
amends the route-facing seams of #133, #162 and #165 without moving their
domain ownership.

The page hierarchy and presentation remain owned by
[Local Monitor v1 IA](local-monitor-v1-ia.md). Raw-local posture remains owned
by [Local Monitor v1 Security](local-monitor-v1-security.md). Workspace semantic
data requirements and projections remain #133/#134-owned. The accepted #171
success wire in
[`local-monitor-v1-session-collection.md`](local-monitor-v1-session-collection.md)
composes with this transport, so #134 mapping may proceed.
The Repository success/cursor authority in
[`local-monitor-v1-repository-collection.md`](local-monitor-v1-repository-collection.md)
also composes with the exact GET transport below.
Optional AI storage remains #162/#163/#164-owned. Comparison formulas and
snapshots remain #165/#166-owned.

## 1. Decision and non-negotiable boundary

Local Monitor v1 uses:

- six lowercase, slashless primary human routes;
- exact owner-issued local identities and no heuristic repair;
- one closed body-bearing Session Explorer collection read;
- transient dynamic search/model values;
- process-keyed opaque pagination cursors;
- fixed malformed, missing, stale and expired outcomes;
- no redirects, aliases, compatibility readers or permissive parsers.

The former unimplemented #133 collection GET is replaced by:

```text
POST /api/local-monitor/v1/sessions
```

There is no `GET` alias, query reader, fallback, dual transport, server-side
search handle, saved search or compatibility path. The POST is an idempotent
read and creates no operation receipt, cookie, history row or server-side
search session.

This decision keeps `q` and dynamic `model` values out of URLs, browser
history/storage/cache, cursors, logs, errors and reusable evidence. The cost is
intentional: those two filters are not bookmarkable and reset on reload or
back/forward navigation.

This specification does not change `/api/monitor/*`,
`/api/session-workspace/*` v1, SSE, Canvas, or any technical-evidence route.

## 2. Ownership

| Concern | Sole owner |
| --- | --- |
| Human path/query parsing, canonical URL generation, history restoration and retired-list dispatch | #136 |
| Session collection request/query/cursor contract | this route-facing amendment to #133; pure parsing implementation is #136-owned |
| Session collection semantic read requirements | #133/#134 |
| Exact closed success response wire | [`local-monitor-v1-session-collection.md`](local-monitor-v1-session-collection.md); #134 alone maps and serializes it |
| Repository collection GET/query transport | this specification; #134 implements the route and parser |
| Repository collection success/cursor wire | [`local-monitor-v1-repository-collection.md`](local-monitor-v1-repository-collection.md); #134 alone maps and serializes it |
| Repository/archive scope facts used by the Workspace read | the one `ILocalRepositoryScopeSnapshotService` composed by #156/#161 |
| Local execution/node identity and exact Session/node index | #133/#134, with the route-facing amendment in this specification |
| AI run identity/scope lookup | #162/#163/#164, with the route-facing amendment in this specification |
| Comparison identity, operational snapshot and expiry tombstone | #165/#166, with the route-facing amendment in this specification |
| Human state wording and sentence-level copy | #137/#169 |

#136 owns only pure typed path/query/request/cursor parsers and URL builders.
Those rules compose with the accepted exact success wire in
[`local-monitor-v1-session-collection.md`](local-monitor-v1-session-collection.md),
which supersedes #133's former GET and incomplete response prose. #134 alone
maps and serializes the POST. No placeholder page, fabricated data, inferred
response DTO, substitute Workspace reader or provisional serializer is
permitted.

## 3. Primary human paths

The active primary paths are exactly:

```text
/
/repositories/{repositoryId}/sessions
/sessions
/sessions/unassigned
/sessions/{sessionId}
/repositories/{repositoryId}/comparisons/{comparisonId}
```

Path literals are ordinal case-sensitive, lowercase and slashless. A primary
route has no alternate root, trailing-slash alias, duplicate-slash alias,
case alias, canonicalization redirect or name-based redirect.

### Raw-path classification

Classification uses the raw path before percent decoding.

The reserved static path `/sessions/unassigned` is classified before the
`/sessions/{sessionId}` variable template. `unassigned` is never parsed as a
Session ID. A case variant such as `/sessions/Unassigned` or
`/sessions/UNASSIGNED` is a reserved-literal near path and returns the empty
no-store 404; it is not a matched malformed Session UUID and never returns 400.

1. A raw path whose slash positions and literal segments exactly match one of
   the templates is a matched primary route. Its variable segments are then
   validated by the identity grammar below.
2. A `%` escape, malformed escape, plain backslash, control, dot segment or
   encoded character in a variable segment of an otherwise matched template
   is a matched malformed path and returns `400 invalid_request`.
3. A case-variant literal, changed literal, missing/extra segment,
   trailing/double slash, or encoded separator that changes the template's raw
   slash/literal layout is not a primary route. It returns the empty no-store
   `404` defined in section 11.

Therefore an uppercase or otherwise noncanonical UUID in a correctly matched
template is `400`; `/Sessions`, `/sessions/` and
`/repositories/{id}//sessions` are empty `404`. Neither class redirects or
falls back to another scope.

## 4. Route identity grammars

### Canonical local UUIDv7

`repositoryId`, `sessionId`, local `execution`, local AI `analysis`, and
`comparisonId` use one canonical local UUIDv7 representation:

- exactly 36 ASCII characters;
- hyphens at zero-based indices 8, 13, 18 and 23;
- lowercase hexadecimal at every other position;
- version nibble `7` at index 14;
- RFC variant nibble `8`, `9`, `a` or `b` at index 19;
- parse as UUID and serialize in lowercase `D` form byte-for-byte equal to the
  supplied value.

Uppercase, braces, parentheses, whitespace, trimming, percent encoding,
base64, a native/source carrier or another UUID version is invalid.

The owner-facing meanings are:

- `repositoryId`: #155/#156 local Repository ID;
- `sessionId`: local Session ID;
- `execution`: #133/#134 local execution/Run ID; any opaque source carrier
  remains internal and cannot appear in a human URL;
- `analysis`: #162 `local_ai_runs.run_id`; it never means `latest`;
- `comparisonId`: #165 immutable operational comparison snapshot ID.

### Timeline node

`node` is exactly `node-` followed by 32 lowercase hexadecimal characters.
It is the first 16 bytes of #133's domain-separated exact-identity SHA-256,
rendered as lowercase hexadecimal. #136 validates and carries the value but
never derives, reconstructs or approximately resolves it.

## 5. Per-page query contract

Property names and values are case-sensitive. Unknown keys, an empty key, an
empty value, a duplicate singleton, a duplicate repeated value, a malformed
percent escape or a value outside its grammar return `400 invalid_request`.
Input key order is nonsemantic. Generated links use the orders below.

Query components use `&` and exactly one `=`. Except for the timestamp `%2B`
spelling below, keys and values use only their shown literal ASCII characters.
Any other percent escape, raw `+`, whitespace, semicolon separator or encoded
unreserved character is noncanonical and returns `400 invalid_request`; percent
decoding never creates an otherwise accepted token, ID, node or cursor.

A raw trailing `?` containing no key/value pair is equivalent to no query.
The `settings` key is valid on every primary page and has the closed values:

```text
state | receiver | ai | repositories | archive | storage | diagnostics
```

### Repository selection and Compare

`/` and `/repositories/{repositoryId}/comparisons/{comparisonId}` accept only:

```text
settings
```

Comparison metric/row drill-down state is not added to the URL in v1. Exact
evidence links navigate to owner-issued Session/node routes.

### Session detail

`/sessions/{sessionId}` accepts, in generated-link order:

```text
execution, node, analysis, settings
```

- `execution` uses the canonical local UUIDv7 grammar.
- `node` uses the exact node grammar.
- `analysis` uses the canonical local UUIDv7 grammar.
- `node` may appear without `execution`; the exact Session/node index resolves
  its execution and ancestors.
- when both appear, the supplied execution must byte-equal the node's exact
  execution. A mismatch is `404 node_not_found`.
- a valid absent execution is `404 execution_not_found`.
- an analysis run must belong to the route Session and have `session` or
  `node` scope. Another Session/scope is indistinguishable from absent and is
  `404 analysis_run_not_found`.
- a node-scoped analysis may resolve its exact anchor node/execution when they
  are omitted. If supplied selection values disagree with that anchor, the
  result is `404 analysis_run_not_found`.
- a transient node analysis removed after its operational retention is
  `404 analysis_run_not_found`. Existing durable Session-report metadata with
  expired content remains the #162 `200` expired-content state.

No failure selects a latest execution/run, a nearby node, a same-name object,
or the Session overview implicitly. Recovery is an explicit user action.

### Session Explorer

The three Explorer pages are:

```text
/repositories/{repositoryId}/sessions
/sessions
/sessions/unassigned
```

Their paths determine `scope=repository|all|unassigned`. They accept, in
generated-link order:

```text
from, to, source*, status*, has_skill, has_subagent,
has_error, has_retry, archive_scope, cursor, mode, settings
```

`*` is a repeated set-valued key. Input order is nonsemantic; generated links
sort repeated values in ordinal byte order.

| Key | Exact human-URL grammar |
| --- | --- |
| `from` / `to` | Singleton exact UTC seven-fraction timestamp. It must parse as a real Gregorian instant and re-serialize byte-for-byte to the accepted decoded form below. `from` is inclusive, `to` exclusive, and `from < to` when both exist. |
| `source` | 1..16 distinct values from the current closed set `copilot-sdk`, `copilot-cli`, `vscode`, `hook-unknown`, `claude-code`. |
| `status` | 1..16 distinct values from the current closed set `active`, `completed`, `failed`, `unknown`. |
| `has_skill`, `has_subagent`, `has_error`, `has_retry` | Singleton exact `true` or `false`; omission means no predicate. |
| `archive_scope` | Omitted, `active_only` or `include_archived`; omission means `active_only`. |
| `cursor` | Singleton exact 147-character token from section 9. |
| `mode` | Omitted or exact `compare`. Draft Session IDs never enter the URL. |
| `settings` | The closed Settings token above. |

`q`, `model`, `scope`, `repository_id`, `limit`, `after`, draft cohort IDs and
any legacy `activity` key are not valid human-URL keys.

Human `from`/`to` values have the exact raw spelling:

```text
yyyy-MM-ddTHH:mm:ss.fffffff%2B00:00
```

`-`, `T`, `:`, and `.` remain literal. The plus sign is uppercase `%2B`.
Raw `+`, space, `Z`, another offset, another fraction width, lowercase `%2b`,
`%3A`, double encoding or any other percent escape is invalid. The decoded
value must parse as a real Gregorian `DateTimeOffset` instant and re-serialize
exactly as `yyyy-MM-ddTHH:mm:ss.fffffff+00:00`; spelling alone does not admit an
impossible date or time. The POST body uses that decoded exact value.

## 6. Browser state

URL-safe Explorer state is the exact set in section 5. It may be restored by
reload and browser back/forward.

Dynamic `q` and `model` values exist only in the current document's form state,
JavaScript memory and the POST request body. They are not written to:

- URL path, query or fragment;
- `history.state`;
- `localStorage`, `sessionStorage`, IndexedDB or Cache API;
- a service worker, cookie or reusable cache;
- autocomplete history or a reusable DOM data attribute;
- application/browser analytics, telemetry or console output;
- errors, diagnostics or reusable evidence.

The controls use `autocomplete="off"`; the request uses Fetch
`cache: "no-store"`. Reload and back/forward restore only URL-safe state and
reset `q` to null, `model` to an empty array and `limit` to null/default 50.
The UI must not claim that a copied URL reproduces those transient values.

A returned cursor may be placed in the human URL only when `q` is null,
`model` is empty and the exact request body has `limit:null`, meaning default
50. The client emits null rather than explicit 50 for that default. When either
dynamic filter is active or `limit` is any non-null value, the cursor and
non-default limit remain in page memory and the POST body only; the client first
removes any URL cursor. Reload/back clears that cursor and resets the limit to
null/default 50. Changing any filter or limit clears the cursor. Neither client
nor server repairs a mismatch or restarts at page one.

## 7. Session Explorer request wire

### Common transport guards

```text
POST /api/local-monitor/v1/sessions
```

- raw-default only; absent in `--sanitized-only`;
- loopback and Host validation use the accepted Local Monitor guard;
- a present `Origin` must equal the effective loopback origin;
- `Sec-Fetch-Site: cross-site` is rejected;
- CORS is disabled and no response has `Access-Control-Allow-*`;
- exact `x-monitor-csrf: local-monitor` is required;
- `Idempotency-Key` is neither required nor consumed;
- request `Content-Encoding` must be absent;
- media type is `application/json`, with no parameter or only
  `charset=utf-8`; media type/name/value comparison is ASCII
  case-insensitive;
- any other media type, parameter, charset or content encoding is
  `415 unsupported_media_type`;
- declared and streamed body maximum is 32,768 bytes; overflow is
  `413 request_too_large` and no prefix is processed;
- bytes are strict UTF-8 without BOM;
- JSON depth is at most 4;
- input is exactly one JSON object followed only by JSON whitespace;
- property names are case-sensitive; unknown, duplicate or missing properties
  are invalid;
- property order is nonsemantic, but the client emits the order below.

### Closed object

Every property is required exactly once, in canonical emitted order:

```text
schema_version
scope
repository_id
archive_scope
from
to
source
model
status
has_skill
has_subagent
has_error
has_retry
q
cursor
limit
```

Canonical no-dynamic-filter example:

```json
{"schema_version":"local-monitor-session-search.request.v1","scope":"all","repository_id":null,"archive_scope":"active_only","from":null,"to":null,"source":[],"model":[],"status":[],"has_skill":null,"has_subagent":null,"has_error":null,"has_retry":null,"q":null,"cursor":null,"limit":null}
```

`null` is valid only where the table permits it. Missing is never equivalent
to null.

| Field | Exact contract |
| --- | --- |
| `schema_version` | Exact `local-monitor-session-search.request.v1`. |
| `scope` | Exact `all`, `unassigned` or `repository`. The Local Monitor browser derives it from the human page path; the endpoint does not trust or require `Referer`. |
| `repository_id` | Canonical local Repository UUIDv7 only for `scope=repository`; otherwise exactly null. The browser copies it only from a validated Repository page route. |
| `archive_scope` | Exact `active_only` or `include_archived`; never null. |
| `from` / `to` | Null or exact decoded UTC `yyyy-MM-ddTHH:mm:ss.fffffff+00:00`. A non-null value must parse as a real Gregorian `DateTimeOffset` instant and re-serialize byte-for-byte to that form; `from < to` when both exist. After that validation, each bound is converted to a signed Unix epoch millisecond as `floor((UTC instant - 1970-01-01T00:00:00Z) / 1ms)`, exactly matching `DateTimeOffset.ToUnixTimeMilliseconds` and the projection/cursor time basis. Filtering compares the Session accepted ordering epoch millisecond—first valid `started_at`, then `created_at`, then `last_seen_at`—with `from` inclusive (`>=`) and `to` exclusive (`<`); invalid-time Sessions do not match a non-null bound. Sub-millisecond fractions add no comparison precision, and pre-epoch values floor toward negative infinity (`1969-12-31T23:59:59.9999999+00:00`, one tick before the epoch, becomes `-1ms`). Two wire-distinct bounds in the same millisecond bucket may therefore form an empty effective interval even though parser-level `from < to` holds. Cursor request binding remains over the exact canonical request semantics and wire values; it does not substitute quantized timestamp text. |
| `source` | Array of 0..16 distinct current source tokens from section 5. |
| `model` | Array of 0..16 distinct dynamic values. Each has 1..128 Unicode scalars, at most 256 strict UTF-8 bytes, and no C0/C1 control or line/paragraph separator. No trim, normalization, case fold, alias or approximate match. |
| `status` | Array of 0..16 distinct current status tokens from section 5. |
| `has_skill`, `has_subagent`, `has_error`, `has_retry` | JSON Boolean or null; null means no predicate. |
| `q` | Null or 1..200 Unicode scalars and at most 800 strict UTF-8 bytes, with no unpaired surrogate. NFKC followed by invariant lowercase must remain nonempty and at most 800 UTF-8 bytes. Original and normalized values are request-memory only. Matching uses ordinal substring comparison over exactly the three current normalized fact classes `label`, `skill`, and `tool`; it never searches prompts, Skill bodies, Tool payloads/results/errors, paths, or response text. |
| `cursor` | Null or the exact token in section 9. |
| `limit` | Null or a canonical JSON integer 1..200. Null means 50. Decimal, exponent, string, Boolean, sign and negative-zero spellings are invalid. |

Array entries cannot be null. Input array order is nonsemantic; the validated
set is ordinal-sorted for query execution and cursor framing. Text-identical
duplicate entries are invalid. An unknown source/status token is
`400 invalid_request`, never a broad/no-filter search.

## 7A. Repository collection GET transport

The exact route is:

```text
GET /api/local-monitor/v1/repositories
```

It is registered in raw-default composition only and is absent in
`--sanitized-only` and receiver-only composition. Loopback/Host validation uses
the accepted Local Monitor guard. A present `Origin` must equal the effective
loopback origin, `Sec-Fetch-Site: cross-site` is rejected, CORS is disabled,
and no response has `Access-Control-Allow-*`. GET and HEAD require no CSRF or
idempotency header and consume no request body.

The query is closed to the generated order `archive_scope, after, limit`.
Input key order is nonsemantic. Unknown or empty keys, duplicate singletons,
or malformed structure outside the value of exactly one `after` component are
`400 invalid_request`. Once the grammar identifies exactly one `after`
component, every value is passed unchanged to cursor validation: empty,
short, padded, percent-bearing, raw `+`, whitespace, semicolon-bearing,
multi-`=` and other malformed or noncanonical values are `400 invalid_cursor`.
A trailing `?` with no component is equivalent to no query. Names and
non-cursor values are ordinal case-sensitive and are never trimmed or repaired.

| Query | Exact contract |
| --- | --- |
| `archive_scope` | Optional singleton. Missing means `active_only`; present is exactly `active_only` or `include_archived`. |
| `after` | Optional singleton. Missing means the first page; present is exactly one canonical unpadded base64url Repository cursor of 135 ASCII characters from [`local-monitor-v1-repository-collection.md`](local-monitor-v1-repository-collection.md). It is never a Repository ID. |
| `limit` | Optional singleton. Missing means effective limit 50; present is a canonical unsigned decimal integer 1..200 with no sign, leading zero, decimal point, exponent, whitespace, or negative-zero spelling. |

Changing `archive_scope` or effective `limit` invalidates `after`; the server
does not repair the cursor or restart at page one. The first applicable failure
wins in this exact order:

1. Host guard;
2. exact route and method dispatch, including the shared-path Repository catalog POST contract;
3. same-origin guard;
4. closed query structure, names, duplicates, and non-cursor parameter values;
5. cursor syntax, canonical encoding, version, UUIDv7 position, tag, process
   key, and exact archive-scope/effective-limit binding;
6. one coherent #156/#161 scope snapshot and bounded #134 serialization.

GET success is status `200`, exact
`Content-Type: application/json; charset=utf-8`, and
`Cache-Control: no-store`. It has no `Location`, `ETag`, `Set-Cookie`, CORS
header, or content negotiation. Its complete entity and property order are
owned solely by
[`local-monitor-v1-repository-collection.md`](local-monitor-v1-repository-collection.md).

HEAD follows the winning GET status, content type, no-store header, and exact
representation `Content-Length`, but emits zero entity bytes. The shared path
also retains the separately owned Repository catalog POST create contract.
PUT, PATCH, DELETE, OPTIONS, and every other method that is neither GET, HEAD,
nor the registered POST return exact `405 {"error":"method_not_allowed"}`
before query validation, with integrated `Allow: GET, HEAD, POST`; no `OPTIONS`
request becomes a CORS preflight response. The collection read surface itself
contributes exactly GET and HEAD to that integrated method set.

Every nonempty response is compact strict UTF-8 JSON without BOM, indentation,
trailing whitespace, or newline. Errors and status are closed to:

| Condition | Status and exact body |
| --- | --- |
| Non-loopback/invalid Host | `400 {"error":"invalid_host"}` |
| Cross-site/origin rejection | `403 {"error":"csrf_rejected"}` |
| Invalid query structure, unknown/duplicate parameter, or invalid non-cursor value | `400 {"error":"invalid_request"}` |
| Malformed, noncanonical, tampered, restarted, or filter-mismatched cursor | `400 {"error":"invalid_cursor"}` |
| Complete success entity exceeds the exact 8,388,608 UTF-8 entity-byte ceiling owned by the Repository success contract | `409 {"error":"workspace_too_large"}` |
| SQLite remains busy after the accepted bounded policy | `503 {"error":"persistence_busy"}` |
| Raw-default composition unavailable | `503 {"error":"local_monitor_ui_unavailable"}` |
| Method other than GET/HEAD or the separately owned Repository catalog POST | `405 {"error":"method_not_allowed"}` |

Every error has `Cache-Control: no-store` and nonempty JSON has the exact JSON
content type above. Error bodies never echo the raw target, query value,
cursor, Repository ID, path, exception, stack, SQL, or inner detail.

## 8. Request validation precedence

For the raw-default host, the first applicable failure wins:

1. loopback/Host guard;
2. exact route and method dispatch;
3. same-origin and CSRF;
4. declared or streamed body limit;
5. media type and content encoding;
6. strict UTF-8, JSON syntax/depth, closed property/type/value rules;
7. cursor syntax, integrity and exact filter binding;
8. exact Repository existence for Repository scope;
9. after the separate response-contract gate closes, one coherent Workspace
   read and exact bounded serialization under that owner.

No failure contains a request property name/value, raw request target, cursor,
Repository ID, q/model text, path, exception, stack, SQL or inner detail.

## 9. Session pagination cursor

### Process key

At raw-default host startup, #134 creates a cryptographically random 32-byte
`session_cursor_key`. It remains in process memory only. It is never persisted,
backed up, exported, logged, diagnosed or placed in browser state. A restart
invalidates every outstanding cursor with `400 invalid_cursor`.

### Semantic filter frame

The frame starts with the exact ASCII bytes:

```text
local-monitor-session-filter\0v1\0
```

It then carries `scope` through `limit` in request-property order, omitting
`cursor`. Encodings are:

- nullable string: byte `00` for null, otherwise `01` +
  `U32BE(byte_length)` + exact UTF-8 bytes;
- required string: `U32BE(byte_length)` + exact UTF-8 bytes;
- string array: `U16BE(count)`, then ordinal-sorted entries as
  `U32BE(byte_length)` + exact UTF-8 bytes;
- nullable Boolean: `00` null, `01` false, `02` true;
- nullable limit: `U16BE(0)` for null/default, otherwise the exact value;
- `q`: the exact bounded normalized search bytes, not original spelling;
- every other string: its validated exact bytes.

```text
filter_binding = HMAC-SHA256(session_cursor_key, semantic_filter_frame)
```

Only the 32-byte HMAC enters the cursor. Raw/normalized `q`, model values and
an unkeyed digest of either never enter it.

### Exact 110-byte cursor

Before unpadded base64url encoding, the cursor is exactly 110 bytes:

```text
offset  size  field
0       1     version = 0x01
1       32    filter_binding
33      1     sort_group: 0x00 valid-time, 0x01 invalid-time
34      8     signed Int64 epoch milliseconds as two's-complement U64BE;
               exactly zero for invalid-time
42      36    canonical lowercase Session UUIDv7 ASCII
78      32    cursor_tag
```

```text
cursor_tag = HMAC-SHA256(
  session_cursor_key,
  ASCII("local-monitor-session-cursor\0v1\0") + bytes[0..77])
```

The public token is canonical unpadded base64url and exactly 147 ASCII
characters. `+`, `/`, `=`, whitespace, percent encoding, noncanonical pad bits,
wrong length/version/group, invalid UUID, bad tag, filter mismatch or another
process key is `400 invalid_cursor`. HMAC comparison is constant-time.

The keyset is only:

```text
sort_group ASC,
sort_instant_utc DESC,
session_id DESC ordinal
```

The resume predicate is exclusive and follows that tuple exactly. For a valid-
time cursor with epoch milliseconds `T` and Session ID `S`, the next page is:

```text
(sort_group = 0 AND
  (sort_instant_utc < T OR
   (sort_instant_utc = T AND session_id < S ordinal)))
OR sort_group = 1
```

It therefore completes smaller valid times/IDs and then admits every invalid-
time row. For an invalid-time cursor, bytes 34..41 are exactly zero and the next
page is only:

```text
sort_group = 1 AND session_id < S ordinal
```

Equality is never resumed, and an invalid-time cursor never returns to the
valid group.

The complete filter body is resent on every page. The server never reconstructs
filters from a cursor or looks up a saved search.

## 10. Session API response and registration gate

### Success transport already fixed here

- status `200`;
- `Content-Type: application/json; charset=utf-8`;
- `Cache-Control: no-store`;
- no `Location`, `ETag`, `Set-Cookie` or CORS header;
- response never echoes `q` or model filters;
- any returned continuation token follows section 9.

The success transport in this section composes with the complete closed object
graph, member types, nullability, exact property order, coherent snapshot rules
and canonical bytes in
[`local-monitor-v1-session-collection.md`](local-monitor-v1-session-collection.md).
That document is the sole success authority and supersedes #133's former GET
and incomplete response prose. The fixed errors below remain owned here.

### Fixed errors

Every nonempty error is compact strict UTF-8 JSON with no BOM, indentation,
trailing space or newline:

```json
{"error":"<fixed_code>"}
```

| Condition | Status and code |
| --- | --- |
| Non-loopback/invalid Host | `400 invalid_host` |
| Cross-site or missing/wrong CSRF | `403 csrf_rejected` |
| Invalid UTF-8/JSON/schema/type/value | `400 invalid_request` |
| Invalid/tampered/restarted/filter-mismatched cursor | `400 invalid_cursor` |
| Valid absent Repository for Repository scope | `404 repository_not_found` |
| Request body above 32,768 bytes | `413 request_too_large` |
| Unsupported media/charset/content encoding | `415 unsupported_media_type` |
| Result cannot fit the accepted response ceiling | `409 workspace_too_large` |
| SQLite remains busy after the accepted bounded policy | `503 persistence_busy` |
| Raw-default Local Monitor composition is unavailable | `503 local_monitor_ui_unavailable` |
| Any method other than POST | `405 method_not_allowed` |

All errors have `Cache-Control: no-store`. Nonempty JSON has the exact JSON
content type above. After the common Host guard succeeds, every non-POST method
has exact `Allow: POST`. `HEAD` has the same status, media type and
representation `Content-Length` as the winning JSON error but emits zero entity
bytes: invalid Host wins with 400; otherwise method dispatch wins with 405. No
`OPTIONS` request becomes a CORS preflight response.

## 11. Human response and recovery matrix

The exact machine response graphs, headers, bytes, bounds, cursor binding and
error precedence for the four #134 Session-detail APIs are owned by
[`local-monitor-v1-session-detail.md`](local-monitor-v1-session-detail.md).
This document continues to own the shared identifier and human-route grammar.

For raw-default human requests, Host validation runs first. Raw-path template
classification then distinguishes a primary template from the near paths in
section 3. For an exactly shaped primary template, a method other than GET or
HEAD returns the fixed 405 before identity/query/data resolution. GET and HEAD
then validate path identity and the complete query before any Repository,
Session, child or comparison resource lookup. Only a valid query proceeds to
exact parent/scope/data resolution in that order.
Thus the malformed-ID and malformed-query 400 rows below apply to GET/HEAD;
non-GET/HEAD uses 405, while a near path uses empty 404 for every method.

### Active matched routes

Successful GET is `200`, `Content-Type: text/html; charset=utf-8` and no-store.
HEAD has the GET-equivalent status, content type, no-store and representation
`Content-Length`, with zero entity bytes.

For an active matched primary path, POST/PUT/PATCH/DELETE/OPTIONS and every
other non-GET/HEAD method return `405`, exact `Allow: GET, HEAD`, no-store,
no content type, `Content-Length: 0` and zero entity bytes.

Human error HTML bytes and sentence-level copy are deliberately not frozen.
The status, no-store header, closed state token and closed recovery-action
token are frozen. Error rendering never receives or reflects raw request text,
an invalid ID, q/model value, locator/path, exception, stack or SQL.

Closed recovery actions are:

```text
open_repository_selection
open_all_sessions
open_repository_sessions
open_session_overview
refresh_session_summary
recreate_comparison
retry
```

Parsing a canonical parent ID is not resource resolution. An action that needs
a Repository or Session ID is emitted only after the complete query is valid
and that parent/resource has resolved exactly. It never carries a malformed,
unresolved or merely echoed value.

| Condition | Status/state | Recovery action |
| --- | --- | --- |
| Invalid Repository-selection query | `400 invalid_request` | `open_repository_selection` |
| Malformed Repository ID | `400 invalid_request` | `open_repository_selection` |
| Valid absent Repository | `404 repository_not_found` | `open_repository_selection` |
| Invalid Repository Explorer query | `400 invalid_request` | `open_repository_selection` |
| Invalid all/unassigned Explorer query | `400 invalid_request` | `open_all_sessions` |
| Malformed Session ID | `400 invalid_request` | `open_all_sessions` |
| Valid absent Session | `404 session_not_found` | `open_all_sessions` |
| Invalid Session selection query or malformed execution/node/analysis child | `400 invalid_request` | `open_all_sessions` |
| Valid absent execution | `404 execution_not_found` | `open_session_overview` |
| Valid absent node or execution/node mismatch | `404 node_not_found` | `open_session_overview` |
| Valid absent, wrong-scope or expired transient AI run | `404 analysis_run_not_found` | `open_session_overview` |
| Stale Workspace revision | `409 workspace_snapshot_stale` | `refresh_session_summary` |
| Session Workspace exceeds its accepted bound | `409 workspace_too_large` | `open_all_sessions` |
| Invalid comparison query | `400 invalid_request` | `open_repository_selection` |
| Malformed comparison ID | `400 invalid_request` | `open_repository_selection` |
| Valid unknown or Repository-mismatched comparison | `404 comparison_not_found` | `recreate_comparison` |
| Exact known expired comparison | `410 comparison_expired` | `recreate_comparison` |
| Persistence busy | `503 persistence_busy` | `retry` |
| Raw-default UI composition unavailable | `503 local_monitor_ui_unavailable` | `retry` |

An invalid Host is a pre-page security response: status 400,
`Content-Type: application/json; charset=utf-8`, no-store, and the exact strict
UTF-8 bytes `{"error":"invalid_host"}` with no BOM, indentation, trailing space
or newline. HEAD retains that representation's media type and `Content-Length`
but emits zero entity bytes. It is not rendered as a human state page.

### Unmatched and near-path response

Existing exact registered technical/machine routes are dispatched first and
retain their owning contracts. After that dispatch, a would-be primary-route
path that fails the primary-template classification in section 3 uses an empty
response for every method:

- status `404`;
- `Cache-Control: no-store`;
- no `Content-Type`, `Allow`, `Location`, `ETag` or `Set-Cookie`;
- `Content-Length: 0`;
- zero entity bytes.

It does not render a human error model, advertise an endpoint or redirect.

## 12. Comparison identity and expiry amendment

#165/#166 issue canonical local UUIDv7 comparison IDs. The human route first
resolves the exact Repository. It then resolves only an exact
`(repository_id, comparison_id)` pair. A Repository mismatch is
`comparison_not_found` and never discloses membership.

To make the accepted 24-hour expiry deterministic after operational content is
deleted, the comparison component owns this minimal append-only, non-listable
table:

```text
local_comparison_expiry_tombstones
  comparison_id  TEXT COLLATE BINARY PRIMARY KEY
  repository_id  TEXT COLLATE BINARY NOT NULL
  expired_at      TEXT COLLATE BINARY NOT NULL
```

- both IDs are canonical lowercase UUIDv7;
- `expired_at` is the snapshot's exact expiry instant in UTC
  `yyyy-MM-ddTHH:mm:ss.fffffff+00:00` form;
- application and component schema validators reject any other value;
- UPDATE and DELETE are rejected by immutable guards;
- insert is exact insert-or-identical; a same-ID field mismatch fails closed;
- when `now >= snapshot.expires_at`, reads return `410 comparison_expired`
  before cleanup;
- cleanup atomically inserts/validates the tombstone and deletes all
  operational snapshot/result/evidence content;
- an exact tombstone pair returns `410 comparison_expired`;
- a valid unknown ID or Repository mismatch returns
  `404 comparison_not_found`;
- the tombstone contains no cohort/Session IDs, filters, receipt, evidence,
  metric, hash, q/model value, label, content or path;
- there is no tombstone list/read API;
- the runtime-backup owner removes this exact table from its private staging
  copy transactionally after SQLite backup and before inventory/hash/archive;
  it never mutates the source database;
- the table and rows are absent from the manifest/database member and restore;
  accepted restore startup creates and validates one empty table before HTTP
  readiness, without reconstructing a tombstone;
- sanitized export/import never queries or represents the table;
- a restored database therefore returns 404 for an old comparison URL;
- tombstones remain for the lifetime of the current runtime database and are
  not pruned or reconstructed.

This exclusion is executable only through the exact staging projection in
[Runtime Backup and Restore](runtime-backup-restore.md). It grants no exclusion
to future #166 snapshot/result/evidence tables. #166 must add each such table's
exact validated staging removal, dependency order and empty/absent restore
behavior to that owning contract before an operational table can ship.

The retained fixed row is the smallest state that preserves deterministic
`410`. Its database-lifetime growth is an accepted tradeoff; adding pruning
would reintroduce time-dependent `410`/`404` behavior, and retaining comparison
facts would violate the operational-only boundary.

## 13. Retired human routes

### `/traces` list

The list retires atomically with #138 Session Explorer integration, not when a
parser helper or placeholder page lands. In the same integrated host
composition:

- the three Explorer pages are functional through #134's POST read under its
  later accepted canonical response contract;
- the old `/traces` list page is unregistered;
- there is no interval with both list UIs or with neither functional list.

The retired-list classifier is intentionally narrow and matches the existing
list classifier only: one path segment equal to `traces` ordinal-ignore-case,
with zero or one trailing slash, and any query. It does not match a double
slash, encoded spelling, extra segment or `/traces/{traceId}`.

Every method for a matched retired-list spelling returns the empty no-store
404 from section 11, with no `Allow`. It is a removed endpoint, not a 405 or a
redirect to `/sessions`.

Exact `/traces/{traceId}` and its raw/span/analysis technical descendants
remain under their existing owners. #136 does not change their case, slash,
query, method, status, header or byte behavior.

### `/historical-analysis`

This human page remains unchanged until #164 integrates its backend. At that
same integration point its existing one-segment, ordinal-ignore-case,
optional-one-trailing-slash classifier becomes the empty no-store 404 for
every method. Versioned historical-analysis machine APIs remain unchanged.

## 14. Sanitized-only and logging

Under `--sanitized-only`:

- no primary page or `/api/local-monitor/v1/*` endpoint is registered;
- known human GET/HEAD uses the existing empty no-store 404;
- the Session POST body is not read;
- other unmatched-method behavior remains #159/#168-owned and is not replaced
  by a route-specific JSON or 405 response here.

The Local Monitor application may log only a fixed route template, method,
status and bounded duration for this surface. It never logs raw request target,
body, cursor, semantic frame/binding, q/model text, normalized search,
Repository/Session/comparison ID, rejected value, SQL or exception.

## 15. Required deterministic proof

Use synthetic identities and text only.

1. All path literal/ID/case/percent/slash matrices, including matched uppercase
   UUID 400 versus near-path empty 404 and reserved `/sessions/unassigned`
   precedence/case variants.
2. Per-page unknown/duplicate/empty query matrices and exact node/execution/
   analysis scope resolution.
3. Session POST exact/minimum/maximum body; UTF-8/BOM/depth/trailing JSON;
   duplicate/unknown/missing/null/type/value; media/content-encoding/origin/
   CSRF and declared/streamed overflow.
4. Scope, timestamp, source/status, model/q scalar/byte/normalization,
   Boolean, limit and browser path-to-body mapping matrices.
5. A fixed synthetic 32-byte key golden vector proving the exact 110-byte and
   147-character cursor, framing, HMACs, tamper/restart/filter mismatch,
   noncanonical encoding, valid-to-invalid group ordering and both exclusive
   resume predicates, including zero invalid-time bytes.
6. Browser proof that q/model enter only the POST body; safe state survives;
   q/model/non-default limit reset; URL cursor eligibility requires exact
   q=null/model=[]/limit=null; no storage/cache/console/log/error leak or silent
   cursor repair occurs.
7. Pure-parser proof for exact API status/media/no-store/Allow/HEAD/error bytes
   and nonreflection; active HTTP proof waits for the canonical #134 response
   contract and endpoint-registration gate.
8. Pure-parser human GET/HEAD/method/status/no-store/state/recovery and
   nonreflection matrices, without snapshotting sentence-level HTML bytes;
   active HTTP proof waits for each owning registration gate.
9. Comparison live/just-expired/cleaned-tombstone/unknown/Repository-mismatch,
   atomic cleanup, immutable guards, staging-only backup removal, manifest/
   restore absence, empty startup rematerialization and export exclusion.
10. Atomic `/traces` retirement, deferred `/historical-analysis` retirement,
    surviving technical evidence and frozen Monitor/Workspace/SSE bytes.
