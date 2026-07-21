# Issue #105 Cross-surface Closeout Evidence

This repository-safe record contains no prompts, responses, tool arguments or
results, credentials, authorization values, PII, database contents, or
machine-specific sensitive paths. The only runtime identifiers retained below
are repository-safe opaque verification, evidence, trace, and observation
references required to reproduce the candidate-bound navigation result.
Product behavior remains defined by the canonical requirements and
specifications.

Validation date: 2026-07-21. Branch:
`codex/issue-105-cross-surface-closeout`. Starting SHA:
`5180a0424ff5488354a3e173c74b7e931d28679d`. The first immutable candidate was
`78958f2e10d2c1ba15a324bb8cf44cc96231e026`; its failed live results remain
below as historical evidence. The current replacement validation candidate is
`b581be9864c284d27884e94208e92138b3e83040`. Each dedicated-candidate worktree
was clean when its candidate was frozen and after validation. Evidence-only
commits do not replace or mutate either candidate.

## Dependency and ancestry gate

| Dependency | GitHub state on 2026-07-21 | Accepted / evidence revisions | Candidate ancestry | Gate result |
| --- | --- | --- | --- | --- |
| #102 | closed | `1d7822a`; closeout `31d7ec5` | both ancestors | passed |
| #103 | closed on 2026-07-21 | production `8dd751d`; live `f67e1a0`; closeout `916484d` | all ancestors | passed |
| #104 | closed on 2026-07-21 | frozen product `54d758a260f347cc31a3191d342ad509eb62d81f`; integration `70206cec` | both ancestors | passed |
| #106 | closed | evidence `87c240ad`; integration `b4e59a59`; closeout `2378228d` | all ancestors | passed |

The candidate also contains #99 revision `09e0bd76` and #110 live-evidence
revision `502acd9a`. Every ancestry check used
`git merge-base --is-ancestor <revision> 78958f2e...` and returned exit `0`.
The #105 acceptance criterion requiring #102, #103, and #104 to be complete is
now **passed**. The primary coordinator independently audited every #103 and
#104 acceptance criterion and ancestry chain before closing those Issues with
repository-safe evidence comments.

## First-candidate execution envelope (`78958f2e...` only)

| Field | Recorded value |
| --- | --- |
| Build and package OS | Windows x64; repository worktree and disposable package output, not a clean user profile |
| Doctor schemas | result `doctor.v1`; facts `doctor.facts.v1`; shared UI `doctor.ui.v1` |
| Fixed source/application/adapter identities | `github-copilot-vscode` / `github-copilot-doctor` / `vscode-chat`; `github-copilot-cli` / `github-copilot-doctor` / `cli`; `github-copilot-app-sdk` / `github-copilot-doctor` / `app-sdk`; `claude-code` / `claude-code-otel` / `interactive-cli`, `print`, or `agent-sdk` |
| Inventoried source versions | GitHub Copilot CLI 1.0.71; Claude Code 2.1.215; packaged Copilot CLI 1.0.65 |
| Safe settings labels | loopback OTLP boundary; content disabled for reused Copilot evidence; explicit-content, gate-disabled, Hook, restart, and sanitized-only labels are retained only in the cited Claude evidence records |
| New live opaque refs | one candidate verification was created and cancelled; its opaque ID is intentionally not duplicated here |
| Reused opaque refs | retained in the cited #103/#106/#110 repository-safe records and not duplicated here |
| Automated state transitions | fixture-specific expected states and lifecycle transitions matched actual canonical Doctor results in the passing source, route, and Playwright matrices |
| Live state transitions | install/ready/plan/apply/begin passed; real trace arrived but candidate promotion failed; cancel/rollback passed; uninstall failed |

## Delivered integration

- Canonical cross-surface Doctor contract, fixed source registry and detection
  authority, shared state semantics, exact evidence navigation, and security
  boundaries.
- GitHub Copilot and Claude Code source adapters composed through one Doctor
  result contract without source-specific state enums or heuristic selection.
- Shared Local Monitor Doctor workflow, source selection, exact Session/trace/
  source-diagnostics links, lifecycle operations, rollback refresh, keyboard
  flow, and fail-closed API handling.
