# Issue #66 Task 01: T2c2 contract audit record

- Audit status: `DONE_WITH_CONCERNS`
- Audited baseline: `2a3bfcbc54add17e6b47b90ba4c330be094d5705`
- Scope: read-only contract audit; no production, test, specification, task-card, or ledger edit
- Implementer self-review: `PASS` for source traceability and owned-file scope
- Independent read-only review: `CHANGES REQUESTED` at `3d15076b2270e26d1e10b4ff382961b6c494cb78`; `PENDING` re-review after the Q4 correction below

## Cross-surface contract table

| Surface | Producer | Exact DTO/carrier | Consumer | Executable test/gate | Issue ownership and status |
| --- | --- | --- | --- | --- | --- |
| Backend / adapter | Issue #67 registered adapter `Plan` / `Revalidate` | Internal `SetupPlanResult<SetupChangePlan>` / `SetupPlanResult<SetupRevalidation>` carrying `SetupChangeRecord`, closed `warnings`, and closed `next_actions`; never `SetupCommandResult` | Issue #66 `SetupAdapterRegistry`, apply coordinator, ledger/transaction pipeline | T3d fake-partition aggregate tests are skeletal only; T7 `ConfigurationSetupIntegrationTests` is the first real all-partition gate | #67 owns adapter/detection; not yet implemented. The canonical table requires internal plan/revalidation carriers (`docs/specifications/interfaces/configuration-setup.md:23-27`), and the adapter types expose those carriers (`src/CopilotAgentObservability.ConfigCli/Setup/Adapters/ISetupAdapter.cs:6-15`, `:26-48`, `:50-72`). |
| Config CLI / backend public result | Config CLI is the **sole public `SetupCommandResult` surface**. `SetupCommandDispatcher` produces Plan/Apply/Rollback results; `SetupStatusService` is the verb-specific Status producer delegated by the dispatcher. Adapters never produce this DTO. | `SetupCommandResult`, serialized exactly as `setup.v1` JSON by `SetupJson` | `CliApplication`, terminal user, and the thin PowerShell wrapper; wrapper forwards JSON unchanged | #66 Tasks 02-07 dispatcher tests, Tasks 09-10 CLI/wrapper tests, and #67 T7 real-producer integration | #66 owns this surface. Plan is implemented; Apply still ends at a sentinel and Rollback/Status dispatch are not implemented at the audited baseline (`src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs:94-102`, `:273-303`; `docs/sprints/sprint23-configuration-ownership-github-copilot/milestones/M3-shared-setup-command-surface/handoff-prompt.md:190-197`). Producer ownership is explicit in the design (`docs/superpowers/specs/2026-07-12-issues-66-67-configuration-setup-design.md:160-170`) and canonical interface (`docs/specifications/interfaces/configuration-setup.md:147-159`, `:924-927`). |
| PowerShell | Repository/Release ZIP `setup.ps1` does not create a second result | Exact CLI arguments and unchanged `setup.v1` JSON stdout; no independent DTO | Config CLI executable on input; terminal user on output | #66 Task 10 byte-for-byte forwarding tests; #67 T7/T8 integration | #66 T2d owns wrapper transport; not implemented at this baseline. Canonical contract: `docs/specifications/interfaces/configuration-setup.md:27-28`. |
| HTTP | N/A: producer `none` | N/A: no route and no DTO | N/A: consumer `none` | T7 records N/A; absence is a contract gate, not a fabricated HTTP test | N/A for #66/#67; not added (`docs/specifications/interfaces/configuration-setup.md:17-19`, `:29`, `:33-35`; `docs/spec.md:87-88`). |
| Proxy | N/A: producer `none` | N/A: no proxy DTO | N/A: consumer `none` | T7 records N/A; no proxy facsimile is accepted | N/A for #66/#67; not added (`docs/specifications/interfaces/configuration-setup.md:30`, `:33-35`; `docs/superpowers/plans/2026-07-12-issues-66-67-configuration-setup.md:658-666`). |
| Local Monitor UI | N/A: producer `none` | N/A: no view model | N/A: consumer `none` | T7 records N/A; no UI facsimile is accepted | N/A for #66/#67; not added (`docs/specifications/interfaces/configuration-setup.md:31`, `:33-35`; `docs/spec.md:187-192`). |

