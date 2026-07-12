# Source Schema Drift and Claude Code Interface

This specification is the canonical contract for Issues #62, #63, #64, and
#65. It extends the source-capability v1 contract without weakening exact
identity or the raw/sanitized boundary.

## Compatibility model

Each successfully committed ingest batch produces one immutable schema
observation containing:

- `ingest_batch_id`
- `source_surface`
- nullable `source_application_version`
- `source_adapter`
- `adapter_version`
- `schema_fingerprint`
- `inventory_hash`
- `compatibility_state`
- ordered `reason_codes`
- `capture_content_state`
- `observed_at`

Adapter/parse failure before raw persistence produces an immutable diagnostic
observation with a new `observation_id`, nullable `ingest_batch_id`, nullable
fingerprint/hash fields, `compatibility_state = adapter_failure`, a fixed
sanitized reason code, and no payload fragment or exception text. Unsupported
media type, oversize, queue-full, and commit-timeout keep their existing HTTP
error contracts and are not misreported as schema drift.

`compatibility_state` is one of `supported`,
`supported_with_unknown_fields`, `schema_drift_detected`,
`unsupported_source_version`, `recognized_record_drop_detected`, or
`adapter_failure`.

Compatibility reason codes have this exact canonical order:

1. `unknown_fields_observed`
2. `unsupported_source_version`
3. `schema_drift_detected`
4. `recognized_record_drop_detected`
5. `adapter_parse_failure`
6. `adapter_exception`

Reasons are de-duplicated and emitted in that order. `supported` has no reason.
`supported_with_unknown_fields` has `unknown_fields_observed`.
`unsupported_source_version`, `schema_drift_detected`, and
`recognized_record_drop_detected` each carry the same-named reason.
`adapter_failure` carries exactly one of `adapter_parse_failure` or
`adapter_exception`.

`next_action` has these exact wire values:

| State/reason | `next_action` |
| --- | --- |
| `supported` | `none` |
| `supported_with_unknown_fields` | `review_unknown_fields` |
| `unsupported_source_version` | `use_compatible_source_or_update_adapter` |
| `schema_drift_detected` | `capture_fixture_and_review_mapping` |
| `recognized_record_drop_detected` | `restore_mapping_or_update_versioned_golden` |
| `adapter_parse_failure` | `validate_payload_and_protocol` |
| `adapter_exception` | `inspect_sanitized_adapter_failure` |

For pre-persistence adapter failure, `source_surface`,
`source_application_version`, `source_adapter`, `adapter_version`,
`schema_fingerprint`, `inventory_hash`, and `capture_content_state` are
nullable. A value is populated only when known before parsing failed; no value
is inferred from payload text. `observed_at`, state, reason, and next action are
always present.

Verified versions are recorded as evidence. They are not a receive allowlist.
An unverified version with a known fingerprint is supported. A new fingerprint
is persisted, processed conservatively, and degraded with
`schema_drift_detected`. A version is unsupported only when it is explicitly
known incompatible or a required signal is absent.

## Fingerprint and unknown representation

The exact descriptor, encoding, hash domains, closed inventory model, required
Span condition, and executable matrix are frozen in
`contracts/source-capabilities/v1/otlp-trace-structural-v1.md`. That descriptor
pins OpenTelemetry Proto `v1.10.0` commit
`ca839c51f706f5d53bfb46f06c3e90c3af3a52c6`.

One versioned OTLP descriptor model covers request, resource spans, resource,
scope spans, instrumentation scope, span, event, link, status, `KeyValue`,
`AnyValue`, `ArrayValue`, `KeyValueList`, and `EntityRef` envelopes. It defines the accepted
JSON property/type and protobuf tag/wire type for every known field. The JSON
and protobuf walkers recursively apply that same model to the original input.
A known name or tag with the wrong representation is unknown. An unknown
length-delimited protobuf field is recorded at its containing envelope and is
never guessed to be another message.

For OTLP/JSON, structural inventory is completed from the accepted source JSON
before normalization. For OTLP/protobuf, it is completed while reading the
original wire message, before producing the canonical JSON used by the existing
raw store. Fingerprinting only the lossy canonical JSON or merging a second
post-conversion unknown-field path is invalid. Recognized semantic facts have
the same canonical identities across JSON and protobuf. Unknown JSON property
names and unknown protobuf field numbers remain transport-scoped because their
meaning cannot be safely equated.

Arbitrary producer-controlled names are never retained literally, including
span/event names, attribute keys, schema URLs, and unknown JSON properties.
Each becomes:

```text
sha256:<64 lowercase hexadecimal characters>
SHA256(UTF8("source-structure-v1\0" + role + "\0" + raw_name))
```

