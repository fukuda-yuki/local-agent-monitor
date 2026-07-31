# Repository Workflow Guidance

This guide owns the detailed workflow for coding agents in this repository. It is repository guidance, not product behavior.

Keep feature-specific contracts, smoke procedures, and Issue history with the owning specification, script README, test project, or sprint record. Do not copy them into this general guide.

## Working Order

Start with the smallest context that can establish the affected contract.

Always inspect:

1. The user's latest request and the active work item, including its current body and newest accepted Product Owner decision.
2. The narrowest current specification that owns the affected behavior; when current sources conflict, retain the precedence in `AGENTS.md`:
   - `docs/requirements.md` for product-wide requirements;
   - `docs/spec.md` for cross-cutting product contracts;
   - the relevant file under `docs/specifications/` for an interface, layer, or bounded contract.
3. The target code and its nearest tests.

Inspect only when the task affects them:

- `docs/architecture.md` or `docs/decisions.md` for architecture, policy, security, or data-boundary changes;
- `docs/task.md` for roadmap or status changes;
- user guides for user-workflow changes;
- sprint material for historical rationale or evidence, following `docs/agent-guides/sprint-history.md`.

Do not read all canonical documents or all sprint material by default.
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

## Impact-Based Validation

Validation must match the changed surface. Start narrow and expand only when the affected contract or acceptance criteria require it.

### 1. During iteration

Run the nearest targeted test or check for the behavior being changed.

```powershell
dotnet test <test-project.csproj> --filter FullyQualifiedName~<test-or-class>
```

### 2. Before completion

Validate the affected project or contract boundary:

- documentation-only changes: inspect the rendered Markdown or diff, paths, and references; no build is required unless the documentation change is generated or executable;
- local implementation changes: build or test the affected project and run nearby regression tests;
- shared libraries, public interfaces, schemas, storage, serialization, or cross-process contracts: run all affected project and contract tests;
- Razor Pages or browser behavior: run the affected browser-facing tests and install Playwright when those tests require it.

### 3. Full integration validation

Run the full solution validation only when the change is broad or integration-sensitive, the active acceptance criteria require it, or the work is being integrated, released, or closed as a cross-cutting item.

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

The Playwright wrapper sets `PLAYWRIGHT_BROWSERS_PATH` to `artifacts\playwright-browsers` when unset. On Linux CI, pass `-WithDeps`.
Do not install Playwright or run the full solution by default for an unrelated documentation or bounded implementation change.

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
- Update `docs/task.md` only for roadmap or status changes.
- Keep sprint notes historical; do not introduce current product behavior only there.
- Do not duplicate the same normative rule across requirements, specifications, task records, sprint notes, and handoff files.

If a required authoritative update cannot be made, do not claim the work complete. State the missing decision or authority.

## Subagent Requests

Use subagents only when the user explicitly requests delegation and the active surface supports it.
When used, delegate only independent workstreams with explicit scope, file ownership, and success criteria.
The primary agent remains responsible for shared-file coordination, integration, validation, and final decisions.
Do not claim delegation when the active surface does not provide it.

## Git Rules

Create local commits in small, coherent steps after validation and review are complete. Do not wait for another request when a completed, verified step can be committed cleanly.

Remote writes require explicit user authorization for the exact action and target. Without that authority, do not push or tag, create or update pull requests, merge, or move remote refs. Never rewrite remote history unless the user explicitly requests that exact destructive action.

Commit messages must start with the active work item name and then follow Conventional Commits.
For `feat`, `fix`, `refactor`, and `perf`, the body must record why the change was needed; see `docs/agent-guides/information-placement.md`.
