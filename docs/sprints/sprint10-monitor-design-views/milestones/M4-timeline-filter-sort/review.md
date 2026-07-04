# Sprint10 M4 Review — Timeline Filter Sort

## Scope

M4 implements A4 Timeline filter/sort on the Local Ingestion Monitor trace-detail
page. The Timeline tab now renders a flat span list client-side from the existing
sanitized spans API. It adds:

- an `Errors only` status filter;
- `Time` and `Tokens` sort controls;
- JS-created timeline rows with stable `data-span-row-id` targets for the M3
  Flow Chart node-click interaction.

No backend endpoint, SQLite schema, projection field, query parameter, dependency,
CDN reference, build step, or raw-bearing route changed.

## Boundary Review

The new Timeline renderer reads only:

```text
GET /api/monitor/traces/{traceId}/spans?after=...&limit=200
```

DOM insertion uses `createElement` and `textContent`; `monitor-views.js` still
does not use `innerHTML`, `Html.Raw`, or any `/raw` route. Raw OTLP preview
remains server-rendered in the lower trace-detail section, preserving D020/D023.

## Validation

Recorded during implementation:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorTraceDetailTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Results:

- `MonitorTraceDetailTests`: 11 passed.
- `MonitorUiTests`: 15 passed.
- solution build: 0 warnings, 0 errors.
- solution test: 544 passed (300 ConfigCli + 244 LocalMonitor).

One first full-test attempt hit a transient LocalMonitor test port bind race
(`address already in use`) in an unrelated `MonitorProjectionApiTests` case; the
same command was rerun unchanged and passed.