- Durable exact-navigation storage. Active verifications use returned
  `doctor.evaluation.states[].evidence_refs`; completed verifications use the
  persisted accepted evidence refs. Candidate/preview references never become
  navigation authority.
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

The ZIP was later installed and exercised in the current Windows user context
under the operator's explicit boundary waiver. The run found the product
defects recorded below; package identity remains tied to this hash.

## Historical live-evidence compatibility

| Source | Reused evidence | Compatibility basis | Reused coverage | Unverified gap |
| --- | --- | --- | --- | --- |
| GitHub Copilot CLI | #103 `f67e1a0`, CLI 1.0.71 | revision is ancestral; currently inventoried CLI version matches; content disabled and loopback boundary match | exact accepted ingest, raw persistence, completed projection, honest unbound state | candidate shared Doctor UI/navigation, Release ZIP install, rollback/uninstall, clean-user journey |
| Claude Code | #106/#110, frozen #104 candidate and Claude 2.1.214 | product/evidence revisions are ancestral; pinned settings and disposable loopback boundary are recorded | producer ingestion/content gates, exact binding, restart/reconnect, sanitized-only behavior | current installed Claude is 2.1.215, so no candidate/version-compatible shared Doctor live journey is claimed |
| Claude Code older run | #99 `09e0bd76`, Claude 2.1.207 | ancestral historical context only; source version/settings and later implementation differ | schema-drift history only | all candidate closeout claims |

Historical evidence is not used to claim the final common UI, exact navigation,
Release ZIP, or rollback/uninstall journey. A new candidate-bound GitHub
Copilot CLI run was executed after the operator explicitly accepted the current
Windows user as the validation boundary. Its failed results supersede the
previous external-boundary blocker for the affected rows.

### 2026-07-21 pre-waiver clean-user boundary diagnostic

The operator subsequently authorized GitHub Copilot CLI use. The candidate ZIP
hash was reverified, extracted to OS-temporary storage, and its published
monitor was started with a disposable database and non-default loopback port.
`/health/live` and `/health/ready` both returned `200` with ready state
`ready`. `first-trace begin` then failed closed with
`first_trace_doctor_failed` because no authoritative applied setup ledger
existed in that user context. Copilot CLI was not executed and no source prompt
or response was produced or retained. Process-scoped environment labels were
not accepted as a substitute for guided setup authority.

The current Windows user already owns Local Monitor state and is therefore not
a conforming clean/disposable user. The existing dedicated
`CodexSandboxOnline` account has no active logon session, reusable token,
scheduled task, service, or credential-free execution broker. Windows Sandbox
and Hyper-V are not installed, and enabling either requires administrator
action. The temporary monitor was stopped, its loopback port was released, and
all temporary extracted files, database, and logs were moved to the recycle
bin. Existing user state was not changed.

This diagnostic applied the Issue body's original clean/disposable-user rule.
The operator's later explicit instruction accepted the current Windows user as
the #105 validation boundary. Per the repository source-of-truth order, that
latest user instruction supersedes the clean-user restriction for the run
below; no separate clean-machine blocker is carried forward.

### 2026-07-21 current-user candidate live run

The operator explicitly waived the clean/disposable-user requirement and
authorized GitHub Copilot CLI use. Existing Local Monitor runtime material was
moved to a recoverable sibling location before the run. Existing user-scoped
Copilot OTel values were represented only by SHA-256 equality checks and were
restored byte-equivalently after rollback.

