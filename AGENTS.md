# AGENTS.md

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## Language Rules

- Write agent-facing materials and cross-agent shared materials in English.
- Write user-facing responses in Japanese.
- Keep product deliverables in the language and tone already used by the target document unless the user asks otherwise.

## Source of Truth

`AGENTS.md` defines agent workflow. It is not the place to define product behavior.

When instructions conflict, use this order:

1. The user's latest explicit instruction.
2. `docs/requirements.md`.
3. `docs/spec.md`.
4. The active milestone task in `docs/milestones/<milestone-slug>/task.md`.
5. `docs/task.md`.
6. `README.md` and existing implementation.

If `docs/requirements.md` and `docs/spec.md` conflict, do not choose silently. If intent is clear, update `docs/spec.md` before implementation; otherwise ask the user.

Do not infer product requirements from `README.md` or existing implementation unless they are reflected in `docs/spec.md`.

## Working Order

Before changing code, repository guidance, or project documents, inspect context in this order:

1. `docs/requirements.md` and `docs/spec.md` for requirements and implementation policy.
2. `docs/task.md` to identify the active milestone.
3. The active milestone files under `docs/milestones/<milestone-slug>/`.
4. The target file to understand current structure, style, and local conventions.
5. `docs/knowledge/` only when shared prior findings or decisions may matter.

For Phase 1 local Langfuse PoC work, always check `docs/requirements.md`, `docs/spec.md`, `docs/task.md`, and the relevant milestone task before deciding implementation details.
For Aspire AppHost usage decisions, refer to `docs/spec.md` § 9 (Aspire AppHost の役割と使い分け).

If these sources disagree, state the conflict before editing.

## Confirmation Policy

Ask before proceeding when the task would:

- make an irreversible change,
- change product behavior, public interfaces, input/output formats, or security policy,
- add runtime or development dependencies,
- conflict with `docs/requirements.md` or `docs/spec.md`,
- require a product/spec decision missing from `docs/spec.md`,
- require creating a commit or review note when the milestone is unclear.

Do not stop unnecessarily for minor, reversible, local edits.

## Think Before Coding

**Do not assume. Do not hide confusion. Surface tradeoffs.**

Before implementing:

- State assumptions explicitly when they affect the work.
- If multiple interpretations exist, present them instead of choosing silently.
- If a simpler approach exists, say so.
- Push back when the request is risky, unclear, or inconsistent with the project source of truth.

## Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No flexibility, configurability, or new workflow unless requested or required by `docs/spec.md`.
- No error handling for impossible scenarios.
- If a change is much larger than the problem, simplify it.

Ask: "Would a senior engineer say this is overcomplicated?" If yes, rewrite.

## Surgical Changes

**Touch only what you must. Clean up only your own mess.**

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

**Define success criteria. Loop until verified.**

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

## Tests and Validation

- Derive test scope from `docs/spec.md`, `docs/task.md`, and the active milestone task.
- Use small, synthetic or anonymized fixtures.
- Do not commit secrets, real user data, confidential data, or generated runtime artifacts.
- Check the changed behavior plus nearby edge cases and regression risks.
- Keep automated tests deterministic; isolate external services, network, local machine state, and live services.
- If behavior cannot be automatically verified, document the live check procedure and required evidence.
- Use commands defined by `docs/spec.md`, project files, or existing repository scripts. Do not invent a mandatory workflow.
- If required tools are missing, report the missing tool and the command that should have been run.

## Dependencies and Environment

- Do not add runtime or development dependencies unless `docs/spec.md` requires it or the user explicitly asks.
- Do not update lockfiles as a side effect when dependency changes are out of scope.
- Do not use network-dependent validation as the only proof of correctness.
- Do not substitute a different workflow if it changes what is being verified.

## Subagent Delegation

When the user asks to split work across reader and writer subagents, use the repository-local Mission Card guidance in `.agents/skills/codex-subagent-dispatch/SKILL.md`.

- Treat the main Codex chat as the coordinator and subagents as bounded workers.
- If the active Codex surface does not auto-discover the repo-local skill, explicitly read and follow `.agents/skills/codex-subagent-dispatch/SKILL.md`.
- Do not delegate vague work. Define the mission, scope, permissions, expected output, and stop condition before assigning work.
- Prefer reader agents before writer agents when implementation scope is unclear.
- The main chat integrates results and makes final decisions.

## Review Workflow

Before declaring implementation complete, review the change at a level proportional to its risk.

Use multiple subagents when available for implementation changes, behavior changes, public interface changes, security-sensitive changes, or broad refactors. A recorded self-review is enough for documentation-only, typo-only, formatting-only, or other minor reversible changes.

Minimum review perspectives:

- Spec compliance and functional correctness.
- Tests, edge cases, and regression risk.
- Maintainability, readability, and extensibility.

For preserved review records, use `docs/milestones/<milestone-slug>/review.md`. If the milestone is unclear, ask before creating the review note.

## Project Document Updates

Before finishing, update project documents when the task requires it:

- Update the active milestone task and, when needed, `docs/task.md`.
- Record milestone-local assumptions, decisions, findings, and verification notes in the active milestone notes.
- Record reusable cross-milestone findings in `docs/knowledge/`.
- Reflect confirmed product specifications in `docs/spec.md`; do not hide specs only in notes or knowledge files.
- If required documentation cannot be updated, do not claim completion. State the blocker and needed confirmation.

## Git Rules

Create local commits in small, coherent steps after validation and review are complete.
Do not wait for an explicit user request when a completed, verified step can be committed cleanly.
If the milestone is unclear or the change mixes unrelated concerns, ask before committing.

Do not:

- push branches or tags,
- create, update, merge, or auto-merge pull requests,
- rewrite remote history.

Commit messages must start with the milestone name and then follow Conventional Commits.

---

**These guidelines are working if:** diffs contain fewer unnecessary changes, implementations need fewer rewrites due to overcomplication, and clarifying questions happen before mistakes rather than after them.
