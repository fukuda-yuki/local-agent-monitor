# Task 05: Implement the Rollback dispatcher

**Objective:** Route `SetupCommand.Rollback` through a real
`DispatchRollback` that acquires the one non-waiting lock and delegates the
single mandatory recovery pass and the rollback execution to
`SetupRollbackCoordinator`, mapping every `SetupRollbackExecutionResult`
outcome to a validated public `SetupCommandResult`. Today Rollback falls into
the `Unimplemented` guard.

**Depends on:** Task 04 and the new Task 04a committed and independently
reviewed; Task 04a focused validation and independent read-only review are
`PASS`. Audit record Q4 is `PINNED` (complete coordinator code enumeration).

**Files:**
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs`
  (add a rollback delegate seam mirroring the apply seam, a `DispatchRollback`
  method, and generalize the failure helpers to carry the Rollback command)
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs`

**Owned scope:** The dispatcher rollback delegate seam, `DispatchRollback`,
contextual trusted-row projection, and its focused dispatcher tests in the two
files above.

**Non-scope:** Task 04a's rollback producer/carrier and preflight files, public
specification or DTO changes, storage/recovery implementation, catalog
declarations, compatibility layers, or any #67 adapter work.

**Interfaces:**
- Consumes: `SetupRollbackCoordinator.Rollback(SetupLock, Guid) : SetupRollbackExecutionResult`
  with Task 04a's explicit trust invariant. A non-null `ChangeSet` (or the
  chosen explicit trust marker) is trustworthy only when it matches
  `RequestedChangeSetId`; immutable-identity/pre-normal failures have no
  projectable row. `Recovery` is non-null when the coordinator's internal
  mandatory recovery pass consumed the command.
- Produces: seams for Task 09 —
  - production constructor gains a `Func<SetupLock, Guid, SetupRollbackExecutionResult>`
    built from `SetupRollbackCoordinator` (mirror `CreateApply`);
  - internal test constructor gains the same delegate parameter.

**Constraints:**
- `DispatchRollback` must NOT call the dispatcher's injected `recover`
  delegate: `RollbackCore` already runs `RecoverNext` internally (exactly one
  recovery pass per command — pin this with a counter test).
- Shared rollback preflight logic stays inside the coordinator
  (`SetupRollbackPreflightEvaluator`); the dispatcher never re-evaluates
  preflight or reconstructs target DTOs (spec: rollback targets are "the
  requested row's same immutable ledger projection"; `rollback_available` is
  always false on rollback results).
- Never infer trust from the outcome code. The complete outcome mapping table
  comes from audit Q4 and the Task 04a carrier:

| Coordinator outcome | Public result |
| --- | --- |
| lock busy (dispatcher-level) | `setup_busy`, `success=false`, requested ID retained, empty targets |
| `Recovery` non-null, recovered | recovery correlation result for `SetupCommand.Rollback` (reuse `RecoveryResult`), `rerun_requested_setup_command` next action |
| `Recovery` non-null, failed | `interrupted_recovery_failed` / `recovery_required` per `RecoveryResult` rules |
| `rollback_succeeded` | `success=true`, targets projected from `result.ChangeSet`, rollback_available false everywhere |
| `rollback_not_available` | `success=false`, trustworthy requested-row targets in ledger order with rollback_available false; empty when untrusted |
| `rollback_stale` | `success=false`, trustworthy requested-row targets in ledger order with rollback_available false; empty when untrusted |
| `unsafe_path` | `success=false`, trustworthy requested-row targets in ledger order with rollback_available false; empty when untrusted |
| `partial_rollback` | `success=false`, trustworthy requested-row targets in ledger order with rollback_available false; empty when untrusted |
| `recovery_required` / `internal_error` after identity success | `success=false`, trustworthy requested-row targets in ledger order with rollback_available false |
| `invalid_arguments` (missing row or invalid/missing identity) | `success=false`, empty targets |
| `interrupted_apply_recovered` / `interrupted_rollback_recovered` | recovery correlation result with empty targets |
| `interrupted_recovery_failed` | recovery correlation failure result with empty targets |
| `ledger_corrupt` / `ledger_version_unsupported` | `success=false`, empty targets (storage failure) |

The exhaustive coordinator-code union is:
`invalid_arguments`, `rollback_succeeded`, `rollback_not_available`,
`rollback_stale`, `unsafe_path`, `partial_rollback`, `recovery_required`,
`internal_error`, `interrupted_apply_recovered`,
`interrupted_rollback_recovered`, `interrupted_recovery_failed`,
`ledger_corrupt`, and `ledger_version_unsupported`. Dispatcher-level
`setup_busy` is separate. Mandatory recovery, lock/storage failures, missing
row, invalid/missing plan or identity, and malformed typed results always have
empty targets; a trustworthy normal result projects the requested row in
ledger order, including trusted `recovery_required` and `internal_error`.