| Step | Bounded actual result | Classification |
| --- | --- | --- |
| Candidate artifact | SHA-256 matched `111A288DFBFBBDEE4FB0ABBDEC3DE26103638F905CB4A49258F9AB4BDADAA71B` | passed |
| Install and published start | install exit `0`; live `200`; ready `200` / `ready` | passed |
| Guided setup | redacted plan `plan_ready`; explicit apply `apply_succeeded`; status `status_ready`; fresh-process values matched the canonical loopback settings | passed |
| Verification start | `first_trace_verification_started`, revision 1 | passed |
| Real source | GitHub Copilot CLI 1.0.71, authenticated, bounded no-tool interaction exit `0`; content capture disabled | passed |
| Ingest and projection | 7 ingestion records and 1 sanitized trace appeared | passed |
| Candidate promotion | trace classified `source_surface=raw-otlp`, `source_adapter=raw-otlp`, `compatibility_state=schema_drift_detected`; three exact status reads returned 0 candidates and `agent_restart_required` | failed |
| Exact navigation | no authoritative candidate/evidence navigation target could be created | failed as downstream consequence of candidate-promotion defect |
| Cancel and rollback | exact cancel exit `0`, revision advanced to 2; rollback exit `0`; all four managed user-environment labels restored to their pre-run presence/value hashes | passed |
| Post-rollback read | exact status exit `0`, 0 candidates, preview `protocol_mismatch` | passed as an honest non-ready result |
| Uninstall | internal `stop_timeout` was emitted, but the wrapper continued to print `uninstalled` and returned exit `0`; listener and installed executable remained | failed |
| Cleanup/restoration | remaining candidate-owned PID was verified by executable path and stopped; candidate runtime and temporary raw-bearing output were moved to the recycle bin; prior Local Monitor `logs/setup` state was restored; port and task were absent | passed as cleanup only; not an uninstall pass |

No prompt, response, raw payload, credential, authorization value, database,
log body, or sensitive path is retained in this evidence. The candidate
promotion failure belongs to the GitHub Copilot first-trace evidence observer /
source-compatibility integration. The uninstall failure belongs to
`scripts/local-monitor/stop.ps1` and
`scripts/local-monitor/uninstall-startup-task.ps1`. No existing open Issue was
found that owns either exact defect; separate fix Issues are required before a
replacement #105 candidate can be frozen. The defects are not repaired inside
this validation path.

## Live and external matrix

Every required live capability is explicitly classified; none is converted to
`passed` after an incomplete or failed execution.

| Required row | Classification | Severity and exact blocker | Retry condition / unverified capability |
| --- | --- | --- | --- |
| Release ZIP operator-accepted current-user setup-to-first-trace happy path | failed | P0: candidate-promotion and uninstall product defects above | fix both owning components, freeze a replacement candidate, and rerun the complete journey |
| GitHub Copilot supported happy-path run on candidate | failed | P0: real trace arrived but remained `raw-otlp/schema_drift_detected`; 0 exact candidates | fix the candidate observer/source-compatibility integration and rerun against a replacement candidate |
| Claude supported happy-path run on candidate | blocked | P0: current CLI is 2.1.215 while reusable evidence is 2.1.214, and no candidate run was authorized | pin and record 2.1.215 (or an explicitly accepted version), authorize the run, and execute the shared journey against the candidate ZIP |
| Exact Session/trace/source navigation from a fresh real record | failed | P0: a fresh real record exists, but no authoritative candidate/navigation target was created | fix candidate promotion and follow only replacement-candidate server-provided targets |
| Rollback/uninstall live consistency | failed | P0: rollback restored all tracked values, but uninstall reported success after `stop_timeout` and left the process/app installed | make uninstall fail closed or complete removal, then rerun exact rollback/uninstall |
| GitHub restart-required live behavior | failed | P1: Copilot ran as a fresh process with applied values and emitted telemetry, but Doctor remained `agent_restart_required` because no candidate was promoted | fix candidate promotion and prove the replacement candidate reaches the expected post-restart state |
| GitHub receiver-down/projection failure/unbound live cases | blocked | P1: destructive/error live matrix was not authorized | execute bounded disposable negative cases without converting unavailable cases to pass |
| Claude Hook/OTel unavailable, drift, parentage, content, restart live gaps on candidate | blocked | P1: reused evidence predates the candidate/shared journey and current source version differs | rerun only incompatible gaps with pinned version/settings and candidate identity |
| Genuine attended Claude interactive TTY on candidate | blocked | P1: attended TTY/operator authorization unavailable | run an attended, explicitly authorized interaction in the disposable boundary |
| GitHub App/SDK provider execution | blocked | P1: provider package/credentials/caller application unavailable | provide caller application, accepted package/version and credentials; verify caller-managed state without storing sensitive values |
| Claude Agent SDK provider execution | blocked | P1: SDK package/credentials/caller application unavailable | same caller-managed retry boundary for an accepted Claude Agent SDK version |

