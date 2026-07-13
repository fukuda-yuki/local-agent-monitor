# Issues #66/#67 Configuration Setup Implementation Plan

**Goal:** Complete the approved `setup.v1` ownership framework and deliver the
GitHub Copilot VS Code/CLI/App-SDK adapter with one production DTO path, bounded
user-scoped mutation, and no claim that static setup proves telemetry receipt.

**Architecture:** Config CLI is the only result producer. Adapters produce
internal plans, the #66 coordinator owns every mutation/recovery/rollback, the
generic dispatcher constructs plan/apply/rollback `SetupCommandResult` values
and delegates status directly to the frozen `SetupStatusService` result, the
CLI serializes the returned DTO, and PowerShell forwards the JSON unchanged. No
HTTP, proxy, UI, database, AppHost resource, project, or dependency is added.

**Contract:** `docs/specifications/interfaces/configuration-setup.md`, D057,
`docs/specifications/security-data-boundaries.md`, and the paired design.

## Completed baseline and evidence preservation

T0 current-spec alignment supersedes the old prospective Task 1a-13b ordering.
It does not erase completed #66 work or rename historical evidence. Exact
commit ranges, focused counts, reviews, residuals, and the old task labels
(1a-8a, including split recovery/rollback remediations) remain canonical in
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`.

The implementation DAG starts from these completed prerequisites:

- `setup.v1` result types, serializer, bounds, and redaction validation;
- runtime #61 manifest loader (old Task 8a);
- runtime paths, private plans, ownership ledger v1, status snapshot schema,
  JSONC/TOML codecs, path/hash/atomic file primitives, and Windows current-user
  environment primitives;
- journal, apply, reverse compensation, restart recovery, rollback producer,
  notification replay, and their recorded fault/review evidence;
- the initial SetupOptions/parser and adapter-registry work recorded before the
  audited T2 reopen.

The T2 reopen does not reassign the already-declared closed catalog. Commit
`139338a` remains the owner of those code/warning/action declarations and their
closed allowlist checks. T2a1 reopens only SetupOptions target delegation;
T2a2 reopens only adapter-predicate routing in its named contract/status files.
T3 later adds semantic positive validation/emission tests for the already-
declared #67 values without editing `SetupCodes.cs` or validator declarations.

The current untracked `SetupCommandDispatcher.cs` and
`SetupCommandDispatcherTests.cs` draft predates the audited carrier contract.
It must be abandoned before T2a1 starts. No code or test is copied from
that draft; T2c1 starts fresh with a RED test after T2b freezes the carrier.

Do not rewrite those rows as new T1-T9 work. T9 alone appends the final results
to the durable ledger and identifies both the current T-number and any
historical task whose residual it closes.

## Bounded dependency DAG

```text
T0 current-spec alignment
 |
 v
T1 #66 status/terminal closure
 |
 v
T2a1 -> T2a2 -> T2b -> T2c1
                         |
                         v
                 T1bA || T1bB
                         |
                         v
                integrated T1b PASS
                         |
                         v
                    T2c2 -> T2d
#66 public command producer
 |
 v
T3a platform observations
 |          |
 v          v
T3b        T3c
policy     endpoint
 |          |
 +----+-----+
      |
      v
T3d aggregate/partition seam
 |          |          |
 v          v          v
T4         T5         T6
VS Code    CLI       App/SDK
 |          |          |
 +----------+----------+
            |
            v
T7 real #66->#67 integration
 |
 v
T8 package/docs/evidence
 |
 v
