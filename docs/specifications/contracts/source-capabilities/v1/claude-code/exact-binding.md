# Claude Code exact binding

This document freezes the Claude Code binding subset of the
[source schema drift and Claude Code contract](../../../../interfaces/source-schema-drift-claude-code.md).
It applies to the registered paths declared by the
[Claude Code manifest](../manifests/claude-code.json), while actual provenance
remains `claude-code-otel` or `claude-code-hook` as specified by the
[OTel mapping](otel-mapping.json) and [Hook mapping](hook-mapping.json).

## Allowed binding evidence

Binding is a closed allowlist. A candidate must satisfy exactly one row below;
otherwise it remains unbound.

| Binding kind | Required evidence | Result |
| --- | --- | --- |
| Identical native session ID | Mapping fields `hook.session_id` and `otel.session_id` are both present and their UTF-8 bytes are identical. The Hook value resolves to exactly one Session. | Bind the OTel trace evidence to that Session with `CopilotAgentObservability.Telemetry.Sessions.SessionBindingKind.Native`; lower-authority context cannot participate. |
| Explicit resume/handoff | `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.ExplicitLink` contains an exact target `CopilotAgentObservability.LocalMonitor.Sessions.SessionExplicitLink.NativeSessionId`, and `CopilotAgentObservability.LocalMonitor.Sessions.SessionExplicitLink.Kind` is `resume` or `handoff`. | Merge only the explicitly named Sessions with `CopilotAgentObservability.Telemetry.Sessions.SessionBindingKind.ExplicitResume` or `CopilotAgentObservability.Telemetry.Sessions.SessionBindingKind.ExplicitHandoff`. |
| Byte-equivalent trace context | A producer event and an OTel record carry the complete same trace context byte-for-byte, with provenance for both values. | **Deferred in Session v1:** the current envelope carries only `trace_id`, which is insufficient. No `TraceContext` binding is emitted until a provenance-bearing complete trace-context DTO is added and separately reviewed. |

There is no normalization before comparison: no trimming, case folding,
Unicode normalization, decoding/re-encoding, prefix removal, or numeric
conversion. Missing or ambiguous evidence cannot bind.

Claude Code's shipped v1 resolver uses the exact native-session-ID and explicit
resume/handoff rows above. `SessionSourceSurface.ClaudeCode`, the
`source_surface = claude-code` envelope validation, and the
`claude-code-otel`/`claude-code-hook` adapters are current shipped contracts.

The exact native-session-ID resolver is gated only by its own evidence: a
single unambiguous `session.id` attribute on the OTel span whose UTF-8 bytes
equal exactly one persisted `claude-code` Hook native session ID with binding
kind `Native`, `ExplicitResume`, or `ExplicitHandoff`. It does not require
`claude-code-otel` adapter promotion; a span still labeled `raw-otlp` binds on
this evidence. Binding never rewrites provenance: evidence stored from a
non-promoted span keeps its actual source labels, and the raw `session.id` is
still not persisted as a new native ID.

The complete trace-context row remains a named future interface because the
Session envelope still exposes only `trace_id`; trace-id-only evidence remains
`otel_only`/`hook_only` and can never become `exact_linked`. The official Hook
reference and approved Task 7-9 inventories do not establish a complete Hook
trace-context field or a target native session ID for `SessionStart.source =
resume`. Agent SDK `TRACEPARENT`/`TRACESTATE` propagation establishes OTel
parentage only; it does not add trace context to a Hook event or bind a
Session.

## Existing-target audit

| Evidence or result | Exact existing target | Current state |
| --- | --- | --- |
| Hook native session ID | `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.NativeSessionId` | Field exists and accepts the shipped Claude Hook source contract. |
| Persisted native ID and binding kind | `CopilotAgentObservability.Telemetry.Sessions.SessionNativeId.NativeSessionId` and `CopilotAgentObservability.Telemetry.Sessions.SessionNativeId.BindingKind` | Fields exist; `SessionSourceSurface.ClaudeCode` and native/explicit kinds are shipped. |
| Explicit link | `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.ExplicitLink`, `CopilotAgentObservability.LocalMonitor.Sessions.SessionExplicitLink.NativeSessionId`, and `CopilotAgentObservability.LocalMonitor.Sessions.SessionExplicitLink.Kind` | Fields exist; no documented or observed Claude target native ID is available. |
| Hook trace ID | `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.TraceId` | Field exists, but no Hook trace field was observed and a trace ID is not complete trace context. |
| OTel trace/span/parent IDs | `CopilotAgentObservability.Telemetry.MonitorSpanProjection.TraceId`, `CopilotAgentObservability.Telemetry.MonitorSpanProjection.SpanId`, and `CopilotAgentObservability.Telemetry.MonitorSpanProjection.ParentSpanId` | Exact OTel targets exist; they do not by themselves create a Session binding. |

There is no existing normalized complete Hook trace-context DTO. The raw OTel
`session.id` is compared byte-for-byte to the persisted Claude native ID only
inside the exact native resolver; it is not persisted as a new native ID. A
trace ID alone remains insufficient binding evidence, and the absent Hook
trace-context/explicit-link producer fields remain documented-unmapped. This
contract does not invent either target.

