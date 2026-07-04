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

## M6-Time Blockers

- At M6 time, the `--sanitized-only` conflict was unresolved by design. The
  Sprint10 README said the new views work identically under `--sanitized-only`,
  but TraceDetail returned `404` because the page was raw-bearing. M6 recorded
  this instead of changing behavior.
- Live VS Code Copilot Chat validation is pending user execution. M6 must not be
  marked complete until sanitized live evidence is supplied.
- `docs/sprints/sprint10-monitor-design-views/milestones/` has no M3 milestone
  folder, even though commit history and the Sprint10 README record M3 as done.
  M6 does not backfill a synthetic M3 review.

## Current Completion State

Updated on 2026-06-29 after the Sprint10 bug-fix commits:

- S10-1 resolved the `--sanitized-only` TraceDetail conflict. The sanitized tabs
  now remain available while the raw section and full raw links are absent.
- S10-2 resolved the Playwright bootstrap gap. Chromium is installed before the
  standard solution test command in CI and local validation docs.
- S10-4 resolved the LocalMonitor test-host port allocation race. Socket-bound
  tests now use a shared dynamic-port helper instead of `GetFreePort()`
  preselection.
- S10-5 resolved the shutdown-drain validation gap. A gated worker regression
  proves `StopAsync` waits for an already-accepted queue item to commit before
  returning.
- Live VS Code Copilot Chat validation is still pending user execution. M6 must
  not be marked complete until sanitized live evidence is supplied.

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

Follow-up validation for the Sprint10 BUG_ISSUE cleanup on 2026-06-29:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

Results:

- Full solution build: 0 warnings, 0 errors.
- Playwright Chromium install: passed.
- Full solution test: passed, 300 ConfigCli tests and 250 LocalMonitor tests.
- The evidence remains automated / synthetic only; live VS Code Copilot Chat
  validation is still pending user execution.
