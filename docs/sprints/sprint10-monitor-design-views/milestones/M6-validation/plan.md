# Sprint10 M6 — Validation Plan

## Scope

M6 validates the Sprint10 Local Monitor design views without changing product
behavior. It adds browser-level Playwright smoke coverage and records the two
remaining gates:

- `--sanitized-only` conflict: current TraceDetail is raw-bearing and returns
  `404`, while the Sprint10 README says the new views work under
  `--sanitized-only`.
- Live VS Code Copilot Chat validation: user-gated and pending.

## Implementation

- Add `Microsoft.Playwright` `1.61.0` to the LocalMonitor test project only.
- Add `MonitorDesignViewPlaywrightTests` to validate tab switching, Timeline
  filter/sort, Flow Chart rendering, Cache Explorer rendering, dark-theme CSS,
  and browser requests limited to sanitized spans JSON.
- Keep production code, public interfaces, routes, schemas, telemetry inputs,
  query parameters, raw-bearing routes, and vendored runtime assets unchanged.
- Create M6 validation records and update Sprint10 tracking docs. Do not invent
  an M3 milestone record after the fact; note the missing M3 milestone folder in
  review instead.

## Validation Commands

```powershell
dotnet build tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorDesignViewPlaywrightTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSecurityBoundaryTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSseTests
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

If the default Playwright browser path is not writable in a sandbox, set
`PLAYWRIGHT_BROWSERS_PATH` to an untracked writable directory before installing
and running the Playwright tests.
