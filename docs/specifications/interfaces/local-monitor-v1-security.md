# Local Monitor v1 Security and Data Boundary

Status: **Accepted current authority**
Route/transport amendment: PO136-A2b, 2026-08-09
Archive amendment: D082, 2026-08-09
Skill snapshot amendment: D083, 2026-08-11
Product posture: loopback-only, single trusted local user

## Human UI posture

The Local Monitor human UI exists only in raw-default posture.

Raw-default may display the local user's captured prompt, response, Tool input/result, Sub-agent input, Skill snapshot, local path and AI report where an accepted contract authorizes it.

Every raw-local surface remains:

- loopback-only;
- Host-header validated;
- same-origin;
- `Cache-Control: no-store`;
- escaped inert text;
- retention-authorized;
- bounded by closed routes and payload limits;
- absent from normal logs and repository artifacts.

Exact human-path, query, method, error, browser-history and Session Explorer
POST behavior is owned by
[Local Monitor v1 Route and Session Explorer Transport](local-monitor-v1-route-transport.md).

## Sanitized-only posture

`--sanitized-only` is receiver-only per Issue #159.

- host, ingestion, health and accepted machine APIs remain available;
- Razor Pages and human static assets are not registered;
- `/api/local-monitor/v1/*` is not registered;
- no per-screen metadata-only fallback or explanation page is provided;
- unmatched human GET/HEAD returns empty 404 with no-store.

