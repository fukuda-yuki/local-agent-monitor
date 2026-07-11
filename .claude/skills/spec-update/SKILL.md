---
name: spec-update
description: Use when a change alters product behavior, a public interface (CLI flags, HTTP routes/status codes, config names, schemas, thresholds/units), or security policy, or when the implementation disagrees with docs/requirements.md, docs/spec.md, or docs/specifications/
---

# Spec-First Document Update

Update the source-of-truth documents before (or together with) the code
change. "Spec first, implementation second" is this repository's core
discipline; user-facing docs must never be the only place a behavior is
defined.

## Read order (before editing anything)

1. `docs/requirements.md`
2. `docs/spec.md`
3. The relevant `docs/specifications/` file
4. `docs/architecture.md` / `docs/decisions.md` when architecture or policy may be affected
5. The target file

## Decide which layers to update

| Change | Update |
|---|---|
| What the product must do | `docs/requirements.md` |
| System-level behavior | `docs/spec.md` |
| Detailed contract (routes, flags, config keys, schemas, thresholds/units, status mapping) | relevant `docs/specifications/` file |
| User workflow | `README.md` / `docs/user-guide*` (derived — never the only definition) |
| Roadmap / historical status | `docs/task.md` |

## Rules

- Conflict between `docs/requirements.md` and the implementation: state
  the conflict before editing. If the intended behavior is clear, update
  the specification first; otherwise ask the user.
- Product behavior, public interface, and security policy changes require
  asking the user before proceeding (AGENTS.md confirmation policy).
- Grep for the concrete identifiers (route, flag, config key, threshold
  value) to find every spec statement that must change together.
- Do not leave the new behavior only in `docs/sprints/`, review notes, or
  handoff records — promote it (see `docs/agent-guides/sprint-history.md`).

## Self-check before finishing

- Every behavior/interface change in the diff has a covering statement in
  requirements/spec/specifications.
- Reverse direction: anything newly described in `README.md` or user
  guides also exists in the requirements/spec layer.
- When the user asks for a subagent review, the `spec-consistency-reviewer`
  agent verifies exactly these points.
