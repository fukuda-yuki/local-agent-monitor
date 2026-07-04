# Sprint10 M5 Review — Cache Explorer

## Scope

M5 implements A2 Cache Explorer on the Local Ingestion Monitor trace-detail
page. The Cache tab now renders client-side from the existing sanitized spans
API and groups `chat` / `llm_call` turns by the trace-local root `invoke_agent`
ancestor. It shows cache-hit rate, cache read / creation tokens, token
breakdown, duration, model, and timestamp.

No backend endpoint, SQLite schema, projection field, query parameter,
dependency, CDN reference, build step, or raw-bearing route changed.

## Boundary Review

The Cache Explorer reads only:

```text
GET /api/monitor/traces/{traceId}/spans?after=...&limit=200
```

DOM insertion uses `createElement` and `textContent`; `monitor-views.js` still
does not use `innerHTML`, `Html.Raw`, or any raw route. Raw OTLP preview remains
server-rendered in the lower trace-detail section, preserving D020/D023.

Out-of-scope items from D026 remain out: no raw prompt prefix diff and no
cross-trace stitching by `conversation_id`.

## Validation

Recorded during implementation:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorTraceDetailTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Results:

- `MonitorTraceDetailTests`: 12 passed. RED was observed first on
  `TraceDetail_RendersCacheExplorerShell` because `cache-status` was absent.
- `MonitorUiTests`: 16 passed. RED was observed first on
  `MonitorViewsScript_UsesSanitizedSpanFieldsForCacheExplorer` because
  `renderCacheExplorer` was absent.
- solution build: 0 warnings, 0 errors.
- solution test: 546 passed (300 ConfigCli + 246 LocalMonitor).

Final full-solution test attempts hit an unrelated intermittent failure in
`IngestionWriterWorkerTests.Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown`
while running both test assemblies from the solution. The failing test passed
when rerun directly, the LocalMonitor test assembly passed as a whole, and the
same full-solution command was rerun unchanged and passed.
