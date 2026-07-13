# Task 04: Replace the Apply sentinel with real coordinator invocation (finding 2)

**Objective:** Make `SetupCommandDispatcher.DispatchApply` invoke the injected
apply delegate (production: `SetupApplyCoordinator.Apply`) after the corrected
preflight and adapter pre-resolution, and map every success and failure
outcome to a validated public `SetupCommandResult`. Today the code discards
the delegate (`_ = apply;`) and returns an intentional `internal_error`
sentinel.

**Depends on:** Tasks 02–03 committed and reviewed; audit record Q3 `PINNED`
(failed-apply target projection source).

**Files:**
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs`
  (the sentinel hunk at the end of `DispatchApply`, plus extending the private
  `ApplyFailure` helper with warning/next-action parameters)
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs`

**Interfaces:**
- Consumes: `Func<SetupLock, Guid, SetupPlanSuccess<SetupLedgerChangeSet>> apply`
  (already injected). Success carries `Value` (the post-apply
  `SetupLedgerChangeSet`), empty `Targets`, and closed `Warnings`/`NextActions`
  from revalidation. Failure surfaces as `SetupApplyException` whose
  `Failure : SetupPlanFailure<SetupLedgerChangeSet>` carries `Code`,
  `Warnings`, `NextActions`.
- Produces: the complete public Apply result contract Tasks 05–10 rely on.

**Outcome mapping (verify each row against the spec "Result contract" table
and the audit record before implementing):**

| Coordinator outcome | Public result |
| --- | --- |
| Success, `Value.State == SetupChangeSetState.Applied` | `success=true`, `code=apply_succeeded`, targets = `ProjectApplyTargets(applied, apply_succeeded)` (rollback_available true only for complete changed ownership), warnings/next_actions copied |
| Success, `Value.State == SetupChangeSetState.NoChanges` | `success=true`, `code=no_changes`, targets projected with rollback_available false, warnings/next_actions copied |
| `SetupApplyException` `unsupported_adapter` / `unsupported_target` | `success=false`, exceptional apply pair: persisted adapter retained, `targets=[]`, both arrays empty |
| `SetupApplyException` `stale_plan`, `partial_apply`, `recovery_required`, `target_not_installed`, `unsupported_version`, `managed_policy_conflict`, `environment_override_conflict`, `malformed_settings`, `permission_denied`, `unsafe_path`, `port_owned_by_foreign_process`, `internal_error` | `success=false`, `code` copied, warnings/next_actions copied from `exception.Failure`, targets per audit Q3 (post-failure ledger row projection or pinned alternative) |
| Any other exception | `success=false`, `internal_error`, empty arrays, empty targets |

`change_set_id` stays the requested correlation ID on every row;
`recovered_change_set_id`/`recovery_operation` stay null (recovery already
returned `None` before this point).

## Steps

- [ ] **Step 1: Write the failing success-path test.** A Planned row with one
  changed writable target, a registered adapter, and an injected apply
  delegate returning an Applied change set with warnings
  `[managed_policy_unverified]` and next actions `[restart_vscode]`:

```csharp
[Fact]
public void DispatchApply_PlannedRowInvokesCoordinatorAndMapsAppliedResult()
{
    // fixture: Planned row + matching plan + registered RecordingAdapter;
    // apply delegate returns SetupPlanResult.Success(appliedChangeSet, [],
    //     [SetupCodes.ManagedPolicyUnverified], [SetupCodes.RestartVscode])
    var result = fixture.Dispatcher.Dispatch(CreateApplyOptions(fixture.ChangeSetId));

    Assert.True(result.Success);
    Assert.Equal(SetupCodes.ApplySucceeded, result.Code);
    Assert.Equal(fixture.ChangeSetId.ToString("D"), result.ChangeSetId);
    Assert.Null(result.RecoveredChangeSetId);
    Assert.Equal([SetupCodes.ManagedPolicyUnverified], result.Warnings);
    Assert.Equal([SetupCodes.RestartVscode], result.NextActions);
    Assert.Single(result.Targets);
    Assert.True(result.Targets[0].RollbackAvailable); // complete changed ownership
    Assert.Equal(1, fixture.ApplyCalls);              // delegate invoked exactly once
}
```

