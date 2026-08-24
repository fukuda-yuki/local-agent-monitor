# Skill Invocation Snapshot Interface

Status: **D086 producer decision-closed; frozen v1 ready; #119 nonregistered parser/handoff remains conditional on mandatory live-Issue reconciliation/readback; #158 owned-session importer implementation and release evidence pending**

This specification is the detailed authority for Issues #119, #157, and #158.
It defines the frozen Session-ingest v1 correction, the exact Skill-only v2
transport and runtime-capability boundary, the `skill_invocation_snapshot:1`
component, historical content reads, and explicit bounded current-file reads.

D083 resolves the eight wire/store/read choices. D086 supersedes only its
Group 5 producer/topology clauses and the affected raw-analysis no-Skill rule.
No product contract is left for an implementation to infer. Canonical promotion does not itself
prove the prerequisite implementations, platform walkers, signed-in live
runtime, focused/full validation, or independent review required below.

This interface is additive to the frozen
[Canvas Session Workspace v1](canvas-session-workspace.md). It does not change
the shape, property order, bytes, availability, or semantics of
`/api/monitor/*`, `/api/session-workspace/*` v1, Session ingest v1, or SSE.
It creates no compatibility entry, migration/adoption/backfill path, dual
reader/writer, direct historical-path read, inferred join, or fallback because
v2 has never shipped.

## Scope and authority

The ownership split is exact:

- Issue #119 owns the strict bounded v2 parser and immutable capability-bearing
  typed handoff. It lands nonregistered and writes nothing.
- Issue #154 is the single current-valid Skill claim, point-diagnostic, and
  opaque current-authorization capability authority.
- Issue #158 owns the `skill_invocation_snapshot:1` component, its exact
  seven-authority writer/receipt validation, and the three raw-local Skill
  routes after every prerequisite and release gate succeeds.
- The Session subsystem owns Local Session, Run, Event,
  `session_event_content`, selected native binding, and generic-route policy.
- Retention owns the sole raw-content item, admitted read/operation grants,
  terminal publication seals, denial, pin/delete interaction, and cleanup.
- Issue #124 owns the exact Session 14 parent schema; #156 owns archive fact
  composition; #161 owns direct archive facts and `local_archive:1`.
- Issue #134 consumes finished authorities for Workspace reads and adds no
  direct table reader or second Skill projection.
- Issue #140 consumes this interface for the final Skill inspector.
- Issues #162/#163 may consume an exact historical snapshot only through their
  explicit bounded AI snapshot authority.

Historical Skill content and the current discovered file are different
authorities. Neither substitutes for the other. Issue #152 remains outside
this contract.

## Execution status

The frozen-v1 correction is independently executable and remains unchanged.
After tracked D083 promotion and the mandatory live-Issue reconciliation/readback,
#119 may implement only its nonregistered parser/typed-handoff lane. #158
runtime code may start only after the integrated Session 14 -> archive
composition/direct-fact chain, the separate Retention prerequisite
implementation, and that #119 lane are all landed and green. Host activation,
route registration, and release remain gated by the focused, platform, live,
full-validation, and review evidence in this specification.

A v2 route never returns `204` until its exact complete transaction or exact
validated equality-replay terminal fence succeeds.

## Current-valid Skill claims

### Source arms

The Issue #154 Skill claim authority has exactly two source arms:

```text
otel_trace_span
sdk_session_event
```

The `otel_trace_span` arm is bound by:

- exact trace ID and exact span ID;
- the current SourceCompatibility resolution;
- the current #154 compatibility revision and generation; and
- the exact retained OTel input frontier used by that generation.

The `sdk_session_event` arm is bound by:

- exact Local Monitor Session ID;
- exact persisted `session_events.event_id`;
- exact producer source event ID;
- exact source adapter and source surface;
- exact source application version;
- exact adapter version;
- exact normalization version;
- exact payload schema;
- exact producer schema fingerprint; and
- exact `payload_sha256` over the received UTF-8 byte slice of the normalized
  `event.payload` JSON value token, plus the exact byte length of that slice.

The complete SDK claim binding, point diagnostic, and opaque current-
authorization capability remain owned by
[Skill Projection](../layers/skill-projection.md). This interface consumes those
#154 results and does not query claim or registry tables through a second reader.

The SDK arm is current-valid only when the current compatibility registry
accepts this complete tuple:

```text
(
  source_application_version,
  adapter_version,
  normalization_version,
  payload_schema,
  schema_fingerprint
)
```

The existence of raw content or a snapshot row does not make an unsupported
tuple valid and does not revive an invalid or stale claim.

### Cross-arm merge

An SDK observation and an OTel observation may merge into one invocation claim
only when the producer supplied both an exact trace ID and an exact span ID and
both values equal the OTel claim.

The following never merge the arms:

- trace ID without span ID;
- Local Session or Run identity alone;
- Skill name or definition path;
- timestamp or ordinal;
- proximity or arrival order; and
- expected cardinality.

Without the exact trace-and-span link, both rows remain positive observations.
When one Session has both arms and they cannot be exactly deduplicated, the
aggregate Skill invocation count is JSON `null` and its existing state is
`certification_pending`. The observations are not added together and missing
is not converted to zero.

An OTel-only claim has no snapshot row. Its snapshot projection is:

```text
snapshot_id = null
snapshot_state = not_captured
```

No fake Event ID or placeholder snapshot row represents `not_captured`.

## Frozen Session ingest v1 correction

This accepted correction supersedes the previously documented inverse clause
in [Canvas Session Workspace v1](canvas-session-workspace.md). The shared
Canvas authority now lists `skill.started` and `skill.completed` in the exact
supported-v1 event set and treats `skill.invoked` as unsupported. This restores
the frozen pre-#119 event semantics. It does not change the route, header/body
versions, envelope/event wire shape, size/batch limits, error entity bytes, or
workspace response bytes.

The installed route remains:

```text
POST /api/session-ingest/v1/events
```

Its existing contract remains byte- and behavior-frozen:

- `X-CAO-Session-Event-Version: 1`;
- body `schema_version = 1`;
- body maximum `1,048,576` bytes;
- batch length `1..100`;
- existing adapter/surface enums;
- existing envelope/event property sets and required/nullable rules;
- existing five-value `session_events.content_state` vocabulary;
- existing validation order, status mapping, error entity bytes, queue behavior,
  commit timeout, and `204` behavior; and
- existing `/api/session-workspace/*` v1 response shape, property order, and
  bytes.

The supported v1 event set preserves its pre-#119 membership. In particular:

```text
capture.started
assistant.usage
session.usage_info
session.start
session.started
session.shutdown
session.task_complete
user.message
assistant.message
assistant.turn_end
tool.execution_start
tool.execution_complete
subagent.started
subagent.completed
subagent.failed
subagent.selected
subagent.deselected
skill.started
skill.completed
SessionStart
UserPromptSubmit
PreToolUse
PermissionRequest
PostToolUse
PostToolUseFailure
SubagentStart
SubagentStop
Stop
StopFailure
SessionEnd
```

`skill.invoked` is not a supported v1 event. A syntactically valid
`skill.invoked` v1 event follows the existing unsupported-event behavior:

- persist only the existing unsupported Event metadata;
- use `content_state = unsupported`;
- create no `session_event_content` row;
- increment the unsupported-event counter only for a newly admitted exact
  source event; and
- do not infer or redirect the event to v2.

The generic exact-source replay guard remains unchanged. For an already
persisted `(source_adapter, source_event_id)`:

- an identical replay creates no second Event or content row;
- it does not overwrite or backfill existing content;
- it does not increment the unsupported-event counter again; and
- a conflicting replay remains fail closed under the Session store authority.

There is no retry, redirect, or fallback from v2 to v1.

## D083 decision

Decision: **GO for canonical specification promotion. After tracked closure and
the mandatory live-Issue reconciliation/readback gate, the nonregistered #119
parser/handoff may implement independently of the other engineering lanes.
Bounded #158 runtime implementation may start only after all three prerequisite
lanes join: the integrated `#124 Session 14 -> #156 carrier/composition -> #161
direct archive` chain, the separate Retention prerequisite, and the
nonregistered #119 handoff. Host activation, route registration, and release
remain NO-GO until the focused, platform, live, full-validation, and review
gates below pass.**

This decision supersedes every provisional #158 byte/hash claim that contains
a tenth payload property named `model`, omits the selected native Session
identity, treats an SDK-normalized empty Skill list as observable raw null,
unconditionally ignores Session child triggers, or stores/replays response
bytes. Those provisional hashes are not aliases or historical versions.

## Mechanically fixed artifacts

All files use UTF-8 without BOM, LF only, and one final LF. Hashes are SHA-256
of exact file bytes unless a row explicitly says it hashes decoded bytes.

| Packet file | Bytes | SHA-256 | Canonical destination/use |
|---|---:|---|---|
| `github-copilot-sdk.skill-invoked.v1.schema.json` | 980 | `8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c` | `docs/specifications/contracts/skill-invocation-snapshot/v1/github-copilot-sdk.skill-invoked.v1.schema.json` |
| `github-copilot-sdk.skill-invoked.v1.schema.sha256` | 65 | `3f6b076bb7329662088c0b055a81e5f3d9789cd654ddde27bf3b1877d32ba123` | same directory; content is the schema digest plus LF |
| `compatibility-registry-r0001.json` | 431 | `3ae5d255647edad6e23f077c3e9042be50d593211cd9a90d6c9f7210c53bfdda` | same directory; no registry sidecar |
| `skill-invocation-snapshot.schema.v1.sql` | 9,213 | `502f787c28b13363826aeccde96979ed22dc89c8ee137593922b106528935d7c` | same directory; exactly two tables and eight triggers; no component stamp |
| `session-v14-child-trigger-extensions-r0001.json` | 1,019 | `0b5f7782a9686791c2ce9bcff8638dccf1de44833303c0932f05e2ae57259c64` | `docs/specifications/contracts/session/v14/session-child-trigger-extensions-r0001.json` |
| `session-v14-child-trigger-installed-sql.golden.json` | 979 | `546fe44ec0cbdf21b7c55c99f35b1ce30f749ddae4e0e63e3fb02b3ffa9fb251` | test fixture proving executable DDL versus installed-SQL delimiter bytes |
| `discovery-revision-v1.golden.json` | 1,348 | `7d72e2b7213c04012f49dafc37f55a7573e59eab9c3be7e9c72fdc0778a82a28` | test fixture; five mechanically verified Windows/Linux zero/one/mixed-role frames |
| `json-writer-v1.golden.json` | 1,768 | `9f95ad12d58be87869ced76fb995832e94a102c4ffb43588f8cd4f380200166e` | .NET 10 `Utf8JsonWriter` options, exact escaping/scalar tokens, and pre-writer unpaired-surrogate rejection fixtures |
| `metadata-response-v1.golden.json` | 19,013 | `e3fe1403b13bebbc46856c2220828d333d7513fb398a8521c8dff8b9b5e0130f` | 17 literal metadata/error response fixtures covering every derived-state/nullability row |
| `path-key-v1.golden.json` | 4,497 | `e397becf9711b6fe51bd1174cbca00a450b699a4fe941cc24c298d7739b1370e` | closed Windows/Linux producer-path parse/equality/relation fixtures |
| `request-fingerprint-v1.golden.hex` | 1,453 | `fc0160e65a64e2dcb2154aeab515e4a0351783006a4d644e42ff0cec6354e606` | test fixture; decoded 726-byte frame hashes to `5698c710512676dab263596e169be6e73746525a695f67b7929866fbc502cfb7` |
| `generic-route-not-found.body.hex` | 87 | `0366941f6a5b3316b1cf5fc2204a97eafe6ed133431d70bc7cb914602de57a75` | test fixture; decoded 43-byte entity hashes to `9efd316487e88e9c4ca2440f058d7097518cd01205e5ed1788bd37010f758855` |

The exact migration stamp, deliberately excluded from the DDL artifact, is:

```sql
INSERT INTO schema_version(component,version)
VALUES('skill_invocation_snapshot',1);
```

## Group 1 — Session 14 conditional child-trigger extension

### Current contract

The Session validator has one unchanged Session 14 core fingerprint. It reads
the actual `schema_version` table and activates one compile-time registry entry
only for BINARY-exact text component `skill_invocation_snapshot` with SQLite
integer version `1`. Absence activates nothing. Wrong case, text `"1"`, zero,
two or later, duplicate/corrupt metadata, or manifest/database disagreement is
incompatible.

When and only when that exact stamp is present, the validator requires both and
only both registered trigger tuples: exact name, `type=trigger`, exact target
`session_events`, and SQL equal under the existing Session canonical SQL
tokenizer. Each registry `sql` value is the exact UTF-8 text SQLite returns in
`sqlite_schema.sql` after installing the canonical DDL: it retains the internal
semicolon before `END` and ends in ASCII `END`, with no terminal statement
delimiter. The executable DDL source ends `END;`; SQLite removes that delimiter
when it stores the schema SQL. The Session canonicalizer preserves punctuation,
so the implementation compares installed SQL with the delimiter-free registry
value and does not change the canonicalizer or compare directly with the DDL
source block. Missing, partial, altered, wrong-target, case-aliased, additional
registered-namespace, future-version, or terminal-delimiter-mismatched triggers
fail closed. Only after proving the pair does the validator remove that pair
from the Session profile before computing the unchanged parent fingerprint.
The existing Retention trigger exemption is unchanged and is not part of this
registry.

The parent check is not child validation. A later mandatory
`skill_invocation_snapshot:1` validator still proves the exact two tables,
eight triggers, namespace, columns/constraints/FKs, every graph invariant,
receipt reconstruction, content/Retention/claim equality, and Session 14 null
terminal outcome/version. Startup, ensure, online-backup creation,
inspect/preview, staging restore, pre-swap, and installed validation all run it.

Install in one transaction: validate exact Session 14 + Retention 1 + Skill
Projection 1; create both tables and eight triggers; insert the exact stamp
last; rerun Session through the now-active conditional registry; run the
complete child namespace/empty-data-graph validator; commit once. Any failure
rolls back the objects and stamp together. No
committed object-only/stamp-only state, `INSERT OR REPLACE`, adoption, repair,
pending-install bypass, or Session 13/14 dual validator is permitted.

| Alternative | Decision |
|---|---|
| A. Make the two triggers Session-14-owned | Reject: presence/absence would create two parent fingerprints under one version. |
| B. Ignore a prefix/name or any stamped child trigger | Reject: altered executable SQL could disappear from parent validation. |
| C. Exact `skill_invocation_snapshot:1` closed registry | **Choose:** prove the exact pair, filter only that pair, then require full child validation. |

Strongest counterexample: a database can have the exact stamp and the two exact
Session-target triggers while omitting both child tables and the other six
triggers. Parent validation may correctly pass; only mandatory later child
validation rejects that partial component.

## Group 2 — separate Retention pinned-read prerequisite

### Current contract

The exact canonical admission, renewal, publication, terminal, and equality
authority is [Raw Store Normalization](../layers/raw-store-normalization.md),
with consumer-specific behavior in this interface, Canvas/Session workspace,
historical analysis, and raw-local replay. Admission uses `row_readable`: a
pinned item remains admissible after its original expiry when
all current source/owner/receipt/revision and denial proofs hold; an expiring
item is admissible only strictly before item expiry. Consumption instead uses
the immutable admitted source/owner capability plus its exact live lease. It
does not recheck the current row's lifecycle, expiry, denial, or revision, so a
lease committed while an expiring row was readable keeps its bounded duration
across cleanup transitions. Cleanup denies new admission and may advance
state/revision at expiry, but physical deletion waits for the admitted lease.
Expiring renewal must commit before item expiry; pinned renewal ignores the
historical expiry while current admission proofs hold. The original pinned
expiry remains immutable historical metadata, and expiry alone never mutates
`retained_by_policy` toward deletion. Missing or invalid source/ownership proof
recomputed from the exact current source/catalog retains its existing
irreversible denial transition at admission; stale caller capability/revision/
receipt mismatch denies without mutating the item. This is a
separate canonical amendment and isolated commit that must precede #158.
Retention, never a caller timestamp, owns acquisition time: an initial injected-
clock sample inside the shared transaction preserves expiry-before-source-proof,
and one fresh final sample rechecks every expiring member and derives the exact
two-minute lease expiry immediately before all-or-none insertion/commit.
Selected reads enumerate only metadata candidates under each existing owner's
exact query, ordering, cardinality, and public policy limits; they select no raw
content before a committed hidden handle wins a store-backed publication fence,
except the narrow generic Session transaction-aware adapter. That exception may
buffer content only inside Retention in the same type-check/admission transaction;
no byte is accessible unless commit, hidden publication, and value publication
all win.
That fence re-samples the Retention clock, proves the exact persisted lease and
immutable admitted catalog/store/item/source-item plus owner-token/capability
binding, and atomically competes with the committed-expiry notification. It does
not recompute the current catalog receipt/coverage or require current item
revision/state. Only a
strictly pre-expiry winner publishes the handle. Raw selection then occurs in a
separate consistent transaction under `grant_usable`, with a second store-backed
value-publication fence before any value escapes. Item-policy expiry still takes
priority and commits only exact `DenyAndQueue` for crossed expiring members;
pinned/unexpired siblings stay unchanged. Checked date-add overflow rolls back
without denial mutation, lease/capability/raw output, or exception surface.
These new clock/grant/selector/terminal rules apply only to access/operation raw-
read grants. Deletion and maintenance keep their existing worker/claim clocks,
eligibility, intent/recovery, duration/renew/release, and quiescence; they create
no read grant, raw-read publication, or HTTP terminal capability. The amendment
introduces no shared selector registry and no new global row/cell/aggregate/
batch limit: every current raw-store, projection, Session, raw-replay, sensitive-
bundle, and analysis query preserves its owning accepted data domain. Its
explicit pre-admission `Empty` result preserves each zero-request/candidate
empty value with no lease/handle/timer/source token/terminal call. For nonempty
admission, every hidden handle/dormant expiry notification/cleanup record is
prebuilt before lease commit; precommit failure rolls back, while postcommit
activation failure loses and synchronously releases all members or transfers
their exact tuples to mandatory cleanup, with no partial authority/value.
Its
pre-grant `SelectorUnavailable` result—metadata-query/shape contradiction,
checked date-add overflow, or hidden-handle/notification/cleanup-resource
preparation failure—and post-grant `ConsumptionUnavailable` raw-query/
content/mapper result are distinct from `LifecycleDenied`: #158
historical/current-file maps it to exact `503 local_monitor_ui_unavailable`, the
generic Session route to exact `503 session_store_unavailable`, and raw-local
replay to its existing store-unavailable result except that replay policy limits
and busy contention retain their exact existing tokens; it is never lifecycle 410.
Every raw HTTP access/operation consumer—not only #158—fully buffers under its
handle and wins the store-backed raw terminal seal before response start. Raw
trace/detail/page, analysis-run, generic Session, historical, and current-file
retain their existing successful bytes; loss/busy aborts without a response.
For 2+ members, one composite handle wins one CAS/transaction/clock sample,
acquires every member publication scope in the Retention publication-lock
order, and proves every exact live tuple while those scopes are held;
seal/completion and release are all-or-none, and any member loss/busy discards
the whole entity. Returned values and owner-visible processing remain in
semantic frontier order.
Raw-local replay preserves four modes. `PreviewAsync` uses
`TryCompleteWithoutRaw`; HTTP preview terminal loss/busy aborts, while direct CLI
retains its exact safe DTO/error/exit. Handle-derived existing results for Local
Monitor GET `/replays/{replayId}` and POST `/replays` use the same completion:
pre-admission denied/busy keeps exact 503 tokens, post-admission loss/busy aborts.
Every other post-grant branch that will not reach its named seal—archive/
credential/bounds/inspection failure, transient reservation refusal, CLI
`output_exists`, parent/temp/create/write/flush `publish_failed`, or staged
`publish_validation_failed`—zeroes raw/staging and must win same-handle
`TryCompleteWithoutRaw` before its fixed safe result. HTTP terminal loss/busy
aborts; direct CLI loss/busy replaces the pending result with the unchanged
failure DTO plus `snapshot_read_denied|snapshot_store_busy`. Pre-capture control/
output-name failure needs no terminal.
Local Monitor `CreateAsync` first acquires an invisible exact transient-store
reservation, then calls `TrySealRawReplayTransientPublication`; its sealed point
atomically commits the guaranteed reservation with the existing memory-only
archive/preview buffers, metadata, and ten-minute authority—no file/path. CLI `CreateAndPublishAsync`
stages only a hidden same-directory file and calls
`TrySealRawReplayFilePublication`; the single-use sealed ticket permits exact
non-overwrite `File.Move`. Exact order remains commit-control ->
`output_name_invalid` -> capture/provider -> complete in-memory archive result ->
full-path/`output_exists` -> parent/temp/create/write/flush `publish_failed` ->
staged `publish_validation_failed` -> Retention seal -> move
`publish_failed|success`. Both seals share
the raw terminal CAS/transaction/clock/scope core, release DB/Monitor locks before
map/file I/O, and release once after publication/discard. Existing Skill
Projection, Local Repository, analysis, migration, and diagnostic publication
fences must likewise prove the exact grant; disposal lifetime alone is not
authority.
General operation renewal likewise uses checked `renewal_at + two minutes`;
overflow is `not_renewed`, rolls single/composite persisted changes back all-or-
none, publishes nothing, and leaves each grant usable only to its old expiry.
Renewal prebuilds/arms a dormant higher expiry-notification generation before
commit; its callback can record due but cannot touch the handle. After commit an
infallible publication-scope CAS activates/publishes it and disposes the old
timer. An old callback is stale; construction/arm/commit failure preserves the
prior expiry/generation/notification; an already-due activation loses and exact-
releases the whole single/composite handle. Composite preparation/activation is
all-or-none. The #158 current-file operation handle is the explicit
nonrenewing, never-rescheduled exception.

