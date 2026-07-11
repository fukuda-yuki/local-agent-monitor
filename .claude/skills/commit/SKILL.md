---
name: commit
description: Create a local commit following the repository convention (work item name prefix + Conventional Commits), after validation and review are complete
disable-model-invocation: true
---

# Repository Commit

Create a local commit following the git rules in `AGENTS.md`.

## Message format

```
<work item>: <type>(<optional scope>): <subject>
```

Real examples from this repository's history:

```
Sprint16 Canvas cross-repo adapter: docs: plan milestones
Sprint15: feat(canvas): add prompt-aware trace selection (M7, D039)
Sprint15: docs: add M7 live validation evidence
```

- `<work item>` is the active work item name (usually the sprint). If the
  active work item is unclear, ask the user before committing.
- `<type>` follows Conventional Commits (`feat`, `fix`, `docs`, `test`,
  `refactor`, `chore`, ...).

## Body: record the Why

The title carries the searchable what; the body records why the change
was needed and what was wrong before — the one thing `git diff` cannot
reconstruct.

- `feat`, `fix`, `refactor`, `perf`: body required.
- `docs`, `chore`, `test`, `style`: body optional when the title already
  carries the why.
- Never a work log ("Update IngestionService.cs", "Fix tests").

Example:

```
Sprint8 ingestion hardening: fix(monitor): reset connection state before re-ingest

Reconnect processing could submit the final buffered span twice because
the previous connection state remained active. Reset the state before
starting a new ingestion cycle.
```

Detail: `docs/agent-guides/information-placement.md`.

## Procedure

1. Run `git status` and `git diff` to review exactly what would be committed.
2. Confirm the change is one small, coherent step. If it mixes unrelated
   concerns, ask the user how to split it.
3. Confirm validation and review for this step are complete. If not, say
   what is missing instead of committing.
4. Stage only the files that belong to this step (explicit paths; no
   `git add -A`).
5. Never stage: secrets, real user data, raw prompts/responses, tool
   arguments/results, sensitive bundle content/paths, or generated runtime
   artifacts (`bin/`, `obj/`, `artifacts/` except the committed
   `artifacts/dashboard-input/README.md`).
6. Commit with the title format and body rule above.

## Hard limits

Do not push, tag, create/update/merge pull requests, or rewrite remote
history.
