# Task 01: T2c2 contract audit (read-and-record only)

**Objective:** Re-audit the T2b/T2c2 ownership, Apply preflight validation
ordering, and pre-mutation diagnostic contract before any further code change,
and record the frozen decisions the later tasks implement. The 399f441 review
found repeated boundary corrections in this area; the handoff forbids starting
with another local patch.

**Owned scope:**
- Create: `docs/superpowers/plans/2026-07-14-issue-66-t2-completion/audit-record.md`
- Read-only: everything else. This task changes no production or test code.

**Non-scope:** Any `.cs` edit. Any spec edit (if the audit finds a spec
contradiction, the audit record states it and the orchestrator decides the
spec update as a separate step before Task 02).

**Interfaces:**
- Consumes: `docs/specifications/interfaces/configuration-setup.md`
  ("Result contract", "Fixed result and error codes", "Transaction and
  concurrency rules"), the M3 handoff findings 1–4,
  `Setup/Cli/SetupCommandDispatcher.cs`, `Setup/Transactions/SetupApplyCoordinator.cs`,
  `Setup/Contracts/SetupContractValidator.cs`, `Setup/Adapters/SetupAdapterRegistry.cs`.
- Produces: `audit-record.md` — the decision document Tasks 02–06 cite as
  their authority. Later tasks implement exactly what this record pins.

## Questions the audit record must answer, each with spec citations

- [ ] **Q1 — Apply preflight order.** Confirm (or refute, with the exact spec
  sentence) the handoff's required order:
  `planStore.Load` → `SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet)`
  → lifecycle inspection (`changeSet.State != Planned` → `invalid_arguments`
  with ledger-projected targets) → only when `Planned`,
  `SetupStorageValidation.ValidatePlanAndLedger(plan, changeSet)`.
  Confirm that the current standalone `SetupStorageValidation.ValidatePlan(plan)`
  call in `DispatchApply` is redundant because `SetupPlanStore.Load` already
  validates the standalone plan shape (cite the `SetupPlanStore.Load`
  implementation and its tests).

- [ ] **Q2 — Pre-mutation diagnostic catalog ownership (handoff finding 4).**
  `SetupApplyCoordinator.RevalidationWarningCodes` / `RevalidationNextActionCodes`
  duplicate the closed catalog in `SetupContractValidator.WarningCodes` /
  `NextActionCodes` (the coordinator's warning set omits nothing but also
  omits `rerun_requested_setup_command` from next actions — verify the exact
  membership difference). Decide the owner contract:
  - Option A (handoff's unapproved hypothesis): expose one reusable internal
    validation operation from `SetupContractValidator` (e.g.
    `internal static bool IsAllowedRevalidationDiagnostics(IReadOnlyList<string> warnings, IReadOnlyList<string> nextActions)`),
    invoke it in `SetupApplyCoordinator.HasValidRevalidationDiagnostics`
    before any mutation, delete the duplicated sets, keep the dispatcher's
    final full-result validation.
  - Option B: keep the duplication and add a contract test that the two sets
    stay equal to the validator catalogs.
  The record must state which option, why, whether the coordinator's
  revalidation-time allowlist is intentionally narrower than the public
  result allowlist (`rerun_requested_setup_command` is recovery-owned and must
  NOT become acceptable adapter revalidation output), and which files the
  chosen option may edit (this becomes Task 03's owned scope).

- [ ] **Q3 — Failed-apply target projection source.** For a failed real apply
  (`stale_plan`, `partial_apply`, `recovery_required`, mutation failures), the
  spec's "normal `apply` result" row projects "the requested row's immutable
  ledger target fields". The coordinator mutates the ledger row before
  throwing (e.g. stale outcome persistence, Partial state). Pin whether the
  dispatcher must reload the ledger row after a `SetupApplyException` to
  project targets from the post-failure row, or project from the pre-loaded
  row; and pin which failure codes project targets versus which return empty
  `targets` (the spec's exceptional apply pairs `unsupported_adapter` /
  `unsupported_target` and lock/storage/recovery failures return empty).

- [ ] **Q4 — Rollback dispatcher recovery ownership.** Confirm from
  `SetupRollbackCoordinator.RollbackCore` that the coordinator runs its own
  mandatory recovery pass (`recoveryCoordinator.RecoverNext`), so
  `DispatchRollback` must acquire the lock but must NOT call the dispatcher's
  injected `recover` delegate (exactly one recovery pass per command).
  Enumerate every `SetupRollbackExecutionResult.Code` value the coordinator
  can return (read the full file and `SetupRollbackTests`) — this list becomes
  Task 05's mapping table.

- [ ] **Q5 — Status delegation and validation.** Confirm `SetupStatusService.Status`
  acquires its own lock, runs its own recovery, and returns an
  already-validated `SetupCommandResult` (check whether
  `SetupStatusListProjector.Project` validates). Pin whether `DispatchStatus`
  re-validates the returned DTO or delegates purely (the sprint plan says
  "no outer lock/recovery or DTO reconstruction").

- [ ] **Q6 — T2c2 task-split confirmation.** Confirm the Task 02–07 split in
  this directory matches the corrected T2c2 boundaries, or record the exact
  corrections (the orchestrator updates the task cards before dispatching).

## Steps

- [ ] **Step 1:** Read the sources listed under Interfaces.
- [ ] **Step 2:** Write `audit-record.md` answering Q1–Q6. Each answer cites
  file paths/line anchors and exact spec sentences. Mark every decision as
  `PINNED` or `SPEC-CONFLICT (needs spec update first)`.
- [ ] **Step 3:** Commit:

```powershell
git add docs/superpowers/plans/2026-07-14-issue-66-t2-completion/audit-record.md
git commit -m "Issue #66: docs(setup): pin T2c2 validation and diagnostic contract"
```

## Validation

No test run required (documentation only). `git diff --check` must be clean.

## Completion criteria

- Q1–Q6 each have a `PINNED` decision or an explicit `SPEC-CONFLICT` marker.
- No production/test file changed.
- An independent read-only review confirms each pinned decision against the
  spec (verdict recorded in the audit record header).

**Report destination:** chat summary + `audit-record.md` header (review
verdict) + ledger row per the index README policy.
