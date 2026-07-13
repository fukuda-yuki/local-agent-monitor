# Task 03: Pre-mutation diagnostic catalog ownership (handoff finding 4)

**Objective:** Resolve the duplicated closed warning/next-action catalog in
`SetupApplyCoordinator` (`RevalidationWarningCodes` / `RevalidationNextActionCodes`
versus `SetupContractValidator.WarningCodes` / `NextActionCodes`) exactly as
the Task 01 audit record pinned (Q2), while preserving the pre-mutation
boundary: an unsupported diagnostic from adapter revalidation must be rejected
before any backup, journal, ledger transition, or target write.

**Depends on:** Task 02 committed and reviewed; Task 01 audit Q2 `PINNED`.

**Files (Option A default — replace with the audit record's decision if it
pinned Option B):**
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Contracts/SetupContractValidator.cs`
  (add one reusable internal validation operation only; no catalog
  declaration change, no behavior change to `Validate(SetupCommandResult)`)
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs`
  (delete the two duplicated sets; call the shared operation in
  `HasValidRevalidationDiagnostics`)
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupApplyTests.cs`
  (deterministic no-mutation tests for invalid diagnostics)

**Interfaces:**
- Consumes: audit record Q2; `ISetupApplyRevalidator.Revalidate` returning
  `SetupPlanResult<SetupRevalidation>` with `Warnings`/`NextActions`.
- Produces (Option A): one internal shared predicate, for example:

```csharp
// SetupContractValidator
internal static bool IsAllowedRevalidationDiagnostics(
    IReadOnlyList<string> warnings,
    IReadOnlyList<string> nextActions) =>
    warnings.All(WarningCodes.Contains) &&
    nextActions.All(RevalidationNextActionCodes.Contains);
```

  where the revalidation next-action set is the public `NextActionCodes`
  minus `rerun_requested_setup_command` (recovery-owned; never a valid
  adapter revalidation output). If the audit pinned a different membership,
  follow the audit. The exact name/signature is Task 03's to finalize; record
  it in the commit body because Task 04's dispatcher mapping relies on the
  coordinator continuing to throw `internal_error` for unsupported
  diagnostics.

**Constraints:**
- The catalog declarations in `SetupCodes.cs` and the validator's
  `WarningCodes`/`NextActionCodes` sets remain byte-identical in membership.
- The coordinator must keep rejecting unsupported diagnostics with
  `SetupApplyException(SetupCodes.InternalError)` before `CaptureAndValidateBases`
  and before any journal/backup/ledger call (current call order:
  `RunRevalidation` precedes `CaptureAndValidateBases` — preserve that).
- No dispatcher edit in this task.

## Steps

- [ ] **Step 1: Write the failing no-mutation tests in `SetupApplyTests`.**
  One test per diagnostic channel; both must prove zero mutation artifacts:

```csharp
[Fact]
public void Apply_RevalidationWithUnsupportedWarningFailsClosedBeforeAnyArtifact()
{
    // revalidator returns Success with warnings: ["not_a_declared_warning"]
    // Act: coordinator.Apply(...)
    // Assert: SetupApplyException with Code == SetupCodes.InternalError
    // Assert: no backup file, no transaction journal, ledger row still
    //         Planned with null OutcomeCode, no target file/env write
    //         (reuse the existing no-artifact assertion helpers in SetupApplyTests).
}

[Fact]
public void Apply_RevalidationWithRecoveryOwnedNextActionFailsClosedBeforeAnyArtifact()
{
    // revalidator returns Success with nextActions: ["rerun_requested_setup_command"]
    // Same assertions: internal_error, zero artifacts, Planned row unchanged.
}
```

  The second test is the boundary the duplication was protecting: the value
  is lexically valid in the public result catalog but must stay invalid as
  adapter revalidation output. If the audit record pinned Option B or a
  different membership, adjust the second test to the pinned contract.

- [ ] **Step 2: Run them.** Expected: the unsupported-warning test may already
  pass (the duplicate set rejects it today) — keep it as the regression that
  survives the refactor. The recovery-owned next-action test must reflect the
  pinned membership; run and record RED/GREEN state before refactoring.

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupApplyTests.Apply_Revalidation"
```

- [ ] **Step 3: Implement the pinned option.** Option A: add the shared
  internal predicate to `SetupContractValidator`, delete
  `RevalidationWarningCodes`/`RevalidationNextActionCodes` from the
  coordinator, and rewrite:

```csharp
private static bool HasValidRevalidationDiagnostics(SetupPlanResult<SetupRevalidation> result) =>
    SetupContractValidator.IsAllowedRevalidationDiagnostics(result.Warnings, result.NextActions);
```

- [ ] **Step 4: Run GREEN.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupContractValidationTests|FullyQualifiedName~SetupContractShapeTests"
```

Expected: PASS with no assertion or fixture meaning changed in the frozen
contract suites.

- [ ] **Step 5: `git diff --check`, commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Contracts/SetupContractValidator.cs src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupApplyTests.cs
git commit -m "Issue #66: refactor(setup): share pre-mutation diagnostic validation"
```

  Body: why — 399f441 finding 4; the coordinator duplicated the closed
  catalog, and removing it without a shared pre-mutation check would let a
  lexically safe but unsupported diagnostic mutate state before the final
  dispatcher validation; cite the audit record decision.

## Validation

Commands above plus `dotnet build CopilotAgentObservability.slnx` (0 warnings,
0 errors) and `git diff --check`.

## Completion criteria

- Exactly one owner for the revalidation diagnostic allowlist (or the audit's
  pinned alternative), with the recovery-owned next-action boundary encoded
  in an executable test.
- Both no-mutation tests prove zero backup/journal/ledger/target activity.
- Frozen contract suites unchanged in meaning; independent review PASS.

**Report destination:** chat + ledger row per README policy.
