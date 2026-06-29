# Sprint11 M3 - Minimal Actions (Plan)

Status: **Planned** - first agent-callable sanitized monitor actions. Gated by
M2 scaffold/runtime shape.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` -> `docs/spec.md` -> `docs/specifications/`. Sprint
context: [../../README.md](../../README.md).

## Objective

Add the first Canvas actions over sanitized monitor data:
`monitor_health`, `list_recent_traces`, and `get_trace_summary`. The actions
return bounded LLM-oriented DTOs and never raw monitor payload dumps.

## Scope

In scope:

1. Add action declarations to the `createCanvas({...})` configuration in
   `.github/extensions/otel-monitor-canvas/extension.mjs`.
2. Add strict `inputSchema` for each action:
   - `monitor_health`: no additional properties.
   - `list_recent_traces`: `limit` integer `1..50`, optional `status` enum
     `ok | error`, optional non-empty `model` string with a short max length.
   - `get_trace_summary`: required `traceId` string matching a trace-id token
     shape accepted by the monitor APIs.
3. Implement Local Monitor fetch helpers:
   - base URL must be loopback (`localhost`, `127.0.0.1`, or `[::1]`);
   - fetch `/health/ready` for health;
   - fetch `/api/monitor/traces?limit=...` for trace list;
   - fetch `/api/monitor/traces/{traceId}/spans?limit=200` only as needed to
     summarize top spans.
4. Return DTOs:
   - `monitor_health`: reachable boolean, ready HTTP status, readiness body,
     canvasSafe boolean, diagnostic message.
   - `list_recent_traces`: bounded array of trace summaries with trace id,
     client kind, span/tool/error counts, token totals, duration, primary model,
     first/last seen timestamps.
   - `get_trace_summary`: one trace summary plus top spans by duration/tokens,
     error count, cache token totals, and model list.
5. Use `CanvasError` for expected failures such as monitor unavailable,
   invalid monitor URL, trace not found, persistence busy, and non-2xx monitor
   responses.

Out of scope:

- Full span tree action.
- Cache-focused summary action.
- UI trigger.
- New monitor endpoints or query parameters.
- Returning raw monitor API responses verbatim.

## DTO safety rules

Action responses may include only:

- trace/span identifiers;
- sanitized operation/category/tool/MCP/agent names already returned by
  `/api/monitor/*`;
- model names;
- status and error type;
- durations and timestamps;
- token counts including cache-read and cache-creation counts.

Action responses must not include:

- raw prompt or response bodies;
- system prompt text;
- tool arguments or tool results;
- PII (`user.id`, `user.email`);
- credentials, tokens, secrets, local sensitive paths;
- raw OTLP payloads or `raw_record_id` unless needed only as an internal cursor.

## Acceptance criteria

- `list_canvas_capabilities` shows the three actions.
- Each action rejects invalid input through `inputSchema`.
- `monitor_health` reports unavailable monitor state without throwing an
  unstructured error.
- `list_recent_traces` is bounded and filters by derived status/model without
  reading raw routes.
- `get_trace_summary` returns an LLM-oriented summary for one trace and does not
  dump full span pages.
- No action fetches `/traces/{rawRecordId}/raw` or any raw-bearing route.

## Validation

Canvas validation:

```text
invoke_canvas_action({ instanceId: "m3-smoke", actionName: "monitor_health", input: {} })
invoke_canvas_action({ instanceId: "m3-smoke", actionName: "list_recent_traces", input: { limit: 10 } })
invoke_canvas_action({ instanceId: "m3-smoke", actionName: "get_trace_summary", input: { traceId: "<synthetic-trace-id>" } })
```

Invalid-input checks:

```text
invoke_canvas_action({ instanceId: "m3-smoke", actionName: "list_recent_traces", input: { limit: 0 } })
invoke_canvas_action({ instanceId: "m3-smoke", actionName: "list_recent_traces", input: { limit: 51 } })
invoke_canvas_action({ instanceId: "m3-smoke", actionName: "get_trace_summary", input: {} })
```

Repository validation after extension changes:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

Diff review:

- verify no committed raw telemetry or runtime artifacts;
- search extension code and docs for forbidden fields and raw route fetches;
- inspect action outputs for raw/PII/token/secret/path leakage.
