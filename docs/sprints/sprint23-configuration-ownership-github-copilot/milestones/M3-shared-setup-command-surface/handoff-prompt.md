# Sprint23 Issues #66/#67 implementation handoff

This is the self-contained implementation memo for resuming Issues #66 and
#67 after the current agent session is cleared. Paste everything below the
`---` line into the next session. Sprint evidence is historical context; the
current requirements and specifications remain authoritative.

---

```
Repository: C:\Users\mwam0\.codex\worktrees\80e4\copilot-agent-observability
Branch: codex/issues-66-67-guided-setup
Implementation baseline: 399f441 (working tree clean before this handoff-only documentation change)
Do not push or create a PR unless the user explicitly asks.

# Objective

Complete Issue #66 (configuration ownership ledger and reversible setup
transaction) and then Issue #67 (GitHub Copilot guided setup). Preserve the
existing implementation and continue from the exact T2c2 Apply-dispatch stop
point. Do not reimplement the completed transaction foundation.

# Read first, in order

1. AGENTS.md
2. docs/requirements.md
3. docs/spec.md
4. docs/specifications/interfaces/configuration-setup.md
5. docs/specifications/security-data-boundaries.md
6. docs/architecture.md
7. docs/decisions.md, especially D057
8. docs/superpowers/specs/2026-07-12-issues-66-67-configuration-setup-design.md
9. docs/superpowers/plans/2026-07-12-issues-66-67-configuration-setup.md
10. docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md
11. this handoff

The implementation plan's dependency order is still controlling:

T2c2 -> T2d -> T3a -> {T3b || T3c} -> T3d ->
{T4 || T5 || T6} -> T7 -> T8 -> T9.

# Current completion boundary

## Issue #66 foundation: completed and independently reviewed

The durable ledger contains the detailed commit ranges, focused tests, fault
matrices, and reviews for the following foundation. Treat those rows as the
evidence source instead of reconstructing this work:

- Strict setup wire DTOs, validation, serializer, fixed code/warning/action
  catalogs, and SetupOptions parsing.
- User-scoped runtime paths and an operation-lifetime exclusive lock.
- Private plan and ownership-ledger v1 stores with strict version handling,
  bounded reads, atomic persistence, and reopen evidence. There is no legacy
  migration requirement for strict v1.
- Bounded JSONC/TOML mutation, path/reparse checks, hashes, backups, atomic file
  replacement, and current-user environment member mutation.
- Apply journaling, reverse compensation, restart recovery, notification
  replay, and normal rollback for file, environment, mixed, and all-NoOp
  targets.
- Deterministic concurrency/fault coverage for stale state, supersession,
  rollback races, intent/effect ambiguity, terminal hashes, and the audited
  actual-producer-to-fresh-store recovery windows.
- Strict status snapshots, historical source-capability manifests, status
  projection/list behavior, recovery-aware status service, shared rollback
  preflight, and the adapter registry bridge.
- The generic adapter identifier/target routing and typed plan, revalidation,
  and apply diagnostic carriers.

Important approved recent commits after the detailed ledger foundation:

- c748e12, e222b89, 4d0181e: target delegation and canonical adapter-slug
  boundaries.
- 8a2c0af, 8c1e14f, 82ec4e6, 3cf85da: typed sanitized diagnostics across the
  adapter/revalidation/apply carrier.
- e2cc532 and 3ed6cfb: validated Plan command dispatch; independent review
  PASS.
- ab40ea0 and bfd0cc7: command-aware current/historical manifest contract and
  fixture correction; independent review PASS.
- f242a6b: Apply target projection from the ledger; independent review PASS.

## Shared #66 command surface: partially complete

T2c1 Plan dispatch is complete. T2c2 Apply dispatch has only its projector and
preflight skeleton. Commit 399f441 added the preflight routing and passed its
tests, but its independent review result is CHANGES REQUESTED. Therefore
399f441 is a safe checkpoint, not an approved or deliverable completion.

At 399f441 the latest recorded evidence was:

- SetupCommandDispatcherTests: 78/78 passing.
- Dispatcher + Apply + Rollback + Recovery + Status related tests: 746/746
  passing in the independent review run.
- Full ConfigCli test project: 1,990/1,990 passing.
- dotnet build CopilotAgentObservability.slnx: 0 warnings, 0 errors.
- Working tree: clean before this documentation-only handoff.

These green tests do not override the review findings below. The Playwright
bootstrap and full solution test suite have not been rerun at 399f441 and are
still required at final closeout.

## Issue #67: shared contracts only; user-visible setup is not implemented

Issue #67 has the closed shared code/warning/action catalog, manifest semantics,
adapter interfaces, registry, and typed diagnostic transport needed by later
tasks. It does not yet have a production GitHub Copilot adapter or a usable
guided-setup flow.

The following work has not been implemented:

- T3a: platform/runtime observation types and common GitHub Copilot detection.
- T3b: managed-policy precedence (native > server > file whole-channel, plus
  independent VS Code enterprise policy handling).
- T3c: bounded Local Monitor endpoint ownership probe.
- T3d: aggregate GitHubCopilotSetupAdapter, target-partition seam, detection
  composition, and manifest pairing.
- T4: VS Code Stable/Insiders Default Profile target.
- T5: Copilot CLI Windows Apply and macOS/Linux detect/plan unsupported target.
- T6: App/SDK no-write guidance target.
- T7: production composition root and executable #66-to-#67 producer/consumer
  integration.
- T8: wrapper packaging, parity checks, and verified operator documentation.
- T9: final independent reviews, security/concurrency/migration/integration
  closeout, full validation, and final durable-ledger update.

# Immediate blocking review findings at 399f441

1. Apply preflight ordering is wrong in
   src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs.
   The current code calls SetupStorageValidation.ValidatePlan(plan) before
   SetupTransactionEvidence.RequireImmutableIdentity(plan, changeSet) and
   before checking the lifecycle. SetupPlanStore.Load already validates the
   standalone plan shape. The required order is:

   Load plan -> RequireImmutableIdentity -> inspect lifecycle -> only when
   Planned, ValidatePlanAndLedger.

   Add a deterministic regression for a non-Planned identity mismatch that
   returns recovery_required, preserves the persisted adapter, emits no target
   projection, and performs zero adapter resolution/apply activity. Pair it
   with a valid non-Planned case that returns invalid_arguments with targets
   projected from the ledger.

2. A valid registered Planned Apply currently ends at an intentional
   internal_error sentinel. Lines near the end of DispatchApply resolve the
   adapter, discard the injected apply delegate, and return InternalError. It
   never invokes the real SetupApplyCoordinator. Replace the sentinel and map
   the coordinator result before T2c2 can be approved.

3. Minor missing pin: add a normal applicable RecoveryDisposition.None path
   proving exactly one lock acquisition and one recovery call.

4. Open design/audit item discovered immediately before the session stopped:
   SetupApplyCoordinator currently duplicates the closed warning/action
   catalog in RevalidationWarningCodes and RevalidationNextActionCodes. This
   duplication protects the pre-mutation boundary: removing it without an
   equivalent shared validation call could allow a lexically safe but
   unsupported diagnostic to mutate state before the final dispatcher DTO
   validator rejects it. Re-audit the owner contract before editing. A likely
   minimal design is to expose a reusable diagnostic validation operation from
   SetupContractValidator, invoke it inside SetupApplyCoordinator before any
   mutation, remove the duplicate sets, and retain final full-result validation
   in the dispatcher. This is an unapproved hypothesis, not an implementation
   instruction; verify it against the governing spec and ownership plan first.

Because the current area has accumulated boundary corrections, do not start
with another local patch. First re-audit the T2b/T2c2 ownership, validation
ordering, and pre-mutation diagnostic contract, then update the task split and
tests. If that audit contradicts the current plan, update the current
specification/plan before code as required by AGENTS.md.

# Work not yet done in T2

- Complete Apply dispatcher coordinator invocation and every success/failure,
  recovered/failed-recovery, adapter, target, warning, and next-action mapping.
- Implement Rollback dispatcher using the shared rollback preflight and real
  rollback coordinator.
- Implement Status dispatcher as direct delegation to the recovery-aware status
  service.
- Complete historical-manifest cross-surface executable coverage for all
  applicable commands.
- T2d process surface: CliApplication injection/exit mapping, help text,
  scripts/local-monitor/setup.ps1, and byte-for-byte wrapper forwarding tests.
- Run an early #66 command-surface integration review before T3 starts.

# Contract and interface status

| Producer | Consumer | Contract state | Executable evidence |
| --- | --- | --- | --- |
| SetupOptions parser | SetupCommandDispatcher | Target token and canonical adapter routing complete | Focused parser/dispatcher/registry tests complete |
| Adapter Plan | Dispatcher | Typed sanitized diagnostics complete | Carrier tests and Plan dispatcher tests complete |
| Plan + ownership ledger | Apply dispatcher | Immutable identity and ledger-origin target projection defined | Projector tests complete; preflight ordering review failed |
| Apply revalidator/coordinator | Dispatcher result | Typed diagnostics exist | Final coordinator invocation/mapping not implemented |
| Rollback coordinator | Dispatcher result | Coordinator and shared preflight exist | Public dispatcher mapping not implemented |
| Status service | Dispatcher result | Recovery-aware service exists | Public dispatcher delegation not implemented |
| Config CLI process | PowerShell wrapper | Public JSON/exit/forwarding contract specified | T2d not implemented |
| Real #66 producer DTO | #67 target partitions | Contract specified | No real cross-surface executable test yet |
| HTTP / proxy / UI | Any setup component | N/A for Issues #66/#67 | No DTO is expected on these surfaces |

The command-aware source-capability fixture correction at bfd0cc7 uses the
actual command semantics. Do not create mocks or fixtures that diverge from
the real producer DTO. The first #67 integration fixture must be captured from
the real #66 producer path at T7, not invented independently by a target.

# Required resume sequence

1. Inspect git status and confirm the implementation baseline and any handoff
   documentation commit. Preserve all user and other-agent changes.
2. Re-audit the four blocking points above and record the corrected T2c2 task
   boundaries before dispatching implementation.
3. Fix and independently review the Apply preflight ordering tests.
4. Resolve the pre-mutation diagnostic-catalog ownership, with deterministic
   no-mutation tests for invalid diagnostics.
5. Replace the Apply sentinel with real coordinator invocation and complete
   result mapping; run focused, related, full ConfigCli, and build validation;
   obtain an independent review.
6. Implement and separately review Rollback dispatcher, Status dispatcher, and
   historical-manifest regression. Then perform the early T2 integration
   review.
7. Implement and review T2d. Do not begin T3 until the T2 gate passes.
8. Continue T3 through T9 in the exact dependency order above. T3b/T3c and
   T4/T5/T6 are the only planned parallel groups.

For every delegated task, explicitly state: objective, owned scope, non-scope,
constraints, completion criteria, validation command, and report destination.
Use a different subagent for independent review. Do not accept DONE or pass
counts alone; map each requirement to the exact executable test and inspect the
diff/command output. Security/concurrency/migration and final integration review
remain mandatory.

# Validation commands

Focused dispatcher iteration:

    dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupCommandDispatcherTests

Full ConfigCli gate after each coherent T2 slice:

    dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
    dotnet build CopilotAgentObservability.slnx

Required repository closeout, from the repository root and without substituting
commands:

    dotnet build CopilotAgentObservability.slnx
    pwsh scripts\test\install-playwright-chromium.ps1
    dotnet test CopilotAgentObservability.slnx

Use deterministic barriers/fault seams for atomicity, rollback, stale-state,
and concurrency tests. Do not use sleeps or retries to hide failures. For a
full-test failure, use systematic debugging and reproduce the root cause before
editing.

# Why the work took longer than expected

This was not primarily idle execution. Issue #66 contains a large transaction
and recovery subsystem whose correctness gates include atomic persistence,
rollback, stale-state classification, deterministic concurrency, strict wire
validation, and restart evidence. Most of that foundation is now complete.
However, the original estimate did not sufficiently account for the sequential
dependency gate before any #67 target work could start.

There was also material rework. Independent reviews repeatedly found boundary
problems in ownership, status lifecycle, manifest command semantics, adapter
routing, and the current Apply preflight ordering. Those corrections prevented
unsafe integration, but the work was split too finely and the durable sprint
summary was not kept current enough, making progress appear slower and harder
to assess. The next session should use larger reviewable vertical slices only
after freezing T2c2's validation/ownership contract, and should update the
durable ledger at each approved slice instead of reconstructing status later.

# Stop conditions

- Do not call Issues #66/#67 complete while 399f441's review findings or the
  Apply sentinel remain.
- Do not claim #67 complete until the real #66 producer is consumed by all
  three target partitions through T7 executable tests.
- Do not push, tag, open/update/merge a PR, or rewrite remote history.
- At final handoff, report exact commands/results, unverified scope, review
  verdicts, unresolved minors, issue-interface gaps, commit range, and worktree
  state in the durable ledger.
```

