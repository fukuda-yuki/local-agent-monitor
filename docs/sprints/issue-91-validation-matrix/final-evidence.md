# Issue #91 Final Candidate Evidence

This record contains bounded results only. It contains no credentials,
authorization values, prompt/response bodies, tool arguments/results, PII,
database content, reversible markers, or machine-sensitive paths.

## Candidate and dependency gate

| Field | Value |
| --- | --- |
| Branch | `codex/issue-91-validation-matrix` |
| Starting SHA / `matrix_prep_sha` | `5180a0424ff5488354a3e173c74b7e931d28679d` |
| Immutable `final_validation_sha` | `40ac55974dc7788f4abd54dfa85abd97739b3201` |
| Candidate state at freeze | clean dedicated worktree |
| Date / boundary | 2026-07-21; Windows native, current user; live runs used loopback-only disposable databases |

All final dependency states were `CLOSED`. `git merge-base --is-ancestor`
returned exit `0` for each accepted revision against the candidate:

| Dependency | Accepted revision | Additional ancestral evidence |
| --- | --- | --- |
| #69 | `b72309cca5544fdf84e2935619449dc26fb4f261` | #105 candidate `b581be9864c284d27884e94208e92138b3e83040` |
| #89 | `de48d717479a40921a6fe70825f6e95a7a75037a` | accepted wake-coalescing fix; prior lineage `043f7a32...` |
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
Monitor 948/948, with zero failed and zero unexecuted cases.

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

## Required candidate validation and correction

The accepted #89 fix `de48d717...` makes active-cycle wake coalescing
atomic and is ancestral to the final candidate. Candidate-focused retention
runtime tests passed 7/7. The first pre-correction #91 candidate was invalidated
and none of its classifications were carried forward.

A later candidate at `f4bc73f1...` produced one file-level Canvas Node smoke
failure during the required full suite: the first 12 in-memory JS cases passed
and the file aborted when the first loopback helper server began. Exact focused
and direct Node runs passed, but the retry was not converted into passing
evidence. The validation test was corrected by placing all 28 Canvas extension
contract tests in a dedicated xUnit collection with
`DisableParallelization = true`. This preserves every assertion and removes
full-suite loopback/process resource contention without retry, sleep, skip, or
product behavior change. The corrected candidate is `40ac5597...`.

Commands were run from the repository root at the unchanged corrected
candidate:

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | exit 0; 0 warnings, 0 errors |
| `pwsh scripts\\test\\install-playwright-chromium.ps1` | exit 0 |
| `dotnet test CopilotAgentObservability.slnx` | exit 0; 6,582/6,582 passed, 0 skipped |

The corrected candidate automated matrix also passed: scanner self-test 100
transformations and five negative cases, semantic contract 10/10, ConfigCli
760/760, Doctor 24/24, and LocalMonitor 948/948. The runner removed its
ephemeral TRX directory in `finally`; only these bounded counts are retained
in the repository evidence ledger.

## 91-F classification and decision

The signed matrix and final candidate inventory is
[`final-matrix.json`](final-matrix.json). Its semantic validator reports:

```text
matrix_validation=PASS rows=19 decision=release_ready_with_external_blockers
```

Classification totals are 17 `passed`, one `not_applicable`, and one
`blocked_external`. There are no `failed`, `not_attempted`, unknown,
unclassified, or unknown-owner rows.

- `blocked_external/medium`: `91-D-001` current Claude 2.1.215
  content-enabled capture, pending distinct operator authorization.
- `not_applicable`: `91-C-017` setup HTTP/proxy/UI, excluded by the canonical
  public-interface specification.

The exact release decision is **`release_ready_with_external_blockers`**.
All hard required rows pass on the immutable candidate; the remaining live row
is a correctly recorded external authorization blocker. Under the Issue #91
release and close contracts, #91 satisfies its close condition and #57 closeout
may begin. Historical blocker handoffs remain provenance and must be superseded
by the final GitHub close record; none is used as current classification
evidence.

## Delegated evidence checks

Read-only subagents independently checked #113/#114/#115 close conditions,
dependency state and ancestry, final active surfaces, historical compatibility,
live execution requirements, the #89 retention defect, the Canvas file-level
failure, and the deterministic Canvas isolation correction. The primary
coordinator verified each result using GitHub state, exact ancestry commands,
candidate diffs, the matrix runner, focused runtime execution, and the required
repository commands.

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
3. Candidate/evidence/test integrity reconfirmed all cited ancestry, 19/19 SHA
   ties, bounded historical reuse, the Canvas isolation boundary, and the
   `release_ready_with_external_blockers` aggregation. It found one false
   persistent-artifact reference, which was removed because the runner deletes
   its ephemeral TRX directory.

After final-candidate correction, both final artifacts are rescanned separately
and the semantic matrix validator is rerun. The preparation inventory
intentionally retains its pre-freeze state and null final SHA; this signed
matrix is the final candidate inventory.