Only fixed identifiers created by repository code, such as OTLP envelope names
and protobuf field numbers, remain literal. Known-name allowlists classify a
token but do not change it; an allowlist update therefore cannot rewrite an old
fingerprint. Inventory and diagnostic serialization never contain source
values, raw bodies, PII, credentials, source text, or local paths.

The `schema_fingerprint` hashes a domain/version marker plus every distinct
canonical structural identity in ordinal order. Counts are excluded. The
`inventory_hash` hashes every identity plus its bounded occurrence count. Both
hash the complete structural set before diagnostic truncation. Different
overflow identities must therefore produce different schema fingerprints even
when retained rows and overflow counts match.

At most 256 unknown identities are retained per ingest batch in canonical
order. Each identity count and the aggregate overflow occurrence count are
capped at 1,000,000. Exact aggregate unknown span/event/attribute occurrence
counts are tracked separately from retained diagnostic rows. Overflow counts
describe only truncated diagnostic identities and never redefine support.

Unknown kind is exactly `span`, `event`, or `attribute`. Unrecognized span and
event identities map respectively to `span` and `event`; unrecognized attribute
keys or fields at any OTLP envelope map to `attribute`. Unknown children derive
their version label from their parent observation. Their name is either the
exact keyed-hash form above or a fixed code-owned transport identifier; count is
1..1,000,000; first observed time is not after last observed time; and sample
references use a monitor-generated opaque `sample:v1:<64 lowercase hex>` token.
Unknown records are never classified as recognized operations or used to
synthesize hierarchy.

Local Monitor pre-persistence normalization and post-reload projection consume
an ephemeral recognized projection view, not the original raw JSON directly.
The versioned OTLP descriptor owns both
inventory classification and view filtering. The view recursively excludes
unknown properties, wrong representations, invalid repeated elements, and
Trace-ignored fields while preserving valid siblings and descriptor-valid
numeric enum/status values. It is built without `JsonNode`, is never stored or
logged, and cannot change the original-input inventory or hashes.

For monitor ingestion, a valid JSON object is inventoried before normalization.
Wrong hierarchy or field representations therefore produce an atomically
persisted raw record plus degraded or unsupported observation, not an adapter
failure. The monitor raw ingestor receives the original payload and recognized
view explicitly. The existing strict Config CLI raw-ingestion path does not
change. Malformed transports, a non-object request root, and internal adapter
failures unrelated to an accepted representation remain pre-persistence
diagnostics with no raw record.

After reload, every raw-backed compatibility state and legacy row rebuilds the
same recognized view for trace and span projection. One successful scheduled
pass completes both existing idempotent projection phases even when the view is
partial or empty. The trace and span backlogs remain independent; no combined
transaction, schema, retry loop, or sleep is added. Corrupt already-stored
legacy raw remains a projection failure; no drift or adapter observation is
fabricated. SQLite busy handling retains the existing scheduled replay.

`capture_content_state` is a closed source-capture value: `available`,
`not_captured`, `redacted`, or `unsupported`. Observation construction derives
state, its zero-or-one scalar reason, and `next_action` together; callers cannot
supply arbitrary or inconsistent reason arrays. Pre-persistence adapter failure
uses a distinct failure draft. Aggregated reason unions de-duplicate only the
six fixed reason codes and emit their canonical order.

Structural name tokens, occurrence counts, unknown identities, full structural
inventories, decisions, and drafts are closed validated values. Collections are
defensively copied to immutable canonical snapshots. Callers cannot supply a
fingerprint, inventory hash, aggregate unknown count, retained-row set,
`HasUnknownFields`, state, reason, or action independently.

Successful-batch evaluation uses this precedence: recognized-record drop;
missing required trace signal or exact incompatible-version evidence; unknown
fingerprint; known fingerprint with unknown identities; supported. A required
trace signal means at least one structurally valid Span envelope under the
standard OTLP trace hierarchy. Missing optional identity/timing fields are
completeness or mapping evidence, not version rejection. Versions remain
evidence labels and never become a receive allowlist.

## Persistence and migration

Schema observations and unknown observations use dedicated additive monitor
tables behind a focused store. They do not add source-specific responsibility
to `RawTelemetryStore.cs` or make Session storage authoritative for batch
compatibility.

`IIngestionCommitStore.Commit(ValidatedIngestionBatch)` is the transaction
coordinator: it inserts the raw record and batch observation in one SQLite
transaction by calling shared raw-record SQL and the focused compatibility
store. `ISourceCompatibilityStore.RecordAdapterFailure(...)` persists a
diagnostic with no raw record through the same single-writer queue.
`ISourceCompatibilityStore.List(after, limit)` owns diagnostic reads. Neither
compatibility writes nor queries are added to `IMonitorProjectionStore`.

