# Task 04c (T0c): Transient JSONC materialization transaction correction

**Objective:** Make tagged JSONC desired bytes transient per-record
`SetupRevalidation` data under the existing apply lock, with the generic
coordinator owning identity/hash validation and every recovery path using only
expected hashes plus backups.

**Depends on:** task-04b committed and independently reviewed. A fresh
security, concurrency, and recovery review must all PASS after this task before
T4/T5/T6 begins; T7 remains blocked until those downstream tasks then pass.

**Worktree / branch:** Run only from
`C:\Users\mwam0\Documents\Codex\copilot-agent-observability` on
`codex/issues-66-67-guided-setup`. Before editing and before committing, verify
the root/branch plus `git status --short` and `git diff --name-only`; only the
owned paths below may appear.

**Files (ownership):**

- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/ISetupAdapter.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/ISetupApplyRevalidator.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRecoveryCoordinator.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackPreflightEvaluator.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Status/SetupStatusProjector.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupApplyTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupCompensationTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupRecoveryTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupStatusProjectorTests.cs`

**Non-scope:** partition JSONC parsing/emission (task-05), private-plan union
serialization (task-04b), platform interfaces, journal schema/version,
public DTOs/catalogs, the ledger, schema v2, migrations, and Issue #66
historical cards.

**Transaction contract:** Extend `SetupRevalidation` with an immutable ordered
collection of `SetupMaterializedTarget(record_id, desired_bytes,
expected_state_hash)` records. It is not serializable and has no public DTO
projection. For a tagged JSONC target with one or more non-`no-op` members,
the adapter returns exactly one entry. There is no entry for legacy inline
targets, current-user environment targets, guidance targets, or all-no-op
JSONC targets.

After registry/plan/ledger identity validation and while holding `setup.lock`,
the coordinator validates that materialized IDs are unique and ordered exactly
like the changed tagged targets, their cardinality is exact, and
`SetupHash.File(true, desired_bytes)` equals both the entry and persisted
lowercase expected hash. Only then does the coordinator take ownership of the
bytes for captures, backups, journal desired hashes, atomic replacement, and
post-apply verification. Missing, extra, duplicate, reordered, wrong-record,
or wrong-hash materialization is `recovery_required` before a backup, journal,
ledger transition, notification, or target write. Generic capture still checks
all planned physical targets, including no-op records.

The coordinator drops materialized bytes at the end of the apply attempt.
Neither private plan, ledger, journal, status projection/result, log, exception
mapping, fixture, or repository-safe evidence receives the bytes. Journal and
ledger contain only hashes. `SetupRecoveryCoordinator`, rollback preflight,
and status projection must not call the adapter or JSONC materializer: tagged
file desired-state classification uses the persisted expected hash; prior state
uses the flushed backup; journal hashes remain the exact crash evidence.

**Required tests:**

- Valid tagged changed records are applied only after exact ID/order/cardinality
  and hash checks. A no-op tagged record creates no materialization yet still
  fails stale base checks when it drifts.
- Each malformed carrier shape above returns `recovery_required` with zero
  artifacts/writes/notifications and no raw byte value in diagnostics.
- A marker in an unrelated JSONC member is absent from plan, revalidation
  carrier diagnostics, ledger, journal, logs, result, and fixture; the private
  backup is the only persisted artifact allowed to contain previous bytes.
- Deterministic barriers/fault points cover before intent, after intent, after
  replace, after completion, compensation, and rollback. Close/reopen recovery
  proves zero revalidator/materializer calls and uses expected hash + backup to
  restore/classify prior, desired, and third-party state without overwrite.
- Existing legacy-inline and environment recovery tests retain their behavior;
  the existing ownership-ledger fixture and task-04b private-plan fixture
  retain their distinct byte-identity guarantees. No sleep/retry is introduced.

## Steps

- [ ] Write failing apply/recovery/rollback/status tests for the exact transient
  carrier contract, all invalid-carrier no-artifact cases, no-op aggregation,
  marker non-leakage, and every deterministic crash boundary.
- [ ] Run RED:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupRollbackTests|FullyQualifiedName~SetupStatusProjectorTests"
```

- [ ] Implement only the owned carrier/coordinator/recovery/status changes.
  Preserve journal/ledger hash shapes and make recovery consume persisted
  expected hashes rather than desired bytes or adapter work.
- [ ] Run GREEN:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupCompensationTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupRollbackTests|FullyQualifiedName~SetupStatusProjectorTests|FullyQualifiedName~SetupStorageTests"
dotnet build CopilotAgentObservability.slnx
git diff --check
```

- [ ] Verify scope before staging. `git status --short` and
  `git diff --name-only` must list only this card's enumerated paths; otherwise
  stop rather than staging another worker's change.

- [ ] Commit:

```powershell
git add -- src/CopilotAgentObservability.ConfigCli/Setup/Adapters/ISetupAdapter.cs src/CopilotAgentObservability.ConfigCli/Setup/Transactions/ISetupApplyRevalidator.cs src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRecoveryCoordinator.cs src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackPreflightEvaluator.cs src/CopilotAgentObservability.ConfigCli/Setup/Status/SetupStatusProjector.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupApplyTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupCompensationTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupRecoveryTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupStatusProjectorTests.cs
git commit -m "Issues #66-#67: fix(setup): materialize JSONC only during apply"
```

Body: why — JSONC preservation needs exact desired bytes at write time without
turning private plans, recovery, or repository-safe evidence into raw storage.

## Completion criteria

- Apply owns transient bytes only after exact validation; all durable state is
  hashes/backup evidence only.
- Recovery never materializes and passes its fresh security, concurrency, and
  crash-window recovery reviews.
- Focused suites, build, and `git diff --check` pass before T4/T5/T6 unblocks.
- Root/branch and status/diff scope gates pass before staging.

**Report destination:** chat only. Do not update the ledger in this task.
