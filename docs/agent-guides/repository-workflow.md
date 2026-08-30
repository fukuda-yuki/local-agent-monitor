# Repository Workflow Guidance

This guide owns the detailed workflow for coding agents in this repository. It is repository guidance, not product behavior.

Keep feature-specific contracts and smoke procedures with the owning specification, script README, or test project. Keep work tracking and implementation history in GitHub Issues, Pull Requests, and Git history. Do not copy them into this general guide.

## Working Order

Start with the smallest context that can establish the affected contract.

Always inspect:

1. The user's latest request.
2. The target code or document and its nearest tests or checks.

For implementation, fix, or contract work, also inspect:

- the active work item when one is identified, including its current body and newest accepted Product Owner decision;
- the narrowest current specification that owns the affected behavior; when current sources conflict, retain the precedence in `AGENTS.md`:
  - `docs/requirements.md` for product-wide requirements;
  - `docs/spec.md` for cross-cutting product contracts;
  - the relevant file under `docs/specifications/` for an interface, layer, or bounded contract.

Inspect only when the task affects them:

- `docs/architecture.md` or `docs/decisions.md` for architecture, policy, security, or data-boundary changes;
- user guides for user-workflow changes.

Do not read all canonical documents by default.
If task authority and a current specification disagree, report the conflict before editing. When the user's latest explicit decision resolves the conflict, update the owning specification before or together with the implementation. Otherwise stop for a product decision.

## Autonomy And Confirmation

For review, diagnosis, planning, or explanation requests, inspect and report without editing unless the user also requests changes.

For implementation or fix requests, make the requested in-scope local changes and run relevant non-destructive validation without asking again.

Stop before proceeding only when the task would:

- perform a remote write that the user has not explicitly authorized for the exact target;
- make a destructive or irreversible change;
- add or update a runtime or development dependency or lockfile without explicit authority;
- materially expand the requested scope;
- require an unresolved product or specification decision.

Minor, reversible, in-scope local edits do not require confirmation.

## Simplicity And Change Scope

Implement the smallest coherent change that satisfies the current request and contract.

- Do not add features beyond the request.
- Do not create abstractions for one use.
- Do not add flexibility, configurability, alternate workflows, or impossible-scenario handling without a current requirement.
- Do not refactor, reformat, or clean up adjacent code outside the task.
- Match existing local style and patterns.
- Remove only imports, variables, functions, documentation, or tests made obsolete by the current change.
- Mention unrelated defects or dead code instead of fixing them without authority.
- Every changed line must trace to the request, the affected contract, or required validation.

If a change is materially larger than the problem, reduce it before continuing.

## Goal-Driven Execution

Define observable completion criteria before editing. For a multi-step task, use a brief plan that pairs each step with its verification.
Do not broaden ambiguous wording into speculative product behavior. Resolve scope from the active work item and current specification; if they cannot resolve it, state the minimum explicit assumption or stop for the missing decision.

### Pre-implementation scope contract

For a non-trivial task, establish this short scope contract before editing:

```text
Goal
Non-goals
Acceptance criteria
Intended change surface
What remains untouched
Verification for each changed contract
```

Use the active work item or the conversation for task-specific design, planning, status, and review. Update a current owning specification only when the durable product contract changes. Do not create task-specific design, plan, review, closeout, or status Markdown files in the repository.

### New-test gate

Add a test only when all three questions have concrete answers:

1. Which changed acceptance criterion or observable contract does it verify?
2. Why would the existing tests not detect that regression?
3. What is the smallest test case that detects it?

Do not add a test mechanically for each new method or class, backfill coverage for an unchanged module, expand into future cases or a snapshot/parameter matrix, create a production abstraction only to enable a test, or add an unrelated E2E, OS, live, or long-running matrix.

### Scope-expansion stop conditions

Return to the smallest design or replan before continuing when:

- an unplanned production file or subsystem must change;
- a new framework, dependency, config layer, or public interface becomes necessary;
- a compatibility shim, fallback, old/new dual path, or second implementation becomes necessary;
- tests expand beyond the changed acceptance criteria into a suite or E2E matrix; or
- surrounding cleanup, abstraction, documentation, or test scaffolding becomes larger than the implementation.

### Artifacts, delegation, and worktrees

- Do not create repository design drafts, implementation plans, review reports, closeout records, status ledgers, or generated agent reports for any task. A Skill's file-output convention does not override this rule.
- Use a Sub-agent only when the user or active work item explicitly requests delegation and an independent workstream exists. Do not dispatch Sub-agents by repository default.
- When delegation is requested, give each Sub-agent an independent scope, file ownership, and success criteria. The primary agent remains responsible for shared-file coordination, integration, validation, and final decisions.
- Do not claim delegation when the active surface does not provide it.
- Create a worktree only when the active work item, concurrent work, or branch isolation requires it.
- Generic procedure in an executable plan does not override `AGENTS.md` or this guide.

### Single ownership of responsibilities

