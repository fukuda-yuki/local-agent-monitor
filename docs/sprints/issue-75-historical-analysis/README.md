# Issue #75 historical analysis validation closeout

Status: candidate-pinned automated, browser, and security validation complete;
genuine provider-backed multi-Session execution remains `blocked_external`.

This directory is historical validation evidence. It records how Issue #75
was validated at the pinned candidate and does not define current product
behavior. Current behavior remains defined by `docs/requirements.md`,
`docs/spec.md`, and
`docs/specifications/interfaces/historical-analysis.md`.

## Candidate and matrix

- Wave 4 kickoff: `2df115682f0e280d020c04b4936968d4602f623c`
- Pre-freeze matrix activation: `da016c581e89dc3902e6e0332b618252d5028481`
- Functional candidate: `e2c2e2d5f80d26f8921e9b7c6b1ee8396e79a2c3`
- Superseded functional candidate: `23c5212e0bf0bf05885930974e53d051d731e117`
- Superseded integrated candidate: `0c67e185dd0d72c33b6ff3bf661b24e414fc3739`
- #72 repair source: `e9bc3a7bb6feb5bdefa084dcace420f19670fd1f`
- #72 repair integration: `23c5212e0bf0bf05885930974e53d051d731e117`
- Validation date: 2026-07-23
- Matrix: `docs/sprints/issue-75-historical-analysis/validation-matrix.json`
- Detailed evidence:
  `docs/sprints/issue-75-historical-analysis/milestones/M1-historical-analysis/live-validation.md`

| Row | Classification | Scope |
| --- | --- | --- |
| `91-H-075` | `passed` | Bounded scope/preview, included and excluded Sessions, independent analyses, state distinctions, exact drill-down, and browser behavior |
| `91-S-075` | `passed` | Sanitized-host fail-closed owner boundary, bounded browser binding, exact ownership, inert rendering, accessibility, HTTP negatives, and repository-safe scanning |
| `91-L-075` | `blocked_external` | Genuine provider-backed multi-Session execution using a reviewed source tuple |

The release decision is `release_ready_with_external_blockers`. The live row
is not converted into a fixture-backed pass. No separately authorized content
capture or reviewed exact multi-Session provider tuple was available, so no
genuine provider execution was attempted.

Final safety review invalidated the earlier `91-S-075` pass recorded at
`452ff6ab8c1265dfb3820894f258c3d176761269` and integrated candidate
`0c67e185dd0d72c33b6ff3bf661b24e414fc3739`. The earlier implementation
trusted a caller-controlled raw-capable preview selection on a sanitized-only
host and retained the complete preview response in browser closure state. The
corrected functional candidate rejects that selection before #72 owner access
without rewriting it and retains only extraction ID plus the two exact
checksums. The matrix and detailed evidence record the new regression tests,
independent reviews, and fresh candidate-pinned validation.

Historical import, proposal apply, verified effect verdicts, provider pricing,
Alert Center, and portability behavior remain owned by their existing Issues;
Issue #75 did not absorb those scopes.