| Alternative | Decision |
|---|---|
| A. Apply `now<expires_at` to pinned and expiring rows | Reject: it contradicts accepted Retention pin semantics. |
| B. Null or extend `expires_at` when pinned | Reject: it rewrites historical evidence and adds mutation semantics. |
| C. State-sensitive admission plus immutable bounded grant | **Choose:** preserve expiry bytes; only expiring rows age out; do not revoke an admitted lease by a current-row recheck. |

Strongest counterexample: an operation lease can commit one tick before an
expiring item's boundary. Cleanup then changes denial/state/revision at the
boundary. Requiring current-row equality during consumption revokes the exact
lease the raw-store contract promised would remain usable and can race a
physical delete into an active read.

## Group 3 — actual SDK 1.0.4 payload and rebuilt bytes

### Current contract

The payload schema is payload-only and has exactly the nine public SDK 1.0.4
properties: required `name`, `path`, `content`; optional `allowedTools`,
`description`, `pluginName`, `pluginVersion`, `source`, `trigger`. There is no
`model`. Unknown `model` is classified by the existing deterministic payload
fault rule as `malformed/unknown_property`; it is not accepted or silently
dropped. `trigger` and `source` are locally closed even though the SDK wrapper
types can carry unknown strings. Runtime reflection and serializer defaults are
not schema authorities.

The exact 980-byte schema, 65-byte sidecar, 431-byte complete immutable r0001
registry, and their hashes are fixed above. The receipt golden's field 16 is
the raw decoded schema digest. No old 1,031-byte/`9eb451...c568`,
`ac871...bf8`, 1,579-byte, provisional registry, provisional receipt, or outer-
envelope hash remains valid.

| Alternative | Decision |
|---|---|
| A. Keep `model` as a local superset | Reject: it invents producer capability under the accepted tuple. |
| B. Remove it and rehash every dependent artifact | **Choose:** exact nine-property SDK surface, no compatibility alias. |
| C. Reflect the installed SDK at runtime | Reject: deployment state would redefine checked-in product bytes. |

Strongest counterexample: `SkillInvokedData.Model` does not exist, so a mapper
using it cannot compile; accepting a wire `model` anyway would certify evidence
the pinned producer cannot emit through this adapter.

## Group 4 — exact Session/Run binding, stored selection, and replay

### Current contract

Receipt lookup by exact `(source_adapter,source_event_id)` is always first.
Only a receipt miss enters one mutation `BEGIN IMMEDIATE` transaction and resolves the
Session by BINARY-exact `('copilot-sdk',native_session_id)` with a `LIMIT 2`
cardinality check:

- zero: create one UUIDv7 Session, exact native binding kind `native`, observed
  time `occurred_at`, `last_seen_at=occurred_at`, status `active`, completeness
  `partial` for the initial Skill-only fact, null repository/workspace/start/end,
  and transaction-consistent created/updated time; its raw-retention projection
  is `expiring` for the new Retention-owned content item;
- one: accept only an unchanged binding kind `native`, `explicit_resume`, or
  `explicit_handoff`; reuse it without replacing its kind, identity, or
  `observed_at`/enrichment, update Session last-seen by max, and recompute
  status/completeness through the Session 14 authority. `trace_context`, any
  malformed kind, or an orphaned owner graph returns exact
  `503 local_monitor_ui_unavailable`, zero writes;
- more than one: exact `503 local_monitor_ui_unavailable`, zero writes.

A binding under another surface counts as zero; a Session may legally have
other/mixed bindings, which are neither selected nor enumerated. The snapshot
privately persists the exact selected `native_session_id`. The DDL enforces
text, 1..256 SQLite characters, no U+0000, and 1..1,024 UTF-8 storage bytes;
the writer and full validator independently prove strict UTF-8, 1..256 Unicode
scalars, no U+0000, and at most 1,024 bytes. The full graph requires exactly
the selected `session_native_ids(session_id,'copilot-sdk',native_session_id)`
row with unchanged binding kind in the closed accepted set `native`,
`explicit_resume`, or `explicit_handoff`. The snapshot need not duplicate the
kind: its selected native ID plus Session ID selects the exact binding. Replay
and backup apply the same closed-kind check and never rewrite legal
resume/handoff evidence into `native`.

For `run_native_id=null`, create no Run and keep Event `run_id=null`. For a
nonnull exact ID, query BINARY-exact
`(session_id,'copilot-sdk',native_run_id)` only within the selected Session,
with `LIMIT 2`: zero creates one UUIDv7 Run with status `unknown` and null
parent/trace/model/timing/token facts; one reuses the exact row without erasing
enrichment; more than one is the same fail-closed 503. Event `run_id` always
records this outer identity when nonnull. An available snapshot links the same
Run; a classified-fault snapshot retains its mandated null derived Run/trace/
span fields while the parent Event still retains valid outer identity. Local
Event parent/status/match-kind and Session-14 terminal outcome/version remain
null for this source.

Every newly admitted v2 candidate writes the parent
`session_events.content_state` as exact `available`, including snapshot states
`malformed`, `missing`, `binary`, and `oversized`: this field says the canonical
raw Event content document exists, not that a current-valid Skill claim exists.
`not_captured`, `redacted`, and `unsupported` are never first-write values for
this route. The referenced Event is immutable under the child triggers, so
existing Retention never updates this Event field. The live graph requires
`content_state='available'`, one exact `session_event_content` row, and its exact
readable Retention item. An owner-valid expired/read-denied/cleanup transition
may retain the raw row exactly as the Retention lifecycle requires; component
and backup validation accept that exact nonreadable graph, but equal ingest
replay is not authorized to select the document and returns exact
`503 local_monitor_ui_unavailable` until deletion completes. After existing
Retention state/tombstone authority deletes that raw row, the Event remains
exact `available`; replay/backup instead require the absent raw row, exact
deleted Retention item, and tombstone as one closed graph. Any combination
outside those exact readable, owner-valid transitional, or deleted graphs is
corruption and uses the same sanitized 503 on replay.

On a receipt miss, after current-registry admission and transaction ownership
but before the first insert, read the injected clock exactly once and format one
`write_at` as the 33-byte UTC value `yyyy-MM-ddTHH:mm:ss.fffffff+00:00`. Checked
addition produces one exact `expires_at = write_at + 90 days`; overflow fails
closed before writes. No participant samples another server time. A new Session
uses `created_at=updated_at=write_at`; a reused Session preserves `created_at`
and applies Session 14's `updated_at=MAX(previous_updated_at,write_at)` merge.
Its `last_seen_at` remains the max of event `occurred_at`, not wall-clock time.
A newly created native binding uses `observed_at=occurred_at`; a reused accepted
binding preserves its prior observation time.

The Event-content `captured_at`, Retention-item `captured_at`, snapshot
`captured_at`, snapshot `created_at`, and receipt `created_at` are all exact
`write_at`; Event-content and Retention-item `expires_at` are both the one exact
`expires_at`. An available claim's #154-owned `created_at` is exact `write_at`;
nonavailable classifications create no claim or claim time. The existing SDK
claim arm has no current-pointer timestamp to invent. Full validation and backup
prove those equalities, `Session.created_at <= write_at <= Session.updated_at`,
and the independent event-time rules. A different-fingerprint replay samples no
clock. An identical replay samples one nonpersisted `validation_at` only inside
the validation transaction below to evaluate live `row_readable`; it changes no
time or row and preserves every first-write value.

For a new source key, an existing `session_events` row under exact
`(source_adapter,source_event_id)` is conflict and is never adopted. Otherwise
generate one local UUIDv7 `event_id` and one local UUIDv7 `snapshot_id`; the
receipt uniquely selects that snapshot, which uniquely selects the Session/
Event and optional claim/content/Run graph. These server IDs, transaction times,
owner token, and claim ID are first-write results excluded from request
identity. Replay never regenerates or substitutes them.

`request_fingerprint_sha256` is lowercase SHA-256 over the exact binary frame:

```text
UTF8("skill-invocation-snapshot-receipt") || 00 || UTF8("v1") || 00
|| U16BE(29)
|| fields in ascending field ID

field = U16BE(field_id) || kind u8 || U32BE(payload_length) || payload

kind 00 NULL     = zero length
kind 01 UTF8     = strict UTF-8 bytes, no normalization
kind 02 BOOL     = length 1, byte 00 or 01
kind 03 UINT64   = length 8, unsigned big-endian
kind 04 UTC_TIME = length 33, canonical ASCII yyyy-MM-ddTHH:mm:ss.fffffff+00:00
kind 05 SHA256   = length 32, raw decoded digest
```

Nullable values use kind `00`, never an empty surrogate. The closed ordered
field inventory is:

| ID | Value | Kind |
|---:|---|---|
| 1 | `source_adapter` | UTF8 |
| 2 | `source_event_id` | UTF8 |
| 3 | `source_surface` | UTF8 |
| 4 | `native_session_id` | UTF8 |
| 5 | `run_native_id` | NULL/UTF8 |
| 6 | `source_parent_event_id` | NULL/UTF8 |
| 7 | `source_ephemeral` | BOOL |
| 8 | producer `trace_id` | NULL/UTF8 |
| 9 | producer `span_id` | NULL/UTF8 |
| 10 | `occurred_at` | UTC_TIME |
| 11 | literal `skill.invoked` | UTF8 |
| 12 | `source_application_version` | UTF8 |
| 13 | `adapter_version` | UTF8 |
| 14 | `normalization_version` | UTF8 |
| 15 | `payload_schema` | UTF8 |
| 16 | `schema_fingerprint` | SHA256 |
| 17 | `payload_sha256` | SHA256 |
| 18 | `payload_bytes` | UINT64 |
| 19 | classified snapshot state | UTF8 |
| 20 | classified snapshot reason | UTF8 |
| 21 | name | NULL/UTF8 |
| 22 | source | NULL/UTF8 |
| 23 | trigger | NULL/UTF8 |
| 24 | body SHA-256 | NULL/SHA256 |
| 25 | body UTF-8 bytes | NULL/UINT64 |
| 26 | definition-path SHA-256 | NULL/SHA256 |
| 27 | definition-path UTF-8 bytes | NULL/UINT64 |
| 28 | literal `application/json` | UTF8 |
| 29 | `content_document_sha256` | SHA256 |

The checked-in golden uses adapter `copilot-sdk-stream`, source Event
`123e4567-e89b-42d3-a456-426614174000`, surface `copilot-sdk`, native Session
`session-A`, fields 5/6/8/9 null, ephemeral false, occurrence
`2026-08-09T00:00:00.0000000+00:00`, CLI `1.0.65`, the exact adapter/
normalizer/schema literals above, corrected schema digest `8fac48d8...a5c`,
payload digest byte `22` repeated 32 and length 42, `available/none`,
`review/project/user-invoked`, body digest byte `33` repeated 32 and length 7,
path digest byte `44` repeated 32 and length 12, and content-document digest
byte `55` repeated 32. It decodes to exactly 726 bytes and the request
fingerprint fixed in the artifact table.

On a receipt hit, recompute the 29-field fingerprint from the request. A
different fingerprint returns exact `409 idempotency_conflict`, zero writes.
An identical fingerprint never enters either zero-create arm. It opens one
validation-only `BEGIN IMMEDIATE` through the Retention-owned public replay
validator, rechecks the exact receipt/fingerprint, and
thereby serializes its consistent SQLite snapshot against Retention cleanup
without creating a Retention lease or any row. For a live raw graph, it samples
`validation_at`, proves exact `row_readable(validation_at)`, selects the
canonical document only inside the validator, and fully reclassifies/digest-
checks it. For a deleted graph it selects no raw bytes and instead proves exact
deleted item, absent raw row, tombstone, and all surviving immutable digests/
links. It then validates
exactly one stored native selection with an accepted unchanged binding kind,
the Event-linked optional Run and its exact natural-key cardinality, the
snapshot/content/Retention/claim/receipt graph, and classification, then derives
the canonical 204 before consulting the current registry and rolls back the
read/lock transaction with no data change. Immediately after that rollback and
before finalizing the response, the request's same callback-generation
capability must win `TrySealReplaySuccess`; loss to runtime invalidation returns
the fixed sanitized stage-1 503 (or aborts if an impossible already-started
response is observed), while a win may publish only that indivisible empty 204.
Raw document bytes/decoded values
never leave the validator, are never returned/cached/logged, and are released
before the transaction ends. Busy is exact `503 persistence_busy`; any
row-readability, tombstone, reclassification, or graph contradiction is exact
`503 local_monitor_ui_unavailable`. A
pre-existing v1/unsupported Event without this receipt is conflict. There is no
adoption, ID regeneration, compatibility read, or backfill.

The receipt recheck after a true miss has already opened the mutation
`BEGIN IMMEDIATE` is the same semantic validator with different transaction
ownership. Before any insert, if that recheck finds a receipt, #158 calls only
the Retention-owned transaction-aware internal replay arm on the already-held
connection/transaction. That arm rechecks the exact receipt/fingerprint in that
snapshot; a different fingerprint returns 409 without sampling a clock, while
an equal fingerprint samples `validation_at` exactly once and applies the same
readable/transitional/deleted/corrupt graph rules above. It opens no connection
or nested transaction, creates no Retention lease/row, selects no raw on the
transitional/deleted arms, emits no raw value, and neither commits nor rolls
back independently. The enclosing mutation owner always rolls its still-zero-
write transaction back after the derived 204, 409, or 503; an equal readable/
deleted arm then uses the same `TrySealReplaySuccess` fence before the 204, while
different/error arms never seal success. Its reserved lock
therefore serializes cleanup without a rollback/reopen gap; current registry
capture is discarded and no registry-generation lease is acquired.

| Alternative | Decision |
|---|---|
| A. Enumerate all native bindings until one reconstructs the receipt | Reject: mixed bindings are unbounded and offer no immutable cardinality proof. |
| B. Persist the exact selected native Session ID privately | **Choose:** direct bounded O(snapshot) graph validation; the receipt already fingerprints it. |
| C. Add a third child selection table | Reject: redundant object, FK graph, backup carrier, and failure surface. |

Strongest counterexample: a legally resumed Session may have exactly one
`('copilot-sdk',native_session_id)` row with `explicit_resume`. Native-only
validation either rejects a valid frozen-Session graph or mutates its evidence.
The closed accepted set preserves that graph; persisted selected ID and exact
>1 Run rejection still remove all enumeration choices.

## Group 5 — bundled CLI, application, SDK, and adapter identity

### D086 current producer contract

The only producer is a raw-default Local Monitor raw-analysis session created,
run, completed, and disposed by one exclusively owned `CopilotClient`. External
Copilot CLI/VS Code sessions remain unavailable and unobserved. Do not list,
resume, attach to, or read history from foreign sessions; in particular,
`ResumeSessionAsync` is configuration-mutating and cannot certify ownership or
the resumed runtime identity.

Versioned T0b precedes r0002 and all producer startup code. On one same signed-
in bundled client it proves exact Version/ProtocolVersion status, matching
SessionStart version, pre-creation callback registration on both sessions,
prompt-free probe inventory, execution `DisabledSkills`, retained-root-only
execution inventory/invocation, and exact task completion. Deterministic T0b
alone resolves the exact admitted enabled/user-invocable retained-skill command
through the SDK commands API, invokes it with
`executionSession.Rpc.Commands.InvokeAsync`, requires a prompt-producing result,
sends that exact returned prompt with `AgentMode.Autopilot`, and observes an
exact matching typed retained `SkillInvoked` followed by an exact typed task-
complete event. The candidate tuples are CLI
`1.0.65` and `1.0.75`, SDK/package `1.0.4/1.0.4.0`, protocol `3`, adapter
`copilot-sdk-dotnet-1.0.4+cao-skill-v2.1`, and normalizer
`github-copilot-sdk.skill-invoked.normalize.v2`; r0002 admits only complete
exact tuples T0b actually certifies. Failure leaves r0001 unchanged and stops
r0002, startup implementation, integration, and release. r0001 is historical
authority, never a fallback.

### D087 current content authority

For the current owned producer, typed SDK `SkillInvokedData.Content` is a
required, well-formed UTF-16 auxiliary-file inventory, not the Skill definition
snapshot. Certified 1.0.65 and 1.0.75 exhibit this contract; the single-file
synthetic case is exactly two LF bytes. Its arbitrary well-formed value is not
a mismatch and is never persisted, logged, returned, or retained.

After existing session, identity, and description checks, the callback validates
that required SDK field, freshly re-proves the retained native target, and
requires exact equality with the frozen proof. Normalizer v2 then writes exact
callback-time `currentProof.Content` into the existing `payload.content`; all
other payload fields remain typed SDK event values in their existing order.
There is no transform heuristic, auxiliary enumeration parser, fallback,
second field, later read, or reserialization. Missing or malformed upstream
Content is `InvocationContent`; native proof failure or drift is
`InvocationNativeReproof`; preparation and buffer failures retain their
existing closed reasons.

One admitted analysis candidate freezes the certified identity and owns the
exact client plus retained directory scope while remaining invisible to
current-file readers. With explicit `--skill-discovery-directory` roots it sets
`EnableSkills=true`, `EnableConfigDiscovery=false`, and
`SkipCustomInstructions=true`, supplies no plugin/instruction directories, and
uses exact retained Skill directories. These explicit roots are the sole Skill
provenance. The same certified client first creates and disposes a prompt-free
inventory-probe Session with `OnEvent` registered before creation. That probe
retains only the existing exact source-qualified custom raw-analysis tool
entries and does not require `builtin:skill` or `builtin:task_complete`. It
rejects
name collisions and missing/unverifiable paths, then freezes all non-retained
Skill names into `SessionConfig.DisabledSkills` for a distinct owned execution
Session, whose `OnEvent` is also registered before creation. Before any prompt,
execution inventory must contain no enabled non-retained Skill and no drift or
inability to disable. Before prompt, every retained inventory path passes the
existing native retained-root opener and lease proof; each later invocation
path is re-proved when invoked, not trusted from SDK strings. Only
the execution Session can produce callbacks/import. With retained roots, its
tool allowlist is exactly the same source-qualified custom raw-analysis entries
plus `builtin:skill` and `builtin:task_complete`. Wildcards, every other built-in,
MCP tools, plugins, and ambient instruction/config discovery remain unavailable;
a retained Skill's `allowed-tools` metadata cannot widen the exact allowlist.
Production sends the ordinary requested prompt with `AgentMode.Autopilot`, does
not force an arbitrary retained Skill invocation, and treats only the exact
typed task-complete event as terminal. With no roots, Skills are disabled, no generation is retained, and
the current-file service/POST is absent (outer `404`).

The callback installed before session creation accepts under one lock exactly
one matching SessionStart, 0..64 SkillInvoked callbacks with assigned ordinals,
then exact same-session task completion. Callback time prepares each complete
one-event v2 UTF-8 body from the frozen identity and retains only those opaque
bytes. The aggregate cap is exactly 8,388,608 complete body bytes. Any malformed,
out-of-order, mismatched, 65th, oversized, post-terminal, cancellation, root,
identity, or lease failure poisons the candidate.

Before completion it sends and persists nothing. The **owned-session
post-completion buffer/import** is synchronous, process-memory-only, and non-
durable. Zero invocations perform no v2 or v1 writes. With one or more
invocations, after completion it sequentially obtains same-candidate
capabilities and fresh body-bound tokens and sends exact prepared v2 bytes
without reserialization or retry. Only after all v2 events succeed does it
enqueue and await one-event v1 SessionStart followed by one-event v1 task-complete. A
failure stops at once, preserves only the valid committed prefix, releases each
capability exactly once, fails the analysis, destroys the failed candidate, and
keeps the prior current generation. There is no durable queue or importer
receipt, startup recovery, or automatic retry.

After complete import and SDK session disposal, the exact candidate atomically
becomes current; publication order defines the latest successful generation.
Zero invocations write no snapshot, receipt, or Session events, while a
successful roots-configured analysis may publish a current-file generation.
Roots with no published generation retain the existing exact `503
skill_current_file_discovery_unavailable` result after earlier gates. Replacement, failure,
refusal, lease loss, and shutdown reject new capabilities, cancel unsealed work,
drain capabilities, dispose client, then dispose retained scope, exactly once.

