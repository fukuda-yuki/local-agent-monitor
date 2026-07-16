# Task 06: Implement the Status dispatcher (pure delegation)

**Objective:** Route `SetupCommand.Status` through direct delegation to the
frozen `SetupStatusService`, which owns its lock, its mandatory recovery pass,
and the complete status result construction. The dispatcher adds no outer
lock, no recovery, and no DTO reconstruction. This removes the last
`Unimplemented` guard.

**Depends on:** Task 05 committed and reviewed; audit record Q5 `PINNED`
(whether the dispatcher re-validates the returned DTO).

**Files:**
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs`

**Interfaces:**
- Consumes: `SetupStatusService.Status(string? adapterFilter) : SetupCommandResult`
  (acquires its own lock via `SetupLock.TryAcquire`, runs `RecoverNext`,
  applies the failed-recovery overlay, filters/orders/caps via
  `SetupStatusListProjector.Project`).
- Produces: seams for Task 09 —
  - production constructor builds a `Func<string?, SetupCommandResult>` from
    `SetupStatusService` (static `CreateStatus(...)` mirroring
    `CreateRecovery`/`CreateApply`, constructed with the same five stores);
  - internal test constructor gains the same delegate parameter.

**Constraints:**
- No outer `SetupLock.TryAcquire` and no `recover(...)` call in
  `DispatchStatus` — the service owns both (double acquisition would
  self-deadlock or produce a spurious `setup_busy`).
- The dispatcher passes `options.Adapter` through unchanged (the parser
  already enforced the slug shape; status filters historically and never
  resolves the registry).
- Follow audit Q5 on validation: if the service result is already validated
  by the frozen status pipeline, delegate purely; do not add a second
  validation that could diverge. If Q5 pinned re-validation, call
  `SetupContractValidator.Validate` and nothing else.

## Steps

- [ ] **Step 1: Write the failing delegation test.**

```csharp
[Fact]
public void DispatchStatus_DelegatesDirectlyToStatusServiceWithoutOuterLockOrRecovery()
{
    SetupCommandResult expected = /* a validator-valid status_ready result built
        with the existing status fixtures */;
    string? observedFilter = "missing";
    var statusCalls = new CallCounter();
    // fixture: status delegate = filter => { statusCalls.Value++; observedFilter = filter; return expected; }
    var result = fixture.Dispatcher.Dispatch(
        new SetupOptions(SetupCommand.Status, "github-copilot", null, null, false, null));

    Assert.Same(expected, result);            // no reconstruction/copying
    Assert.Equal("github-copilot", observedFilter);
    Assert.Equal(1, statusCalls.Value);
    Assert.Equal(0, fixture.RecoveryCalls);   // dispatcher recovery not invoked
    Assert.Equal(0, fixture.LockAcquisitions); // dispatcher-level lock not acquired
}
```

  If audit Q5 pinned re-validation, replace `Assert.Same` with equality on
  the validated instance and add a test that an invalid service result is
  rejected as `setup_contract_invalid` failure mapping — follow the audit.

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupCommandDispatcherTests.DispatchStatus
```

- [ ] **Step 3: Add the null-filter passthrough test** (Status with no
  `--adapter` passes `null` through unchanged) **and a production-constructor
  test** proving the production path wires a real `SetupStatusService`
  (mirror `DispatchPlan_ProductionConstructorRunsMandatoryRecoveryBeforeAdapter`:
  a seeded ledger returns `status_ready` with projected change sets through
  the real service).

- [ ] **Step 4: Implement.**

```csharp
// Dispatch switch:
SetupCommand.Status => DispatchStatus(options),

private SetupCommandResult DispatchStatus(SetupOptions options) =>
    status(options.Adapter);   // plus Validate(...) only if audit Q5 pinned it

private static Func<string?, SetupCommandResult> CreateStatus(
    ISetupPlatform platform,
    SetupRuntimePaths paths,
    SetupPlanStore planStore,
    SetupLedgerStore ledgerStore,
    SetupTransactionJournalStore journalStore) =>
    new SetupStatusService(platform, paths, planStore, ledgerStore, journalStore).Status;
```

  Delete the now-unreachable `Unimplemented` helper and its remaining test
  coverage (every command now has a real dispatcher), or keep the `_` switch
  arm throwing `InvalidOperationException` for undefined enum values — pick
  whichever the existing enum-guard tests require and state it in the commit
  body.

- [ ] **Step 5: Run GREEN, then focused + status suites.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupCommandDispatcherTests|FullyQualifiedName~SetupStatus"
git diff --check
```

- [ ] **Step 6: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs
git commit -m "Issue #66: feat(setup): delegate status dispatch to the status service"
```

  Body: why — the status result pipeline (lock, recovery, overlay, ordering,
  cap) is already owned and frozen in SetupStatusService; a second dispatcher
  lock or projection would race or diverge from that contract.

## Validation

Step 5 commands.

## Completion criteria

- Status delegation is pure: zero dispatcher-level lock acquisitions and zero
  dispatcher recovery calls, proven by counters.
- Filter passthrough (`github-copilot` and null) proven.
- Production constructor wires the real service.
- No `Unimplemented` path remains reachable for the four public verbs.
- Focused suites pass; `git diff --check` clean; independent review PASS.

**Report destination:** chat + ledger row per README policy.
