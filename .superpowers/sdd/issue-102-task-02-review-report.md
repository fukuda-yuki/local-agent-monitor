# Issue #102 Task 2 Independent Re-review Report

## Final verdict

**PASS** — 0 Critical, 0 Important, and 0 Minor findings.

The corrected Task 2 diff provides the requested TDD-backed executable
`monitor_not_running` cross-surface slice. The prior Important stateless-route
security finding and Minor test cleanup finding are resolved, all exact focused
checks pass, and no new scope or regression was found.

## Identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability`
- Branch: `codex/issue-102-doctor-core`
- Review HEAD: `287f0c302ccbd24b9cef91acb98747d8635319f7` plus the corrected uncommitted Task 2 diff
- Identity matched the original review brief and fix brief before re-review.
- The requested GPT-5.6 Luna/xhigh runtime could not be selected or verified by
  the available dispatch surface.
- Re-review was read-only except for replacing this report. No source, test,
  specification, ledger, plan, or other report was edited; no commit, push, PR,
  or integration was performed.
- Repository guidance referenced
  `.agents/skills/codex-subagent-dispatch/SKILL.md`, but that file is absent in
  this worktree. The re-review followed the explicit review/fix briefs and
  `docs/agent-guides/review-workflow.md` without substituting another skill.

## Evidence reviewed

Repository and canonical contract:

- `AGENTS.md`
- `docs/agent-guides/review-workflow.md`
- `docs/agent-guides/repository-workflow.md`
- the Issue #102 sections of `docs/requirements.md` and `docs/spec.md`
- `docs/specifications/interfaces/first-trace-doctor.md`
- `docs/specifications/interfaces/config-cli.md`
- the Local Monitor boundary in
  `docs/specifications/security-data-boundaries.md`
- the receiver/readiness contract in
  `docs/specifications/layers/telemetry-ingestion.md`
- the Doctor dependency/flow sections of `docs/architecture.md`
- D060 in `docs/decisions.md`
- the Task 2 and later-lane boundaries in
  `docs/superpowers/plans/2026-07-16-issue-102-doctor-core.md`
- `.superpowers/sdd/issue-102-task-02-brief.md`
- `.superpowers/sdd/issue-102-task-02-implementer-report.md`
- `.superpowers/sdd/issue-102-task-02-review-brief.md`
- `.superpowers/sdd/issue-102-task-02-fix-brief.md`
- `.superpowers/sdd/issue-102-task-02-fix-report.md`
- the prior version of this review report

Actual change set:

- re-verified the complete tracked diff for all 7 modified files;
- re-read all 11 untracked source/test/project/fixture files, including the
  complete corrected route and cross-surface test with line numbers;
- inspected all Task 2 brief/report Markdown files;
- inspected the unchanged global Host middleware and existing same-origin/CSRF
  helpers/call sites in `MonitorHost`;
- enumerated actual untracked files with `git ls-files --others
  --exclude-standard` rather than relying on directory-collapsed status output.

## Prior finding resolution

### Prior Important — resolved

The stateless `POST /api/doctor/evaluations` handler now begins with content-type
validation at
`src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs:21`; it no longer
calls `MonitorHost.IsCrossSiteRequest` or
`MonitorHost.HasMonitorCsrfHeader`. The real request at
`tests/CopilotAgentObservability.Doctor.Tests/DoctorCrossSurfaceContractTests.cs:51-56`
contains no CSRF header and passes with HTTP 200.

This now matches the canonical classification at
`docs/specifications/interfaces/first-trace-doctor.md:435-438`: all Doctor
routes retain loopback bind and Host-header protection, while same-origin/CSRF
is required for state-changing routes. Global Host validation remains unchanged
at `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs:166-175`; the
tracked `MonitorHost` diff still contains only the additive
`DoctorRoutes.Map(app)` call at line 178. Existing mutation checks and helper
methods remain present, and no future Doctor verification route was added or
weakened.

