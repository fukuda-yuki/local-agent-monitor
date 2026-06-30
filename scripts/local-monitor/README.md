# LocalMonitor Windows Scripts

These scripts operate LocalMonitor in two modes:

- `DotnetRun`: repository-local development mode.
- `Published`: Release ZIP / installed app mode.

Both modes preserve the same loopback-only LocalMonitor boundary. The scripts
do not edit VS Code, Copilot CLI, or Codex routing settings.

## Release ZIP Usage

Extract `local-monitor-win-x64.zip`, then run from the extracted folder:

```powershell
.\scripts\install.ps1
.\scripts\start.ps1 -Mode Published
.\scripts\status.ps1
```

Install copies the app to:

```text
%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\
```

Install does not start LocalMonitor and does not register Task Scheduler. Start
and startup registration are separate choices.

Optional logon startup:

```powershell
.\scripts\install-startup-task.ps1 -Mode Published
.\scripts\set-startup-task.ps1 -Action Disable
.\scripts\set-startup-task.ps1 -Action Enable
```

Start immediately without registering startup:

```powershell
.\scripts\start.ps1 -Mode Published
```

Uninstall keeps DB and logs by default:

```powershell
.\scripts\uninstall-startup-task.ps1 -StopRunning
```

Remove runtime data only when explicitly intended:

```powershell
.\scripts\uninstall-startup-task.ps1 -StopRunning -RemoveData -Force
```

## Repository Development Usage

```powershell
.\scripts\local-monitor\start.ps1
.\scripts\local-monitor\status.ps1
.\scripts\local-monitor\stop.ps1 -Force
.\scripts\local-monitor\install-startup-task.ps1 -StartNow
```

Point VS Code / Copilot Chat at the monitor with the existing Config CLI source
of truth:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
```

Use `-SanitizedOnly` on `start.ps1` or `install-startup-task.ps1` to run the
metadata-only monitor posture.

## Packaging

From the repository root:

```powershell
pwsh scripts\local-monitor\package-release.ps1
```

The package script creates `artifacts\local-monitor-release\local-monitor-win-x64.zip`.
The ZIP contains the published app, scripts, README, manifest, and notices. It
must not contain runtime DB, logs, state, raw telemetry, credentials, or PII.

## Defaults

- URL: `http://127.0.0.1:4320`
- Install root: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\`
- DB: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\raw-store.db`
- Logs: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\logs\`
- Task: `CopilotAgentObservability LocalMonitor`
- Trigger: current user logon

## Troubleshooting

- `published_app_not_installed`: run `install.ps1`, or pass the correct `-InstallRoot`.
- `port_already_in_use`: another process owns `4320`; stop it or pass `-Url`.
- `task_already_exists`: rerun install with `-Force` only when replacing the task is intended.
- `task_not_registered`: register startup before enabling or disabling it.
- `health_ready_not_ready`: inspect `status.ps1` for readiness status and degraded reasons.
- `non_loopback_url`: only `127.0.0.1`, `localhost`, or `::1` HTTP URLs are accepted.

Runtime DB, logs, state files, and generated task state are local machine data
and must not be committed.