The canonical surface table itself says CLI is the required `SetupCommandResult`
JSON surface and HTTP/proxy/UI are not added
(`docs/specifications/interfaces/configuration-setup.md:21-35`). The current
source-of-truth summary likewise says no setup HTTP, proxy, Canvas, or Local
Monitor UI DTO is added (`docs/spec.md:60-88`).

## Pre-flight migration conflict

**Decision: `SPEC-CONFLICT (needs spec update first)`.** The requested gate says
to migrate a real older-version fixture through process restart. The canonical
spec says, exactly: "Schema version `1` is the first shipped ownership-ledger
version. There is no fabricated v0 migration fixture" and requires older-version
fixtures only for a later schema version after an older version has actually
shipped (`docs/specifications/interfaces/configuration-setup.md:512-518`). The
approved design independently records that migration is N/A for v1 and that a
future v2 must migrate the actually shipped v1 through restart
(`docs/superpowers/specs/2026-07-12-issues-66-67-configuration-setup-design.md:490-492`).

Therefore this audit does not invent a v0 fixture, a schema, a loader path, or a
migration. The requested migration evidence cannot be pinned until either an
older ledger version has actually shipped or the canonical specification is
changed in a separate, approved spec task. Existing v1 write-close-reopen proof
remains required; it is not a substitute for migration evidence
(`docs/specifications/interfaces/configuration-setup.md:513-521`, `:1313-1316`).

## Earliest executable Issue #66 to Issue #67 contract test

**Decision: `PINNED`.** Two gates must not be conflated:

1. A **skeletal, non-gating compatibility test** can first exist at #67 T3d,
   after the aggregate `GitHubCopilotSetupAdapter` and its scripted partition
   seam exist. It may register that real aggregate adapter backed by scripted
   fake partitions, drive the real #66 `SetupCommandDispatcher` Plan path, and
   serialize the resulting production `SetupCommandResult` with `SetupJson`.
   This proves carrier/type compatibility only. It does not prove a real VS
   Code, CLI, or App/SDK partition, mutation behavior, production composition,
   or Issue #67 completion. T3d already owns
   `GitHubCopilotSetupAdapterTests.cs` and fake-partition aggregation
   (`docs/superpowers/plans/2026-07-14-issue-67-guided-setup/task-04-t3d-aggregate-adapter.md:13-27`,
   `:96-106`, `:156-163`). Plan dependency still requires the complete #66 T2
   gate before any #67 task starts
   (`docs/superpowers/plans/2026-07-14-issue-67-guided-setup/README.md:27-29`).
2. The first **real adapter/producer integration test** remains #67 T7, after
   T4/T5/T6 have implemented and reviewed all three partitions. It must use the
   real #66 dispatcher/result/serializer with the real aggregate and real
   partition implementations. A fake adapter cannot satisfy it
   (`docs/superpowers/plans/2026-07-12-issues-66-67-configuration-setup.md:645-666`;
   `docs/superpowers/plans/2026-07-14-issue-67-guided-setup/task-08-t7-composition-integration.md:1-11`,
   `:64-92`, `:129-146`). The M3 handoff expressly requires the first real #67
   integration fixture to be captured from that real #66 producer path at T7
   (`docs/sprints/sprint23-configuration-ownership-github-copilot/milestones/M3-shared-setup-command-surface/handoff-prompt.md:197-203`).

Smallest plan-card adjustment: add one explicitly non-gating
`real dispatcher + real aggregate + scripted partitions + SetupJson` smoke test
to #67 Task 04's already-owned `GitHubCopilotSetupAdapterTests.cs`. Label it
"skeletal compatibility", not an integration fixture or acceptance gate. Keep
Task 08/T7 unchanged as the first real producer/consumer gate. No plan card is
edited by this audit.

## Q1 — Apply preflight order

**Decision: `PINNED`.** The required order is:

```text
planStore.Load(changeSetId)
-> SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet)
-> if changeSet.State != Planned: invalid_arguments with ledger-projected targets
-> only for Planned: SetupStorageValidation.ValidatePlanAndLedger(plan, changeSet)
```

