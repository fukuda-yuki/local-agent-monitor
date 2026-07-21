# Issue #91 Final Candidate Evidence

This record contains bounded results only. It contains no credentials,
authorization values, prompt/response bodies, tool arguments/results, PII,
database content, reversible markers, or machine-sensitive paths.

## Candidate and dependency gate

| Field | Value |
| --- | --- |
| Branch | `codex/issue-91-validation-matrix` |
| Starting SHA / `matrix_prep_sha` | `5180a0424ff5488354a3e173c74b7e931d28679d` |
| Immutable `final_validation_sha` | `9fe02f6e7dfaaec71bd6d7cc05aa75d1e3318858` |
| Candidate state at freeze | clean dedicated worktree |
| Date / boundary | 2026-07-21; Windows native, current user; live runs used loopback-only disposable databases |

All final dependency states were `CLOSED`. `git merge-base --is-ancestor`
returned exit `0` for each accepted revision against the candidate:

| Dependency | Accepted revision | Additional ancestral evidence |
| --- | --- | --- |
| #69 | `b72309cca5544fdf84e2935619449dc26fb4f261` | #105 candidate `b581be9864c284d27884e94208e92138b3e83040` |
| #89 | `043f7a3228d1e1e97f91de965d91b6e41a48e472` | retention implementation lineage |
| #90 | `5180a0424ff5488354a3e173c74b7e931d28679d` | implementation `4d966472...`, closeout `f412a5bf...` |
| #106 | `87c240adf0dfbaa20b0abf24c4b2d7571a828781` | #110 evidence through `11d6c587...` |

Issues #113, #114, and #115 satisfied their close conditions and were closed.
The #105 replacement candidate and corrected evidence were fast-forwarded to
local `main`; integrated-main build, Playwright bootstrap, and full tests
passed 6,575/6,575 with no skip. Issues #105 and #69 were then closed. Issue
#90's already accepted local closeout was verified as ancestral and formally
closed before candidate freeze.

## 91-A through 91-D

- 91-A: canonical matrix/classification/release contract, preparation and
  final inventory, future registry, evidence schema and semantic validator are
  complete.
- 91-B: versioned synthetic taxonomy/corpus, bounded transformation scanner,
  negative cases and self-tests are complete. Scanner self-test passed 100
  transformations and five negative cases; the semantic contract passed ten
  cases.
- 91-C: the automated manifest contains every applicable final active surface,
  including Doctor UI, exact evidence navigation, packaged lifecycle, cleanup,
  and mutation. The setup HTTP/proxy/UI surface is contract-based
  `not_applicable`.
- 91-D: source compatibility and exact-binding matrices cover supported/new/
  incompatible/drift/unknown/parse states, Hook/OTel availability, and exact
  negative binding rules.

The final candidate automated run reported `PASS`: ConfigCli 760/760 and Local
Monitor 942/942, with zero failed and zero unexecuted cases.

## 91-E live and runtime evidence

### Reused evidence

- #105 candidate `b581be98...` and corrected evidence `b72309cc...` are
  ancestral. No package/setup production change exists afterward. Reuse is
  bounded to GitHub Copilot CLI 1.0.71 content-disabled/current-user setup,
  readiness, real trace, exact trace/source-diagnostic navigation, restart,
  rollback, uninstall, and cleanup. The historical Session target remained
  unbound and was not relabeled as passed.
- #106/#110 evidence through `11d6c587...` is reused only for Claude Code
  2.1.214 and its recorded settings/environment: gate state, Hook/OTel native
  equality, negative binding, restart/reconnect, and sanitized-only behavior.
  Candidate automation rechecked the current binding and source-state rules.
- #99 and #103 are provenance/drift context only where superseded by #105 or a
  candidate-current run; mismatched observations are not current passes.

### Newly executed evidence

- Claude Code 2.1.215, Windows native/current-user, content capture disabled:
  readiness HTTP 200, producer exit 0, one sanitized trace,
  `content_state=not_captured`, and repository/log/sanitized-output leak scan
  `PASS`. The runtime marker and every marker-derived value are omitted. The
  disposable process and database were removed by the repository cleanup
  script.
