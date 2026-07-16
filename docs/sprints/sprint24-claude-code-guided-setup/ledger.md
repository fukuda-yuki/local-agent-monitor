# Sprint24 Durable Ledger

Updated: 2026-07-16

## Execution identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\8660\copilot-agent-observability`
- Branch: `codex/issue-68-claude-guided-setup`
- Start HEAD: `8940b34f4e031b894705682dc50c079a9ed5c180`
- Current end HEAD: `109c30cb6d20f015e79f7e87acd953f584c4a874`
- Feature-branch commit range: `8940b34f4e031b894705682dc50c079a9ed5c180..109c30cb6d20f015e79f7e87acd953f584c4a874`
- Main integration: not performed
- Push, pull request, and external Issue closure: not performed

## Task state

| Task | State | Owner | Start/end HEAD | Commit range | Focused/full tests | Review | Unresolved |
| --- | --- | --- | --- | --- | --- | --- | --- |
| W0 canonical contract and ledger | Complete, committed | delegated documentation implementer | `8940b34f` / `399b7881` | `8940b34f..399b7881` | baseline full PASS; post-remediation `git diff --check` and both-interface identifier/scoped-diff checks PASS | independent remediation re-review PASS, C0/I0/M0 | none |
| W1 cross-surface RED contract | Complete, committed RED checkpoint | delegated test implementer | `399b7881` / `8be9f7fa` | `399b7881..8be9f7fa` | ConfigCli: physical parity 1 PASS and 2 intended RED; Release three-way transport 1 PASS; existing wrapper/release regressions 6 PASS | initial C0/I2/M0 remediated; independent re-review PASS C0/I0/M0 | aggregate adapter and single-option parser are intentionally absent |
| W2a settings/storage | Complete, committed | `/root/issue68_w2a_settings_storage` | `8be9f7fa` / `573267d8` | `8be9f7fa..573267d8` | `ClaudeSettingsDocumentTests` 17/17; `SetupStorageTests` 202/202; fixture bytes unchanged; `git diff --check` PASS | initial C0/I1/M1 remediated; independent re-review PASS C0/I0/M0 | W3 must materialize the arm through apply/status/rollback |
| W2b detection/WSL2 | Complete, committed | `/root/issue68_w2b_detection_wsl` | `573267d8` / `42eaf495` | `573267d8..42eaf495` | shared `ClaudeCodeSetupAdapterTests` 80/80; production HTTP seam 2/2; existing liveness proxy 1/1; build PASS | initial success-path integration gap remediated; independent re-review PASS C0/I0/M0 | W3 must compose version/context/readiness and fixed codes |
| W2c Agent SDK guidance | Complete, committed | `/root/issue68_w2c_sdk_guidance` | `42eaf495` / `109c30cb` | `42eaf495..109c30cb` | SDK guidance 4/4; shared `ClaudeCodeSetupAdapterTests` 80/80; build PASS | initial C0/I2/M0 remediated; independent re-review PASS C0/I0/M0 | W3 must integrate `app-sdk|all` and the closed guidance validator |
| W3 adapter/composition | Pending | unassigned | pending | pending | pending | pending | `setup.v1` projection and #66 transaction |
| W4 transaction/migration/release | Pending | unassigned | pending | pending | pending | pending | deterministic stale/concurrency and actual-v1 restart |
| Final reviews | Pending | three independent reviewers | pending | pending | focused/full pending | requirements; security; transaction/migration | #102 merge-tree check pending |

## Validation evidence

| Gate | Command/evidence | Result |
| --- | --- | --- |
| Baseline build | `dotnet build CopilotAgentObservability.slnx` | PASS, 7 projects, 0 warnings, 0 errors |
| Baseline browser bootstrap | `pwsh scripts\test\install-playwright-chromium.ps1` | PASS |
| Baseline full test | `dotnet test CopilotAgentObservability.slnx` | PASS on completed rerun: ConfigCli 3,484/3,484; LocalMonitor 1,393/1,393; 0 failed/skipped |
| Earlier bounded orchestration attempt | same full-test command under a 120-second outer limit | Inconclusive timeout; not counted as pass/fail evidence |
| W0 documentation | `git diff --check` plus required identifier and scoped-diff inspection | PASS; documentation-only scope, required identifiers present, D058/D059/D060 order verified |
| W1 executable RED | Config `FullyQualifiedName~ClaudeConfigurationSetupIntegrationTests`; LocalMonitor `FullyQualifiedName~ClaudeSetup` | Config: 1 safe pre-dispatch transport PASS + 2 intended assertion RED; LocalMonitor direct/repository/Release transport PASS; no compile/process/package/parity failure |
| W2 settings/storage | exact focused filters for `ClaudeSettingsDocumentTests` and `SetupStorageTests` | PASS, 17/17 and 202/202 |
| W2 detection/readiness | exact focused filters for `ClaudeCodeSetupAdapterTests` and `SystemSetupHttpProbeClaudeTests` | PASS, 80/80 and 2/2 |

## Review state

Wave 2 implementation re-reviews are PASS C0/I0/M0 for all three lanes. The W0 documentation's
initial independent review found C0/I3/M1: ambiguous Claude readiness probing,
non-generic status syntax, unsafe user-guide wording, and a stale table heading.
Root cross-check then found the same public surface stale in `config-cli.md`.
All five were remediated before the W0 commit; independent re-review returned
PASS C0/I0/M0. Wave 2 review findings and their remediations are summarized in
the task-state table above. Implementers and reviewers remain separate for
later waves.

Final review requires three independent perspectives:

1. requirements/spec/public DTO and Issues #66/#102/#104 contracts;
2. security for raw content, paths, tokens, Hook execution, and WSL boundary;
3. atomicity, concurrency, actual-v1 restart, rollback, and migration claims.

## Unresolved and unverified interfaces

- No product decision is unresolved; implementation follows the approved
  Issue #68 plan.
- Issue #102 remains in a separate worktree. Before feature-branch closeout, a
  read-only merge-tree check must enumerate CLI-composition/spec conflicts.
- Issue #104 first-real-trace/Doctor mapping is intentionally unimplemented and
  no first trace will be claimed by Issue #68.
- Live interactive Claude CLI, `claude -p`, Agent SDK, native Windows mutation,
  and WSL2 routing remain unverified until evidence is recorded; unavailable
  live surfaces do not replace fixture-backed implementation gates.
- Native macOS/Linux installer, Windows ZIP-to-WSL mutation, remote collector,
  non-loopback exposure, shell-profile mutation, HTTP route/DTO, proxy DTO, UI,
  and Issue #100 are out of scope.
- Feature-branch completion and main integration are separate states. Main
  integration, push, PR, and external Issue closure are not authorized here.

## Evidence hygiene

Ledger updates record only safe task state, commit IDs/ranges, command results,
review verdicts, and unresolved interface names. They never contain raw Claude
settings, absolute user target paths, Hook commands, credentials, tokens,
authorization headers, prompts, responses, tool content, or user data.
