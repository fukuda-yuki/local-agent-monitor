# LocalMonitor Windows Startup

These scripts provide a user-level Windows Task Scheduler startup surface for
the Local Ingestion Monitor. The task starts the monitor when the current user
logs on and keeps runtime data under `%LOCALAPPDATA%`.

## Quick Start

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -StartNow
.\scripts\local-monitor\status.ps1
```

Point VS Code / Copilot Chat at the monitor with the existing Config CLI source
of truth:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
```

## Commands

```powershell
.\scripts\local-monitor\start.ps1
.\scripts\local-monitor\status.ps1
.\scripts\local-monitor\stop.ps1 -Force
.\scripts\local-monitor\install-startup-task.ps1 -StartNow
.\scripts\local-monitor\uninstall-startup-task.ps1 -StopRunning
```

Use `-SanitizedOnly` on `start.ps1` or `install-startup-task.ps1` to run the
metadata-only monitor posture.

## Defaults

- URL: `http://127.0.0.1:4320`
- DB: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\raw-store.db`
- Logs: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\logs\`
- Task: `CopilotAgentObservability LocalMonitor`
- Trigger: current user logon

The scripts do not edit VS Code settings. They only start, stop, inspect, or
register the local monitor process.

## Troubleshooting

- `port_already_in_use`: another process owns `4320`; stop it or pass `-Url`.
- `dotnet` not found: install the repository's required .NET SDK.
- `task_already_exists`: rerun install with `-Force` only when replacing the task
  is intended.
- `health_ready_not_ready`: inspect `.\scripts\local-monitor\status.ps1` for the
  readiness status and degraded reasons.
- `non_loopback_url`: only `127.0.0.1`, `localhost`, or `::1` HTTP URLs are
  accepted.

Runtime DB, logs, state files, and generated task state are local machine data
and must not be committed.
