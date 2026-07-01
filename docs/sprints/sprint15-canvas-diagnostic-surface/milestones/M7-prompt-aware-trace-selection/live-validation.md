# Sprint15 M7 — Live Canvas Validation Evidence

Sprint-local validation evidence, not product behavior. Source of truth:
`docs/requirements.md`, `docs/spec.md`, `docs/specifications/`.

## Environment

- Runtime: GitHub Copilot CLI 1.0.66-1, Windows_NT
- Canvas runtime tools available: `extensions_reload`, `extensions_manage`,
  `list_canvas_capabilities`, `open_canvas`, `invoke_canvas_action`
- Local Monitor: started on `http://127.0.0.1:4320` in raw-default posture with
  a local validation DB for this milestone
- Synthetic data:
  - prompt-bearing trace `22222222222222222222222222222222`
  - prompt-less trace `11111111111111111111111111111111`
  - current-session ingestion trace `2973759d1661a72ff460945befd61649`

## Repository validation

| Check | Result |
|---|---|
| `dotnet build CopilotAgentObservability.slnx` | 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | completed successfully |
| `dotnet test CopilotAgentObservability.slnx` | 611 passing, 0 failed, 0 skipped |

## Local Monitor route checks

| Route | Result |
|---|---|
| `GET /traces/22222222222222222222222222222222/prompt-label` | `200`, `Cache-Control: no-store`, prompt label present |
| `GET /traces/11111111111111111111111111111111/prompt-label` | `200`, `Cache-Control: no-store`, `prompt_label: null` |
| `GET /traces/does-not-exist-at-all/prompt-label` | `200`, `Cache-Control: no-store`, `prompt_label: null` |

## Canvas runtime validation (live)

### Extension discovery

| Step | Result |
|---|---|
| `extensions_reload` | `otel-monitor-canvas` reloaded and ready |
| `extensions_manage({ operation: "inspect", name: "otel-monitor-canvas" })` | running, PID reported, log available |
| `list_canvas_capabilities({ canvasId: "otel-monitor" })` | 5 actions returned with expected schemas |

### Canvas open

| Step | Result |
|---|---|
| `open_canvas({ canvasId: "otel-monitor", instanceId: "m7-live", input: { monitorBaseUrl: "http://127.0.0.1:4320" } })` | title `OTel Monitor`, status `Connected`, loopback URL returned |

### Browser dropdown check

| Check | Result |
|---|---|
| Helper page title | `OTel Monitor — Canvas` |
| Trace dropdown | prompt-bearing trace shows `prompt label — line`; prompt-less trace shows line only |
| Fallback text | no `null —` or `undefined —` text observed |

### Action surface checks

| Action | Result |
|---|---|
| `monitor_health` | reachable, ready, bounded DTO only |
| `list_recent_traces` | 3 traces, no prompt text in response |
| `get_trace_summary` for `22222222222222222222222222222222` | bounded DTO only, no prompt text in response |

### Action safety — schema rejection

| Input | Result |
|---|---|
| `list_recent_traces` with `{ limit: 100 }` | rejected by schema |
| `get_trace_summary` with `{ traceId: "bad id!" }` | rejected by schema |

### Action safety — raw/PII negative checks

All inspected action responses were free of:

- raw prompt bodies
- raw response bodies
- tool arguments / tool results
- PII
- credentials, tokens, local sensitive paths

## Summary

| Acceptance criterion | Status |
|---|---|
| Repository build/test validation is green | ✅ passed |
| Canvas extension discovered, inspected, opened | ✅ passed |
| New prompt-label route works for present and absent labels | ✅ passed |
| Helper page dropdown uses `prompt_label — line` when available | ✅ passed |
| Bounded Canvas actions remain free of prompt text | ✅ passed |
| Invalid inputs rejected by schema | ✅ passed |

