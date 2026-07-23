# Issue #75 M1 historical analysis validation evidence

This is historical candidate evidence, not a product specification. It
contains no user content, provider payload, credential, raw record, sensitive
path, or content-enabled capture output.

## Evidence boundary

| Field | Record |
| --- | --- |
| Wave 4 kickoff | `2df115682f0e280d020c04b4936968d4602f623c` |
| Pre-freeze activation | `da016c581e89dc3902e6e0332b618252d5028481` |
| Source functional candidate | `e7037714f16bc4c9c14b34ed8f69fc6e18cd6972` |
| Exact integrated validation candidate | `2054bae8daa433315ba30221e456e031a488b02b` |
| Superseded source candidates | `e2c2e2d5f80d26f8921e9b7c6b1ee8396e79a2c3`, `23c5212e0bf0bf05885930974e53d051d731e117` |
| Superseded integrated candidates | `7e9be266a9898226caf863366de9625294ba8d87`, `0c67e185dd0d72c33b6ff3bf661b24e414fc3739` |
| #72 repair source / integration | `e9bc3a7bb6feb5bdefa084dcace420f19670fd1f` / `23c5212e0bf0bf05885930974e53d051d731e117` |
| Date / OS | 2026-07-24 / Windows x64 |
| Browser | Repository-local Playwright Chromium and headless Chromium |
| Data | Disposable SQLite databases, loopback-only hosts, and sanitized synthetic multi-Session metadata |
| Capture policy | No genuine provider execution and no content-enabled live capture |

All accepted Wave 1–3 functional revisions named by the Wave 4 contract were
verified as ancestors of the integrated validation candidate. Source commit
`e7037714...` and integrated commit `2054bae8...` have the same stable patch ID,
and their #75 production, specification, contract, evidence, and test paths
have no tree differences. The integration candidate worktree was clean before
and after execution.

## `91-H-075` — automated and browser gate

The focused matrix covers bounded repository/workspace/date/explicit-Session
scope, deterministic ordering and truncation, included and excluded Sessions,
mixed source/completeness, independent Instruction and Efficiency starts,
supported/weak/incomplete and zero/failure/timeout/canceled/stale states,
provider unavailable/partial/failed states, exact evidence resolution, and
safe browser state.

Required and focused results at exact integrated validation candidate
`2054bae8daa433315ba30221e456e031a488b02b`:

- handoff-declared 13-filter gate: Local Monitor 279/279 and
  InstructionFindings 20/20, zero failed or skipped;
- Historical Analysis Playwright matrix: 7/7;
- Issue #75/#91/specification contract gate: 15/15;
- production historical evidence class: 33/33;
- solution build: zero warnings and zero errors;
- Playwright Chromium bootstrap: passed;
- final full solution: 8486/8486, zero failed or skipped —
  InstructionFindings 20, Alerts 451, Doctor 266, Config CLI 4598, and Local
  Monitor 3151.

The Playwright matrix launches real loopback HTTP hosts and exercises the
compiled page and JavaScript against safe multi-Session projections. It is a
repository-safe local execution, not evidence of genuine provider
interoperability.

## `91-S-075` — security, accessibility, and no-leak gate

The corrected candidate retains loopback Host validation, same-origin and
bounded-body guards, no-store responses, strict DTOs, raw/sanitized separation,
exact owner pairing, missing/unresolved/expired distinctions, inert text
insertion, keyboard order, focus restoration, and polite live-region
announcements. A sanitized-only host now rejects
`selection.sanitized_only=false` as
`400 invalid_historical_analysis_request` before the #72 owner opens a snapshot
or reads a descriptor; the server does not rewrite the closed selection. The
browser stores only a frozen `extraction_id`, `raw_local_sha256`, and
`repository_safe_sha256` binding after transient preview rendering.

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

After the first #75/#48 closeout, an independent final safety review rejected
the `91-S-075` claim at integrated candidate
`0c67e185dd0d72c33b6ff3bf661b24e414fc3739`. It found that the coordinator
forwarded caller-controlled `selection.sanitized_only=false` on a
sanitized-only host and that the browser assigned the complete preview
response to persistent closure state. #75 and dependent parent #48 were
reopened, and that candidate and its prior closeout claim were explicitly
superseded.

Before production changes, the two new regressions both failed against the old
implementation: the route returned `200` instead of the fixed `400`, and the
runtime browser binding observation was absent because no bounded projection
was retained. Candidate `e2c2e2d5f80d26f8921e9b7c6b1ee8396e79a2c3`
binds the immutable host posture into the coordinator, rejects before owner
access, and retains only the exact three-field browser binding. The two
regressions then passed 2/2; the settled Historical Analysis gate passed 77/77,
route plus Playwright passed 47/47, specification plus coordinator passed
25/25, and two independent reviews reported C0/I0/M0. The required build,
bootstrap, full suite, and all focused #75 gates were then rerun at the new
candidate with the results above.

A subsequent independent Issue/spec/matrix review rejected that evidence as a
final closeout because canonical requirements, specification, interface, and
security text still described only a generic bounded safe browser projection.
That wording did not exclude retention of the complete repository-safe preview
response. The same review found that the corrected candidate commits were
created on 2026-07-24 JST while evidence metadata still said 2026-07-23.

The new specification regression failed 2/5 against the generic canonical
wording. Source candidate
`e7037714f16bc4c9c14b34ed8f69fc6e18cd6972` pins the post-render long-lived
binding to exactly `extraction_id`, `raw_local_sha256`, and
`repository_safe_sha256` across all four canonical documents; the regression
then passed 5/5. Its integrated patch-equivalent candidate
`2054bae8daa433315ba30221e456e031a488b02b` reran skill mirror, build,
Playwright bootstrap, the full 8486/8486 suite, handoff 279+20, Playwright 7,
contract/specification 15, and production evidence 33 with zero failures or
skips. Evidence dates were corrected to the actual 2026-07-24 JST execution
date before re-attestation.

The first corrected-evidence scan invocation supplied three comma-delimited
paths as one PowerShell string and returned
`scan_result=ERROR reason=required_target_missing`. No artifact was missing;
the command shape was wrong. The same canonical scanner was rerun with an
explicit three-element path array and passed with 3 files, 354 variants, and
zero matches. The failed invocation is not represented as a scanner pass.

## Candidate validation commands

Commands were run from exact integrated validation candidate `2054bae8...`
unless the failure history above says otherwise. The source candidate ran the
new specification regression directly as 5/5.

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
