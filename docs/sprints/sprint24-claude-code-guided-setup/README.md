# Sprint24 - Claude Code Guided Setup

| Boundary | Producer | Contract | Consumer | Issue #68 change |
| --- | --- | --- | --- | --- |
| Backend domain | `ClaudeCodeSetupAdapter` and CLI/SDK partitions | `SetupPlanRequest` to `SetupChangePlan` / `SetupRevalidation` | registry and apply/rollback/status coordinators | add `claude-code`, `cli|app-sdk|all`, and explicit WSL2 opt-in |
| CLI JSON | `SetupCommandDispatcher` | existing `setup.v1` `SetupCommandResult` / `SetupTargetResult` | Config CLI users and wrappers | additive targets, five result codes, one warning, and one restart action |
| HTTP | existing Local Monitor | existing `/health/ready`, `/v1/traces`, and `/api/session-ingest/v1/events` | endpoint probe, Claude OTel, and `hook-forward` | reuse only; no new route or HTTP DTO |
| Proxy | repository and Release ZIP `setup.ps1` | exact arguments, stdout bytes, and exit code | Config CLI executable | forward `--allow-wsl2-routing` without interpretation |
| UI | none | none | none | no UI; human guidance remains the `setup.v1` projection |

Current product behavior remains canonical in requirements, spec, and the
[configuration setup interface](../../specifications/interfaces/configuration-setup.md).
This sprint material is execution history and evidence.

## Scope

Issue #68 extends the Issue #66 transaction with the `claude-code` adapter.
The CLI target covers interactive Claude Code and `claude -p`; Agent SDK is
caller-managed Python/TypeScript guidance. Windows native and explicitly
opted-in WSL2 repository execution are supported. Native macOS/Linux installer,
Windows-to-WSL mutation, remote collectors, first-real-trace/Doctor work, HTTP
routes, proxy DTOs, and UI are out of scope.

Issue #107/#108 are already integrated in main. Issue #102 is active in the
separate `codex/issue-102-doctor-core` worktree. Issue #100 is an independent
bug lane. Issue #104 consumes the static setup result later and owns first real
trace evidence; Issue #68 does not emit `run_first_trace_doctor`.

## Pinned public contract

```text
config-cli setup plan --adapter claude-code \
  --target <cli|app-sdk|all> \
  [--endpoint <loopback-http-url>] \
  [--include-content-capture] \
  [--allow-wsl2-routing]
```

`--allow-wsl2-routing` is required only in verified WSL2 and invalid elsewhere.
The five additive result codes are `endpoint_unreachable`,
`hook_command_conflict`, `content_policy_conflict`,
`wsl2_opt_in_required`, and `wsl2_routing_unavailable`. The additive restart
requirement/action are `restart_agent_process` and `restart_claude_process`.

The default managed env set enables Claude telemetry and trace export only. The
three OTel content gates are managed only by explicit opt-in. The 11
mapper-compatible Hook events are managed by default with matcher omitted,
five-second handler timeout, and 250 ms internal forwarding timeout. Because
those Hooks may carry raw prompt/tool content independently of OTel gates, the
plan emits `claude_hooks_capture_raw_content`.

The private plan gains `claude_settings_owned_values_v1`. It stores the expected
complete-state hash and bounded owned env/Hook values. Public DTOs, ledger,
journal, logs, and repository-safe evidence never contain those values, target
paths, or Hook commands. Existing ownership-ledger and private-plan v1 fixtures
remain byte-identical.

## Execution waves

| Wave | State | Deliverable |
| --- | --- | --- |
| W0 | Complete, uncommitted | canonical specification, decision, user workflow, Sprint24 plan, durable ledger |
| W1 | Pending | early executable adapter/registry/dispatcher/direct CLI/repository/release wrapper RED contract |
| W2a | Pending | nested settings renderer and private-plan storage arm |
| W2b | Pending | strict version/platform/WSL2/endpoint detection |
| W2c | Pending | Python/TypeScript Agent SDK no-write guidance |
| W3 | Pending | aggregate adapter, CLI composition, transaction integration |
| W4 | Pending | deterministic stale/concurrency/rollback, actual-v1 restart, release hardening, negative evidence |
| Final | Pending | independent requirements, security, and transaction/migration reviews plus focused/full validation |

Each implementation unit follows test-driven development. Atomicity,
rollback, stale state, and concurrency tests use barriers or injected fault
points with no sleeps, polling retries, or time-proximity assertions.

## Baseline

- Worktree: `C:\Users\mwam0\.codex\worktrees\8660\copilot-agent-observability`
- Branch: `codex/issue-68-claude-guided-setup`
- Start HEAD: `8940b34f4e031b894705682dc50c079a9ed5c180`
- Baseline build: PASS, seven projects, zero warnings/errors.
- Baseline Playwright bootstrap: PASS.
- Baseline solution tests: PASS on the completed rerun, ConfigCli 3,484 and
  LocalMonitor 1,393, total 4,877, zero failed/skipped. The earlier 120-second
  orchestration timeout is not success evidence.

See [ledger.md](ledger.md) for task state, commit ranges, focused/full test
evidence, review verdicts, unresolved items, and inter-Issue interfaces.