## Resolution recorded 2026-07-15

The memo above is preserved as the historical `399f441` stop condition. Its
four blocking findings are resolved, Tasks 02-10 have passed their final
independent reviews, and Task 11 passes the **Issue #66 T2 gate only** at
reviewed Task 10 HEAD `0b0885424f59ad5cdfafe0fa77b5e2fc53ed0407`.
The validated implementation range is `df868e6..0b08854`.

Resolution mapping:

- findings 1 and 3: Task 02, `b1775fe..f2b6b40`, final review Spec PASS,
  Code/Test Quality APPROVED, Security/fail-closed PASS, C0/I0/M0;
- finding 4: Task 03, `51d79c5..2e171dd`, final review Spec PASS, Code/Test
  Quality APPROVED, Security/pre-mutation PASS, C0/I0/M0;
- finding 2: Task 04, `f7d6d12..61dde7b`, with Task 08 trust remediations
  `2d9940b` and `be1fac9`; final review Spec/Quality/Security/Transaction PASS,
  C0/I0/M0;
- Task 04a trusted Rollback carrier, Task 05 Rollback dispatcher, and Task 06
  Status dispatcher: final reviews PASS/APPROVED, C0/I0/M0;
- Task 07 historical-manifest regression: final Spec/Test Quality/Security/
  Cross-surface review PASS, C0/I0/M0 after its report-accuracy Minor was
  corrected;
