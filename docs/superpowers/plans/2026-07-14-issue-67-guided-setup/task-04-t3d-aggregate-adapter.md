# Task 04 (T3d): Aggregate adapter and target-partition seam (freeze)

**Objective:** Create the one shared `GitHubCopilotSetupAdapter` (ID exactly
`github-copilot`) and the stable partition contract T4/T5/T6 implement:
deterministic selected-target/`all` aggregation, canonical #61 manifest
pairing, typed carrier emission through the frozen T2 vocabulary, and the
semantic-positive tests proving the already-declared #67 codes/warnings/
actions are emitted in their intended combinations. The seam freezes at this
task's commit; target tasks may not change it.

**Depends on:** task-02 (T3b) and task-03 (T3c) committed and reviewed.

**Files (T3d ownership; exact per the sprint plan matrix):**
- Create: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/IGitHubCopilotTargetPartition.cs`
- Create: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/GitHubCopilotSetupAdapter.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/GitHubCopilotSetupAdapterTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SourceCapabilityRuntimeTests.cs`
  (manifest-pairing assertions only)
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupContractShapeTests.cs`
  and `tests/CopilotAgentObservability.ConfigCli.Tests/SetupContractValidationTests.cs`
  (NEW semantic-positive #67 methods only; no existing assertion or
  declaration changes)

**Non-scope:** `SetupCodes.cs`, any validator declaration, the generic T2
carrier/dispatcher/coordinator, target subdirectories (`VsCode/`,
`CopilotCli/`, `AppSdk/`), production registration (`Program.cs` /
composition root — T7 owns that join).

**Constraints:** Keep the compatibility evidence skeletal and explicitly
non-gating. It does not prove real VS Code/CLI/App-SDK target behavior,
mutation, production composition, or Issue #67 completion. Keep T7 unchanged
as the first real all-partition producer/consumer integration gate; do not add
a second public DTO producer.

**Interfaces:**
- Consumes: `ISetupAdapter` (frozen T2b contract: `AdapterId`,
  `Plan(SetupPlanRequest)`, `Revalidate(SetupPrivatePlan, SetupLedgerChangeSet)`),
  `GitHubCopilotDetection.Observe`, `GitHubCopilotManagedPolicyResolver.Resolve`,
  `GitHubCopilotEndpointProbe.Classify`,
  `SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget)`.
- Produces (FROZEN partition seam — T4/T5/T6 implement this and nothing
  else; finalize exact shapes here and record them in the commit body):

```csharp
internal interface IGitHubCopilotTargetPartition
{
    // The public --target token(s) this partition owns: "vscode", "cli", "app-sdk".
    string TargetToken { get; }

    // Plan-side: produce this partition's ordered SetupChangeRecord list plus
    // partition-scoped warnings/next-actions, or a typed refusal.
    GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context);

    // Apply-side revalidation for records this partition owns.
    SetupPlanResult<SetupRevalidation> Revalidate(
        GitHubCopilotPartitionContext context,
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet);
}

internal sealed record GitHubCopilotPartitionContext(
    ISetupPlatform Platform,
    SetupPlanRequest Request,                       // endpoint, capture flag, ids
    GitHubCopilotObservations Observations,          // T3a
    GitHubCopilotEndpointClassification Endpoint);   // T3c
// Managed-policy resolution stays partition-internal for vscode (T4 calls
// the T3b resolver with its own desired values); cli/app-sdk never resolve
// managed sources — keep the resolver OUT of the shared context so the seam
// cannot tempt T5/T6 into opening managed sources.