- A repository-wide rule belongs in exactly one place: `AGENTS.md` or the narrowest applicable guide under `docs/agent-guides/`.
- Product behavior belongs in the current specification.
- Feature-specific operation belongs in the relevant Skill, script README, or owning specification.
- Do not copy the same normative rule into a Skill, Sub-agent, Hook, or plan document.

## Impact-Based Validation

Validation must match the changed surface. Start narrow and expand only when the affected contract or acceptance criteria require it.

### 1. Local iteration and completion report — Affected

During implementation and before the ordinary coding-agent completion report,
run the nearest existing test or component-owned check that directly exercises
the changed behavior or contract. A bounded task can complete with this
Affected validation.

```powershell
dotnet test <test-project.csproj> --filter FullyQualifiedName~<test-or-class>
```

Select the Affected boundary from the change:

- documentation-only changes: inspect the rendered Markdown or diff, paths, and references; no build is required unless the documentation change is generated or executable;
- local implementation changes: build or test the affected project and run nearby regression tests;
- shared libraries, public interfaces, schemas, storage, serialization, or cross-process contracts: run all affected project and contract tests;
- Razor Pages or browser behavior: run the affected browser-facing tests and install Playwright when those tests require it.

Do not start the Completion or Nightly runner locally unless the user or active
work item explicitly requires that lane. Direct changes to an E2E, OS-specific,
or long-running test make that specific test Affected; they do not make the
whole Nightly lane Affected.

### 2. Pull Request / main integration — Completion CI

GitHub Actions owns Completion validation for Pull Requests and pushes to
`main`. The single runner executes all unclassified portable deterministic Fast
tests and the fixed Critical smoke set without running the smoke tests twice.

```powershell
pwsh scripts\test\run-validation.ps1 -Lane Completion
```

Completion CI is the ordinary integration gate. Do not require the same full
gate locally before push unless the user or active work item says to do so.

### 3. Scheduled deep validation — Nightly

Scheduled GitHub Actions owns Nightly validation on Windows and Linux at 03:00
JST. It runs all schedulable automated tests while excluding operator-only live
validation.

```powershell
pwsh scripts\test\run-validation.ps1 -Lane Nightly -Partition Windows
pwsh scripts\test\run-validation.ps1 -Lane Nightly -Partition Linux
```

Nightly does not run from Pull Requests or ordinary coding-agent completion,
and it does not retry automatically. Operator-only live validation keeps its
existing runbook and separate authorization.

### 4. Failure diagnosis

When Completion CI or Nightly fails, narrow diagnosis to the failed project,
class, or test and reproduce only that Affected boundary. Do not automatically
restart the entire Completion or Nightly lane during the same task.

Release tags, manual releases, and cross-surface release candidates keep the
validation required by their owning specification and existing release
workflow; these development lanes do not replace those gates.

Use component-owned specifications, scripts, READMEs, and test projects for feature-specific validation. This guide intentionally contains no feature- or Issue-specific smoke procedure.

Keep automated tests deterministic and isolate external services, network access, and live machine state. Do not use network-dependent validation as the only proof of correctness. Follow the fixture and data-safety rules in `AGENTS.md`.

If a required command fails, is skipped, or cannot run:

- do not treat a different command as equivalent success;
- use diagnostic commands only as additional evidence, not substitution;
- report the exact failed or unavailable command, the result, and the remaining unverified scope.

## Blockers, Fallbacks, And Compatibility

Use the path, command, schema, source, tool, and validation procedure specified by the request or current source of truth.
If it is unavailable, name the exact blocker instead of silently switching routes.

Do not add fallback behavior, compatibility shims, dual paths, migration layers, alternate parsers, permissive parsing, default fallbacks, or silent retry paths unless the current contract or the user's explicit instruction requires them.

An existing documented public interface remains binding until the owning contract changes. When compatibility is required, keep it narrow and identify the exact interface being preserved. When an unreleased contract changes and no current specification requires retained data or behavior, update the single current path instead of adding migration or old/new modes.

## Project Document Updates

Update only the document that owns the changed information.

- Update the relevant current specification when product behavior or a public contract changes.
- Update `docs/requirements.md` or `docs/spec.md` only when their broader contract actually changes.
- Update user-facing guides only when their explanation or workflow becomes incorrect.
- Put bugs, unresolved work, future implementation, roadmaps, and durable implementation plans in GitHub Issues or Projects.
- Put review findings and validation closeout in Pull Request reviews, GitHub Issue comments, or the final response.
- Do not duplicate the same normative rule across requirements, specifications, work tracking, and handoff material.

If a required authoritative update cannot be made, do not claim the work complete. State the missing decision or authority.

## Git Rules

Create local commits in small, coherent steps after validation and review are complete. Do not wait for another request when a completed, verified step can be committed cleanly.

Remote writes require explicit user authorization for the exact action and target. Without that authority, do not push or tag, create or update pull requests, merge, or move remote refs. Never rewrite remote history unless the user explicitly requests that exact destructive action.

Commit messages must start with the active work item name and then follow Conventional Commits.
For `feat`, `fix`, `refactor`, and `perf`, the body must record why the change was needed; see `docs/agent-guides/information-placement.md`.
