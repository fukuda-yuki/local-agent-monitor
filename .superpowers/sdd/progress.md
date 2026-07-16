# Issue #102 Doctor Core Recovery Map

- Primary branch: `codex/issue-102-doctor-core`
- Base: `8940b34f4e031b894705682dc50c079a9ed5c180`
- Design HEAD: `3e0760b961465fa78c9f1e6cc48d9f8002cd34d9`
- Durable ledger: `docs/sprints/issue-102-doctor-core/ledger.md`
- Executable plan: `docs/superpowers/plans/2026-07-16-issue-102-doctor-core.md`
- Main integration: not performed

## Current checkpoint

- Task 0 preflight: PASS on design HEAD.
- Baseline build: PASS, 0 warnings and 0 errors.
- Baseline Playwright Chromium bootstrap: PASS.
- Baseline full tests: PASS, 4,877 passed, 0 failed, 0 skipped.
- Task 0 commit: `0a2542d9e973429e39a4986c823996b057fb607b`.
- Task 1 canonical specification: implementation complete; initial independent
  review found five Important findings; all five were corrected; re-review PASS
  with 0 Critical, 0 Important, and 0 Minor findings.
- Next action: commit Task 1, then dispatch Task 2 thin executable
  cross-surface slice with TDD and independent review.

## Worker constraint

The available `spawn_agent` interface does not expose model or reasoning selectors. Task briefs request GPT-5.6 Luna xhigh as the preferred runtime, but orchestration cannot verify or enforce that selection.
