# Sprint History Guidance

This guide explains how agents should use `docs/sprints/`.
It is repository guidance, not product behavior.

## Role Of Sprint Material

`docs/sprints/` is historical planning and evidence.
Use it to understand how a decision was reached, what was already validated, or why a follow-up exists.
Do not treat sprint-local material as current product behavior unless the behavior is also present in one of the current sources of truth:

1. `docs/requirements.md`
2. `docs/spec.md`
3. the relevant file under `docs/specifications/`
4. `docs/architecture.md` or `docs/decisions.md`
5. `docs/task.md`

## When To Read Sprint Material

Read sprint material only when it is needed for the task:

- a current source of truth points to a sprint note for evidence;
- the task asks for historical context;
- a conflict needs the original rationale;
- a review or closeout note may contain validation evidence for a completed work item.

Do not search all sprint files by default.
Start from `docs/task.md`, the relevant sprint README, or the known milestone path.

## Promotion Rule

If a sprint note contains behavior that should be current product behavior, promote it before relying on it:

- update `docs/requirements.md`, `docs/spec.md`, or the relevant `docs/specifications/` file;
- update user-facing guides when the user workflow changes;
- update tests when behavior or public interfaces change.

Do not hide new product specifications only in sprint-local notes, knowledge files, review files, or handoff records.

## Evidence Handling

Sprint evidence can support a report, but it does not replace current validation.
If the current task changes code, project files, CLI behavior, or workflow, run the current repository validation commands unless they are explicitly out of scope or blocked.

When summarizing sprint evidence, cite the sprint path and distinguish:

- confirmed current behavior;
- historical behavior;
- unresolved follow-up;
- stale or superseded decision.
