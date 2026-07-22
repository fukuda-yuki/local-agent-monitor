# Issue #84 Alert Center validation closeout

Status: candidate-pinned automated and security validation complete; the
genuine provider-positive gate remains `blocked_external`.

This directory is historical validation evidence. It records how Issue #84
was validated at the pinned functional candidate and does not define current
product behavior. Current behavior remains defined by `docs/requirements.md`,
`docs/spec.md`, and `docs/specifications/interfaces/alert-center.md`.

## Candidate and matrix

- Functional candidate: `b1fb801101f25ff5d8716a56a8b0ff9c95cc988e`
- Matrix preparation baseline: `c02c10ab18553acef1619ce12ec630f4f6f5aa5f`
- Validation date: 2026-07-23
- Matrix: `docs/sprints/issue-84-alert-center/validation-matrix.json`
- Detailed evidence: `docs/sprints/issue-84-alert-center/milestones/M1-alert-center/live-validation.md`

| Row | Classification | Scope |
| --- | --- | --- |
| `91-A-084` | `passed` | Typed bounded read, exact evidence, explicit evaluation, lifecycle, recurrence, pagination, and restart behavior |
| `91-S-084` | `passed` | Metadata-only/no-store rendering and Host, origin, CSRF, malformed-input, stale-revision, and evidence mismatch negatives |
| `91-L-084` | `blocked_external` | Genuine positive receipt generation for GitHub Copilot and Claude Code |

The release decision is `release_ready_with_external_blockers`. The live row
is not converted into a fixture-backed pass: no named reviewed #61
source/version adapter currently grants the required Alert Center rule
capabilities for either provider. No provider run or content-enabled capture
was attempted.

Issue #85's `alert_center` export capability remains unavailable; Issue #84
does not promote or claim that separate export surface.