The OTel `agent_id` row is `otel.agent_id` with disposition
`documented_unmapped`. It never targets
`CopilotAgentObservability.Telemetry.MonitorSpanProjection.AgentName`, never
creates an ownership edge, and never participates in any binding resolver.

## Forbidden near matches

None of the following is Session or trace binding evidence, alone or in
combination:

- repository, workspace, `cwd`, `transcript_path`, agent transcript path,
  filename, or any other local path;
- timestamp equality, proximity, overlap, arrival order, duration, or process
  lifetime;
- process ID, executable, command line, host, operating-system user, account,
  organization, terminal, or installation identity;
- source surface, adapter ID, the composite registry label, application
  version, schema fingerprint, inventory hash, service name, or resource
  attributes;
- a trace ID without the complete byte-equivalent trace context;
- `prompt_id` / `prompt.id`, `tool_use_id` / `gen_ai.tool.call.id`, request ID,
  client request ID, event sequence, or canonical Hook hash;
- `agent_id`, `parent_agent_id`, `agent_type`, model, tool name, permission
  mode, retry/attempt value, error category, or content equality;
- `SessionStart.source = resume`, `--resume`, `--continue`, or `/resume`
  without the explicitly named target native session ID;
- a generic link, or any link kind other than the exact `resume` or `handoff`
  values in the strict envelope;
- the presence of `TRACEPARENT` in an environment without captured,
  provenance-bearing byte-equivalent context on both records;
- manual selection, UI adjacency, shared prompt text, shared response text, or
  a hash of raw content.

`prompt_id` and `tool_use_id` may correlate event or tool evidence only after
the enclosing Session is already exact-bound. They never promote or repair a
Session binding.

## Authority after binding

- OTel remains authoritative for
  `CopilotAgentObservability.Telemetry.MonitorSpanProjection.TraceId`,
  `CopilotAgentObservability.Telemetry.MonitorSpanProjection.SpanId`,
  `CopilotAgentObservability.Telemetry.MonitorSpanProjection.ParentSpanId`,
  `CopilotAgentObservability.Telemetry.MonitorSpanProjection.StartTime`,
  `CopilotAgentObservability.Telemetry.MonitorSpanProjection.EndTime`,
  `CopilotAgentObservability.Telemetry.MonitorSpanProjection.DurationMs`, token
  projection fields, and
  `CopilotAgentObservability.Telemetry.MonitorSpanProjection.Status`.
  Documented TTFT has no existing target and remains unmapped.
- Hook remains authoritative for
  `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.NativeSessionId`,
  `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.SourceEventId`,
  lifecycle/permission evidence retained in
  `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.Payload`,
  and `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.ExplicitLink` only
  when the strict envelope actually contains a complete link.
- OTel parentage is the only Claude Agent ownership edge. Missing or ambiguous
  parentage is `unresolved`; Hook agent IDs and time ranges do not repair it.
- A lower-authority or missing value never overwrites a present authoritative
  value. No binding creates a span, parent, duration, token count, or status.

## Status resolver

`otel.status_code` is the sole authority for
`CopilotAgentObservability.Telemetry.MonitorSpanProjection.Status`.
`otel.tool_success` is `corroboration_only` and never supplies a fallback. The
`otel.status_resolution` derived mapping is exhaustive:

1. `ERROR` with `success = false` or missing produces `error` and evaluates
   agreement or missing corroboration.
2. `ERROR` with `success = true` produces `error` and evaluates a conflict.
3. `OK` or `UNSET` with `success = true` or missing produces `ok` and evaluates
   agreement or missing corroboration.
4. `OK` or `UNSET` with `success = false` produces `ok` and evaluates a
   conflict.
5. Missing or invalid `status.code` produces null regardless of `success`.

The agreement/conflict evaluation is not persisted and creates no target,
diagnostic, or reason code.

Hook error, duration, stop, and session-end fields are Hook lifecycle/raw
evidence only. They never overwrite OTel status or timing.

## Absence and completeness

An OTel record with no exact Session link is `otel_only` and
`SessionCompleteness.Unbound`. A Hook Session without exact-linked OTel evidence
is `hook_only` and cannot become `SessionCompleteness.Full`. Missing native
identity, trace context, content state, provenance, or source structure maps
only to the canonical completeness reasons; it is never zero-filled or
guessed. The fixed wire status and reason vocabulary remains the one defined by
the Canvas Session workspace specification.

Task20 projection emits `binding_state = exact_linked` only after the shipped
identical-native-ID or explicit resume/handoff resolver has produced an exact
link. A shared `trace_id`, including an identical trace ID on Hook and OTel
records, remains `hook_only` / `otel_only` under v1 and never projects as
`exact_linked`. This remains true until a later spec-first change defines and
implements the complete provenance-bearing trace-context DTO.

## Evidence status

The producer field names above are documented by the official Claude Code
monitoring, Hook, and Agent SDK observability references; they are not observed
capability claims. Task 7 interactive and Task 9 Agent SDK execution were
blocked, and Task 8 print execution completed without emitting structural
telemetry. No Task 7-9 inventory observed a producer Hook envelope, OTel span
structure, verified fingerprint, explicit link, or Hook trace context. The
manifest therefore leaves all related capability leaves `unknown`; only the
actually executed version detector is `available`.
