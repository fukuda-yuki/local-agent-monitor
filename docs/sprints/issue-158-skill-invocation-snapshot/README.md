# Issue #158 Skill invocation snapshot validation evidence

Status: exact-final-candidate Windows owned-session and Linux native-ext4
platform/live lanes passed. Pinned full validation, fresh final reviews, merge,
release, and Issues #173/#158 closure remained pending when this document was
created.

This directory contains historical candidate evidence, not a product
specification. Current behavior remains defined by `docs/requirements.md`,
`docs/spec.md`, and
`docs/specifications/interfaces/skill-invocation-snapshot.md`.

## Candidate matrix

| Lane | Immutable final candidate | Date | Outcome |
| --- | --- | --- | --- |
| Windows signed-in owned session | `711b3e16283796d7d8dcebe8733fdb63dbc86df6` | 2026-08-25 | `passed` in one authorized attempt |
| Linux WSL Ubuntu native ext4 | `711b3e16283796d7d8dcebe8733fdb63dbc86df6` | 2026-08-25 | `passed` in one wrapper attempt |

The earlier accepted anchor
`527c7b5f299296afefe13def783c08be121684b9` is historical and superseded
because production changed afterward. It is not a final evidence row, and no
cross-candidate inference is used.

The evidence-document commit and frozen SHA are established by Git history;
they cannot be self-recorded in this document. The pending release gates above
are not claimed complete.

## Milestones

| Milestone | Status | Evidence |
| --- | --- | --- |
| `M1` owned-session producer | Final platform/live lanes passed; pinned full validation, fresh final reviews, merge, release, and closure pending | [Live validation](milestones/M1-owned-session-producer/live-validation.md) |
