---
name: validate
description: Run the pinned repository validation suite (solution build, Playwright Chromium install, solution tests, optional collector config check) and report results in the required format
---

# Repository Validation Suite

Run the pinned validation commands from the repository root, in order.
These are the commands defined in `AGENTS.md`; do not substitute different
commands when one fails — report the failure instead.

## Commands

1. `dotnet build CopilotAgentObservability.slnx`
2. `pwsh scripts\test\install-playwright-chromium.ps1`
   - Required because the solution test suite contains LocalMonitor
     Playwright browser smoke tests. The wrapper sets
     `PLAYWRIGHT_BROWSERS_PATH` to `artifacts\playwright-browsers` when unset.
   - On Linux CI, pass `-WithDeps`.
3. `dotnet test CopilotAgentObservability.slnx`
4. Only when files under `infra\otel-collector\` changed:

   ```powershell
   $env:LANGFUSE_AUTH="dummy"
   docker compose -f infra\otel-collector\docker-compose.example.yml config
   ```

Targeted test while iterating (NOT a substitute for the full suite):

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~<test-or-class>
```

## Failure policy

- If a command fails, is skipped, or cannot run because a tool is missing,
  stop and report the blocker. Do not treat a different command as an
  equivalent success.
- Diagnostic commands may be useful follow-up evidence, but they do not
  replace the required validation.

## Report format

Always end with:

- Commands run and their results (including pass/fail test counts).
- Unverified scope.
- Exact commands still needed, if any.