T9 reviews/full validation
```

The only permitted order is T1 -> T2a1 -> T2a2 -> T2b -> T2c1 ->
{T1bA || T1bB} -> integrated T1b PASS -> T2c2 -> T2d -> T3a ->
{T3b || T3c} -> T3d -> {T4 || T5 || T6} -> T7 -> T8 -> T9. T1bA/T1bB,
T3b/T3c, and T4/T5/T6 are the only parallel groups. Target work starts only
after T3d freezes the shared platform/GitHubCopilot aggregate seam. Each target task owns only its
partition; T7 owns the composition-root join. No adapter may declare completion
before T7. T8 and T9 are sequential closeout.

Gate releases are explicit:

| Gate | Release condition |
| --- | --- |
| T1 -> T2 | exact T1 status/rollback/recovery tests, including the implemented status-lifecycle validation invariant, and all Setup tests pass; review and commit are complete |
| T2b/T2c1 -> T1b | completed T1, T2b typed diagnostics, and the frozen T2c1 plan slice pass their focused tests and independent reviews |
| T1b -> T2c2 | disjoint T1bA semantic and T1bB fixture halves are integrated as one buildable commit; focused union, full ConfigCli, build, and independent integrated review pass |
| T2 -> T3 | T2c2 completes the four-command dispatcher and historical-manifest cross-surface regression, then T2d process/wrapper gates pass; exact #66 exceptional apply combinations are reviewed and committed |
| T3 -> T4/T5/T6 | T3a platform observations, parallel T3b policy and T3c endpoint services, then T3d aggregate/partition join and positive semantic/catalog/manifest tests pass; the shared seam is frozen |
| T4/T5/T6 -> T7 | all three target partitions pass focused tests and are separately reviewed and committed without editing T3-owned files |
| T7 -> T8 | the real composition root and CLI handoff pass the cross-target producer gate and full ConfigCli tests |
| T8 -> T9 | package, wrapper parity, links, and operator documentation checks pass; review and commit are complete |
| T9 -> close | final reviews, required repository validation, contradiction searches, clean range/worktree inspection, and the sole ledger update pass |

The audited active owner matrix is exact. `...` below is never recursive unless
the row explicitly says a target subdirectory.

| Unit | Exclusive files/hunks |
| --- | --- |
| completed T1 | the already-implemented `ValidateStatusChangeSet` hunk in `Setup/Contracts/SetupContractValidator.cs` and its lifecycle/aggregate assertions in `SetupStatusProjectorTests.cs`; frozen except the exact T2a2 adapter-predicate, T2b compile-only, and T1bA/T1bB manifest hunks named below |
| completed catalog baseline (`139338a`) | existing #66 exceptional combinations and every already-declared #67 code/warning/action plus their closed allowlist/shape assertions in `Setup/Contracts/SetupCodes.cs`, `Setup/Contracts/SetupContractValidator.cs`, `SetupContractShapeTests.cs`, and `SetupContractValidationTests.cs`; declarations remain frozen while T2a2 routes adapter fields and T1bA changes only command-aware manifest semantics/labels |
| T2a1 | `Setup/Cli/SetupOptions.cs`, `SetupOptionsTests.cs` only |
| T2a2 | adapter-predicate routing only in `Setup/Contracts/SetupContractValidator.cs`, `SetupContractValidationTests.cs`, `Setup/Status/SetupStatusListProjector.cs`, `SetupStatusOrderingTests.cs`; optional new adapter-predicate cross-surface assertion methods only in `SetupAdapterRegistryTests.cs`, with no registry production edit |
| T2b | production: `Setup/Adapters/ISetupAdapter.cs`, `Setup/Adapters/SetupAdapterRegistry.cs`, `Setup/Transactions/ISetupApplyRevalidator.cs`, `Setup/Transactions/SetupApplyCoordinator.cs`; diagnostics-carrier semantic hunks only in `SetupAdapterRegistryTests.cs` and `SetupApplyTests.cs`, excluding frozen T2a2 assertion methods; compile-only signature hunks: `SetupCompensationTests.cs`, `SetupRollbackTests.cs`, `SetupRecoveryTests.cs`, `SetupStatusProjectorTests.cs` |
| completed T2c1 | already-committed plan-specific hunks in `Setup/Cli/SetupCommandDispatcher.cs` and `SetupCommandDispatcherTests.cs`; frozen before T1b |
| T1bA | semantic manifest-validation hunks only in `Setup/Contracts/SetupContractValidator.cs`, `SetupContractValidationTests.cs`, `SetupContractShapeTests.cs`, `SetupAdapterRegistryTests.cs` |
| T1bB | manifest-bearing obsolete-label fixture hunks only in `SetupStorageTests.cs`, `SetupStatusProjectorTests.cs`, `SetupApplyTests.cs`, `SetupCompensationTests.cs`, `SetupRecoveryTests.cs`, `SetupRollbackTests.cs`, `Fixtures/Setup/v1/ownership-ledger.v1.json`; excludes every `ExpectedResult`-null synthetic row |
| T2c2 | remaining apply/rollback/status hunks only in `Setup/Cli/SetupCommandDispatcher.cs` and `SetupCommandDispatcherTests.cs`, including the historical-manifest cross-surface regression; excludes frozen T2c1 hunks |
| T2d | `Cli/CliApplication.cs`, `Cli/CliHelpText.cs`, `CliApplicationTests.cs`, new `scripts/local-monitor/setup.ps1`, new `SetupWrapperTests.cs` only |
| T3a | `Setup/Platform/ISetupPlatform.cs`, `Setup/Platform/SystemSetupPlatform.cs`, `SetupTestPlatform.cs`, new `Setup/Adapters/GitHubCopilot/GitHubCopilotDetection.cs`, and `GitHubCopilotDetectionTests.cs` |
| T3b | new `Setup/Adapters/GitHubCopilot/GitHubCopilotManagedPolicyResolver.cs` and new `GitHubCopilotManagedPolicyTests.cs` only |
| T3c | new `Setup/Adapters/GitHubCopilot/GitHubCopilotEndpointProbe.cs` and `GitHubCopilotEndpointProbeTests.cs` only |
| T3d | new `Setup/Adapters/GitHubCopilot/IGitHubCopilotTargetPartition.cs`, new `Setup/Adapters/GitHubCopilot/GitHubCopilotSetupAdapter.cs`, new `GitHubCopilotSetupAdapterTests.cs`, `SourceCapabilityRuntimeTests.cs`, and only new semantic-positive #67 methods in `SetupContractShapeTests.cs`/`SetupContractValidationTests.cs` |
| T4 | target subdirectory `Setup/Adapters/GitHubCopilot/VsCode/` and `VsCodeSetupAdapterTests.cs` |
| T5 | target subdirectory `Setup/Adapters/GitHubCopilot/CopilotCli/` and `CopilotCliSetupAdapterTests.cs` |
| T6 | target subdirectory `Setup/Adapters/GitHubCopilot/AppSdk/`, `CopilotSdkGuidanceAdapterTests.cs`, and `CopilotSdkTelemetryCompileTests.cs` |
| T7 | `Program.cs`, new `Setup/SetupCompositionRoot.cs`, new `ConfigurationSetupIntegrationTests.cs` |
| T8 | `scripts/local-monitor/package-release.ps1`, the package/layout/wrapper-parity methods in `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorScriptTests.cs`, and the documentation files listed in T8 |
| T9 | read-only reviews/validation and `docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md` only |

No two tasks edit these files concurrently. A finding reopens the task that owns
the affected hunk/section and invalidates its downstream releases. Optional
T2a2 methods in `SetupAdapterRegistryTests.cs` are frozen before T2b, whose
diagnostics-carrier assertions cannot edit or duplicate them. A T2b
compile-only hunk may update only construction/signature plumbing required by
the carrier; it may not change a frozen domain assertion, fixture meaning, or
behavior. Any semantic change in one of those four test files reopens that
domain owner rather than expanding T2b. T1bA and T1bB share no files and do not
commit independently; only their integrated buildable result is committed and
reviewed. T1bB changes only obsolete labels on manifest-bearing fixtures and
must not touch `ExpectedResult`-null synthetic rows. T1b adds no migration,
schema change, or compatibility alias. T9 remains the sole editor of
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`.
T1bA's command/manifest methods remain disjoint from the frozen T2a2/T2b test
hunks and the later T3d semantic-positive #67 methods. T1bB's fixture-only
hunks remain disjoint from every frozen T1/T2b behavioral assertion.

