# Issue #75 M1 historical analysis validation evidence

This is historical candidate evidence, not a product specification. It
contains no user content, provider payload, credential, raw record, sensitive
path, or content-enabled capture output.

## Evidence boundary

| Field | Record |
| --- | --- |
| Wave 4 kickoff | `2df115682f0e280d020c04b4936968d4602f623c` |
| Pre-freeze activation | `da016c581e89dc3902e6e0332b618252d5028481` |
| Functional candidate | `23c5212e0bf0bf05885930974e53d051d731e117` |
| #72 repair source / integration | `e9bc3a7bb6feb5bdefa084dcace420f19670fd1f` / `23c5212e0bf0bf05885930974e53d051d731e117` |
| Date / OS | 2026-07-23 / Windows x64 |
| Browser | Repository-local Playwright Chromium and headless Chromium |
| Data | Disposable SQLite databases, loopback-only hosts, and sanitized synthetic multi-Session metadata |
| Capture policy | No genuine provider execution and no content-enabled live capture |

All accepted Wave 1–3 functional revisions named by the Wave 4 contract were
verified as ancestors of the functional candidate. The candidate worktree was
clean before and after execution.

## `91-H-075` — automated and browser gate

The focused matrix covers bounded repository/workspace/date/explicit-Session
scope, deterministic ordering and truncation, included and excluded Sessions,
mixed source/completeness, independent Instruction and Efficiency starts,
supported/weak/incomplete and zero/failure/timeout/canceled/stale states,
provider unavailable/partial/failed states, exact evidence resolution, and
safe browser state.

Results at the exact functional candidate:

- handoff-declared 13-filter gate: Local Monitor 278/278 and
  InstructionFindings 20/20, zero failed or skipped;
- Historical Analysis Playwright matrix: 7/7;
- Issue #75/#91/specification contract gate: 15/15;
- production historical evidence class: 33/33;
- solution build: zero warnings and zero errors;
- Playwright Chromium bootstrap: passed;
- final full solution: 8485/8485, zero failed or skipped —
  InstructionFindings 20, Alerts 451, Doctor 266, Config CLI 4598, and Local
  Monitor 3150.

The Playwright matrix launches real loopback HTTP hosts and exercises the
compiled page and JavaScript against safe multi-Session projections. It is a
repository-safe local execution, not evidence of genuine provider
interoperability.

## `91-S-075` — security, accessibility, and no-leak gate

The candidate retains loopback Host validation, same-origin and bounded-body
guards, no-store responses, strict DTOs, raw/sanitized separation, exact owner
pairing, missing/unresolved/expired distinctions, inert text insertion,
keyboard order, focus restoration, and polite live-region announcements.

The Issue #91 scanner self-test passed 118 transformation cases and 5 negative
cases. The installed Issue #75 handoff produced 118 generated variants with
zero matches. The final Issue #75 evidence directory was rescanned after
materialization; that result and the matrix validator result are recorded in
the evidence commit and GitHub closeout.

No raw history is kept in browser storage or full client state. No heuristic
Session lookup by repository, workspace, path, time, order, or shared trace was
introduced.

## `91-L-075` — genuine provider gate

Classification: `blocked_external` / severity `high`.

No genuine provider-backed multi-Session execution was attempted. Two
independent preconditions were absent:

- content-enabled live capture did not have separate authorization; and
- no exact reviewed provider/source/version multi-Session tuple was available.

Retry requires both preconditions, a newly frozen candidate, provider
execution, canonical #73 and #74 receipt readback, exact #53 drill-down, and a
repository-safe leak scan. The unverified capability is genuine
provider-backed instruction findings and efficiency evidence across that exact
reviewed multi-Session dataset.

Synthetic and production-component fixtures prove deterministic local
behavior but are not substituted for this live row.

## Required failure and correction history

The first frozen candidate
`da016c581e89dc3902e6e0332b618252d5028481` did not pass the full suite.
`HistoricalEvidenceProductionTests` used fixed 2026-07-22 content expirations
with `TimeProvider.System`; as wall-clock UTC crossed those values, the focused
class produced 26 passes and 7 deterministic
`retention_migration_blocked` failures. The original full run observed six of
those failures, reached 2960 Local Monitor passes, then stopped making progress
and was explicitly terminated. It is not recorded as a pass.

Issue #72 was reopened because it owned those fixtures. The production
retention code remained unchanged and correctly fail-closed. Source repair
`e9bc3a7bb6feb5bdefa084dcace420f19670fd1f` binds the affected fixtures to
their declared clocks; integrated candidate
`23c5212e0bf0bf05885930974e53d051d731e117` contains the same repair. The class
then passed twice as 33/33, retention/read-denial regressions passed 46/46, and
an independent review approved the test-only correction with no findings.

The first full-suite run at `23c5212e...` produced 8484 passes and one
non-reproducible legacy Sprint18 design-view Playwright failure. The candidate
does not change that test, `monitor-flow.js`, or `monitor-waterfall.js`; the
only shared CSS change is a panel-title margin reset. Diagnosis retained the
failure and found:

- the design-view class passed 3/3 and then 30/30 across ten coordinator
  repetitions;
- independent exact-case repetitions passed 12/12;
- an expanded Playwright set passed 96/96;
- the baseline design-view class passed 3/3 after its browser bootstrap.

No timeout was increased and no failure was masked. Because no candidate
regression or reproducible source defect was found, no behavior change or
candidate reselection was justified. The fresh required full suite then passed
8485/8485. A recurrence must capture browser page errors, failed requests,
`window.caoWaterfall`, and the waterfall DOM before any legacy-owner change.

An auxiliary `dotnet build --no-restore` in the new #72 repair worktree failed
with `NETSDK1004` because five projects had not yet produced restore assets.
The exact required build with restore then passed with zero warnings and zero
errors. The failed precondition attempt is not represented as validation
success.

## Candidate validation commands

Commands were run from the exact functional candidate unless the failure
history above says otherwise.

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx

$handoff = Get-Content docs\specifications\contracts\historical-analysis\v1\issue-91-validation-handoff.json -Raw | ConvertFrom-Json
$filter = $handoff.automated_test_filters -join '|'
dotnet test CopilotAgentObservability.slnx --no-build --no-restore --filter $filter --logger "console;verbosity=minimal"

dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~HistoricalAnalysisPlaywrightTests --logger "console;verbosity=minimal"
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Issue75ValidationContractTests|FullyQualifiedName~Issue91ValidationContractTests|FullyQualifiedName~HistoricalAnalysisSpecificationTests" --logger "console;verbosity=minimal"
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~HistoricalEvidenceProductionTests --logger "console;verbosity=minimal"

pwsh -NoProfile -File scripts\validation\issue-91\test-scan-outputs.ps1
pwsh -NoProfile -File scripts\validation\issue-91\scan-outputs.ps1 -InputPath docs\specifications\contracts\historical-analysis\v1 -OutputType evidence
pwsh scripts\validation\issue-91\validate-matrix.ps1 -MatrixPath docs\sprints\issue-75-historical-analysis\validation-matrix.json
pwsh -NoProfile -File scripts\validation\issue-91\scan-outputs.ps1 -InputPath docs\sprints\issue-75-historical-analysis -OutputType evidence
```

## Extension and publication state

Rows `91-H-075`, `91-S-075`, and `91-L-075` are owned by this matrix. The
historical-analysis placeholder is absent from the #91 future registry, and no
active pass was inherited from that registry. Revisions and evidence are local
only; no push or pull request was performed.
