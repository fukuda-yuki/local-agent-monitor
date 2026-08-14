# Canvas Session Workspace Interface

Local Monitor v1 disposition: this v1 wire contract remains byte-frozen for
Canvas and existing consumers. It is not the reader for the new
`/api/local-monitor/v1/*` human UI; #133/#134 own that composition and must
reuse these exact identities without widening this schema or adding fallback
binding.

## Scope

This specification freezes the Issue #51 Session foundation for the installed
Local Monitor. It defines Session ingestion, identity and merge rules,
normalized storage, sanitized reads, raw-content reads, and the Canvas capture
boundary. It is additive to the existing OTLP receiver and monitor projection.

Issue #51 supersedes earlier Canvas statements that categorically prohibited a
new telemetry input, schema, API field, or session-to-trace correlation only for
the interfaces and tables defined here. Existing Canvas bounded-action,
loopback, token, raw-data, and `session.send()` constraints remain unchanged.

## Identity And Source Uniqueness

- Local Session, Run, and Event IDs are UUIDv7 strings.
- A native session ID is a source-provided identifier and is never treated as a
  local Session ID.
- Source event uniqueness uses, in priority appropriate to the adapter, the SDK
  event ID, the canonical hash of a Hook event, or the exact OTel trace/span
  identity.
- Repository, workspace, timestamp, tool name, transcript path, and temporal
  proximity are not identity evidence.

Sessions may merge only when at least one exact condition holds:

1. the native session ID is identical;
2. an explicit resume or handoff linkage connects the sessions; or
3. an ingested event and OTel evidence carry the byte-for-byte identical trace
   context.

For Claude v1, condition 3 is reserved but unavailable: the event envelope
contains only optional `trace_id`, not a provenance-bearing complete trace
context. Trace-ID-only evidence never merges Sessions and never becomes
`trace_context` or `exact_linked`. Claude v1 exact linking is limited to
conditions 1 and 2 until a later spec-first DTO adds the complete context.

Repository and timestamp proximity must never merge sessions.
`client_kind` never participates in Session binding or merge. An exact
`gen_ai.conversation.id` may bind/enrich only when it is byte-for-byte equal to
an already-recorded native session ID; this is the identical-native-ID rule,
not a separate heuristic. Otherwise OTel evidence remains `unbound`.

## Completeness

The normalized Session uses exactly one of these values:

| Value | Contract |
| --- | --- |
| `unbound` | OTel-only and not linked to a native session ID. |
| `partial` | A native ID exists, but the lifecycle or input family is incomplete. |
| `rich` | Instruction, lifecycle, and SDK/Hook or OTel evidence exist, but some content or terminal evidence is missing. |
| `full` | Surface-required start-to-end evidence exists, there is no unsupported version or ingest gap, and OTel enrichment is exact-linked. |

Missed events are not reconstructed. In particular, opening Canvas after a
session has already started lowers completeness when earlier evidence was not
captured.

### Completeness input facts and decision order

The `v1` source-capability contract supplies declarations, not inferred Session
facts. The normalizer evaluates native identity; exact trace context and trace
signal; lifecycle/input, content, and terminal evidence; source-version,
ingest, Hook-only, historical-summary, span-kind, schema-drift, and
source-enabled facts. It returns only the four values above and the fixed
reason-code set below. The reasons are de-duplicated and ordered exactly as
listed, never by arrival order:

1. `missing_native_session_id`
2. `missing_trace_context`
3. `trace_signal_disabled`
4. `content_capture_disabled`
5. `unsupported_source_version`
6. `ingest_gap`
7. `hook_only`
8. `historical_summary_only`
9. `unknown_span_kind`
10. `schema_drift_detected`
11. `planned_source_not_enabled`

Status ranks are ordered `unbound < partial < rich < full`. First calculate the
base status: missing native ID is `unbound`; otherwise missing required
lifecycle, input, or SDK/Hook/OTel evidence-family fact is `partial`; otherwise
missing required content, terminal, exact-enrichment, or surface-required
evidence, or an unsupported source version or ingest gap, is `rich`; otherwise
it is `full`. A missing lifecycle/input fact does not introduce a twelfth
reason code.

Every schema reason has exactly one maximum status:

| Reason code | Maximum status | Why it cannot be higher |
| --- | --- | --- |
| `missing_native_session_id` | `unbound` | No native Session can bind the evidence. |
| `missing_trace_context` | `rich` | Exact-linked OTel enrichment is absent. |
| `trace_signal_disabled` | `rich` | Exact-linked OTel enrichment cannot be obtained. |
| `content_capture_disabled` | `rich` | Required captured content is unavailable. |
| `unsupported_source_version` | `rich` | It is an existing #51 full blocker after the `partial` checks. |
| `ingest_gap` | `rich` | With lifecycle/input present it is an existing #51 full blocker; a missing start remains a `partial` base fact. |
| `hook_only` | `rich` | Native Hook evidence may exist, but exact-linked OTel enrichment is absent. |
| `historical_summary_only` | `partial` | Allowlisted summaries cannot establish lifecycle or explicit event input. |
| `unknown_span_kind` | `rich` | The span cannot qualify as required exact enrichment. |
| `schema_drift_detected` | `partial` | Required declared input agreement is not established. |
| `planned_source_not_enabled` | `unbound` | A disabled planned source supplies no observed native Session input. |

