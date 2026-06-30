# Sprint14: LocalMonitor Release ZIP Startup

Sprint14 makes LocalMonitor distributable as a Windows x64 self-contained
Release ZIP while preserving the existing local-first monitor boundary.

The goal is to move LocalMonitor from a repository-local `dotnet run` workflow
toward a user-machine published app that can be installed per user, started on
demand, and optionally registered for user-level logon startup. This sprint does
not turn LocalMonitor into a Windows Service, IIS app, machine-wide collector,
shared server, tray app, MSI, winget package, Intune package, Docker dependency,
or Langfuse/Collector bundle.

## Scope

- GitHub Actions creates `local-monitor-win-x64.zip`.
- The ZIP contains a self-contained folder publish under `app/` and operation
  scripts under `scripts/`.
- User machines do not need .NET SDK, `dotnet run`, `dotnet build`,
  `dotnet restore`, .NET Runtime, or ASP.NET Core Runtime for ZIP usage.
- Install, start-now, Task Scheduler registration, startup enable/disable,
  status, stop, and uninstall are separate operations.
- Task Scheduler startup remains current-user, least-privilege, AtLogOn, and
  opt-in only.
- Runtime DB / logs / state remain local runtime artifacts under
  `%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor\`.
- Uninstall keeps DB / logs by default and removes them only with an explicit
  remove-data option.

## Milestones

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Specs, Decisions, Sprint Setup | Promote Release ZIP and install lifecycle requirements into current specs, D032, roadmap, and this README. | Implemented |
| M2 Publish And Package Artifact | Add Windows x64 self-contained folder publish packaging and fixed ZIP layout. | Implemented |
| M3 User-Level Install Lifecycle | Add per-user app install and uninstall behavior with DB/log retention by default. | Implemented |
| M4 Published Mode Start, Stop, Status | Support `start.ps1 -Mode Published` and status fields for install/runtime/startup state. | Implemented |
| M5 Optional Logon Startup Registration | Keep startup registration separate; support current-user AtLogOn and enable/disable. | Implemented |
| M6 GitHub Actions Release ZIP Workflow | Add manual workflow that builds, tests, packages, and uploads the ZIP artifact. | Implemented |
| M7 Docs, Validation, Live Evidence | Update user/operator docs, automated tests, required validation, and Windows live evidence template. | Implemented; live evidence remains human-gated |

## Validation Gates

Required automated validation after code/workflow/doc changes:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Package smoke validation:

```powershell
pwsh scripts\local-monitor\package-release.ps1
```

Windows live evidence should record ZIP extraction, install-only, start-now
without startup registration, Task Scheduler registration, startup enable /
disable, readiness, uninstall with DB/log retention, explicit remove-data, and
confirmation that raw / PII did not appear in logs or artifacts.

## Data Boundary

Release ZIPs, workflow logs, workflow artifacts, README files, manifest files,
Issues, PRs, static dashboards, and GitHub Pages snapshots must not contain raw
prompt/response bodies, full tool arguments/results, PII, credentials, tokens,
local sensitive paths, raw OTLP payloads, runtime DB files, runtime logs, or
state files.

The packaged app is the same loopback-only LocalMonitor. Raw / PII display
continues to follow the existing LocalMonitor boundary: raw-bearing
server-rendered pages are local runtime surfaces, `/api/monitor/*` and SSE stay
sanitized, and `--sanitized-only` remains an optional metadata-only mode.
