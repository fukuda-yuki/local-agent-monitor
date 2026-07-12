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
- `observed_at`

`compatibility_state` is one of `supported`,
`supported_with_unknown_fields`, `schema_drift_detected`,
`unsupported_source_version`, `recognized_record_drop_detected`, or
`adapter_failure`.

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
resume/handoff, or byte-equivalent trace context. Repository, cwd, transcript
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

Source diagnostics use the additive sanitized endpoint
`GET /api/monitor/source-diagnostics?after&limit`. `limit` defaults to 50 and
is bounded to 1..200; malformed cursors or limits return the existing sanitized
`400` failure shape. Its response is:

```text
{
  items: [{
    observation_id, ingest_batch_id, source_surface,
    source_application_version, source_adapter, adapter_version,
    schema_fingerprint, inventory_hash, compatibility_state,
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
