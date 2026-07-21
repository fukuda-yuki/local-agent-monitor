# Issue #105 Cross-surface Closeout Evidence

This repository-safe record contains no prompts, responses, tool arguments or
results, credentials, authorization values, PII, database contents, runtime
trace identifiers, or machine-specific sensitive paths. Product behavior
remains defined by the canonical requirements and specifications.

Validation date: 2026-07-21. Branch:
`codex/issue-105-cross-surface-closeout`. Starting SHA:
`5180a0424ff5488354a3e173c74b7e931d28679d`. Immutable validation candidate:
`78958f2e10d2c1ba15a324bb8cf44cc96231e026`. The dedicated worktree was clean
when the candidate was frozen and after validation. This evidence-only record
does not replace or mutate that candidate.

## Dependency and ancestry gate

| Dependency | GitHub state on 2026-07-21 | Accepted / evidence revisions | Candidate ancestry | Gate result |
| --- | --- | --- | --- | --- |
| #102 | closed | `1d7822a`; closeout `31d7ec5` | both ancestors | passed |
| #103 | open | production `8dd751d`; live `f67e1a0`; closeout `916484d` | all ancestors | blocked: Issue completion state is not satisfied |
| #104 | open | frozen product `54d758a260f347cc31a3191d342ad509eb62d81f`; integration `70206cec` | both ancestors | blocked: Issue completion state is not satisfied |
| #106 | closed | evidence `87c240ad`; integration `b4e59a59`; closeout `2378228d` | all ancestors | passed |

The candidate also contains #99 revision `09e0bd76` and #110 live-evidence
revision `502acd9a`. Every ancestry check used
`git merge-base --is-ancestor <revision> 78958f2e...` and returned exit `0`.
The #105 acceptance criterion requiring #102, #103, and #104 to be complete is
therefore **blocked** by the external Issue states of #103 and #104, despite
their integrated revisions being present.

## Execution envelope

| Field | Recorded value |
| --- | --- |
| Build and package OS | Windows x64; repository worktree and disposable package output, not a clean user profile |
| Doctor schemas | result `doctor.v1`; facts `doctor.facts.v1`; shared UI `doctor.ui.v1` |
| Fixed source/application/adapter identities | `github-copilot-vscode` / `github-copilot-doctor` / `vscode-chat`; `github-copilot-cli` / `github-copilot-doctor` / `cli`; `github-copilot-app-sdk` / `github-copilot-doctor` / `app-sdk`; `claude-code` / `claude-code-otel` / `interactive-cli`, `print`, or `agent-sdk` |
| Inventoried source versions | GitHub Copilot CLI 1.0.71; Claude Code 2.1.215; packaged Copilot CLI 1.0.65 |
| Safe settings labels | loopback OTLP boundary; content disabled for reused Copilot evidence; explicit-content, gate-disabled, Hook, restart, and sanitized-only labels are retained only in the cited Claude evidence records |
| New live opaque refs | none; no candidate live run occurred |
| Reused opaque refs | retained in the cited #103/#106/#110 repository-safe records and not duplicated here |
| Automated state transitions | fixture-specific expected states and lifecycle transitions matched actual canonical Doctor results in the passing source, route, and Playwright matrices |
| Live state transitions | not observed on this candidate; classified `blocked` below |

## Delivered integration

- Canonical cross-surface Doctor contract, fixed source registry and detection
  authority, shared state semantics, exact evidence navigation, and security
  boundaries.
- GitHub Copilot and Claude Code source adapters composed through one Doctor
  result contract without source-specific state enums or heuristic selection.
- Shared Local Monitor Doctor workflow, source selection, exact Session/trace/
  source-diagnostics links, lifecycle operations, rollback refresh, keyboard
  flow, and fail-closed API handling.
- Durable exact-navigation storage and authoritative use of
  `doctor.evaluation.states[].evidence_refs`; candidate/preview references never
  become navigation authority.
- Release `first-trace.ps1` wrapper and Windows x64 ZIP packaging coverage.
- Deterministic source, route, store, UI, Playwright, security-negative,
  accessibility, and release-boundary tests using synthetic data.

Implementation commits from the starting SHA through the candidate are:
`3de3bb86`, `bd93d15c`, `381678ea`, `d25d47e1`, `84fe1bda`, `314dfdd4`,
`175ac431`, `ce2105ae`, `65cb1b3`, and `78958f2e`.