`failed`: Release ZIP happy path, GitHub candidate happy path, exact navigation,
and rollback/uninstall consistency. `not-applicable`: none. `not-attempted`
without a blocker: none. Remaining provider-dependent rows stay `blocked` until
their retry conditions are met.

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

Release decision: **BLOCKED**. The automated suite passes and #103/#104 are
closed, but the current candidate has two P0 live product defects: GitHub
Copilot candidate promotion/exact navigation fails after a real trace arrives,
and uninstall reports success after `stop_timeout` while leaving the process
and installed application present.
Issue #105 does not satisfy its close condition. Consequently #69 does not
satisfy its four-child close condition. This evidence location is the intended
#60/#91 handoff, but #91 must retain the dependency gate and must not treat this
record as a final live pass. Issue #57 closeout must not begin.

## Replacement candidate closeout (supersedes the blocked decision above)

The remainder of this section is the current decision for Issue #105. It
supersedes the earlier `78958f2e...` live classifications without deleting
their failure evidence. The replacement candidate is
`b581be9864c284d27884e94208e92138b3e83040` on
`codex/issue-105-cross-surface-closeout`.

### Fix integration and ancestry

The replacement contains the fail-closed uninstall correction, exact Copilot
candidate promotion, finite-ledger-history authority selection, completed
accepted-evidence navigation, and deterministic Release ZIP validation. Their
integrated revisions are `17dac4c1`, `f4b2ddad`, `df308d09`, `5208fe5c`,
`0772d0fc`, and `b581be98` respectively.

The accepted #102 revisions `1d7822a` / `31d7ec5`; #103 production, live, and
closeout revisions `8dd751d`, `f67e1a0`, and `916484d`; #104 product, handoff,
and integration revisions `54d758a260f347cc31a3191d342ad509eb62d81f`,
`95d2985`, and `70206cec`; and #106 evidence revisions `87c240ad`, `b4e59a59`,
and `2378228d` are all ancestors of the replacement candidate. The compatible
#99 / #110 revisions `09e0bd76`, `502acd9`, and `11d6c58` are also ancestors.
Every `git merge-base --is-ancestor <revision> b581be9...` check returned exit
`0`.

### Required validation and artifact identity

The candidate remained unchanged for all commands. The shared-skill mirror
check passed first. The repository-required commands then produced:

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | passed; 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | passed; exit 0 |
| `dotnet test CopilotAgentObservability.slnx` | passed; Doctor 266/266, ConfigCli 4,282/4,282, LocalMonitor 2,027/2,027; total 6,575/6,575; 0 failed, 0 skipped |

Focused replacement validation passed: GitHub source/Doctor 179/179; completed
Doctor UI 15/15; Release script 35/35; and the previously failing Claude
repository/release parity case 1/1. The package validation completed without a
reusable MSBuild node remaining.

The Windows x64 package was generated outside the repository from the exact
candidate. `local-monitor-win-x64.zip` has SHA-256
`F5872519EA58850D35022D5AF2EF9407E75786CB5E2ABE9CCA4390FB075234A9`, size
174,536,743 bytes, 767 entries, and exactly one root manifest.

### Live boundary and journey

The operator explicitly accepted the current Windows user instead of a clean
user. Pre-existing runtime state was moved to a recoverable sibling location;
four managed user-environment values were represented only by presence and
SHA-256 equality, never by their values. The source was GitHub Copilot CLI
1.0.71 with content capture disabled. One harmless synthetic, no-tool
interaction ran in a fresh process. Its output bodies were not read or
retained; the temporary files were moved to the recycle bin.

| Step | Bounded result | Classification |
| --- | --- | --- |
| Install / published start | install exit 0; candidate version matched; live 200; ready 200 / `ready` | passed |
| Guided setup | one redacted `plan_ready`; explicit `apply_succeeded`; the sole status row was applied/current with all targets current/desired | passed |
| Verification begin | `first_trace_verification_started`, active revision 1 | passed |
| Real source execution | Copilot CLI 1.0.71 fresh process, no-tool synthetic interaction, exit 0 | passed |
| Candidate promotion | four exact real candidates: ingest, raw persistence, projection, completeness/content | passed |
| Explicit completion | all four refs explicitly selected; `first_trace_completed`, completed revision 2, four accepted refs | passed |
| Completed navigation | `doctor.ui.v1` returned 200/no-store with four trace and four source-diagnostic targets; both unique server-provided destinations returned 200/no-store | passed |
| Rollback | exact change set returned `rollback_succeeded`; all four managed values matched pre-run presence/hash | passed |
| Uninstall | `stopped` then `uninstalled`, exit 0; candidate PID gone, port released, app absent, task absent | passed |
| Restoration | retained candidate runtime data moved to recycle bin; prior runtime restored; no listener or task remained | passed cleanup |

