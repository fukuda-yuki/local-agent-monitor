# Issue #158 Skill invocation snapshot validation evidence

Status: Windows owned-session and Linux native-ext4 platform lanes passed;
final full repository validation and merge remained pending when this document
was created.

This directory contains historical candidate evidence, not a product
specification. Current behavior remains defined by `docs/requirements.md`,
`docs/spec.md`, and
`docs/specifications/interfaces/skill-invocation-snapshot.md`.

## Candidate matrix

| Lane | Immutable candidate | Date | Outcome |
| --- | --- | --- | --- |
| Windows signed-in owned session | `527c7b5f299296afefe13def783c08be121684b9` | 2026-08-25 | `passed` in one attempt |
| Linux WSL Ubuntu native ext4 | `527c7b5f299296afefe13def783c08be121684b9` | 2026-08-25 | `passed` in one attempt |

Both rows are accepted live executions at the same exact detached-clean
candidate; no cross-candidate inference is used. The evidence-document commit
SHA was not yet frozen, and full pinned validation, fresh final review, merge,
and Issues #173/#158 closure remained pending.

## Milestones

| Milestone | Status | Evidence |
| --- | --- | --- |
| `M1` owned-session producer | Platform lanes passed; final full validation and merge pending | [Live validation](milestones/M1-owned-session-producer/live-validation.md) |
