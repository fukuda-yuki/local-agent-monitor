# Sprint10 M5 — A2 Cache Explorer (Plan)

Status: **Done** — code, tests, and review recorded.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` -> `docs/spec.md` -> `docs/specifications/`. Sprint
context: [../../README.md](../../README.md) (M5 milestone row, D026 scope, and
Safety boundary). Decisions: `docs/decisions.md` D024-D028, especially D026.

## Objective

Implement the TraceDetail `Cache` tab as sanitized, client-side presentation over
the existing spans API. The view groups trace-local chat turns by the top-level
`invoke_agent` ancestor, using that root span as the available approximation of a
user request.

## Scope

- Replace the Cache tab placeholder in `TraceDetail.cshtml` with stable
  `cache-status` and `cache-groups` containers.
- Add `renderCacheExplorer()` to `wwwroot/monitor-views.js`. It reuses
  `fetchAllSpans(traceId)`, identifies chat turns by `operation == "chat"` or
  `category == "llm_call"`, traverses `parent_span_id` to the root
  `invoke_agent`, and renders grouped cache metrics with `createElement` and
  `textContent`.
- Add minimal Cache Explorer styling to `wwwroot/monitor.css`, matching the
  existing VS Code-styled dark theme.
- Add xUnit contract coverage for the Cache shell and sanitized script fields.

## Out Of Scope

- No backend endpoint, query parameter, API field, SQLite schema, telemetry input,
  dependency, CDN, or build-step change.
- No raw prompt prefix diff and no cross-trace stitching by `conversation_id`
  (D026).
- No Playwright smoke tests in M5; those remain M6 validation scope.

## Acceptance Criteria

- TraceDetail renders the Cache tab shell with `cache-status`, `cache-groups`,
  and `data-cache-trace-id`.
- `monitor-views.js` contains the Cache Explorer renderer and uses only sanitized
  span fields including `cache_read_tokens`, `cache_creation_tokens`,
  `input_tokens`, and `parent_span_id`.
- `monitor-views.js` still contains no `/raw`, no `innerHTML`, and no `Html.Raw`.
- Required validation commands pass:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorTraceDetailTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```
