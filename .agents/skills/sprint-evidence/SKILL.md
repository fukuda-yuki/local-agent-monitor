---
name: sprint-evidence
description: Scaffold a sprint milestone document (plan, review, handoff prompt, or live-validation evidence) under docs/sprints/ following the established repository format
disable-model-invocation: true
---

# Sprint Evidence / Handoff Document Scaffolding

Create one sprint milestone document following the conventions already used
in `docs/sprints/`. Read `docs/agent-guides/sprint-history.md` first.

## Inputs

If not provided in the arguments, ask the user for:

1. Sprint directory (e.g. `sprint16-canvas-cross-repo-adapter`).
2. Milestone (e.g. `M2-<kebab-slug>`).
3. Document type: `plan.md`, `review.md`, `handoff-prompt.md`,
   `live-validation-prompt.md`, or `live-validation.md`.

## Format reference

Before writing, read at least one recent existing example of the same
document type, e.g. under
`docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M7-prompt-aware-trace-selection/`.
Match its structure and tone; do not invent a new format.

Established conventions:

- Location: `docs/sprints/<sprint-dir>/milestones/<M#-kebab-slug>/<doc>.md`.
- Handoff prompts are self-contained: a short preamble, then `---`, then a
  single fenced block containing everything the fresh session needs
  (repo path, branch/HEAD, ordered reading list starting from AGENTS.md and
  the governing decisions in `docs/decisions.md`, what is already done with
  commit hashes, the task, and the validation commands).
- Review and live-validation documents cite concrete evidence: commit
  hashes, exact commands run, and observed results.
- Update the milestone table in the sprint `README.md` when milestone status
  changes.

## Rules

- Write in English (agent-facing material).
- Never include secrets, real user data, raw prompts/responses, or tool
  arguments/results in committed evidence.
- Promotion rule: if the document describes behavior that should be current
  product behavior, it must also be promoted into `docs/requirements.md`,
  `docs/spec.md`, or the relevant `docs/specifications/` file — do not hide
  product specifications only in sprint notes.