The repository-safe opaque identity set for this replacement run is:

| Identity | Candidate-bound value / result |
| --- | --- |
| Verification | `019f8400-80e4-7816-bca4-ca7b04d43963` |
| Accepted ingest evidence | `gc_doctor_a83319b8_622cf48e_89ac4201_5e7c1386_f3371a8e` |
| Accepted raw-persistence evidence | `gc_doctor_b3509ea4_2e1925ec_83b2a6cc_d28809f4_2806f7dd` |
| Accepted completeness/content evidence | `gc_doctor_445ccdc4_48bb6a75_b32f79b2_65daf704_54de8b05` |
| Accepted projection evidence | `gc_doctor_7e1cc8b5_a8d4e5c7_1a9e38fa_9c996a84_b8b290ee` |
| Trace navigation target | `1df2a154a032e835d9cdffa072eddcf3`; all four accepted refs resolved to this exact target |
| Source-diagnostic observation target | `019f8401-874f-7c5f-b7a9-a14aafe7061e`; all four accepted refs resolved to this exact target |
| Session navigation target | not applicable / unbound for this source run; the server returned no Session target and no Session pass is claimed |

These values were read after cleanup from only the sanitized
`doctor_verification_evidence` and `first_trace_evidence_navigation` rows for
the exact verification. No raw event, prompt, response, tool, or authorization
table or value was queried. The candidate runtime database remained outside
the repository in a recoverable recycle-bin location.

The earlier finite-history failure was reproduced only after multiple plan rows
had accumulated. The replacement selects the sole exact applied/current
authority independent of history order and fails closed when more than one
current authority exists. Deterministic regressions passed, and the live run
proved the normal single-plan path.

### Reused evidence compatibility

- #103 GitHub Copilot CLI evidence remains compatible by source version,
  loopback/content-disabled settings, and ancestral revisions. The replacement
  live run closes its prior Release ZIP, shared UI/navigation, rollback, and
  uninstall gaps.
- #106/#110 Claude Code 2.1.214 evidence remains compatible for its exact
  content, Hook+OTel binding, restart/reconnect, sanitized-only, and interactive
  producer scopes because the frozen #104 product and later correction
  revisions are ancestors and those producer/settings contracts did not change
  in the replacement. No result is extended to a different source version or
  unrecorded setting.
- #99 remains historical schema-drift context only; it is not used to replace
  the newer #106/#110 evidence.

No required #105 row remains failed, blocked without an exact reusable-evidence
basis, not-attempted, or unclassified. GitHub App/SDK and Claude Agent SDK are
caller-managed contract surfaces; provider execution is not applicable to the
managed Release ZIP happy path and is covered by deterministic caller-managed
state tests rather than being claimed as a live provider pass.

### Replacement decision

Independent implementation reviews for #113/#114/#115 were inspected by the
primary coordinator before integration. The final #105 review wave is recorded
after this evidence commit; any blocking finding will require a new candidate
and a replacement section rather than changing `b581be9...`.

Issues #113, #114, and #115 remain open on GitHub. Their corrections are
integrated and validated in this candidate, but this record does not claim
that their formal GitHub close state has been satisfied and performs no close
action for them.

Current Issue #105 decision: **release-ready for the #105 cross-surface scope**.
The replacement candidate satisfies the Issue #105 technical close condition
under the operator-approved current-user boundary. The Issue remains open and
no GitHub close action is performed here. Issue #69 cannot satisfy its formal
four-child close condition until #105 is closed. Issue #91 must independently
apply its dependency gate; this #105 decision does not create a #91
`final_validation_sha` or authorize Issue #57 closeout.