- [ ] **Step 2: Run it RED** (current sentinel returns `internal_error`).

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupCommandDispatcherTests.DispatchApply_PlannedRowInvokesCoordinator
```

- [ ] **Step 3: Write the remaining failing tests, one per mapping row:**
  - `no_changes` success (State `NoChanges`, rollback_available false on all
    targets).
  - `stale_plan` failure with warnings/next_actions copied from the typed
    failure and targets per audit Q3.
  - `partial_apply` failure (targets per audit Q3; rollback_available false).
  - `unsupported_target` exceptional pair: persisted `github-copilot`
    adapter, empty targets, both arrays empty (macOS/Linux CLI plan case at
    the dispatcher boundary).
  - `recovery_required` failure.
  - Unexpected exception (`InvalidOperationException` from the delegate) →
    `internal_error`, empty arrays, empty targets, no raw text in the result.
  - Update `DispatchApply_ProductionConstructorBindsCoordinatorButStopsAtB1SentinelWithoutMutation`:
    the sentinel is gone; repurpose it to prove the production constructor
    wires the real coordinator (rename accordingly; its no-mutation guarantee
    moves to the preflight failure tests).

- [ ] **Step 4: Implement.** Replace the sentinel:

```csharp
SetupPlanSuccess<SetupLedgerChangeSet> applied;
try
{
    applied = apply(acquisition.Lock!, changeSetId);
}
catch (SetupApplyException exception)
{
    return Validate(ApplyFailure(
        exception.Code,
        correlationId,
        changeSet.Adapter,
        ApplyFailureTargets(changeSetId, changeSet, exception.Code),
        exception.Failure.Warnings,
        exception.Failure.NextActions));
}

var code = applied.Value.State == SetupChangeSetState.NoChanges
    ? SetupCodes.NoChanges
    : SetupCodes.ApplySucceeded;
return Validate(new SetupCommandResult(
    SetupCommand.Apply,
    true,
    code,
    correlationId,
    null,
    null,
    changeSet.Adapter,
    ProjectApplyTargets(applied.Value, code),
    [],
    Snapshot(applied.Warnings),
    Snapshot(applied.NextActions),
    false));
```

  Extend `ApplyFailure` with optional `warnings`/`nextActions` parameters
  (defaulting to empty) without changing existing call sites' behavior.
  Implement `ApplyFailureTargets` exactly as audit Q3 pinned: empty for the
  exceptional pairs (`unsupported_adapter`, `unsupported_target`) and for
  outcomes with no surviving row evidence; otherwise the pinned ledger-row
  projection (post-failure reload if that is what Q3 pinned). The generic
  outer `catch (SetupStorageException)` / `catch (Exception)` blocks already
  map storage and unexpected failures; keep them last.

- [ ] **Step 5: Run all new tests GREEN, then the focused class and related
  suites.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupCommandDispatcherTests|FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupCompensationTests"
```

- [ ] **Step 6: Full ConfigCli + build (coherent-slice gate).**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
```

Expected: all pass, 0 warnings/0 errors.

- [ ] **Step 7: `git diff --check`, commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs
git commit -m "Issue #66: feat(setup): dispatch apply through the transaction coordinator"
```

  Body: why — 399f441 finding 2; the dispatcher discarded the injected apply
  delegate and returned a deliberate internal_error sentinel, so a valid
  registered Planned apply never reached SetupApplyCoordinator.

## Validation

Steps 5–6 commands; `git diff --check` clean.

## Completion criteria

- The sentinel and `_ = apply;` are gone; the delegate is invoked exactly
  once on the applicable path.
- Every mapping-table row has an executable dispatcher test.
- Exceptional apply pairs keep empty targets and empty arrays; persisted
  adapter is retained.
- Full ConfigCli suite and solution build pass; independent review PASS.

**Report destination:** chat + ledger row per README policy.
