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
- Task 1 commit: `287f0c302ccbd24b9cef91acb98747d8635319f7`.
- Task 2 thin cross-surface slice: RED observed at real CLI dispatch; GREEN
  direct/CLI/HTTP result equality; independent review found one Important and
  one Minor; both corrected through a second RED/GREEN cycle; re-review PASS.
  Root focused test passed 1/1 and root solution build passed with 0 warnings
  and 0 errors.
- Task 2 commit: `80fd19e`.
- Tasks 3-6 completed in isolated linked worktrees, independently reviewed,
  locally committed, and cherry-picked into the primary feature branch through
  `52794b3`. Source and primary commit ranges are preserved in the durable
  ledger.
- Task 7 integration implements the shared SQLite application service, default
  CLI and Local Monitor production composition, trusted candidate completion,
  D051 isolation, all-state cross-surface equality, and real lifecycle matrices.
- Task 7 independent review initially found three Important issues. Observation
  source/adapter mismatches are now rejected before persistence, both real
  production surfaces cover lifecycle/no-mutation outcomes, and 19 SDD evidence
  identities were sanitized. Re-review PASS: 0 Critical, 0 Important, 0 Minor.
- Task 7 commit: `105e156e6beacebe4104f1d0bf2a6a65a1edbda6`.
- Root Task 7 evidence: Doctor 216/216, Config CLI Doctor 152/152, Local Monitor
  Doctor 49/49, solution build 0 warnings/0 errors, machine-local path scan 0,
  prohibited wait/retry scan 0. Implementer full solution: 5,294/5,294 (Doctor
  216 + LocalMonitor 1,442 + ConfigCli 3,636), with no failed or skipped tests.
- Task 8 updates the derived Local Monitor guide, repository Doctor smoke
  commands, roadmap status, durable ledger, and SDD evidence. It does not change
  canonical behavior or product code. Independent documentation review PASS:
  0 Critical, 0 Important, 0 Minor. The reviewer reran Doctor 216/216, Config
  CLI Doctor 152/152, and Local Monitor Doctor 49/49 and confirmed executable
  evaluate/start/status/cancel guidance, marker coverage, and evidence hygiene.
- Accepted handoff: #103/#104 source-specific candidate producers remain
  compile-shape and live-unverified; #105 proxy/UI is absent and unverified.
  Setup, evaluation, and verification start do not prove a first real trace.
  The feature branch is not integrated into `main`, and Issue #102 is not
  recorded as closed.
- Next action: commit Task 8, run pinned validation, complete the four
  whole-branch reviews, and make the separate integration/closure decision.

## Worker constraint

The available `spawn_agent` interface does not expose model or reasoning selectors. Task briefs request GPT-5.6 Luna xhigh as the preferred runtime, but orchestration cannot verify or enforce that selection.