Every task uses test-driven changes, an independent read-only review, a focused
RED/GREEN command, `git diff --check`, and a coherent local commit. Workers do
not revert concurrent edits and adjust to the shared contract. Two consecutive
fix cycles in one area trigger a contract/test-design re-audit.

## T1 - Close #66 status and terminal invariants

**Own exactly:** `Setup/Status/SetupStatusProjector.cs`,
`Setup/Status/SetupStatusListProjector.cs`,
`Setup/Transactions/SetupRollbackPreflightEvaluator.cs`, the status-preflight
integration in `Setup/Transactions/SetupRollbackCoordinator.cs`, and
`SetupStatusProjectorTests.cs`, `SetupStatusOrderingTests.cs`,
`SetupRollbackTests.cs`, and `SetupRecoveryTests.cs`; plus only the implemented
`ValidateStatusChangeSet` lifecycle/aggregate hunk in
`Setup/Contracts/SetupContractValidator.cs` and its projector serialization
invariant in `SetupStatusProjectorTests.cs`. This is semantic ownership: after
the completed T1 gate, T2a2 may reopen only adapter-predicate routing in the two
named production files and paired tests without changing status behavior or a
catalog declaration. T2b may touch only the compile-only carrier-signature hunks
explicitly listed in the matrix, without changing a T1 assertion, fixture
meaning, or production behavior.

**Deliver:** lifecycle-relative reference/current state, immutable ledger
snapshot reconstruction without adapter rediscovery, fresh private-plan/target/
backup checks, all-NoOp guard-only equivalence with rollback, partial rollback
false, filter -> priority -> deterministic order -> hard 100 cap -> truncation,
and the remaining terminal environment hash/evidence residuals.

**Verify:** focused `SetupStatus|SetupRollback|SetupRecovery` tests, then all
Setup tests. Do not change #67 target behavior. Commit:
`Issue #66: feat(setup): complete ownership status`.

## T2 - Expose the real #66 command producer

T2 uses the sequential release path T2a1 -> T2a2 -> T2b -> T2c1 ->
{T1bA || T1bB} -> integrated T1b PASS -> T2c2 -> T2d. Only the disjoint T1b
implementation halves may run in parallel, and they produce one buildable
commit and one integrated independent review. T3 starts only after the T2d
gate. Each unit owns only the files listed below and runs its exact focused
command before handing off shared files.

