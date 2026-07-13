# Task 02: Fix Apply preflight ordering (handoff findings 1 and 3)

**Objective:** Reorder the Apply preflight in `SetupCommandDispatcher.DispatchApply`
to `Load plan → RequireImmutableIdentity → lifecycle inspection → (only when
Planned) ValidatePlanAndLedger`, remove the redundant standalone
`SetupStorageValidation.ValidatePlan` call, and add the deterministic
regressions the 399f441 review required, plus the missing
`RecoveryDisposition.None` single-lock/single-recovery pin.

**Depends on:** Task 01 audit record, Q1 `PINNED`.

**Files:**
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs`
  (only the `DispatchApply` preflight hunk between the ledger-row lookup and
  the adapter resolution; frozen T2c1 Plan hunks untouched)
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs`

**Interfaces:**
- Consumes: `SetupPlanStore.Load(Guid) : SetupPrivatePlan?` (throws
  `SetupStorageException` on corrupt storage; already validates standalone
  plan shape), `SetupTransactionEvidence.RequireImmutableIdentity(SetupPrivatePlan, SetupLedgerChangeSet)`
  (throws on identity mismatch),
  `SetupStorageValidation.ValidatePlanAndLedger(SetupPrivatePlan, SetupLedgerChangeSet)`,
  `SetupCommandDispatcher.ProjectApplyTargets(SetupLedgerChangeSet, string code)`.
- Produces: the corrected preflight order Tasks 03–04 build on. No public
  signature changes.

**Constraints:** Use the existing test helpers (`DispatcherFixture`,
`CreateApplyDispatcher`, `CreateApplyOptions`, `CreateApplyChangeSet`,
`RecordingAdapter`, `CallCounter`, `NoRecovery`). Reference implementation
below — verify against current signatures before committing; the repo state
wins over this document on mechanical details.

## Steps

- [ ] **Step 1: Write the failing regression for a non-Planned identity
  mismatch.** With the current (wrong) order, a non-Planned row whose private
  plan fails `RequireImmutableIdentity` may pass the standalone validation and
  reach the lifecycle branch, returning `invalid_arguments`; the required
  behavior is `recovery_required` before lifecycle inspection. Test sketch
  (align names/helpers with the existing file):

```csharp
[Fact]
public void DispatchApply_NonPlannedRowWithIdentityMismatchReturnsRecoveryRequiredWithoutProjection()
{
    // Arrange: persisted plan whose ChangeSetId/Adapter/target identity
    // disagrees with the ledger row (reuse the existing row-artifact fixture
    // technique from DispatchApply_RowArtifactFailureReturnsRecoveryRequiredWithPersistedAdapter),
    // and a ledger row in a non-Planned state (e.g. SetupChangeSetState.Applied).
    var adapter = new RecordingAdapter("github-copilot");
    var fixture = DispatcherFixture.Create(adapter /* + identity-mismatched plan + Applied row */);

    var result = fixture.Dispatcher.Dispatch(CreateApplyOptions(fixture.ChangeSetId));

    Assert.False(result.Success);
    Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
    Assert.Equal("github-copilot", result.Adapter);           // persisted adapter preserved
    Assert.Equal(fixture.ChangeSetId.ToString("D"), result.ChangeSetId);
    Assert.Empty(result.Targets);                             // no target projection
    Assert.Equal(0, adapter.PlanCalls);                       // zero adapter activity
    // zero apply activity: injected apply delegate counter remains 0
}
```

