# Repository Workflow Guidance

This guide owns execution details, not product behavior. Use the authority and essential safety boundaries in [AGENTS.md](../../AGENTS.md).

## Working Order

Inspect the current request, target code or document, and nearest relevant checks. For implementation, fixes, or contracts, also read the active work item's current criteria and accepted Product Owner decisions and the narrowest owning specification. Read architecture, decisions, or user guides only when the affected contract requires them. Do not load all canonical documents by default.

## Autonomy And Confirmation

Apply the confirmation boundaries in AGENTS.md. A missing decision blocks only dependent work; continue independent authorized work and report the blocker. Do not ask again for an already resolved decision or routine in-scope implementation, validation, and correction.

## Simplicity And Change Scope

Use the smallest coherent solution and existing local patterns. Do not add speculative features, one-use abstractions, configurability, impossible-scenario handling, or adjacent refactoring/formatting. Remove only code, documentation, or tests made obsolete by this change. Every changed line must serve the request, affected contract, or necessary validation; report unrelated defects rather than silently fixing them.

## Goal-Driven Execution

Judge acceptance by customer value and product quality, not prior investment. Report evidence-backed disagreements and gaps candidly.

### Pre-implementation scope contract

Establish the intended outcome and observable completion criteria before editing. Use a brief plan for multi-step work; add boundaries, affected surfaces, or verification details only when useful. No fixed planning form is required. Resolve ambiguity from task authority and the current contract; state a narrow contract-preserving assumption or identify the unresolved decision.

### New-test gate

Add the smallest regression for a changed observable contract only when existing tests would not catch it. No separate questionnaire is required. Do not add tests per method/class, backfill unchanged modules, grow speculative parameter/snapshot matrices, create production abstractions solely for tests, or add unrelated E2E, OS, live, or long-running coverage.

### Scope and replanning

Scope follows the authorized outcome and current contract, not a predicted file list. When another file, subsystem, or check is necessary for the same outcome, update the plan internally and continue. Remove unnecessary expansion; use the confirmation boundaries for retained proposals that need a decision. Newly discovered necessary work is not an over-implementation finding merely because it was unplanned.

### Artifacts, delegation, and worktrees

Keep task-specific planning, findings, and closeout in the conversation or active Issue/PR, not repository reports. Create a worktree only when the work item, concurrent work, or branch isolation requires it.

This section is the single delegation-policy owner. Codex may choose bounded delegation when an independent workstream is expected to reduce elapsed time or improve quality enough to justify coordination overhead; no separate delegation approval is required. Delegation is optional, not a completion gate. Prefer direct execution when better, and honor current explicit user restrictions.

Give each worker an independent scope, file ownership or read-only review surface, and a concrete expected result. Use only needed workers; avoid competing edits and duplicate work. Briefly state the split and expected benefit without another planning artifact. The primary agent owns assessment of returned findings/diffs, integration, verification, and the final answer.

Delegation grants no wider task, data, dependency, destructive-action, or remote-write authority. Tool/concurrency limits are ceilings, not worker-count targets. Use only available capabilities; when delegation is unavailable, continue directly and do not claim an independent review.

#### Codex reviewer delivery

These are reference procedures, not automatically registered native Codex agents. Select only the relevant one:

- [API contract reviewer](../../.agents/agents/api-contract-reviewer.md): changed specification/producer/consumer wire contracts.
- [Async UI state reviewer](../../.agents/agents/async-ui-state-reviewer.md): changed request/render ordering and focus behavior.
- [Local risk posture reviewer](../../.agents/agents/local-risk-posture-reviewer.md): changed HTTP, logging, artifact, and data boundaries.

Supply the repository root, exact procedure path, bounded review surface/diff, and expected result through the active surface's available agent tool. Require the reviewer to read the procedure; if inaccessible, deliver its relevant text explicitly. Do not assume inherited context or file access. An unread or undelivered procedure leaves the review unverified.

Require no file edits or remote mutations and return findings to the primary agent. Markdown `tools:` metadata serves Claude, not Codex permission enforcement. Inspect effective runtime permissions, including inherited/session overrides, before claiming enforced read-only isolation; observed no-write behavior is a separate fact. Keep canonical procedures and existing Claude copies aligned without new registration or synchronization machinery.