### T2a1 - Preserve and delegate the target token

**Own exactly:** `Setup/Cli/SetupOptions.cs` and `SetupOptionsTests.cs`.

**Deliver:** parse an adapter slug matching exactly
`[a-z0-9]+(?:-[a-z0-9]+)*` and bounded to 1..128 UTF-16 code units without
registry lookup. `--target` consumes exactly one nonempty option value,
preserves that value unchanged, and delegates its validation to the selected
adapter; an empty value returns `invalid_arguments`. Preserve exact recognized-
verb grammar and loopback endpoint normalization. Do not edit the adapter
registry, contract validator, status projection, transactions, dispatcher, or
CLI host. The catalog already declared in `139338a` remains frozen.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupOptionsTests
git diff --check
```

Commit: `Issue #66: fix(setup): defer adapter resolution until dispatch`.

### T2a2 - Route the canonical adapter identifier end to end

**Depends on:** T2a1.

**Own exactly:** adapter-predicate routing only in
`Setup/Contracts/SetupContractValidator.cs` and
`SetupContractValidationTests.cs`, plus
`Setup/Status/SetupStatusListProjector.cs` and
`SetupStatusOrderingTests.cs`. If needed, T2a2 may add only bounded
adapter-predicate cross-surface assertion methods to
`SetupAdapterRegistryTests.cs`; it does not edit `SetupAdapterRegistry.cs`, and
those methods freeze before T2b owns diagnostics-carrier assertions in that
test file.

**Deliver:** one adapter-only predicate matching exactly
`[a-z0-9]+(?:-[a-z0-9]+)*` and bounded to 1..128 UTF-16 code units, used for
public adapter fields and the exact historical status adapter filter, including
digit-leading historical adapter IDs. Do not change the shared
`FixedIdentifier` predicate used by non-adapter fields. T2a1's target token
remains preserved for adapter validation. This reopen changes only predicate
routing: no status lifecycle/order/cap behavior, catalog declaration, DTO
shape, or registry production behavior changes.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupContractValidationTests|FullyQualifiedName~SetupStatusOrderingTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupAdapterRegistryTests # only when the optional assertion methods are added
git diff --check
```

Commit: `Issue #66: fix(setup): route canonical adapter identifiers`.

### T2b - Carry sanitized adapter and revalidation diagnostics

**Depends on:** T2a2.

**Own exactly:** four production files:
`Setup/Adapters/ISetupAdapter.cs`, `Setup/Adapters/SetupAdapterRegistry.cs`,
`Setup/Transactions/ISetupApplyRevalidator.cs`, and
`Setup/Transactions/SetupApplyCoordinator.cs`; two semantic test files:
diagnostics-carrier hunks in `SetupAdapterRegistryTests.cs` and
`SetupApplyTests.cs`, excluding any frozen T2a2 adapter-predicate assertion
methods; compile-only signature hunks in `SetupCompensationTests.cs`,
`SetupRollbackTests.cs`, `SetupRecoveryTests.cs`, and
`SetupStatusProjectorTests.cs`.

**Deliver:** one immutable generic carrier vocabulary whose only public-safe
diagnostic fields are an allowed nullable failure code plus ordered closed
`warnings` and `next_actions`. Adapter plan success carries targets and those
two arrays; adapter plan failure carries the fixed code and arrays without raw
exception text. `ISetupApplyRevalidator` uses the same vocabulary for fresh
success/failure diagnostics, and `SetupApplyCoordinator` returns or throws the
typed safe payload needed by the later dispatcher without rerunning the
adapter. `SetupAdapterRegistry` consumes the changed carrier and structurally
copies its immutable typed diagnostics without interpreting or revalidating the
closed catalog. T2b removes its temporary
`SetupCommandResult`; neither the registry nor coordinator may construct the
public DTO. Unexpected/framework failures keep both arrays empty. Persisted-
adapter removal and `unsupported_target` also keep both arrays empty.

T2b owns only the generic transport and semantic assertions in its two named
test files. The four compile-only test hunks may adjust constructors, return
unwrapping, or test doubles solely so the frozen compensation/rollback/
recovery/status suites compile; their assertions and fixture meaning cannot
change. The target-specific values are already declared by `139338a`; T3/T4/
T5/T6 later validate and emit them through the frozen carrier. Do not edit
status/rollback production, CLI/process files, catalog/validator declarations,
or `SetupCommandDispatcher`.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupAdapterRegistryTests|FullyQualifiedName~SetupApplyTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupCompensationTests|FullyQualifiedName~SetupRollbackTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupStatusProjectorTests"
git diff --check
```

Commit: `Issue #66: feat(setup): carry sanitized adapter diagnostics`.

