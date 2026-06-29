# Repository Workflow Guidance

This guide contains detailed repository workflow rules for coding agents.
It is repository guidance, not product behavior.

Keep `AGENTS.md` short and practical. Codex loads `AGENTS.md` automatically, so it should carry the durable instructions needed at the start of every task. Put detailed task-specific procedures here or in nearby agent guides, and read them when the task needs that detail.

## Codex Guidance Surfaces

- `AGENTS.md` is the durable natural-language instruction file for this repository. Nested `AGENTS.md` or `AGENTS.override.md` files can add more specific guidance closer to the working directory.
- `.codex/rules/*.rules` is not a natural-language instruction surface. Codex rules control which commands may run outside the sandbox; they are for command policy, not review, workflow, or product guidance.
- Alternate instruction filenames are ignored unless configured through `project_doc_fallback_filenames`.
- If `AGENTS.md` grows too large, keep the main file concise and reference task-specific Markdown files such as this guide, `docs/agent-guides/review-workflow.md`, or architecture notes.
- Add ecosystem commands such as `npm test` or `pytest -v` only when the repository contains the matching project manifest or current specification.
- Prefer commands with concrete flags and paths so agents can run the right checks without guessing.

These rules follow the official Codex distinction between `AGENTS.md` guidance, configuration, and command execution rules.
They also reflect common agent-maintenance practice: keep the always-loaded instruction file short, put commands early, include flags and paths, and move rarely used detail to referenced guides.

## Do Not Rule Shape

Keep prohibitions short, concrete, and testable.
Prefer "Do not commit secrets" over broad statements like "Be careful with security."
If a prohibition needs nuance, put the short rule in `AGENTS.md` and keep the nuance in this guide or the relevant specification.

## Repository Commands

Run these commands from the repository root.

Standard validation for code, project file, CLI behavior, or workflow changes:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

The Playwright install runs after build because `playwright.ps1` is generated
under the LocalMonitor test output directory. Linux CI uses the same script with
`install --with-deps chromium`.

Targeted test example while iterating:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~<test-or-class>
```

Collector example validation:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

Representative CLI smoke checks may use synthetic fixtures, for example:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-dashboard-dataset tmp\dashboard-demo\measurements.json --raw tests\CopilotAgentObservability.ConfigCli.Tests\TestData\raw-otlp.synthetic.json --json tmp\dashboard-demo\dashboard.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-static-dashboard tmp\dashboard-demo\dashboard.json --out-dir tmp\dashboard-demo\site
```

For the complete Config CLI surface, use `docs/specifications/interfaces/config-cli.md` and the user guides.

## Working Order

Before changing code, repository guidance, or project documents, inspect context in this order:

1. `docs/requirements.md`.
2. `docs/spec.md`.
3. The relevant `docs/specifications/` file.
4. `docs/architecture.md` and `docs/decisions.md` when architecture or policy may be affected.
5. `docs/task.md` for roadmap and historical status.
6. The target file to understand current structure, style, and local conventions.
7. Historical sprint material only when a prior decision or evidence trail is needed.

For Aspire AppHost usage decisions, refer to `docs/specifications/layers/telemetry-ingestion.md` and `docs/architecture.md`.

## Confirmation Policy

Ask before proceeding when the task would:

- make an irreversible change;
- change product behavior, public interfaces, input/output formats, or security policy;
- add runtime or development dependencies;
- conflict with `docs/requirements.md`, `docs/spec.md`, or `docs/specifications/`;
- require a product/spec decision missing from the current specifications;
- require creating a preserved review note when the active work item is unclear.

Do not stop unnecessarily for minor, reversible, local edits.

## Think Before Coding

Do not assume. Do not hide confusion. Surface tradeoffs.

Before implementing:

- State assumptions explicitly when they affect the work.
- If multiple interpretations exist, present them instead of choosing silently.
- If a simpler approach exists, say so.
- Push back when the request is risky, unclear, or inconsistent with the source of truth.

## Simplicity First

Minimum code that solves the problem. Nothing speculative.

- No features beyond what was asked.
- No abstractions for single-use code.
- No flexibility, configurability, or new workflow unless requested or required by the current specifications.
- No error handling for impossible scenarios.
- If a change is much larger than the problem, simplify it.

Ask: "Would a senior engineer say this is overcomplicated?" If yes, rewrite.

## Surgical Changes

Touch only what you must. Clean up only your own mess.

When editing:

