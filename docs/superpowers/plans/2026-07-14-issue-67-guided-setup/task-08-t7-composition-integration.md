# Task 08 (T7): Production composition root and real #66→#67 integration

**Objective:** Compose the three completed target partitions into the single
T3d `GitHubCopilotSetupAdapter`, register exactly that one adapter under ID
`github-copilot`, construct the production T2c2 dispatcher, pass it through
the T2d `CliApplication` injection seam, and prove the real #66 producer /
#67 consumer contract end-to-end with executable integration tests. This is
the gate that makes Issue #67 real — no fake adapter can satisfy it.

> **Dated evidence correction (2026-07-16):** A child-process
> `LOCALAPPDATA` value does not isolate production setup storage:
> `SystemSetupPlatform` resolves its root through `Environment.GetFolderPath`
> and provides no setup environment override. Successful status/storage and
> real #66→#67 behavior are therefore evidenced by executable in-process
> production composition with an injected trusted platform. Direct CLI/wrapper
> subprocess evidence is limited to pre-dispatch invalid-input byte/exit
> parity. This correction supersedes this card's historical successful
> `status_ready` subprocess-isolation assertions.

**Depends on:** tasks 05, 06, 07 committed and individually reviewed, plus
the completed #66 T2 gate.

**Files (T7 ownership):**
- Modify: `src/CopilotAgentObservability.ConfigCli/Program.cs`
- Create: `src/CopilotAgentObservability.ConfigCli/Setup/SetupCompositionRoot.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/ConfigurationSetupIntegrationTests.cs`

**Non-scope:** adapter, shared-contract, platform, dispatcher, CLI-host,
wrapper, or target-specific file edits. A defect found here reopens the
originating owner (T2–T6 task) and its downstream gates; T7 does not patch
around it.

**Interfaces:**
- Consumes: the frozen internal constructor
  `GitHubCopilotSetupAdapter(ISetupPlatform platform,
  IReadOnlyList<IGitHubCopilotTargetPartition> partitions)`, the three
  partitions, `SetupAdapterRegistry`, the production
  `SetupCommandDispatcher` constructor (platform, paths, stores, registry,
  tool version), and the T2d `CliApplication.Run(args, output, error, setupDispatcher)`
  injection seam. `SetupCompositionRoot` constructs one `ISetupPlatform`
  instance and passes that exact same instance to both the aggregate adapter
  and the production dispatcher/runtime wiring; it must not construct a second
  platform.
- Produces:

```csharp
internal static class SetupCompositionRoot
{
    // The one production wiring: one shared SystemSetupPlatform instance ->
    // runtime paths -> stores -> three partitions -> one aggregate adapter ->
    // registry -> production dispatcher -> dispatch delegate for CliApplication.
    public static Func<SetupOptions, SetupCommandResult> CreateSetupDispatch();
}
```

  `Program.cs` changes only its `CliApplication.Run` call to pass
  `SetupCompositionRoot.CreateSetupDispatch()`. No other adapter is
  registered; VS Code/CLI/App-SDK are never registered as separate adapters.

**Deliver (sprint plan T7):** real plan/apply/rollback/status for all
targets through the same ledger/transaction/result types; all-target `all`
behavior; stale and rollback boundaries; recovery correlation; repeated
no-op; exact manifest pairing; the two exceptional apply/code combinations;
proof that App/SDK and Unix CLI never become mutation authorities.
HTTP/proxy/UI remain N/A.

## Steps

- [ ] **Step 1: Write the failing composition tests** in
  `ConfigurationSetupIntegrationTests` — integration style: real
  `SetupCompositionRoot` wiring with an injected trusted `SetupTestPlatform`
  seam for the platform root and OS-specific observation (decide the seam:
  either an internal `CreateSetupDispatch(ISetupPlatform)` overload used by
  tests with the public parameterless one delegating to
  `SystemSetupPlatform`, and record the choice; the overload must pass that
  injected platform instance to both the aggregate adapter and dispatcher).
  The resulting executable in-process composition must prove successful
  status/storage; do not infer that behavior from a child environment root or
  a public-process smoke case.

  Include the deferred wrapper transport test: invoke an invalid setup command
  (for example, `setup apply` without `--change-set`) directly through the CLI
  and through `pwsh scripts/local-monitor/setup.ps1`; assert fixed
  pre-dispatch stdout/stderr bytes and exit-code parity. This remains transport
  evidence only and must not be backported as a successful-status test to Task
  10, where the null dispatcher correctly returns `internal_error`.