The fix report records a credible focused RED after the test header was removed:
expected HTTP 200, actual 403. The same test then went GREEN after removing only
the evaluation-specific mutation gate. The intermediate RED tree is no longer
available, so chronology remains report evidence, but the described failure is
consistent with the prior inspected implementation and the corrected test/diff.

### Prior Minor — resolved

`RunningDoctorMonitor.StartAsync` now assigns the built app to a nullable local
inside a `try` at
`tests/CopilotAgentObservability.Doctor.Tests/DoctorCrossSurfaceContractTests.cs:107-120`.
Any build, start, address-discovery, URI, client, or wrapper-construction failure
enters the catch at lines 121-136. A built app is asynchronously disposed, and
a nested `finally` attempts recursive directory deletion even if disposal
throws. Successful startup remains owned by `RunningDoctorMonitor.DisposeAsync`
at lines 139-150. The fix adds no sleep, polling, retry, fallback, dependency,
or repository artifact.

Direct code-path inspection is adequate for this narrow cleanup fix. Adding a
new host-start failure-injection seam would expand Task 2; the full focused test
and solution build confirm the corrected helper compiles and the success cleanup
path remains operational.

## Requirement-by-requirement verdict

| Requirement | Verdict | Evidence |
| --- | --- | --- |
| Credible TDD RED/GREEN evidence | PASS | The original report records an executable RED at real CLI dispatch (`Expected: 3`, `Actual: 1`) and repeated GREEN. The fix report records a second executable RED (`expected OK`, `actual Forbidden`) followed by GREEN. Both failures align with the inspected before/after behavior and are product-surface failures rather than fixture/setup failures. |
| One production `DoctorResult` across direct, real CLI dispatch, and real HTTP | PASS | Test lines 19-37 invoke real `CliApplication`, the production serializer/evaluator, byte-equal CLI JSON, and strict production-DTO equivalence. Lines 51-64 start real Kestrel, send a real headerless HTTP request, and strictly compare the production HTTP DTO. No facsimile DTO exists. |
| Exact `monitor_not_running` metadata and non-ready semantics | PASS | Test lines 65-87 pin `doctor.v1`, `success=true`, `evaluation_completed`, source, empty missing facts/evidence, severity `error`, retryability `after_action`, next action `start_monitor`, reason-code equality, canonical timestamp, null verification ID, CLI exit 3, and HTTP 200. |
| Deterministic JSON and strict DTO equality | PASS | Test lines 30-37 assert direct/CLI byte equality, repeated serialization equality, and strict production DTO equivalence; lines 63-64 prove strict HTTP equivalence. Stable property and array construction is visible in the shared Doctor project. |
| Human projection consumes the evaluated result | PASS | `DoctorCli.cs:27-43,97-103` evaluates once and passes that result to `DoctorHumanProjector`; the projector accepts only `DoctorResult`. Test lines 39-49 compare real human CLI output with that projector and bound it. |
| JSON naming, canonical timestamps, unknown-member rejection | PASS by inspection; narrow positive test evidence | `DoctorJson.cs:22-33` fixes snake_case, case-sensitive property matching, unmapped-member rejection, and string-only snake_case enums. Lines 36-60 enforce the exact seven-fraction UTC form. The fixture and JSON assertions exercise canonical positive forms. Full duplicate/cardinality/cross-field negative validation remains Task 3. |
| HTTP 200, `application/json`, `no-store`, stateless store independence | PASS | `DoctorRoutes.cs:16-42` sets no-store/application-json, invokes only the shared evaluator/serializer, and has no Doctor-store dependency. Test lines 51-64 prove a headerless request returns 200 with the required media type/header and equal result. |
| Loopback/Host retained; mutation-only same-origin/CSRF | PASS | The evaluation-specific mutation gate is absent. Global Host validation remains at `MonitorHost.cs:166-175`; loopback binding configuration is unchanged. Existing state-changing route checks/helpers remain, and no later Doctor mutation route is pre-implemented. |
| Sanitized/bounded Task 2 fixture and output | PASS for the scoped slice | CLI and HTTP use 65,536-byte plus-sentinel reads, strict UTF-8, fixed error DTOs, and no exception/path projection. The synthetic fixture contains no raw/PII/path/credential, and test line 88 proves its local fixture path is absent from HTTP output. General lexical/cross-field/leak-negative validation is explicitly Task 3/Task 7 scope and is not claimed here. |
| Dependency direction and IVT | PASS | The Doctor project has no project/package dependency. Config CLI and Local Monitor reference downward to it. The test project reuses existing pinned test packages. Each upper assembly adds one test-only IVT required for real internal dispatch/host access. |
| Verification interfaces remain compile-shape only | PASS with future obligation | `DoctorVerificationContracts.cs` adds source-neutral records, clock, and store method shapes only. It adds no persistence, lifecycle behavior, source-specific state, fallback, or polling. Task 4 must still prove trusted candidate resolution, atomic evidence acceptance/CAS, failure classification, and restart semantics. |
| No out-of-scope implementation or fix regression | PASS | No remaining 19-state behavior, persistence/schema/migration, lifecycle command/route, source adapter, readiness change, proxy/Razor/JavaScript/Canvas/UI, dependency, heuristic, sleep/retry, or unrelated refactor was added. The fix is limited to the route, test helper/request, and its brief/report. |
| Test reliability, cleanup, and port behavior | PASS | Real Kestrel uses port `0` and the single assigned address. The success path disposes client/host and deletes its unique directory; the corrected failure path disposes any built app and attempts directory deletion under nested `finally`. |
| D051 readiness non-regression | PASS for Task 2 scope | `MonitorHost` readiness evaluation/body/status/threshold code is untouched; the only production-host change is additive route registration. Executable byte-level D051 regression remains Task 7 and is not claimed. |

