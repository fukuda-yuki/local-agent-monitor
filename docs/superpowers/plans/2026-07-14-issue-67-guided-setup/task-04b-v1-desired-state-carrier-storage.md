# Task 04b (T0b): Schema-v1 desired-state carrier and storage correction

**Objective:** Replace the private-plan scalar-only desired-state assumption
with the closed v1 legacy-or-tagged union. Preserve the existing committed real
ownership-ledger v1 fixture as unchanged restart evidence, and before changing
the serializer capture a separate production-serializer private-plan v1 fixture
with legacy inline `desired_state` for byte-identity/write-close-reopen proof.

**Depends on:** task-04a committed and independently reviewed. This task
reopens the generic #66 storage/carrier boundary; task-04c and all T4/T5/T6
target work wait for its fresh security review PASS.

**Worktree / branch:** Run only from
`C:\Users\mwam0\Documents\Codex\copilot-agent-observability` on
`codex/issues-66-67-guided-setup`. Before editing and before committing, verify
the root/branch plus `git status --short` and `git diff --name-only`; only the
owned paths below may appear.

**Files (ownership):**

- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/ISetupAdapter.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/SetupAdapterRegistry.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Storage/SetupPlanStore.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Storage/SetupLedgerStore.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupStorageTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupAdapterRegistryTests.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/Fixtures/Setup/v1/private-plan.v1.json`
- Modify, compile-only constructor/signature plumbing only:
  `SetupApplyTests.cs`, `SetupCompensationTests.cs`, `SetupRecoveryTests.cs`,
  `SetupRollbackTests.cs`, `SetupStatusProjectorTests.cs`, and
  `SetupCommandDispatcherTests.cs` under
  `tests/CopilotAgentObservability.ConfigCli.Tests/`.

**Non-scope:** `VsCode/`, `CopilotCli/`, and `AppSdk/` partition behavior;
materialization/apply/recovery logic; public DTOs/catalogs; ledger content;
schema v2; migration; compatibility aliases; and Issue #66 historical cards.

**Carrier contract:** Introduce an internal `SetupPrivateDesiredState` union
used by `SetupChangeRecord` and `SetupPrivatePlanTarget`.

- `SetupInlineDesiredState` holds the existing inline string. The serializer
  writes it as the original JSON string token, and the loader accepts it as the
  canonical v1 arm for historical private-plan bytes and generic non-tagged
  file/TOML/opaque targets. It is neither rewritten nor upgraded, and is not a
  fallback from a malformed tagged arm.
- `SetupJsoncOwnedValuesDesiredState` holds exactly an expected lowercase
  64-hex hash and 1..32 ordered `SetupJsoncOwnedValue` entries. Serialization
  writes exactly `kind`, `expected_state_hash`, and `owned_values`; each entry
  writes exactly `setting_key`, `value_kind`, and `value`. `kind` is exactly
  `jsonc_owned_values_v1`; kinds are exactly boolean JSON values or strings of
  exactly 1..2,048 UTF-16 units. Entries are unique, ordered, and exactly 1:1
  with target members. No other object field, token type, kind, hash casing,
  member relation, or value type is valid.

The serializer chooses one arm from the carrier type; it never probes one shape
and falls back to another. Loader/validation failure maps through the existing
private-plan failure path to `recovery_required`, with no raw JSON/value in an
exception or log. The tagged arm is valid only for `SetupTargetKind.File` and
only when the bound adapter record is `github-copilot` with label
`vscode-stable-default-user-settings` or
`vscode-insiders-default-user-settings`; any other **tagged**
target-kind/adapter/label combination fails `recovery_required`. The inline arm
remains valid for the existing non-tagged v1 record shapes. New VS Code emission
is owned by task-05, not this task.

**Required tests:**

- Keep the existing committed ownership-ledger fixture byte-identical across
  production load/restart. Before changing serialization, capture a new
  committed `Fixtures/Setup/v1/private-plan.v1.json` from the production
  `SetupPlanStore` with a legacy inline-string `desired_state`; byte-compare
  it after production write, close, and reopen through `SetupPlanStore`.
- Round-trip both valid v1 arms through the production serializer. Assert
  tagged property names/order, unique ordered 1:1 member keys, boolean/string
  types, exact 0/1/2048/2049 string boundary, and lowercase expected hash.
- Reject every unknown/missing property, non-object/non-string arm, wrong tag,
  duplicate/reordered/missing/extra owned key, invalid member relation,
  wrong value type, empty/33-entry array, over-bound string, and uppercase or
  non-hex hash as `recovery_required`.
- Inject a previous-state secret marker in a JSONC-like existing value and
  prove it is absent from serialized private plans, ledger, journal, result,
  log, exception mapping, and both committed ownership-ledger/private-plan
  fixtures; only a private backup may
  contain the prior state. Do not serialize the source target to make this
  assertion pass.
- Existing constructor fixtures compile with an explicit union arm and retain
  their assertion meaning. No sleep/retry test is introduced.

## Steps

- [ ] Before changing the serializer, construct the canonical synthetic legacy
  private plan with the current production `SetupPlanStore` serializer and
  commit its exact bytes as
  `tests/CopilotAgentObservability.ConfigCli.Tests/Fixtures/Setup/v1/private-plan.v1.json`.
  Do not regenerate this fixture after the union serializer changes.
- [ ] Add failing storage/registry tests for both union arms, distinct
  ownership-ledger/private-plan fixture identity and restart loading, structural
  rejections, exact 0/1/2048/2049 boundaries, target-kind/arm validation, and
  marker non-leakage.
- [ ] Run RED:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupStorageTests|FullyQualifiedName~SetupAdapterRegistryTests"
```