The canonical requested-ID matrix requires a missing/unreadable/private-plan
identity failure to become `recovery_required`, while only a readable, validly
bound plan with an ineligible lifecycle becomes `invalid_arguments`
(`docs/specifications/interfaces/configuration-setup.md:633-640`). That ordering
requires immutable identity before lifecycle classification. A missing row is
still the earlier `invalid_arguments`/empty-target case
(`docs/specifications/interfaces/configuration-setup.md:638`).

The audited dispatcher currently performs the redundant standalone
`ValidatePlan(plan)` before identity and lifecycle
(`src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs:220-262`).
`SetupPlanStore.Load` already deserializes the plan and verifies the requested
ID (`src/CopilotAgentObservability.ConfigCli/Setup/Storage/SetupPlanStore.cs:46-67`);
its deserializer enforces exact shape/version then calls `ValidatePlan` before
returning (`:144-186`). Tests prove exact reopen/round-trip and rejection of
malformed loaded shapes as fixed `recovery_required`
(`tests/CopilotAgentObservability.ConfigCli.Tests/SetupStorageTests.cs:234-247`,
`:762-779`). Delete only the dispatcher's standalone call. Keep
`ValidatePlanAndLedger` for the Planned pair; it intentionally includes both
standalone validations and cross-artifact matching
(`src/CopilotAgentObservability.ConfigCli/Setup/Storage/SetupLedgerStore.cs:680-723`).

## Q2 — Pre-mutation diagnostic catalog ownership

**Decision: `PINNED` — Option A.** `SetupContractValidator` owns the reusable
closed diagnostic check. Task 03 may add one internal operation that accepts
the revalidation warning catalog and the revalidation next-action catalog,
call it from `SetupApplyCoordinator.HasValidRevalidationDiagnostics` before
base capture, and delete the coordinator's duplicated sets. Final full
`SetupCommandResult` validation remains in the dispatcher.

Exact membership audit:

- Coordinator warnings and validator warnings are identical: six values
  (`content_capture_sensitive`, `managed_policy_unverified`,
  `monitor_not_running`, `shared_user_environment_affects_other_processes`,
  `vscode_non_default_profiles_not_modified`, and
  `cli_trace_protocol_override_not_modified`). Compare
  `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs:25-33`
  with
  `src/CopilotAgentObservability.ConfigCli/Setup/Contracts/SetupContractValidator.cs:60-68`.
- Coordinator next actions contain the validator's first twelve entries and
  intentionally exclude only `rerun_requested_setup_command`. Compare
  `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs:34-48`
  with
  `src/CopilotAgentObservability.ConfigCli/Setup/Contracts/SetupContractValidator.cs:70-85`.

The narrower revalidation allowlist is intentional. The omitted action is
recovery-owned: successful recovery adds it and stops the requested mutation
(`docs/specifications/interfaces/configuration-setup.md:155-159`, `:857-864`).
It must not become acceptable adapter revalidation output. The adapter carrier
is only lexical at the registry boundary
(`src/CopilotAgentObservability.ConfigCli/Setup/Adapters/SetupAdapterRegistry.cs:140-170`,
`:214-263`), so the semantic closed-catalog check must remain before mutation.
Current call order already places `RunRevalidation` before base capture
(`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs:88-100`,
`:936-990`), matching the spec's pre-artifact revalidation requirement
(`docs/specifications/interfaces/configuration-setup.md:711-719`, `:730-757`).

Task 03 exact edit scope is:

- `src/CopilotAgentObservability.ConfigCli/Setup/Contracts/SetupContractValidator.cs`;
- `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs`;
- `tests/CopilotAgentObservability.ConfigCli.Tests/SetupApplyTests.cs`.

No code/warning/action declaration membership changes. Tests must reject an
unknown warning and recovery-owned `rerun_requested_setup_command` before any
backup, journal, ledger transition, or target write. This is a reopened T2b
contract repair, not an expansion of normal T2c2 dispatcher ownership; the
design says T2b owns the carrier/coordinator and must not retain duplicate
catalog validation (`docs/superpowers/specs/2026-07-12-issues-66-67-configuration-setup-design.md:175-184`).

## Q3 — Failed-apply target projection source