## Fresh command results

All commands ran from the repository root on the corrected diff.

1. `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorCrossSurfaceContractTests`
   - Exit 0.
   - 1 passed, 0 failed, 0 skipped, 1 total.
   - Runner duration: 408 ms.
   - Informational `NETSDK1057` preview-support-policy messages were emitted;
     they were not warnings or failures.
2. `dotnet build CopilotAgentObservability.slnx`
   - Exit 0.
   - 9 projects built.
   - 0 warnings, 0 errors.
   - Elapsed time: 2.52 seconds.
3. `git diff --check`
   - Exit 0, no output.
4. `git status --short --branch`
   - Exit 0.
   - Branch remained `codex/issue-102-doctor-core`.
   - 7 tracked modified files and 17 actual untracked files (24 porcelain
     entries with `--untracked-files=all`), all attributable to Task 2 and its
     review/fix records.

Additional required checks:

- Whitespace scan covered all 11 untracked `.cs`, `.csproj`, and fixture
  `.json` files: 0 trailing-whitespace findings and 0 missing-final-newline
  findings.
- A tracked-runtime-artifact name scan found only
  `artifacts/dashboard-input/README.md`, an existing committed README rather
  than generated runtime data.
- Status contained 0 untracked runtime-artifact matches; test/build introduced
  no tracked/untracked database, WAL/SHM, log, test-result, `bin`, or `obj`
  artifact into the reviewable change set.

## Explicitly unverified boundaries

The following remain correctly outside Task 2 and are not inferred from this
PASS:

- Tasks 3-8: the other nineteen state behaviors; complete blocker/terminal/
  advisory order; unknown/partial/cross-field/cardinality/duplicate-property
  validation; generalized leak prevention; verification service and trusted
  candidate resolution; CAS/atomicity/concurrency; Doctor SQLite v1 schema and
  historical migration/rollback/restart; lifecycle CLI commands; lifecycle HTTP
  routes and their full security/status/error matrix; full cross-surface and
  D051 hardening; derived documentation and ledger.
- Issue #103 GitHub Copilot fact/candidate producers and live evidence.
- Issue #104 Claude Code fact/candidate producers and live evidence.
- Issue #105 proxy DTO, Razor/JavaScript/Canvas/UI, and live workflow.

Full solution tests and Playwright installation were not required by the exact
Task 2 review/fix briefs and were not run. They remain final Issue #102
validation and are not substituted by the focused test or solution build.
