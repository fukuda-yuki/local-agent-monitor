# Sprint24 Durable Ledger

Updated: 2026-07-16

## Execution identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\8660\copilot-agent-observability`
- Branch: `codex/issue-68-claude-guided-setup`
- Start HEAD: `8940b34f4e031b894705682dc50c079a9ed5c180`
- Validated implementation HEAD before this closeout record: `f30fa0e686d565b133aad8785487d169bb9cacc8`
- Validated feature-branch commit range: `8940b34f4e031b894705682dc50c079a9ed5c180..f30fa0e686d565b133aad8785487d169bb9cacc8`
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
| W3 adapter/composition | Complete, committed | delegated implementation and remediation agents | `4e6ea1c` / `80c56cc4` | `4e6ea1c..80c56cc4` | required focused 110/110, 17/17, 8/8, 219/219; transaction evidence 8/8; ConfigCli 3,688/3,688 | initial C0/I3/M1 and first re-review C0/I1/M0 remediated; final re-review PASS C0/I0/M0 | none; Wave4 hardening remains separate |
| W4 transaction/migration/release | Complete, committed | three delegated test lanes | `3c83943f` / `6efb90ca` | `3c83943f..6efb90ca` | W4 focused 104/104; transaction/recovery/rollback/compensation 622/622; migration/storage/status 346/346; extracted Release ZIP 1/1 | initial C0/I2/M2 remediated; final independent re-review PASS C0/I0/M0 | live Claude/WSL and #104 first trace remain unverified |
| Final reviews | Complete | three independent non-implementing reviewers | `6c51e247` / `f30fa0e` | `6c51e247..f30fa0e` | final required focused 111/111, 17/17, 8/8, 219/219; pinned full 5,092/5,092 | all three perspectives PASS after documentation remediation, C0/I0/M0 | live surfaces and #102 integration remain separate |

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
| W3 required focused | exact requested filters for `ClaudeCodeSetupAdapterTests`, `ClaudeSettingsDocumentTests`, `ClaudeConfigurationSetupIntegrationTests`, and `SetupStorageTests` | PASS on final snapshot: 110/110, 17/17, 8/8, and 219/219 |
| W3 recovery/rollback evidence | deterministic unknown/mixed SDK discriminator and 5/8-env mismatch tests plus existing recovery/rollback suites | PASS: new 8/8; existing recovery/rollback 418/418 |
| W3 ConfigCli project full | `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj` | PASS, 3,688/3,688, 0 failed/skipped, 112 seconds |
| W3 bounded concurrent run | the same ConfigCli project command under a 180-second outer timeout while another worktree and reviewer tests were active | Inconclusive timeout; systematic diagnosis found no local residual testhost, and the isolated exact rerun above passed; not counted as success evidence |
| W4 deterministic transaction | `ClaudeSetupTransactionHardeningTests` plus existing apply/rollback/recovery/compensation filters | PASS: new 7/7; existing grouped evidence 622/622; no sleep/retry |
| W4 actual-v1 restart | `ClaudeSetupMigrationCompatibilityTests` plus storage/status filters | PASS: migration 2/2 and grouped evidence 346/346; fixture SHA-256 and historical row/private-plan identity verified |
| W4 CLI privacy | W4 classes plus `CliApplicationTests` | PASS on root snapshot, 104/104; actual CLI stdout/stderr, ledger, journal, and private-artifact boundary covered |
| W4 extracted Release ZIP | `LocalMonitorScriptTests.ClaudeSetup_RepositoryAndReleaseWrappersPreserveTransportParityWithoutDotnetAndIsolatedUserState` | PASS on root rerun, 1/1 in 80 seconds; package tree hash stable, Release PATH has no dotnet, repository/Release stdout/stderr/exit parity |
| Final required focused | exact requested filters for `ClaudeCodeSetupAdapterTests`, `ClaudeSettingsDocumentTests`, `ClaudeConfigurationSetupIntegrationTests`, and `SetupStorageTests` | PASS at `f30fa0e`: 111/111, 17/17, 8/8, and 219/219; 0 failed/skipped |
| Final pinned build | `dotnet build CopilotAgentObservability.slnx` | PASS, 7 projects, 0 warnings, 0 errors |
| Final browser bootstrap | `pwsh scripts\test\install-playwright-chromium.ps1` | PASS |
| Final pinned full test | `dotnet test CopilotAgentObservability.slnx` | PASS: ConfigCli 3,698/3,698; LocalMonitor 1,394/1,394; total 5,092/5,092; 0 failed/skipped |

## Review state

Wave 3 independent review initially found target/cardinality binding, explicit
Agent SDK content-variant persistence, WSL repository-only command provenance,
and the app-sdk WSL flag boundary. Remediation re-review then found one shared
recovery/rollback evidence gap. All findings were repaired with RED/GREEN tests;
the final independent re-review is PASS C0/I0/M0. The W3 commit is `80c56cc4`.

Wave 4 added only canonical clarification and executable hardening evidence; no
production defect required a product-code change. Independent review found
C0/I2/M2 evidence gaps in package-tree immutability, actual CLI stderr privacy,
historical missing-journal plan/status proof, and reverse-order ownership
wording. All were remediated; final re-review is PASS C0/I0/M0. The W4 commit
is `6efb90ca`.

Final review used three independent non-implementing perspectives for
requirements/public/Issue contracts, security/privacy/WSL boundaries, and
atomicity/concurrency/migration. The committed snapshot initially had one
inconsistent private-arm sentence and one under-specified app-sdk WSL option
rule. Commit `f30fa0e` corrected both. Each perspective re-reviewed the
remediation and returned PASS C0/I0/M0.

Wave 2 implementation re-reviews are PASS C0/I0/M0 for all three lanes. The W0 documentation's
initial independent review found C0/I3/M1: ambiguous Claude readiness probing,
non-generic status syntax, unsafe user-guide wording, and a stale table heading.
Root cross-check then found the same public surface stale in `config-cli.md`.
All five were remediated before the W0 commit; independent re-review returned
PASS C0/I0/M0. Wave 2 review findings and their remediations are summarized in
the task-state table above. Implementers and reviewers remain separate for
later waves.

Final review completed with three independent perspectives:

1. requirements/spec/public DTO and Issues #66/#102/#104 contracts;
2. security for raw content, paths, tokens, Hook execution, and WSL boundary;
3. atomicity, concurrency, actual-v1 restart, rollback, and migration claims.

## Unresolved and unverified interfaces

- No Issue #68 product decision is unresolved; implementation follows the
  approved contract as clarified by executable actual-fixture evidence.
- Issue #102 remains clean in a separate worktree at `31d7ec59`. Read-only
  `git merge-tree --write-tree` against validated Issue #68 HEAD found content
  conflicts in `docs/decisions.md`, `docs/spec.md`,
  `docs/specifications/interfaces/config-cli.md`, `docs/task.md`, and
  `src/CopilotAgentObservability.ConfigCli/Cli/CliHelpText.cs`. Integration must
  reconcile these canonical/CLI-help files and rerun the pinned suite; no merge
  was attempted here.
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
