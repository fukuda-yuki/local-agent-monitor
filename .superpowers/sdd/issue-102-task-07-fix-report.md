# Issue #102 Task 7 Independent Review Fix Report

## Identity

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-doctor-core`
- HEAD under review: `52794b3b780235081a7465e414ac1315a98f31bf`
- State: reviewable uncommitted diff; no commit, push, PR, or main integration
- Runtime preference: GPT-5.6 Luna xhigh was requested but is not selectable or verifiable in this surface

## Verdict Target

All three Important findings from `.superpowers/sdd/issue-102-task-07-review-report.md` are addressed without a specification, dependency, public surface, proxy, UI, or source-specific adapter change.

## Finding Mapping

### Important 1 — Observation-time source/adapter trust

The concrete SQLite store now compares each candidate's source surface and adapter with the loaded active verification before candidate expiry/cardinality checks and before insert. A mismatch rolls back and returns canonical `expected_source_mismatch` with the active verification; it does not return or echo candidate metadata. Complete-time source/adapter checks remain unchanged as defense in depth.

TDD evidence:

- RED command: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~ObserveCandidate_SourceOrAdapterMismatchIsRejectedWithoutPersistence --no-restore -v minimal`
- RED result: exit 1; 2/2 failed. Source and adapter cases expected `ExpectedSourceMismatch` and received `VerificationActive`.
- GREEN result after the minimum store comparison: exit 0; 2/2 passed.
- Assertions cover no persistence, unchanged full Doctor-table snapshot, zero matching evidence row, no marker echo, active revision/evidence unchanged, and the same verification after reopening the store.
- The older completion test no longer pins mismatched candidate acceptance. Complete-time verification-context checks and synthetic-only rejection remain covered.

Files:

- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorVerificationStore.cs`
- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorVerificationStoreTests.cs`

### Important 2 — Real default production lifecycle matrices

Two bounded integration tests now execute actual default adapters through `SqliteDoctorApplicationService` and the concrete store.

CLI matrix (`ProductionCliLifecycleProjectsSharedStoreOutcomesWithoutMutationDrift`):

- uses default `CliApplication.Run`, the supplied SQLite path, and internal concrete-store candidate insertion;
- covers non-ready, partial, stale completion revision, ready completion, already-completed conflict, derived expiry, stale cancel revision, successful cancel, and already-cancelled conflict;
- asserts exit code, deserialized canonical result JSON, stderr according to the pinned `Success` contract, revision/state, accepted refs, and active no-mutation after non-ready/partial/stale outcomes.

HTTP matrix (`DefaultProductionLifecycleProjectsSharedStoreOutcomesWithoutMutationDrift`):

- starts Local Monitor without injecting a Doctor application, so the default monitor database/shared `TimeProvider` composition is used;
- inserts candidates only through the internal concrete store; no route or command was added;
- covers the same lifecycle outcomes and asserts fixed HTTP 200/409/410/422 mapping, canonical result JSON, no-store/content type, revision/state/accepted refs, and active no-mutation;
- derives expiry by advancing the shared deterministic `TimeProvider`; it uses no delay, polling, or retry.

Integration execution evidence:

- CLI targeted command passed 1/1. Its first execution reached production but found a test-only expectation error: non-ready `EvaluationCompleted` is `Success=true`, so exit 3 intentionally has no stderr. The helper was aligned with the existing `WriteResult`/CLI tests, then passed.
- HTTP targeted command passed 1/1 on first execution; no production adapter change was required.

Files:

- `tests/CopilotAgentObservability.Doctor.Tests/DoctorCrossSurfaceContractTests.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/DoctorRoutesTests.cs`

### Important 3 — Machine-local path sanitization

The 19 Issue #102 Markdown files identified by the review had only their machine-specific `Worktree:` identity changed to `linked Issue #102 worktree`. Branch and HEAD identities and all review evidence remain intact. The Task 7 brief, main report, review report, and this fix report contain no machine-local user/worktree path.

The required fixed-string scan across `.superpowers`, `docs/sprints/issue-102-doctor-core`, and `docs/superpowers` returns no matches.

Files sanitized:

- Task 1: brief, review brief, implementer report, review report
- Task 2: brief, fix brief, review brief, implementer report, fix report, review report
- Task 3: brief, report, fix report
- Task 5: report, fix report
- Task 6: report, fix report
- Task 7: brief, report

## Fresh Validation

| Command | Result |
| --- | --- |
| `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj` | Exit 0; 216/216 passed; 0 failed; 0 skipped. |
| `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor` | Exit 0; 152/152 passed; 0 failed; 0 skipped. |
| `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Doctor` | Exit 0; 49/49 passed; 0 failed; 0 skipped. |
| `dotnet build CopilotAgentObservability.slnx` | Exit 0; 0 warnings; 0 errors. Informational preview-SDK messages only. |
| `pwsh scripts\test\install-playwright-chromium.ps1` | Exit 0. |
| `dotnet test CopilotAgentObservability.slnx` | Exit 0; 5,294/5,294 passed: Doctor 216, LocalMonitor 1,442, ConfigCli 3,636; 0 failed; 0 skipped. |
| Required fixed-string machine-path scan | No matches. |

Final `git diff --check` exited 0. Final status remained on `codex/issue-102-doctor-core` with the expected uncommitted Task 7 production/test/evidence diff and no generated runtime artifact.

## Self-Review

- Trust gate is inside the existing immediate transaction and precedes insert; the complete callback remains concrete and atomic.
- Local Monitor still passes the shared host clock. CLI production still uses its default system clock; the CLI expiry test uses direct deterministic persisted state control rather than sleeping.
- Non-ready/partial/stale assertions prove revision 1, active state, and empty accepted evidence on both real surfaces.
- Terminal assertions prove revision 2 and stable accepted evidence or empty cancelled evidence.
- No public candidate route/command, compatibility path, source enum, heuristic, proxy DTO, Razor/JavaScript/Canvas UI, dependency, or canonical spec change was introduced.
- No raw/PII, credential, authorization, candidate detail, SQLite text, exception text, input/database path, or machine-local worktree identity is added to preserved output.
- No commit was created; the same independent reviewer can inspect the complete uncommitted diff.
