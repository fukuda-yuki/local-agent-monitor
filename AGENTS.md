# AGENTS.md

## Language Rules

- Write agent-facing materials and cross-agent shared materials in English.
- Write user-facing responses in Japanese.

## Repository Commands

Run these commands from the repository root.

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

The Playwright install command is required because the solution test suite
contains LocalMonitor browser smoke tests. The wrapper sets
`PLAYWRIGHT_BROWSERS_PATH` to `artifacts\playwright-browsers` when unset so
browser cache locks are created inside the writable repository workspace. On
Linux CI, pass `-WithDeps` to the same script.

Targeted test while iterating:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~<test-or-class>
```

Collector example validation:

```powershell
$env:LANGFUSE_AUTH="dummy"
docker compose -f infra\otel-collector\docker-compose.example.yml config
```

For CLI smoke examples and the complete command surface, use `docs/agent-guides/repository-workflow.md` and `docs/specifications/interfaces/config-cli.md`.
Do not add `npm test`, `pytest -v`, or other ecosystem commands unless a matching project manifest or specification is added.

## Source Of Truth

When instructions conflict, use this order:

1. The user's latest explicit instruction.
2. `docs/requirements.md`.
3. `docs/spec.md`.
4. The relevant file under `docs/specifications/`.
5. `docs/architecture.md` and `docs/decisions.md`.
6. `docs/task.md`.
7. `README.md`, user guides, contributor guides, and existing implementation.

`README.md` and `docs/user-guide*` are user-facing explanations derived from the product requirements and specifications.
Do not infer product behavior from them unless it is also reflected in `docs/requirements.md`, `docs/spec.md`, or `docs/specifications/`.

`docs/sprints/` contains historical planning and evidence.
Use it for context only. Do not treat sprint-local material as current product behavior unless it has been promoted into the current requirements or specifications.
For detailed sprint-history handling, use `docs/agent-guides/sprint-history.md`.

If `docs/requirements.md` and implementation details disagree, state the conflict before editing.
If the intended behavior is clear, update the specification first; otherwise ask the user.

## Working Defaults

Use `docs/agent-guides/repository-workflow.md` for detailed working order, confirmation policy, simplicity, surgical change rules, goal-driven execution, validation, failure policy, document updates, and git rules.

Before changing code, repository guidance, or project documents, inspect `docs/requirements.md`, `docs/spec.md`, the relevant `docs/specifications/` file, architecture/decision/task docs when applicable, and then the target file.
For Aspire AppHost usage decisions, refer to `docs/specifications/layers/telemetry-ingestion.md` and `docs/architecture.md`.

Keep changes minimum, scoped, and traceable to the request.
Do not preserve backward compatibility.
Choose the simplest implementation that fully meets the current requirements.
Prefer established, well-maintained libraries over custom implementations.

## Information Placement

- Production code shows **How**: structure, naming, and types carry it; no comments that restate the code.
- Test code guarantees **What**: observable behavior and contracts under stated conditions, not implementation details.
- Commit logs record **Why**: the pinned title carries the what; the body says why the change was needed.
- Code comments keep **Why not** and constraints the code cannot show: rejected alternatives, external constraints, invariants.

Detail and examples: `docs/agent-guides/information-placement.md`.

## Local-First Risk Posture

This repository's local tools (e.g. the Sprint8 Local Ingestion Monitor) target a
single trusted local user who accepts same-machine exposure of their own data.
Do not over-engineer.

## Do Not

- Do not change product behavior, public interfaces, or security policy without updating the current specs first.
- Do not add runtime or development dependencies, or update lockfiles, unless specs require it or the user explicitly asks.
- Do not silently switch commands, input sources, schemas, tools, or documentation sources when the specified path fails.
- Do not add fallback behavior, compatibility shims, dual paths, migration layers, or permissive parsing unless current specs require it or the user explicitly asks.
- Do not commit secrets, real user data, raw prompts/responses, tool arguments/results, sensitive bundle content/paths, or generated runtime artifacts.
- Do not substitute a failed, skipped, or unavailable validation command with a different command.
- Do not hide inability: if required context, tools, credentials, or validation are unavailable, say what is blocked and what exact evidence is missing.
- Do not use `.codex/rules` as natural-language workflow guidance.
- Do not delegate to subagents unless the user explicitly asks and the active surface supports it.
- Do not push, tag, create/update/merge pull requests, or rewrite remote history.
- Do not hide product specifications only in sprint notes, review notes, knowledge files, or handoff records.

## Tests And Validation

Derive test scope from `docs/requirements.md`, `docs/spec.md`, and the relevant `docs/specifications/` file.
Use small synthetic or anonymized fixtures.

If a required command fails, is skipped, or cannot run because a tool is missing, do not treat a different command as an equivalent success.

## Fallbacks And Compatibility

Use the path, command, schema, source, tool, and validation procedure specified by the user or the current source of truth.
If it is unavailable, stop and report the blocker instead of silently switching.

## Codex Guidance Files

`AGENTS.md` is the natural-language repository guidance Codex loads automatically.
Keep it short and practical; put detailed procedures in `docs/agent-guides/` and read them when relevant.

`.codex/rules/*.rules` is for command execution policy outside the sandbox, not for natural-language workflow guidance.

## Review Workflow

Use `docs/agent-guides/review-workflow.md` for review depth, self-review expectations, preserved review records, and subagent-independent review practice.

## Project Document Updates

Update `docs/requirements.md`, `docs/spec.md`, and the relevant `docs/specifications/` file when product behavior or public interfaces change.
Update user-facing guides when the user workflow changes.
Update `docs/task.md` when roadmap or historical status changes.

## Git Rules

Create local commits in small, coherent steps after validation and review are complete.
Do not wait for an explicit user request when a completed, verified step can be committed cleanly.

Commit messages must start with the active work item name and then follow Conventional Commits.
For `feat`, `fix`, `refactor`, and `perf` commits, the body must record why the change was needed (see `docs/agent-guides/information-placement.md`).