### T2c1 - Dispatch validated plans (completed)

**Depends on:** T2b.

**Own exactly:** the already-committed plan-specific hunks in
`Setup/Cli/SetupCommandDispatcher.cs` and `SetupCommandDispatcherTests.cs`.
Those hunks are frozen before T1b and excluded from T2c2.

The pre-audit untracked files with those names must already be removed. This
task creates both files fresh from its first failing test and does not copy the
abandoned draft.

**Deliver:** the sole generic Plan result producer, with one non-waiting lock,
one recovery pass, registry/adapter dispatch, final DTO validation, and atomic
private-plan/ledger persistence. It preserves requested versus recovered
correlation, adapter/target order, typed diagnostics, prospective rollback
availability, and `no_changes` persistence.

Its executable cross-surface test gate owns the frozen parser -> non-waiting
lock/recovery -> registry -> adapter -> validated result -> `SetupJson`
serialization path. The production dispatcher remains serializer-free; T2d
owns only the process/wrapper exposure of that serialization. Its exact
regressions assert that a digit-leading unknown adapter yields serialized
`plan`/`unsupported_adapter` only after lock/recovery, and that an arbitrary
nonempty target reaches a known fake adapter unchanged, returns
`unsupported_target`, and is absent as raw text from the JSON.

Recorded commits: `e2cc532` and correction `3ed6cfb`.

### T1b-contract - Close command-aware manifest validation

**Depends on:** completed T1, T2b, and frozen T2c1.

T1b has two disjoint implementation halves that may run in parallel but must be
integrated, built, committed, and independently reviewed as one result. Neither
half creates its own commit.

**T1bA semantic owner exactly:** `Setup/Contracts/SetupContractValidator.cs`,
`SetupContractValidationTests.cs`, `SetupContractShapeTests.cs`, and
`SetupAdapterRegistryTests.cs`.

**T1bB selective fixture owner exactly:** `SetupStorageTests.cs`,
`SetupStatusProjectorTests.cs`, `SetupApplyTests.cs`,
`SetupCompensationTests.cs`, `SetupRecoveryTests.cs`, `SetupRollbackTests.cs`,
and `Fixtures/Setup/v1/ownership-ledger.v1.json`.

**Deliver:** T1bA makes manifest validation command-based: Plan requires exact
equality with the current canonical manifest; Apply, Rollback, and Status use
strict historical validation. The only `ExpectedSurface` labels are
`vscode-stable-default-user-settings`,
`vscode-insiders-default-user-settings`, and
`copilot-cli-user-environment`; obsolete aliases are rejected. T1bB changes
only obsolete labels on manifest-bearing fixtures and never touches an
`ExpectedResult`-null synthetic row. Do not add a compatibility alias,
migration, schema change, or ledger version. T9 remains the sole editor of
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`.

**Verify the integrated result:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupContractValidationTests|FullyQualifiedName~SetupContractShapeTests|FullyQualifiedName~SetupAdapterRegistryTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupStorageTests|FullyQualifiedName~SetupStatusProjectorTests|FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupCompensationTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupRollbackTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

Commit both halves together: `Issues #66-#67: fix(setup): validate ledger-origin manifests`.

T2c2 cannot resume until the integrated T1b commit receives an independent
PASS.

### T2c2 - Complete the generic dispatcher

**Depends on:** integrated T1b independent PASS.

**Own exactly:** remaining apply/rollback/status hunks only in
`Setup/Cli/SetupCommandDispatcher.cs` and `SetupCommandDispatcherTests.cs`,
excluding every frozen T2c1 hunk. T2c2 owns the dispatcher historical-manifest
cross-surface regression.

**Deliver:** complete the generic Apply and Rollback result producer. Apply
acquires one non-waiting lock and runs common recovery once; Rollback delegates
its one recovery pass to `SetupRollbackCoordinator` under the same held lock.
Status delegates directly to the frozen `SetupStatusService`, which constructs
the status result, with no outer lock/recovery or DTO reconstruction. Preserve
apply actual rollback availability, missing-row/private-plan distinctions, the
closed exceptional apply matrix, requested versus recovered correlation, and
typed diagnostics. Validate every final public DTO with the frozen
`SetupContractValidator`; the registry does not duplicate validation. Add the
cross-surface regression proving a strict non-current historical ledger
manifest survives dispatcher projection and `SetupJson` serialization for its
ledger-origin command without weakening Plan's exact-current check. Production
dispatch never serializes JSON or resolves target-specific values.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupCommandDispatcherTests|FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupRollbackTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupStatus"
git diff --check
```

Commit: `Issue #66: feat(setup): complete reversible setup dispatch`.

### T2d - Expose the process and wrapper surfaces

**Depends on:** T2c2.

