# Sprint12 M6 - Validation (Plan)

## Goal

Validate that the Sprint12 visual implementation is correct, stable, and does not change existing monitor contracts.

## Required Validation

Run from repository root:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

## Browser Smoke

Validate the Local Monitor pages at representative desktop and narrow widths:

- `/`
- `/ingestions`
- `/traces`
- `/traces/{traceId}`
- `/diagnostics`

Checks:

- Header, navigation, tables, metrics, tabs, banners, raw preview, and filter controls render without overlap.
- TraceDetail sanitized tabs still render from sanitized spans data.
- Raw-bearing sections remain server-rendered and unchanged in boundary.
- `--sanitized-only` omits raw sections and full raw links while keeping sanitized tabs available.
- No new CDN or external runtime fetch is introduced.

## Completion Evidence

Record:

- command results;
- browser smoke result;
- screenshots or screenshot paths when available;
- any unverified live-only scope.