**Decision: `PINNED`.** Once the injected Apply coordinator has been invoked,
the dispatcher must not project a failure from its pre-call `changeSet`
snapshot. It must reload the requested ledger row after `SetupApplyException`
and project from that post-failure row. The coordinator can persist
`stale_plan`, `restored`, or `partial` evidence before throwing
(`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupApplyCoordinator.cs:158-187`,
`:191-203`, `:211-237`). The canonical source for every normal Apply result is
the requested row's immutable ledger fields and `status_projection`; every
failed target has `rollback_available=false`
(`docs/specifications/interfaces/configuration-setup.md:161-170`).

Projection is contextual, not a code-only rule:

- After a valid row/private-plan preflight and actual coordinator invocation,
  project a readable post-failure row for these typed normal Apply outcomes:
  `target_not_installed`, `unsupported_version`, `managed_policy_conflict`,
  `environment_override_conflict`, `malformed_settings`, `permission_denied`,
  `unsafe_path`, `stale_plan`, `port_owned_by_foreign_process`,
  `partial_apply`, `recovery_required`, and coordinator-owned `internal_error`.
  This includes pre-artifact revalidation/base failures and mutation/
  compensation failures; reloading is what preserves any newly persisted
  outcome/lifecycle evidence.
- Return empty `targets` for `unsupported_adapter` and `unsupported_target`;
  they are the two exceptional Apply pairs and create no lifecycle transition
  (`docs/specifications/interfaces/configuration-setup.md:137-145`, `:158-164`).
- Return empty `targets` for failures before real Apply begins: lock busy,
  mandatory recovery result/failure, unreadable storage, missing requested
  row, missing/unreadable/identity-mismatched private plan, and unexpected
  exceptions. `ledger_corrupt` and `ledger_version_unsupported` are storage
  failures and are empty. An ineligible but validly bound row remains the
  explicit `invalid_arguments` case with its ledger projection
  (`docs/specifications/interfaces/configuration-setup.md:161-165`, `:633-643`).
- If the post-failure reload cannot produce a readable requested row, return
  empty `targets`; never fall back to the stale pre-call snapshot. The failure
  remains repository-safe and no target source is fabricated.

An Apply-delegate `recovery_required` is a normal requested-command failure
after the mandatory recovery phase has already returned `None`, so it uses the
post-failure rule when the row remains readable. A mandatory-recovery-phase
`recovery_required` short-circuits before Apply and has empty targets. This
distinction follows the normal-outcome definition
(`docs/specifications/interfaces/configuration-setup.md:161-164`) and the
one-recovery/normal-handoff matrix (`:663-668`).

## Q4 — Rollback dispatcher recovery ownership and code catalog

**Decision: `PINNED`.** `DispatchRollback` acquires the generic non-waiting
lock, then calls `SetupRollbackCoordinator.Rollback` exactly once. It must not
call the dispatcher's `recover` delegate. `Rollback` executes under the
caller-held lock and `RollbackCore` immediately runs its own `RecoverNext`
(`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackCoordinator.cs:40-57`).
That is the canonical ownership rule
(`docs/specifications/interfaces/configuration-setup.md:656-668`).

The complete `SetupRollbackExecutionResult.Code` union is exactly:

```text
invalid_arguments
rollback_succeeded
rollback_not_available
rollback_stale
unsafe_path
partial_rollback
recovery_required
internal_error
interrupted_apply_recovered
interrupted_rollback_recovered
interrupted_recovery_failed
ledger_corrupt
ledger_version_unsupported
```

Direct requested-operation paths produce missing-row `invalid_arguments`,
artifact/preflight `recovery_required`, success, partial, or the preflight
catalog (`rollback_not_available`, `rollback_stale`, `unsafe_path`,
`internal_error`)
(`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackCoordinator.cs:59-76`,
`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackCoordinator.cs:79-145`,
`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackCoordinator.cs:148-201`;
`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackPreflightEvaluator.cs:50-67`,
`:128-186`). The internal recovery pass can add the two recovered codes,
`interrupted_recovery_failed`, `recovery_required`, and the two fixed ledger
read codes (`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRecoveryCoordinator.cs:91-101`,
`:1997-2025`, `:2264-2286`). `SetupRollbackTests` exercise the direct success,
stale, unavailable, partial, recovery-required, unsafe-path, internal-error,
and interrupted-rollback cases
(`tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs:571-635`,
`:684-723`, `:841-883`).

