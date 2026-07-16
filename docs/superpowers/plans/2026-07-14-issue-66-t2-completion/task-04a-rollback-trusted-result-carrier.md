# Task 04a: Rollback trusted-result carrier

**Purpose:** Establish the rollback-domain trust boundary required by audit Q4
before the public dispatcher maps results. A trustworthy requested-row result
is explicit and cannot be inferred from `Code`: immutable identity failure is
untrusted and has no projectable row; identity success retains the requested
ledger row even when later journal, evidence, observation, preparation,
persistence, or compensation work fails.

**Depends on:** Task 04 committed and independently reviewed; audit record Q4
and Q6 approved. Task 05 is blocked until this task's focused validation and
independent read-only review both pass.

**Owned scope (exact):**

- `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackCoordinator.cs`
  for `SetupRollbackExecutionResult` and every direct-result construction.
- `src/CopilotAgentObservability.ConfigCli/Setup/Transactions/SetupRollbackPreflightEvaluator.cs`
  for identity-first ordering and explicit distinction between identity failure
  and post-identity journal/evidence failure.
- `tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs` for
  focused producer/carrier contract coverage.

**Non-scope:** No dispatcher or public DTO mapping; no storage or recovery
semantics; no lock/recovery ownership changes; no 13-code catalog declaration;
no specifications, task cards, dependencies, compatibility paths, v0/v2
fixtures, or fallback behavior. Task 05 owns public dispatcher projection.

**Constraints:**

- Choose one unambiguous contract: either non-null `ChangeSet` is the sole
  proof of a trustworthy requested row, or add one explicit equivalent trust
  marker. In either shape, trusted means a non-null row matching
  `RequestedChangeSetId`; untrusted means no projectable row, and contradictory
  combinations are invalid.
- All direct normal outcomes after immutable identity retain trust, including
  `recovery_required` and `internal_error`. Mandatory recovery and every
  failure before identity do not.
- Preserve dispatcher-owned one non-waiting lock and coordinator-owned
  mandatory recovery; this task does not move either boundary.
- Deterministic existing in-memory fault, checkpoint, or barrier seams only;
  no sleeps, timing polls, or retries. Keep evidence repository-safe.

**Required deterministic owner/test matrix (audit Q4, copied verbatim):**

1. lifecycle-ineligible plus rebound identity returns untrusted
   `recovery_required`, proving identity precedes lifecycle;
2. an Applied row with failed immutable identity returns untrusted
   `recovery_required` (`tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs:768-783`);
3. identity success followed by journal/rollback-evidence failure returns
   trusted `recovery_required` with the requested row;
4. a valid lifecycle-ineligible row returns trusted
   `rollback_not_available` (`tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs:478-503`);
5. post-validation rollback-journal and ledger-preparation faults each retain a
   trusted `recovery_required` row (`tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs:592-627`);
6. a post-identity observation fault and an attempted-outcome persistence fault
   after a stale/not-available classification each retain a trusted
   `internal_error` row (`tests/CopilotAgentObservability.ConfigCli.Tests/SetupRollbackTests.cs:399-443`,
   `:703-729`).

Every trusted case asserts `Recovery == null`, the chosen trust signal, and a
non-null `ChangeSet` whose ID equals `RequestedChangeSetId`; every untrusted
direct case asserts the inverse trust signal and no projectable row.

**Implementation steps:**

- [ ] Write the six failing producer/carrier tests above and run them RED for
  the expected trust-boundary reasons.
- [ ] Implement identity-first preflight and the explicit trusted-result
  invariant in the two owned production files. Preserve all existing direct
  outcome codes and coordinator-owned recovery behavior.
- [ ] Run the focused suite GREEN, inspect the owned diff, and obtain
  independent read-only review PASS before Task 05 starts.

**Validation commands:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupRollbackTests
git diff --check
```

**Worktree/branch:** repository root
on `codex/issues-66-67-guided-setup`.

**Report destination:** chat + ledger row per the Issue #66 README policy,
including the six-context matrix, focused output, and independent review PASS.

**Local commit subject:**
`Issue #66: fix(setup): make rollback result trust explicit`