## Automated matrix

| Issue matrix area | Classification | Candidate-bound evidence |
| --- | --- | --- |
| Source selector / no sources | passed | deterministic registry and Playwright cases |
| ready-no-real-trace | passed | shared Doctor route and UI cases |
| running / expired / cancelled lifecycle | passed | route, exact-refresh, and Playwright cases |
| first-trace-ready evidence links | passed | exact navigation store, routes, and UI cases |
| receiver/process errors | passed | route and Playwright error cases |
| protocol/signal mismatch | passed | source/integration matrix |
| schema drift / unsupported | passed | source/integration and UI matrix |
| projection pending / failed | passed | source/integration and UI matrix |
| unbound / content-disabled / sanitized-only | passed | shared state and security-negative matrix |
| stale / rollback state | passed | deterministic rollback and exact-GET refresh cases |
| source switch preserving independent facts | passed | source-lock and in-flight identity Playwright cases |
| API failure and retry | passed | fail-closed begin and loss-recovery cases |
| keyboard-only flow | passed | Playwright accessibility case |
| raw / secret / PII / path negative checks | passed | route, UI, script, and evidence hygiene checks |
| CLI JSON / human summary / UI projection | passed | one canonical Doctor result contract and projection tests |
| GitHub VS Code Chat source contract | passed | synthetic source registry/adapter coverage; no candidate live run claimed |
| GitHub Copilot CLI source contract | passed | synthetic coverage plus bounded compatible historical source evidence; no candidate shared-UI live run claimed |
| GitHub App/SDK caller-managed state | passed | deterministic caller-managed state coverage; no provider execution claimed |
| Claude interactive CLI source contract | passed | synthetic coverage; historical producer evidence is bounded below |
| Claude `-p` source contract | passed | synthetic coverage; compatible historical producer evidence is bounded below |
| Claude Agent SDK availability/caller-managed state | passed | deterministic caller-managed state coverage; no SDK execution claimed |

No automated row is classified `failed`, `blocked`, or `not-applicable` at the
candidate. Earlier red tests and an output-directory collision during an
in-repository packaging diagnostic were corrected or superseded; they are not
reported as successful runs. The supported external disposable-output package
run below passed.

## Required validation

All three commands ran from the repository root against the unchanged
candidate, in the required order.

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | passed; 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | passed |
| `dotnet test CopilotAgentObservability.slnx` | passed; Doctor 266/266, ConfigCli 4,268/4,268, LocalMonitor 2,014/2,014; total 6,548/6,548; 0 failed, 0 skipped |

Focused candidate validation also passed: source registry 9/9; Config first
trace 54/54; GitHub evidence 59/59; exact navigation/Claude observer 13/13;
Doctor UI Playwright 17/17; exact evidence routes 12/12; Local Monitor script
tests 31/31; and the earlier combined Doctor/Monitor UI set 37/37.
`node --check`, `git diff --check`, and the final clean-worktree check passed.

## Candidate release artifact

`scripts\local-monitor\package-release.ps1` completed against the immutable
candidate using a new disposable output directory outside the repository.

| Field | Value |
| --- | --- |
| Artifact | `local-monitor-win-x64.zip` |
| SHA-256 | `111A288DFBFBBDEE4FB0ABBDEC3DE26103638F905CB4A49258F9AB4BDADAA71B` |
| Size | 174,522,586 bytes |
| Staging files | 765 |
| Classification | passed for package construction only |

The ZIP was not installed or exercised in a clean Windows user profile. Its
hash therefore proves package identity, not the clean-machine journey.

## Historical live-evidence compatibility

| Source | Reused evidence | Compatibility basis | Reused coverage | Unverified gap |
| --- | --- | --- | --- | --- |
| GitHub Copilot CLI | #103 `f67e1a0`, CLI 1.0.71 | revision is ancestral; currently inventoried CLI version matches; content disabled and loopback boundary match | exact accepted ingest, raw persistence, completed projection, honest unbound state | candidate shared Doctor UI/navigation, Release ZIP install, rollback/uninstall, clean-user journey |
| Claude Code | #106/#110, frozen #104 candidate and Claude 2.1.214 | product/evidence revisions are ancestral; pinned settings and disposable loopback boundary are recorded | producer ingestion/content gates, exact binding, restart/reconnect, sanitized-only behavior | current installed Claude is 2.1.215, so no candidate/version-compatible shared Doctor live journey is claimed |
| Claude Code older run | #99 `09e0bd76`, Claude 2.1.207 | ancestral historical context only; source version/settings and later implementation differ | schema-drift history only | all candidate closeout claims |

