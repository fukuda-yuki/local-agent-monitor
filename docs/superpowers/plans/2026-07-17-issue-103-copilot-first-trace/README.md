# Issue #103 GitHub Copilot First Trace Execution Contract

> **For agentic workers:** Use `superpowers:subagent-driven-development` and
> `superpowers:test-driven-development`. GitHub Issue #103's
> `Implementation decomposition` and `Acceptance criteria` are the work
> checklist; this file does not replace or restate them.

**Issue:** https://github.com/fukuda-yuki/local-agent-monitor/issues/103

**Goal:** Complete the GitHub Copilot setup-to-verification vertical slice
without changing the frozen `doctor.v1` contract or taking #104, #105, or #106
work.

**Kickoff:** `codex/issue-103-copilot-first-trace` at
`920ff43a9ec63088a9cc109bcd15d0e6f4f9dc5c` in the dedicated Issue #103
worktree.

## Backend / HTTP / proxy / UI contract table

| Boundary | DTO | Producer | Consumer | #103 rule |
| --- | --- | --- | --- | --- |
| Guided setup backend | `SetupCommandResult` / `SetupTargetResult` | `SetupCommandDispatcher`, `SetupStatusService`, and `SetupStatusProjector` using `GitHubCopilotSetupAdapter` | GitHub Copilot Doctor orchestration | Static facts and restart guidance only. Setup success is never real-source evidence. |
| Setup CLI proxy | canonical `setup.v1` bytes | `SetupJson` through `CliApplication` | repository / Release `setup.ps1` byte-faithful wrapper | Source-neutral shared files may change only when required to emit the already-reserved `run_first_trace_doctor` action. No HTTP/UI setup surface is added. |
| Doctor backend | frozen `DoctorFactSnapshot`, `DoctorEvidenceCandidate`, `DoctorResult` | GitHub Copilot fact/candidate adapter calls existing `SqliteDoctorApplicationService.ObserveCandidate`; shared service/evaluator produces the result | Config CLI and Local Monitor HTTP | No new Doctor state, reason, next action, evidence class/kind, precedence, or store rule. |
| Doctor HTTP | the same serialized `doctor.v1`; no HTTP-specific DTO | existing five routes in `DoctorRoutes` and `DoctorJson.SerializeResult` | HTTP caller | No candidate-observation route and no response reshaping. Existing status/security mapping stays frozen. |
| Doctor CLI result projection | the same `DoctorResult` | existing Doctor application | `DoctorJson.SerializeResult` or `DoctorHumanProjector.Project` | Source-specific orchestration must return this same result rather than create a parallel result DTO. |
| Proxy | none | none | none | #105 owns a common Doctor proxy. #103 does not create one. |
| UI | none | none | none | #105 owns Razor/JavaScript/common lifecycle UX. #103 does not create it. |

## Canonical GitHub Copilot Doctor identities

The source adapter validates underlying source-owned provenance, then writes a
single Doctor adapter identity so every candidate in one frozen #102
verification satisfies exact adapter equality.

| Setup target | Doctor `source_surface` | Doctor `source_adapter` | Accepted underlying provenance |
| --- | --- | --- | --- |
| VS Code Copilot Chat | `github-copilot-vscode` | `github-copilot-doctor` | exact selected raw record with `client.kind=vscode-copilot-chat`; optional exact-linked Session surface `vscode` with actual adapter `copilot-compatible-hook` or exact OTel enrichment |
| Copilot CLI | `github-copilot-cli` | `github-copilot-doctor` | exact selected raw record with `client.kind=copilot-cli`; optional exact-linked Session surface `copilot-cli` with actual adapter `copilot-compatible-hook` or exact OTel enrichment |
| caller-managed App/SDK | `github-copilot-app-sdk` | `github-copilot-doctor` | explicit native Session selection on surface `copilot-sdk` / adapter `copilot-sdk-stream`, plus its exact-linked OTel raw record when ingest/persistence/projection are claimed |

The composite capability labels containing `+` are registry descriptions, not
Doctor adapter tokens. Capability declaration alone is never observed evidence.