**Own exactly:** `Cli/CliApplication.cs`, `Cli/CliHelpText.cs`,
`CliApplicationTests.cs`, new repository `scripts/local-monitor/setup.ps1`, and
new `SetupWrapperTests.cs` only.

**Deliver:** recognized setup grammar, exactly one JSON stdout result for each
recognized verb, fixed stderr/exit mapping, bare/unknown setup-verb handling,
preserved legacy top-level behavior, a generic `CliApplication` dispatcher-
injection seam, and byte-for-byte wrapper forwarding. T2d only consumes T2c2;
it does not construct the production dispatcher, register an adapter, edit
`Program.cs`, or recreate lock, recovery, target, or diagnostics behavior. The
repository wrapper is not added to the release layout until T8.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~CliApplicationTests|FullyQualifiedName~SetupWrapperTests"
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
git diff --check
```

Commit: `Issue #66: feat(setup): expose reversible setup commands`.

The T2 gate releases only after T2a1, T2a2, T2b, T2c1, integrated T1b, T2c2,
and T2d commits and reviews exist. Its closed
command matrix is: unknown adapter at plan -> `plan`/`unsupported_adapter`;
valid persisted plan whose adapter is removed ->
`apply`/`unsupported_adapter` with retained adapter ID and zero platform/
artifact/lifecycle/target activity; macOS/Linux CLI persisted plan ->
`apply`/`unsupported_target` with no shell profile, artifact, lifecycle,
notification, or target write.

## T3 - Build the #67 shared aggregate foundation

T3 runs T3a -> {T3b || T3c} -> T3d. Only T3b and T3c may run in parallel;
their production/test files do not overlap. T4/T5/T6 start only after T3d is
reviewed and committed.

### T3a - Freeze platform observations and common detection

**Depends on:** T2d.

**Own exactly:** `Setup/Platform/ISetupPlatform.cs`,
`Setup/Platform/SystemSetupPlatform.cs`, `SetupTestPlatform.cs`, new
`Setup/Adapters/GitHubCopilot/GitHubCopilotDetection.cs`, and
`GitHubCopilotDetectionTests.cs`.

**Deliver:** deterministic OS/runtime-root, filesystem, current-user
environment, registry/preferences/file, process, and HTTP observation
boundaries; Stable/Insiders/CLI version observations; planning-OS capture; and
the official local managed-source locations. Expose bounded observations only.
Do not resolve policy precedence, classify the endpoint, build an adapter plan,
or edit catalog/validator declarations.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupRuntimeTests|FullyQualifiedName~GitHubCopilotDetectionTests"
git diff --check
```

Commit: `Issue #67: feat(setup): observe Copilot setup platforms`.

### T3b - Resolve managed policy

**Depends on:** T3a. May run in parallel with T3c.

**Own exactly:** new
`Setup/Adapters/GitHubCopilot/GitHubCopilotManagedPolicyResolver.cs` and new
`GitHubCopilotManagedPolicyTests.cs`.

**Deliver:** Copilot native > server > file whole-channel selection with no
merge, independent VS Code enterprise-policy evaluation, equal managed/no-write
versus differing conflict classification, and the unverified-server boundary.
Consume only T3a observations and already-declared fixed codes/warnings. Do not
edit platform, aggregate, target partition, catalog, or validator files.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~GitHubCopilotManagedPolicyTests
git diff --check
```

Commit: `Issue #67: feat(setup): resolve Copilot managed policy`.

### T3c - Classify Local Monitor endpoint ownership

**Depends on:** T3a. May run in parallel with T3b.

**Own exactly:** new
`Setup/Adapters/GitHubCopilot/GitHubCopilotEndpointProbe.cs` and
`GitHubCopilotEndpointProbeTests.cs`.

**Deliver:** no-redirect `GET <origin>/health/live`; one 500 ms total budget;
connect/read/total timeout as foreign-owner; maximum 4096 payload bytes plus
one sentinel byte unless trustworthy `Content-Length` proves oversize; accept
only HTTP 200 and exactly `{ "status": "live" }` modulo JSON whitespace/
property order with no duplicate/extra property. Refused/proved no-listener
uses the already-declared `monitor_not_running`; every other transport failure,
timeout, redirect, non-200, oversize, malformed/non-object, or different JSON
uses `port_owned_by_foreign_process`.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~GitHubCopilotEndpointProbeTests
git diff --check
```

Commit: `Issue #67: feat(setup): classify monitor endpoint ownership`.

### T3d - Join the shared aggregate and target-partition seam

**Depends on:** T3b and T3c.