Task 05 projection is context-based, not code-based. `Recovery != null` is the
mandatory-recovery short circuit and always maps through `RecoveryResult` with
empty targets. Syntax/lock/storage failures, a missing requested row, a missing
or invalid private plan or immutable row/plan identity, and a malformed typed
result also have empty targets. By contrast, every typed normal requested
rollback outcome reached after the applicable row/private-plan validation must
project the trustworthy requested-row `ChangeSet` in ledger order, with
`rollback_available=false` everywhere. That includes `recovery_required` and
`internal_error` when they occur after that validation and the result carries
the trustworthy row, as well as success, unavailable, stale, unsafe, and
partial outcomes (`docs/specifications/interfaces/configuration-setup.md:161-171`,
`:633-645`). A code alone never decides whether targets are present.

**`SPEC-CONFLICT / OWNER CORRECTION REQUIRED BEFORE TASK 05`:** the current
`SetupRollbackExecutionResult` has only nullable `ChangeSet` and `Recovery`
context (`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackCoordinator.cs:7-12`),
but its producers do not establish `ChangeSet != null` as trustworthy proof.
`Prepare` returns lifecycle/environment-count `rollback_not_available` before
immutable identity validation, and an identity/journal/evidence failure returns
`recovery_required` without evidence
(`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackPreflightEvaluator.cs:50-125`).
The coordinator nevertheless persists/returns the supplied row for the former
and returns it for the latter whenever only `plan is null` is false
(`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackCoordinator.cs:79-98`,
`:148-180`). Those paths prove that a supplied row can currently be invalid.

The rollback-domain owner must therefore make the typed result distinguish a
trustworthy normal-result source before Task 05: either enforce and test the
nullable-carrier invariant that `ChangeSet` is non-null if and only if the
requested row passed applicable row/private-plan validation, or add an explicit
equivalent trust marker. The correction must still carry a valid bound row for
the lifecycle-ineligible `rollback_not_available` case and must carry the row
for post-validation `recovery_required`/`internal_error`, including rollback
journal/ledger preparation and preflight-observation/persistence failures
(`src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackCoordinator.cs:103-121`,
`:165-180`; `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackPreflightEvaluator.cs:128-186`).
It must omit the row for invalid identity/evidence paths. Until that producer
contract is corrected, the dispatcher cannot infer trust from code plus a
non-null row. After correction, a missing required trustworthy `ChangeSet`, an
unknown code, a success/code mismatch, or any malformed trust combination
fails closed to `internal_error`/empty targets. `setup_busy` is dispatcher-level
and is not a coordinator-produced code.

## Q5 — Status delegation and validation

**Decision: `PINNED`.** `SetupStatusService.Status` owns its non-waiting lock
and calls recovery once while holding it
(`src/CopilotAgentObservability.ConfigCli/Setup/Status/SetupStatusService.cs:56-83`).
It then composes recovery evidence and delegates ordering/filtering/projection
to `SetupStatusListProjector` (`:83-121`). The generic dispatcher must add no
outer lock, recovery, or DTO reconstruction, exactly as required by the
transaction matrix
(`docs/specifications/interfaces/configuration-setup.md:663-674`).

The claim that the service currently returns an already-validated DTO is
refuted. `SetupStatusListProjector.Project` validates filter/row prerequisites
and returns a record copy, but never calls `SetupContractValidator.Validate`
(`src/CopilotAgentObservability.ConfigCli/Setup/Status/SetupStatusListProjector.cs:15-53`).
`SetupStatusService` also does not make that call; its tests invoke the
validator explicitly after `Status` returns
(`tests/CopilotAgentObservability.ConfigCli.Tests/SetupStatusServiceTests.cs:36-59`,
`:220-249`, `:363-391`). Serialization would validate later
(`src/CopilotAgentObservability.ConfigCli/Setup/Contracts/SetupJson.cs:6-12`),
but the sprint-wide T2c2 contract requires every final public DTO to be
validated before handoff
(`docs/superpowers/plans/2026-07-12-issues-66-67-configuration-setup.md:391-403`).