One source-neutral `MonitorSchemaMigrator` owns the existing monitor v1-v4 base
DDL and accepts an existing connection/transaction. `RawTelemetryStore` delegates
base-schema initialization to it and never rewrites a higher monitor stamp back
to v4. The focused compatibility initializer runs the same base migration plus
v5 DDL and stamp in one transaction, so failure from any real v1-v4 input rolls
the whole attempt back to its original version. A stamp newer than v5 is never
downgraded and is rejected by the focused current-version initializer.
The v5 observation table uses defensive CHECK constraints for the exact state,
scalar reason, seven-value `next_action`, and capture-state vocabulary and their
valid state/reason/action combinations. Direct SQL cannot persist arbitrary
action text even when it bypasses the closed in-memory factories.

Migration must be transactional, restart-safe, preserve every existing row,
and reject a database schema newer than the implementation. Tests use fixtures
created by each actual shipped schema implementation or preserved database
artifact. A current schema with columns manually removed is not valid migration
evidence. Each fixture is migrated, the store is closed, reopened, and checked
again.

Fixture verification covers complete SQLite schema semantics: user object
inventory; every `table_list` field including `wr` and `strict`; ordered
`table_xinfo` columns including hidden kind; declared types, nullability,
defaults and primary-key ordinal; `AUTOINCREMENT`; canonical checks;
primary/unique keys; every `index_xinfo` key or expression identity, ASC/DESC,
collation, `key` versus auxiliary-term flag, origin and partial predicate; and
foreign keys. Quote/comment/string-literal decoys cannot satisfy the targeted
`AUTOINCREMENT` or CHECK parser. Internal autoindex names are not contractual;
equivalent autoindex semantics compare equal despite different internal names.
The expected schema is a static reviewed semantic contract checked against the
read-only, manifest-hashed fixture from the latest shipped schema. It is never
generated by the current migration under test.

## Claude producer and authority contract

Issue #63 records repository-safe inventories for interactive Claude Code,
`claude -p`, and Claude Agent SDK when those environments are available. A
missing live environment is recorded as a separate follow-up; observed fields
must not be invented.

OTel is authoritative for trace ID, span ID, parent span ID, start/end/duration,
and emitted timing fields. Hook is authoritative for native session lifecycle,
explicit Hook event identity, permission/lifecycle evidence, and explicit
resume/handoff links. Hook input cannot create or overwrite spans, hierarchy,
duration, token counts, TTFT, or OTel status.

Binding is allowed only by identical native session ID, explicit
resume/handoff, or byte-equivalent trace context. A matching trace ID without
byte-equivalent trace context is insufficient. A generic link whose kind is
not `resume` or `handoff` is insufficient. Repository, cwd, transcript
path, process identity, and timestamp proximity are not binding evidence.
Duplicate OTel and Hook inputs are idempotent by source identity and canonical
Hook hash.

For Claude v1, positive `trace_context` binding is deferred because the Session
event envelope exposes only `trace_id` and no provenance-bearing complete
trace-context DTO. The canonical condition remains reserved for a later
spec-first interface. Until that DTO exists, a shared trace ID is insufficient
and must remain unbound; only identical native session ID or an explicit
resume/handoff may produce an exact Claude link.

## Cross-surface DTO contract

| Boundary | Required data | Rule |
| --- | --- | --- |
| Producer to raw | source/version, adapter, source IDs, native IDs, content state | Fixture must use the actual producer shape. |
| Raw to compatibility | batch ID, fingerprint, inventory hash, unknown metadata | No raw values or bodies. |
| Compatibility to diagnostics | state, ordered reasons, next action, verified-version evidence | Unknown version alone is not unsupported. |
| Raw to Claude adapter | recognized OTel/Hook fields plus provenance | No guessed values or zero fill. |
| Adapter to Session | Session/Run/Event plus exact binding evidence | Hook-only never becomes `full`. |
| Projection to HTTP | source/version/schema state, binding kind, completeness, rollups, content state | Existing routes receive additive sanitized fields only. |
| HTTP to UI | the same field names and enum values | UI does not reinterpret or invent fallback facts. |
| HTTP to Canvas | existing execution graph DTO | No parallel Claude hierarchy DTO. |

The exact Claude OTLP and Hook producer shape is frozen by the Issue #63
observed inventory before adapter implementation. Claude OTel continues to use
the standard existing `POST /v1/traces` receiver. Claude command/HTTP Hook
input is converted by the installed `hook-forward` path into the existing
strict `POST /api/session-ingest/v1/events` envelope with distinct
`source_adapter = claude-code-hook` and `source_surface = claude-code`.
Claude Hook mode is selected only by exact `--source claude-code`; omitting
`--source` preserves the existing Copilot Hook mode. `--source-version` and
`--schema-fingerprint` are valid only with that Claude selector, and at least
one is required. The forwarder never selects Claude from payload shape and never
promotes documentation/fixture labels, inventory-only version evidence, or Hook
payload values into provenance. Missing or invalid selector/provenance suppresses
the intended Claude forwarding with the existing fail-open/silent behavior and
becomes a named live configuration follow-up, not invented evidence.

