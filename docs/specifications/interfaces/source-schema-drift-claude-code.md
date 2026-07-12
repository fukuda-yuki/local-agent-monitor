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

The `schema_fingerprint` is deterministic over an emitted semantic-convention
or schema identifier when present plus the sorted observed structural set:
signal, record/event name, attribute key, and structural value type. The
`inventory_hash` additionally covers the bounded occurrence counts observed in
that ingest batch. Neither includes attribute values, raw bodies, PII,
credentials, source text, or local paths. Compatibility compares the resulting
schema fingerprint with fingerprints recorded by verified evidence; the
inventory hash diagnoses batch-to-batch changes without redefining support.

For OTLP/JSON, structural inventory is collected from the accepted source JSON
before normalization. For OTLP/protobuf, the decoder collects structural
inventory while reading the original wire message, before producing the
canonical JSON used by the existing raw store. Fingerprinting only the lossy
canonical JSON is invalid because an unhandled protobuf field could disappear
before drift evaluation.

Unknown observations are bounded per ingest batch and identity tuple. They
contain kind (`span`, `event`, or `attribute`), bounded name or keyed hash,
count, source version label, first/last observed time, and an opaque sample
reference. They never contain the source value or raw body. Excess distinct
unknowns collapse into a single overflow count. Unknown records are never
classified as recognized operations or used to synthesize hierarchy.

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

Migration must be transactional, restart-safe, preserve every existing row,
and reject a database schema newer than the implementation. Tests use fixtures
created by each actual shipped schema implementation or preserved database
artifact. A current schema with columns manually removed is not valid migration
evidence. Each fixture is migrated, the store is closed, reopened, and checked
again.

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
