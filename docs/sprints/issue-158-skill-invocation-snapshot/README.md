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
| Windows signed-in owned session | `bb698a6aad0f2b178d2d5a24596a8ce07fb64d63` | 2026-08-25 | `passed` |
| Linux WSL Ubuntu native ext4 | `9d5fbfb8798fe277bffda6d2d54f95815319d9a5` | 2026-08-25 | `passed` |

The Windows lane was not rerun at `9d5fbfb8798fe277bffda6d2d54f95815319d9a5`.
The exact name-status diff from the Windows candidate to the Linux candidate
changed only the Linux wrapper/helper/runbook/self-test, Linux native opener
and APIs, Linux live matrix tests, and native classifier tests. The conclusion
that the signed-in Windows lane's code path was unchanged is therefore a
deliberate diff-based inference, not a claim of Windows execution at the later
candidate.

## Milestones

| Milestone | Status | Evidence |
| --- | --- | --- |
| `M1` owned-session producer | Platform lanes passed; final full validation and merge pending | [Live validation](milestones/M1-owned-session-producer/live-validation.md) |