For a valid fact/reason combination, final status is the minimum rank of the
base status and every present reason maximum. An unknown reason is invalid
schema drift and must be rejected, never ignored. Reasons are de-duplicated
and emitted in the canonical schema order above, never arrival order. Thus an
`unbound` base plus a `rich` reason remains `unbound`, and a `partial` base plus
a `rich` reason remains `partial`; `historical_summary_only` can never reach
`full`. `historical_summary_only` and `schema_drift_detected` are future
adapter-handoff `partial` reasons with no distinct current #51 calculator
boolean; they must not be conflated with `unsupported_source_version`.

This calculation has no heuristic merge and creates no synthetic span. Missing
required provenance maps only to fixed reasons: missing event/trace-span
identity to `missing_trace_context`, missing capture/content state to
`content_capture_disabled`, and missing adapter/version/fingerprint/
normalization version to `schema_drift_detected`.

The contract is an adapter handoff requirement. Task 15 adds only the v1 ingest
envelope provenance fields defined below; it does not change sanitized read
DTOs, the raw-content route, Session/Run/Event identity, or Issue #49 Agent
ownership.

Terminal evidence for completeness is exactly a persisted Session Event with
non-null `terminal_outcome` and `terminal_policy_version`. A `neutral` fact is
terminal evidence for completeness. Event type, event or child status, error
text, trace/span status, and content availability are not terminal-evidence
substitutes. After every ordinary write and during v13-to-v14 migration, the
Session owner reruns the existing durable completeness calculator over the full
persisted Session evidence and supplies `terminal_evidence = facts.Any()`.
The calculator, rank/reason order, and every other input remain unchanged.

## Session Outcome Aggregation

Session schema component version `14` owns the private terminal-fact and
aggregate contract in this section. All comparisons are ordinal and
case-sensitive. Adapter, surface, event `type`, root discriminator name, and
root discriminator value must match exactly. Nested values, aliases, summary
text, names, status/error text, child state, timing proximity, and the prior
`sessions.status` are never consulted.

### Source-scoped terminal policy v1

| Exact `source_adapter` | Exact legal `source_surface` | Exact event `type` | Exact evidence | Persisted `terminal_outcome` |
| --- | --- | --- | --- | --- |
| `copilot-sdk-stream` | `copilot-sdk` | `session.task_complete` | Type alone | `clean` |
| `copilot-sdk-stream` | `copilot-sdk` | `session.shutdown` | Root `shutdownType` is `routine` | `clean` |
| `copilot-sdk-stream` | `copilot-sdk` | `session.shutdown` | Root `shutdownType` is `error` | `failed` |
| `copilot-sdk-stream` | `copilot-sdk` | `session.shutdown` | `shutdownType` is absent, unavailable, duplicated, non-string, or any other string | `neutral` |
| `copilot-compatible-hook` | one of `copilot-cli`, `vscode`, `hook-unknown` | `SessionEnd` | Root `reason` is `complete` or `user_exit` | `clean` |
| `copilot-compatible-hook` | one of `copilot-cli`, `vscode`, `hook-unknown` | `SessionEnd` | Root `reason` is `error` or `timeout` | `failed` |
| `copilot-compatible-hook` | one of `copilot-cli`, `vscode`, `hook-unknown` | `SessionEnd` | `reason` is `abort`, absent, unavailable, duplicated, non-string, or any other string | `neutral` |
| `claude-code-hook` | `claude-code` | `SessionEnd` | Root `reason` is `clear`, `resume`, `logout`, or `prompt_input_exit` | `clean` |
| `claude-code-hook` | `claude-code` | `SessionEnd` | Root `reason` is `bypass_permissions_disabled`, `other`, or any other admitted string; during migration only, an already-admitted legacy Event has absent, unavailable, duplicated, non-string, invalid-JSON, or non-object discriminator content | `neutral` |

A discriminator is read only from the one exact root JSON property. The Claude
Hook mapping remains required at live admission: absent, duplicated,
non-string, wrong-content-kind, invalid-JSON, or non-object root `reason`
rejects the producer event through the existing invalid-mapping path, admits no
Event, and persists no fact. An admitted string outside the clean set is
`neutral`. During v13-to-v14 migration only, an already-admitted legacy
SessionEnd whose discriminator content is absent, unavailable, duplicated,
non-string, invalid-JSON, or non-object is `neutral`; migration never treats
that fallback as proof that a new live event was valid. No path falls back to a
legacy event-name heuristic. Other root properties do not participate.
`session.task_complete` is `clean` without a content read.

Every other retained event has no terminal fact. This includes
`PostToolUseFailure`, `Stop`, `StopFailure`, `subagent.failed`, child
Run/Event/Trace/Span errors, exception or error text, and every
`claude-code-otel` event. An error outside the table cannot synthesize a failed
Session. In particular, the recoverable `PostToolUseFailure` signal and the
nonterminal `subagent.failed` lifecycle event remain ordinary evidence.

### Immutable fact schema and reducer

In exact Session schema `14`, `session_events` appends these columns immediately
after `match_kind`:

```sql
terminal_outcome TEXT NULL,
terminal_policy_version INTEGER NULL
```

The table has exactly this additional CHECK:

```sql
CHECK (
    (terminal_outcome IS NULL AND terminal_policy_version IS NULL)
    OR
    (
        typeof(terminal_outcome) = 'text'
        AND terminal_outcome IN ('clean', 'failed', 'neutral')
        AND typeof(terminal_policy_version) = 'integer'
        AND terminal_policy_version = 1
    )
)
```