Existing `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE bytes/availability remain under their frozen contracts.

## Raw-local surface enumeration

The accepted v1 raw-local surface includes only the following owned categories.

### Session and instruction

- first-instruction list label and search index, retained no longer than its source content;
- full instruction/Event content on demand;
- exact raw record/span/event technical links.

Authority: #133/#134 and existing retention contracts.

### Tool / Sub-agent / Event

- Tool input, result and error;
- exact Sub-agent input when captured;
- Event/error/permission content.

Authority: #133/#134.

### Skill

- historical Skill body and definition path captured only by the accepted
  additive v2 exact `skill.invoked` Session Event/snapshot transaction;
- current file only after explicit POST action and current inventory/path/root/reparse validation.

Frozen Session ingest v1 supports `skill.started | skill.completed` and treats
`skill.invoked` as unsupported without changing any other v1 wire, limit,
status or response bytes. Authority:
[Skill Invocation Snapshot](skill-invocation-snapshot.md), #157/#158.
Historical snapshot and current file are never substituted for one another.
Invocation/inventory claim validity is independently owned by
[Skill Projection](../layers/skill-projection.md). A retained snapshot cannot
resurrect a claim. OTel claims require a current resolved compatibility
revision/generation; SDK Session/Event claims instead require exact persisted
source identity and current-registry acceptance of their complete compatibility
tuple, without requiring trace/span. OTel projection workers hold exact
Retention operation leases through their publish fence; an unavailable input
publishes no OTel claim.
Cross-arm linkage requires exact producer trace ID and span ID together;
name/path/time/cardinality and Session co-membership never link claims.
D083 closes the remaining #119/#158 product decisions; it does not claim that
the implementation or release gates have passed. The nonregistered #119
parser/handoff follows mandatory live-Issue reconciliation. #158 runtime work
waits for the exact prerequisite join, while host activation, route
registration and release remain gated by the focused, platform, live,
full-validation and review evidence in the snapshot contract.

After those gates pass, D083 adds to raw-default composition only the additive
`POST /api/session-ingest/v2/events` and these three Skill raw routes:

- `GET /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}`;
- `GET /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/content`; and
- `POST /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/current-file-read`.

Receiver-only/`--sanitized-only` composition registers none of the producer,
bridge, #119 sink, #158 writer/service or four routes above; each endpoint is
absent/nonmatching rather than remapped to a service error or frozen-v1
handler. Frozen v1 keeps its exact unsupported-`skill.invoked` behavior.
Once activated, raw-default keeps v2 mapped when the runtime generation is
absent/mismatched and fails it at stage 1 with exact
`503 local_monitor_ui_unavailable` before body read. Metadata and historical
content remain mapped without a live-runtime dependency. A configured and
platform-certified current-file POST likewise remains mapped across runtime
loss and, after its fixed earlier authorization stages, forms exact
`503 skill_current_file_discovery_unavailable` subject to the terminal
Retention rule below; runtime loss never relabels or suppresses stored history.

`SkillRuntimeCapabilityBridgeV1` is the sole SDK-event admission bridge to v2
HTTP. It admits only the callback-owning immutable runtime generation, retains
at most 64 pending process-memory entries after expiry purge, and gives each
entry exactly 30 seconds under an injected monotonic clock with
`now < expires_at`. Each cryptographically random 32-byte token is exact
43-character unpadded base64url, is sent in one physical
`X-CAO-Skill-Runtime-Capability` header and is atomically consumed once before
body read. Missing, malformed, duplicate/combined, unknown, expired, canceled
or already-consumed tokens return exact stage-1
`503 local_monitor_ui_unavailable` before body read and writes; arbitrary
loopback callers cannot acquire the current runtime generation. Token,
capability, body length/digest, pending count, generation identity and expiry
never enter logs or metrics and are never persisted, backed up, restored or
returned.

`SkillRuntimeBridgeHttpTransportV1` is the sole sender. It receives only the
actual Kestrel listener's already-bound numeric loopback HTTP address and port
(`127.0.0.1` or `[::1]`), uses exact HTTP/1.1 with one POST, and accepts no DNS,
user URI, path, configuration or environment target. Its dedicated sender has
`UseProxy=false`, `AllowAutoRedirect=false`, `UseCookies=false`, null
credentials/default proxy credentials, no preauthentication,
`ActivityHeadersPropagator=null`, no ambient/default/trace headers and no retry,
resilience or automatic-resend handler. A redirect, authentication/proxy
challenge, non-204 result or transport ambiguity is sanitized producer
unavailability and cannot forward the header/body elsewhere or resend it. There
is no direct callback-to-writer path, second Skill authority or previous-
generation fallback.

The public discovery-root carrier is only repeatable raw-default CLI options
`--skill-discovery-project-path` (0..16) and
`--skill-discovery-directory` (0..32). There is no environment, JSON,
delimiter-list, CWD, Repository, historical-path or inferred-root fallback.
Any explicit invalid root rejects startup without echoing its value. A zero-root
host registers no current-file service/POST and never calls `DiscoverAsync`.
Windows and Linux register independently only after their exact native
filesystem/platform certification; an unsupported platform with explicit
roots rejects startup, while an unsupported zero-root platform still serves
the metadata and historical routes. macOS/BSD/other systems register no
current-file POST. Historical paths are parsed only as request-local comparison
keys and are never opened directly; current bytes come only from the selected
platform's retained-root native no-follow handle/fd walk.

Historical-content and current-file raw publication use the Retention handle's
store-backed terminal operation. That operation opens its own
`BEGIN IMMEDIATE`, acquires the Monitor publication scope, proves the exact live
persisted lease/expiry and samples the Retention clock once; #158 supplies no
timestamp and performs no validate-then-seal split. `lost|busy` discards all
buffers, releases every capability and aborts the transport with no HTTP
status, header or entity. Both database and publication scopes are released
before any HTTP I/O.

Current-file safe results determined before runtime-capability acquisition use
Retention `TryCompleteWithoutRaw()` alone and fabricate no runtime capability.
A safe error determined after runtime acquisition first completes Retention
without raw and then wins runtime `TrySealResponse`. A raw success first wins
runtime `TrySealResponse` and then Retention `TrySealRawResponse`; only both
seals allow the single fully buffered entity. Loss at either required authority
discards the candidate under the exact snapshot-contract mapping, never
publishes partially authorized bytes and never reacquires another generation.

### Repository

- canonical GitHub locator only in a dedicated management read;
- standard Repository cards and Session rows do not return raw locator/path/owner.
- admitted canonical locator, fingerprint, display casing and bounded
  provenance are catalog-owned durable raw-local metadata after raw expiry;
  they do not reconstruct the raw body or expose the internal raw-record
  reference;
- Repository management routes and catalog data are absent in receiver-only
  and `--sanitized-only` composition and excluded from sanitized evidence
  export/import.

Authority:
[Local Repository Catalog and Session Assignment](local-repository-catalog.md),
#155/#156. #134 alone owns the composite
`GET /api/local-monitor/v1/repositories` route.

### Archive

The accepted raw-default archive surface contains exactly:

- `GET /api/local-monitor/v1/archive`;
- `POST /api/local-monitor/v1/archive-actions`; and
- `GET /api/local-monitor/v1/archived-items`.

Authority: [Local Archive v1](local-archive.md), #160/#161 and D082. #161
supplies direct Session/full-catalog Repository facts; #156/D081 alone validates
and composes assignment-dependent effective eligibility/reason. The archive
routes, application and contributor are absent from `--sanitized-only` before
route adaptation, request-body read or archive-store access. Runtime-backup
component validation remains a separate non-human database authority. Archive
state/history is absent from sanitized evidence and repository-safe artifacts.

### AI

- immutable Session report content;
- transient node/Repository/Compare result;
- provider-bound raw reads through process-internal tools only.

Authority: #162/#163/#164.

### Backup / replay

Runtime backup and raw replay retain their existing separate explicit authorities. Backup is not a repository-safe export.

## `/api/local-monitor/v1` boundary

This namespace is raw-default human-UI support, not a sanitized public API.

- all responses are no-store;
- mutations require same-origin and CSRF;
- the body-bearing Session collection read also requires same-origin and the
  exact CSRF header because it carries raw-local search/model state;
- request bodies are closed and bounded;
- error responses use fixed codes and never echo raw input or inner exceptions;
- raw values, display names, local paths and provider content never become URL identity;
- routes are absent in sanitized-only posture.

For each exact matched archive path, global loopback/Host validation precedes
path and method dispatch. Both GET routes accept GET only and the action route
accepts POST only; unsupported methods, including OPTIONS, use the owner-fixed
405 response and never become CORS preflight. HEAD uses the same fixed status,
headers and representation length with zero entity bytes. Every archive route
then requires same-origin; POST additionally requires exactly one effective
`x-monitor-csrf: local-monitor` value. Complete query admission precedes cursor
decoding, and POST origin/CSRF precede bounded media/body reads. Every response
is no-store; fixed errors echo no target, row, path, SQLite/framework message,
inner exception or request value. Exact bytes and precedence are owned by
[Local Archive v1](local-archive.md), with no alias or framework-generated
fallback entity.

The sole Session collection transport is the 32,768-byte closed JSON
`POST /api/local-monitor/v1/sessions`. The former unimplemented GET is removed;
there is no alias, saved-search handle, compatibility reader or fallback. This
request/security boundary composes with the accepted exact success wire in
[`local-monitor-v1-session-collection.md`](local-monitor-v1-session-collection.md);
#134 alone owns active mapping and serialization.

## Retention

- every raw content read uses the existing Retention catalog access lease;
- an active access/operation lease prevents physical deletion;
- every new read admission rechecks expiry/delete denial; consumption of an
  admitted grant uses its immutable capability and live lease without a current-
  row lifecycle/revision reread;
- derived raw instruction labels cannot outlive their source content;
- Skill body/path index may outlive content only as digest/provenance/expired state;
- sanitized Skill invocation/inventory rows are readable only through the
  current-valid #154 projection authority;
- archive does not extend retention;
- Session AI report content uses analysis retention;
- node/Repository/Compare AI operational content is deleted after 24 hours and is not backed up;
- Compare deterministic snapshots are deleted after 24 hours and are not backed up.
- Comparison expiry retains only the route/transport-owned append-only
  `(comparison_id, repository_id, expired_at)` tombstone in the current runtime
  database. It contains no cohort, Session, filter, receipt, evidence, metric,
  hash, content, model or path and has no list/read API. Runtime backup removes
  the exact table transactionally from its private staging copy before
  inventory/hash/archive, never from source; manifest/restore omit it and
  restore startup creates it empty. Sanitized export/import never queries it.

## AI provider egress

AI execution crosses the local-only boundary.

- provider is GitHub Copilot SDK in v1;
- actions are visible only when provider-ready;
- the user starts every run explicitly;
- Settings permanently explains that selected content may be sent to GitHub Copilot;
- Repository selection requires a scope preview;
- the provider receives only one bounded immutable snapshot and process-internal tools constrained to its evidence index;
- the SQLite file and arbitrary SQL are never exposed;
- scope-outside identifiers are rejected;
- credentials and secrets are removed by the accepted filters and closed tool responses;
- provider request/response/raw bodies/paths are not logged or emitted in diagnostics/artifacts.

## Repository-safe outputs

Sanitized evidence export, static dashboards, GitHub Issues, repository docs, logs and test artifacts may contain only their existing allowlisted contracts.

They must not contain:

- prompt/response/Tool/Sub-agent/Skill body;
- raw local paths or Repository locators;
- credentials/PII;
- raw AI provider input/output;
- reversible sensitive identifiers unless an existing contract explicitly authorizes them.

## Browser state

- no raw value is stored in URL, browser storage or reusable cache;
- Explorer URLs carry only canonical timestamps, closed source/status,
  `has_skill`/`has_subagent`/`has_error`/`has_retry`, archive/mode/Settings
  tokens and an eligible opaque process-keyed cursor;
- dynamic `q` and `model` values exist only in current-page form/JavaScript
  memory and the bounded Session POST body; non-default limit is also transient;
  reload/back clears all three;
- URL cursor eligibility requires exact q=null/model=[]/limit=null/default 50;
  other cursors remain page-memory/POST state;
- cursor HMACs bind the complete semantic filter without exposing raw or
  normalized q/model values or an unkeyed low-entropy digest;
- comparison and AI URLs carry opaque snapshot/run IDs only;
- pre-preview Compare checkbox selection is transient;
- follow-up AI transcript is held only in the current browser page and is not persisted;
- other Settings/query state contains only the route/transport-authorized
  canonical timestamps, closed tokens, Booleans and opaque IDs/cursors.

## Rendering

- text is inserted with server escaping or DOM `textContent`;
- generated Markdown/HTML is not executed;
- color is not the only signal;
- long content is bounded and never partially returned when its owner contract requires fail-closed oversized state.

## Frozen boundaries

The v1 redesign does not modify:

- `/api/monitor/*` v1 shape/order/bytes;
- `/api/session-workspace/*` v1 shape/order/bytes;
- SSE shape/order/bytes;
- `session_events.content_state` vocabulary;
- exact identity/provenance rules;
- no heuristic Session/Repository/parent binding;
- no missing-to-zero or composite score.
