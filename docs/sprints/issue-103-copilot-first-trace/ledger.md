# Issue #103 GitHub Copilot First Trace Durable Ledger

Current product behavior remains in `docs/requirements.md`, `docs/spec.md`, and
the canonical interface specifications. The Issue body is the authoritative
work checklist; this ledger records execution evidence without copying it into
a second plan.

## Execution identity

- Worktree: dedicated Codex Issue #103 worktree (machine-specific absolute path
  intentionally omitted from repository-safe evidence)
- Branch: `codex/issue-103-copilot-first-trace`
- Kickoff/base SHA: `920ff43a9ec63088a9cc109bcd15d0e6f4f9dc5c`
- Feature-branch completion: not yet complete
- Main integration: not performed
- Push / pull request / external Issue changes: not performed

## Task state

| Issue checklist section | State | Commit range | Focused / full tests | Independent review | Unresolved |
| --- | --- | --- | --- | --- | --- |
| 103-A | In progress | `920ff43..e99767c`; canonical promotion and RED test awaiting checkpoint commit | kickoff baseline PASS; ConfigCli and cross-surface focused REDs have only expected `CS0103` calls to missing `GitHubCopilotDoctorFactMapper` | contract table and final spec/RED re-review PASS C0/I0/M0 | mapper GREEN |
| 103-B | Pending | pending | pending | pending | trusted candidate producer |
| 103-C | Pending | pending | pending | pending | setup-to-verification orchestration |
| 103-D | In progress | projection RED `8650465`; GREEN awaiting checkpoint commit | disposition/migration/worker/compatibility 77/77 PASS; Doctor migration 12/12 PASS; no sleep/retry | projection RED and final GREEN re-reviews PASS C0/I0/M0 | source matrices, privacy, full regressions |
| 103-E | Pending | pending | pending | pending | supported live surface or exact external blocker |

## Validation evidence

| Gate | Command | Result |
| --- | --- | --- |
| Kickoff build | `dotnet build CopilotAgentObservability.slnx` | PASS; 0 warnings, 0 errors |
| Kickoff browser bootstrap | `pwsh scripts\test\install-playwright-chromium.ps1` | PASS |
| Kickoff full suite | `dotnet test CopilotAgentObservability.slnx` | PASS; Doctor 232/232, ConfigCli 3,857/3,857, LocalMonitor 1,460/1,460; total 5,549/5,549; 0 failed, 0 skipped |

## Acceptance criteria tracking

The fourteen acceptance criteria are tracked as `AC-01` through `AC-14` in
their exact Issue-body order. Evidence is added only after root compares the
requirement with executable output and the reviewed diff; a worker report alone
does not satisfy an AC.

| Criterion | State | Evidence |
| --- | --- | --- |
| AC-01..AC-13 | Pending | pending implementation and reviewed executable evidence |
| AC-14 | Baseline only | kickoff commands pass; final committed-HEAD rerun still required |

## Review state

- Per-task implementer and reviewer must be different agents.
- Final reviews must separately cover requirements/public/Issue contracts,
  security/privacy, concurrency/atomicity/rollback, migration/restart, and the
  whole branch.
- No open review finding is accepted as complete without a recorded disposition
  and re-review where required.

## Unresolved and unverified Issue interfaces

- #104 and #106 worktrees/branches are untouched and their implementations are
  not merged, rebased, or cherry-picked.
- #105 proxy, common UI, Playwright/accessibility closeout, and Release ZIP
  workflow are out of scope and remain unverified.
- The production OTLP metadata provider currently labels ordinary input
  `raw-otlp`; #103 must not claim source attribution until the selected
  raw/Session evidence is validated by source-owned exact values.
- Per-record projection disposition is not currently durable for non-completed
  records; raw-row presence without a projection row cannot distinguish
  `not_started`, `pending`, or `failed`. All three remain unknown until an exact
  selected-record seam exists; row absence and the global failure counter are
  not evidence for any of them.
- Live supported GitHub Copilot evidence has not yet been recorded.
- Feature-branch completion and main integration are distinct; neither is
  currently claimed.

## Evidence hygiene

The ledger records only safe task state, commit IDs/ranges, command results,
review verdicts, fixed source identifiers, and unresolved interface names. It
does not contain raw prompts/responses, tool bodies, credentials, authorization
values, PII, sensitive local paths, database paths, or runtime artifacts.