The pair is either both null or the exact v1 fact pair, including SQLite
storage classes. No new table, view, generated column, named index, trigger,
wire DTO, or public field is added. Each accepted in-memory event is classified
before secret filtering or optional content retention, and its event row and
final fact are committed atomically. Production has no fact update/delete API.

For the complete durable event set of one Session, reduction is exact:

```text
facts = events where terminal_outcome is non-null
status = failed     if any fact is failed
         completed  else if any fact is clean
         unknown    else if any fact is neutral
         active     else
ended_at = null     if facts is empty
           UTC-O(max parsed facts.occurred_at) otherwise
```

`UTC-O` is invariant round-trip UTC with seven fractional digits. Every fact
participates in the maximum, including neutral facts and outcomes that lose the
status lattice. A later neutral fact can therefore advance `ended_at` without
changing a completed or failed status. Equal instants produce identical bytes.
An empty fact set, including a zero-event, OTel-only, Stop-only, or error-only
Session, always produces `active` and null `ended_at`; prior status and prior
ended time are never consulted or preserved.

Normal writes run inside the existing `BEGIN IMMEDIATE` transaction: validate
and classify the batch; merge non-outcome Session metadata; resolve exact event
identities and compare replays; insert new event/content/fact rows; reduce all
durable facts for every affected Session and assign exact status and ended time;
then rerun the existing durable completeness calculator with terminal evidence
equal to any persisted fact before commit. Late delivery follows only the
lattice: clean then failed becomes failed, failed then clean remains failed,
neutral then clean becomes completed, and every later terminal instant may
advance `ended_at`. Arrival order, batch split, and exact duplicate delivery
cannot change final bytes.

### Replay, retention, and downstream gates

Replay identity remains exact `(source_adapter, source_event_id)`. First resolve
the incoming caller-local Event ID and parent reference to their canonical local
IDs under the existing identity rules. Then compare this exact vector:

```text
event_id, session_id, run_id, source_surface, parent_event_id, trace_id,
status, source_adapter, source_event_id, type, occurred_at, content_state,
source_application_version, adapter_version, schema_fingerprint,
normalization_version, match_kind, terminal_outcome, terminal_policy_version
```

Both incoming and durable `occurred_at` values pass through the existing
canonical UTC seven-fractional-digit formatter before exact equality. All
nullable strings and IDs use ordinal equality after canonical resolution.
Exact replay is a complete no-op. Any
vector mismatch aborts the whole batch and transaction; there is no
first-writer/last-writer choice, merge, repair, or reclassification. The same
comparator applies to `claude-code-otel`; its former owner-only replay shortcut
is not sufficient. Same-batch duplicate source identities resolve against the
first canonical candidate and must compare exactly. A mixed batch commits only
when every database or same-batch replay is exact.

Session Event content remains outside this event/fact comparator and preserves
its existing separate ownership/conflict contract. An exact replay never
creates or backfills a missing content row, replaces content, or reclassifies a
fact. Where both sides already have content, the existing content conflict
checks remain authoritative and any mismatch aborts the transaction. Existing
writer failures retain the frozen `503` /
`{ "error": "session_store_busy" }` response.

Retention expiry or deletion removes only authorized content. It never deletes
or rewrites a fact, Session status, terminal time, or historical downstream
receipt/estimate. Completeness, proposal, and cost terminal lookup is scoped by
the exact `session_id` and uses any persisted non-null fact, not a raw event-type
list. Run/Trace scope participates only where an existing objective/evidence DTO
already carries those fields; PO124-A adds no Run/Trace field to proposal DTOs.
A neutral fact can satisfy completeness but leaves the Session `unknown`.
Current proposal, objective, comparison, and cost eligibility always revalidates
exact-bound status `completed` or `failed`, completeness `full`, and a
Session-scoped non-null fact. `active`, `unknown`, and non-full Sessions are
ineligible. `Stop` cannot unlock any terminal gate.

### Exact v13-to-v14 transition and current validation

The Session owner performs one atomic `13 -> 14` migration in the established
schema-initialization envelope. Because `session_events` is a referenced parent,
the connection sets `PRAGMA foreign_keys=OFF` while still in autocommit and
before `BEGIN IMMEDIATE`; changing the pragma inside a transaction is forbidden.
It then requires one exact version-13 component row and exact v13 Session
profile before mutation; partial, mixed, future, or extra-object shapes fail
closed. The migration rebuilds `session_events` with the exact column order and
CHECK above while preserving every legacy event and every child/descendant row
byte-for-byte, including `session_event_content`, Retention catalog/receipt
state, and installed Skill projection descendants, including production-valid
OTel invocation/inventory claim rows. `skill_projection_sdk_claims` remains
empty until the #158 writer is promoted, as required by the Skill projection
authority.

It verifies source/destination event counts, canonical IDs, all child joins,
and every copied legacy value before classification, then enumerates every event
in ordinal `event_id` order and applies the v1 classifier. For every Session,
including zero-event Sessions, it overwrites `status` and `ended_at` from the
fact reducer and recomputes `completeness` through the existing durable
calculator with terminal evidence equal to any fact. No other Session value,
including `created_at` or `updated_at`, changes merely because migration ran.
It then verifies the exact v14 profile, tuple/outcome domains, aggregates,
completeness, row counts, copied legacy/descendant values, and
`PRAGMA foreign_key_check`. It stamps component version `14` last and reruns
current-v14 validation before commit.