- [ ] Implement the internal union and deterministic `desired_state` reader/
  writer/validator in the owned generic carrier and storage files. Update only
  necessary fixture constructors in the listed compile-only files; do not alter
  their behavioral assertions.
- [ ] Run GREEN:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupStorageTests|FullyQualifiedName~SetupAdapterRegistryTests|FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupCompensationTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupRollbackTests|FullyQualifiedName~SetupStatusProjectorTests|FullyQualifiedName~SetupCommandDispatcherTests"
dotnet build CopilotAgentObservability.slnx
git diff --check
```

- [ ] Verify scope before staging. `git status --short` and
  `git diff --name-only` must list only this card's enumerated source/test/
  fixture paths; otherwise stop rather than staging another worker's change.

- [ ] Commit:

```powershell
git add -- src/CopilotAgentObservability.ConfigCli/Setup/Adapters/ISetupAdapter.cs src/CopilotAgentObservability.ConfigCli/Setup/Adapters/SetupAdapterRegistry.cs src/CopilotAgentObservability.ConfigCli/Setup/Storage/SetupPlanStore.cs src/CopilotAgentObservability.ConfigCli/Setup/Storage/SetupLedgerStore.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupStorageTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupAdapterRegistryTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupApplyTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupCompensationTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupRecoveryTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupStatusProjectorTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/Fixtures/Setup/v1/private-plan.v1.json
git commit -m "Issues #66-#67: fix(setup): define v1 desired-state union"
```

Body: why — retain actual v1 restart evidence without persisting complete new
JSONC documents or treating a private representation change as a migration.

## Completion criteria

- The existing ownership-ledger fixture is byte-identical/restart-readable; the
  new private-plan fixture is captured before serializer change and passes
  production `SetupPlanStore` byte-identity/write-close-reopen proof; no schema
  version, fixture rewrite, migration, fallback, public DTO, or catalog change
  exists.
- The inline arm remains canonical for generic non-tagged file/TOML/opaque
  records; the tagged arm is closed, 1:1 with members, exact-target-bound, and
  fails closed at 0/1/2048/2049 string boundaries.
- Focused suites, build, `git diff --check`, and a fresh security review PASS
  are recorded before task-04c starts.
- Root/branch and status/diff scope gates pass before staging.

**Report destination:** chat only. Do not update the ledger in this task.
