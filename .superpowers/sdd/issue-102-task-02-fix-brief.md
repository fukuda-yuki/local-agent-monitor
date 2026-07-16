# Issue #102 Task 2 Fix Brief — Stateless Evaluation Security

## Identity

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-doctor-core`
- Starting HEAD: `287f0c302ccbd24b9cef91acb98747d8635319f7` plus the uncommitted Task 2 diff
- Preferred runtime: GPT-5.6 Luna with xhigh reasoning; unavailable as an enforceable dispatch selector.
- Input review: `.superpowers/sdd/issue-102-task-02-review-report.md`
- Report path: `.superpowers/sdd/issue-102-task-02-fix-report.md`

## Purpose

Resolve the Task 2 independent review's one Important and one Minor finding without expanding the thin slice.

## Scope

- `tests/CopilotAgentObservability.Doctor.Tests/DoctorCrossSurfaceContractTests.cs`
- `src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs`
- the fix report only

## Required TDD Fix

1. Remove the mutation CSRF header from the valid stateless evaluation request in the cross-surface test.
2. Run the focused test and observe RED: the current route returns 403 instead of the canonical 200.
3. Remove same-origin/CSRF enforcement from `POST /api/doctor/evaluations` only. Retain existing global loopback bind and Host-header protection. Do not weaken or pre-implement later state-changing verification routes.
4. Rerun the focused test to GREEN.
5. Make `RunningDoctorMonitor.StartAsync` clean up any partially built/started app and its temporary directory when build/start throws. Keep cleanup deterministic and do not use sleep, polling, or retry.
6. Rerun focused test and solution build.

## Non-Scope and Constraints

- Do not edit canonical specs, other production/tests, Task 2 implementer/review reports, ledger, plan, persistence, CLI behavior, remaining states, later routes, source adapters, or UI.
- Do not accept the Minor silently; resolve it and report how.
- Use `apply_patch`. Do not commit, push, create a PR, or integrate `main`.
- Do not expose paths, request bodies, exception text, or runtime artifacts.

## Completion Conditions

- The real evaluation HTTP request succeeds without `x-monitor-csrf` and still returns 200/application-json/no-store/equal `DoctorResult`.
- Global Host protection remains unchanged.
- Startup failure cannot leak the app or temporary directory.
- Focused test passes 1/1, build has 0 warnings/errors, diff check passes, and scope is limited to the two files plus report.

## Verification

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorCrossSurfaceContractTests
dotnet build CopilotAgentObservability.slnx
git diff --check
git status --short --branch
```

## Report

Write `.superpowers/sdd/issue-102-task-02-fix-report.md` with identity, RED failure, GREEN/build results, exact edits, self-review, unresolved findings, and unverified later boundaries. Report to `/root`; do not commit.