Any failure rolls back schema, facts, aggregates, completeness, descendants,
and stamp together. In a `finally` path after commit or rollback, the connection
returns to autocommit, executes `PRAGMA foreign_keys=ON`, and verifies the pragma
is enabled; failure to restore enforcement fails initialization. Fresh
databases create exact v14 directly. Versions `1..12` traverse the existing
migrations to exact v13 and then this v14 step in the same initialization
transaction whose foreign-key pragma was disabled before `BEGIN`.

Migration reads discriminator content only through a Retention-owned helper on
the already-open connection/transaction and one `migration_now` captured from
the injected `TimeProvider` before classification; system time is not a
fallback. The helper opens no connection, acquires no lease, advances no
Retention state, and reads nothing after the transaction. It returns content
only when all of these agree: exactly one available `application/json` content
row; the current Retention v1 schema; exact coverage-version-1 rows for
`session_event_content`, `raw_record`, `analysis_run_raw`, `sensitive_bundle`,
and `analysis_sdk_directory`; one valid store singleton; exactly one matching
catalog row for that singleton with `store_kind=session_event_content`,
`source_item_id=event_id`, receipt/coverage version `1`, lifecycle state
`expiring` or `retained_by_policy`, and null `read_denied_at`; one 32-byte owner
token; and the recomputed Session ownership receipt. For `expiring`, both
catalog and content expiry must be strictly later than `migration_now`. For
`retained_by_policy`, the authorized pinned item remains readable regardless of
its original past expiry; migration must not demote it to unavailable. No
Retention component or ordinary absent/expired/denied/deleted/unavailable
content yields `neutral` for an already-admitted legacy discriminator-bearing
event.
Malformed or partial Retention schema, duplicate identity/singleton, invalid
coverage/token, receipt contradiction, or a malformed recognized terminal
`occurred_at` is corruption and rolls back; there is no direct-content fallback
or second attempt.

The exact current-v14 validator checks column/affinity/nullability/key/FK/CHECK
semantics and reserved objects, every fact pair/domain/version, canonical fact
timestamps, and this exact tuple/outcome matrix: SDK `session.task_complete` is
`clean` only; SDK `session.shutdown` permits `clean|failed|neutral`;
Copilot-compatible `SessionEnd` permits `clean|failed|neutral`; Claude Hook
`SessionEnd` permits `clean|neutral` and never `failed`; every other tuple,
including all Claude OTel, has the null pair. Thus a failed task-complete or
failed Claude SessionEnd is corruption even though `failed` is in the global
domain.

For every Session, the validator independently recomputes exact
status/`ended_at` and invokes the existing durable completeness calculator with
terminal evidence equal to any fact, then requires persisted `completeness` to
match. It does not reopen content, reclassify, rewrite, or repair. Aggregate or
completeness drift, or a contradictory current fact, fails startup before
readiness. A second open of a valid v14 database is select-only and
byte-idempotent.

Required migration/reducer fixtures include content-bearing terminal events,
Retention catalog/receipt descendants, installed Skill projection descendants
with Session-bound OTel invocation/inventory claims, pinned
`retained_by_policy` content whose original expiry is past,
all tuple/outcome invalid pairs, and a Session whose otherwise-full evidence
contains only `Stop`; the latter must lose terminal evidence and must not remain
`full`.

Required replay fixtures vary each of the 17 legacy event columns and each fact
column independently, cover canonical timestamp equality and inequality,
caller-local Event/parent resolution, database and same-batch duplicates,
mixed-batch rollback, the Claude OTel comparator, and the separate content
no-backfill/conflict rule. Migration tests reopen the same valid v14 database
and require byte-idempotent select-only validation.

The public Session list/detail/status wire shape, property order, routes,
headers, enum strings, status codes, fixed error representations, monitor
health/SSE bytes, and content-state vocabulary remain frozen. The two private
fact columns are never serialized.

## Session Event Ingest

The installed Local Monitor exposes:

```text
POST /api/session-ingest/v1/events
```

The request requirements are:

- loopback Local Monitor boundary and Host-header validation;
- `Content-Type: application/json`;
- custom version header `X-CAO-Session-Event-Version: 1`;
- request body at most **1 MiB (1048576 bytes)**;
- `events` batch length from **1 through 100**, inclusive;
- schema version `1` only;
- `source_adapter` is `copilot-sdk-stream`, `copilot-compatible-hook`, or
  `claude-code-hook`;
- `source_surface` is `copilot-sdk`, `copilot-cli`, `vscode`,
  `hook-unknown`, or `claude-code`.

The v1 envelope has these fields:

| Field | Required | Contract |
| --- | --- | --- |
| `schema_version` | yes | Integer exactly `1`. |
| `source_adapter` | yes | `copilot-sdk-stream`, `copilot-compatible-hook`, or `claude-code-hook`. |
| `source_surface` | yes | `copilot-sdk`, `copilot-cli`, `vscode`, `hook-unknown`, or `claude-code`. |
| `native_session_id` | yes | Nonblank string, 1..256 characters. |
| `events` | yes | JSON array with 1..100 entries. |
| `explicit_link` | no | The sole v1 wire representation of explicit resume/handoff linkage; shape below. |
| `source_application_version` | conditional | JSON null or an adapter-generated metadata token. Required for `claude-code-hook` when `schema_fingerprint` is absent; legacy Copilot envelopes may omit it. |
| `adapter_version` | conditional | Adapter-generated metadata token. Required for `claude-code-hook`; legacy Copilot envelopes may omit it. |
| `schema_fingerprint` | conditional | JSON null or exactly 64 lowercase hexadecimal characters. Required for `claude-code-hook` when `source_application_version` is absent; legacy Copilot envelopes may omit it. |
| `normalization_version` | conditional | Adapter-generated metadata token. Required for `claude-code-hook`; legacy Copilot envelopes may omit it. |

An adapter-generated metadata token matches
`^[A-Za-z0-9][A-Za-z0-9._+-]{0,255}$`. The receiver never derives these fields
from `payload`, content, a path, prompt/response text, tool input/output, or an
exception. Control characters, whitespace, path separators, URI separators,
and other free-form text are invalid. The four envelope values are copied
unchanged to every accepted event in the batch. They do not participate in
Session/Event IDs, binding, ownership, or content storage.

For `claude-code-hook`, `adapter_version` and `normalization_version` are
required and at least one of `source_application_version` or
`schema_fingerprint` is required. `claude-code-otel` is not valid on this
endpoint and remains an OTLP `/v1/traces` adapter. The composite registry label
`claude-code-otel+claude-code-hook` is never a persisted adapter value.

`explicit_link`, when present, is exactly:

```json
{
  "source_surface": "copilot-sdk|copilot-cli|vscode|hook-unknown|claude-code",
  "native_session_id": "nonblank 1..256 characters",
  "kind": "resume|handoff"
}
```

No other v1 field or inferred relationship represents explicit linkage.

The v1 envelope example is:

```json
{
  "schema_version": 1,
  "source_adapter": "claude-code-hook",
  "source_surface": "claude-code",
  "native_session_id": "claude-session-example",
  "source_application_version": "2.1.207",
  "adapter_version": "claude-hook-v1",
  "schema_fingerprint": null,
  "normalization_version": "session-normalization-v1",
  "explicit_link": {
    "source_surface": "claude-code",
    "native_session_id": "prior-claude-session",
    "kind": "resume"
  },
  "events": [
    {
      "source_event_id": "event-1",
      "type": "session.started",
      "occurred_at": "2026-07-11T10:00:00+09:00",
      "payload": {}
    }
  ]
}
```

Each v1 event has these fields:

| Field | Required | Contract |
| --- | --- | --- |
| `source_event_id` | yes | Nonblank string, 1..256 characters. |
| `type` | yes | String matching `^[A-Za-z][A-Za-z0-9._-]{0,127}$`. |
| `occurred_at` | yes | ISO-8601 timestamp with an explicit offset. |
| `payload` | yes | JSON object; scalar, array, and null are invalid. |
| `parent_event_id` | no | JSON null or a string 1..256 characters. |
| `run_native_id` | no | JSON null or a string 1..256 characters. |
| `trace_id` | no | JSON null or a string 1..128 characters. |

`copilot-sdk-stream` is valid only with source surface `copilot-sdk`.
`copilot-compatible-hook` is valid only with `copilot-cli`, `vscode`, or
`hook-unknown`. Adapter/surface mismatch is
`400` / `invalid_session_event_request`.

Event-type support uses exact ordinal matching. The supported v1 set is:
`capture.started`, `assistant.usage`, `session.usage_info`, `session.start`,
`session.started`, `session.shutdown`, `session.task_complete`, `user.message`,
`assistant.message`, `assistant.turn_end`, `tool.execution_start`,
`tool.execution_complete`, `subagent.started`, `subagent.completed`,
`subagent.failed`, `subagent.selected`, `subagent.deselected`, `skill.started`,
`skill.completed`,
`SessionStart`, `UserPromptSubmit`, `PreToolUse`,
`PermissionRequest`, `PostToolUse`, `PostToolUseFailure`, `SubagentStart`,
`SubagentStop`, `Stop`, `StopFailure`, and `SessionEnd`. `skill.invoked` is
unsupported and follows the existing unsupported-event behavior. This exact
membership correction changes no other v1 wire, enum, limit, status, error
entity, `204`, or workspace response byte.

Supported non-usage events are stored with `content_state=available`, and their
payload is secret-filtered into `session_event_content`. `assistant.usage` and
`session.usage_info` are stored with `content_state=not_captured` and no content
row. The existing five-value `content_state` vocabulary is unchanged.

For exact event type `subagent.completed`, the only secret-key exception is one
direct, ordinal case-sensitive root property named `totalTokens`. A candidate
qualifies only when the outgoing and received payload each contain exactly one
such occurrence, represented by an original JSON number token matching
`0|[1-9][0-9]*` in the inclusive range `0..2147483647`. Canvas admits only a
finite JavaScript integer in that range, rejects negative zero, and emits the
canonical unsigned-decimal token. It omits an invalid candidate before the
POST; the receiver independently revalidates the raw JSON token and omits every
candidate occurrence on any value or cardinality failure. Missing and rejected
values remain absent, valid zero is retained as numeric `0`, and the remaining
valid event and safe payload fields are preserved. Wrong event, case, depth, or
similar names do not qualify, and all other recursive secret-key removal and
credential-value redaction is unchanged.