### D083 r0001 historical producer contract (superseded by D086)

The following text records the frozen r0001 topology and artifact identity. It
does not authorize the current producer or release policy.

The only r0001 producer composition is:

```text
GitHub.Copilot.SDK package/assembly = 1.0.4 / 1.0.4.0
application-bundled Copilot CLI    = exact same-client status "1.0.65"
same-client SDK protocol           = exact integer 3
adapter_version                    = copilot-sdk-dotnet-1.0.4+cao-skill-v2.1
normalization_version              = github-copilot-sdk.skill-invoked.normalize.v1
```

Launch only the application-co-located SDK bundle, headless and non-updating.
The #158 composition forbids an explicit runtime path, external URI,
`COPILOT_CLI_PATH`, PATH lookup, and build/runtime substitution. Before event
forwarding or discovery, call `GetStatusAsync` on that same `CopilotClient`
connection. The response and its documented members must be nonnull; freeze
exact `Version="1.0.65"` and integer `ProtocolVersion=3` together in the
connection-generation context. SDK 1.0.4's public minimum/protocol literal is
also 3; accepting a higher compatible protocol is not certification for r0001.
Any later status observation in that generation must repeat both values, and an
observed `SessionStartData.CopilotVersion` must equal the frozen Version. A new/
reconnected client is a new generation and repeats admission before use.

The adapter publishes each admitted same-client connection as one immutable
`CopilotRuntimeGenerationV1` behind an atomic current pointer. It contains the
exact client object, frozen Version/ProtocolVersion, opaque generation identity,
and a cancelable non-mutating operation capability. Current-file acquires one
after #154 authorization and holds it through `DiscoverAsync`, full result
validation, native walk/read/re-proofs, fresh serialization, and response
completion. No generation identity is persisted or emitted.

The sole SDK event transport remains the one v2 HTTP route; there is no direct
callback-to-writer path. `SkillRuntimeCapabilityBridgeV1` is its sole bounded
process-local admission bridge. At the SDK callback, the adapter first
atomically acquires the capability for that callback's exact owning runtime
generation; a stale callback performs no complete body serialization and never
reads the current pointer. Only under that capability does it cancellation-
aware serialize/hash the exact normalized body with an 8,388,609-byte stopping
buffer. Invalidation or serializer failure at any point discards every partial
byte and releases the capability with no token/send. The complete body must be
at most 8,388,608 bytes before token generation/registration or loopback send;
observing byte 8,388,609 releases the capability and fails with the same
sanitized producer unavailability, with no pending entry or write.
It generates one cryptographically random 32-byte token and encodes it as exact
unpadded base64url: 43 ASCII characters in `[A-Za-z0-9_-]`. Before loopback
send, it registers one pending entry containing only token, exact body length/
digest, monotonic creation/expiry, and that same capability, then sends the
token as exactly one physical `X-CAO-Skill-Runtime-Capability` header.

`SkillRuntimeBridgeHttpTransportV1` is the sole sender. Host composition gives
it one exact already-bound numeric loopback `http` address and port from the
actual Kestrel listener (`127.0.0.1` or `[::1]`); it accepts no user-supplied
base URI, DNS name, PATH/config/environment override, or alternate target. Its
dedicated `SocketsHttpHandler` has `UseProxy=false`,
`AllowAutoRedirect=false`, `UseCookies=false`, null credentials/default proxy
credentials, no preauthentication, no cookie/auth/default-header forwarding,
`ActivityHeadersPropagator=null`, no `DefaultRequestHeaders` or ambient
instrumentation/header enricher, and no retry/resilience/automatic-resend
handler. Thus `traceparent`, `tracestate`, `baggage`, and `Request-Id` are absent.
The request uses exact HTTP/
1.1 with `RequestVersionExact`, one POST, and only the fixed version,
capability, content media, and body fields. A redirect, authentication/proxy
challenge, any other non-204 response, connection ambiguity, or transport error
is sanitized producer unavailability; no redirected/resend request occurs and
the response body/detail is not logged or returned. Ambiguous semantic retry
uses only the fresh-callback/fresh-token rule below.

The pending registry holds at most 64 entries after purging expired entries;
entry lifetime is exactly 30 seconds from the injected monotonic clock, with
validity defined only as `now < expires_at` (`now == expires_at` is expired).
Clock overflow, RNG failure, token collision, capacity exhaustion, send failure,
or expiry fails the callback with sanitized unavailability and no write. The
callback acquires only the capability for its callback-owning exact connection
generation; it never rereads or borrows a newer current pointer. A token
is atomically consumed at v2 stage 1 exactly once and transfers its capability
to that request before body read. Missing, malformed, duplicate/combined,
unknown, expired, canceled, or already-consumed token is the same stage-1
`503 local_monitor_ui_unavailable`, zero body read/writes. After the bounded body
read, byte length and SHA-256 must equal the pending entry before parsing; a
mismatch is gate-1 `400 invalid_request`, never a token retry. Wrong method and
required service/max-body-feature failure occur before capability-header parsing;
the route neither consumes nor removes an entry on those arms. Producer send
completion or expiry cleanup removes/releases that still-unconsumed entry.
After atomic token consumption, every media/size/parse/replay/write/abort exit
releases the transferred capability exactly once. Token,
body digest, pending count, generation identity, and expiry are never logged,
persisted, backed up, restored, fingerprinted, returned, or measured.

The route passes the transferred opaque capability only into #119's immutable
typed handoff; #158 accepts no handoff without it and has one writer/admission
path. Arbitrary loopback v2 callers are unsupported and cannot borrow whatever
runtime generation happens to be current. A retry after an ambiguous HTTP
result requires a fresh token issued only from a callback that owns an admitted
generation: the same old generation if it is still admitted, or a genuinely
replayed callback owned by the new generation. An old callback never reacquires
the current pointer. The semantic request body/fingerprint remains identical,
so the normal receipt rule alone decides 204/409. There is no second transport, direct typed writer,
cookie/query token, persisted correlation, or previous-generation fallback.

Null, missing, empty, changed, or mismatched status, SessionStart mismatch, or
reconnect/loss atomically closes old-generation admission, marks it invalid,
cancels every active unsealed capability and removes every pending bridge
token; a capability whose terminal seal already won may finish only that sealed
commit/send. It does
not wait to publish the external runtime fact. No new callback, token, handoff,
v2 request, or current-file request can acquire the old generation. Every
unsealed in-flight operation checks linked cancellation before/after loopback
send, route transfer, #119 parse/handoff, receipt read, transaction begin, first
insert, SDK discovery/result enumeration, each native read/re-proof, response
buffer construction, and its terminal fence; invalidation makes it rollback/
discard with no write or raw response and it cannot transfer generations.

After bridge-token transfer, any v2 runtime-generation invalidation or lost
terminal seal before response publication is exact
`503 local_monitor_ui_unavailable`, with the normal owned JSON media/no-store
headers, empty persistence effect, and no raw user-data entity; this includes mutation and
equal-replay arms. After current-file acquires its runtime capability, any such
invalidation before `TrySealResponse` wins discards its candidate and is exact
`503 skill_current_file_discovery_unavailable` only after any admitted
Retention grant returns `completed_without_raw`; Retention `lost|busy` instead
uses the fixed no-response abort. Every arm releases all runtime/#154/
Retention/root capabilities and exposes no SDK/native/path detail. Caller
abort, or an invariant-violating already-started response, aborts the transport
without attempting a substitute entity; neither case is reclassified as a
successful response.

`CurrentSkillRequestTerminationV1` keeps caller abort, runtime-generation
invalidation, and Retention grant loss/expiry as three tagged causes even though
their work-cancellation tokens may be linked. Root-generation/normal host
shutdown is not a cancellation cause: it closes admission and drains the exact
already-admitted root/runtime leases before disposal. Runtime
invalidation cancels SDK/native work and the runtime capability only; it never
cancels/releases the Retention handle. While the caller remains connected and
the already-admitted root lease remains held (including normal shutdown drain),
the route ignores the canceled work token
for the synchronous Retention `TryCompleteWithoutRaw()` terminal call and may
therefore publish the fixed runtime 503 if Retention completes. The fixed cause
priority when multiple facts are observable is caller abort -> Retention
`lost|busy` -> runtime invalidation. The first two abort without a substitute
response; only the final arm can send the runtime 503.
No generic `OperationCanceledException`, shared-token propagation, exception
message, or callback arrival order determines the public result.

Terminal publication has one atomic total order with invalidation. Immediately
before any post-transfer v2 noncommit status/header/entity,
`TrySealV2NonCommitResponse` either seals the fully buffered candidate or loses
to invalidation and substitutes exact `503 local_monitor_ui_unavailable`. It
covers body-binding/media/size/outer-parse faults, receipt/Event conflicts,
registry/storage/busy/corruption outcomes, and every other response after a
valid token transferred but before a commit/equal-replay success arm; pre-token
method/service/max-feature/capability failures need no generation seal.

Immediately before SQLite `COMMIT`, `TrySealCommit` either seals that still-
valid exact generation or loses to invalidation and rolls back. A won commit
seal authorizes exactly one commit attempt and its fixed terminal result: commit
success derives 204; SQLite busy/locked rolls back and returns exact
`503 persistence_busy`; another fail-closed commit error rolls back and returns
exact `503 local_monitor_ui_unavailable`. Invalidation after the seal cannot
change that commit result, and no second response seal/retry is permitted.
After an equal public or
in-transaction receipt replay has completed its required rollback and graph
validation, but before setting/finalizing status or headers,
`TrySealReplaySuccess` either seals the still-valid exact generation for the
empty derived 204 or loses and returns the sanitized 503; it cannot convert a
conflict/error arm into success. Immediately before the first current-file
response byte, `TrySealResponse` either seals the fully buffered response or
loses and discards it. A seal that wins occurred while the generation was still
admitted and may finish only that indivisible commit/send, or be abandoned and
released with no output when a later independent authorization fails;
it is not general drain and admits no later work. Mismatch/reconnect/loss never
waits for an unsealed operation. Host shutdown with an otherwise unchanged
admitted runtime is the sole drain case: it closes new admission without
invalidating/canceling existing capabilities, waits for each admitted operation
to reach its ordinary terminal completion/abort, then releases capabilities and
disposes the client. Forced process termination relies on OS cleanup and claims
no response result.
The host lifetime coordinator atomically marks normal shutdown and closes both
root-request and runtime-capability admission. A current-file request already
holding its root request lease is a drain participant, but that lease does not
permit it to acquire a runtime capability after shutdown closure. If it reaches
the runtime-acquisition stage without a previously acquired runtime capability,
`shutdown_closed` is distinct from runtime mismatch/unavailability: it invokes
Retention `TryCompleteWithoutRaw()` solely to release an admitted grant, releases
#154/root resources, and starts no HTTP status/header/entity regardless of
`completed_without_raw|lost|busy`. No runtime capability is fabricated. A
runtime capability acquired before the atomic shutdown closure drains under its
ordinary terminal rules.
There is no mid-operation reacquire or status fallback.

Once the closure/publication fence completes, the mismatch disables producer forwarding,
the #119 handoff sink, #158 writer execution, and every new v2 write. The
raw-default v2 path remains registered, but its exact-path POST fails at
stage 1 with `503 local_monitor_ui_unavailable` before body read and with zero
writes until an admitted generation exists; it never labels the mismatched
runtime as 1.0.65 or falls back to another executable.

Runtime admission does not own stored-snapshot availability. In raw-default
composition, metadata and historical-content routes remain registered and use
only their fixed database/#154 point-diagnostic/Retention contracts. When a
nonempty configured root set and certified platform make current-file POST a
registered surface, an absent/mismatched runtime generation does not unmap it:
after the fixed request/snapshot/Retention/#154 authorization stages and before
`DiscoverAsync`, it forms exact
`503 skill_current_file_discovery_unavailable`, then calls the already admitted
grant's store-backed `TryCompleteWithoutRaw()`. Only
`completed_without_raw` sends that candidate; Retention `lost|busy` aborts with
no response. This pre-runtime arm releases every #154/Retention/root capability,
acquires/seals no runtime capability, and exposes no SDK discovery/native read
or path/runtime detail. Zero roots and unsupported platform continue to
govern route absence exactly as Gate 8 says. A later same-client mismatch has
these same per-surface effects; it does not suppress or relabel historical
data. Protocol 3 is an admission-only runtime fact: it is not added to the
normalized wire, receipt, snapshot, database, backup, response, log, or metric;
`adapter_version` already binds SDK 1.0.4.

The inspected baseline's current application-co-located debug executable at
`src/CopilotAgentObservability.LocalMonitor/bin/Debug/net10.0/runtimes/win-x64/native/copilot.exe`
is 128,372,512 bytes, hashes as
`c1d86ddd95da68c826455f8239580166f7bf598502f83684b936403b510cd2b6`, and
self-reports first line `GitHub Copilot CLI 1.0.75.` (followed by the update
advisory). The evaluated project property still
pins `CopilotCliVersion=1.0.65`, with no `CopilotCliBinaryPath` override; SDK
assembly identity is `1.0.4.0` and its public minimum protocol literal is 3.
This mismatch is current release evidence that the local output is **not
admitted** by r0001. It authorizes no producer, handoff, writer, new v2 write,
replay label, or `source_application_version`; it is not authority to suppress
stored metadata/historical content. On a valid configured/certified root
surface it yields only the exact current-file discovery-unavailable result
above, not route absence. Release activation requires a fresh application bundle
that contains the exact package-selected 1.0.65 executable, forbids external
or updating substitution, and whose same-client live status proves exact
Version `1.0.65` and ProtocolVersion 3 before discovery or forwarding.

The separately installed/global CLI also reports `1.0.75` (with file metadata
observed around `1.0.74`). It is likewise not admitted by r0001. A later CLI or
SDK requires a new complete immutable registry revision plus producer/live
certification. Machine-specific executable hashes are certification evidence,
not cross-platform product tuple members.

| Alternative | Decision |
|---|---|
| A. Hardcode 1.0.65 from NuGet props only | Reject: overrides/external connections can run another CLI. |
| B. Label events from PATH or file-version metadata | Reject: observed PATH/self-report/file metadata already disagree. |
| C. Enforce the bundle and bind same-client runtime status | **Choose:** actual running producer and tuple must agree. |

Strongest counterexample: SDK 1.0.4 can otherwise connect through an explicit
path, environment override, substituted build binary, or URI while package
props still say 1.0.65, falsely admitting a different producer.

## Group 6 — generic raw-route denial before materialization

### Current contract

`GET /sessions/{sessionId}/events/{eventId}/content`
sets `Cache-Control: no-store` before validation. After origin and UUID checks,
it calls one Session-owned generic-route read operation,
`ISessionStore.ReadGenericRouteContentAsync`. That operation opens one
owner-coordinated `BEGIN IMMEDIATE` and, on the same connection and
transaction, first runs a scalar metadata-only query for the exact Event
identity/type. It does not call the current separately transactional
`ReadContentAsync` path and does not nest a Retention transaction. Every
existing `session_events.type='skill.invoked'`, regardless of adapter or the
presence/health of a snapshot row, is indistinguishable from missing. It returns:

```text
status       = 404
Content-Type = application/json
Cache-Control= no-store
entity       = {"error":"session_event_content_not_found"}
```

The entity is exact UTF-8, 43 bytes, no BOM/newline/charset/Allow, with the
decoded hash fixed above. The query happens before a Retention lease, content
column selection, base64 parsing, or response materialization. Exact Event
missing or `skill.invoked` ends that transaction and returns the same 43-byte
404. Only an existing non-Skill Event may continue inside the same SQLite
snapshot: the Session owner invokes the existing Retention admission and
content-selector logic through a transaction-aware internal arm on that same
connection/transaction, so no concurrent type change can commit between the
policy decision, lease insertion, and content selection. The returned access
capability/lease preserves all normal non-Skill status/entity bytes. The route
fully buffers an exact non-Skill 200 only while holding that handle and calls
the same store-backed `TrySealRawResponse` strictly before response start. Only
`sealed` sends the unchanged entity; `lost|busy`, expiry, cleanup, or caller
abort discards it and aborts with zero status/header/entity. There is no current-
row reread. The metadata-policy result is total: SQLite busy/locked returns the
existing exact
`503 {"error":"session_store_busy"}` (30 UTF-8 bytes, SHA-256
`e95ded880eae255e934e1c7b71a51751a395ee6cdaea0d3d36a716154ec09f58`);
and malformed schema/type/storage, multiple identity rows, or any non-busy
policy failure returns new exact
`503 {"error":"session_store_unavailable"}` (37 UTF-8 bytes, SHA-256
`10ea144d09df95f7af00604f9fb41a72c0cf0a2c10dc140eff017d250e27d383`).
Both 503 entities have the route's existing exact `application/json` media,
no-store, no BOM/LF/charset/Allow, and no raw detail. Missing/Skill/busy/
unavailable branches never call `ReadContentAsync`, acquire Retention, select
`content_json`, decode base64, or materialize content. Other frozen v1
200/404/410 bytes remain exact.

The transaction-aware arm is not a second raw authority: it is reachable only
from this exact generic-route operation after the non-Skill result and shares
the existing Retention predicate, capability, lease, selector, commit,
consumption, seal, and release implementation. A busy/failure before commit
rolls back every uncommitted non-Skill lease and discards content. No
implementation may split the type check
from Retention admission, rely on an application-level Event immutability
convention, or add `type <> 'skill.invoked'` only to a later selector after a
lease has already been created.

| Alternative | Decision |
|---|---|
| A. Metadata-only deny before content access | **Choose:** no carrier crosses the generic authority. |
| B. Read/materialize and replace the response afterward | Reject: the raw payload already crossed the boundary. |
| C. Block the storage read primitive globally | Reject: the dedicated Retention-authorized Skill reader needs it. |

Strongest counterexample: checking only snapshot existence leaks a v2 payload
when the component is partially corrupt or missing its snapshot row. Checking
adapter plus type leaks a contradictory Skill Event. Type-only denial remains
fail-closed under both conditions.

## Group 7 — SDK `Skills` null normalization

### Current contract

SDK 1.0.4 publicly normalizes missing `skills`, JSON `skills:null`, and
`skills:[]` to the same nonnull empty `IList<ServerSkill>` through its lazy
getter. The product cannot promise to distinguish those raw inputs. A nonnull
aggregate whose public `Skills` enumerates as empty is a successful empty
inventory and, for an otherwise eligible call, returns exact
`409 skill_current_file_not_discovered`.

A top-level null `ServerSkillList` is observable despite the annotated return
type and produces the exact
`503 {"error":"skill_current_file_discovery_unavailable"}` nonraw candidate.
SDK/RPC exception, getter/enumeration failure, null item, or unreadable
documented DTO member/aggregate is a nonraw discovery-failure candidate and
uses the sanitized 503 only after Retention completion-without-raw and the
runtime response seal both succeed. Retention grant cancellation/loss/busy uses
the fixed zero-response transport-abort branch; caller abort emits no
replacement response. A readable DTO whose matching path/root relation violates the closed
authorization rules follows the separately fixed `unsafe` outcome. The product
does not bypass the SDK to inspect raw RPC JSON.

| Alternative | Decision |
|---|---|
| A. Accept the SDK's public normalization | **Choose:** empty public inventory means not discovered; observable failures mean unavailable. |
| B. Add a raw-RPC decoder to recover null/absent | Reject: it creates a second transport and schema authority. |
| C. Specify `Skills==null` as a public DTO branch | Reject: the getter makes it unobservable and untestable. |

Strongest counterexample: `result:null` can produce a top-level null and must
not be collapsed with an empty result, while `{"skills":null}` cannot be
distinguished from `{"skills":[]}` and must not be advertised as detectable.

## Group 8 — request-memory discovery, fresh serialization, platform gates

### Current contract

`discovery_revision` is a request-memory-only lowercase SHA-256 of:

```text
UTF8("skill-discovery-roots\0v1\0")
|| platform u8                         # 1 Windows, 2 Linux
|| entry_count u16be                   # 0..48
|| sorted entries

entry = root_kind u8                   # 1 ProjectPath, 2 SkillDirectory
     || native_id_length u16be         # exactly 24
     || native_identity_bytes
     || sdk_path_key_length u32be      # 1..4096
     || sdk_root_path_key_utf8

Windows identity = volume_serial u64be || FILE_ID_128
Linux identity   = mount_id u64be || dev_major u32be ||
                   dev_minor u32be || inode u64be
```

Entries sort by `(root_kind,native_identity_bytes,sdk_root_path_key_utf8)`.
Within one role/native identity, choose the ordinally smallest validated SDK
path key; the same native directory in both roles remains two entries and is
passed once in each SDK array. The process root-set revision, root-string copy,
root native IDs, handles, discovery inventory/results, and absolute paths are
absent from snapshot, receipt/fingerprint, product database, backup/restore,
state, responses, logs, metrics, and evidence. Configured values may exist only
in the explicit OS process-argv/Task Scheduler action carrier disclosed in Gate
8. Restore uses only the destination process's current root-set object.

