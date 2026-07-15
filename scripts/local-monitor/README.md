# LocalMonitor Windows Scripts

These scripts operate LocalMonitor in two modes:

- `DotnetRun`: repository-local development mode.
- `Published`: Release ZIP / installed app mode.

Both modes preserve the same loopback-only LocalMonitor boundary. LocalMonitor
lifecycle scripts do not edit VS Code, Copilot CLI, or Codex routing settings.
The separate `setup.ps1` wrapper changes only an explicitly planned GitHub
Copilot setup change set.

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

Optional current-user telemetry environment:

```powershell
.\scripts\install-user-env.ps1
.\scripts\uninstall-user-env.ps1
```

This persists raw-local-receiver / monitor routing in the current Windows
user's environment for newly started VS Code and Copilot CLI processes. Restart
already-running clients after changing it.

Guided GitHub Copilot setup is available from the extracted Release ZIP. Copy
the `change_set_id` from the plan result into the later commands:

```powershell
.\scripts\setup.ps1 plan --adapter github-copilot --target all
.\scripts\setup.ps1 apply --change-set <change-set-id>
.\scripts\setup.ps1 status --adapter github-copilot
.\scripts\setup.ps1 rollback --change-set <change-set-id>
```

The wrapper invokes the packaged self-contained Config CLI under
`app/config-cli/`; no installed .NET SDK or runtime is required. Each invocation
preserves the CLI exit code and emits exactly one `setup.v1` JSON result on
stdout. Setup success verifies static configuration only. It does not prove a
trace arrived; the `run_first_trace_doctor` next action hands that verification
off to Issue #69.

Optional Copilot CLI / VS Code Session Hooks forwarding is a separate opt-in:

```powershell
.\scripts\install-session-hooks.ps1
.\scripts\uninstall-session-hooks.ps1
```

The installer creates only `~/.copilot/hooks/local-agent-monitor.json`. It is
idempotent for the LocalMonitor-managed file and refuses to overwrite or remove
an existing file without its management marker. Normal LocalMonitor install
does not enable Hooks. Hook forwarding is fail-open: malformed input, an
unavailable collector, or the 250 ms timeout never changes the agent decision.

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

The same guided setup flow uses the repository Config CLI project:

```powershell
.\scripts\local-monitor\setup.ps1 plan --adapter github-copilot --target all
.\scripts\local-monitor\setup.ps1 apply --change-set <change-set-id>
.\scripts\local-monitor\setup.ps1 status --adapter github-copilot
.\scripts\local-monitor\setup.ps1 rollback --change-set <change-set-id>
```

Point VS Code / Copilot Chat at the monitor with the existing Config CLI source
of truth for a single shell:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
```

For all newly started processes under the current Windows user, use:

```powershell
.\scripts\local-monitor\install-user-env.ps1
```

Use `-SanitizedOnly` on `start.ps1` or `install-startup-task.ps1` to run the
metadata-only monitor posture.

## Packaging

From the repository root:

```powershell
pwsh scripts\local-monitor\package-release.ps1
```

The package script creates `artifacts\local-monitor-release\local-monitor-win-x64.zip`.
The ZIP contains the published LocalMonitor app, the self-contained Config CLI
under `app/config-cli/`, `scripts/setup.ps1`, the remaining scripts, README,
manifest, and notices. It must not contain runtime DB, logs, state, raw
telemetry, credentials, or PII.

## Defaults

- URL: `http://127.0.0.1:4320`
- Install root: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\`
- DB: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\raw-store.db`
- Logs: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\logs\`
- Task: `CopilotAgentObservability LocalMonitor`
- Trigger: current user logon
- User env endpoint: `http://127.0.0.1:4320`

## Troubleshooting

- `published_app_not_installed`: run `install.ps1`, or pass the correct `-InstallRoot`.
- `port_already_in_use`: another process owns `4320`; stop it or pass `-Url`.
- `task_already_exists`: rerun install with `-Force` only when replacing the task is intended.
- `task_not_registered`: register startup before enabling or disabling it.
- `health_ready_not_ready`: inspect `status.ps1` for readiness status and degraded reasons.
- `non_loopback_url`: only `127.0.0.1`, `localhost`, or `::1` HTTP URLs are accepted.

Runtime DB, logs, state files, and generated task state are local machine data
and must not be committed.
