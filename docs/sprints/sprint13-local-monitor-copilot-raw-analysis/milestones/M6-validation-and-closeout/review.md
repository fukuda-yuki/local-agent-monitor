# Sprint13 M6 - Validation And Closeout Review

## Automated validation

Run from repository root:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Results:

- `dotnet build CopilotAgentObservability.slnx`: passed, 0 warnings, 0 errors.
- `pwsh scripts\test\install-playwright-chromium.ps1`: passed.
- `dotnet test CopilotAgentObservability.slnx`: passed, 301 ConfigCli + 272 LocalMonitor tests.
- `GitHub.Copilot.SDK` package restore/build: passed with version 1.0.4.
  Live signed-in .NET SDK invocation remains unverified.

## Coverage added

- Analysis persistence, lifecycle status, local raw result, and
  repository-safe summary tests.
- Raw analysis route tests for `--sanitized-only`, CSRF, .NET runner dispatch,
  absence of old bridge routes, and safe summary raw-marker exclusion.
- TraceDetail UI tests for the raw analysis action and sanitized-only omission.

## Live validation

Not run in this environment. It requires a GitHub Copilot app/CLI runtime that
can run the GA .NET GitHub Copilot SDK and a signed-in Copilot session.

Remaining live checks:

- record the .NET SDK package/runtime version.
- invoke analysis with each internal raw/summary tool available to the SDK.
- confirm raw tool data reaches Copilot only through the Local Monitor process.
- confirm repository-safe summary remains raw-free when used outside Local
  Monitor.
