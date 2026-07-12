# Claude Code contract security boundary

This is the Claude-specific application of the canonical
[security and data boundaries](../../../../security-data-boundaries.md). It
does not authorize a receiver, adapter, content read, storage path, or UI
surface. The [manifest](../manifests/claude-code.json) and mapping artifacts are
repository-safe metadata only.

## Input paths

- Claude OTel traces use the existing Local Monitor `POST /v1/traces`
  `otel-http` transport path while recording `claude-code-otel` provenance.
- Claude command/HTTP Hooks use `hook-forward`, which converts exactly one
  producer JSON object into the existing strict
  `POST /api/session-ingest/v1/events` envelope with
  `X-CAO-Session-Event-Version: 1`, `schema_version = 1`,
  `source_adapter = claude-code-hook`, and
  `source_surface = claude-code`. The envelope fields exist, while Task10B
  owns adding Claude Code to the current validation/source-surface vocabulary.
- Both paths remain loopback-only with Host-header validation and no CORS.
  The Hook endpoint is JSON-only, at most 1 MiB, and one converted event is
  within the existing 1..100 event batch limit.
- `hook-forward` remains observational: invalid input, network failure, and
  timeout exit `0`, stdout/stderr remain empty, and no outcome changes the
  Claude Hook decision. This fail-open behavior does not weaken ingest
  validation or storage safety.

The composite `claude-code-otel+claude-code-hook` value is a manifest registry
label only. Per-observation and per-field provenance is always the actual
adapter, `claude-code-otel` or `claude-code-hook`. `otel-http` and
`copilot-compatible-hook` name transport/implementation paths only; they are
never Claude `source_adapter` or provenance values.

## Content gates

Claude telemetry is documented as opt-in, but the Task 7-9 inventories did not
observe a trace, log, metric, Hook event, or content-bearing producer record.
Accordingly, those manifest capability leaves remain `unknown`; the table below
describes documentation-only gates and does not claim availability.

| Content | Gate | Default contract state |
| --- | --- | --- |
| User prompt in OTel | `OTEL_LOG_USER_PROMPTS=1` | `not_captured` / redacted unless explicitly enabled |
| Assistant response in OTel logs/events | documented assistant-response gate | documented only; no Task 7-9 structure or exact target observed |
| Tool details and input-derived paths/commands | `OTEL_LOG_TOOL_DETAILS=1` | `not_captured` unless explicitly enabled |
| Tool input/output bodies in spans | `OTEL_LOG_TOOL_CONTENT=1` | `not_captured` unless explicitly enabled |
| Raw API request/response bodies | `OTEL_LOG_RAW_API_BODIES` | prohibited for repository capture; remains disabled without separate authorization |
| Detailed beta trace content | detailed beta tracing settings | documented unstable raw content; no observed mapping to sanitized targets |
| Hook prompt, tool input/output, assistant message, error detail, paths | the corresponding Hook event is configured and fires | raw-bearing local event payload only |

`capture_content_state = available` requires both an enabled source gate and an
actually emitted allowed content-bearing field. Disabled or absent content is
`not_captured`; explicit source redaction is `redacted`; a surface without a
gate is `unsupported`. Documentation, a manifest declaration, or a configured
exporter never changes these states and never grants content authority. The
documented log, metric, and response surfaces remain unmapped until observed
producer structure and an exact existing target are available.

## Forbidden sensitive values

The forbidden sanitized/repository-safe set includes authenticated account and
PII attributes such as `user.email`, `user.account_uuid`, `user.account_id`,
`organization.id`, and user-supplied `enduser.*` identity. It also includes
workspace/file/transcript/agent-transcript paths, commands, tool arguments and
results, prompts, assistant responses, full error messages/details, Hook
definitions, raw API bodies, file content, URLs containing secrets, credentials,
and tokens embedded in nested values.

Those values must not enter sanitized monitor/session DTOs, source diagnostics,
readiness, logs, exception messages, repository-safe summaries, source
inventories, documentation-only fixtures, Issue/PR/docs text, CI artifacts, or
committed runtime artifacts. Paths are also forbidden identity evidence under
[exact binding](exact-binding.md). Both documented forms of the Claude OTel
tool-execution `error` attribute remain only inside
`CopilotAgentObservability.Telemetry.RawTelemetryRecord.PayloadJson`. The
configured gate changes the documented meaning from category to full detail,
but no observable producer discriminator currently authorizes either form to
enter `CopilotAgentObservability.Telemetry.MonitorSpanProjection.ErrorType`.

## Allowed opaque identifiers

Opaque identifiers are not content or account PII merely because they identify
a telemetry record. Sanitized local surfaces may carry an exact OTel trace ID,
span ID, or parent span ID; the monitor-generated local Session/Event IDs; a
source native session ID needed for exact Session resolution; schema
fingerprints; inventory hashes; and monitor-generated opaque observation/sample
references. Their exact existing targets include
`CopilotAgentObservability.Telemetry.MonitorSpanProjection.TraceId`,
`CopilotAgentObservability.Telemetry.MonitorSpanProjection.SpanId`, and
`CopilotAgentObservability.Telemetry.MonitorSpanProjection.ParentSpanId`,
`CopilotAgentObservability.Telemetry.Sessions.ObservedSession.SessionId`,
`CopilotAgentObservability.Telemetry.Sessions.ObservedSessionEvent.EventId`, and
`CopilotAgentObservability.Telemetry.Sessions.SessionNativeId.NativeSessionId`.

Allowed opaque IDs still cannot be logged with raw content, used to infer an
account identity, or promoted into a field that has no exact target. In
particular, documented `prompt_id` and `tool_use_id` remain inside raw Hook
payload content unless and until an exact sanitized target is specified; they
are never Session-binding evidence.

## Storage and reads

- Raw OTel remains governed by the existing raw Local Monitor storage and raw
  route policy. `CopilotAgentObservability.Telemetry.RawTelemetryRecord.PayloadJson`
  is the sole raw-storage target named by the OTel mapping.
- The accepted complete Hook object first occupies the transport-only
  `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.Payload`.
  It is then secret-filtered before persistence to
  `CopilotAgentObservability.Telemetry.Sessions.SessionEventContent.ContentJson`
  and separated from `session_events` metadata. `SessionIngestEvent.Payload` is
  not a normalized field target.
- Raw Session content receives `expires_at = captured_at + 90 days`.
- `GET /sessions/{id}/events/{eventId}/content` is same-origin and
  `Cache-Control: no-store`; expired reads return the canonical `410` /
  `expired_pending_deletion` response.
- `--sanitized-only` removes raw-bearing routes with `404` while preserving
  sanitized hierarchy and metadata. It does not reconstruct hidden content.
- Sanitized reads and source diagnostics contain only bounded metadata and
  opaque identifiers. They never echo payload fragments or raw exceptions.

Hook capture time may populate the strict envelope's required `occurred_at`
transport field when Claude supplies no documented event timestamp. It is not
source timing authority and cannot create duration, ordering, ownership, or
binding evidence.

Hook `duration_ms`, error/status labels, stop fields, and SessionEnd reason stay
within the Hook lifecycle/raw boundary. They never overwrite OTel
`MonitorSpanProjection.DurationMs`, `Status`, or `ErrorType`.

## Repository-safe evidence

The Task 7-9 inventories contain settings labels, execution state, counts,
blockers, and missing-capability statements only. They contain no raw producer
values, PII, credentials, token values, or local paths. Interactive and Agent
SDK producer capture remains unverified; the completed print run emitted no
structural telemetry. No fixture or fingerprint may be invented to replace
those missing live observations.