internal sealed record GitHubCopilotPartitionPlan(
    string? FailureCode,                             // null on success
    IReadOnlyList<SetupChangeRecord> Records,        // empty on failure
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NextActions);
```

**Aggregation contract the adapter owns:**
- Validate `request.SelectedTarget` ∈ {`vscode`, `cli`, `app-sdk`, `all`};
  anything else → `SetupPlanResult.Failure<SetupChangePlan>(SetupCodes.UnsupportedTarget)`
  with empty arrays (T2 contract: unsupported target keeps arrays empty).
- Selected single target → exactly that partition plans. `all` → the three
  partitions plan in fixed order vscode, cli, app-sdk; records concatenate in
  that order; warnings/next-actions union preserves first-occurrence order
  and de-duplicates exact codes (e.g. `vscode_non_default_profiles_not_modified`
  exactly once per command).
- Any partition failure fails the whole plan with that partition's code and
  its warnings/next-actions (no partial plan — verify this all-or-nothing
  rule against the spec's `target_not_installed` "does not produce a partial
  plan" sentence and pin the multi-partition composition rule for `all` in
  the commit body; if the spec is ambiguous for `all`, STOP and raise a spec
  question before implementing).
- Probe once per distinct endpoint, not once per partition (call-counter
  test).
- `Revalidate` routes each persisted record to its owning partition by
  target label and composes fresh warnings/next-actions the same way.
- Manifest pairing: the adapter (not partitions) attaches `expected_result`
  from `SourceCapabilityManifestLoader.LoadForTarget` — VS Code records pair
  `github-copilot-vscode`, CLI records pair `github-copilot-cli`, App/SDK
  records carry null.

**Early compatibility evidence (skeletal, non-gating):** Add to the already
owned `GitHubCopilotSetupAdapterTests.cs` scope one test that registers the real
aggregate `GitHubCopilotSetupAdapter`, backed by scripted partitions, in the
real #66 registry/dispatcher Plan path. Serialize the returned real
`SetupCommandResult` with `SetupJson` and assert adapter/carrier/manifest/result
serialization compatibility and that no second public DTO producer exists.
This smoke test is type/carrier evidence only; it is not an integration
fixture, acceptance gate, mutation proof, production composition proof, or
Issue #67 completion evidence. Task 08/T7 remains the first real
all-partition producer/consumer integration gate.

## Steps

- [ ] **Step 1: Write the failing adapter tests** in
  `GitHubCopilotSetupAdapterTests` using three scripted fake partitions:
  selected-target routing (each token reaches only its partition), `all`
  ordering and concatenation, warning de-duplication across partitions,
  unsupported token → `unsupported_target` with empty arrays and zero
  partition calls, partition failure propagation (code + diagnostics, no
  records), single probe per distinct endpoint, revalidation routing by
  label, and manifest pairing per record kind (assert `expected_result`
  equals the canonical manifest via `SourceCapabilityManifestLoader.MatchesCanonical`).
  Add the explicitly non-gating early compatibility test described above.

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~GitHubCopilotSetupAdapterTests
```

- [ ] **Step 3: Write the semantic-positive catalog tests** (new methods
  only) in `SetupContractShapeTests`/`SetupContractValidationTests`: each
  already-declared #67 warning/next-action/code is ACCEPTED by the validator
  in its intended combination (e.g. `managed_policy_unverified` +
  `run_vscode_policy_diagnostics` on a successful vscode plan;
  `environment_override_conflict` + `review_cli_trace_protocol_override` on
  a cli plan failure; `monitor_not_running` + `start_local_monitor` on a
  usable plan). These prove emission legality — no declaration edits.

- [ ] **Step 4: Extend `SourceCapabilityRuntimeTests`** with the pairing
  assertions (adapter-attached manifest equals the loader's canonical
  manifest for both surfaces; App/SDK null).

- [ ] **Step 5: Implement `GitHubCopilotSetupAdapter`** implementing
  `ISetupAdapter` over an injected `IReadOnlyList<IGitHubCopilotTargetPartition>`.
  Build `SetupChangePlan` via `SetupPlanResult.Planned(...)` so snapshotting
  and target derivation reuse the frozen T2 helpers. The adapter never
  catches-and-rewrites partition exceptions into diagnostics — an unexpected
  partition exception propagates so the registry/coordinator map it to
  `internal_error` with empty arrays (T2 contract).

- [ ] **Step 6: Run GREEN + build + `git diff --check`.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~GitHubCopilotSetupAdapterTests|FullyQualifiedName~SourceCapabilityRuntimeTests|FullyQualifiedName~SetupContractShapeTests|FullyQualifiedName~SetupContractValidationTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

- [ ] **Step 7: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot tests/CopilotAgentObservability.ConfigCli.Tests/GitHubCopilotSetupAdapterTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SourceCapabilityRuntimeTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupContractShapeTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupContractValidationTests.cs
git commit -m "Issue #67: feat(setup): freeze Copilot aggregate seam"
```

  Body: why — three target partitions must compose into one adapter with one
  deterministic ordering, one probe, and one manifest-pairing rule before
  any target implementation exists, so T4/T5/T6 cannot diverge; record the
  final frozen seam shapes and the pinned `all`-composition rule.

## Completion criteria

- The adapter is created but NOT registered in production anywhere.
- Routing/aggregation/de-dup/manifest/probe-count matrix fully executable
  with fake partitions.
- Semantic-positive tests added for every declared #67 value without any
  declaration edit (`git diff` on `SetupCodes.cs` is empty).
- Full ConfigCli suite and build pass; independent review PASS; seam frozen.

**Report destination:** chat + ledger row per README policy.

**Worktree/branch:** `C:\Users\mwam0\Documents\Codex\copilot-agent-observability`
on `codex/issues-66-67-guided-setup`.

**Local commit subject:**
`Issue #67: feat(setup): freeze Copilot aggregate seam`