- [ ] **Step 2: Run it RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupCommandDispatcherTests.DispatchApply_NonPlannedRowWithIdentityMismatch
```

Expected: FAIL (current code returns `invalid_arguments` with projected
targets, or passes standalone validation first).

- [ ] **Step 3: Confirm the paired valid non-Planned regression exists and
  still pins the required pair.**
  `DispatchApply_ValidNonPlannedRowProjectsHistoricalLedgerBeforeAdapterResolution`
  must assert `invalid_arguments` + targets projected from the ledger row.
  Extend it only if it does not already assert zero adapter/apply activity.

- [ ] **Step 4: Write the failing `RecoveryDisposition.None` pin (finding 3).**
  A normal applicable Apply path must acquire the lock exactly once and call
  the recovery delegate exactly once. Use an injected `recover` delegate that
  increments a `CallCounter` and returns `NoRecovery()`; count lock
  acquisitions through the fixture's platform observation (the same seam the
  busy-lock tests use). Sketch:

```csharp
[Fact]
public void DispatchApply_ApplicableRowRunsExactlyOneLockAcquisitionAndOneRecoveryPass()
{
    var recoveryCalls = new CallCounter();
    // fixture: Planned row + matching plan; recover = lock => { recoveryCalls.Value++; return NoRecovery(); }
    var result = fixture.Dispatcher.Dispatch(CreateApplyOptions(fixture.ChangeSetId));

    Assert.Equal(1, recoveryCalls.Value);
    Assert.Equal(1, fixture.LockAcquisitions); // use the fixture's existing lock observation seam
}
```

  Note: until Task 04 lands, the applicable path ends at the sentinel
  `internal_error`; assert only the counts here, not the final code, so this
  test survives Task 04 unchanged.

- [ ] **Step 5: Run it RED (or confirm it is already GREEN and keep it as a
  pin).** If GREEN against current code, that is acceptable — the review
  required the pin to exist, not to fail first; state this in the commit body.

- [ ] **Step 6: Implement the reorder.** Replace the current preflight hunk
  (plan load through `ValidatePlanAndLedger`) with:

```csharp
SetupPrivatePlan? plan;
try
{
    plan = planStore.Load(changeSetId);
}
catch (Exception exception) when (
    exception is SetupStorageException or FormatException or ArgumentException or InvalidOperationException)
{
    return Validate(ApplyFailure(SetupCodes.RecoveryRequired, correlationId, changeSet.Adapter));
}

if (plan is null)
{
    return Validate(ApplyFailure(SetupCodes.RecoveryRequired, correlationId, changeSet.Adapter));
}

try
{
    SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet);
}
catch (Exception exception) when (
    exception is SetupStorageException or FormatException or ArgumentException or InvalidOperationException)
{
    return Validate(ApplyFailure(SetupCodes.RecoveryRequired, correlationId, changeSet.Adapter));
}

if (changeSet.State != SetupChangeSetState.Planned)
{
    return Validate(ApplyFailure(
        SetupCodes.InvalidArguments,
        correlationId,
        changeSet.Adapter,
        ProjectApplyTargets(changeSet, SetupCodes.InvalidArguments)));
}

try
{
    SetupStorageValidation.ValidatePlanAndLedger(plan, changeSet);
}
catch (Exception exception) when (
    exception is SetupStorageException or FormatException or ArgumentException or InvalidOperationException)
{
    return Validate(ApplyFailure(SetupCodes.RecoveryRequired, correlationId, changeSet.Adapter));
}
```

  The standalone `SetupStorageValidation.ValidatePlan(plan)` call is deleted
  (redundant with `SetupPlanStore.Load`, per audit Q1).

- [ ] **Step 7: Run the new tests GREEN, then the focused class.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupCommandDispatcherTests
```

Expected: all pass (78 existing + new). If an existing test pinned the old
ordering, treat it as owned by this task and correct its expectation with a
one-line justification in the commit body.

- [ ] **Step 8: `git diff --check`, then commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs
git commit -m "Issue #66: fix(setup): order apply preflight identity before lifecycle"
```

  Body: why — 399f441 review finding 1; standalone plan validation ran before
  immutable-identity and lifecycle checks, letting a corrupt pairing surface
  as invalid_arguments with projected targets instead of recovery_required.

## Validation

Focused command above, plus:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupCommandDispatcherTests|FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupRecoveryTests"
git diff --check
```

## Completion criteria

- Non-Planned identity mismatch → `recovery_required`, persisted adapter,
  empty targets, zero adapter resolution and zero apply-delegate activity.
- Valid non-Planned → `invalid_arguments` with ledger-projected targets.
- `RecoveryDisposition.None` pin: exactly one lock acquisition, one recovery
  call.
- Standalone `ValidatePlan` call removed; order matches audit Q1.
- Focused suites pass; `git diff --check` clean; independent review PASS.

**Report destination:** chat + ledger row per README policy.