An unknown but syntactically valid event `type` is stored with normalized status
`unsupported`, increments `unsupported_event_version_count`, and prevents
`full` completeness. The normalizer must not guess a mapping. Event `payload`
is raw-bearing local runtime data and is not returned by sanitized reads.

The endpoint returns `204` only after the complete batch is committed. A
rejected or failed batch does not report success. Error responses use only the
fixed shape:

```json
{ "error": "<code>" }
```

The fixed failure mapping is:

| Status | Error code | Condition |
| ---: | --- | --- |
| `400` | `invalid_session_event_request` | Invalid v1 request other than an unsupported version. |
| `400` | `unsupported_session_event_version` | Unsupported header or body schema version. |
| `413` | `request_too_large` | Body exceeds 1 MiB. |
| `415` | `unsupported_media_type` | Request is not JSON. |
| `503` | `session_event_queue_full` | Session event queue is full. |
| `503` | `session_store_busy` | SQLite remains busy after retry. |
| `504` | `session_event_commit_timeout` | Batch commit does not finish before the commit timeout. |

Responses and logs must not echo payload content, credentials, PII, local paths,
or raw exception messages.

## Installed Hook Forwarder

The installed Local Monitor provides this mode:

```text
hook-forward --endpoint <loopback-url> --timeout-ms 250 \
  [--source claude-code \
    [--source-version <metadata-token>] \
    [--schema-fingerprint <64-lowercase-hex>]]
```

It reads exactly one JSON payload from stdin and forwards it to the Session
event ingest endpoint. It always exits `0` for invalid input, network failure,
or timeout, writes nothing to stdout or stderr, and never influences the agent
Hook decision. The endpoint must be loopback. No permissive alternate parser,
retry path, or agent-decision fallback is added.

Omitting `--source` selects the existing Copilot Hook mode and preserves its
no-new-argument behavior. The only accepted selector is the exact pair
`--source claude-code`; it selects Claude mode before stdin is interpreted.
The forwarder never infers the source from Hook payload shape. Provenance flags
are valid only in Claude mode. A provenance flag without the selector, a
missing selector value, any selector value other than `claude-code`, or a
duplicate selector is invalid input.
The selector and provenance option pairs may each appear at most once and may
appear in any option order; existing endpoint and timeout parsing is unchanged.

Claude mode requires at least one of `--source-version` or
`--schema-fingerprint`. Both use the exact Session provenance validation above.
The value is supplied out-of-band from the actually emitting Claude
installation or an approved Hook schema fingerprint. The forwarder never
derives it from Hook payload/content, documentation/fixture labels, or
inventory-only executable evidence. Missing or invalid Claude provenance means
the payload is not forwarded; if both values are supplied, either invalid value
invalidates the command rather than being ignored. Fail-open exit `0` and silent
stdout/stderr remain unchanged. Adapter and normalization versions remain
installed adapter constants, not CLI input.

The command acceptance matrix is exact:

| Mode and arguments | Result |
| --- | --- |
| `--source` omitted; no provenance flags; valid Copilot payload | Preserve the existing Copilot envelope and forward once. |
| `--source claude-code` plus valid `--source-version` only | Forward one Claude envelope with the version copied unchanged and null fingerprint. |
| `--source claude-code` plus valid `--schema-fingerprint` only | Forward one Claude envelope with null application version and the fingerprint copied unchanged. |
| `--source claude-code` plus both valid provenance flags | Forward one Claude envelope with both values copied unchanged. |
| Claude selector with neither provenance flag | Do not forward; exit `0` with empty stdout/stderr. |
| Provenance flag without the Claude selector | Do not forward; exit `0` with empty stdout/stderr. |
| Missing selector value, unknown or duplicate selector, duplicate provenance option, or any supplied invalid provenance value | Do not forward; exit `0` with empty stdout/stderr. |
| Valid mode metadata with invalid stdin, non-loopback endpoint, invalid timeout, network failure, or timeout | Keep the existing fail-open/silent behavior; network and timeout receive no retry. |

Every forwarded Claude envelope uses `source_adapter = claude-code-hook`,
`source_surface = claude-code`, header/schema version `1`, and installed
adapter/normalization constants. Payload fields that resemble a source,
version, or fingerprint never select the mode or populate provenance.

GitHub Copilot CLI and VS Code use the same PascalCase Hooks contract.
Ambiguous Hook input is recorded as `hook-unknown`; its surface must not be
inferred from environment variables, repository metadata, tool names,
transcript paths, or timestamps. OTel `client_kind` may only confirm whether
`hook-unknown` is `copilot-cli` or `vscode`; it is never combined with
conversation ID for Session binding or merge.

## Canvas And SDK Capture

- App/SDK capture uses Canvas `ctx.sessionId` as the native session ID.
- Persisted SDK events are stored through the Session subsystem.
- Canvas applies the bounded `subagent.completed` `totalTokens` admission above
  before enqueueing or posting an event; receiver validation remains
  independent.
- Ephemeral usage is aggregated rather than persisted as event content.
- Reasoning and streaming deltas are not persisted.
- Capture begins at the first Canvas open. Earlier events are not reconstructed;
  their absence lowers completeness.

Issue #51 does not change the Issue #45 `session.send()` execution behavior or
transfer execution ownership to Local Monitor. Issue #49 Agent ownership
semantics are also unchanged.

## Sanitized Workspace Reads

