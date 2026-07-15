# Task 04b (T0b): Schema-v1 desired-state carrier and storage correction

**Objective:** Replace the private-plan scalar-only desired-state assumption
with the closed v1 legacy-or-tagged union, preserving the committed real v1
fixture byte-for-byte while giving new JSONC targets a secret-free owned-values
carrier.

**Depends on:** task-04a committed and independently reviewed. This task
reopens the generic #66 storage/carrier boundary; task-04c and all T4/T5/T6
target work wait for its fresh security review PASS.

**Files (ownership):**

- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/ISetupAdapter.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/SetupAdapterRegistry.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Storage/SetupPlanStore.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Storage/SetupLedgerStore.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupStorageTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupAdapterRegistryTests.cs`
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
  legacy v1 arm only. It exists solely for the committed real v1 fixture; no
  new target chooses it, and it is neither rewritten nor upgraded.
- `SetupJsoncOwnedValuesDesiredState` holds exactly an expected lowercase
  64-hex hash and 1..32 ordered `SetupJsoncOwnedValue` entries. Serialization
  writes exactly `kind`, `expected_state_hash`, and `owned_values`; each entry
  writes exactly `setting_key`, `value_kind`, and `value`. `kind` is exactly
  `jsonc_owned_values_v1`; kinds are exactly boolean JSON values or strings of
  at most 2,048 UTF-16 units. Entries are unique, ordered, and exactly 1:1
  with target members. No other object field, token type, kind, hash casing,
  member relation, or value type is valid.

The serializer chooses one arm from the carrier type; it never probes one shape
and falls back to another. Loader/validation failure maps through the existing
private-plan failure path to `recovery_required`, with no raw JSON/value in an
exception or log. New VS Code emission is owned by task-05, not this task.

**Required tests:**

- Byte-compare the committed real v1 fixture before/after production load and
  restart read; it remains exactly unchanged and readable.
- Round-trip both valid v1 arms through the production serializer. Assert
  tagged property names/order, unique ordered 1:1 member keys, boolean/string
  types, string bounds, and lowercase expected hash.
- Reject every unknown/missing property, non-object/non-string arm, wrong tag,
  duplicate/reordered/missing/extra owned key, invalid member relation,
  wrong value type, empty/33-entry array, over-bound string, and uppercase or
  non-hex hash as `recovery_required`.
- Inject a previous-state secret marker in a JSONC-like existing value and
  prove it is absent from serialized private plans, ledger, journal, result,
  log, exception mapping, and committed fixture; only a private backup may
  contain the prior state. Do not serialize the source target to make this
  assertion pass.
- Existing constructor fixtures compile with an explicit union arm and retain
  their assertion meaning. No sleep/retry test is introduced.

## Steps

- [ ] Add failing storage/registry tests for both union arms, fixture byte
  identity/restart loading, structural rejections, and marker non-leakage.
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

- [ ] Commit:

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Adapters src/CopilotAgentObservability.ConfigCli/Setup/Storage tests/CopilotAgentObservability.ConfigCli.Tests
git commit -m "Issues #66-#67: fix(setup): define v1 desired-state union"
```

Body: why — retain actual v1 restart evidence without persisting complete new
JSONC documents or treating a private representation change as a migration.

## Completion criteria

- The real v1 fixture is byte-identical/restart-readable; no schema version,
  fixture rewrite, migration, fallback, public DTO, or catalog change exists.
- The tagged arm is closed, bounded, 1:1 with members, and fails closed.
- Focused suites, build, `git diff --check`, and a fresh security review PASS
  are recorded before task-04c starts.

**Report destination:** chat only. Do not update the ledger in this task.
