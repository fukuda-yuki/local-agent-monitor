# Task 09 (T8): Packaging, wrapper parity, and operator documentation

**Objective:** Package the T2d-owned `scripts/local-monitor/setup.ps1` and
the T7-composed Config CLI into the existing release layout (self-contained
`app/config-cli/`, no installed .NET runtime requirement), prove wrapper
parity in release mode, and document only verified behavior for operators —
never claiming that static setup produces a first trace.

**Depends on:** task-08 (T7) committed and reviewed.

**Files (T8 ownership; exact per the sprint plan matrix):**
- Modify: `scripts/local-monitor/package-release.ps1`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorScriptTests.cs`
  (package/layout/executable and wrapper-parity cases only)
- Modify: `README.md`
- Modify: `scripts/local-monitor/README.md`
- Modify: `docs/user-guide/local-monitor.md`
- Modify: `docs/agent-guides/repository-workflow.md`
- Modify: `docs/specifications/interfaces/config-cli.md`
- Modify: `docs/sprints/sprint23-configuration-ownership-github-copilot/README.md`
- Modify: `docs/task.md`

**Non-scope:** any setup runtime behavior change, the wrapper command
contract (frozen at #66 T2d), unrelated startup/user-env scripts, the
release workflow itself, `ledger.md` (T9 owns the final update; per-slice
rows still follow the README policy).

**Interfaces:**
- Consumes: the T2d wrapper (`scripts/local-monitor/setup.ps1`), the T7
  production CLI, the existing release packaging conventions in
  `package-release.ps1` and their tests in `LocalMonitorScriptTests`.
- Produces: a release layout in which
  `pwsh scripts/local-monitor/setup.ps1 <verb> ...` (repository mode) and
  the packaged wrapper against `app/config-cli/` (release mode) return
  byte-identical `setup.v1` stdout for the same machine state.

## Steps

- [ ] **Step 1: Read the current packaging contract.**
  `package-release.ps1`, the existing layout assertions in
  `LocalMonitorScriptTests`, and `docs/specifications/interfaces/config-cli.md`'s
  release-mode wrapper sentences. The wrapper must select release mode by the
  mechanism the spec pins (verify: presence of `app/config-cli/` relative to
  the script's packaged location) — if the T2d wrapper does not yet
  implement release-mode selection, that hunk is T8's to add ONLY if the
  spec assigns it to packaging; otherwise stop and reopen the T2d owner.

- [ ] **Step 2: Write the failing packaging tests** in
  `LocalMonitorScriptTests`: the release layout contains self-contained
  `app/config-cli/` with the published executable; `setup.ps1` is included
  at its packaged path; a packaged-wrapper invocation of `setup status`
  returns exit 0 and stdout that parses as `setup.v1` and byte-equals the
  repository-mode invocation on the same isolated runtime root; no `dotnet`
  on PATH is required for the release-mode run (assert via an environment
  that hides the SDK, following the existing self-contained test technique
  in this file).

- [ ] **Step 3: Run RED, extend `package-release.ps1`, run GREEN.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~LocalMonitorScriptTests
```

- [ ] **Step 4: Update the documentation set.** Every claim must trace to an
  executable test or recorded command evidence:
  - `docs/specifications/interfaces/config-cli.md`: add the four setup verbs
    to the command surface with exit-code/stdout contract references.
  - `docs/user-guide/local-monitor.md` + `scripts/local-monitor/README.md` +
    `README.md`: guided-setup usage (plan → apply → status → rollback) with
    implemented examples; state explicitly that setup success does not prove
    a trace arrived (`run_first_trace_doctor` hands off to Issue #69).
  - `docs/agent-guides/repository-workflow.md`: CLI smoke examples for the
    setup verbs.
  - Sprint README: M4 state update. `docs/task.md`: roadmap status.
  Keep each document in its existing language and tone (user-facing guides
  stay in their current style).

- [ ] **Step 5: Link/path check and full validation.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

  Manually verify every relative link added to the touched documents
  resolves (open each target path).

- [ ] **Step 6: Commit.**

```powershell
git add scripts/local-monitor/package-release.ps1 tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorScriptTests.cs README.md scripts/local-monitor/README.md docs/user-guide/local-monitor.md docs/agent-guides/repository-workflow.md docs/specifications/interfaces/config-cli.md docs/sprints/sprint23-configuration-ownership-github-copilot/README.md docs/task.md
git commit -m "Issues #66-#67: docs(setup): record guided setup evidence"
```

## Completion criteria

- Release layout packages the self-contained CLI and wrapper; parity and
  no-runtime-required assertions executable.
- All operator documentation updated with implemented examples only; no
  first-trace claim anywhere (grep the touched docs for trace-receipt
  claims).
- Wrapper command contract unchanged (no `setup.ps1` behavior diff unless
  Step 1 assigned the release-mode hunk here — then it is reviewed as such).
- Both test projects and the build pass; independent review PASS.

**Report destination:** chat + ledger row per README policy.
