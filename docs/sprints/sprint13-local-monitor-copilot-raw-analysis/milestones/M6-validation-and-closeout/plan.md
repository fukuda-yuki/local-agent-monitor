# Sprint13 M6 - Validation And Closeout Plan

## Goal

Validate regression coverage and record what remains human-gated.

## Required automated validation

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

## Live validation

Use a GitHub Copilot app/CLI runtime that supports the GA .NET GitHub Copilot
SDK, then validate:

- .NET SDK package/runtime version.
- `dotnet build CopilotAgentObservability.slnx` in an environment where
  `GitHub.Copilot.SDK` can be restored.
- SDK analysis invocation for all raw and summary tools.
- Local Monitor raw-default trace analysis run start.
- raw tool access through process-internal .NET tool calls.
- repository-safe summary output without raw content.

Record date, environment, Copilot runtime version, monitor URL, trace id/raw
record id, confirmed items, and unconfirmed items.
