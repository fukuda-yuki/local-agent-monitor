# Issue #104 → #106 handoff manifest

Status: the Issue #104 product candidate is frozen for live-validation handoff.
The feature branch closeout is complete; Terra high final re-review #3 approved
this documentation-only closeout. This document is the finalized T9 closeout
and #106 handoff manifest. It does not claim main integration or live Claude
producer validation.

## Frozen lineage

| Item | Frozen value |
| --- | --- |
| Kickoff | `main@920ff43` |
| Product candidate SHA | `54d758a260f347cc31a3191d342ad509eb62d81f` |
| Branch | `claude/issue-104-implementation-447584` |
| Worktree | `.claude/worktrees/issue-104-implementation-447584` |
| Monitor schema revision | `monitor 6` |
| Session schema revision | `session 12` |
| Post-freeze product changes | None permitted; only this documentation closeout is being prepared |

The candidate SHA is the product/test boundary for #106. Any product or test
change after this SHA requires a new candidate SHA and an explicit handoff
replacement; it is not part of this manifest.

## Major implementation checkpoints

| Area | Checkpoint commits |
| --- | --- |
| Normative interface, contract table, and review resolutions | `b43ed58`, `f012db9`, `328e29d`, `939e6f4`, `5628323`, `400a917`, `8d6c669`, `f333d94` |
| Doctor store reads and atomic exclusive start | `07f44c1`, `cb2db6f`, `7b0be26` |
| Claude fact mapper and collector | `b6e5bbf`, `d21fbb4`, `01f0e4a`, `9a78afe`, `079686e` |
| Claude exact binding, observer, and monitor worker | `6270a83`, `1ed3e12`, `eb00ac5` |
| `first-trace` orchestration, Claude adapter, and setup handoff | `e1a4a56`, `f640812`, `a83334f`, `521b7d6` |
| #109 exact-binding projection repair and migration | `09978b8`, `c807c7a`, `8f4b6b7`, `d062f48` |
| Cross-surface acceptance, T8 matrix, and final zero-candidate/privacy repairs | `3700d6b`, `a1e5db2`, `3576421`, `5799958`, `a4ff479`, `91e4802`, `5838cc4`, `6de19b6`, `66b7bff`, `54d758a` |

## Source version boundary

The Claude Code setup and first-trace contract accepts exactly one normal
`MAJOR.MINOR.PATCH (Claude Code)` version at `2.1.207` or newer. Older,
prerelease/build-suffixed, malformed, additional-output, failed, timed-out, or
missing-version observations remain unsupported or not installed according to
the setup contract. #106 must record the live producer version used; this
branch does not claim that a live producer version was observed.

The first-trace identity is `source_surface=claude-code` with expected Doctor
adapter `claude-code-otel` for all three execution surfaces. Hook traffic keeps
the Session-store adapter `claude-code-hook` and does not produce Doctor
candidates.

## Three-surface settings matrix

| Surface | Invocation/guidance | Setup ownership | Settings contract at the candidate boundary | Live execution owner |
| --- | --- | --- | --- | --- |
| Interactive CLI | New shell, run `claude`, send one bounded prompt, then exit. | `cli` target; Claude user settings and approved Hook entries are managed by the guided setup transaction. | Default owned telemetry/routing settings are `CLAUDE_CODE_ENABLE_TELEMETRY=1`, `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1`, `OTEL_TRACES_EXPORTER=otlp`, `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf`, and the canonical monitor traces endpoint. OTel content gates remain unchanged unless explicitly requested; the #110 run requires `OTEL_LOG_USER_PROMPTS` off. | #106 owns the live run. |
| `claude -p` | New shell, run one bounded `claude -p` interaction, then allow telemetry to flush. | Same `cli` target and shared Claude user settings as the interactive CLI. | The same telemetry/routing settings and content-gate boundary as the interactive CLI; the #110 run requires `OTEL_LOG_USER_PROMPTS` off. | #106 owns the live run. |
| Caller-managed Agent SDK | Use the fixed Python/TypeScript guidance; merge with the existing process environment and flush telemetry before a short-lived process exits. | `app-sdk` guidance only; no caller-owned file, environment, backup, or rollback mutation. | The caller supplies the same `claude-code` source identity and `claude-code-otel` expected adapter context. `first-trace` does not edit caller configuration or claim a live SDK result. | Caller-managed; outside the #110 two-prompt input set. |

The three surfaces share the same first-trace verification identity. The
surface distinction is guidance and setup ownership, not a new adapter token.

## Required #110 live input conditions

#106 owns execution of the live producer check under these exact conditions:

- telemetry on;
- `OTEL_LOG_USER_PROMPTS` off;
- one interactive prompt;
- one `claude -p` prompt;
- for each raw export, record whether the `user_prompt` key is present.

Record only repository-safe metadata and the presence/absence result in the
#106 evidence. Do not place raw prompts, responses, tool arguments/results,
credentials, authorization values, PII, or local sensitive paths in this
repository or in the handoff.

The live producer execution and the `user_prompt` key result belong to #106.
Issue #104 completion on this branch does not claim that either live
interaction succeeded, that a raw export was received, or that the key was
present or absent.

## Execution boundary and remaining open interfaces

Issue #104 owns the guided setup handoff, source-neutral `first-trace` CLI
orchestration, Claude fact collection, candidate observer, exact-binding
firewall, deterministic cross-surface fixture evidence, T8 matrix, and the
frozen product candidate. The candidate was validated with synthetic/local
fixtures and is not a live Claude producer result.

The following remain outside the frozen #104 claim:

- #106 live Claude producer execution and #110 `user_prompt` key observation;
- main integration of the feature branch;
- the future provenance-bearing complete trace-context DTO needed for
  byte-equivalent Hook/OTel trace-context binding in Session v1. Trace-id-only
  continuity remains non-binding;
- any new product/test change after the frozen SHA, which requires a new
  candidate and a replacement handoff.

## Validation evidence at the frozen SHA

- `dotnet build CopilotAgentObservability.slnx`: PASS, 0 warnings, 0 errors.
- `pwsh scripts\test\install-playwright-chromium.ps1`: PASS.
- First `dotnet test CopilotAgentObservability.slnx`: **FAIL only** on one
  intermittent `MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary(false)`
  failure only; Doctor 260/260 and ConfigCli 4029/4029 passed, Local Monitor
  was 1475/1476. The failing request-list observation was at line 80 while
  the production UI showed the expected prompt label; that test and its JS
  were unchanged by #104.
- Exact rerun of `dotnet test CopilotAgentObservability.slnx`: PASS; Doctor 260/260, ConfigCli 4029/4029,
  Local Monitor 1476/1476; 5765 total, 0 failed, 0 skipped.
- Focused `MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary` diagnostic (false/true cases): 2/2 PASS.
- Exact focused command: `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorOverviewPlaywrightTests.Overview_PeriodToggleAndLists_RespectSanitizedBoundary" --no-restore`
- `git diff --check` before T9 documentation: clean.

This evidence is frozen candidate evidence. It does not replace #106 live
validation.