- [ ] **Step 2: Write the failing cross-surface matrix** (each case drives
  parser-shaped args through the real dispatcher; assert the serialized
  `setup.v1` JSON):
  1. `plan --adapter github-copilot --target all` on a machine-state fake
     with everything installed → three-partition plan, Stable→Insiders→CLI→
     App/SDK record order per T3d, manifests pair per surface, private plan
     and ledger row persisted.
  2. Plan → Apply round trip for a Windows CLI env target through the real
     #66 transaction (fake user environment): `apply_succeeded`, ledger
     Applied, rollback available.
  3. Apply → Rollback round trip: `rollback_succeeded`, targets
     rollback_available false.
  4. Stale boundary: mutate the target between plan and apply →
     `stale_plan`, no write.
  5. Repeated no-op: plan when already configured → `no_changes` end to
     end.
  6. Exceptional pair 1: persisted plan whose adapter is unregistered (build
     a dispatcher whose registry lacks `github-copilot` against the same
     stores) → `apply`/`unsupported_adapter`, byte-unchanged plan/ledger,
     zero activity.
  7. Exceptional pair 2: macOS-planned CLI target applied →
     `apply`/`unsupported_target`, no shell profile/backup/journal/
     notification/write (negative proofs via the platform fake log).
  8. Recovery correlation: seed an interrupted transaction, run `plan` →
     recovered correlation fields + `rerun_requested_setup_command`.
  9. Status: after the above lifecycle, `setup status` returns the rows with
     immutable projections and correct current-state recomputation; prove
     this status path makes zero aggregate-adapter detection/probe calls.
  10. Mutation-authority negatives: App/SDK and non-Windows CLI paths
      produce zero platform write calls across the whole matrix.

- [ ] **Step 3: Run RED, implement `SetupCompositionRoot` + `Program.cs`
  wiring, run GREEN.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ConfigurationSetupIntegrationTests
```

- [ ] **Step 4: Early real-producer gate.** Run the executable in-process
  production-composition matrix with its injected trusted platform, and invoke
  a pre-dispatch invalid command through the real CLI process (repository
  build) and the existing wrapper:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- setup apply
pwsh scripts\local-monitor\setup.ps1 apply
```

  Confirm the subprocess invocations have fixed invalid-input stdout/stderr
  bytes and exit-code parity, and record in the report that HTTP/proxy/UI
  surfaces remain N/A. The mandatory successful status/storage evidence belongs
  to this task's in-process `ConfigurationSetupIntegrationTests`, not to a
  subprocess root claim.

- [ ] **Step 5: Run the wide filter + full ConfigCli + build.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~Setup|FullyQualifiedName~GitHubCopilot|FullyQualifiedName~VsCode|FullyQualifiedName~CopilotCli|FullyQualifiedName~CopilotSdk"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

- [ ] **Step 6: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Program.cs src/CopilotAgentObservability.ConfigCli/Setup/SetupCompositionRoot.cs tests/CopilotAgentObservability.ConfigCli.Tests/ConfigurationSetupIntegrationTests.cs
git commit -m "Issues #66-#67: test(setup): verify guided setup integration"
```

  Body: why — task-level adapter/partition approvals do not prove the
  cross-surface contract; only the real producer wired through the real
  registry/dispatcher/transaction does.

## Validation

Step 5 commands; the Step 4 real-CLI evidence recorded verbatim (command +
stdout + exit code).

## Completion criteria

- Exactly one production adapter registered (`github-copilot`); `Program.cs`
  diff is the injection call only.
- The 10-case matrix passes against real stores/transactions (fake platform
  observations, real persistence).
- The executable in-process production-composition matrix proves successful
  status/storage and real #66→#67 behavior with an injected trusted platform.
- Direct CLI/wrapper subprocess evidence captures only fixed pre-dispatch
  invalid-input bytes and exit-code parity; HTTP/proxy/UI are recorded N/A.
- Full ConfigCli suite and build pass; independent review PASS. Only after
  this gate may anyone claim #67 target behavior works end to end.

**Report destination:** chat + ledger row per README policy.
