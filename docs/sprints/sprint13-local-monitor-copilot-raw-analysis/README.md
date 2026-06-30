# Sprint13 - Local Monitor Copilot Raw Analysis

Status: Complete with BYOK live Copilot SDK validation.

Sprint13 adds a Local Monitor raw-analysis surface that lets a local user start
a Copilot SDK analysis run for a selected trace. The monitor creates a local
analysis run, dispatches it to the in-process .NET GitHub Copilot SDK analysis
service, and keeps raw analysis results as local runtime data. Repository-safe summary export is a
separate allowlist output that excludes raw prompt / response / tool content,
PII, credentials, local sensitive paths, and source fragments.

## Boundary

- Raw analysis is active only in the normal raw-default Local Monitor posture.
- `--sanitized-only` removes the raw analysis UI, start route, and result route.
- New raw-bearing routes are under `/traces/{traceId}/analysis/...`, not
  `/api/monitor/*`.
- Existing `/api/monitor/*` and `/events` remain sanitized-only.
- The existing Sprint11 Canvas adapter remains separate and raw-safe.

## Milestones

| Milestone | Summary | Status |
| --- | --- | --- |
| M0 | Worktree and baseline | Branch/worktree creation blocked by git common-dir permissions; baseline build/test passed in existing clean linked worktree |
| M1 | Specs and boundary | Implemented |
| M2 | Persistence and local routes | Implemented |
| M3 | .NET Copilot SDK service and internal tools | Implemented and BYOK live-validated |
| M4 | Orchestration and status handling | Implemented |
| M5 | TraceDetail UI and safe summary export | Implemented |
| M6 | Regression and live validation | Completed with automated validation and BYOK live SDK validation |

## User-provided live validation inputs

- GitHub Copilot app/CLI runtime that supports the GA .NET GitHub Copilot SDK,
  or compatible BYOK provider configuration.
- Running raw-default Local Monitor.
- A local trace/raw record acceptable for raw analysis.
- Human confirmation before using any repository-safe summary in GitHub Issue,
  PR, docs, or dashboard context.