The Claude manifest registry label is
`claude-code-otel+claude-code-hook`. It describes registered input paths only.
Per-field and per-observation actual-adapter provenance is exactly
`claude-code-otel` or `claude-code-hook`; the composite label is never stored as
actual provenance. This uses the existing v1 string field and does not extend
the v1 manifest schema.

Source diagnostics use the additive sanitized endpoint
`GET /api/monitor/source-diagnostics?after&limit`. `limit` defaults to 50 and
is bounded to 1..200; malformed cursors or limits return the existing sanitized
`400` failure shape. Its response is:

```text
{
  items: [{
    observation_id, ingest_batch_id?, source_surface,
    source_application_version, source_adapter, adapter_version,
    schema_fingerprint?, inventory_hash?, compatibility_state,
    reason_codes, unknown_span_count, unknown_event_count,
    unknown_attribute_count, observed_at, next_action
  }],
  next_cursor
}
```

All identifiers are opaque and all fields are sanitized metadata. Existing
`/health/ready` body, status mapping, thresholds, and degraded reasons remain
unchanged as required by D051. Source compatibility does not make process
readiness fail. Existing monitor/session DTOs may gain the same additive
source diagnostic field names; no sanitized route may return raw content.

The trace DTOs returned by `GET /api/monitor/traces` and
`GET /api/monitor/trace-list`, and the Session DTO returned in
`/api/session-workspace/sessions` list/detail responses, gain the same additive
fields:

```text
source_diagnostic: {
  source_surface,
  source_application_version,
  source_adapter,
  adapter_version,
  schema_fingerprint,
  compatibility_state,
  reason_codes,
  next_action
},
binding_state,
completeness,
completeness_reason_codes,
content_state
```

`binding_state` is `hook_only`, `otel_only`, or `exact_linked`.
`completeness` keeps the Issue #51 four-value enum and reason codes keep Issue
#61 canonical ordering. `content_state` uses the existing Session content-state
wire values. If a trace has no exact Session link it is `otel_only` and
`unbound`; the API does not invent Session evidence. Nullable source fields
remain null rather than receiving display defaults. The Agent graph response
shape does not change.

For Task20 projection, `exact_linked` requires the shipped identical-native-ID
or explicit resume/handoff resolver. Trace-ID-only evidence never produces
`exact_linked`; the separate Hook and OTel evidence remain `hook_only` and
`otel_only` until a complete provenance-bearing trace-context DTO is specified
and implemented.

For a trace or Session linked to multiple observations, select the display
observation by compatibility severity, then newest `observation_id`:
`recognized_record_drop_detected` > `adapter_failure` >
`unsupported_source_version` > `schema_drift_detected` >
`supported_with_unknown_fields` > `supported`. Emit the ordered distinct union
of reason codes from every linked observation. A pre-persistence
`adapter_failure` without a trace/session reference is diagnostics-only and is
not joined by time or repository context.

`capture_content_state` is `available` only when capture was explicitly enabled
and an allowed content-bearing field was emitted, `not_captured` when disabled
or absent, `redacted` when the source explicitly reports redaction, and
`unsupported` when the surface cannot expose the gate. Trace/Session
`content_state` is that value only when all linked observations agree;
otherwise it is null and completeness reasons describe the missing content.
`expired_pending_deletion` remains an event-content retention state and is not
fabricated as a source capture state.

## Diagnostics and UI

Diagnostics distinguish supported, unknown fields, unsupported version,
schema drift, recognized-record drop, and adapter/parse failure. Every state
has a stable reason code and a concrete next action. Session and trace views
show `claude-code`, version/schema state, Hook-only/OTel-only/exact-linked,
completeness reasons, and only fields actually emitted.

Claude flow, waterfall, and ownership use exact source parentage only. Missing
or ambiguous parentage is `unresolved`; no time-range ownership inference is
applied. Content-disabled hides raw controls. `--sanitized-only` retains the
sanitized hierarchy and removes raw-bearing controls and routes.

## Deterministic tests and gates

The executable producer-to-UI path starts from actual producer DTO fixtures.
It covers source IDs, edges, counts, ordering, mappings, token/timing/status,
evidence resolution, and raw/sanitized separation. Golden changes are
versioned and explicit.

Atomicity, rollback, stale state, deduplication, and concurrency tests use
barriers, controllable stores, or transactions; sleep-based coordination is
forbidden. Existing GitHub Copilot fixtures remain a regression gate.

Required automated closeout commands are:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Live evidence records date, OS, source version, settings labels, opaque
trace/session references, observed/missing capabilities, completeness, and
explicit blockers. Repository-safe reports contain no raw content, PII,
credential, or local path.
