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
| Byte-equivalent trace context | A producer event and an OTel record carry the complete same trace context byte-for-byte, with provenance for both values. | Bind only the records carrying that exact context with `CopilotAgentObservability.Telemetry.Sessions.SessionBindingKind.TraceContext`. `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.TraceId` by itself is insufficient. |

There is no normalization before comparison: no trimming, case folding,
Unicode normalization, decoding/re-encoding, prefix removal, or numeric
conversion. Missing or ambiguous evidence cannot bind.

Claude Code has one documentation-only candidate for this table: mapping field
`hook.session_id` compared byte-for-byte with `otel.session_id`. The resolver is
fully specified, but no approved inventory observed the OTel producer field and
the current
`CopilotAgentObservability.Telemetry.Sessions.SessionSourceSurface` enum has no
Claude Code member, and `SessionIngestValidation` does not accept
`source_surface = claude-code`; Task10B owns that registry/adapter seam. The
official Hook reference and approved Task 7-9 inventories also do not establish
a Hook trace-context field or a target native session ID for
`SessionStart.source = resume`. Agent SDK `TRACEPARENT`/`TRACESTATE`
propagation establishes OTel parentage only; it does not add trace context to a
Hook event and does not bind a Session.

## Existing-target audit

| Evidence or result | Exact existing target | Current state |
| --- | --- | --- |
| Hook native session ID | `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.NativeSessionId` | Field exists; Claude source acceptance is the Task10B seam. |
| Persisted native ID and binding kind | `CopilotAgentObservability.Telemetry.Sessions.SessionNativeId.NativeSessionId` and `CopilotAgentObservability.Telemetry.Sessions.SessionNativeId.BindingKind` | Fields exist; `CopilotAgentObservability.Telemetry.Sessions.SessionSourceSurface` lacks Claude Code. |
| Explicit link | `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope.ExplicitLink`, `CopilotAgentObservability.LocalMonitor.Sessions.SessionExplicitLink.NativeSessionId`, and `CopilotAgentObservability.LocalMonitor.Sessions.SessionExplicitLink.Kind` | Fields exist; no documented or observed Claude target native ID is available. |
| Hook trace ID | `CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEvent.TraceId` | Field exists, but no Hook trace field was observed and a trace ID is not complete trace context. |
| OTel trace/span/parent IDs | `CopilotAgentObservability.Telemetry.MonitorSpanProjection.TraceId`, `CopilotAgentObservability.Telemetry.MonitorSpanProjection.SpanId`, and `CopilotAgentObservability.Telemetry.MonitorSpanProjection.ParentSpanId` | Exact OTel targets exist; they do not by themselves create a Session binding. |

There is no existing normalized OTel native-session-candidate field or complete
Hook trace-context DTO. `otel.session_id` is therefore
`corroboration_only`, and the absent Hook trace-context/explicit-link producer
fields remain documented-unmapped. This contract does not invent either
target.

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

## Evidence status

The producer field names above are documented by the official Claude Code
monitoring, Hook, and Agent SDK observability references; they are not observed
capability claims. Task 7 interactive and Task 9 Agent SDK execution were
blocked, and Task 8 print execution completed without emitting structural
telemetry. No Task 7-9 inventory observed a producer Hook envelope, OTel span
structure, verified fingerprint, explicit link, or Hook trace context. The
manifest therefore leaves all related capability leaves `unknown`; only the
actually executed version detector is `available`.
