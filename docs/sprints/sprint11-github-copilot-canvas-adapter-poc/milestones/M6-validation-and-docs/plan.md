# Sprint11 M6 - Validation & Docs (Plan)

Status: **Planned** - final validation, docs, and evidence. Gated by M5.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` -> `docs/spec.md` -> `docs/specifications/`. Sprint
context: [../../README.md](../../README.md).

## Objective

Validate the Sprint11 Canvas adapter end-to-end, prove the sanitized-only/raw
boundary, record any human-gated Canvas limitations, and update user-facing docs
for setup and operation.

## Scope

In scope:

1. Run repository validation:

   ```powershell
   dotnet build CopilotAgentObservability.slnx
   pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
   dotnet test CopilotAgentObservability.slnx
   ```

2. Run Canvas validation in an active Copilot app environment:

   ```text
   extensions_reload
   extensions_manage({ operation: "list" })
   extensions_manage({ operation: "inspect", name: "otel-monitor-canvas" })
   list_canvas_capabilities({ canvasId: "otel-monitor" })
   open_canvas({ canvasId: "otel-monitor", instanceId: "m6-smoke", input: { monitorBaseUrl: "http://127.0.0.1:4320" } })
   invoke_canvas_action({ instanceId: "m6-smoke", actionName: "monitor_health", input: {} })
   invoke_canvas_action({ instanceId: "m6-smoke", actionName: "list_recent_traces", input: { limit: 10 } })
   invoke_canvas_action({ instanceId: "m6-smoke", actionName: "get_trace_summary", input: { traceId: "<trace-id>" } })
   invoke_canvas_action({ instanceId: "m6-smoke", actionName: "get_trace_span_tree", input: { traceId: "<trace-id>" } })
   invoke_canvas_action({ instanceId: "m6-smoke", actionName: "get_cache_summary", input: { traceId: "<trace-id>" } })
   ```

3. Validate Local Monitor posture:
   - run monitor with `--sanitized-only`;
   - verify TraceDetail sanitized tabs are available;
   - verify raw section/full raw links are absent;
   - verify `GET /traces/{rawRecordId}/raw` returns `404`;
   - verify Canvas `open()` refuses or diagnoses non-safe posture.
4. Validate action safety:
   - invalid inputs are rejected by schema;
   - action responses are bounded;
   - no raw prompt/response bodies, tool arguments/results, PII, credentials,
     tokens, local sensitive paths, raw OTLP payloads, runtime artifacts, or
     generated telemetry content appears in action responses, logs, or committed
     files.
5. Validate server lifecycle:
   - extension-owned servers bind only to `127.0.0.1`;
   - token-protected helper routes reject missing/wrong tokens;
   - `onClose()` closes servers.
6. Update docs:
   - `docs/user-guide/local-monitor.md` or a dedicated user guide section for
     loading and using the Canvas adapter;
   - `docs/task.md` Sprint11 status/evidence pointer;
   - Sprint11 README status table and validation notes;
   - M6 live-validation evidence file if the active environment supports Canvas
     runtime validation.

Out of scope:

- Shared/team deployment.
- Remote monitor access.
- Publishing raw or captured telemetry evidence.
- Replacing the Local Monitor user guide with Canvas-only instructions.

## Acceptance criteria

- Repository build/test validation is green, or exact blockers are recorded.
- Canvas extension is discovered, inspected, opened, and its actions invoked in
  the active Copilot app environment, or missing Canvas tooling is recorded as a
  human/environment blocker.
- Sanitized-only posture is verified.
- Raw/PII/data-safety negative checks are recorded.
- User-facing docs explain setup, operation, limitations, and boundaries.
- Sprint11 completion status distinguishes automated validation from any
  human-gated live Canvas evidence.

## Review

Use a recorded self-review and, if the active surface supports it and the user
explicitly asks, subagent-independent or subagent-assisted review per
`docs/agent-guides/review-workflow.md`.

Review perspectives:

- spec compliance;
- Canvas skill compliance;
- raw/PII boundary;
- loopback/server lifecycle;
- input schema and bounded DTOs;
- docs consistency;
- generated/runtime artifact exclusion.
