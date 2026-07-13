# Task 11: T2 closeout validation and durable ledger update

**Objective:** Run the pinned repository validation suite at the final Task 10
commit, verify the T2 gate conditions, and update the durable sprint ledger
and M3 handoff so Issue #66's command-surface state is recorded truthfully.
This closes the Issue #66 scope of this plan; Issue #67 (T3a onward) is a
separate follow-on decision.

**Depends on:** Task 10 committed and reviewed.

**Files:**
- Modify: `docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`
- Modify: `docs/sprints/sprint23-configuration-ownership-github-copilot/milestones/M3-shared-setup-command-surface/handoff-prompt.md`
  (or add a successor continuation note beside it — do not delete the
  historical handoff content; append a dated "resolved" section)
- Modify: `docs/sprints/sprint23-configuration-ownership-github-copilot/README.md`
  (M3 state row)
- No production or test code. A validation failure here is fixed by
  reopening the owning task, never by editing in this task.

## Steps

- [ ] **Step 1: Confirm a clean tree and record the commit range.**

```powershell
git status
git log --oneline df868e6..HEAD
git diff --check
```

- [ ] **Step 2: Run the pinned repository validation, exactly these commands
  from the repository root (no substitution; a failure stops this task):**

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Expected: build 0 warnings/0 errors; bootstrap exit 0; full solution suite
pass. On failure: systematic debugging, root-cause reproduction, reopen the
owning task. Do not retry blindly or swap in a narrower command as evidence.

- [ ] **Step 3: Verify the T2 gate checklist** (each item must cite its task
  and review verdict):
  - 399f441 findings 1–4 fixed and independently reviewed (Tasks 02–04).
  - Four-command dispatcher complete: Plan (T2c1), Apply, Rollback, Status
    (Tasks 04–06), each with complete outcome mapping.
  - Historical-manifest cross-surface regression executable (Task 07).
  - Early T2 integration review PASS (Task 08).
  - Process surface + exhaustive 29-code exit mapping (Task 09) and
    byte-faithful wrapper (Task 10).
  - Closed exceptional apply matrix pinned: plan-time unknown adapter →
    `plan`/`unsupported_adapter`; persisted plan with removed adapter →
    `apply`/`unsupported_adapter` with retained adapter ID and zero activity;
    macOS/Linux CLI persisted plan → `apply`/`unsupported_target` with no
    write of any kind.

- [ ] **Step 4: Update the durable documents.**
  - `ledger.md`: replace the coarse `Shared setup commands | #66 | Pending`
    row with the completed T2 evidence rows (commit ranges, focused/full
    counts, review verdicts, unresolved minors — including "wrapper not yet
    in release layout (T8)" and any audit-accepted residuals). Update the
    "Current continuation note" to point at this plan directory and state
    that the 399f441 findings are resolved.
  - Handoff: append a dated resolution note (findings 1–4 resolved at
    commits X..Y; T2 gate passed; next boundary is T3a).
  - Sprint README: set M3 to Complete (or the truthful state if any residual
    blocks it) and leave M4/M5 Pending.

- [ ] **Step 5: Commit the documentation.**

```powershell
git add docs/sprints/sprint23-configuration-ownership-github-copilot
git commit -m "Issue #66: docs(setup): record T2 command-surface completion"
```

- [ ] **Step 6: Final report** (chat): exact commands run and results,
  unverified scope (anything not executed on this host, e.g. Linux/macOS
  runtime branches), review verdicts per task, unresolved minors, issue
  interface gaps that remain for #67 (the real #66→#67 producer/consumer
  gate is T7 and is NOT claimed here), the full commit range, and worktree
  state.

## Completion criteria

- All three pinned validation commands pass at HEAD, evidence captured.
- Ledger, handoff, and sprint README reflect the truthful T2 state.
- The report explicitly does NOT claim Issue #66/#67 sprint completion:
  T7 (real producer/consumer integration), T8 (packaging/docs), and T9
  (final reviews) remain, and #67 target work has not started.

**Report destination:** chat + the durable ledger (this task's own edits).