Zero accepted explicit roots means the current-file service and POST are not
registered and `DiscoverAsync` is never called. Any invalid/unopenable/
unsupported configured root aborts host startup with the Gate-8 sanitized
configuration/platform reason; no valid subset is retained and there is no
route-only degradation of an explicitly supplied configuration. With a
nonempty valid root set but no
root role eligible for the historical source, return not-discovered without an
SDK call. Otherwise call `DiscoverAsync` exactly once with both complete
canonical arrays, explicit `excludeHostSkills:false`, and the tagged caller/
runtime/operation-grant work-cancellation composition fixed in Group 5. Scan the materialized result once without an
invented post-return product budget and keep at most two distinct eligible
descriptors; never select first/by order.

Windows and Linux register independently. Windows requires retained-root
handle-relative every-segment `NtCreateFile` proof and only certified local
NTFS/ReFS roots. Linux requires kernel 5.8+ `openat2` with
`RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS|RESOLVE_NO_MAGICLINKS|RESOLVE_NO_XDEV`, retained fd/`statx` proof,
and only certified local ext4/xfs/btrfs roots. Each returned `statx` mask must
contain `STATX_MNT_ID|STATX_INO|STATX_TYPE|STATX_MODE` for the retained root and
every relevant retained directory/file fd, plus
`STATX_SIZE|STATX_MTIME|STATX_CTIME` wherever those fields classify or enter a
stability/read comparison. A missing required bit is sanitized native failure,
never zero/default evidence. The exact mount ID must remain stable through the read;
Linux 5.6/5.7 cannot satisfy this contract. Failure of one platform gate does
not block the other or historical routes. macOS/BSD/other systems register no
current-file POST. There is no absolute-path/.NET path fallback.

Metadata, historical-content, and current-file success JSON are freshly written
from the just-validated semantic facts in the already accepted fixed property
order, with explicit nulls. No serializer default/ignore policy or cached
response bytes is authoritative. Metadata re-queries #154's point-in-time
projection diagnostic; historical content revalidates graph/classification/
digests under the Retention lease. Current-file instead holds the exact runtime-
generation operation capability, distinct #154-owned current-authorization
capability, and root-set/Retention operation leases across discovery, stable native read, and response
serialization; its body/digests/comparison/read time come from that request's
stable handle read. Ingest
success is always derived exact `204`, `Cache-Control:no-store`, empty body, no
`Content-Type`/`Allow`. The DDL has no response columns.

```text
metadata token = local-skill-invocation-snapshot.metadata.v1
order = schema_version,snapshot_id,session_id,claim_id,event_id,name,source,
        trigger,invoked_at,run_id,trace_id,span_id,projection_validity,
        snapshot_state,snapshot_reason,body_sha256,body_utf8_bytes,
        definition_path_sha256,definition_path_utf8_bytes,captured_at,
        source_application_version,adapter_version,payload_schema

historical token = local-skill-invocation-snapshot.content.v1
order = schema_version,snapshot_id,content_kind,body,definition_path,
        body_sha256,definition_path_sha256,captured_at

current token = local-skill-current-file-read.response.v1
order = schema_version,snapshot_id,content_kind,comparison,
        historical_body_sha256,current_body_sha256,current_body_utf8_bytes,
        body,read_at
```

| Alternative | Decision |
|---|---|
| A. Persist discovery inventory/revision or response bytes | Reject: stale authority, sensitive path state, restore ambiguity, and semantic drift. |
| B. Request-local root proof + fresh semantic serialization + independent platform gates | **Choose:** smallest state and fail-closed platform scope. |
| C. Register universally and fall back to path APIs/runtime 503 | Reject: it advertises uncertified raw capability and cannot prove beneath/no-follow identity. |

Strongest counterexample: restoring a valid snapshot on a host with different
roots must not recreate source authority from backup, and a cached current-file
response would return old bytes/digest/read time after file replacement.

Issue #152 remains excluded. This work neither detects unknown OTel attribute
keys nor infers a source/version from paths, processes, time, repositories, or
unknown telemetry. The closed SDK payload's `unknown_property` classification
does not solve general OTel unknown-key drift.

## Normative closure of the original eight canonical gates

This section closes the eight provisional Product Owner gate groups in
#119/#157/#158. Earlier reports are evidence only;
no value below depends on their proposal status. Where a repair group above is
more detailed, it is part of this same D083 contract and not a compatibility
alternative.

### Gate 1 — exact normalized v2 wire and SDK mapping

The request has exactly one `X-CAO-Session-Event-Version` field whose complete
ASCII value is exactly `2`, exactly one physical
`X-CAO-Skill-Runtime-Capability` field whose complete value is the consumed
43-character bridge token from Group 5, exactly one accepted JSON
`Content-Type`, and one strict
UTF-8 JSON object followed only by JSON whitespace. Request object property
order is nonsemantic, but the SDK adapter emits this fixed order and the parser
requires every property exactly once and nonnull unless marked nullable:

```text
schema_version
source_adapter
source_surface
native_session_id
source_application_version
adapter_version
normalization_version
payload_schema
schema_fingerprint
events
```

| Property | Exact historical-r0001 / current-r0002 rule |
|---|---|
| `schema_version` | JSON integer `2` |
| `source_adapter` | string `copilot-sdk-stream` |
| `source_surface` | string `copilot-sdk` |
| `native_session_id` | exact `CopilotSession.sessionId`; 1..256 Unicode scalars, no U+0000, at most 1,024 strict UTF-8 bytes |
| `source_application_version` | exact Version from the same-client certified tuple (r0001 historical `1.0.65`; current r0002 exact admitted tuple) |
| `adapter_version` | string `copilot-sdk-dotnet-1.0.4+cao-skill-v2.1` |
| `normalization_version` | r0001 historical string `github-copilot-sdk.skill-invoked.normalize.v1`; current r0002 string `github-copilot-sdk.skill-invoked.normalize.v2` |
| `payload_schema` | string `github-copilot-sdk.skill-invoked.v1` |
| `schema_fingerprint` | string `8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c` |
| `events` | array containing exactly one event |

The event adapter emits this fixed order; all nine properties are required:

```text
source_event_id
source_parent_event_id
type
occurred_at
run_native_id
source_ephemeral
trace_id
span_id
payload
```

`source_event_id` is SDK `id` parsed as UUIDv4 and emitted canonical lowercase
`D`; `source_parent_event_id` is SDK `parentId`, JSON null or the same UUIDv4
form, and is only source-chain provenance. Local
`session_events.parent_event_id` remains null. `type` is `skill.invoked`.
`occurred_at` is the SDK timestamp normalized to the exact 33-byte UTC form
`yyyy-MM-ddTHH:mm:ss.fffffff+00:00`. `run_native_id` is JSON null when SDK
`agentId` is absent, otherwise its exact 1..256-scalar, no-U+0000, at-most-1,024
UTF-8-byte value. `source_ephemeral` is true only for SDK semantic true; absent
or false upstream maps to JSON false. Under historical r0001 and current r0002,
the required `trace_id` and `span_id` properties are both JSON null because SDK
1.0.4 exposes no exact correlation source. No Session/Run/name/path/time
inference supplies them.

For current owned normalizer v2, `payload` is a newly written object whose
`content` is exact callback-time certified native `currentProof.Content` after
equality with the frozen proof. Typed upstream `SkillInvokedData.Content` is
only required, well-formed transient auxiliary inventory and is never persisted
or transformed. Every other payload field comes from typed `SkillInvokedData`,
not a raw SDK serializer buffer, preserving producer order
`name,path,content`, then each present optional member in
`allowedTools,description,pluginName,pluginVersion,source,trigger` order.
Absent optionals are omitted; JSON null is never synthesized. Strings and
allowed-tool order are preserved without trim, case fold, normalization,
filtering, path rewriting, or replacement repair. Historical r0001 retains its
frozen normalizer-v1 meaning. There is no transform, fallback, additional
field, later read, or `model`. The
receiver accepts any property order but performs one bounded raw
`Utf8JsonReader` pass with ordinal name sets and exact `JsonReaderOptions`:
`AllowTrailingCommas=false`, `CommentHandling=JsonCommentHandling.Disallow`, and
`MaxDepth=64`, before constructing the immutable #119 handoff. Any reader
structural/depth failure anywhere in the complete request—including inside an
unknown payload member's value—is outer gate-2 `400 invalid_request` before
payload fault selection. Depth 64 is accepted when every other rule passes;
depth 65, a comment, or a trailing comma is 400. Only after one structurally
complete pass do duplicate/unknown payload facts enter gate 6's total order.
Duplicate/unknown envelope or event properties, invalid outer UTF-8/JSON/type/
nullability/value/count, wrong provenance, or trailing value also returns 400;
other payload-local faults reach gate 6 and the atomic #158 transaction.
The runtime-capability header is the sole exception to that ordinary header
mapping: missing/malformed/duplicate/combined/unknown/expired/consumed/canceled
forms are the indistinguishable stage-1 sanitized 503 before body read so an
arbitrary loopback caller cannot use the parser as a capability oracle. A
consumed token whose expected complete body length/SHA-256 differs is outer 400
before JSON parse. Token, pending body digest, and runtime generation are
transport admission only and are excluded from all 29 fingerprint fields and
every normalized/persisted value.

Before constructing any complete JSON body, and while the callback-owning
runtime capability is live, the producer scans every producer-sourced .NET
string for well-formed UTF-16 scalar pairing: Session ID, Event ID, parent ID,
agent/Run ID, and every required/present optional payload string, including
each `allowedTools` element and the underlying `source`/`trigger` string. Any
lone high or low surrogate is sanitized producer unavailability: discard every
partial buffer, release the capability, and create no token/send/handoff/write.
It is never passed to `Utf8JsonWriter`, whose .NET 10 replacement behavior is
not an evidence-preserving encoder. Fixed adapter/version/time/UUID literals
are produced only after their typed parsing/exact admission. The Gate-6
`body_unicode_invalid`/`path_unicode_invalid` arms remain receiver raw-token/
internal-admission defenses for a synthetically authorized request; the certified
SDK adapter cannot emit them.

The normalized SDK producer and every gate-7 fresh success writer use .NET 10
`Utf8JsonWriter` directly with exact `JsonWriterOptions`:
`Indented=false`, `SkipValidation=false`, and
`Encoder=JavaScriptEncoder.Default`. They write each fixed ASCII property name
and typed value explicitly; `JsonSerializer`, ambient web defaults,
`WriteRawValue`, and unsafe-relaxed/custom encoders are forbidden. Prevalidated
input strings remain unchanged semantic scalar sequences, while the writer alone produces
the canonical token spelling. Thus solidus remains literal `/`, backslash is
`\\`, non-ASCII BMP scalars use uppercase `\uXXXX`, astral scalars use uppercase
UTF-16 surrogate-pair escapes, HTML-sensitive `<`, `>`, `&`, `'`, and `"` use
their uppercase `\uXXXX` forms, literal plus uses `\u002B`, and controls use the
writer's exact short escapes or uppercase `\uXXXX`. Consequently the semantic
33-byte UTC timestamp remains the specified value while its JSON token escapes
the plus byte. The checked-in `json-writer-v1.golden.json` token vectors are
normative and prevent runtime/encoder drift.

Choice A, the closed normalized wire above, is selected. A verbatim SDK event
would omit the Local Session and compatibility authority; a flattened wire
would merge producer and persistence namespaces. Strongest counterexample: a
serializer/reflection path can silently add a future SDK property or last-win a
duplicate, while the exact typed/raw parser must either classify the payload
fault or reject the outer wire deterministically.

### Gate 2 — status, media, method, error bytes, and precedence

Every owned JSON error entity is exact compact UTF-8
`{"error":"<token>"}`, without BOM, indentation, or final LF. It has exact
`Content-Type: application/json; charset=utf-8` and
`Cache-Control: no-store`. Every non-405 owned response, including every error,
200, and 204, has no `Allow` header. Exact `204` has no entity or `Content-Type`
and has `Cache-Control: no-store`. A matching route with a wrong method returns
`405`, exact `method_not_allowed`, and the only permitted `Allow`: exact `POST`
for v2/current-file or exact `GET` for either Skill GET. This includes HEAD and
OPTIONS; the framework does not synthesize GET for HEAD or another method
result. OPTIONS and every non-HEAD wrong method transmit the exact 30-byte UTF-8
`{"error":"method_not_allowed"}` entity. HEAD returns the same owned 405,
`Content-Type: application/json; charset=utf-8`, no-store, exact `Allow`, and
`Content-Length: 30`, but transmits zero entity bytes as HTTP requires. Method
selection precedes parsing, origin, CSRF, lookup, and state. A nonmatching path
keeps the outer 404, including HEAD.

Both POST routes use the same closed request-media parser. Accept only when
there is exactly one physical `Content-Type` field,
`Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse` succeeds for its
single complete field value, and the parsed media type is ordinal-ignore-case
`application/json`. The parameter count is zero or exactly one. When present,
the sole parameter name is ordinal-ignore-case `charset` and its value is the
unquoted token ordinal-ignore-case `utf-8`. Parser-legal optional whitespace is
accepted. A second physical field, comma list, quoted value (including
`"utf-8"`), duplicate or extension parameter, empty/alternate charset, or any
parse failure is exact `415 unsupported_media_type`. No pre-parser concatenation,
split, trim, or framework charset normalization changes this decision.

The v2 ingest route owns an exact 8,388,608-byte request-body boundary,
independent of the general trace-ingest `--max-request-body-bytes` value. At
the beginning of an exact-path POST, before media parsing or any body read, it
sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` to exactly 8,388,608,
including when the configured general limit is lower, following the existing
route-owned boundary pattern. A missing/read-only feature, a setter failure, or
an observed value other than 8,388,608 is stage-1
`503 local_monitor_ui_unavailable`, with no body read and zero writes. A
declared length above the exact boundary or a bounded streaming read that
observes byte 8,388,609 is stage-3 `413 request_too_large`; bytes 0 through
8,388,608 proceed. Framework/Kestrel default 413 bytes are never the route
contract. The current-file POST's own 128-byte reader applies the same
route-owned-feature rule with exact value 128 and the same stage-1 failure,
then returns its existing exact 413 at byte 129. These endpoint-specific limits
do not raise or reinterpret the general trace-ingest limit.

For `POST /api/session-ingest/v2/events`, stop at the first stage:

After the receipt miss, orders 15 and 16 classify the first terminal result returned
by `SessionSkillInvocationParticipant.InsertOrVerify`; they do not prescribe that
participant's internal check order, which Group 4 fixes as Session resolution, then
Run resolution, then Event-conflict detection.

| Order | Condition | Exact result | Writes |
|---:|---|---|---|
| 0 | wrong method on exact path | `405 method_not_allowed`, `Allow: POST` | zero |
| 1 | required raw-default route/store/max-body feature or exact matching runtime-generation capability unavailable, excluding current compatibility-registry disposition | `503 local_monitor_ui_unavailable` | zero |
| 2 | media failure | `415 unsupported_media_type` | zero |
| 3 | declared or streamed entity exceeds 8,388,608 bytes | `413 request_too_large` | zero |
| 4 | any remaining gate-1 outer/header/provenance fault after the capability-header exception at order 1 | `400 invalid_request` | zero |
| 5 | payload classification | continue with one classified candidate | deferred |
| 6 | receipt read is busy | `503 persistence_busy` | zero |
| 7 | receipt hit, different fingerprint | `409 idempotency_conflict` | zero |
| 8 | receipt hit, identical fingerprint, validation-only `BEGIN IMMEDIATE` is busy | `503 persistence_busy` | zero |
| 9 | receipt hit, identical fingerprint, owner-valid nonreadable raw-retained lifecycle or invalid stored graph | `503 local_monitor_ui_unavailable` | zero |
| 10 | receipt hit, identical fingerprint, validation-only graph valid, rollback complete, and `TrySealReplaySuccess` wins | derived `204`; seal loss is sanitized stage-1 `503` | zero |
| 11 | receipt miss; exact current complete registry is unavailable, or the exact tuple is revoked/unaccepted | `503 local_monitor_ui_unavailable` | zero |
| 12 | mutation `BEGIN IMMEDIATE` or eventual commit is busy | `503 persistence_busy` | rollback |
| 13 | in-transaction receipt recheck now hits | Retention-owned internal replay arm on the same connection/transaction: different -> order 7; equal transitional/corrupt -> order 9; equal readable/deleted -> enclosing rollback then order 10's replay-success seal; no nested BEGIN; current registry irrelevant | zero/rollback |
| 14 | still a miss; acquire current-generation read lease and prove captured pointer/revision/tuple; if changed, recapture once; second mismatch or lease/generation failure | continue under held lease, or `503 local_monitor_ui_unavailable` | zero/rollback |
| 15 | pre-existing `(source_adapter,source_event_id)` Event without receipt, or same first-write source/Event/receipt identity wins a uniqueness race | `409 idempotency_conflict` | rollback |
| 16 | another storage/graph invariant fails | `503 local_monitor_ui_unavailable` | rollback |
| 17 | `TrySealCommit` wins, the new complete transaction commits while the registry-generation lease remains held, then releases it | derived `204` | all authorities |

Every response candidate after a valid bridge token transfers the runtime
capability must reach exactly one runtime terminal arm. Orders 2-9, 11-16, and
the different-fingerprint side of 13 use
`TrySealV2NonCommitResponse`; seal loss substitutes exact
`503 local_monitor_ui_unavailable`. Equal valid replay uses
`TrySealReplaySuccess`. First-write uses `TrySealCommit`, whose won seal owns
the one SQLite commit result: success 204, busy/locked rollback plus exact
`503 persistence_busy`, or other commit failure rollback plus exact
`503 local_monitor_ui_unavailable`, with no second seal or retry. Orders 0 and
the pre-token service/max-feature/capability failures at 1 have no transferred
generation and need no terminal seal.

Sanitized-only does not register this route, so stage 1 is not a sanitized-only
oracle. Stage 1 never treats registry revocation as route unavailability. Wrong
wire tuple literals are gate-1 outer faults at order 4. Payload faults never
become transport 400 after an admitted outer wire. Receipt lookup and its
complete graph check always precede current-registry loading; consequently an
identical durable retry remains 204 after registry removal or corruption. Only
a true receipt miss captures one immutable complete current registry, requires
the exact parsed request/facts producer tuple to be accepted by that captured
current registry (r0002 here), and re-proves that captured object at the
transaction fence under the provider generation read lease held through
commit. One pre-lease recapture is allowed; no second churn or stale commit is.
There is no previous-registry fallback. `persistence_busy`
is exclusively a SQLite read/write lock or commit-busy result; corruption,
missing authority, or non-busy storage failure never uses it.
The stage-1 runtime capability is acquired before body read, must be the exact
generation transferred from the atomically consumed bridge entry, is then
passed into the internal #119 typed handoff, and remains held through every
zero-write replay outcome or first-write terminal commit fence; every exit
releases it. Within stage 1, required route/store/bridge services and the exact
max-body feature are admitted first, then the capability header is parsed and
consumed; neither branch reads a body. After the bounded body read, the bridge
length/digest equality precedes JSON parsing at outer gate 1.

For the three Skill raw routes, set no-store after route/method selection, then
enforce the existing exact `MonitorHost.IsCrossSiteRequest` predicate. Missing
`Origin` and missing `Sec-Fetch-Site` are accepted; `Sec-Fetch-Site` equal to
`same-origin` or `none` ignoring ASCII case is accepted; any other nonempty
`Sec-Fetch-Site` is rejected. A nonempty `Origin` is accepted only when it
equals the request's own `scheme://host:port` under the existing ordinal-ignore-
case comparison; a foreign, malformed, duplicate/combined, or otherwise
nonmatching value is rejected. Every rejected cross-site GET or POST is exact
`403 csrf_rejected`, with the owned JSON headers and no `Allow`; it is not a
distinct token. Current-file additionally requires the existing exact monitor
CSRF header: exactly one physical `x-monitor-csrf` field whose complete value is
ordinal-exact `local-monitor`. Missing, empty, case-changed, duplicate/combined,
or any other value is the same `403 csrf_rejected`
before media,
128-byte inclusive bound, and the closed request parse. Both GETs then parse
canonical UUIDs and do exact `(session_id,snapshot_id)` lookup; invalid UUID,
wrong Session, and missing snapshot are the same
`404 skill_snapshot_not_found`. Current-file order is method -> required
route/service plus exact max-body-feature admission -> one
`DiscoveryRootSetV1` request-generation-lease CAS -> origin/CSRF -> media ->
size -> request parse -> exact lookup -> historical state/Retention -> #154
current-authorization capability -> exact runtime-generation operation
capability -> discovery/filesystem. Thus a missing/read-
only/wrong max-body feature is the fixed stage-1 503 even when the same request is cross-site;
only after feature admission can origin/CSRF return 403. The exact state map is:

That within-stage arrow is normative. Required-service lookup and the max-body-
feature set/readback finish first; any failure is the fixed stage-1 503 and no
root lease is attempted. Only their success reaches the root CAS. If normal
shutdown has closed root admission before that CAS wins, the route performs the
fixed zero-response shutdown abort. A shutdown racing a service/max failure
cannot replace a failure already selected before the CAS; a successful service/
max arm that later loses the CAS cannot emit the 503.

