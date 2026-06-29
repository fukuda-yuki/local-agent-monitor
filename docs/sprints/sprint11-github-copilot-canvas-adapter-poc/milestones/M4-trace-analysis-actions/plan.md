# Sprint11 M4 - Trace Analysis Actions (Plan)

Status: **Planned** - trace hierarchy and cache analysis actions. Gated by M3
minimal actions.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` -> `docs/spec.md` -> `docs/specifications/`. Sprint
context: [../../README.md](../../README.md).

## Objective

Add the deeper trace-analysis actions `get_trace_span_tree` and
`get_cache_summary`, with strict input schemas, bounded output, expected failure
handling, and explicit sanitized-output checks.

## Scope

In scope:

1. Add `get_trace_span_tree({ traceId })`.
   - Fetch `/api/monitor/traces/{traceId}/spans?limit=200`.
   - Convert sanitized spans to a compact hierarchy DTO using `span_id`,
     `parent_span_id`, and `span_ordinal`.
   - Include operation, category, sanitized names, model, status, error type,
     duration, start/end timestamps, token counts, cache tokens, and child
     references.
   - If parent ids are absent or incomplete, return a flat ordered list plus a
     `hierarchy_status` diagnostic rather than failing.
2. Add `get_cache_summary({ traceId })`.
   - Fetch sanitized spans only.
   - Summarize LLM/chat spans within the trace.
   - Compute cache hit rate as `cache_read_tokens / input_tokens` with a
     divide-by-zero guard.
   - Report cache-read tokens, cache-creation tokens, input/output/total tokens,
     model, duration, timestamp, and per-turn breakdown.
3. Add strict `inputSchema` for both actions.
4. Bound output:
   - maximum 200 spans read from the existing monitor API page;
   - maximum 50 tree nodes returned in the high-level tree unless the action
     explicitly returns a truncated flag;
   - maximum 50 cache turns returned.
5. Use `CanvasError` for monitor unavailable, invalid trace id, persistence busy,
   unsupported response shape, and trace not found.
6. Add helper checks that redact/drop any accidental forbidden fields before a
   DTO is returned.

Out of scope:

- Raw prompt prefix diffing.
- Cross-trace stitching by `conversation_id`.
- New monitor endpoints, fields, query parameters, or raw-bearing routes.
- A custom graph renderer inside the extension.

## Acceptance criteria

- `list_canvas_capabilities` shows `get_trace_span_tree` and
  `get_cache_summary`.
- Invalid input is rejected by schema.
- `get_trace_span_tree` returns sanitized hierarchy data without raw rows.
- `get_cache_summary` returns cache metrics and explicitly avoids prefix diffing.
- Both actions are bounded and include `truncated` or diagnostic fields when
  output is reduced.
- No action response includes raw prompt/response bodies, tool
  arguments/results, PII, credentials, tokens, local sensitive paths, or raw OTLP
  payloads.

## Validation

Canvas validation:

```text
invoke_canvas_action({ instanceId: "m4-smoke", actionName: "get_trace_span_tree", input: { traceId: "<synthetic-trace-id>" } })
invoke_canvas_action({ instanceId: "m4-smoke", actionName: "get_cache_summary", input: { traceId: "<synthetic-trace-id>" } })
```

Invalid-input checks:

```text
invoke_canvas_action({ instanceId: "m4-smoke", actionName: "get_trace_span_tree", input: {} })
invoke_canvas_action({ instanceId: "m4-smoke", actionName: "get_cache_summary", input: { traceId: "../raw" } })
```

Repository validation after extension changes:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

Security review:

- search the extension for raw route usage;
- inspect action outputs for forbidden strings/fields;
- verify `conversation_id` is not used for cross-trace grouping;
- verify no runtime artifacts were committed.
