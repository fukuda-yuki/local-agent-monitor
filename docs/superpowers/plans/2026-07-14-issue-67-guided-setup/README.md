# Issue #67 Guided Setup Implementation Plan (index)

> **For agentic workers:** Each `task-NN-*.md` file in this directory is one
> self-contained mission card for a Codex implementation run. Execute tasks in
> the dependency order below, one task per run, with an independent read-only
> review between tasks. REQUIRED SUB-SKILL for the orchestrating session:
> superpowers:subagent-driven-development (or the repository's Codex
> delegation flow). Steps inside each task use checkbox (`- [ ]`) syntax.

**Goal:** Deliver the GitHub Copilot guided setup (Issue #67): platform
observations, managed-policy and endpoint classification, the aggregate
`github-copilot` adapter with three target partitions (VS Code, Copilot CLI,
App/SDK), the production composition root joining the real #66 producer, and
packaging/documentation/closeout.

**Architecture:** Adapters produce internal typed plan carriers only; the #66
coordinator owns every mutation/recovery/rollback; the generic dispatcher
produces the sole public `SetupCommandResult`. T3 freezes the shared
platform/detection/policy/endpoint/aggregate seam; T4/T5/T6 each own one
disjoint target-partition subdirectory; T7 alone composes and registers the
one production adapter. No HTTP server, proxy, UI, database, AppHost
resource, new project, or dependency is added.

**Tech stack:** .NET (existing ConfigCli project), xunit, deterministic
platform fakes (`SetupTestPlatform`). No new PackageReference.

**Prerequisite gate:** The Issue #66 T2 gate must have passed first — every
card in the authoritative dependency order in
`docs/superpowers/plans/2026-07-14-issue-66-t2-completion/` (including Task
01a's v1 restart evidence and Task 04a's rollback carrier) must be complete
with review PASS. No task in this directory starts before that.

T3d may add one skeletal, explicitly non-gating compatibility smoke test using
the real aggregate adapter, scripted partitions, the real #66 registry/
dispatcher Plan path, and `SetupJson` serialization. This proves only
adapter/carrier/manifest/result compatibility and does not prove target
behavior, mutation, production composition, or Issue #67 completion. T7
remains the first real all-partition producer/consumer integration gate.

## Source of truth

Read before implementing any task, in this order:

1. `AGENTS.md`
2. `docs/requirements.md`
3. `docs/spec.md`
4. `docs/specifications/interfaces/configuration-setup.md` ("the spec";
   especially "GitHub Copilot adapter", "VS Code GitHub Copilot Chat",
   "Terminal GitHub Copilot CLI", "GitHub Copilot App / SDK", "Endpoint and
   error-state detection", "Security and evidence rules")
5. `docs/specifications/security-data-boundaries.md`
6. `docs/decisions.md` D057
7. `docs/superpowers/plans/2026-07-12-issues-66-67-configuration-setup.md`
   (sprint-wide plan; its T3–T9 ownership matrix is binding)
8. The Issue #66 plan's audit record
   (`../2026-07-14-issue-66-t2-completion/audit-record.md`) for the frozen
   carrier/diagnostic contracts

## Global constraints

- Adapter ID is exactly `github-copilot`. Target labels are exactly
  `vscode-stable-default-user-settings`,
  `vscode-insiders-default-user-settings`, `copilot-cli-user-environment`,
  and `github-copilot-app-sdk-guidance`.
- Version floors are fixed from official sources: VS Code `1.128.0`+
  (Stable and Insiders), Copilot CLI `1.0.4`+.
- The code/warning/next-action catalogs are already declared and closed
  (`Setup/Contracts/SetupCodes.cs`, validator sets). No task adds, renames,
  or removes a catalog value. T3d and the target tasks add only
  semantic-positive tests proving declared values are emitted in their
  intended combinations.
- Adapters never construct `SetupCommandResult`, never serialize JSON, never
  write through a platform API outside the #66 coordinator, and never touch
  the dispatcher/CLI/wrapper files.
- Manifest pairing: VS Code targets pair `github-copilot-vscode`, CLI pairs
  `github-copilot-cli` via `SourceCapabilityManifestLoader`; App/SDK has a
  null manifest. A new plan must equal the current canonical manifest exactly
  (property order ignored).
- Security (spec "Security and evidence rules"): no credential, raw setting
  value (other than the validated loopback endpoint), absolute/user-derived
  path, raw exception message, prompt/response content, or PII in any DTO,
  plan, ledger, log, or test fixture. Negative marker tests are mandatory in
  every target task.
- Every task is test-driven (RED → GREEN), uses deterministic fakes/fault
  seams (no sleeps, no real network, no real registry outside
  `SystemSetupPlatform`), runs its focused filter and `git diff --check`,
  and commits with `Issue #67: <type>(setup): <summary>` (T7–T9 use
  `Issues #66-#67:` where the sprint plan says so).
- Do not push, tag, or open/update a PR. Local commits only.
- File ownership is the sprint plan's matrix, restated per card. A finding
  in a frozen T3 file reopens the T3 owner; target tasks never edit shared
  files.

## Dependency order

```text
task-01 T3a platform observations + common detection
   |
   +----------------------+
   v                      v
task-02 T3b managed    task-03 T3c endpoint
        policy                 probe          (may run in parallel)
   +----------------------+
   |
   v
task-04 T3d aggregate adapter + partition seam (freeze)
   |
   +--------------+---------------+
   v              v               v
task-05 T4     task-06 T5      task-07 T6     (may run in parallel)
VS Code        Copilot CLI     App/SDK
   +--------------+---------------+
   |
   v
task-08 T7 composition root + real #66->#67 integration
   |
   v
task-09 T8 packaging, wrapper parity, operator docs
   |
   v
task-10 T9 final reviews, full validation, ledger closeout
```

Task-02/03 and task-05/06/07 are the only permitted parallel groups; their
production/test files are disjoint. When run in parallel, each still gets its
own independent review, and workers do not revert concurrent edits.

## Delegation protocol (per task)

For every Codex run state explicitly: objective, owned scope, non-scope,
constraints, completion criteria, validation commands, and report
destination — all in the task card. Use a different, fresh read-only Codex
run for each independent review. Do not accept "DONE" or pass counts alone;
map each requirement to the exact executable test and inspect the diff and
command output.

## Validation commands

Each card names its focused filter. After each coherent slice (tasks 04, 08,
09 at minimum):

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
```

Repository closeout (task-10 only; never substitute commands):

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

## Ledger update policy

Same as the #66 plan: after each task's independent review passes, append its
evidence row to
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md` in a
documentation-only commit. Task-10 (T9) performs the final sprint-wide
closeout update.

## Stop conditions

- No target task (05–07) may declare Issue #67 behavior complete; only
  task-08's real-producer integration gate proves the cross-surface contract.
  A fake adapter cannot satisfy that gate.
- A finding reopens the owning task and invalidates downstream gates; two
  consecutive fix cycles in one area trigger a contract/test-design re-audit
  before more code.
- If required behavior contradicts the spec, stop and update the spec first.
- Windows-host limitation: macOS/Linux platform branches are covered by
  deterministic fakes and static review; record un-executed OS branches as
  unverified scope in every report, never as verified.
- Do not claim first-trace/telemetry receipt anywhere: static setup evidence
  only (`run_first_trace_doctor` is a handoff to Issue #69).