### Single ownership of responsibilities

Keep each shared rule in AGENTS.md or its narrowest existing guide. Product behavior belongs in its owning specification; feature operations belong in their existing Skill, script README, or specification. Reference those owners rather than copying their rules.

## Impact-Based Validation

Start with the nearest existing check that exercises the changed contract. A bounded task can complete locally with **Affected** validation:

- Documentation only: inspect Markdown/diff, paths, and references; no build unless generated or executable content requires it.
- Local implementation: build/test the affected project and nearby regressions.
- Shared libraries, interfaces, schemas, storage, serialization, or cross-process contracts: exercise all affected projects and contract tests.
- Razor/browser behavior: run affected browser-facing tests and install Playwright when those tests require it.

For example:

```powershell
dotnet test <test-project.csproj> --filter FullyQualifiedName~<test-or-class>
```

Reuse passing results while relevant files, inputs, and environment remain unchanged. Review, commit, or reporting alone does not require a rerun. Revalidate for relevant changes, failures, unresolved risks, or explicit requirements; reuse does not waive a required validation lane or live gate.

GitHub Actions owns **Completion CI** as the ordinary PR/main integration gate and **Nightly** as scheduled deep validation. Do not run either lane locally unless the user or active work item explicitly requires it. A directly changed E2E, OS-specific, or long-running test is Affected, not its entire lane. Diagnose failures at the failed project/class/test rather than automatically restarting full lanes. Use the existing [runner](../../scripts/test/run-validation.ps1) and [validation/release matrix](../specifications/validation-release-matrix.md) for lane mechanics and release requirements; these development rules do not replace release or operator-only live gates.

Keep automated tests deterministic and isolate external services, network access, and live machine state. Network-dependent checks must not be the only correctness evidence. Do not assume every existing test or local command is disposable or safe for a real installation.

During an Issue repair, relevant existing local data and the actual Monitor may be used for diagnosis and customer-facing acceptance without asking again merely because the data is real. Preserve originals; use consistent copies for experiments that modify data. This does not authorize destructive source-installation changes or raw-data publication. Keep working copies gitignored, and track a distilled fixture only when its regression value justifies it. Anonymization must preserve the failure-relevant identities/relationships, ordering, types, missing-versus-zero values, and failure states; inspect derived fixtures before sharing.

For repaired user-visible defects, revisit the original failure with the same records where available and trace the relevant data-to-UI path. Distinguish installed-path observations, replay/copy results, and mocked/component checks. A passing mock is not normal-user acceptance; absent UI data is not source absence. Real-data investigation is on demand, not a recurring test suite or separate preparation task.

Report the exact failed, skipped, or unavailable command, its result, and unverified scope. Diagnostic commands add evidence; they do not substitute for a failed or unavailable required check. Put only necessary sanitized evidence in the Issue/PR.

## Blockers, Fallbacks, And Compatibility

Use the specified path, command, schema, source, tool, and validation procedure. Name an unavailable required route rather than silently substituting another. Do not introduce fallback behavior, compatibility shims, dual paths, migrations, alternate/permissive parsers, defaults, or retries without current contract or explicit user authority.

A documented public interface remains binding until its owner changes it. Preserve required compatibility narrowly. For an unreleased contract with no retention/compatibility requirement, update the single current path instead of inventing old/new modes.

## Project Document Updates

Update only the owner of changed information. Change requirements/spec.md only for their broader contracts, the relevant specification for product/public-contract changes, and user guides only when their explanation becomes wrong. Use [information placement](information-placement.md) for artifact ownership; do not duplicate normative rules. If a required authoritative update is blocked, name it and do not claim that work complete.

## Git Rules

Create small coherent local commits after relevant validation and review; no extra request is needed for a clean completed step. Remote writes require explicit user authorization for the exact action and target. Without it, do not push/tag, create/update PRs, merge, or move remote refs. Never rewrite remote history without explicit authority for that destructive action.

Use `<work item>: <type>(<scope>): <subject>` Conventional Commits. The body for `feat`, `fix`, `refactor`, and `perf` explains why; see [information placement](information-placement.md#commit-log-why).
