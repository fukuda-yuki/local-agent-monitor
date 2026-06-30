# LocalMonitor Windows Task Scheduler Operation

This document describes the Windows user-level startup surface for Local
Ingestion Monitor. It is an operational wrapper around the existing loopback
monitor process.

## Purpose

Use Windows Task Scheduler to start LocalMonitor when the current Windows user
logs on. This avoids manually keeping a PowerShell foreground process open while
preserving the existing local-first boundary.

## Install

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -StartNow
.\scripts\local-monitor\status.ps1
```

Dry-run the generated task command without registering it:

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -DryRun
```

## Start, Status, Stop

```powershell
.\scripts\local-monitor\start.ps1
.\scripts\local-monitor\status.ps1
.\scripts\local-monitor\stop.ps1 -Force
```

`status.ps1` reports task registration, process state, URL, DB path,
`/health/live`, `/health/ready`, readiness status, degraded reasons, projection
lag, projection backlog, and mode.

## Uninstall

```powershell
.\scripts\local-monitor\uninstall-startup-task.ps1 -StopRunning
```

DB and logs are kept by default. Runtime data is removed only with
`-RemoveData`; without `-Force`, that path asks for confirmation.

## Defaults

```text
Task name: CopilotAgentObservability LocalMonitor
Trigger: current user logon
URL: http://127.0.0.1:4320
DB: %LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\raw-store.db
Logs/state: %LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\
```

Task registration does not edit VS Code or Copilot settings. Use the existing
Config CLI output to point VS Code at the monitor:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
```

## Sanitized-Only Mode

Use `-SanitizedOnly` when installing or starting the task if the always-on
process should omit raw-bearing routes and PII:

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -SanitizedOnly -StartNow
```

Without `-SanitizedOnly`, LocalMonitor keeps its normal raw-default local UI
posture.

## Security Boundary

- The wrapper accepts only loopback HTTP URLs.
- The monitor still enforces loopback bind, Host header validation, same-origin
  controls, and `Cache-Control: no-store` on raw-bearing surfaces.
- Wrapper logs contain operational facts only.
- Runtime DB, logs, pid/state files, and generated task state are local machine
  artifacts and must not be committed.
- This is not a Windows Service, shared server, IIS deployment, installer, tray
  app, or organization-wide telemetry collector.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `task_already_exists` | Re-run with `-Force` only when replacing the task is intended. |
| `non_loopback_url` | Use `http://127.0.0.1:<port>`, `http://localhost:<port>`, or `http://[::1]:<port>`. |
| `port_already_in_use` | Stop the other process or pass another loopback URL. |
| `monitor_start_timeout` | Inspect `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\logs\`. |
| `/health/live` is reachable but ready is `503` | Run `status.ps1` and inspect readiness status / degraded reasons. |
| VS Code telemetry does not arrive | Regenerate and apply `profile-vscode-env --profile raw-local-receiver --target monitor`, then start VS Code from that shell. |

## Manual Validation Evidence

Record the date, Windows version, PowerShell version, .NET SDK/runtime version,
repository commit, task name, task trigger, URL, DB path shape, health results,
trace id or raw record id from VS Code / Copilot Chat, and confirmation that
Langfuse was not required.