- Do not improve adjacent code, comments, formatting, or structure outside the task.
- Do not refactor things that are not broken.
- Match existing style, even if you would choose a different one.
- If you notice unrelated dead code, mention it; do not delete it unless asked.

When your changes create orphans:

- Remove imports, variables, functions, docs, or tests made unused by your change.
- Do not remove pre-existing dead code unless asked.

Every changed line should trace directly to the user's request.

## Goal-Driven Execution

Define success criteria. Loop until verified.

Transform tasks into verifiable goals:

- "Add validation" -> "Write or identify checks for invalid inputs, then make them pass."
- "Fix the bug" -> "Reproduce the bug, then verify the fix."
- "Refactor X" -> "Confirm behavior before and after using the relevant tests or checks."

For multi-step tasks, state a brief plan when useful:

```text
1. [Step] -> verify: [check]
2. [Step] -> verify: [check]
3. [Step] -> verify: [check]
```

Weak success criteria such as "make it work" require clarification or an explicit assumption.

## Tests And Validation

- Derive test scope from `docs/requirements.md`, `docs/spec.md`, and the relevant `docs/specifications/` file.
- Use small, synthetic or anonymized fixtures.
- Do not commit secrets, real user data, confidential data, or generated runtime artifacts.
- Check the changed behavior plus nearby edge cases and regression risks.
- Keep automated tests deterministic; isolate external services, network, local machine state, and live services.
- If behavior cannot be automatically verified, document the live check procedure and required evidence.
- Use commands defined by the current specifications, project files, or existing repository scripts.
- If required tools are missing, report the missing tool and the command that should have been run.
- Use the command set in this guide early when planning, iterating, and reporting validation.

## Failure And Non-Substitution Policy

- If a required command fails, is skipped, or cannot run because a tool is missing, do not treat a different command as an equivalent success.
- Diagnostic commands may be useful follow-up evidence, but they do not replace the required validation command.
- Do not substitute a different workflow when it changes what is being verified.
- In the final report, state the commands run, their result, any unverified scope, and the exact command still needed.

## Fallbacks, Blockers, And Compatibility

Use the path, command, schema, source, tool, and validation procedure specified by the user or the current source of truth.
If the specified route is unavailable, stop and report the blocker instead of silently switching to a fallback.
Name the missing condition precisely: missing tool, missing credential, unavailable service, failing command, unclear product decision, or absent spec.

Do not add compatibility shims, dual behavior, migration layers, alternate parsers, permissive parsing, default fallbacks, or silent retry paths unless one of these is true:

- the current source of truth requires compatibility;
- the user explicitly asks for compatibility behavior;
- the change is needed to preserve an existing documented public interface.

When compatibility is required, keep it narrow and document the exact interface being preserved.
When compatibility is not required, prefer one clear behavior and fail loudly on unsupported inputs.

## Dependencies And Environment

- Do not add runtime or development dependencies unless the current specifications require it or the user explicitly asks.
- Do not update lockfiles as a side effect when dependency changes are out of scope.
- Do not use network-dependent validation as the only proof of correctness.

## Project Document Updates

Before finishing, update project documents when the task requires it:

- Update `docs/requirements.md`, `docs/spec.md`, and the relevant `docs/specifications/` file when product behavior or public interfaces change.
- Update user-facing guides when the user workflow changes.
- Update `docs/task.md` when roadmap or historical status changes.
- Record reusable findings in the relevant specification or shared docs location.
- Do not hide product specifications only in sprint notes or knowledge files.
- If required documentation cannot be updated, do not claim completion. State the blocker and needed confirmation.

## Subagent Requests

Codex cannot assume autonomous access to subagents in every surface.
Use subagents only when the user explicitly asks for subagent delegation and the active surface provides that capability.

- When subagents are available, use the repository-local Mission Card guidance in `.agents/skills/codex-subagent-dispatch/SKILL.md`.
- Do not pretend that delegation happened when no subagent capability is available.
- If subagents are unavailable, continue in the main chat or provide a mission card the user can run elsewhere.
- The main chat remains responsible for integration, validation, and final decisions.

## Git Rules

Create local commits in small, coherent steps after validation and review are complete.
Do not wait for an explicit user request when a completed, verified step can be committed cleanly.
If the active work item is unclear, or the change mixes unrelated concerns, ask before committing.

Do not:

- push branches or tags;
- create, update, merge, or auto-merge pull requests;
- rewrite remote history.

Commit messages must start with the active work item name and then follow Conventional Commits.
