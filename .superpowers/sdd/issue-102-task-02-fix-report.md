# Issue #102 Task 2 Fix Report — Stateless Evaluation Security

## Verdict

PASS — the Task 2 review's one Important and one Minor finding are resolved
within the authorized thin slice. No commit, push, PR, specification update, or
ledger edit was performed.

## Identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability`
- Branch: `codex/issue-102-doctor-core`
- Starting HEAD: `287f0c302ccbd24b9cef91acb98747d8635319f7` plus the uncommitted Task 2 diff
- Identity matched `.superpowers/sdd/issue-102-task-02-fix-brief.md` before edits.
- The preferred GPT-5.6 Luna/xhigh runtime was not selectable or verifiable on
  the available dispatch surface.

## TDD record

### RED

The mutation-only `x-monitor-csrf` header was first removed from the real
`POST /api/doctor/evaluations` request in
`DoctorCrossSurfaceContractTests.MonitorNotRunning_UsesOneCanonicalResultAcrossDirectCliAndHttp`.

Command:

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorCrossSurfaceContractTests
```

Result: exit 1; 0 passed, 1 failed, 0 skipped. The assertion expected HTTP
`OK` and received `Forbidden`, proving that the evaluation-specific
same-origin/CSRF gate caused the contract failure.

### GREEN

The same-origin/CSRF check was removed only from the stateless evaluation
handler. The exact focused command was rerun after the production and cleanup
edits.

Result: exit 0; 1 passed, 0 failed, 0 skipped, 1 total. The runner reported
474 ms. The real headerless HTTP request still proves HTTP 200,
`application/json`, `Cache-Control: no-store`, and strict equality with the
canonical direct `DoctorResult`.

## Exact edits

- `src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs`
  - Removed the evaluation handler's mutation-only same-origin/CSRF rejection.
  - Left JSON validation, bounded input handling, fixed status mapping,
    no-store response behavior, and result serialization unchanged.
- `tests/CopilotAgentObservability.Doctor.Tests/DoctorCrossSurfaceContractTests.cs`
  - Removed `x-monitor-csrf` from the valid stateless evaluation request.
  - Wrapped monitor host build/start and address acquisition in failure-path
    cleanup. A built app is disposed, and temporary-directory deletion is
    attempted in `finally`, including when disposal fails.
  - Added no sleep, polling, retry, fallback, dependency, or alternate route.

## Verification

All commands ran from the repository root.

1. Focused test command above: exit 0; 1 passed, 0 failed, 0 skipped; the
   final fresh verification run reported 459 ms.
2. `dotnet build CopilotAgentObservability.slnx`: exit 0; 9 projects built;
   0 warnings and 0 errors; the final fresh verification run took 2.45 seconds.
3. `git diff --check`: exit 0 with no output.
4. `git status --short --branch --untracked-files=all`: branch remained
   `codex/issue-102-doctor-core`. Comparing with the recorded starting status,
   this fix changed no production or test file outside the two authorized
   files and added only this report.

The emitted `NETSDK1057` preview-support-policy lines were informational SDK
messages, not build warnings or test failures.

## Self-review

- Contract: the test sends a real headerless evaluation request and retains the
  existing 200/content-type/no-store/canonical-result assertions.
- Security: the unchanged global Host middleware still rejects non-loopback
  Host headers before `DoctorRoutes.Map`; loopback binding is unchanged.
  Existing state-changing routes retain their same-origin/CSRF checks. No later
  Doctor verification route was added or weakened.
- Cleanup: if host build fails, the created directory is deleted; if start or
  later construction fails after build, the app is disposed before deletion.
  The nested `finally` ensures deletion is still attempted if disposal throws.
- Data safety: no request body, runtime artifact, exception detail, credential,
  raw telemetry, or temporary runtime path was added to output or reports.
- Scope: inspected both complete scoped files and the unchanged Host middleware
  seam. No specification, ledger, plan, CLI behavior, persistence, source
  adapter, UI, or unrelated file was edited.
- Findings: no blocking issue was found in the scoped fix.

The startup-failure cleanup is verified by direct code-path inspection and the
solution build rather than a separately injected host-start failure. Adding a
new failure-injection seam or a second contract test would expand this Task 2
thin slice and was not required by the fix brief.

## Unresolved findings

None from the Task 2 independent review.

## Explicitly unverified later boundaries

- Tasks 3-8 remain unverified: the remaining state catalog and validation
  matrix; verification lifecycle/service/CAS/concurrency; Doctor persistence
  and migration/restart behavior; lifecycle CLI and HTTP routes; full security
  and status matrix; D051 readiness regression; derived documentation and
  ledger updates.
- Issues #103-#105 source adapters, live evidence, proxy DTOs, UI, and live
  workflow remain unverified.
- Full solution tests and Playwright browser installation were not required by
  this fix brief and were not run. They remain final Issue #102 validation and
  are not substituted by the focused test or solution build.