| Status | Exact token/condition |
|---:|---|
| 400 | `invalid_request` |
| 403 | `csrf_rejected` |
| 404 | `skill_snapshot_not_found`; `skill_current_file_missing` |
| 409 | `idempotency_conflict`; `skill_projection_not_current`; `skill_current_file_not_discovered`; `skill_current_file_unsafe`; `skill_current_file_raced` |
| 410 | `skill_snapshot_expired` |
| 413 | `request_too_large` |
| 415 | `unsupported_media_type` |
| 422 | `skill_snapshot_content_unavailable`; `skill_current_file_oversized`; `skill_current_file_binary` |
| 503 | `persistence_busy`; `local_monitor_ui_unavailable`; `skill_current_file_discovery_unavailable` |

Retention outcomes are not collapsed. `LifecycleDenied` alone can produce the
owner-valid unreadable/expired 410. A pre-grant metadata-query/shape
contradiction, checked date-add overflow, or hidden resource-preparation failure
is `SelectorUnavailable` and exact
`503 local_monitor_ui_unavailable` for both #158 raw routes; no committed grant
exists and no terminal call occurs. A post-grant raw-query/content/fixed-mapper
failure is `ConsumptionUnavailable`: it holds a handle use ref while mapping,
zeroes/discards the buffer, retains the handle, and forms that same fixed 503
candidate. It may send only when `TryCompleteWithoutRaw()` returns
`completed_without_raw`; `lost|busy` aborts with no response. A hidden-handle publication or committed value-
publication loss is instead `LeaseLost|Busy` and takes the closed post-admission
no-response branch, never 410. Generic Session maps structural/mapper unavailable
to exact `503 {"error":"session_store_unavailable"}`; raw-local replay keeps its
existing store-unavailable result, its explicit policy-limit tokens, and
`replay_store_busy` contention mapping.

For both historical-content GET and current-file POST, the snapshot/Retention
cross-product is exact and precedes #154/discovery: structural/component/
parent/receipt/Retention contradiction is `503 local_monitor_ui_unavailable`;
SQLite busy/locked while proving it is `503 persistence_busy`; an owner-valid
canonically unreadable/deleted/expired row is `410 skill_snapshot_expired` for
every persisted state/reason; a currently `row_readable` `malformed`,
`missing`, `binary`, or `oversized` row is
`422 skill_snapshot_content_unavailable`; and only a currently readable
`available/none` row may continue. The proof is metadata-only until that final
available arm, so the 410/422 paths never select/decode the raw document.
State/reason remain visible only through metadata. Current-file then requires
the #154 current-authorization capability below before discovery; a `stale` or
`invalid` result is exact `409 skill_projection_not_current`, SQLite busy is
exact `503 persistence_busy`, and `unavailable` is exact
`503 local_monitor_ui_unavailable`. Only after that capability succeeds does
internal runtime acquisition `missing_or_mismatched` return exact
`503 skill_current_file_discovery_unavailable` before SDK work. The distinct
`normal_shutdown_closed` disposition is not this error and follows the fixed
cleanup/no-response drain rule below.
Historical-content applies its authorized document checks without inventing a
current-claim or live-runtime requirement.

After historical content admits its Retention access grant, or current-file
admits its Retention operation grant, the route starts no HTTP
response until the complete success/error candidate has been determined.
Retention itself owns the no-argument, store/lease-handle-backed
`TrySealRawResponse()` and `TryCompleteWithoutRaw()` operations. Each
synchronously opens the validation `BEGIN IMMEDIATE`, then the Monitor-backed
grant publication scope,
queries the exact live lease tuple/expiry, and samples the Retention clock once
inside that terminal decision; #158 supplies no timestamp and performs no
separate validate-then-seal check. Internal `lost|busy` releases every
runtime/#154/Retention/root capability, discards every buffer, and invokes the
server transport abort with no HTTP status, header, or entity. This is neither
the pre-admission 410 nor any 503/499 token. Caller abort has the same no-
substitute-response effect.

Current-file deliberately does not renew its Retention operation grant. The
route receives one fixed two-minute grant, makes no renewal call at the general
one-minute renewal deadline, and keeps the exact published admission expiry for
the request's entire lifetime. Retention owns the single expiry notification
linked into current-file work; it is not rescheduled and shares the handle's
one-shot terminal total order. A notification or terminal decision at exact
expiry or later that wins is `lost` and takes the no-response abort above.
Either `sealed` or `completed_without_raw` that wins strictly before expiry
atomically disposes the notification; a racing/later callback is a stale no-op,
cannot tag Retention loss, and cannot retract the authorized buffered raw send/
discard or safe nonraw result's later runtime seal/send. Other operation-lease
consumers retain the prerequisite's general renewal behavior.

Historical content sends raw success only after `TrySealRawResponse()`
returns `sealed`, and sends a safe nonraw result only after
`TryCompleteWithoutRaw()` returns `completed_without_raw`. For current-file,
#154 not-current/busy/unavailable and runtime acquisition
`missing_or_mismatched` are pre-runtime-capability outcomes: Retention
completion-without-raw alone authorizes their fixed safe response; `lost|busy`
aborts, and no runtime capability is fabricated or reacquired.
`normal_shutdown_closed` instead always performs the defined Retention cleanup
and no-response abort. Only a nonraw error determined after runtime-capability
acquisition uses two stages: Retention completes without raw, then the exact
runtime-generation `TrySealResponse` must win over the fully buffered safe error
before response start; runtime loss discards that candidate and returns the
exact discovery-unavailable 503. Current-file raw success uses the opposite safe order to avoid sealing Retention raw before runtime authorization:
runtime `TrySealResponse` wins first, then Retention raw sealing must return
`sealed`. If runtime loses, discard raw and attempt Retention completion-without-
raw; completion wins -> exact discovery-unavailable 503, while Retention lost/
busy -> no-response abort. If runtime wins but Retention raw sealing loses,
discard raw, abandon/release the runtime seal without a send, and abort with no
response. Only both raw seals winning submits the single already-buffered entity.

An SDK exception, top-level null/malformed discovery aggregate, null item, or
unreadable DTO enumeration/member produces the sanitized discovery-unavailable
503 candidate and follows the post-runtime two-stage rule. Caller abort emits
no substitute response.
Within a successful discovery phase, gate 8 owns the inner result precedence.

The generic Session content denial in Group 6 is a separate frozen-route
policy: exact 404 and exact 43-byte
`{"error":"session_event_content_not_found"}`, with no-store and its existing
`application/json` media value, before materialization.

Choice A, a closed staged registry, is selected. Reusing v1 validation/error
behavior would change frozen bytes and lacks payload-classification semantics;
exception-driven/ad-hoc errors leak implementation state. Strongest
counterexample: if receipt lookup happens after current registry validation, a
previously committed identical retry can change from 204 to rejection after a
registry revision, violating durable idempotency.

### Gate 3 — payload schema, fingerprint, and registry

The artifact table bytes are normative. `schema_fingerprint` is lowercase
SHA-256 of the exact 980 payload-schema file bytes, including its one final LF;
the 65-byte sidecar is that lowercase digest plus LF. The 431-byte r0001 is one
complete immutable registry with one exact accepted five-tuple. Load requires
exact schema/version/revision/property inventory/types, no duplicate or unknown
entry/member, unique tuple, exact lowercase digest, and byte-for-byte agreement
with the checked-in schema. Missing, malformed, future, partial, duplicate, or
unknown disposition makes the whole current registry unavailable; there is no
previous-revision fallback, range, SemVer family, runtime reflection, or
partial tuple match. A later revision may revoke or add only explicit complete
tuples and never rewrites stored evidence.

Registry history is the contiguous checked-in immutable sequence
`r0001..rNNNN`, with each file's internal revision equal to its filename and
the greatest revision the sole current complete registry. Later files are full
snapshots, not deltas. A `revoked` exact tuple in the current file is valid only
when at least one lower mechanically valid revision contains that exact tuple
as `accepted`; otherwise the history is unavailable. Missing/gapped/duplicate,
hash-invalid, schema-invalid, or internally mismatched history needed for a
diagnostic is `unavailable`. Reading lower revisions solely to prove the
historical accepted predecessor of a current explicit revoke is not admission
fallback: new writes consult only the greatest current complete file. Adding a
different tuple does not implicitly supersede or stale any old tuple; the old
exact tuple must remain accepted, be explicitly revoked, or be absent with the
`invalid` consequence below.

The registry provider publishes one immutable complete generation behind an
atomic current pointer and owns a non-mutating generation read lease. On a true
receipt miss, #158 captures a complete generation for preliminary admission.
At the in-transaction last-miss fence it must acquire and hold the provider's
current-generation read lease through every insert and the SQLite commit, prove
pointer/revision/object identity with the captured generation, and prove the
exact tuple accepted. Publication of a new registry generation waits for
outstanding read leases; therefore acceptance cannot be revoked between the
fence and commit. If the pointer changed before lease acquisition, #158 may
release and recapture exactly once; a second mismatch/churn, lease failure, or
unavailable generation is exact `503 local_monitor_ui_unavailable` and rolls
back. If the in-transaction receipt recheck hits, current registry is irrelevant
and no generation lease is acquired/required. The lease is process memory only:
it creates no registry/SQLite row and is released after commit/rollback.

Current-file authorization is a separate #154-owned use of that provider; it
does not reuse the ingest lease or the metadata point-in-time diagnostic. The
#154 read authority exposes one sanitized
`TryAcquireCurrentSdkClaimAuthorization` operation. It proves the complete
available snapshot/claim equality and exact producer tuple, captures the
greatest complete registry generation, acquires that exact generation's read
lease, and re-proves pointer/revision/object identity plus `accepted` disposition
before returning an opaque capability. If the pointer changed before lease
acquisition, #154 releases the candidate and may recapture/recheck exactly once.
A second mismatch/churn, lease failure, malformed/unavailable generation, or
claim/graph contradiction is `unavailable`; SQLite busy while proving the claim
is `busy`. A mechanically valid current generation in which the exact tuple is
explicitly revoked with valid history or absent returns `not_current`; it never
returns a capability. The current-file route maps `not_current` to exact
`409 skill_projection_not_current`, `busy` to exact `503 persistence_busy`, and
`unavailable` to exact `503 local_monitor_ui_unavailable`.

The opaque capability owns the registry-generation read lease and only the
sanitized exact current-claim facts needed by #158; it exposes no registry file,
history, path, or direct table reader. #158 must hold it without a gap from
before SDK discovery through every root proof, native walk/read/re-proof, fresh
response serialization, and completion, then release it on success, every
error, cancellation, or abort. Registry publication/revocation waits for all
such #154 capabilities, so a tuple cannot become stale while it authorizes new
file bytes. A publication already completed before acquisition is observed by
the bounded recheck and cannot fall back to the previous generation. Metadata
keeps the point-in-time `current|stale|invalid|unavailable` diagnostic, and
historical-content GET adds no current-generation capability.

The schema owns only the nine-property `payload`; it does not describe the SDK
event or normalized outer envelope. Choice A, one payload-only artifact and one
exact tuple, is selected. Renaming it to an outer-event artifact would leave
the accepted `payload_schema` meaningless; keeping both old/new artifacts or
reflecting runtime SDK types creates two authorities. Strongest counterexample:
the provisional full-event/ten-property digest can recompute perfectly while
certifying bytes that are not the value hashed as `payload_sha256` and a
`Model` property SDK 1.0.4 cannot emit.

### Gate 4 — equality receipt, selected identity, and replay

The 29-field binary grammar, inventory, kinds, corrected 726-byte golden, and
selected private `native_session_id` in Group 4 are normative. The digest is a
semantic request fingerprint: it binds every admitted producer identity,
provenance, exact raw payload-token digest/length, deterministic classification,
derived body/path facts, content kind, and canonical content-document digest.
It excludes server-generated UUIDs/times/ownership/claim values. Receipt rows
store source key, event/snapshot selection, request fingerprint, and creation
time only; the v1 DDL has no response status/header/entity columns. Success is
always freshly derived exact 204.

Receipt lookup is first. A hit never creates Session, Run, Event, lease, claim,
snapshot, or IDs and never consults current registry before equality. A
different fingerprint is exact 409/zero writes. An equal fingerprint must
use the separately landed Retention-owned public validation-only transaction
in Group 4. A receipt that races a preceding miss uses the same validator's
transaction-aware internal arm on the already-held mutation connection/
transaction, never a nested transaction or rollback/reopen. Both then validate
exactly one stored selected Session binding in the
accepted three-kind set, exact optional Run cardinality/link, and the complete
Event/content/Retention/claim/snapshot/receipt graph; corruption is sanitized
503, otherwise derive 204. It creates no Retention lease or data write and raw
bytes never leave the validator. A miss alone enters the mutation
`BEGIN IMMEDIATE` 0|1|>1 mapping and seven-authority insert described in Group
4; a racing receipt exits it by enclosing rollback with zero writes. No
pre-existing Event is adopted.

Choice B from Group 4—persist the exact selected native ID—is selected over
unbounded binding enumeration or another selection table. The binary semantic
receipt is selected over full-request hashing and stored-response replay.
Strongest counterexample: mixed bindings plus duplicate natural-key Runs let a
scan/first-match validator choose a different graph after row-order changes;
full-request hashing would also conflict on irrelevant outer whitespace while
stored response bytes would become a second serializer authority.

### Gate 5 — exact payload/content bytes and sole raw owner

`payload_sha256` is SHA-256 over the exact received UTF-8 byte slice of the
normalized `event.payload` JSON value token, including its object whitespace,
property order, escapes, and duplicate spelling. `payload_bytes` is the byte
length of that same slice. It is not the full request, upstream SDK `data`
buffer, decoded value graph, content document, body, or path.

The sole `session_event_content` raw carrier is exactly these bytes, with no BOM
or LF, in this property order:

```text
UTF8('{"schema_version":"session-event-content.skill-invoked.v1",'
     '"payload_utf8_base64":"')
|| strict RFC4648 base64 of the exact payload-token bytes
|| UTF8('"}')
```

There is no base64 whitespace or alternate alphabet. `content_kind` is exact
`application/json`; `content_document_sha256` is SHA-256 of the complete
canonical document bytes. No secret filter, body/path copy, second Retention
item, snapshot raw column, normalized JSON, or direct Skill/generic JSON HTTP
exposure of base64 exists. The separately consented #87 raw-local-replay
archive authority below is the sole deliberate HTTP archive carrier and is not
a Skill read-route exception.

Body/path digests and byte counts are over exact strict UTF-8 bytes after JSON
unescape. Do not strip BOM, normalize Unicode/line endings, trim, change path
case, or repair invalid scalars. Available rows have exact body/path facts;
nonavailable rows still retain the raw payload slice/document, payload digest/
length, Event, one Retention item, state/reason, snapshot, and receipt, but no
fabricated body/path/claim facts. Historical reads decode only an available
row, reclassify it, and prove every digest/length before fresh serialization.

Runtime backup carries the document only once through Session Event content;
snapshot backup carries metadata/links/receipt only. Validation proves strict
base64, decoded payload length/digest, complete reclassification, document
digest, Retention ownership, and available body/path facts. Raw absence is
valid only with the exact deleted Retention item and tombstone. Sanitized
export/import carries no namespace or empty marker.

Raw-local replay preserves its separate #87 explicit-consent authority. When
`include_session_content=true` selects the exact Session, its all-or-none
Retention operation lease may materialize this one canonical
`session_event_content.content_json` value just like other Session content. The
raw-replay `RawReplaySessionContent.ContentJson` value is the exact canonical
document string; the existing canonical archive JSON writer may escape that
string only as required by its enclosing record. For this source the enclosing
facts are exact `SourceAdapter=copilot-sdk-stream`,
`ContentKind=application/json`, `ContentState=available`,
`MatchKind=null`, the Event's exact application/adapter/schema-fingerprint/
normalization evidence, `SecretFilterState=not_applied_raw_capture`, and
`SecretFilterVersion=raw-replay-credential-scan.v1`. It must not claim
`session_secret_filter_applied`.

The existing raw-replay size preflight occurs before raw materialization or
lease insertion; a canonical document above the existing 8 MiB Session-content
limit is exact `entry_too_large`, never truncated. Only after all size/count/
aggregate preflights and one all-or-none Retention operation-grant admission may
the export provider enter one authorized raw-replay read transaction/snapshot.
Before selecting `session_event_content.content_json`, it validates every nonraw
Session/snapshot/Event/receipt/claim/Retention ownership, identity, link,
classification-state, and stored digest/length fact in that same snapshot. A
failure exits without selecting or materializing raw. Only then may it select
and materialize the canonical document under the still-usable exact grant and,
before staging/publication, prove the necessarily raw-dependent document
grammar/base64, decoded payload digest/length, content-document digest,
artifact fingerprint, reclassification, and credential-scan facts. No nested/
second snapshot or validation-after-publication arm exists. The existing 128 MiB archive limit
and error precedence remain unchanged. `--sanitized-only` rejects at the raw-
replay control boundary before Session content lookup or lease admission. The
archive adds no snapshot, receipt, claim, selected native Session ID,
configured root, or discovery fact.

`raw-replay-credential-scan.v1` gains one schema-specific input arm for this new
carrier; its pattern set, matching rules, timeout-means-match rule, public token,
and version literal do not change. After the bounds/grant/nonraw-graph/document proofs,
decode `payload_utf8_base64` once with the same strict bounded payload parser.
Run the existing v1 matcher over both (a) the exact decoded UTF-8 payload-token
text and (b) every decoded JSON property name and string value at every depth,
including required/optional/array/unknown-member strings, so JSON escapes cannot
hide a match. Any match or matcher timeout returns only existing
`credential_material_detected`; it publishes/stages nothing and emits/logs no
payload, string, path, match, or exception detail. This is not a secret filter
and does not make the archive safe.

Strict archive inspection repeats the bounds, document/schema, decoded-domain
credential, and source-evidence checks for every externally supplied canonical
Skill member before replay-preview publication, import, or durable staging.
Thus an archive not produced by this host cannot bypass the decoded scan.
Isolated replay preserves the exact Session-content record as raw source
evidence but never reconstructs or writes a live Session/Event,
`skill_invocation_snapshot`, receipt, #154 claim, Retention item, producer,
discovery service, or Skill route. It never treats an archived registry label
as current admission. Same source identity with different canonical document
bytes remains the existing `source_id_conflict`; identical retry remains the
existing idempotent result. Sanitized evidence export/import still carries no
such member or placeholder.

Choice A, the canonical lossless base64 document under the existing sole owner,
is selected. A decoded body/path document cannot preserve duplicate/unknown/
escape evidence; a second raw carrier adds another Retention/backup authority.
Strongest counterexample: two raw payload tokens can decode to the same body
and path but differ in duplicate member or `"a"` versus `"\u0061"` spelling;
reconstructing from decoded fields would make conflicting evidence identical.

### Gate 6 — total classification, bounds, nullability, and claim validity

The payload scanner records all observable payload faults before choosing one
winner by this total order, independent of property encounter order:

```text
malformed: duplicate_property > unknown_property > invalid_field_type
           > name_invalid > path_invalid
missing:   name_missing > body_missing > definition_path_missing
binary:    body_unicode_invalid > path_unicode_invalid
oversized: body_oversized > path_oversized
available: none
```

The state classes themselves are ordered as shown: any malformed reason wins
any missing/binary/oversized fact, then missing, then binary, then oversized.
A missing property differs from a present null/wrong type, which is
`invalid_field_type`. Optional null, wrong type, bound failure without a more
specific closed reason, and nonclosed `source`/`trigger` are also
`invalid_field_type`.

Name identity is its unchanged strict UTF-8 bytes after requiring 1..200
Unicode scalars, at most 800 bytes, and no unpaired surrogate, U+0000/control
scalar, or Unicode noncharacter. Do not normalize, case-fold, trim, or collapse
whitespace. Path classification requires a present nonempty string, at most
4,096 strict UTF-8 bytes, no U+0000/ASCII control, and no unpaired surrogate;
empty/control is `path_invalid`, unpaired is `path_unicode_invalid`, and over
the byte bound is `path_oversized`. It performs no absolute-path, OS,
containment, normalization, case, or filesystem test. Content allows 0 through
1,048,576 strict UTF-8 bytes; unpaired is `body_unicode_invalid`, over-bound is
`body_oversized`. Description allows 0..4,096 scalars/16,384 bytes;
`allowedTools` 0..64 strings, each 1..128 scalars/512 bytes; `pluginName` and
`pluginVersion` 0..256 bytes. Source/trigger use the exact closed schema tokens.
The unpaired-surrogate classifications are receiver/internal-admission defense,
not evidence expected from the historical r0001 or current r0002 SDK producer;
both pre-writer scans reject them without a transport attempt.