**Own exactly:** new
`Setup/Adapters/GitHubCopilot/IGitHubCopilotTargetPartition.cs`, new
`Setup/Adapters/GitHubCopilot/GitHubCopilotSetupAdapter.cs`, new
`GitHubCopilotSetupAdapterTests.cs`, `SourceCapabilityRuntimeTests.cs`, and only
new semantic-positive #67 methods in `SetupContractShapeTests.cs` and
`SetupContractValidationTests.cs`.

**Deliver:** one shared aggregate adapter with ID exactly `github-copilot`, a
stable partition contract consumed by T4/T5/T6, deterministic selected-target/
`all` aggregation, canonical #61 manifest pairing, and typed carrier emission
using the baseline-declared catalog. The positive tests prove the already-declared
warning/action/code values are accepted and emitted in their intended semantic
combinations. T3d does not edit `SetupCodes.cs`, any validator declaration,
the generic T2 carrier/dispatcher, or target subdirectories. It creates the
aggregate type but does not register it in production; T7 owns that join.

**Verify:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~GitHubCopilotSetupAdapterTests|FullyQualifiedName~SourceCapabilityRuntimeTests|FullyQualifiedName~SetupContractShapeTests|FullyQualifiedName~SetupContractValidationTests"
git diff --check
```

Commit: `Issue #67: feat(setup): freeze Copilot aggregate seam`.

## T4 - Implement VS Code Stable/Insiders Default Profile setup

**Depends on:** T3d.

**Own exactly:** the target partition
`Setup/Adapters/GitHubCopilot/VsCode/` and `VsCodeSetupAdapterTests.cs`. Do not
edit T3-owned shared files or the production composition root; T7 owns the join.

**Deliver:** Stable and Insiders 1.128+; deterministic Stable-then-Insiders
physical targets when both are eligible; per-channel Default Profile paths on
Windows/macOS/Linux; per-channel `GitHub.copilot-chat` requirement; exact
members `github.copilot.chat.otel.enabled`, `.exporterType`, `.otlpEndpoint`,
and optional separate `.captureContent`; JSONC preservation; restart guidance;
and fixed warning `vscode_non_default_profiles_not_modified` exactly once when
any non-default profile exists. Non-default profile files are never opened,
hashed, parsed, planned, backed up, written, or rolled back.

Extension listing is exactly `code --list-extensions --show-versions` or
`code-insiders --list-extensions --show-versions`, with no `--profile` argument;
setup never creates/selects a named profile. Insiders paths use the official
Profiles rule that replaces intermediate folder `Code` with `Code - Insiders`.

Consume the frozen T3a observations, T3b whole-channel/enterprise-policy
evaluation, T3c endpoint classifier for planning and apply revalidation, and
T3d aggregate/partition seam; do not reopen their locations, precedence,
transport logic, or carrier boundary. A differing observed constraint from
either policy system blocks the target plan. Copilot native absence produces
`managed_policy_unverified` because server presence cannot be proved.

**Verify:** `VsCodeSetupAdapterTests` cover three-OS paths, both channels,
profile no-open proof, exact warning de-duplication, managed-source precedence,
content opt-in, forbidden global keys, and apply-time version/extension/policy/
member revalidation. Commit:
`Issue #67: feat(setup): guide VS Code Copilot telemetry`.

## T5 - Implement OS-bounded Copilot CLI setup

**Depends on:** T3d.

**Own exactly:** the target partition
`Setup/Adapters/GitHubCopilot/CopilotCli/` and
`CopilotCliSetupAdapterTests.cs`. Do not edit T3-owned shared files or the
production composition root; T7 owns the join.

**Deliver:** CLI 1.0.4+ and the exact default allowlist:
`COPILOT_OTEL_ENABLED`, `COPILOT_OTEL_EXPORTER_TYPE`,
`OTEL_EXPORTER_OTLP_ENDPOINT`, and `OTEL_EXPORTER_OTLP_PROTOCOL`; optional
content capture adds only
`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`. Never write
`client.kind`, `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`,
`OTEL_EXPORTER_OTLP_HEADERS`, `COPILOT_OTEL_SOURCE_NAME`, or credentials.

Detect-only `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` never joins the write
allowlist. Matching `http/protobuf` is retained with
`cli_trace_protocol_override_not_modified`; any other value returns
`environment_override_conflict` plus `review_cli_trace_protocol_override` and
does not persist a plan.

CLI policy detection is environment-only and every successful plan includes
`managed_policy_unverified`. Windows alone writes the current-user environment.
macOS/Linux run detect/plan but their persisted plan applies as the fixed
`unsupported_target` no-write combination; never edit a shell profile. Preserve
current-process/current-user difference and restart guidance on Windows.
Consume the frozen T3a observations and T3c endpoint classifier; do not reopen
their platform or transport logic.

**Verify:** `CopilotCliSetupAdapterTests` cover exact allowlist/forbidden keys,
Windows state matrix, macOS/Linux plan then refusal, no-artifact proof, warning,
capture option, and endpoint/version/member revalidation. Commit:
`Issue #67: feat(setup): guide Copilot CLI telemetry`.