## Steps

- [ ] **Step 1: Write the failing routing test.** `Dispatch` with a Rollback
  option must reach `DispatchRollback` (no longer `Unimplemented`):

```csharp
[Fact]
public void DispatchRollback_AppliedRowDelegatesToCoordinatorAndMapsSuccess()
{
    // fixture: rollback delegate returns
    //   new SetupRollbackExecutionResult(id, true, SetupCodes.RollbackSucceeded, rolledBackChangeSet, null)
    var result = fixture.Dispatcher.Dispatch(CreateRollbackOptions(fixture.ChangeSetId));

    Assert.True(result.Success);
    Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
    Assert.Equal(SetupCommand.Rollback, result.Command);
    Assert.Equal(fixture.ChangeSetId.ToString("D"), result.ChangeSetId);
    Assert.All(result.Targets, target => Assert.False(target.RollbackAvailable));
    Assert.Equal(1, fixture.RollbackCalls);
    Assert.Equal(0, fixture.RecoveryCalls); // recovery is coordinator-owned for rollback
}
```

  Add `CreateRollbackOptions(Guid)` next to `CreateApplyOptions`.

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupCommandDispatcherTests.DispatchRollback
```

- [ ] **Step 3: Write the remaining failing tests, one per mapping row,**
  including: busy lock stops before the rollback delegate; recovery-consumed
  result maps through `RecoveryResult` with command `Rollback` and correct
  correlation (`change_set_id` = requested, `recovered_change_set_id` =
  recovered); `partial_rollback` projects the Partial row; missing row →
  `invalid_arguments` with empty targets; malformed coordinator result
  (e.g. success flag disagreeing with code) fails closed as `internal_error`.
  Also update `Dispatch_UnimplementedRollbackOrStatusUsesValidatorValidFixedGuardWithoutLocking`
  to cover only Status (Task 06 removes it entirely).

- [ ] **Step 4: Implement.**
  - Add `CreateRollback(...)` static factory mirroring `CreateApply`,
    constructing `SetupRollbackCoordinator` with the same five stores.
  - Extend both constructors and wire `SetupCommand.Rollback => DispatchRollback(options)`.
  - `DispatchRollback` skeleton:

```csharp
private SetupCommandResult DispatchRollback(SetupOptions options)
{
    var changeSetId = options.ChangeSetId!.Value;
    var correlationId = changeSetId.ToString("D");
    try
    {
        using var acquisition = SetupLock.TryAcquire(platform, paths);
        if (!acquisition.Acquired)
        {
            return Validate(CommandFailure(SetupCommand.Rollback, SetupCodes.SetupBusy, correlationId, null));
        }

        var execution = rollback(acquisition.Lock!, changeSetId);
        if (execution.Recovery is { } recovery)
        {
            return Validate(RecoveryResult(recovery, SetupCommand.Rollback, correlationId, null));
        }

        return Validate(MapRollbackExecution(execution, correlationId));
    }
    catch (SetupStorageException exception)
    {
        return Validate(CommandFailure(SetupCommand.Rollback, MapStorageCode(exception.Code), correlationId, null));
    }
    catch (Exception)
    {
        return Validate(CommandFailure(SetupCommand.Rollback, SetupCodes.InternalError, correlationId, null));
    }
}
```

  `MapRollbackExecution` implements the exhaustive outcome table, projecting
  only Task 04a's trustworthy requested-row context via
  `ProjectApplyTargets(execution.ChangeSet, execution.Code)` (or the explicit
  trust marker) in ledger order, and validating that `execution.Success`
  matches the success-code set before trusting it (fail closed to
  `internal_error` with empty targets).
  Generalize `CommandFailure`/`ApplyFailure` so Rollback failures carry
  `SetupCommand.Rollback` — do not change Plan/Apply behavior.

- [ ] **Step 5: Run GREEN, then focused + related suites.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupCommandDispatcherTests|FullyQualifiedName~SetupRollbackTests|FullyQualifiedName~SetupRecoveryTests"
git diff --check
```

- [ ] **Step 6: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs
git commit -m "Issue #66: feat(setup): dispatch rollback through the rollback coordinator"
```

  Body: why — rollback previously fell into the unimplemented guard; the
  public command surface requires the coordinator-owned single recovery pass
  and immutable ledger projection.

## Validation

Step 5 commands.

## Completion criteria

- Every coordinator-producible code from audit Q4 has an executable
  dispatcher mapping test; unknown/malformed results fail closed.
- Exactly one recovery pass (coordinator-owned) proven by counter.
- Rollback targets always report `rollback_available=false`.
- Focused suites pass; `git diff --check` clean; independent review PASS.

**Report destination:** chat + ledger row per README policy.

**Worktree/branch:** repository root
on `codex/issues-66-67-guided-setup`.

**Local commit subject:**
`Issue #66: feat(setup): dispatch rollback through the rollback coordinator`
