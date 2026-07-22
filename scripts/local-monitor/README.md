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
trace arrived; Issue #69 owns that verification. A successful changed CLI apply
by the Claude adapter emits `restart_claude_process` followed by
`run_first_trace_doctor` as a handoff to
`first-trace begin --adapter claude-code`; it is not telemetry evidence.

After setup, select the exact source and start the first-trace Doctor. The
wrapper supplies the installed LocalMonitor runtime database path; no source
checkout, .NET installation, or environment-variable editing is required:

```powershell
.\scripts\first-trace.ps1 begin --adapter github-copilot-vscode --json
.\scripts\first-trace.ps1 begin --adapter github-copilot-cli --json
.\scripts\first-trace.ps1 begin --adapter github-copilot-app-sdk --json
.\scripts\first-trace.ps1 begin --adapter claude-code --json
.\scripts\first-trace.ps1 status --verification-id <verification-id> --json
.\scripts\first-trace.ps1 complete --verification-id <verification-id> --expected-revision <revision> --evidence <opaque-reference> --json
.\scripts\first-trace.ps1 cancel --verification-id <verification-id> --expected-revision <revision> --json
```

`status`, `complete`, and `cancel` operate only on the exact verification ID.
`complete` accepts only explicitly selected opaque evidence references; the
wrapper does not select a latest trace or infer evidence from timestamps. For a
supported rollback journey, cancel the active verification first, run the exact
source setup rollback, and then refresh the cancelled verification with
`status`. Rollback does not erase Doctor history or create a passing state.
Uninstall only after cancellation and rollback. Normal uninstall keeps the
Doctor database and logs, so reinstall can inspect the same persisted state.
`-RemoveData -Force` permanently removes that state.

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

## Runtime backup and offline restore

The loopback page `http://127.0.0.1:4320/backup-restore` creates an online
SQLite backup and inspects a selected archive. It intentionally cannot restore
the live database. Runtime backup ZIPs may contain raw prompts, responses, tool
arguments, results, and PII. They are not repository-safe and are not purged by
retention cleanup; `retention_backup_not_purged` records that operator-owned
file boundary.

For an extracted release, run the packaged Config CLI directly:

```powershell
$cli = '.\app\config-cli\CopilotAgentObservability.ConfigCli.exe'
$db = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\raw-store.db'
& $cli runtime-backup create --database $db --output C:\private\local-monitor-backup.zip
& $cli runtime-backup inspect --bundle C:\private\local-monitor-backup.zip
& $cli runtime-backup preview --bundle C:\private\local-monitor-backup.zip --database $db
```

Restore is an offline operation. Set the start parameters to the intended
installed instance before stopping it; `stop.ps1` removes ephemeral process
state and must not be used as the source of restart configuration.

```powershell
$cli = '.\app\config-cli\CopilotAgentObservability.ConfigCli.exe'
$stopScript = '.\scripts\stop.ps1'
$startScript = '.\scripts\start.ps1'
$monitorUrl = 'http://127.0.0.1:4320'
$db = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\raw-store.db'
$installRoot = Join-Path $env:LOCALAPPDATA 'CopilotAgentObservability\LocalMonitor\app'
$sanitizedOnly = $false # Use $true only when restoring the metadata-only instance.
$startParameters = @{
    Mode = 'Published'
    Url = $monitorUrl
    DbPath = $db
    InstallRoot = $installRoot
    SanitizedOnly = $sanitizedOnly
    NoBrowser = $true
    WaitReady = $true
}

& $stopScript -Force
$stopExitCode = $LASTEXITCODE
if ($stopExitCode -ne 0) {
    exit $stopExitCode
}

& $cli runtime-backup restore --bundle C:\private\local-monitor-backup.zip --database $db
$restoreExitCode = $LASTEXITCODE
if ($restoreExitCode -ne 0) {
    exit $restoreExitCode
}

& $startScript @startParameters
$startExitCode = $LASTEXITCODE
if ($startExitCode -ne 0) {
    exit $startExitCode
}
```

Capture `$LASTEXITCODE` immediately after each command as shown. In particular,
never invoke `start.ps1` after a failed restore. With `-WaitReady`, the Published
start succeeds only after `/health/ready` reports canonical `ready` or accepted
`degraded`; `not_ready` or an unreachable endpoint is a failure.

An existing destination gets a validated pre-restore backup by default. If
preview reports a non-terminal missing source that would be reintroduced, add
`--allow-resurrection --confirmation <digest>` to the restore invocation only
after reviewing that exact archive-bound digest. Confirmation never removes a
current tombstone or read denial. A different machine must have a compatible
release installed and must rerun setup after restore because setup ownership,
credentials, executables, PID/state, and logs are excluded as host-bound or
ephemeral state.

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
under `app/config-cli/`, `scripts/setup.ps1`, `scripts/first-trace.ps1`, the
remaining scripts, README, manifest, and notices. It must not contain runtime
DB, logs, state, raw telemetry, credentials, or PII.

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