Every state persists immutable Event/provenance, timestamps, content item,
payload digest/length, document digest, state/reason, and snapshot/receipt.
Only `available/none` has nonnull claim/name and body/path facts; optional
source/trigger and outer Run/trace/span linkage are null only where the admitted
wire lacks them. Every nonavailable state has null claim/name/source/trigger,
snapshot Run/trace/span, body/path digests and lengths, even though the parent
Event retains valid outer Run/trace/span identity. It creates no partial #154
claim.

Metadata projection validity comes only from #154:

```text
current = structurally valid available snapshot/claim graph + the exact tuple
          is present as accepted in the greatest complete registry revision
stale   = structurally valid available snapshot/claim graph + the exact tuple
          is present as revoked in the greatest complete registry revision +
          at least one lower contiguous valid revision proves that same exact
          tuple accepted
invalid = every nonavailable snapshot without calling the SDK diagnostic; or a
          structurally valid available snapshot/claim graph whose exact tuple
          is absent from the greatest complete registry revision
unavailable = any physical/FK/equality/Retention/receipt/claim contradiction;
              current/history registry missing, gap, malformed, unknown,
              hash/schema/revision invalid; non-busy diagnostic failure;
              or a current revoked tuple without its valid prior acceptance
```

The snapshot reader never reconstructs these outcomes from direct registry,
generation, or claim-table SQL. A missing physical claim/FK parent, immutable
snapshot/Event/claim/receipt mismatch, malformed Retention graph, contradictory
#154 invariant, or unavailable/failing diagnostic is not the `invalid` value;
it is exact `503 local_monitor_ui_unavailable` with no partial metadata.
An exact SQLite busy/locked result while reading the snapshot, #154 diagnostic,
or registry history bypasses `unavailable` and is exact
`503 persistence_busy`; no other exception uses that token.
An available snapshot restored under a later valid current registry is thus
deterministic: current accepted -> `current`, current explicit revoke plus
proved earlier acceptance -> `stale`, current absence -> `invalid`. The
presence of a different/newer tuple never changes those rules.

Choice A, the total scanner and nullability matrix, is selected. First-encounter
selection is order-dependent; rejecting every payload fault before persistence
loses the accepted raw observation. Strongest counterexample: one payload can
have an early missing body and a later duplicate name; encounter-order parsers
choose different state/reason for byte permutations, breaking receipt replay.

### Gate 7 — literal success documents and fresh semantic serialization

Each of the three success documents is exact status 200 with
`Content-Type: application/json; charset=utf-8`, `Cache-Control: no-store`, and
no `Allow`. JSON is compact UTF-8, no BOM/LF/indentation, uses the exact gate-1
writer/encoder, and has fixed property order. Listed nullable values are emitted
as the literal JSON token `null`; nothing is omitted or reordered.

All scalar spellings are closed. `snapshot_id`, `session_id`, `event_id`, and
each nonnull `claim_id`/`run_id` are their validated canonical lowercase UUID
`D` strings; `trace_id`/`span_id` and every SHA-256 are lowercase hexadecimal.
`invoked_at`, every `captured_at`, and `read_at` are exact 33-byte UTC strings
`yyyy-MM-ddTHH:mm:ss.fffffff+00:00`. `read_at` is one clock sample after the
stable native read and all re-proofs but before writing the response, and is not
persisted. Every byte count is a nonnegative invariant base-10 JSON integer with
no sign, leading zero (except `0`), exponent, decimal point, or quoted form.
Schema/content/state/reason/validity/comparison/version values are the exact
closed strings named here or in their owning registries and pass through the
same explicit writer. No culture or ambient converter participates.

Metadata GET returns 200 with `schema_version` value
`local-skill-invocation-snapshot.metadata.v1` and this exact order:

```text
schema_version,snapshot_id,session_id,claim_id,event_id,name,source,trigger,
invoked_at,run_id,trace_id,span_id,projection_validity,snapshot_state,
snapshot_reason,body_sha256,body_utf8_bytes,definition_path_sha256,
definition_path_utf8_bytes,captured_at,source_application_version,
adapter_version,payload_schema
```

Its derived-state matrix is total:

| Persisted snapshot | Exact Retention projection | #154 diagnostic | `projection_validity` | `snapshot_state` | `snapshot_reason` |
|---|---|---|---|---|---|
| `available/none` | currently readable | `current`, `stale`, or explicit `invalid` | same diagnostic token | `available` | `none` |
| `malformed`, `missing`, `binary`, or `oversized` | currently readable | not applicable: no claim | `invalid` | exact persisted state | exact persisted reason |
| `available/none` | canonically unreadable/deleted | `current`, `stale`, or explicit `invalid` | same diagnostic token | `expired` | `none` |
| `malformed`, `missing`, `binary`, or `oversized` | canonically unreadable/deleted | not applicable: no claim | `invalid` | `expired` | exact persisted reason |
| no snapshot row (`not_captured`, including an OTel-only claim) | none | none | no document | no document | no document |
| any inconsistent component/parent/Retention/tombstone/#154 graph | inconsistent or unavailable | none | no document | no document | no document |

`currently readable` means the exact component links and the Retention owner's
current non-lease raw-availability projection satisfy `row_readable`, with the
raw document still in its required owner row; metadata does not select or open
that document. `canonically unreadable/deleted` means the same owner validates
either the at-or-after-expiry `expiring` boundary or an exact read-denied/
cleanup/deleted lifecycle, with raw-row presence matching that lifecycle and an
exact Retention tombstone whenever the raw row is absent. The latter derives
`expired` regardless of why the owner-valid lifecycle denied further raw reads;
it does not rewrite the persisted state or reason. Any other combination is the
exact `503 local_monitor_ui_unavailable` row. `not_captured` has no snapshot ID
or row, so an attempted snapshot URL is the ordinary exact
`404 skill_snapshot_not_found`. `projection_invalid` is not emitted as this
metadata document's `snapshot_state`; v1 represents that independent axis only
as `projection_validity=invalid`.

Every metadata property above is always emitted. These are always nonnull:
`schema_version`, `snapshot_id`, `session_id`, `event_id`, `invoked_at`,
`projection_validity`, `snapshot_state`, `snapshot_reason`, `captured_at`,
`source_application_version`, `adapter_version`, and `payload_schema`. An
`available` persisted row also always emits nonnull `claim_id`, `name`, both
body/path SHA-256 values, and both body/path byte counts. Its `source`,
`trigger`, and `run_id` independently preserve the admitted optional value or
literal null; historical r0001 and current r0002 `trace_id` and `span_id` are
always literal null. Every
nonavailable persisted row emits literal null for `claim_id`, `name`, `source`,
`trigger`, `run_id`, `trace_id`, `span_id`, both body/path digests, and both
body/path counts. Deriving `expired` or changing `current|stale|invalid` never
nulls or changes any safe stored field. In particular, expiry removes raw body/
path text, not the already stored claim/name/digest/count metadata.

The 17 `response_utf8` values in
`metadata-response-v1.golden.json` are normative literal fixtures: available
live and expired crossed with all three projection values, the independent
available optional-null branch, every live and expired fault-state row,
`not_captured` 404, and corrupt-graph 503. Each fixture fixes its byte length and
SHA-256. Alternative reasons within one fault state substitute only that exact
persisted reason token. These are test vectors for the fresh writer, never
runtime response storage or a replay cache.

Historical-content GET returns 200 only for a currently readable available
row, with `schema_version=local-skill-invocation-snapshot.content.v1`,
`content_kind=historical_snapshot`, and exact order:

```text
schema_version,snapshot_id,content_kind,body,definition_path,body_sha256,
definition_path_sha256,captured_at
```

Current-file POST accepts one JSON object containing exactly
`schema_version=local-skill-current-file-read.request.v1`, unknown/duplicate
properties forbidden, within 128 bytes. Success has
`schema_version=local-skill-current-file-read.response.v1`,
`content_kind=current_file`, `comparison=same|changed`, and exact order:

```text
schema_version,snapshot_id,content_kind,comparison,historical_body_sha256,
current_body_sha256,current_body_utf8_bytes,body,read_at
```

`comparison` is byte identity, never digest identity. After the available
historical body has been strictly decoded/revalidated and the current handle
read has passed every stability/UTF-8 proof, emit `same` if and only if the two
exact strict-UTF-8 byte sequences have equal length and every byte is equal;
otherwise emit `changed`. `historical_body_sha256` is the re-proved stored
digest of those historical bytes, while `current_body_sha256` is freshly
computed from the stable current bytes. Even an injected test double that
reports equal digest values for unequal bytes must emit `changed`.

Metadata re-queries the current #154 diagnostic. Historical content validates
the live graph/classification/digests under its Retention access grant and
uses the terminal raw-response seal above. Current-file
values come from the same stable handle read and are written while the exact
runtime-generation capability, Retention operation grant, and opaque #154
current-authorization capability all remain usable. No response body/header/status is stored in the component
or reconstructed from a cached serializer buffer. Ingest replay freshly derives
204; the three GET/POST documents are freshly written from just-validated
semantic facts.

Choice A, fixed fresh writers, is selected. Stored response bytes create stale
authority; general serializer defaults can omit nulls/reorder/escape differently.
Strongest counterexample: after an atomic editor replacement, replaying a cached
current-file response returns the old body/digest/read time even though the
route claims a current read.

### Gate 8 — historical-to-discovery proof and native current-file read

The public startup carrier is closed and additive. Raw-default Local Monitor
accepts repeatable CLI options
`--skill-discovery-project-path <absolute-path>` and
`--skill-discovery-directory <absolute-path>`, with no environment-variable,
JSON, delimiter-list, CWD, or inferred-root fallback. The first option may
occur 0..16 times and the second 0..32 times, counted before native-identity
deduplication; input order has no semantic effect. Missing values, a 17th/33rd
occurrence, or use of either option with `--sanitized-only` is a CLI parse
failure before the host starts. The exact sanitized messages are respectively
`--skill-discovery-project-path requires a value.`,
`--skill-discovery-directory requires a value.`,
`local-monitor accepts at most 16 --skill-discovery-project-path values.`,
`local-monitor accepts at most 32 --skill-discovery-directory values.`, and
`skill discovery options cannot be used with --sanitized-only.` No supplied
root value is echoed.

When these Skill-discovery parse faults coexist, the sole first result is
independent of option/array order: missing/empty ProjectPath member -> missing/
empty SkillDirectory member -> ProjectPath count above 16 -> SkillDirectory
count above 32 -> sanitized-only conflict. Direct CLI and wrappers use that
same precedence and exact corresponding message. After a valid nonempty parse,
unsupported/uncertified platform precedes every configured-root syntax/handle/
filesystem fault and returns only `skill_discovery_platform_unsupported`; on a
supported/certified platform any invalid root returns only
`skill_discovery_root_configuration_invalid`. Multiple root faults never expose
which value failed.

The repository wrappers expose the same carrier as exact repeatable PowerShell
array parameters `-SkillDiscoveryProjectPath <string[]>` and
`-SkillDiscoveryDirectory <string[]>` on both `start.ps1` and
`install-startup-task.ps1`. They reject an empty member with the corresponding
exact `requires a value.` message, apply the same occurrence-limit and
sanitized-only messages before launch/task replacement, and serialize members
only as repeated executable option/value argv pairs. `-StartNow` transfers the
same arrays; scheduled startup encodes them in the Task Scheduler action.
Wrapper DryRun, logs, state, status, and ordinary output reveal only exact
presence/counts, never values, and a validation/encoding failure starts no
process, creates no task, and preserves an existing task. Under the trusted-
local boundary, active process argv and scheduled-task action arguments remain
visible to the local OS user/administrators; this unavoidable OS carrier is not
copied to any other persistence or telemetry.

After parsing, any configured root that fails the exact syntax, handle,
identity, filesystem, or retained-handle preflight aborts host startup with
sanitized reason `skill_discovery_root_configuration_invalid`; no root value or
native fact is emitted. Zero supplied roots is valid and starts the host
without the current-file service/POST or a discovery call. A supported
platform with a nonempty fully valid root set may compose the service/POST only
after its platform certification gate; an unsupported platform aborts startup
with `skill_discovery_platform_unsupported` when roots were explicitly
supplied, while zero roots remains valid route absence. There is no silent
partial-root reduction, runtime reload, or alternate carrier.

Startup builds one immutable per-platform `DiscoveryRootSetV1` with at most 16
ProjectPaths and 32 SkillDirectories. Each configured path is an absolute local
strict-Unicode path of at most 4,096 UTF-8 bytes, opened no-follow from a
platform anchor, classified on the closed local filesystem allowlist, retained
by a noninheritable root handle/fd and native identity, and converted to the
exact canonical SDK path key. Any invalid/unopenable/unsupported root rejects
host startup using the sanitized Gate-8 reason; it never silently degrades an
explicit configuration to route absence. Dedupe only within the
same role/native identity, choosing the ordinally smallest path key; the same
native root in both roles remains two entries. Zero accepted explicit roots
means no current-file service/POST and no discovery call.

Each successfully preflighted root set is one immutable process generation.
Its process-owned canonical root-string copy, revision, noninheritable retained
root handles/fds, and native root identities live only in that generation and
are never product-persisted or static/global across host instances. Configured
values may otherwise exist only in the explicit local OS argv/Task Scheduler
carrier above. V1 has no hot reload or in-process
replacement. Each current-file request atomically acquires one generation
lease at the route's service-admission stage, before origin/body processing,
Retention, #154, or runtime acquisition. A request that loses to the host's
atomic normal-shutdown admission closure aborts with no status/header/entity and
acquires none of those later authorities. A winner may later capture revision/
arrays and releases the lease after response
serialization or every failure/cancellation path. Host shutdown first closes
new root/runtime admission atomically and drains already-admitted requests
without root cancellation. A root-admitted request that has not yet acquired a
runtime capability cannot cross that later closed boundary and follows the
no-response release rule above; one that already owns both drains normally.
Only then does shutdown dispose every retained root
handle/fd only after the last generation lease is released; forced process
termination relies only on OS handle cleanup and publishes no evidence.

Every SDK result/list/item reference, historical/discovery candidate path,
relative-segment array, `CurrentSkillReadTargetV1`, and descendant directory/
file handle or fd is request-local. Descendant/final handles are
noninheritable and deterministically disposed in reverse acquisition order
before the generation lease is released and before the response completes,
including every exception, abort, lease-expiry, missing, unsafe, raced,
oversized, and binary arm. No request object or candidate string is cached,
queued, logged, backed up, restored, or retained by the process generation.

`SkillProducerPathKeyV1` is the only producer/configuration path parser. It is a
pure parser over the supplied .NET string: no `Path.GetFullPath`, CWD, URI,
environment expansion, filesystem casing query, Unicode normalization, trim,
slash replacement, dot collapse, or symlink resolution participates. Common
input requirements are one or more valid Unicode scalars, no unpaired surrogate,
no ASCII control (`U+0000..U+001F` or `U+007F`), and at most 4,096 strict UTF-8
bytes. A root/candidate that fails is not repaired.

The Windows grammar is exact:

- the input begins ASCII letter, colon, backslash; drive letter may arrive in
  either ASCII case and is the only transformed byte, emitted uppercase;
- backslash is the only separator; forward slash, UNC, device/extended prefix,
  drive-relative form, and any colon after the drive colon are invalid;
- `C:\` is the only trailing-separator/root form. Otherwise there is at least
  one segment, no empty/doubled/trailing segment, and no `.` or `..` segment;
- each segment is 1..255 UTF-16 code units, contains none of
  `< > " | ? * : / \` or the common control set, and ends in neither ASCII dot
  nor space. Its stem before the first dot is not ASCII-case-insensitive
  `CON`, `PRN`, `AUX`, `NUL`, `COM1`..`COM9`, or `LPT1`..`LPT9`;
