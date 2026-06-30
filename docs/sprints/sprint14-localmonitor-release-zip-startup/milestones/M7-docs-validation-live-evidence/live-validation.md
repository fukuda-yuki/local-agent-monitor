# Sprint14 M7 Live Validation

Status: Pending human-run Windows validation.

Record only non-sensitive evidence. Do not paste raw prompts, raw responses,
tool arguments/results, PII, credentials, tokens, raw OTLP payloads, full local
sensitive paths, runtime DB contents, or log contents that include sensitive
data.

## Environment

- Date:
- Windows version:
- PowerShell version:
- Repository commit:
- ZIP artifact name: `local-monitor-win-x64.zip`
- LocalMonitor version:
- URL: `http://127.0.0.1:4320`
- DB path shape: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\raw-store.db`
- Log path shape: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\logs\`
- Install root shape: `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\app\`

## Checks

| Check | Result | Evidence |
| --- | --- | --- |
| ZIP extracts and contains `app/`, `scripts/`, `README.md`, `manifest.json`, notices | Pending | |
| Install-only copies app and does not register Task Scheduler | Pending | |
| `start.ps1 -Mode Published` starts without .NET SDK usage | Pending | |
| `/health/live` reachable | Pending | |
| `/health/ready` returns ready or degraded | Pending | |
| `status.ps1` reports install, running, readiness, paths, app version, startup state, task name, sanitized-only mode | Pending | |
| `install-startup-task.ps1 -Mode Published -DryRun` shows current-user AtLogOn task shape | Pending | |
| Actual Task Scheduler registration uses current user / least privilege / AtLogOn / IgnoreNew | Pending | |
| `set-startup-task.ps1 -Action Disable` disables startup | Pending | |
| `set-startup-task.ps1 -Action Enable` enables startup | Pending | |
| `uninstall-startup-task.ps1` keeps DB / logs by default | Pending | |
| `uninstall-startup-task.ps1 -RemoveData -Force` removes runtime data when explicitly requested | Pending | |
| Wrapper logs contain operational facts only and no raw / PII / credentials | Pending | |
| ZIP / workflow artifact contains no runtime DB / logs / state / raw telemetry | Pending | |

## Notes

- Trace id or raw record id may be recorded as opaque identifiers.
- Do not include raw monitor page content or raw store excerpts.
