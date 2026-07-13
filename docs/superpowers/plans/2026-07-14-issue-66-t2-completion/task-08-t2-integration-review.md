# Task 08: Early T2 command-surface integration review (read-only gate)

**Objective:** Run the sprint plan's required early #66 command-surface
integration review before T2d starts. This is a fresh, read-only, independent
review run (per the repository's review-delegation practice), not an
implementation task. A finding reopens the owning task (01–07) and blocks
Task 09.

**Depends on:** Tasks 02–07 committed with individual review PASS.

**Files:** none modified. Output is a review record.

## Review charter (give this verbatim to the fresh reviewer)

Scope: the commit range from `df868e6` (exclusive) to the Task 07 commit
(inclusive), plus the current state of
`src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs`,
`Setup/Transactions/SetupApplyCoordinator.cs`,
`Setup/Contracts/SetupContractValidator.cs`, and
`tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs`.

Verify against `docs/specifications/interfaces/configuration-setup.md` and
the Task 01 audit record:

1. **Preflight ordering:** Load → RequireImmutableIdentity → lifecycle →
   ValidatePlanAndLedger (Planned only); no standalone plan re-validation;
   the non-Planned identity-mismatch and valid-non-Planned pair behave as
   pinned.
2. **Single lock / single recovery per command:** Plan and Apply run exactly
   one dispatcher lock and one recovery pass; Rollback's recovery pass is
   coordinator-owned; Status is fully service-owned. No path acquires twice.
3. **Complete outcome mapping:** every producible coordinator outcome
   (Apply success/failure incl. `partial_apply`, exceptional
   `unsupported_adapter`/`unsupported_target` pairs; every
   `SetupRollbackExecutionResult.Code`; status service outcomes) reaches a
   validator-valid public result; malformed internal results fail closed to
   `internal_error` with empty arrays.
4. **Pre-mutation diagnostic boundary:** unsupported revalidation diagnostics
   are rejected before any backup/journal/ledger/target activity; the
   revalidation allowlist ownership matches the audit Q2 decision;
   `rerun_requested_setup_command` is never accepted as adapter output.
5. **Repository-safe evidence:** no raw target text, path, or exception
   message reaches a public DTO or serialized JSON in any new test or path.
6. **Ownership discipline:** Tasks 02–07 edited only their owned files/hunks;
   frozen T2c1 Plan hunks and frozen contract-suite assertions are unchanged
   in meaning.
7. **Historical-manifest coverage:** the four Task 07 pins exist and assert
   at the dispatcher/serialization boundary.

Method: map each numbered requirement to the exact executable test name and
inspect the diff — do not accept pass counts. Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
```

Verdict format: PASS or CHANGES REQUESTED with, per finding, the owning task,
file:line, the violated contract sentence, and a deterministic reproduction.

## Steps

- [ ] **Step 1:** Dispatch the fresh read-only reviewer with the charter
  above.
- [ ] **Step 2:** If CHANGES REQUESTED: reopen the owning task, fix, re-run
  that task's review, then repeat this integration review. Two consecutive
  cycles in the same area → return to Task 01 for a contract re-audit.
- [ ] **Step 3:** On PASS, record the verdict, reviewer evidence (test names,
  counts, build result), and any accepted minors in the ledger row.

## Completion criteria

- Integration review verdict PASS recorded with requirement-to-test mapping.
- All accepted minors listed explicitly (none silently dropped).

**Report destination:** chat + ledger row per README policy. Task 09 may
start only after this gate.