- the key is the uppercase drive, `:\`, and every otherwise unchanged segment.
  Key equality/prefix comparison is ordinal over exact strict UTF-8 segment
  bytes. There is no segment case fold or `OrdinalIgnoreCase`, even on a
  case-insensitive NTFS/ReFS directory.

The Linux grammar is exact:

- the input begins exactly one `/`; `/` is the root key, and every nonroot key
  has no doubled or trailing slash;
- slash is the only separator; backslash, empty, `.`, and `..` segments are
  invalid;
- every segment is 1..255 strict UTF-8 bytes and satisfies the common scalar/
  control rules; the key preserves the input's exact UTF-8 bytes, with ordinal
  equality/segment comparison and no case fold or normalization.

Parse the historical path before discovery. A nonabsolute/invalid historical
key returns `409 skill_current_file_not_discovered` with no SDK call. During the
single scan, compare unchanged historical name bytes and the closed source token
first. A descriptor with a null, nonabsolute, invalid, or unequal `Path` key is
not a candidate and is ignored. Only an exact equal historical/discovery path
key advances to root-relation validation; at that point malformed/nonconforming
`ProjectPath`, wrong role, or out-of-root relation is `unsafe`, not ignorable.

Path-key equality is `(platform,canonical drive-or-root,ordered segment bytes)`.
Strict descendant is the same anchor and an ordinal segment prefix plus at least
one candidate segment; root equality and textual prefix collisions do not
qualify. The final candidate segment is exact ordinal `SKILL.md`. Project/
inherited requires its parsed `ProjectPath` key to equal one configured
ProjectPath key and that retained root's native identity. Other eligible sources
require JSON null `ProjectPath` and one explicit SkillDirectory relation. A
candidate matching nested/multiple retained roots yields distinct targets and
is `unsafe`; it is never shortened to the longest/first root. The checked-in
`path-key-v1.golden.json` parse/equality/relation vectors are normative.

The exact request-memory revision frame in Group 8 and its five golden vectors
are normative. The process-owned immutable root-set object, revision, string
copy, identities, handles, discovery results, and absolute candidate paths are
never copied into product database/backup/state, restored, logged, measured, or
returned. Only configured values in the disclosed local OS argv/Task Scheduler
carrier may persist outside that generation. Restore uses the destination
process's current root set.

If the historical source is `builtin`/`remote` or its required root role is
absent, return `409 skill_current_file_not_discovered` without SDK work.
Otherwise capture the root-set object, re-prove each canonical root string maps
to its retained native identity, and call SDK 1.0.4 exactly once with immutable
complete canonical ProjectPath/SkillDirectory arrays,
`excludeHostSkills:false`, and cancellation linked to request abort, runtime-
generation invalidation, and Retention operation-
lease's fixed admission expiry. This current-file consumer never renews that
two-minute grant or reschedules Retention's expiry notification. The link stops work only; the tagged cause coordinator above
preserves terminal authority and never forwards runtime cancellation into the
Retention handle. Do not add a post-return 4,096-item/32-MiB/
15-second product budget: the SDK already materialized the result. Top-level
null/exception/enumeration failure/null item/unreadable documented member is
discovery-unavailable. A public nonnull empty list—including SDK-normalized
missing/null/empty `skills`—is successful inventory with no candidate.

At every post-admission discovery/result-enumeration/native-walk/read/re-proof/
serialization fence, cancellation may stop work, but only the store-backed
terminal operations authoritatively prove the live grant/expiry. For every
fully buffered nonraw discovery/native error after runtime acquisition,
current-file first requires Retention
`TryCompleteWithoutRaw()=completed_without_raw`, then runtime
`TrySealResponse`; Retention lost/busy aborts with no response, runtime loss
replaces the candidate with exact discovery-unavailable 503, and both wins send
the original safe error. For fully buffered raw 200, current-file first wins
runtime `TrySealResponse`, then requires Retention
`TrySealRawResponse()=sealed`; runtime loss discards raw and sends its 503
only if Retention completion-without-raw succeeds, while Retention lost/busy at
either point aborts with no response. A runtime seal already won when Retention
raw sealing fails is abandoned/released without output. Thus no buffered raw
200 escapes after a Retention-loss outcome, and no nonraw current-file error
escapes after runtime invalidation.

Scan the complete materialized list once without early exit. For every nonnull
item, read all eight documented SDK 1.0.4 facts exactly:
`(Name,Source-underlying-string,Path,ProjectPath,Description,ArgumentHint,
Enabled,UserInvocable)`, preserving ordinal string bytes, null, and Boolean
values. A later null item, enumeration/member failure, or null in a documented
nonnull member is discovery-unavailable even after one or two candidates were
seen. Name matches the unchanged historical name bytes; source matches the
closed historical token. `Enabled`, `UserInvocable`, `Description`, and
`ArgumentHint` neither grant nor remove eligibility, but remain ambiguity facts.
Apply the same platform producer-path-key parser to historical and discovery
paths. A relative historical/discovery path is not a candidate and is never
resolved from CWD, repository, prompt, workspace, timestamp, path parent, or
configured root string.

The source/root relation is exact:

| Source | Required discovery relation |
|---|---|
| `project`, `inherited` | nonnull `ProjectPath` has the exact configured ProjectPath key and native identity; `Path` is a strict descendant |
| `custom` | `ProjectPath` is null; `Path` is a strict descendant of an explicit SkillDirectory |
| `personal-copilot`, `personal-agents`, `plugin` | same explicit SkillDirectory rule; implicit host roots grant nothing |
| `builtin`, `remote`, missing/unknown | unavailable in v1 |

Strict descendant means one or more relative segments and final segment exact
ordinal `SKILL.md`. Prefix segmentation, never string `StartsWith`, derives a
`CurrentSkillReadTargetV1` containing only retained root handle/identity, role,
relative segments, and expected revision. A matching name/source/path item with
malformed or out-of-root relation is `unsafe`; unrelated readable nonmatches are
ignored. Collapse rows only when the complete eight-fact DTO tuple and resolved
root role/native identity/relative-segment target are identical. Retain at most
two post-collapse eligible descriptors while continuing full-list validation.
Exactly zero is not-discovered, exactly one proceeds, and more than one is
unsafe even when both resolve to the same native target. Never select first or
by SDK order.

Windows current-file registration requires certified local NTFS/ReFS and the
retained-root `NtCreateFile` every-segment walker: one relative segment per
open, `FILE_OPEN_REPARSE_POINT`, retained ancestor handles, reject every reparse
attribute/tag, device/network/UNC/ADS/redirector/mount crossing, and prove
volume/file identities. Linux registration separately requires kernel >=5.8,
certified local ext4/xfs/btrfs, per-segment retained-fd `openat2` with
`RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS|RESOLVE_NO_MAGICLINKS|RESOLVE_NO_XDEV`,
and returned-mask proof of
`STATX_MNT_ID|STATX_INO|STATX_TYPE|STATX_MODE` plus device major/minor for root
and every relevant fd, with `STATX_SIZE|STATX_MTIME|STATX_CTIME` on each object
whose values are classified/compared. Any missing required mask bit is sanitized
native failure, never zero/default evidence. macOS/BSD/other and uncertified filesystems register no POST.

Re-prove root mapping before/after discovery; walk and retain every segment;
require one regular file; capture chain/final identity, mode, size, mtime and
ctime/change time; read at most 1,048,577 bytes from the same handle; repeat all
identity/metadata/root/revision proofs. Any change discards bytes. Only a stable
read is classified and hashed without BOM/line-ending/Unicode transformation.

Native outcomes are total and use no exception-message mapping:

| Observed fact | Exact result |
|---|---|
| successful discovery has no exact eligible target | `409 skill_current_file_not_discovered` |
| path/root relation, node type, reparse/symlink/magic-link, mount/volume, ADS/device/network, or other closed policy violation | `409 skill_current_file_unsafe` |
| retained-root/revision/native identity or any chain/final metadata differs between required proofs, including disappearance after an identity was observed | `409 skill_current_file_raced` |
| sole confirmed candidate-segment/final-node not-found arm below, while every retained-root/revision proof is unchanged and no prior identity proved that object | `404 skill_current_file_missing` |
| stable regular file is greater than 1,048,576 bytes | `422 skill_current_file_oversized` |
| stable in-bound bytes are not strict UTF-8 | `422 skill_current_file_binary` |
| every other native/API/I/O failure, including access denied, sharing violation, resource exhaustion, and unclassified error | `503 local_monitor_ui_unavailable` |
| stable in-bound strict UTF-8 | exact gate-7 200 |

The sole Windows confirmed-not-found inputs are raw NTSTATUS
`STATUS_NO_SUCH_FILE (0xC000000F)`,
`STATUS_OBJECT_NAME_NOT_FOUND (0xC0000034)`, or
`STATUS_OBJECT_PATH_NOT_FOUND (0xC000003A)`, or their exact Win32 translations
`ERROR_FILE_NOT_FOUND (2)` and `ERROR_PATH_NOT_FOUND (3)`. The sole Linux input
is `ENOENT (2)`. They map to `missing` only for a candidate segment/final lookup
under an already re-proved retained root. The same status while re-proving a
previously retained root, or after any candidate identity was observed, is
`raced`. `ENOTDIR`, `ELOOP`, `EXDEV`, nonregular final type, or an observed
reparse/mount/type violation is `unsafe`; `ESTALE` or an exact pre/post mismatch
is `raced`; all remaining statuses/errno values are the sanitized 503.

After a target exists, precedence is `unsafe -> raced -> confirmed missing ->
other native failure -> oversized -> binary -> success`; discovery-unavailable
and not-discovered terminate before this native order. Thus race overrides a
simultaneous not-found/size/UTF-8 fact, and unstable bytes are never classified.
A missing/unsupported native facility is never a request result. With no
configured roots it leaves the platform route absent; with explicitly supplied
roots it aborts startup using the sanitized platform/configuration failure
above. Windows and Linux release gates are independent; an unsupported
zero-root target still serves the unchanged historical routes.

Choice A, request-local exact logical binding plus handle/fd authorization, is
selected. Persisted discovery inventory/revision creates sensitive stale
authority; universal path fallbacks cannot prove beneath/no-follow identity.
Under this product's trusted-local threat model, D083 explicitly accepts the
residual distinction that SDK discovery can observe an ABA-replaced namespace:
the SDK's strings authorize no bytes, and every returned byte is authorized by
the fresh retained-root, handle/fd-relative segment proof. No Security or PO
choice remains. Design C—a common stable SDK origin ID—is only a future
versioned option if the producer later supplies it; it is not a current gate,
fallback, or reason to reinterpret Choice A.

## Receiver-only and sanitized-only composition boundary

`--sanitized-only` is the receiver-only host. This boundary is keyed to that
composition, not to whether a lower-level raw store happens to exist for frozen
receiver duties. It composes or registers none of:

1. the live bundled-SDK v2 Skill producer/forwarder, runtime-capability bridge/
   pending registry, or its #119 handoff sink;
2. `POST /api/session-ingest/v2/events`;
3. the #158 snapshot transaction participant/writer;
4. the Skill discovery/current-file service; or
5. the three exact Skill raw routes:
   `GET /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}`,
   `GET /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/content`,
   and
   `POST /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/current-file-read`.

Those endpoints are absent/nonmatching, not stage-1 service-unavailable
responses. No omitted producer, sink, writer, service, or route is remapped,
retried, or delegated to `SessionRoutes.MapMachineRoutes`, the frozen-v1
`SessionEventNormalizer`, or another handler. Frozen v1 remains registered only
under its unchanged contract and continues to treat `skill.invoked` as an
unsupported v1 event.

Existing OTel-only claims remain under #154's sanitized projection. They create
no `skill_invocation_snapshot` row and project exactly `snapshot_id=null` and
`snapshot_state=not_captured`; therefore no snapshot metadata, historical-
content, or current-file route can address them. This receiver-only rule governs
live composition and endpoint registration only. It neither requires nor
forbids installation or validation of `skill_invocation_snapshot:1`; component
lifecycle and backup validation remain under their startup owner, and no test
may infer host posture merely from table existence.

## AI boundary

Historical Skill content may reach Issue #163 only through an explicit,
immutable, bounded Issue #162 scope. Current-file content is never added
automatically.

Skill body/path and provider request/response payloads do not enter:

- normal logs;
- provider metadata;
- frozen machine APIs or SSE;
- repository-safe evidence;
- static artifacts; or
- committed test/live-validation artifacts.

Provider unavailability does not make Repository, Session, Skill metadata,
inspector, or deterministic Compare core unusable.

## Prerequisite DAG

The rollout prerequisite DAG is explicit and fail closed:

```text
lane A: tracked canonical closure of the Retention and #119/#158 owner text
  -> mandatory #117/#119/#124/#156/#157/#158/#161 issue reconciliation/readback
  -> #119 strict no-model parser + immutable typed handoff, nonregistered

shared implementation fence: #124-owned exact Session 14 schema/migration
  authority integrated; no Retention implementation overlaps this owner

lane B, only after the #124 fence:
  #156-owned carrier/composition contract closure for #161-owned direct
     archive facts
  -> #161 canonical local_archive:1 specification, implementation, validator,
     mutation/replay/batch behavior, routes, backup namespace, and restore

lane C, only after the same #124 fence:
  Retention pinned-read prerequisite implementation and tests; it may run
  beside the remaining #156/#161 work only when their changed owners are
  disjoint, otherwise serialize

lane A + #124 fence + lane B + lane C, all landed and integrated
  -> only then:
  #158 child/schema/migration/persistence/readers/routes/platform registration
```

Tracked decision history preserves D079, D080, D081, D082, then D083. D080 is
an ordering prerequisite for the canonical decision log, not a #158 runtime
dependency. D079 supplies the integrated #124 Session 14 authority; D081 closes
the #156 carrier/composition contract; D082 supplies the #161 direct archive
contract. Their nontracked/reviewed packets do not satisfy the runtime gates:
the corresponding tracked specifications, implementations, tests, backup and
restore behavior must be integrated and green in the order below. The
Retention prerequisite is a separately landed shared-contract and
implementation change; it is neither folded into nor silently installed by
#158.

#158 does not duplicate or weaken either prerequisite owner. In particular,
merely listing `local_archive:1` in component validation order is insufficient:
the #124-owned exact Session 14 schema/migration authority must be integrated
before #156; the #156-owned carrier/composition contract must close for
#161-owned direct exact-ID Session/Repository archive facts; and #161's current
component validation plus backup/restore integration must then be green before
any #158 child registry, migration, writer, reader, route, or host registration.
This chain is only a #158 rollout prerequisite, not a #158 archive contract.
The required Session 14 authority is distinct from the separate provisional
#124 safety-archive exception below, which #158 does not promote or import. The #119
nonregistered parser/handoff is not #158 runtime implementation: once tracked
canonical closure lands and the mandatory live-Issue reconciliation/readback
succeeds, it may proceed in parallel with #124/lane B. The
Retention implementation must wait for integrated #124 because both change
`SqliteSessionStore`; after that fence it may proceed beside disjoint #156/#161
work only when ownership is proven nonoverlapping. All lanes are hard gates at
their join into #158.

## Production symbols and implementation order

Use existing owners and keep the new component isolated:

1. Land the tracked canonical owner amendments above. Then, under separately
   authorized remote-write authority, update or supersede every conflicting
   acceptance/dependency sentence in #117, #119, #156, and #158 plus any
   additive-v2 copy in #157; verify #124 and #161 already point to the promoted
   owners. Link the exact tracked D083 specifications/decision; remove `model`,
   the obsolete `SkillDiscovery.ProjectPaths` and
   `SkillDiscovery.SkillDirectories` carriers, weaker Unix `openat`, and
   conflicting dependency text; and publish the exact
   `#124 -> #156 -> #161 -> #158` order,
   with #119's nonregistered parser/handoff before #158 and the separately
   landed Retention implementation after #124 and before #158. Preserve frozen
   #119 v1 and #154's sole current-valid authority. Read back
   #117/#119/#124/#156/#157/#158/#161, record every Issue URL/revision, prove
   absence of every stale token and the exact DAG, and block dispatch if any
   conflict remains. Tracked promotion alone does not unlock an affected
   implementation or register runtime code.
2. Lane A, only after that readback gate succeeds: under #119 add the strict bounded
   single-pass `SkillInvocationV2Parser`,
   no-`model` SDK 1.0.4 mapper, immutable
   `ParsedSkillInvocationV2Batch`/`SkillInvocationV2AcceptedEnvelope` typed
   handoff, exact artifact loader/registry, `SkillInvocationNormalizedJsonV1`,
   and the typed handoff's required opaque runtime-capability slot. Keep all of it
   nonregistered: no route, DB write, or 204. Lane A may run in parallel with
   the ordered prerequisite work.
3. Integrate and validate the #124-owned current Session 14 schema/migration
   authority alone. Do not overlap the Retention implementation on
   `SqliteSessionStore` before this exact fence is integrated.
4. Only after step 3, run lane B and lane C: lane B closes the #156-owned
   carrier/composition contract for #161-owned direct exact-ID Session/
   Repository archive facts, then lands #161 `local_archive:1` specification,
   validator, mutation/replay/batch, routes, backup namespace, restore, and
   focused/full validation; lane C implements the separately promoted Retention
   admission/grant/renewal/equality-replay contract and tests. They may run
   concurrently only while changed owners are disjoint; otherwise serialize.
5. Join only after lanes A, B, and C are all integrated and green. Then extend
   `SessionSchemaV11Validator` with a closed
   `SessionChildTriggerExtensionRegistry`; prove the unchanged Session 14 core
   fingerprint and mandatory later child validation.
6. Add `SkillInvocationSnapshotSchemaV1`,
   `SkillInvocationSnapshotSchemaV1Validator`, and
   `SkillInvocationSnapshotReceiptFingerprint` under
   `CopilotAgentObservability.Persistence.Sqlite/SkillInvocationSnapshot`.
   Add `SkillInvocationSnapshotBackupValidation` to
   `SqliteRuntimeBackupService` and prove absent/present/partial restore before
   enabling migration or any writer/route.
7. Add `SessionSkillInvocationParticipant.InsertOrVerify(connection,
   transaction, write)` for the exact Session/Run/Event/content/Retention/
   claim/snapshot/receipt transaction. Do not call the frozen-v1
   `SessionEventNormalizer` or open a nested store transaction.
8. Add metadata/historical/current readers and `SkillInvocationRawJson` fresh
   writers. Extend only #154's read owner with
   `TryAcquireCurrentSdkClaimAuthorization`; current-file holds its opaque
   registry-generation capability through response completion, while metadata
   retains the point diagnostic. Add
   `ISessionStore.ReadGenericRouteContentAsync` as the sole call
   from `SessionRoutes.MapRawContentRoute`: one Session-owned `BEGIN IMMEDIATE`
   performs the type-only policy check, then only for non-Skill invokes the
   transaction-aware arm of the existing Retention admission/selector on the
   same connection/transaction. Do not call `ReadContentAsync`, add a separate
   policy service, or nest/split the transaction.
9. Add immutable `CopilotRuntimeGenerationV1`, its cancel/invalidate/terminal-
   seal provider, bounded `SkillRuntimeCapabilityBridgeV1`, and the sole SDK-
   callback -> normalized-body -> pending token -> loopback v2 route topology.
   Add `CopilotSdkSkillDiscoveryGateway`, immutable `DiscoveryRootSetV1`,
   `SkillProducerPathKeyV1`, `WindowsCurrentSkillFileReader`, and
   `LinuxCurrentSkillFileReader`; compose each platform independently but do not
   register it yet.
10. Extend `start.ps1`, `install-startup-task.ps1`, and `common.ps1` with the
    exact `-SkillDiscoveryProjectPath` and `-SkillDiscoveryDirectory` array
    parameters. Validate the same pre-deduplication 16/32 limits, nonempty
    values, and sanitized-only conflict before process launch or scheduled-task
    replacement. Serialize each member only as a repeated
    `--skill-discovery-project-path` or `--skill-discovery-directory` child argv
    pair, including `-StartNow` and encoded Task Scheduler startup; no comma/
    delimiter/list carrier is created. The task parser may report only exact
    presence and counts, and DryRun/log/state/status/output must never expose a
    supplied value. A wrapper validation/encoding error starts no process,
    creates no task, and leaves an existing task unchanged. Task Scheduler
    action arguments and an active process command line necessarily retain the
    configured roots under the trusted-local OS boundary; no other persistence
    or emission is permitted.
11. Only after every prior RED is green and independently governed component
    initialization/validation has succeeded, compose the v2 route and metadata/
    historical routes inside `MonitorHost`'s raw-default, non-receiver branch.
    Activate the live producer -> capability bridge -> sole loopback v2 route
    -> #119 typed handoff -> #158 participant/writer chain only for an admitted
    same-client generation; no direct callback writer/handoff exists. Without one the registered v2
    POST returns its exact stage-1 503 and cannot reach a body or write. Never
    add v2 to unconditional `SessionRoutes.MapMachineRoutes`. Compose the current-file
    service and POST only in that branch when its Windows or Linux platform gate
    is certified; a zero-root platform omits only that service/POST, not v2
    path or the metadata/historical routes. On that registered surface, missing/
    mismatched runtime admission is the fixed discovery-unavailable 503 before
    SDK work, never dynamic route removal. Explicit roots on an
    unsupported/uncertified platform fail startup as fixed in Gate 8. Receiver-only/
    `--sanitized-only` composes none of these surfaces. This live-composition
    rule does not decide whether startup installs or validates the component.

Component migration/restore validation order is exactly:

```text
monitor
-> session:14
-> local_repository_catalog:1
-> local_archive:1
-> retention:1
-> skill_projection:1
-> skill_invocation_snapshot:1
-> local_workspace_projection:1 (when released)
```

An older backup with no snapshot component installs an empty current component
only after all parents, then restores no snapshot rows. A present component is
validated before any carrier materialization, after staging migrations, before
swap, and again installed. Per snapshot, restore proves exactly one selected
binding in `native|explicit_resume|explicit_handoff` and exact optional Run
natural-key cardinality without enumerating unrelated bindings or reconstructing
an explicit-link target. Sanitized export/import has no component, empty
carrier, count, processing version, route, or reconstruction path.

Those strict rules govern #158 and every normal startup, backfill, backup,
restore-staging, and read path. #158 neither owns nor authorizes a Retention
fixed-migration exception. If and only if #124 separately promotes one, #124's
exact canonical text alone governs it and it remains outside snapshot carrier
materialization. The only potentially excepted path is an exact-current
extracted safety archive after the #124 gate and before restore: it uses
select-only current validation and already-existing restorable coverage, and
skips only writable Retention adoption/backfill before restore-only
normalization/reconciliation. Absent or older migrations remain strict; exact
`Missing` stays `Missing`; `Match` is normal; mismatch, extra, malformed, or FK
failure fails. #158 adds no terminalization, synthesis, raw copy, or lifecycle
mutation. Until that separate promotion, no such exception exists.

## Required TDD slices and release gates

1. Prove the exact prerequisite order on one current candidate: #124-owned
   Session 14 schema/migration integration alone; no concurrent Retention edit
   on `SqliteSessionStore`; then #156 carrier/composition RED/GREEN for #161-
   owned direct exact-ID Session/Repository archive facts; then #161
   `local_archive:1` validator, mutation/replay/batch, route-byte, backup-
   namespace, restore, and integrated full validation. No #158 child or runtime
   registration may precede it.
2. Only after the #124 integration fence, run the Retention pinned-read/
   equality-replay RED/GREEN matrix in the prerequisite artifact. It may overlap
   only disjoint remaining #156/#161 work and must be integrated before #158.
3. Session extension REDs: execute the canonical DDL in SQLite, read both exact
   `sqlite_schema.sql` values, prove byte equality with the delimiter-free
   registry strings and equality after the current Session canonicalizer, then
   prove the DDL source differs only by its terminal statement delimiter.
   Cover absent stamp, each missing/altered/aliased/wrong-target/additional
   trigger, appended terminal semicolon, altered internal semicolon/`WHEN`/
   `RAISE`, type/storage-version faults, partial child, and unchanged parent
   fingerprint.
4. Golden-byte REDs for every artifact/hash, no BOM/CR, one LF, no `model`,
    exact nine properties, sidecar/registry linkage, DDL two/eight inventory,
    receipt 29 fields/726 bytes, exact 43-byte generic denial, every .NET 10
    writer token vector, all 17 literal metadata/error response vectors, and
    every platform path parse/equality/relation vector.
   Parser REDs fix reader depth 64/65, trailing comma, comment, and an early
   unknown member containing excessive nesting before a later duplicate.
5. Migration/backup REDs: absent/partial/current/future component, stamp-last
   rollback checkpoints, every column/constraint/trigger/link, selected native
   binding, duplicate Run ambiguity, content/classification/fingerprint
   reconstruction, immutable Event `content_state='available'` for both live and
   canonical post-deletion graphs, all `write_at`/expiry equalities, ordering,
   older backup, and no sanitized carrier. Raw-local replay REDs cover
   consented `include_session_content`, exact document/provenance and
   `not_applied_raw_capture`, bounds and all-or-none grant before one authorized
   read snapshot, complete nonraw graph validation there before content
   selection/materialization, then raw-dependent document/base64/digest/
   reclassification/credential validation before staging/publication; existing
   8 MiB `entry_too_large`, plaintext/JSON-escaped/base64-hidden credential
   matches and matcher timeout after preflight/grant on export plus externally
   supplied replay preview/import, source conflict/idempotency, strict archive
    inspection, isolated no-live-graph reconstruction, and sanitized-only
    rejection before raw lookup. Every rejection proves zero published/staged
    member and no decoded value in response/log/evidence. Interleave operation-
    grant expiry/cancel/busy across HTTP/direct-CLI `PreviewAsync`, Local Monitor
    `CreateAsync`, CLI `CreateAndPublishAsync`, and Local Monitor retained-result
    GET plus POST idempotent/racing-existing arms. Exercise
    `TryCompleteWithoutRaw`, `TrySealRawReplayTransientPublication`, and
    `TrySealRawReplayFilePublication` at every CAS/transaction/scope/clock/map-
    insert/non-overwrite-move fence. Only transient `sealed` inserts one memory-
    only buffers+metadata+ten-minute-authority entry; only file `sealed` returns
    one same-directory move ticket. Cross transient `PreparePut` entry/byte/
    eviction/expiry/dispose/competing-reservation limits: refusal completes to
    unchanged `archive_too_large`, seal requires a guaranteed reservation, and
    `CommitPut` cannot fail. Assert exact HTTP zero-response versus CLI
    safe-code behavior and unchanged CLI failure Preview/ResultView stdout before
    error token/nonzero exit. Cross commit-control -> `output_name_invalid` ->
    capture/provider -> archive result -> `output_exists` -> pre-inspection
    `publish_failed` -> `publish_validation_failed` -> seal -> move
    `publish_failed|success`; prove no partial/replacement file or new disk
    carrier and exactly-once publication/discard release.
