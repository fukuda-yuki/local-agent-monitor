# Task 10 (T9): Final independent reviews, full validation, and closeout

**Objective:** Run the four separate final reviews, resolve findings, execute
the pinned repository validation, run contradiction searches, and make the
single final durable-ledger update closing Issues #66 and #67. Read-only
except the ledger (and the sprint README state row if not already truthful).

**Depends on:** task-09 (T8) committed and reviewed.

**Files (T9 ownership):**
- Modify: `docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`
  (the final closeout update)
- Everything else read-only. A finding reopens its originating T1–T8 owner
  and every downstream gate; after the fix is reviewed and committed,
  execution returns here.

## Steps

- [ ] **Step 1: Dispatch four separate fresh read-only reviews** (one run
  each; do not combine):
  1. **#66 transaction/security/concurrency/status** — the completed T2
     surface plus the transaction foundation touched since; shipped-v1
     migration N/A confirmation.
  2. **#67 contracts** — official-source locations, Default-Profile-only
     rule, OS bounds, managed precedence and unverified-server boundary,
     endpoint classification, forbidden keys, closed warning/action
     emission, version floors (VS Code 1.128+, CLI 1.0.4+).
  3. **Real #66→#67 integration** — DTO/manifest/wrapper end-to-end,
     exceptional apply pairs, mutation-authority negatives, HTTP/proxy/UI
     N/A.
  4. **Repository-safe evidence and full diff** — negative markers
     (secrets/values/paths/exception text) across plans, ledger, logs,
     wrapper output, fixtures; then the complete commit-range diff read.
  Each reviewer maps requirements to exact executable tests and returns
  PASS or CHANGES REQUESTED with owner/file:line/contract citation.

- [ ] **Step 2: Resolve findings** by reopening owners (never patched here),
  then re-run the affected review.

- [ ] **Step 3: Run the pinned repository validation, exactly:**

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Expected: 0 warnings/0 errors; bootstrap exit 0; full suite pass. On failure
use systematic debugging and reproduce the root cause; do not substitute a
command, guess-fix, retry blindly, push, or open a PR.

- [ ] **Step 4: Contradiction searches.** Grep the touched documentation and
  specs for claims contradicting implementation (first-trace claims, stale
  command names, superseded task labels), and confirm
  `docs/requirements.md`/`docs/spec.md`/`docs/specifications/` agree with
  the shipped behavior. Run `git diff --check` and inspect the complete
  commit range and worktree (`git status` clean; `git log` range from the
  #66 plan baseline `df868e6` to HEAD read end-to-end).

- [ ] **Step 5: Final durable-ledger update** (the only T1–T9 ledger edit
  beyond the per-slice rows): completed task rows for T3a–T9 with commit
  ranges, focused/full counts, review verdicts; the requirement-to-test
  evidence table rows flipped from Pending with exact test names; unresolved
  minors and unverified scope (macOS/Linux runtime branches not executed on
  this host, deterministic-seam representations, anything else); the
  "Unverified Issue interfaces" section updated truthfully; sprint README
  M4/M5 states.

- [ ] **Step 6: Commit.**

```powershell
git add docs/sprints/sprint23-configuration-ownership-github-copilot
git commit -m "Issues #66-#67: docs(setup): close guided setup sprint ledger"
```

- [ ] **Step 7: Final report** (chat): exact commands and results, the four
  review verdicts, unresolved minors, unverified scope, issue-interface
  status, full commit range, and worktree state.

## Completion criteria

- Four review verdicts PASS, recorded with requirement-to-test mappings.
- Pinned validation suite passed at HEAD with captured output.
- Ledger and sprint documents truthful; no completion claim exceeds the
  evidence (in particular: no first-trace claim; un-executed OS branches
  listed as unverified).

**Report destination:** chat + the durable ledger (this task's edit).