## T6 - Implement App/SDK no-write guidance

**Depends on:** T3d.

**Own exactly:** the target partition
`Setup/Adapters/GitHubCopilot/AppSdk/`, `CopilotSdkGuidanceAdapterTests.cs`, and
the new `CopilotSdkTelemetryCompileTests.cs`. Do not edit T3-owned shared files
or the production composition root, and do not add/change a PackageReference.

**Deliver:** detected package/version without path, one guidance target, null
manifest, no mutation/rollback, and the exact .NET `TelemetryConfig` sample with
loopback endpoint and `http/protobuf`. Other languages remain caller-managed.

**Verify:** guidance adapter tests and SDK telemetry compile test. Commit:
`Issue #67: feat(setup): add Copilot SDK guidance`.

## T7 - Join the real #66 producer to all #67 adapters

**Depends on:** T1, T2d, T3d, T4, T5, T6.

**Own exactly:** `Program.cs`, new `Setup/SetupCompositionRoot.cs`, and new
`ConfigurationSetupIntegrationTests.cs`. T7 composes the three completed target
partitions into the single T3d `GitHubCopilotSetupAdapter`, registers exactly
that one adapter under ID `github-copilot`, constructs the production T2c2
dispatcher, and passes it through the T2d `CliApplication` injection seam. It
does not separately register VS Code, CLI, or App/SDK adapters and does not edit
adapter, shared-contract, platform, dispatcher, CLI-host, or target-specific
files; findings reopen the originating owner and downstream gates.

**Deliver:** real plan/apply/rollback/status for all targets through the same
ledger/transaction/result types, all-target `all` behavior, stale and rollback
boundaries, recovery correlation, repeated no-op, exact manifest pairing, and
the two exceptional apply/code combinations. Verify that App/SDK and Unix CLI
never become mutation authorities. HTTP/proxy/UI remain N/A.

**Early real-producer gate:** invoke the real CLI, parse `setup.v1`, prove the
runtime #61 manifests and production `SetupCommandResult`/target DTO types, and
record HTTP/proxy/UI N/A. A fake adapter cannot satisfy this gate.

**Verify:** `ConfigurationSetupIntegrationTests`, all test classes matching
`Setup|GitHubCopilot|VsCode|CopilotCli|CopilotSdk`, and the full ConfigCli
project. Commit:
`Issues #66-#67: test(setup): verify guided setup integration`.

## T8 - Package and document only verified behavior

**Depends on:** T7.

**Own exactly:** `scripts/local-monitor/package-release.ps1`; the package/layout/
executable and wrapper-parity cases in
`tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorScriptTests.cs`;
`README.md`; `scripts/local-monitor/README.md`;
`docs/user-guide/local-monitor.md`;
`docs/agent-guides/repository-workflow.md`;
`docs/specifications/interfaces/config-cli.md`;
`docs/sprints/sprint23-configuration-ownership-github-copilot/README.md`;
and `docs/task.md`. Package the T2d-owned `scripts/local-monitor/setup.ps1`
and T7-composed Config CLI into the existing release layout without changing
the wrapper command contract or any setup runtime behavior.
Do not change unrelated startup/user-env scripts or the release workflow.

**Deliver:** self-contained `app/config-cli/`, exact wrapper parity, no installed
.NET runtime requirement, implemented examples that never claim first trace,
and requirement-to-test evidence with exact counts, remaining minors, and
interfaces. Preserve all completed #66 evidence rows for T9.

**Verify:** `LocalMonitorScriptTests`, CLI/wrapper parsed DTO parity, link/path
checks, `git diff --check`. Commit:
`Issues #66-#67: docs(setup): record guided setup evidence`.

## T9 - Independent review, validation, and closeout

**Depends on:** T8.

**Own exactly:** final read-only review and validation plus
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`. T9
does not edit implementation, tests, package files, or other documentation. A
finding reopens its originating T1-T8 owner and every downstream gate; after the
fix is reviewed and committed, execution returns to T9 from that gate.

Run separate final reviews for:

1. #66 transaction/security/concurrency/status and shipped-v1 migration N/A;
2. #67 official-source/profile/OS/managed/endpoint/forbidden-key contracts;
3. real #66 -> #67 DTO/manifest/wrapper integration and HTTP/proxy/UI N/A;
4. repository-safe negative markers and exact full diff.

Resolve findings, then run exactly:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Finally run contradiction searches and `git diff --check`, inspect the complete
commit range/worktree, and make the only T1-T9 durable-ledger update with exact
results and unverified scope. On failure use systematic debugging; do not
substitute a command, guess-fix, retry blindly, push, or open a PR.