Therefore `DispatchStatus` must call `Validate(status(options.Adapter))` and
return that same instance. `Validate` performs no reconstruction and returns
the input object unchanged
(`src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs:532-536`).
Task 06 should retain the no-lock/no-recovery/same-instance tests and add a
malformed service-result rejection pin.

## Q6 — T2c2 task split

**Decision: `PINNED WITH CARD CORRECTIONS`.** The sequential Task 02-07 split
is sound, but the orchestrator must apply these smallest corrections before
dispatching the affected card; this audit does not edit task cards:

| Task | Audit result |
| --- | --- |
| 02 Apply ordering | Correct boundary. Implement Q1 order, remove only the redundant standalone validation, and retain the valid/non-valid non-Planned pair plus one-lock/one-recovery pin (`docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-02-apply-preflight-ordering.md:1-16`, `docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-02-apply-preflight-ordering.md:143-189`). |
| 03 diagnostic catalog | Correct functional slice, but explicitly treat it as reopened T2b ownership. Use Q2 Option A and only the three authorized files; keep `rerun_requested_setup_command` excluded and add both no-mutation tests (`docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-03-pre-mutation-diagnostic-catalog.md:1-31`, `docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-03-pre-mutation-diagnostic-catalog.md:82-128`). |
| 04 Apply invocation | Correct dispatcher slice. Amend its outcome table to include storage-code empty-target handling, the contextual `recovery_required` distinction, and mandatory post-failure reload from Q3; never project the pre-call row (`docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-04-apply-coordinator-invocation.md:28-41`, `docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-04-apply-coordinator-invocation.md:94-138`). |
| 05 Rollback | `BLOCKED` on Q4's rollback-domain owner correction and re-review. Replace the card's "known rows" with Q4's exhaustive 13-code catalog, add `unsafe_path`, both interrupted success codes, interrupted failure, and ledger read codes, and keep dispatcher-level `setup_busy` separate. Delete the code-only empty-target mapping: after the producer exposes trustworthy normal-result context, project its requested-row `ChangeSet` with rollback unavailable for every typed normal outcome, including post-validation `recovery_required` and `internal_error`; use empty targets for mandatory recovery, lock/storage, missing row, missing/invalid plan or identity, and malformed results. The dispatcher must not guess trust from `Code` or today's non-null `ChangeSet` (`docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-05-rollback-dispatcher.md:30-53`, `docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-05-rollback-dispatcher.md:134-169`). |
| 06 Status | Correct direct-delegation slice for ownership, but pure delegation still requires final validation of the same object. Change the implementation sketch from `status(options.Adapter)` to `Validate(status(options.Adapter))`; keep zero outer lock/recovery and no reconstruction (`docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-06-status-dispatcher.md:27-37`, `docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-06-status-dispatcher.md:80-102`). |
| 07 historical manifest | Correct test-only T2c2 slice. It proves Issue #61 historical-manifest behavior across #66 ledger-origin commands and does **not** satisfy the #66-to-#67 real adapter gate (`docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-07-historical-manifest-cross-surface.md:1-25`, `docs/superpowers/plans/2026-07-14-issue-66-t2-completion/task-07-historical-manifest-cross-surface.md:100-106`). |

Tasks remain sequential because 02-07 share dispatcher/test boundaries, and a
finding reopens its originating owner. The corrected ownership model remains:
dispatcher T2c2 owns Apply/Rollback/Status result composition, the reopened T2b
repair owns pre-mutation carrier validation, and T7 alone owns the real #66/#67
composition-root acceptance gate.

## Source audit and review notes

Sources checked include the canonical configuration-setup specification,
`docs/requirements.md`, `docs/spec.md`, the approved Issues #66/#67 design and
sprint-wide plan, the M3 handoff findings, Tasks 02-07 and #67 T3d/T7 cards,
the dispatcher, plan/ledger stores, adapter registry/carriers, contract
validator/catalog, Apply/Rollback/Recovery coordinators, rollback preflight,
Status service/list projector, and their named tests.

Implementer self-review found no product behavior, interface, security,
dependency, fixture, schema, task-card, or test change in this task. Residual
gates are the explicit migration `SPEC-CONFLICT` and the pending independent
read-only review.
