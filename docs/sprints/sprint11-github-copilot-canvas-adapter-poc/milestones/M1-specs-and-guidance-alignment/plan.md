# Sprint11 M1 - Specs & Guidance Alignment (Plan)

Status: **Implemented** - docs/spec alignment only. No product code, no extension
scaffold, no `.github/extensions/*` files.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` -> `docs/spec.md` -> `docs/specifications/`. Sprint
context: [../../README.md](../../README.md).

## Objective

Promote the Sprint11 Canvas adapter decisions into the canonical docs before any
Canvas extension files are created. M1 fixes the public intent, scope, scaffold
workflow, action contracts, `--sanitized-only` Canvas posture, and raw boundary
so implementation milestones do not invent behavior.

## Scope

In scope:

1. Read `.github/skills/create-canvas/SKILL.md` completely and record that it is
   the controlling `/create-canvas` workflow, not a prompt-file substitute.
2. Add a decision to `docs/decisions.md` for the Sprint11 Canvas adapter:
   project scope, scaffold-first workflow, `extension.mjs`, ES modules,
   `joinSession({ canvases: [createCanvas({...})] })`, loopback-only extension
   servers, `session.log()`, and `onClose()` cleanup.
3. Update `docs/requirements.md` to add the Canvas adapter as an optional
   Local Monitor integration over sanitized monitor context only.
4. Update `docs/spec.md` to describe the Canvas adapter architecture and action
   set without changing telemetry input, schema, raw store, normalized dataset,
   candidate records, or dashboard contracts.
5. Update `docs/specifications/layers/telemetry-ingestion.md` to record that
   Canvas actions consume the existing sanitized `/api/monitor/*` APIs and that
   Canvas display requires `--sanitized-only`.
6. Update `docs/specifications/security-data-boundaries.md` to record Canvas
   action response restrictions, extension-owned loopback server requirements,
   no raw/PII/log/artifact leakage, and scaffold-tool blocker behavior.
7. Update `docs/task.md` with a Sprint11 planned-work pointer.

Out of scope:

- Creating `.github/extensions/otel-monitor-canvas/`.
- Running `extensions_manage`.
- Hand-writing extension skeleton files.
- Adding dependencies, package files, lockfiles, Node modules, or runtime state.
- Changing Local Monitor runtime behavior.

## Acceptance criteria

- Canonical docs mention the Canvas adapter only as a thin sanitized adapter over
  the existing Local Monitor.
- `--sanitized-only` is the documented Sprint11 Canvas-safe monitor posture.
- The `/create-canvas` skill is referenced as a skill, not a prompt file.
- The scaffold-first rule is explicit: if `extensions_manage` is unavailable,
  implementation stops and records a blocker.
- Action contracts are listed and bounded.
- D020/D023/D030 and the sanitized `/api/monitor/*` invariant are preserved.

## Validation

Docs-only validation:

```powershell
git diff -- docs/requirements.md docs/spec.md docs/specifications/layers/telemetry-ingestion.md docs/specifications/security-data-boundaries.md docs/decisions.md docs/task.md docs/sprints/sprint11-github-copilot-canvas-adapter-poc
```

Pass criteria: the diff contains only documentation updates, no extension
scaffold, no product code, and no generated/runtime artifacts.

Repository build/test is not required for M1 unless implementation touches code,
project files, CLI behavior, or workflow.

## Review

Use recorded self-review for this docs-only milestone. Check:

- scope and action contracts match the Sprint11 README;
- no product behavior is specified only in sprint-local docs;
- no raw/PII exposure is introduced;
- no placeholder fallback for unavailable Canvas scaffold tools is hidden in the
  implementation plan.