All endpoints in this section return sanitized metadata and never return event
`payload` or raw content. Every field defined as nullable or optional in a v1
response is present with JSON `null`, not omitted.

```text
GET /api/session-workspace/sessions?limit=<1..200>
GET /api/session-workspace/sessions/{sessionId}
GET /api/session-workspace/resolve?source_surface=<enum>&native_session_id=<urlencoded>
GET /api/session-workspace/status
```

`GET /api/session-workspace/sessions` defaults `limit` to `50`, orders items
most-recent-first, and returns `{ "items": [...] }` only. Version 1 has no
pagination and no additional filters. Each list item has exactly these fields:

| Field | Contract |
| --- | --- |
| `session_id` | Local UUIDv7 string. |
| `status` | `active`, `completed`, `failed`, or `unknown`. |
| `completeness` | `unbound`, `partial`, `rich`, or `full`. |
| `completeness_reason_codes` | Canonically ordered Issue #61 reason-code array. |
| `source_surfaces` | Array of source-surface enum values. |
| `source_diagnostic` | Additive sanitized object defined by `source-schema-drift-claude-code.md`, or `null` when no observation is linked. |
| `binding_state` | `hook_only`, `otel_only`, or `exact_linked`. |
| `content_state` | Nullable aggregate capture state defined by `source-schema-drift-claude-code.md`; never a UI-derived fallback. |
| `repository` | Nullable string. |
| `workspace` | Nullable string. |
| `started_at` | Nullable ISO-8601 timestamp. |
| `ended_at` | Nullable ISO-8601 timestamp. |
| `last_seen_at` | ISO-8601 timestamp. |
| `raw_retention_state` | `expiring`, `expired_pending_deletion`, or `not_captured`. |

An invalid or out-of-range `limit` returns `400` with
`{ "error": "invalid_session_workspace_query" }`.

`GET /api/session-workspace/sessions/{sessionId}` returns exactly five
top-level fields (`human_evaluation` is the Issue #52 additive amendment; see
`canvas-session-workspace-ui.md`):

- `session`: the exact additive list-item shape above;
- `human_evaluation`: JSON `null`, or `{ "verdict": "expected"|"problem",
  "recorded_at": "<ISO-8601>" }` recorded through the Issue #52
  human-evaluation endpoint;
- `native_ids`: entries with `source_surface`, `native_session_id`,
  `binding_kind` (`native`, `explicit_resume`, `explicit_handoff`, or
  `trace_context`), and `observed_at`;
- `runs`: entries with `run_id`, `source_surface`, `native_run_id`, `trace_id`,
  `parent_run_id`, `model`, `status`, `started_at`, `ended_at`, `input_tokens`,
  `output_tokens`, and `total_tokens`;
- `events`: entries with `event_id`, `run_id`, `source_surface`, `type`,
  `occurred_at`, `parent_event_id`, `status`, and `content_state` (`available`,
  `not_captured`, `redacted`, `unsupported`, or
  `expired_pending_deletion`).

The detail response does not return `payload`. Fields described as nullable or
optional in the v1 response contract are present with JSON `null`, not omitted.
This applies to absent run native/trace/parent/model/timestamps/token
values and absent event run/parent/status values.

An invalid `sessionId` UUID returns `400` with
`{ "error": "invalid_session_id" }`. A valid but missing Session returns `404`
with `{ "error": "session_not_found" }`.

`GET /api/session-workspace/resolve` returns one of:

```json
{ "binding_status": "bound", "session_id": "...", "completeness": "unbound|partial|rich|full" }
```

with `200`, or:

```json
{ "binding_status": "unbound" }
```

with `404`. `source_surface` uses the ingest surface enum. Resolution follows
the exact identity and merge rules above and never uses repository or timestamp
proximity.
`source_surface` must be an enum value and `native_session_id` must be a
URL-encoded nonblank string of 1..256 characters.
An invalid resolve request returns `400` with
`{ "error": "invalid_session_resolution_request" }`.

`GET /api/session-workspace/status` returns `schema_version`,
`normalizer_status` (`ready` or `degraded`),
`unsupported_event_version_count`, `projection_cursor`, and
`projection_backlog`. `schema_version` is the integer `1`;
`unsupported_event_version_count` and `projection_backlog` are nonnegative
integers; `projection_cursor` is JSON null or a nonnegative integer.

## Improvement Proposal Amendment

Issue #54 adds the proposal collection and mutation routes defined in
`canvas-improvement-proposals.md`. They are additive to this workspace API:
they do not change the list-item or Session-detail shapes above, Session
identity/merge rules, completeness, raw-content routes, or the Issue #45
`session.send()` behavior. Proposal text and evidence references are sanitized
local-runtime metadata; raw event content remains available only through the
separate raw event-content route below.

## Proposal Apply Amendment

Issue #55 adds the separately token-gated Canvas-helper and Local Monitor
apply contract in `canvas-proposal-apply.md`. It consumes an existing Issue #54
proposal but does not alter Session identity, Session list/detail shapes, raw
content routes, ingest semantics, or the proposal lifecycle. Apply drafts,
approval, snapshots, and audit are local runtime data; only opaque proposal /
Session references and sanitized state are workspace metadata.

## Effect Comparison Amendment