- Task 08 fresh integration rerun: PASS, C0/I0/M0, accepted minors none;
- Task 09 CLI process surface: final fresh review PASS, C0/I0/M0 after I1-I4
  were closed; exhaustive 29-code exit mapping and all four verbs are covered;
- Task 10 wrapper transport: review PASS C0/I0/M0 for spec, PowerShell bytes,
  tests, security, and Issue contract. Parsed pre-T7 Status remains intentional
  `internal_error`/5, recognized invalid Apply is `invalid_arguments`/2, and
  bare setup has no stdout.

Pinned Task 11 validation ran once in the required order, with no substitution
or retry:

- `dotnet build CopilotAgentObservability.slnx`: PASS; 7 projects, 0 warnings,
  0 errors;
- `pwsh scripts\test\install-playwright-chromium.ps1`: PASS; exit 0, no output;
- `dotnet test CopilotAgentObservability.slnx`: PASS; ConfigCli 2,159/2,159 and
  LocalMonitor 936/936, total 3,095/3,095, 0 failed, 0 skipped.

No accepted Minor remains unresolved in Tasks 02-10. Migration is N/A because
v1 is the first shipped ledger schema; the committed-v1 fresh-store restart
test already proves write/close/reopen byte identity and no v0/v2/migration/temp
artifact. HTTP, proxy, and UI are N/A. This Windows run did not execute native
macOS/Linux runtime branches; their evidence remains deterministic/injected or
static.

This resolution does **not** declare Issue #66 or Issues #66/#67 finally
complete. Issue #67 T7 must wire `Program.cs` to the production dispatcher and
prove the real aggregate/three-partition producer-consumer path plus direct-CLI
and repository-wrapper `setup status` `status_ready`/exit-0 byte parity. T8
release packaging/operator documentation and T9 final joint reviews remain
open. Resume from the Issue #67 plan, not from the historical `399f441` repair
sequence above.
