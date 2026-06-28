# Sprint10 M6 Review — Validation

## Scope

M6 adds browser-level validation for the Sprint10 Local Monitor design views.
The change is test/documentation only:

- `Microsoft.Playwright` `1.61.0` is added to the LocalMonitor test project.
- `MonitorDesignViewPlaywrightTests` validates TraceDetail tab switching,
  Timeline errors-only filter and tokens/time sort, Flow Chart canvas rendering,
  Cache Explorer metrics, dark-theme CSS, and browser requests limited to the
  sanitized spans API.
- No production route, API field, schema, telemetry input, raw-bearing route,
  CDN, or frontend build step is added.

## Boundary Review

The Playwright smoke test records browser requests and asserts at least one
request to:

```text
/api/monitor/traces/{traceId}/spans
```

It also asserts no browser request URL contains `/raw`. Existing xUnit coverage
continues to cover sanitized JSON/SSE invariants, raw/PII non-leakage,
raw-bearing route `Cache-Control: no-store`, and `--sanitized-only` raw route
absence.

## Blockers

- `--sanitized-only` conflict remains unresolved by design. The Sprint10 README
  says the new views work identically under `--sanitized-only`, but current
  TraceDetail behavior returns `404` because the page is raw-bearing. M6 records
  this instead of changing behavior.
- Live VS Code Copilot Chat validation is pending user execution. M6 must not be
  marked complete until sanitized live evidence is supplied.
- `docs/sprints/sprint10-monitor-design-views/milestones/` has no M3 milestone
  folder, even though commit history and the Sprint10 README record M3 as done.
  M6 does not backfill a synthetic M3 review.

## Validation

Recorded during implementation:

```powershell
dotnet restore tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --ignore-failed-sources
dotnet build tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore
$env:PLAYWRIGHT_BROWSERS_PATH=(Resolve-Path .\tmp).Path + '\ms-playwright'
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\CopilotAgentObservability.LocalMonitor.Tests.dll --filter FullyQualifiedName~MonitorDesignViewPlaywrightTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter FullyQualifiedName~MonitorUiTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter FullyQualifiedName~MonitorSecurityBoundaryTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter FullyQualifiedName~MonitorSseTests
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx --no-restore
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\bin\Debug\net10.0\CopilotAgentObservability.ConfigCli.Tests.dll
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\CopilotAgentObservability.LocalMonitor.Tests.dll
```

Results:

- Targeted Playwright smoke test: 1 passed.
- `MonitorUiTests`: 16 passed.
- `MonitorSecurityBoundaryTests`: 12 passed.
- `MonitorSseTests`: 3 passed.
- Test project build: 0 warnings, 0 errors.
- Full solution build: 0 warnings, 0 errors.
- Full solution test command did not complete because restore / SDK resolution
  hit NuGet TLS authentication failures, including `Aspire.AppHost.Sdk`.
- `dotnet test CopilotAgentObservability.slnx --no-restore` also did not
  complete because solution evaluation still needed `Aspire.AppHost.Sdk/13.2.4`.
- Diagnostic direct DLL runs passed: ConfigCli 300 passed; LocalMonitor 247
  passed; combined 547 passed.

Notes:

- The normal NuGet source had intermittent TLS authentication failures in this
  environment after the initial package restore. `--ignore-failed-sources` and
  direct DLL test runs were used only as diagnostic / cached-package evidence;
  they are not substitutes for the required `dotnet test
  CopilotAgentObservability.slnx` command.
- The sandbox could not write Playwright browsers to the default user profile or
  `C:\tmp`, so browser binaries were installed under untracked `tmp/ms-playwright`.