Issue #56 adds the objective-evaluation, application-receipt, candidate, and
effect-comparison routes defined in `canvas-effect-comparison.md`. Objective
evidence must bind to an existing Session/Run/trace and does not change Session
identity, merge, completeness, ingest, list/detail shapes, or raw-content
retention. Comparison cohort and effect rows are sanitized local-runtime
metadata; event payloads and raw trace content remain outside this interface.

## Raw Event Content Read

The raw-bearing content route is:

```text
GET /sessions/{id}/events/{eventId}/content
```

An available content response has this shape:

```json
{
  "event_id": "...",
  "content_kind": "...",
  "content": "...",
  "captured_at": "...",
  "expires_at": "..."
}
```

The route is same-origin, uses `Cache-Control: no-store`, and is absent under
`--sanitized-only` (`404`). Unknown Session/Event content also returns `404`.
Under `--sanitized-only` the raw route is unregistered: same-host fallback is
the immutable `404` JSON `{"accepted":false,"error":"unsupported_endpoint","message":"Only /v1/traces is supported."}` with `application/json`, no BOM/newline,
and no Cache-Control/ETag/Last-Modified. This is not a Session DTO response.
After expiry it returns `410` with:

```json
{ "error": "raw_content_expired", "content_state": "expired_pending_deletion" }
```

Raw content is secret-filtered before separate storage and receives
`expires_at = captured_at + 90 days`. Retention catalog v1 is the read and
physical-cleanup authority: every captured item has an exact catalog identity.
Ordinary content reads follow the shared Retention read authority in
[Raw Store And Normalization](../layers/raw-store-normalization.md): admission
evaluates current row readability, pinned items stay readable regardless of
their historical original expiry, and an admitted grant keeps its bounded
grace across an expiry boundary. Operation-heartbeat validation alone rechecks
current authority, and only a validated due grant renews. Expiry denies a new
read at admission before the item is queued for deletion. Session v1
is a frozen lossy projection: `expiring` and `retained_by_policy` map to
`expiring`; every denied catalog lifecycle maps to
`expired_pending_deletion`; no captured item maps to `not_captured`. The route's
existing enum, status codes, JSON property names, and exact UTF-8 404/410 bytes
remain unchanged. Pin, unpin, and delete-now remain Issue #90 scope.

For a granted non-Skill raw-content read, the Session owner retains the exact
access handle and every content-buffer use reference through complete response
serialization. It calls the shared Retention `TrySealRawResponse()` strictly
before response start; only `sealed` may send the unchanged 200 entity.
A post-grant content/query/mapper contradiction first discards content, closes
its use references, and calls `TryCompleteWithoutRaw()`; only
`completed_without_raw` may send the exact
`503 {"error":"session_store_unavailable"}` entity. A terminal `lost|busy` or
caller abort discards the buffered entity, closes every use reference, starts no
status, header, or entity, and closes the transport. Exact Event missing,
`skill.invoked`, and lifecycle-denied 404/410 remain entirely before lease/
content selection with their existing bytes. Pre-grant SQLite `Busy` keeps the
exact `503 {"error":"session_store_busy"}` entity; pre-grant
`SelectorUnavailable` uses the exact
`503 {"error":"session_store_unavailable"}` entity without a terminal call.
Committed hidden-handle or value-publication `LeaseLost|Busy` is post-admission
and takes only the zero-response discard/close branch. The allowed non-Skill
branch is the sole metadata-only-admission exception: its uncommitted lease and
inaccessible content selection precede the final Retention clock/item/source/
type recheck; failure rolls back that lease and discards the buffer. No
disposal-only or current-row recheck is response authority.

## Issue #90 Compatibility Note

Issue #90 adds no field, enum, status code, or route change to workspace v1.
Its catalog projection maps readable unpinned `expiring` and readable pinned
`retained_by_policy` items to the existing available/expiring projection;
workspace v1 does not reveal pin state. A confirmed `delete_now` immediately
projects the selected denied content through the exact existing `410` contract
after the atomic catalog commit, before the #89 worker performs physical
deletion. The existing workspace v1 response bytes remain frozen.

## OTel Enrichment

OTel enrichment runs after the existing monitor projection and uses a dedicated
cursor in `session_projection_state`. It may bind or enrich a Session from a
byte-for-byte trace-context match already recorded on an event. Exact
`gen_ai.conversation.id` may bind/enrich only when byte-for-byte equal to an
already-recorded native session ID. Otherwise the OTel evidence remains
`unbound`. `client_kind` never participates in Session binding or merge; it may
only confirm whether an ambiguous `hook-unknown` surface is `copilot-cli` or
`vscode`. An ingest gap, unsupported event version, inexact OTel linkage, or
missing surface-required evidence prevents `full` completeness.

The existing OTLP receiver, trace/span schema, monitor projection cursor, and
readiness contract are unchanged. Session schema migration runs during startup;
failure fails Local Monitor host construction, matching analysis-store startup
migration behavior. It does not add or alter readiness body fields, thresholds,
units, configuration names, or HTTP status mapping. Session normalization is
not added to `RawTelemetryStore.cs`.

## Pre-UI Gate And Non-Goals

Before any Issue #51 Session UI implementation, Issue #52 must capture the
current screen and obtain approval for the four-tab prototype. This is a
mandatory gate, not optional design evidence.

Issue #51 does not add direct apply, Compare, Agent graph behavior, automatic
physical raw cleanup, pin, delete-now, compatibility shims, dependencies, or
changes to Issue #45 `session.send()` or Issue #49 Agent ownership.
