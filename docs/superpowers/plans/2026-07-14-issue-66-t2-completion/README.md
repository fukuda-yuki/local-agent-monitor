# Issue #66 T2 Completion Implementation Plan (index)

> **For agentic workers:** Each `task-NN-*.md` file in this directory is one
> self-contained mission card for a Codex implementation run. Execute tasks in
> the dependency order below, one task per run, with an independent read-only
> review between tasks. REQUIRED SUB-SKILL for the orchestrating session:
> superpowers:subagent-driven-development (or the repository's Codex
> delegation flow). Steps inside each task use checkbox (`- [ ]`) syntax.

**Goal:** Close the remaining Issue #66 work: fix the four blocking review
findings at `399f441`, complete the generic Apply/Rollback/Status dispatcher
(T2c2), expose the process and wrapper surfaces (T2d), and pass the T2 gate.

**Architecture:** The Config CLI is the only result producer. The generic
`SetupCommandDispatcher` constructs plan/apply/rollback `SetupCommandResult`
values and delegates status directly to `SetupStatusService`. `CliApplication`
serializes the returned DTO via `SetupJson` and maps exit codes; the
PowerShell wrapper forwards stdout byte-for-byte. No HTTP, proxy, UI,
database, AppHost resource, project, or dependency is added.

**Tech stack:** .NET (existing ConfigCli project), xunit, PowerShell 7 wrapper
script. No new PackageReference.

**Scope:** Issue #66 only. No #67 task (T3a and later) starts from this plan.
Issue #67 planning resumes after the T2 gate passes.

## Source of truth

Read before implementing any task, in this order:

1. `AGENTS.md`
2. `docs/requirements.md`
3. `docs/spec.md`
4. `docs/specifications/interfaces/configuration-setup.md` (the governing
   contract; "the spec" below)
5. `docs/specifications/security-data-boundaries.md`
6. `docs/decisions.md` D057
7. `docs/superpowers/plans/2026-07-12-issues-66-67-configuration-setup.md`
   (the sprint-wide plan; its T2c2/T2d ownership matrix still applies)
8. `docs/sprints/sprint23-configuration-ownership-github-copilot/milestones/M3-shared-setup-command-surface/handoff-prompt.md`
   (the authoritative resume boundary and the four blocking findings)

## Global constraints

- Baseline: branch `codex/issues-66-67-guided-setup`, commit `df868e6`
  (handoff docs) on top of implementation baseline `399f441`.
- Commit `399f441` (Apply preflight routing) is a safe checkpoint whose
  independent review verdict is CHANGES REQUESTED. Its findings are fixed by
  Tasks 01–04; do not treat its tests as approval.
- File ownership: Tasks 02–07 edit only
  `src/CopilotAgentObservability.ConfigCli/Setup/Cli/SetupCommandDispatcher.cs`
  and `tests/CopilotAgentObservability.ConfigCli.Tests/SetupCommandDispatcherTests.cs`
  (Task 03 additionally edits the files its audit record authorizes). Tasks
  09–10 edit only the T2d-owned files listed in each card. Frozen T2c1 plan
  hunks in the dispatcher are not rewritten.
- Every task is test-driven: write the failing test, run it RED, implement,
  run it GREEN, run the task's focused filter, run `git diff --check`, commit.
- Commit messages: `Issue #66: <type>(setup): <summary>` (Conventional
  Commits); `feat`/`fix` bodies must record why the change was needed.
- Do not push, tag, or open/update a PR. Local commits only.
- No new dependency, no schema/version change, no fallback or compatibility
  path, no catalog (code/warning/action) declaration change.
- Adapter/warning/next-action values remain the closed catalogs declared in
  `Setup/Contracts/SetupCodes.cs` and validated by `SetupContractValidator`.
- Deterministic tests only: fault seams/barriers, no sleeps, no retries.
- Repository-safe evidence: no raw values, paths of user data, or exception
  text in public DTOs, test fixtures, or logs.

## Dependency order

```text
task-01 contract audit (read/record only)
   |
   v
task-02 apply preflight ordering fix  (findings 1 + 3)
   |
   v
task-03 pre-mutation diagnostic catalog ownership  (finding 4)
   |
   v
task-04 apply coordinator invocation and result mapping  (finding 2)
   |
   v
task-05 rollback dispatcher
   |
   v
task-06 status dispatcher
   |
   v
task-07 historical-manifest cross-surface regression
   |
   v
task-08 early T2 integration review (read-only gate)
   |
   v
task-09 T2d CLI process surface
   |
   v
task-10 T2d PowerShell wrapper
   |
   v
task-11 T2 closeout validation and ledger update
```

No parallel execution: Tasks 02–07 share the same two files, and Tasks 09–10
are sequential because the wrapper tests consume the exit mapping.

## Delegation protocol (per task)

For every Codex run state explicitly: objective, owned scope, non-scope,
constraints, completion criteria, validation commands, and report
destination — all of which are in the task card. Use a different, fresh
read-only Codex run for the independent review. Do not accept "DONE" or pass
counts alone; map each requirement to the exact executable test and inspect
the diff and command output.

## Validation commands

Focused iteration (per task; each card names its filter):

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupCommandDispatcherTests
```

After each coherent slice (Tasks 04, 07, 10 at minimum):

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
```

Repository closeout (Task 11 only; never substitute commands):

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

## Ledger update policy

The 2026-07-14 handoff retrospective supersedes the older "T9 is the sole
ledger editor" rule for #66 slices: after each task's independent review
passes, append that task's evidence row to
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`
(Task state table) in a small documentation-only commit
(`Issue #66: docs(setup): record <task> evidence`). Record: commit range,
focused/full test counts, review verdict, unresolved minors. T9 remains the
final sprint-wide closeout for Issues #66–#67 jointly.

## Stop conditions

- A finding in any independent review reopens the owning task; downstream
  tasks do not start until the reopened task passes review.
- Two consecutive fix cycles in one area trigger a contract/test-design
  re-audit (return to Task 01 scope) before more code changes.
- If a task's required behavior contradicts
  `docs/specifications/interfaces/configuration-setup.md`, stop and update the
  spec first (AGENTS.md rule); do not encode the contradiction in code.
- Do not call Issue #66 T2 complete until Task 11's full validation and
  ledger update are done.