## Twelve-family producer mapping

| `DoctorFactSnapshot` family | Exact producer mapping | Unknown / negative rule |
| --- | --- | --- |
| `install_and_source_version` | Setup target detection/version and source feature detection; an exact Local Monitor liveness response may establish monitor installed | missing/ambiguous detection remains `unknown`; not-detected is negative only when the target-specific detector actually completed |
| `process_receiver_and_port` | #67 exact loopback `/health/live` classification | refused maps to known no monitor listener; foreign/malformed response maps only to foreign owner; other missing facts remain `unknown` |
| `source_effective_configuration` | selected target's fresh setup status, immutable expected endpoint, and current/reference state | plan/apply projection alone does not prove current alignment; stale/diverged maps mismatch, unavailable remains `unknown` |
| `endpoint_reachability` | exact #67 Local Monitor probe | exact Local Monitor response is reachable; refusal is unreachable; foreign response does not become Monitor reachability success |
| `protocol_and_signal_compatibility` | fresh target member state for the source-specific enabled/protocol members | absence/difference maps only to its exact disabled/mismatch fact; unobservable managed/caller state remains `unknown` |
| `source_version_and_schema_diagnostics` | selected raw record's exact `SourceCompatibilityRow` | setup version support does not invent schema matching; disagreement/missing observation remains `unknown` |
| `last_ingest` | commit-acknowledged raw record or exact rejected-payload record correlated to the verification/explicit selection | no latest/time-window inference; no selection/correlation remains `unknown` rather than accepted |
| `raw_persistence` | the same commit-acknowledged raw record ID in the raw store | accepted transport without its exact durable row is `not_persisted`; absence without an exact receipt remains `unknown` |
| `projection` | exact raw-record join to `monitor_ingestions` plus a new exact per-record projection-disposition seam | the current successful row proves only `completed`; raw-row presence with no projection row is ambiguous and remains `unknown`; `not_started`, `pending`, and `failed` require an exact selected-record disposition and are never inferred from row absence or a global counter |
| `exact_session_binding` | byte-equal native identity or exact trace context joining the selected raw record to Session | required for all three supported partitions; absent exact join is honest `unbound`; repository/workspace/cwd/process/time never bind |
| `completeness_and_content` | selected Session completeness/content plus source compatibility content state and runtime sanitized-only policy | disagreement or absence remains `unknown`; content disabled/unsupported is advisory content state, not first-trace failure by itself |
| `restart_or_new_process` | setup restart requirement plus explicit fresh-process/current-shell inheritance evidence | successful apply/no-op alone cannot set `not_required`; unresolved current-process divergence remains required or `unknown` |

## Execution order and gates

Use Issue #103 sections 103-A through 103-E verbatim. The first executable
change is a cross-surface contract test spanning the setup producer, source
adapter, shared Doctor result, CLI JSON/human projection, and existing HTTP
serialization. Every behavior task follows RED -> GREEN -> focused validation
-> independent review -> local commit.

Parallel implementation is allowed only for disjoint files after the contract
test fixes their interfaces. Shared files, Doctor contracts/store/evaluator,
`MonitorHost.cs`, `SetupCompositionRoot.cs`, specifications, and ledgers have a
single owner at a time.

Migration validation uses the committed historical Monitor v1-v4 SQLite
fixtures and their manifest under
`tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/monitor/`.
It must migrate through Monitor v5 and Doctor v1, close/reopen twice, preserve
sentinels and exact rows, and verify injected migration rollback. No fabricated
Doctor v0 fixture is permitted.

Atomicity, rollback, stale state, candidate acceptance, and concurrent terminal
operations use barriers, injected clocks, checkpoint/fault seams, and SQLite
locks. Tests must not use sleep, polling, or retry loops.

## Validation

Focused commands are named in each delegation. The closeout commands are
exactly:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

The durable record is
`docs/sprints/issue-103-copilot-first-trace/ledger.md`. It records task state,
commit ranges, focused/full results, independent review verdicts, unresolved
items, and unverified cross-Issue interfaces. It distinguishes feature-branch
completion from main integration.