6. Transaction REDs: receipt-hit-before-registry; equal replay after unavailable/
   revoked registry; equal-corrupt exact local-monitor 503; miss then registry
   unavailable/revoked exact local-monitor 503; in-transaction receipt race;
   pre-existing/racing Event exact 409; lock/commit-only persistence-busy; one
   clock sample and exact time relations; Event state available for every
   classification; Session and Run
   null/0/1/>1; mixed surfaces/bindings; unchanged `native`,
   `explicit_resume`, and `explicit_handoff`; `trace_context`/unknown exact 503
   and zero writes; every injected rollback point; same replay after registry
   revocation; mismatch/v1 Event conflict; derived 204. Inject registry
   publication/revocation before capture, before generation-lease acquisition,
   during the one recapture, after lease acquisition, before every insert, and
   before commit: the held generation blocks publication through commit, one
   pre-lease change recaptures, second churn fails 503/rollback, and a receipt
   race needs no registry lease. Force that race and prove the Retention-owned
   internal validator uses the already-held mutation connection/transaction,
   opens no nested `BEGIN`, samples no clock for different/one clock for equal,
   serializes cleanup, and makes the enclosing owner roll back zero writes for
   exact different 409, readable/deleted 204, transitional/corrupt/busy 503;
   no arm creates a Retention or registry lease or emits raw. Public equal
   replay REDs interleave cleanup before/
   during/after its validation-only `BEGIN IMMEDIATE`, prove live
   `row_readable`, each owner-valid nonreadable raw-retained transition, and
   exact deleted/tombstone. Transition returns the fixed sanitized 503 without
   raw selection, while readable/deleted valid cases derive 204. Prove one
   nonpersisted validation clock, zero Retention lease/data writes, no raw
   output, busy 503, and full cleanup serialization. For public and mutation-
   race equal replay, interleave runtime invalidation after graph validation,
   after rollback, before status/header finalization, and at response completion.
   `TrySealReplaySuccess` must use the same callback capability, totally order
   against invalidation, and permit only the indivisible empty 204 when it wins;
   loss returns sanitized 503 with no write/registry lease/raw output.
7. SDK/route REDs: exact nine-property mapper and normalized writer, global CLI
   ignored, null status/member, empty/wrong/changed Version, null/wrong/changed
   ProtocolVersion, SessionStart mismatch, new connection generation, top-level
   discovery null. Inject lone high and lone low surrogates separately into
   Session ID, Event/parent/agent IDs, `name`, `path`, `content`, every optional
   payload string, and each `allowedTools` element: the producer pre-scan under
   its callback capability yields sanitized unavailability with no complete
   body/token/send/handoff/write and never invokes `Utf8JsonWriter`. Verify both
   `producer_rejection_vectors` in the exact writer golden and keep synthetic
   receiver raw-token/internal-admission tests for exact `body_unicode_invalid`/
   `path_unicode_invalid` classification without replacement. With an existing valid stored snapshot, inject startup and
   later-generation status null/mismatch: producer/handoff/writer remains
   inactive; v2 returns exact stage-1 local-monitor 503 before body/zero writes;
   metadata and historical routes retain their normal database/#154/Retention
   outcomes; and a configured/certified current-file route remains matched but
   forms exact discovery-unavailable 503 after authorization and before SDK/
   native work. Retention completion-without-raw sends it, while lost/busy
   aborts with no response; both release every acquired capability and neither
   fabricates a runtime seal. Also prove zero roots still means
   current-file route absence and no runtime mismatch relabels an admitted exact version or hides
   stored evidence. Runtime-generation REDs publish mismatch/reconnect before
   callback admission, after bridge registration/before loopback send, during
   send/before route consume, after consume/before #119 parse/handoff, at each
   receipt/transaction fence, before `TrySealCommit`, before/at
   `TrySealReplaySuccess`, during discovery, after
   discovery/before result consumption, during native read, and before
   `TrySealResponse`. Prove invalidation immediately closes admission/cancels
   unsealed capabilities and orphan tokens with no drain; each next fence
   discards/rolls back. At every v2 pre-seal invalidation assert exact
   `503 local_monitor_ui_unavailable`, owned JSON/no-store headers, zero writes,
   and no raw data; at every current-file pre-response-seal invalidation assert
   exact `503 skill_current_file_discovery_unavailable` only when Retention
   completion-without-raw succeeds, otherwise the lost/busy no-response abort.
   Prove release of every capability and no SDK/native/path detail. Caller abort/already-started
   invariant paths abort without a substitute response. Terminal seal versus
   invalidation has one atomic order; a won seal either completes only its
   indivisible commit/send or enters the one-shot abandoned/released/no-output
   state when a later independent authorization fails. Every
   transferred capability is the same object and held to terminal completion;
   stale callbacks cannot transfer generations. For every post-transfer v2
   body-binding/media/size/outer-parse, receipt/Event conflict, registry/
   storage/busy/corruption response candidate, invalidate before/at
   `TrySealV2NonCommitResponse`: a win sends only the original candidate and a
   loss substitutes exact local-monitor 503; pre-token method/service/max-
   feature/capability failures call no seal. Interleave invalidation before/
   after a won `TrySealCommit` with SQLite commit success, busy/locked, and
   injected unexpected failure: the one seal authorizes respectively 204,
   rollback+persistence-busy, or rollback+local-monitor-unavailable, with held
   registry lease through commit/rollback and no second seal/retry.
   Bridge REDs cover 0/1/64/65 pending entries after expiry purge; injected
   monotonic just-before/equal/after expiry and clock-add overflow, proving only
   `now < expires_at` is valid; RNG failure and forced collision; exact random
   32-byte to 43-character unpadded-base64url grammar; exact producer body
   preflight at 8,388,608/8,388,609 before token registration/send; invalidation
   before callback-capability acquisition, immediately after it, and during
   cancellation-aware serialization/hash; serializer failure; stale callback
   zero complete body/token/send and no newer-pointer borrowing; and exactly
   one physical header. Missing, malformed, duplicate, combined, unknown, expired, canceled,
   and consumed headers are indistinguishable stage-1 503 with zero body read.
   Declared and streamed/chunked boundary bodies prove the registered byte
   length/SHA-256 binding and mismatch 400 before JSON parse. Interleave token
   consume, producer send completion, expiry cleanup, and generation
   invalidation and prove exactly-once capability release. Wrong method, max-
   feature admission do not parse/consume/remove the header and leave cleanup
   only to producer completion or expiry; prove both races release exactly once.
   Media, size, parser, timeout, caller abort, replay, and send-failure arms
   after consumption release the transferred capability without an orphan,
   body leak, or write. Prove a fresh-token retry with an identical semantic receipt reaches
   the normal receipt result; a callback receives only its callback-owning
   generation and never a newer current pointer; arbitrary loopback callers
   cannot borrow the current generation; and no direct writer or second
   transport exists. Assert token, digest, pending count, generation, and expiry
   are absent from logs, metrics, response, persistence, backup, and request
   fingerprint. Dedicated sender REDs inject HTTP(S)_PROXY/environment proxy,
   cookies/default credentials/auth challenges, DNS/alternate base URI, and
   301/302/303/307/308 targets on a non-loopback capture server; prove only the
   exact already-bound numeric loopback HTTP/1.1 endpoint receives one request,
   no redirect/proxy/resend receives header or body. Run with an active ambient
   `Activity` and assert no `traceparent`, `tracestate`, `baggage`, `Request-Id`,
   or other instrumentation-enriched header. Every non-204/transport
   ambiguity becomes sanitized producer unavailability with fresh-token-only
   retry. CLI/config REDs cover
   0/1/16/17 project flags, 0/1/32/33
   directory flags, counts before dedupe, missing values, all five exact parser
   messages, sanitized-only conflict, invalid-root/platform sanitized startup
   reasons, zero-root route absence, and no supplied path/native fact in any
   error/log. Wrapper REDs cover both exact PowerShell array parameters for
   direct DotnetRun/Published, `-StartNow`, and encoded scheduled startup;
   repeated pair serialization with spaces/quotes; 0/1/16/17 and 0/1/32/33
   counts before deduplication; empty/missing members; sanitized-only conflict;
   no partial process/task; and preservation of an existing task on error.
   Cross every missing/empty/count/sanitized-only fault in differing argument/
   array orders and prove the fixed five-result precedence; cross unsupported
   platform with every invalid-root class and prove platform-unsupported wins.
   Decode the task action in tests to prove exact child argv and count-only
   parser classification, while DryRun/log/state/status/output contain only
   presence/counts and never a root value. Document and test no wrapper-owned
   persistence beyond the unavoidable local process/Task Scheduler argument.
    TestServer and Kestrel max-feature REDs cover missing, read-only,
    setter failure, wrong observed value, a lower general-limit override,
    stage-1-before-origin precedence, zero body read/write on admission failure,
    and every required-service/max failure crossed with shutdown before/during
    root CAS: a selected service/max 503 makes no root attempt and cannot be
    replaced, while service/max success followed by CAS loss aborts with no
    response and cannot emit that 503;
   v2 declared and streamed/chunked 8,388,608 versus 8,388,609 bytes,
   current-file 128 versus 129 bytes, and absence of framework-default 413
   bytes. Then cover raw `{}`, `{"skills":null}`, and
   `{"skills":[]}` deserialization all producing the same nonnull empty public
   list and exact 409; unreadable enumeration/null element or intentionally
   corrupt test-double list producing 503; exact-once call, zero/no-role/
   mixed-role roots, key case/separator/dot/trailing/prefix/nested-root and
   order/duplicate outcomes; same target with differing `Enabled`, `Description`,
   or another DTO fact producing unsafe; a later malformed/null item overriding
    an earlier candidate with discovery-unavailable; generic metadata-policy
    missing/Skill/busy/unavailable branches before content with exact 404/503
    bytes and a mocked zero `ReadContentAsync`/lease/materialization count;
    concurrent Event-type mutation before policy read, after policy read, at
    Retention admission, and before selector/commit, proving the single
    `BEGIN IMMEDIATE` makes every result either pre-change non-Skill under its
    retained grant or post-change 404 and never admits/selects Skill bytes;
    prove no nested transaction, rollback of every uncommitted non-Skill lease,
    and fully buffered access-handle 200 sealing at expiry minus/equal/plus one
    tick. Race item/lease expiry and hidden-handle notification before/during
    selection, commit, hidden publication, and value publication; the narrow
    internal pre-publication buffer never becomes caller-accessible unless every
    fence wins. `lost|busy`/cleanup/abort discards and sends zero status/header/entity;
    #154 current-authorization REDs inject valid acceptance/revocation before
    capture, between capture and capability-lease acquisition, during the sole
    recapture, after capability acquisition but before SDK discovery, during
    native walk/read/re-proof, and before response serialization/completion.
    Prove an already published revoke/absence returns exact 409 with no SDK/
    native work; one pre-lease generation change rechecks, second churn fails
    sanitized 503; publication begun after acquisition blocks until capability
    release; every failed/canceled/unavailable arm releases all leases and
    emits no current-file bytes or registry/path detail. Metadata remains a
    point diagnostic and historical content acquires no registry capability.
    For historical access-grant and current-file operation-grant raw routes,
    interleave Retention grant loss at discovery/result enumeration/native read/re-proof/
    serialization/pre-seal and at lease-expiry minus one tick, exact expiry,
    and plus one tick. A grant loss/cancellation authoritatively observed before
    a runtime terminal attempt disposes every handle/capability, discards every
    buffer, never calls runtime `TrySealResponse`, and aborts with zero status/
    header/entity—not 410, 503, 499, or buffered 200. A lost/busy result first
    discovered by the store-backed Retention raw seal after runtime already won
    follows the later required abandon-runtime-seal/no-output matrix. Prove
    store-backed `TryCompleteWithoutRaw` permits a safe error
    only after its exact live-lease/clock/terminal transaction succeeds;
    `TrySealRawResponse` seals only strictly before expiry and permits
    exactly one already-buffered entity/no further read or renewal. Interleave
    every nonraw current-file outcome—`not_discovered`, `unsafe`, `raced`,
    `missing`, `oversized`, `binary`, discovery-unavailable, and local-monitor-
    unavailable—at Retention completion and runtime seal: Retention lost/busy
    aborts, runtime loss substitutes exact discovery-unavailable 503, and both
    wins send the original safe error, with exactly-once releases. For raw
    success, interleave runtime seal first then Retention raw seal: runtime loss
    discards raw and may send its 503 only after Retention nonraw completion;
    Retention loss aborts and abandons any won runtime seal with no output; only
    both wins permit the single response. Cross every #154 not-current/busy/
    unavailable result and runtime `missing_or_mismatched` with Retention completed/
    lost/busy: completion alone sends the fixed pre-runtime safe result, loss/
    busy aborts, all #154/root/Retention resources release once, and no runtime
    capability is fabricated/reacquired or sealed. Cross distinct
    `normal_shutdown_closed` with every Retention terminal result and prove
    cleanup plus zero response, never that 503. Assert no caller timestamp; delay an
    old pre-read/pre-serialization sample across expiry and prove Retention's
    internal sample rejects it;
    prove current-file never renews its operation grant: at the general one-
    minute renewal deadline minus one tick, exact deadline, and plus one tick,
    assert zero renewal calls/writes/publications, one unchanged two-minute
    admission expiry, and one unchanged Retention-owned expiry notification.
    Interleave DiscoverAsync/result enumeration/native read/serialization and
    terminal seal at that original expiry minus one tick, exact expiry, and plus
    one tick; a pre-expiry seal alone may win, while exact/after expiry cancels
    the tagged work and produces the no-response Retention-loss result. Win
    `TryCompleteWithoutRaw` just before expiry, fire the exact-expiry notification
    before runtime `TrySealResponse`/send, and prove the disposed/stale callback
    cannot tag loss or suppress the already-authorized safe result. Prove no
    renewal-vs-seal/cancel race exists for this consumer without changing the
    prerequisite's renewal behavior for other operation consumers;
    inject caller abort, runtime invalidation, and Retention loss singly and in
    every simultaneous pair/all-three at discovery, native, serialization, and
    terminal fences. Prove fixed priority caller -> Retention -> runtime, no
    callback-order/OCE-message mapping, runtime cancel
    never cancels/releases the Retention handle, and the synchronous terminal
    completion may authorize runtime 503 only while the caller remains live.
    Separately begin normal host/root-set shutdown before root service admission,
    after the root lease but before Retention, between Retention and #154, before
    runtime acquisition, and after runtime acquisition. Prove the atomic closure
    blocks a new root lease with a no-response abort; a root-admitted request
    cannot acquire runtime after closure and uses Retention completion only for
    cleanup before the same no-response abort; an already root+runtime-admitted
    request is not canceled, shutdown waits for its ordinary terminal result and
    final release, and no retained root/runtime object is disposed early;
    same-origin/CSRF exact 403, non-405 no-`Allow`, success media/scalar bytes,
    explicit HEAD 405 headers/`Content-Length: 30`/zero entity on TestServer and
    Kestrel plus OPTIONS JSON entity, no GET/HEAD synthesis,
    and both POSTs' exact Content-Type field-count/TryParse/case/OWS/unquoted-
    token/quoted/duplicate/extension/comma matrix. Cover the complete metadata
    state/reason/nullability table: available live/expired crossed with current/
    stale/invalid, independent optional nulls, every live/expired fault class,
    OTel `not_captured` no-row 404, corrupt graph exact 503, and both raw routes'
    readable/expired crossed with available/every fault plus Retention busy;
    inject every pre-grant Retention `SelectorUnavailable` cause—metadata-query/
    shape contradiction, checked date-add overflow, and hidden handle/
    notification/cleanup-resource preparation failure—and every post-grant
    `ConsumptionUnavailable` raw-query/content/mapper fault against an otherwise
    readable available row. Historical and current-file return
    exact `503 local_monitor_ui_unavailable`; generic Session exact
    `503 session_store_unavailable`; raw-local replay its existing unavailable
    result. Pre-grant causes never call a terminal method; post-grant faults hold
    a use ref, zero/discard raw, retain the handle, and send the safe error only
    after `TryCompleteWithoutRaw=completed_without_raw`; expiry/lost/busy at that
    completion aborts with no response and releases once. Separately
    lose the hidden-handle/value publication fence and prove the post-admission
    no-response branch, never lifecycle 410 or selector-unavailable. Inventory
    raw-record exact/arbitrary batch/all/unprocessed-trace/trace-related-through-
    `monitor_spans`/unprocessed-span, full raw-replay provenance graphs, Session,
    sensitive-bundle, and analysis selectors with no new global bounds. Cross
    >30 MiB configured payload, >64 KiB resource attributes, >255 projection
    frontier, >8 MiB generic Session, unbounded owner-valid analysis values, and
    the Skill document formula `84+4*ceil(payload_bytes/3)` through 11,184,896.
    Preserve raw-replay `selection_limit_exceeded|entry_too_large|archive_too_large`,
    sensitive-bundle ID 8..64 and `replay_store_busy`, and no plan/SQLite detail;
    byte-compare
    every literal fixture after fresh serialization. Cover non-ASCII/HTML/
    control/solidus escaping. Cover historical/current exact equal bytes,
    unequal bytes, and a forced equal-hash/different-bytes test double proving
    only byte equality selects `same`. Receiver-only/`--sanitized-only` must have no
    producer or #119 sink, writer, discovery/current-file service, v2 endpoint,
    or any of the three Skill endpoints, with no v1 fallback and zero writes;
    do not assert component-migration absence.
8. Windows gate: local NTFS/ReFS root and every-segment reparse/junction/ADS/
   device/UNC/redirector/share/race/replace/size/UTF-8 matrix on current files;
   exact three NTSTATUS/two Win32 missing arms, root-loss raced, nonregular
   unsafe, and access/sharing/unexpected-status local-monitor 503. Prove
   root-generation admission/drain plus runtime cancellation/terminal sealing,
   no early retained-root disposal, reverse-order
   request-handle disposal on every result/exception/abort, and no cached
   candidate/result/path after completion.
9. Linux gate: kernel 5.8+ `openat2`, returned
   `STATX_MNT_ID|STATX_INO|STATX_TYPE|STATX_MODE` on the root/every relevant fd
   and `STATX_SIZE|STATX_MTIME|STATX_CTIME` wherever consumed; omit each required
   bit independently before/after read and prove sanitized failure, never a
   default value. Preserve device major/minor identity. Cover ext4/xfs/btrfs,
   symlink/magic-link/bind-mount/
   mount-ID/inode/race/replace/size/UTF-8 matrix on current files; candidate
   `ENOENT` missing, root `ENOENT`/`ESTALE` raced, `ENOTDIR|ELOOP|EXDEV` unsafe,
   and every other errno local-monitor 503, plus the same generation/refcount/
   disposal/no-retention matrix. Windows and
   Linux gates may release independently; unsupported targets prove route
   absence while historical routes remain byte-identical.
10. Versioned T0b and final live gates: the same bundled, signed-in client must
    prove each exact r0002 tuple it admits (`1.0.65` and `1.0.75`, protocol 3),
    matching SessionStart identity, prompt-free inventory probe, distinct
    DisabledSkills-frozen execution Session, retained-root-only inventory and
    invocation, exact completion, owned-session post-completion import, and one
    discovery without retaining raw user data/path evidence. An unproved tuple
    is not admitted; no global or externally selected CLI is used.
11. Update and review the derived public workflow only with the implemented
    flags; run Markdown/link checks plus an exact stale search proving no #158
   workflow still names an obsolete dotted discovery configuration-key alias,
   an executable JSON/environment/delimiter-
   list carrier, or treats the wrapper arrays as an independent source, or
    claims current-file availability with zero roots/`--sanitized-only`.
    Then run the repository's pinned full validation and review workflow only
    after all focused gates are green.

## What is decided versus what still requires evidence

No Product Owner choice remains in these eight groups. Exact byte contracts,
identity semantics, failure behavior, storage, ordering, and platform scope can
be promoted now.

Delegated Product Owner authority cannot manufacture engineering evidence that
does not yet exist: a clean tracked spec commit; the ordered integrated
#124-owned Session 14 -> #156-owned carrier/composition closure for #161-owned
direct archive facts -> #161 `local_archive:1` implementation/backup/restore
evidence; the separate Retention implementation/test result; the nonregistered
#119 parser/handoff result; actual Windows/Linux native walker matrices; a
signed-in bundled exact-tuple T0b/final live observation; full repository validation; and code
review. Those are release gates, not open Product Owner choices. If any gate
disproves the pinned contract, stop and return for a new versioned decision; do
not add fallback, compatibility, or backfill.