- Candidate retention runtime using disposable databases and `TimeProvider`:
  expiry denial, delete-now denial, exact ownership, and no-resurrection tests
  passed 7/7 with zero skip.
- Claude Code 2.1.215 content-enabled capture was not executed. The #106
  runbook requires distinct explicit content-capture authorization; CLI/tool
  authorization alone is insufficient. Row `91-D-001` records the exact
  `blocked_external`, severity, retry condition, and unverified capability.

## Required candidate validation and defect finding

Commands were run from the repository root at the unchanged candidate:

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | exit 0; 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | exit 0 |
| `dotnet test CopilotAgentObservability.slnx` | exit 1; 6,582 total, 6,581 passed, 1 failed, 0 skipped |

The failed test was
`RetentionWorkerRaceTests.DueWinnerDoesNotLeaveAWakeLoserToStealTheNextCoalescedWake`:
one cleanup cycle was expected and two were observed. There is no change to
the worker, the test, or `MutableTimeProvider` between accepted #90 revision
`5180a042...` and the final candidate. A single exact diagnostic execution
passed 1/1; it establishes intermittency and does not replace the required
full-suite failure.

Root cause is a product concurrency defect in the #89-owned
`RetentionCleanupWorker`: with `SemaphoreSlim(0,1)`, the first release may
complete an active waiter while the next release becomes a newly queued wake,
allowing an extra cleanup cycle. Issue #91 explicitly forbids silently fixing
production behavior or splitting a new Issue, so row `91-C-012` is
`failed/high`. Resolution requires an owning retention-worker fix outside
#91, integration into a new candidate, and rerun of the affected row and all
required candidate commands.

## 91-F classification and decision

The signed matrix and final candidate inventory is
[`final-matrix.json`](final-matrix.json). Its semantic validator reports:

```text
matrix_validation=PASS rows=19 decision=release_blocked
```

Classification totals are 16 `passed`, one `not_applicable`, one
`blocked_external`, and one `failed`. There are no `not_attempted`, unknown,
unclassified, or unknown-owner rows.

- `failed/high`: `91-C-012` retention cleanup wake coalescing product defect.
- `blocked_external/medium`: `91-D-001` current Claude 2.1.215 content-enabled
  capture, pending distinct operator authorization.
- `not_applicable`: `91-C-017` setup HTTP/proxy/UI, excluded by the canonical
  public-interface specification.

The exact release decision is **`release_blocked`**. Issue #91 does not satisfy
its close condition because a required active row and the required full test
command failed. Issue #57 closeout must not begin. A repository-safe handoff to
#57/#60 may report this blocker, but cannot claim release readiness.

## Delegated evidence checks

Read-only subagents independently checked #113/#114/#115 close conditions,
dependency state and ancestry, final active surfaces, historical compatibility,
live execution requirements, and the retention failure. The primary
coordinator verified each result using GitHub state, exact ancestry commands,
candidate diffs, the matrix runner, focused runtime execution, and the required
repository commands. Final independent review findings are recorded below
after adjudication.

## Final review

Three independent read-only reviews completed after integration:

1. Specification/matrix coverage reconciled 19 inventory surfaces to 19
   terminal rows, 18 applicable manifest rows, and one canonical
   `not_applicable`; it found no unknown owner or coverage gap.
2. Secret/raw/PII/path safety found one blocking evidence defect: a dynamic
   marker-derived digest prefix appeared in the draft artifacts. All three
   occurrences were removed; no marker or derived value is retained. No other
   credential, authorization, raw body, PII, database-content, or sensitive
   path finding remained.
3. Candidate/evidence/test integrity reconfirmed dependency ancestry, bounded
   historical reuse, classification totals, the authoritative full-suite
   failure, and the `release_blocked` aggregation without a new finding.

After correction, both final artifacts were rescanned separately and the
semantic matrix validator was rerun. The preparation inventory intentionally
retains its pre-freeze state and null final SHA; this signed matrix is the final
candidate inventory.
