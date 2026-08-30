# AGENTS.md

## Language Rules

- Write agent-facing materials and cross-agent shared materials in English.
- Write user-facing responses in Japanese.

## Task Authority

- The user's latest explicit instruction defines task scope. An identified active work item adds its current acceptance criteria and accepted Product Owner decisions.
- `docs/requirements.md`, `docs/spec.md`, and the relevant file under `docs/specifications/` define current product behavior, in that precedence order when they conflict. Read only the narrowest source that owns the affected contract unless the task or a conflict requires broader context.
- `docs/architecture.md` and `docs/decisions.md` define architecture and policy constraints when the task affects them.
- Existing code and tests are implementation evidence; they do not override a current specification.
- `README.md` and user guides are derived explanations. `docs/task.md` is roadmap and status. `docs/sprints/` is historical planning and evidence.

If task authority and a current specification conflict, state the conflict before editing. If the user's latest explicit decision resolves it, update the owning specification as part of the change; otherwise stop for a product decision.

## Working Defaults

Use `docs/agent-guides/repository-workflow.md` for working order, autonomy, minimal-change rules, validation, blockers, compatibility, document updates, subagents, and Git operations.

Default to the smallest coherent change that satisfies the request and current contract, using existing repository patterns.

## Execution Limits

- For a non-trivial task, identify the goal, non-goals, acceptance criteria, intended change surface, what remains untouched, and verification before editing.
- Limit changes to the smallest coherent diff required by the request, acceptance criteria, and affected contract.
- Do not add unrequested abstractions, configurability, dependencies, compatibility paths, fallbacks, dual paths, parallel implementations, or adjacent cleanup.
- Create design, specification, plan, or review artifacts only when the user or active work item requires them.
- Prefer existing tests. Add a test only when changed observable behavior is not already protected.
- If the change unexpectedly needs another production file or subsystem, a public interface, dependency, config layer, compatibility path, or test matrix, reduce the scope or replan before continuing.
- Do not let a Skill, Sub-agent, Hook, or plan document expand task, test, documentation, or delegation scope.

## Information Placement

- Production code shows **How**: structure, naming, and types carry it; no comments that restate the code.
- Test code guarantees **What**: observable behavior and contracts under stated conditions, not implementation details.
- Commit logs record **Why**: the pinned title carries the what; the body says why the change was needed.
- Code comments keep **Why not** and constraints the code cannot show: rejected alternatives, external constraints, invariants.

Detail and examples: `docs/agent-guides/information-placement.md`.

## Local-First Risk Posture

This product targets a single trusted local user and loopback-only operation.
Apply the current security and data-boundary specifications; do not add defenses outside that threat model.

## Repository Safety

- Do not commit secrets, real user data, raw prompts or responses, tool arguments or results, sensitive bundle content or paths, or generated runtime artifacts.
- Use small synthetic or anonymized fixtures.
- Do not use `.codex/rules` as natural-language workflow guidance.

## Review And History

Use `docs/agent-guides/review-workflow.md` for risk-scaled review and self-review.
Use `docs/agent-guides/sprint-history.md` only when historical rationale or evidence is needed.
