# Task 04c (T0c): Transient JSONC materialization transaction correction

**Objective:** Make tagged JSONC desired bytes transient per-record
`SetupRevalidation` data under the existing apply lock, with the generic
coordinator owning identity/hash validation and every recovery path using only
expected hashes plus backups.

**Depends on:** task-04b committed, independently reviewed, and structurally
approved for data safety/security. That structural PASS is necessary but not
sufficient for the reopened #66 gate. Fresh end-to-end security, concurrency,
and recovery reviews must all PASS after this task before T4/T5/T6 begins; T7
remains blocked until those downstream tasks then pass.

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

`SetupRecoveryCoordinator.RecoverNextCore` owns the real recovery result
mapping. Immediately after any non-null private-plan load, it calls task-04b's
shared desired-state arm/adapter/kind/label binding validation before dispatch
on journal operation/phase or ledger lifecycle. The matching-terminal pending-
notification path is not a shortcut around this gate: it loads and validates
the non-null plan before `RecoverNotificationOnly`. An inline desired state on
either VS Code JSON record or a tagged desired state with any other label
returns `recovery_required` with zero journal or ledger mutation, zero target
observation, and zero notification attempt.

**Required tests:**

- Valid tagged changed records are applied only after exact ID/order/cardinality
  and hash checks. A no-op tagged record creates no materialization yet still
  fails stale base checks when it drifts.
- Each malformed carrier shape above returns `recovery_required` with zero
  artifacts/writes/notifications and no raw byte value in diagnostics.
- `SetupRecoveryTests` call the public `RecoverNext` coordinator path, not the
  evidence helper alone, for both an inline VS Code JSON arm and a tagged
  other-label arm. Pin prepared apply and rollback, active apply and rollback,
  committed apply and rollback reconciliation, restored apply reconciliation,
  and matching terminal applied/restored/rolled-back rows with a pending
  environment notification. Every case returns `recovery_required` before its
  lifecycle/journal branch, leaves journal and ledger bytes identical, performs
  zero target observation, and does not attempt the pending notification.
- Exercise the production generic plan/storage/apply path with a bounded
  source-like existing JSONC buffer containing a marker in an unrelated member:
  positively assert the seed buffer contains the marker; create the tagged Plan
  carrier; persist, close, and reopen the private plan; materialize under the
  apply lock; and positively assert the owned private backup contains the prior
  marker. Assert the marker is absent from the record/tagged carrier, serialized
  private plan, ledger, journal, result, logs, exception/error text, and both
  committed fixtures. The backup is the only persisted artifact allowed to
  contain the previous bytes.
- Deterministic barriers/fault points cover before intent, after intent, after
  replace, after completion, compensation, and rollback. Close/reopen recovery
  reuses that marker-bearing production-path setup, proves zero revalidator/
  materializer calls, keeps the marker out of journal/ledger/result/log/error/
  fixture evidence, and uses expected hash + the marker-bearing backup to
  restore/classify prior, desired, and third-party state without overwrite.
- Existing legacy-inline and environment recovery tests retain their behavior;
  the existing ownership-ledger fixture and task-04b private-plan fixture
  retain their distinct byte-identity guarantees. No sleep/retry is introduced.

## Steps

- [ ] Write failing apply/recovery/rollback/status tests for the exact transient
  carrier contract, all invalid-carrier no-artifact cases, no-op aggregation,
  coordinator-level desired-state binding before every prepared/active/
  committed/restored/pending-terminal-notification recovery branch,
  the non-vacuous production-path marker proof (positive source and backup,
  negative record/private-plan/ledger/journal/result/log/error/fixtures), and
  every deterministic crash boundary.
- [ ] Run RED:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupApplyTests|FullyQualifiedName~SetupRecoveryTests|FullyQualifiedName~SetupRollbackTests|FullyQualifiedName~SetupStatusProjectorTests"
```

- [ ] Implement only the owned carrier/coordinator/recovery/status changes.
  In `RecoverNextCore`, validate every non-null plan's desired-state binding
  immediately after load and before journal/lifecycle dispatch, including the
  pending terminal-notification shortcut. Preserve journal/ledger hash shapes
  and make recovery consume persisted expected hashes rather than desired bytes
  or adapter work.
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
- The production plan-persist-reopen-apply path proves the marker exists in the
  source and backup but nowhere in record/private-plan/ledger/journal/result/
  log/error/fixture evidence; crash recovery reuses the backup without
  rematerializing.
- `RecoverNext` rejects invalid inline-VS-Code and tagged-other-label bindings
  before every prepared, active, committed/restored, and pending terminal-
  notification branch, with byte-identical journal/ledger state, zero target
  observation, and zero notification attempt.
- Recovery passes its fresh end-to-end security, concurrency, and crash-window
  reviews; only this PASS closes the reopened #66 correction gate.
- Focused suites, build, and `git diff --check` pass before T4/T5/T6 unblocks.
- Root/branch and status/diff scope gates pass before staging.

**Report destination:** chat only. Do not update the ledger in this task.