Historical evidence is not used to claim the final common UI, exact navigation,
Release ZIP, clean-user, or rollback/uninstall journey. No new real-source live
run was executed in this closeout because the required disposable Windows-user
boundary and source/operator authorization were unavailable.

## Live and external matrix

Every unexecuted required live capability is explicitly classified; none is
converted to `passed`.

| Required row | Classification | Severity and exact blocker | Retry condition / unverified capability |
| --- | --- | --- | --- |
| Release ZIP clean-machine setup-to-first-trace happy path | blocked | P0: `disposable_windows_user_and_operator_source_authorization_unavailable` | provide a disposable Windows user/VM and authorize one supported source; verify install, launch, guided setup, restart, real trace, exact navigation, rollback, uninstall |
| GitHub Copilot supported happy-path run on candidate | blocked | P0: same clean-user and operator authorization boundary | authenticate/authorize VS Code Chat or Copilot CLI in the disposable boundary and run against exact candidate ZIP |
| Claude supported happy-path run on candidate | blocked | P0: current CLI is 2.1.215 while reusable evidence is 2.1.214, and no candidate run was authorized | pin and record 2.1.215 (or an explicitly accepted version), authorize the run, and execute the shared journey against the candidate ZIP |
| Exact Session/trace/source navigation from a fresh real record | blocked | P0: no candidate-bound real record exists | complete either candidate happy path and follow only authoritative evidence refs |
| Rollback/uninstall live consistency | blocked | P0: no disposable installed candidate and authorized source run | complete setup, rollback and uninstall in the disposable boundary; re-read exact Doctor state and tool-owned configuration |
| GitHub restart-required live behavior | blocked | P1: no candidate-bound source restart run | authorize source restart in disposable boundary and retain exact pre/post evidence identity |
| GitHub receiver-down/projection failure/unbound live cases | blocked | P1: destructive/error live matrix was not authorized | execute bounded disposable negative cases without converting unavailable cases to pass |
| Claude Hook/OTel unavailable, drift, parentage, content, restart live gaps on candidate | blocked | P1: reused evidence predates the candidate/shared journey and current source version differs | rerun only incompatible gaps with pinned version/settings and candidate identity |
| Genuine attended Claude interactive TTY on candidate | blocked | P1: attended TTY/operator authorization unavailable | run an attended, explicitly authorized interaction in the disposable boundary |
| GitHub App/SDK provider execution | blocked | P1: provider package/credentials/caller application unavailable | provide caller application, accepted package/version and credentials; verify caller-managed state without storing sensitive values |
| Claude Agent SDK provider execution | blocked | P1: SDK package/credentials/caller application unavailable | same caller-managed retry boundary for an accepted Claude Agent SDK version |

`failed`: none. `not-applicable`: none. `not-attempted` without a blocker:
none. The rows above are unverified and remain `blocked` until their retry
conditions are met.

## Review and decision

Independent final reviews covered (1) Issue/specification and matrix coverage,
(2) raw/secret/PII/path safety and raw-boundary invariants, and (3) candidate
SHA/evidence compatibility/classification/test integrity. Findings corrected
before the candidate included detection authority, `no-store` evidence routes,
mixed ownership, candidate-reference navigation, Session DTO scope, lifecycle
loss recovery, source switching, and in-flight verification identity. Targeted
re-review of the final candidate reported no implementation or security
blocker. Primary coordination then reran focused tests, the complete required
suite, ancestry checks, clean/diff checks, and package construction.

Release decision: **BLOCKED**. The immutable candidate has no known severe
implementation defect and its automated suite passes, but #103 and #104 remain
open and the P0 clean-machine, candidate-bound live journey is unverified.
Issue #105 does not satisfy its close condition. Consequently #69 does not
satisfy its four-child close condition. This evidence location is the intended
#60/#91 handoff, but #91 must retain the dependency gate and must not treat this
record as a final live pass. Issue #57 closeout must not begin.
