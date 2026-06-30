# LocalMonitor Windows Task Scheduler Operation

This document describes the Windows user-level startup surface for Local
Ingestion Monitor. It is an operational wrapper around the existing loopback
monitor process. It supports repository-local `DotnetRun` mode and Release ZIP
`Published` mode.

## Purpose

Use Windows Task Scheduler to start LocalMonitor when the current Windows user
logs on. This avoids manually keeping a PowerShell foreground process open while
preserving the existing local-first boundary.

## Install

Release ZIP install only:

```powershell
.\scripts\install.ps1
```

This copies the published app to the per-user install root. It does not start
LocalMonitor and does not register Task Scheduler.

Repository-local startup registration:

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -StartNow
.\scripts\local-monitor\status.ps1
```

Release ZIP startup registration:

```powershell
.\scripts\install-startup-task.ps1 -Mode Published
.\scripts\status.ps1
```

Dry-run the generated task command without registering it:

```powershell
.\scripts\local-monitor\install-startup-task.ps1 -DryRun
```

## Start, Status, Stop

Release ZIP:

```powershell
.\scripts\start.ps1 -Mode Published
.\scripts\status.ps1
.\scripts\stop.ps1 -Force
```

Repository:

```powershell
.\scripts\local-monitor\start.ps1
.\scripts\local-monitor\status.ps1
.\scripts\local-monitor\stop.ps1 -Force
```

`status.ps1` reports installed state, process state, startup registration,
startup enabled state, task name, URL, DB path, log path, install root, app
version, `/health/live`, `/health/ready`, readiness status, degraded reasons,
projection lag, projection backlog, sanitized-only mode, and launch mode.

## Enable Or Disable Startup

Task Scheduler registration and enablement are separate. After registration:

```powershell
.\scripts\set-startup-task.ps1 -Action Disable
.\scripts\set-startup-task.ps1 -Action Enable
```

## Uninstall

Release ZIP:

```powershell
.\scripts\uninstall-startup-task.ps1 -StopRunning
```

Repository:

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
Install root: %LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\
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
- Release ZIP install, start-now, and startup registration are separate
  operations; install does not imply background startup.
- The monitor still enforces loopback bind, Host header validation, same-origin
  controls, and `Cache-Control: no-store` on raw-bearing surfaces.
- Wrapper logs contain operational facts only.
- Release ZIPs and workflow artifacts contain published app files and scripts,
  not runtime DB, logs, state, raw telemetry, credentials, or PII.
- Runtime DB, logs, pid/state files, and generated task state are local machine
  artifacts and must not be committed.
- This is not a Windows Service, shared server, IIS deployment, installer, tray
  app, or organization-wide telemetry collector.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `task_already_exists` | Re-run with `-Force` only when replacing the task is intended. |
| `published_app_not_installed` | Run `install.ps1`, or pass the correct `-InstallRoot`. |
| `task_not_registered` | Register startup before enabling or disabling it. |
| `non_loopback_url` | Use `http://127.0.0.1:<port>`, `http://localhost:<port>`, or `http://[::1]:<port>`. |
| `port_already_in_use` | Stop the other process or pass another loopback URL. |
| `monitor_start_timeout` | Inspect `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\logs\`. |
| `/health/live` is reachable but ready is `503` | Run `status.ps1` and inspect readiness status / degraded reasons. |
| VS Code telemetry does not arrive | Regenerate and apply `profile-vscode-env --profile raw-local-receiver --target monitor`, then start VS Code from that shell. |

## Manual Validation Evidence

Record the date, Windows version, PowerShell version, repository commit, ZIP
artifact name, task name, task trigger, URL, install root shape, DB path shape,
log path shape, health results, trace id or raw record id from VS Code / Copilot
Chat, and confirmation that Langfuse was not required. For Release ZIP
validation, also confirm that the machine did not need .NET SDK / Runtime to
start the published app.
