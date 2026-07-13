# Task 07: Historical-manifest cross-surface regression

**Objective:** Prove, with executable dispatcher-level tests, that a strict
but non-current historical ledger manifest (`expected_result` captured from an
older embedded Issue #61 manifest) survives dispatcher projection and
`SetupJson` serialization for every ledger-origin command — Apply, Rollback,
and Status — without weakening Plan's exact-current-manifest check. This is
the T2c2-owned cross-surface regression the sprint plan requires before the
T2 gate.

**Depends on:** Tasks 04–06 committed and reviewed.

**Files:**
- Test only: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs`
  (no production change expected; a production fix discovered here reopens
  the task that owns the broken hunk)

**Interfaces:**
- Consumes: `CreateHistoricalVsCodeManifest()` (existing helper),
  `SetupJson` serializer, the command-aware manifest validation from the
  integrated T1b commit (`bfd0cc7`): Plan requires exact equality with the
  current canonical manifest; Apply, Rollback, and Status use strict
  historical validation.
- Produces: the executable evidence rows the T2 gate and the durable ledger
  cite for "historical-manifest cross-surface executable coverage".

## Steps

- [ ] **Step 1: Inventory existing coverage.** `ProjectApplyTargets_PreservesHistoricalManifestThroughValidatedResultAndJson`
  already covers the Apply projector unit. This task adds full
  dispatcher-boundary coverage (parser-shaped options in, serialized JSON
  out) for each command; do not duplicate the projector-level assertions.

- [ ] **Step 2: Write the failing (or pinning) Apply test.**

```csharp
[Fact]
public void DispatchApply_HistoricalManifestRowSurvivesDispatchAndSetupJsonSerialization()
{
    // fixture: ledger row whose target status_projection.expected_result is
    // CreateHistoricalVsCodeManifest() (strict-valid, not current-equal);
    // drive a full DispatchApply outcome that projects targets
    // (e.g. valid non-Planned row -> invalid_arguments with ledger targets).
    var result = fixture.Dispatcher.Dispatch(CreateApplyOptions(fixture.ChangeSetId));
    var json = SetupJson.Serialize(result);

    // The historical manifest is byte-identical through projection and JSON
    // (property order per SetupJson canonical rules), and validation accepted it.
    Assert.Contains("\"expected_result\"", json, StringComparison.Ordinal);
    // assert the historical marker value that distinguishes it from the
    // current canonical manifest survives verbatim
}
```

- [ ] **Step 3: Write the Rollback and Status variants.** Rollback: a
  rollback outcome projecting the same historical row. Status: a status
  result whose change-set target summaries carry the historical
  `expected_result` through `SetupStatusService` delegation and
  serialization (build the service fixture over a seeded ledger file, the
  same technique the production-constructor status test uses).

- [ ] **Step 4: Write the Plan non-weakening pin.** A Plan whose adapter
  emits the historical (non-current) manifest must still be rejected by the
  exact-current check — assert the existing failure code and zero persisted
  artifacts. If an equivalent test already exists in
  `SetupContractValidationTests` from T1b, add only the dispatcher-boundary
  assertion; cite the existing test name in the commit body instead of
  duplicating semantics.

- [ ] **Step 5: Run the class; fix only test code.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupCommandDispatcherTests
```

Expected: PASS. If a production defect surfaces (historical manifest rejected
or rewritten on any ledger-origin command), STOP, report it, and reopen the
owning task (02–06 or T1b) — do not patch production inside this task.

- [ ] **Step 6: `git diff --check`, commit.**

```powershell
git add tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs
git commit -m "Issue #66: test(setup): pin historical manifests across command surfaces"
```

  Body: what the four pins guarantee and which T1b commit defined the
  command-aware semantics.

## Validation

Step 5 command plus:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

## Completion criteria

- Apply, Rollback, and Status each have a dispatcher-boundary test proving a
  strict historical manifest survives projection and `SetupJson`
  serialization unchanged.
- Plan's exact-current check is pinned as not weakened.
- Full ConfigCli suite and build pass; independent review PASS.

**Report destination:** chat + ledger row per README policy.
