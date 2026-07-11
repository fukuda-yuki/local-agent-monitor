# Issue #55 Proposal Apply Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Safely apply an explicitly approved Issue #54 proposal to an explicitly configured local root, with selected-hunk review, stale protection, recovery, and guarded rollback.

**Architecture:** `MonitorOptions` owns private startup root configuration. A focused Local Monitor apply subsystem validates paths and owns draft, approval, transaction, recovery, and audit. `SessionRoutes` maps the loopback API. Canvas remains an unprivileged token-gated helper proxy and is the sole local display surface for diff text.

**Tech Stack:** .NET 10, ASP.NET Core minimal routes, Microsoft.Data.Sqlite, xUnit, Node built-in test runner.

## Global Constraints

- No dependency, git invocation, arbitrary-path registration, directory/delete/rename operation, or remote mutation.
- Support only `--apply-root user_config|skill|repository=<absolute-directory>` and no default root.
- Accept only existing non-reparse regular files below the selected root and normalized relative paths.
- Every mutation route uses the existing loopback/Host-header, same-origin, `x-monitor-csrf: local-monitor`, JSON, 1 MiB, and `Cache-Control: no-store` controls.
- Do not emit path, source/diff/replacement/snapshot text, raw Session content, credential, token, or exception detail outside the token-gated helper display.
- Bind approval to its selected-hunk revision and hashes. A stale target changes no target.
- Snapshot + flushed journal + recovery must leave durable state all-applied or all-restored. Rollback is one-time and checks all post-apply hashes.

---

### Task 1: Root policy and immutable draft/apply core

**Files:**

- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorOptions.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Sessions/SessionDomain.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/ProposalApply/ApplyRoot.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/ProposalApply/ApplyPathPolicy.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/ProposalApply/LineDiff.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/ProposalApply/ProposalApplyService.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/ProposalApply/ProposalApplyTransaction.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorOptionsTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/ApplyPathPolicyTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/ProposalApplyServiceTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/ProposalApplyTransactionTests.cs`

**Consumes:** Issue #54 `ISessionStore.GetImprovementProposal` and `MonitorOptions.Parse`.

**Produces:** `ConfiguredApplyRoot`, strict path policy, immutable draft/revision records, deterministic hunk selection and SHA-256 approval digest, persistent snapshots/journal metadata, and a fault-injectable transaction.

- [ ] Write failing root/path and core behavior tests.

```csharp
[Theory]
[InlineData("..\\outside.txt")]
[InlineData("C:\\outside.txt")]
public void Resolve_rejects_non_relative_path(string value) { ... }
[Fact] public async Task Stale_second_target_changes_neither_target() { ... }
[Fact] public async Task Uncommitted_apply_recovers_all_originals() { ... }
```

- [ ] Run `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorOptionsTests|FullyQualifiedName~ApplyPathPolicyTests|FullyQualifiedName~ProposalApplyServiceTests|FullyQualifiedName~ProposalApplyTransactionTests`; expect missing apply types to fail.
- [ ] Implement `ApplyRootKind`, `ConfiguredApplyRoot`, and `ApplyPathPolicy`. Parse repeated roots, reject duplicate/missing/reparse roots, and expose no canonical path. Reject absolute, drive-relative, UNC/device/URI, dot segments, root escape, missing/nonregular/reparse targets.
- [ ] Add only `proposal_apply_*` SQLite tables/migration. Keep original/replacement text in private runtime storage, never proposal/audit rows.
- [ ] Implement stable line-hunk selection and a SHA-256 approval digest over proposal/root/canonical relative paths/base hashes/hunk IDs/selected hashes/revision. Changing selection invalidates approval.
- [ ] Implement all-target preflight, flushed snapshots, prepared journal, staged same-volume replacements, per-replace markers, commit marker, and uncommitted recovery. Rollback preflights every post-apply hash and may restore a committed apply only once.
- [ ] Run the same focused command; expect PASS. Commit the coherent core change as `Issue #55: feat(monitor): add recoverable proposal apply core`.

### Task 2: Local Monitor route boundary and hostile-request tests

**Files:**

- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Sessions/ProposalApplyRequest.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionProposalApplyRouteTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`

**Consumes:** Task 1 configured roots and apply service.

**Produces:** the specified proposal-apply routes with fixed error/no-echo policy.

- [ ] Write failing route tests for draft/select/approve/apply, missing configuration, invalid root/path/IDs, method/content type, cross-site, CSRF, 1 MiB body, no-store on errors, stale selection/digest/apply, stale rollback, and source/path non-echo.
- [ ] Run `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SessionProposalApplyRouteTests|FullyQualifiedName~MonitorHostTests`; expect missing routes to fail.
- [ ] Map `GET roots`, `POST/GET drafts`, `PUT drafts/{draftId}/selection`, `POST drafts/{draftId}/approve`, `POST drafts/{draftId}/apply`, and `POST applies/{applyId}/rollback` under `/api/session-workspace/proposal-applies`. Set no-store before validation, reuse existing cross-site/CSRF/JSON/bounded-body helpers, and use DTO `UnmappedMemberHandling.Disallow`.
- [ ] Return only fixed errors. Do not log/rethrow request content. The full-diff draft response is available only to the helper proxy and never an action DTO.
- [ ] Run the same focused command; expect PASS. Commit the coherent route change as `Issue #55: feat(monitor): expose guarded proposal apply routes`.

### Task 3: Canvas helper confirmation and privacy contract

**Files:**

- Modify: `.github/extensions/otel-monitor-canvas/extension.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-workspace-helpers.mjs`
- Test: `.github/extensions/otel-monitor-canvas/canvas-workspace-helpers.test.mjs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`

**Consumes:** Task 2 route shape.

**Produces:** per-launch-token helper routes and an inert full-diff/hunk selection/approval/apply/rollback UI; no action or `session.send()` expansion.

- [ ] Write failing Node/contract tests for token/no-store/CSRF proxy behavior, selected-hunk request shape, explicit approval before apply, `textContent` diff display, no `innerHTML`, no action registration, and no path/source/diff in action/prompt/log construction.
- [ ] Run `node --test .github\extensions\otel-monitor-canvas\canvas-workspace-helpers.test.mjs` and `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests`; expect failure.
- [ ] Proxy only proposal-apply endpoints after token + loopback checks and inject CSRF server-side. Render kind labels, full diff, file/hunk checkboxes, selected revision/digest confirmation, explicit approval/apply/rollback, stale/failed/recovered states using DOM `textContent`.
- [ ] Run both commands again; expect PASS. Commit the coherent Canvas change as `Issue #55: feat(canvas): confirm local proposal apply`.

### Task 4: Destructive validation, user guide, and closeout

**Files:**

- Modify: `docs/task.md`
- Modify: `docs/user-guide/local-monitor.md`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/ProposalApplyTransactionTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionProposalApplyRouteTests.cs`

**Consumes:** Tasks 1–3.

**Produces:** documented local workflow and proof for every destructive boundary.

- [ ] Add tests proving reparse ancestor rejection before snapshot, any stale file prevents every write, every uncommitted fault boundary recovers all originals, and stale/second rollback writes nothing.
- [ ] Run `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~ProposalApplyTransactionTests|FullyQualifiedName~ProposalApplyServiceTests|FullyQualifiedName~SessionProposalApplyRouteTests|FullyQualifiedName~ApplyPathPolicyTests`; expect PASS.
- [ ] Document placeholder-only `--apply-root` examples, draft/select/approve/apply, stale no-write, recovery, rollback precondition, and absence of git actions.
- [ ] Run `dotnet build CopilotAgentObservability.slnx`; expect 0 warnings and 0 errors.
- [ ] Run `pwsh scripts\test\install-playwright-chromium.ps1`; expect success.
- [ ] Run `dotnet test CopilotAgentObservability.slnx`; expect every test to pass.
- [ ] Commit the verified closeout as `Issue #55: test(monitor): verify safe proposal apply recovery`.

## Review Gates

1. A separate Terra High reviewer audits Task 1: root/path policy, mutable-state boundaries, durability ordering, and test adequacy.
2. A separate Terra High security reviewer audits Tasks 2–3: browser authority, token/CSRF, source/path disclosure, apply/rollback authorization, and Canvas action non-expansion.
3. Important findings are fixed by a Terra Medium worker and re-reviewed by a different Terra High reviewer.
4. A Terra Medium worker runs the exact mandatory build, Playwright bootstrap, and solution test commands. Commit only after every review gate is clear.
