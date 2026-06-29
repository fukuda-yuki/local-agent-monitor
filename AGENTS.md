# AGENTS.md

These guidelines define how coding agents should work in this repository.
They do not define product behavior.

## Language Rules

- Write agent-facing materials and cross-agent shared materials in English.
- Write user-facing responses in Japanese.
- Keep product deliverables in the language and tone already used by the target document unless the user asks otherwise.

## Repository Commands

Run these commands from the repository root.

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

The Playwright install command is required because the solution test suite
contains LocalMonitor browser smoke tests. On Linux CI, pass `--with-deps` to
the same script.

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

Ask before irreversible changes, product behavior or public interface changes, security policy changes, dependency additions, source-of-truth conflicts, missing spec decisions, or unclear preserved review records.
Keep changes minimum, scoped, and traceable to the request.

## Local-First Risk Posture

This repository's local tools (e.g. the Sprint8 Local Ingestion Monitor) target a
single trusted local user who accepts same-machine exposure of their own data.
Defend the risks that cross the machine boundary — remote / non-loopback access,
other-origin browser-mediated exfiltration, and raw/PII leaking into logs or
repository-committed artifacts — with low-cost controls: loopback bind,
Host-header validation, CORS off, same-origin on the raw-detail route, CSRF on
state-changing actions, no raw/PII in logs or repo.

Do not over-engineer the display side. The monitor exists to show the user their
own captured prompts/outputs, so do not add a heavy anti-XSS / CSP apparatus,
payload sanitizers, or XSS payload-matrix tests for that display — rely on the UI
framework's default output encoding (text, not live markup). The kept baseline is normal correct
rendering: captured content is shown as escaped / inert text (framework default;
no `Html.Raw`), so stored markup does not execute — that is not "over-defense".
The accepted residual is only the absence of defense-in-depth on top of that
escaping. Do not confuse this display de-scope with loosening genuine monitoring
contracts: the readiness contract (default thresholds, units, config names, HTTP
status mapping, machine-readable body) is the monitor's purpose and stays pinned.
Detail: `docs/decisions.md` D020 and
`docs/specifications/security-data-boundaries.md`.

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
Diagnostic commands may be useful follow-up evidence, but they do not replace required validation.
In the final report, state commands run, results, unverified scope, and exact commands still needed.
Do not use network-dependent validation as the only proof of correctness.

## Fallbacks And Compatibility

Use the path, command, schema, source, tool, and validation procedure specified by the user or the current source of truth.
If it is unavailable, stop and report the blocker instead of silently switching.
Preserve compatibility only where `docs/requirements.md`, `docs/spec.md`, or `docs/specifications/` require it, or the user explicitly asks for it.

## Codex Guidance Files

`AGENTS.md` is the natural-language repository guidance Codex loads automatically.
Keep it short and practical; put detailed procedures in `docs/agent-guides/` and read them when relevant.

`.codex/rules/*.rules` is for command execution policy outside the sandbox, not for natural-language workflow guidance.

## Subagent Requests

Use subagents only when the user explicitly asks for subagent delegation and the active surface provides that capability.
When subagents are available, use `.agents/skills/codex-subagent-dispatch/SKILL.md`.
Otherwise continue in the main chat or provide a mission card the user can run elsewhere.

## Review Workflow

Use `docs/agent-guides/review-workflow.md` for review depth, self-review expectations, preserved review records, and subagent-independent review practice.
Documentation-only, typo-only, formatting-only, or other minor reversible changes can use a recorded self-review.

## Project Document Updates

Update `docs/requirements.md`, `docs/spec.md`, and the relevant `docs/specifications/` file when product behavior or public interfaces change.
Update user-facing guides when the user workflow changes.
Update `docs/task.md` when roadmap or historical status changes.

## Git Rules

Create local commits in small, coherent steps after validation and review are complete.
Do not wait for an explicit user request when a completed, verified step can be committed cleanly.
If the active work item is unclear, or the change mixes unrelated concerns, ask before committing.

Commit messages must start with the active work item name and then follow Conventional Commits.

---

These guidelines are working if diffs contain fewer unnecessary changes, implementations need fewer rewrites due to overcomplication, and clarifying questions happen before mistakes rather than after them.
