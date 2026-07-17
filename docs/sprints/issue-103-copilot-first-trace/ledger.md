# Issue #103 GitHub Copilot First Trace Durable Ledger

Current product behavior remains in `docs/requirements.md`, `docs/spec.md`, and
the canonical interface specifications. The Issue body is the authoritative
work checklist; this ledger records execution evidence without copying it into
a second plan.

## Execution identity

- Worktree: dedicated Codex Issue #103 worktree (machine-specific absolute path
  intentionally omitted from repository-safe evidence)
- Branch: `codex/issue-103-copilot-first-trace`
- Kickoff/base SHA: `920ff43a9ec63088a9cc109bcd15d0e6f4f9dc5c`
- Feature-branch completion: complete through production commit `8dd751d` and
  this closeout ledger commit
- Main integration: not performed
- Push / pull request / external Issue changes: not performed

## Task state

| Issue checklist section | State | Commit range | Focused / full tests | Independent review | Unresolved |
| --- | --- | --- | --- | --- | --- |
| 103-A | Complete | contract/RED `31f9c9a`; mapper GREEN `23771e2` | mapper/setup/status 244/244 PASS; mapper-composed direct/CLI/HTTP 1/1 PASS | contract/spec/RED and definitive mapper re-reviews PASS C0/I0/M0 | none; Diverged status fixture remains a 103-D matrix item |
| 103-B | Complete | candidate RED `e74545c`; GREEN `6a22da5` | candidate 40/40 PASS; mapper/candidate 73/73 PASS; Doctor 233/233 PASS; ConfigCli 3,930/3,930 PASS; diff check PASS | final production review APPROVE C0/I0/M1; the concurrency follow-up closed in 103-D C0/I0/M0 | none |
| 103-C | Complete | RED/spec and GREEN `c7c4b75` | orchestrator 36/36 PASS; Copilot Doctor 136/136 PASS; ConfigCli 3,993/3,993 PASS; Doctor 233/233 PASS | final production re-review PASS C0/I0/M0 | none |
| 103-D | Complete | projection/schema `8650465`..`0f85b8b`; source matrices `57731e5`; mid-batch recovery `9fc8905`; owned-revision concurrency `2d133d3`; HTTP producer boundary `979accb`; structural provenance `8dd751d` | Copilot Doctor 154/154; evidence matrix 16/16; integration matrix 8/8; concurrency 4/4; Doctor 235/235; LocalMonitor projection/migration 105/105; ConfigCli and Claude regression results below; no sleep/retry | projection, candidate, concurrency, HTTP producer, security/privacy, integration, migration/restart, and Issue-contract final reviews PASS C0/I0/M0 | none; accepted HTTP is acknowledged only after exact raw persistence, while rejected input has no attributable raw identity and stays Unknown |
| 103-E | Complete | canonical CLI provenance/live closeout `f67e1a0`; final structural hardening `8dd751d` | bounded Copilot CLI interaction exited 0; Local Monitor live/ready 200; exact raw record produced 3 real-source evidence refs for accepted ingest, raw persistence, and completed projection; Session was honestly unbound | canonical provenance and final security reviews PASS C0/I0/M0; root live reconciliation | none |

## Validation evidence

| Gate | Command | Result |
| --- | --- | --- |
| Kickoff build | `dotnet build CopilotAgentObservability.slnx` | PASS; 0 warnings, 0 errors |
| Kickoff browser bootstrap | `pwsh scripts\test\install-playwright-chromium.ps1` | PASS |
| Kickoff full suite | `dotnet test CopilotAgentObservability.slnx` | PASS; Doctor 232/232, ConfigCli 3,857/3,857, LocalMonitor 1,460/1,460; total 5,549/5,549; 0 failed, 0 skipped |
| Copilot source focused | `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~GitHubCopilotDoctor" --no-restore` | PASS; 154/154 |
| ConfigCli full | final solution command below | PASS; 4,011/4,011 |
| Doctor full | `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --no-restore` | PASS; 235/235 |
| Shared projection/migration | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~ProjectionDisposition|FullyQualifiedName~SchemaMigration|FullyQualifiedName~DoctorMigration" --no-restore` | PASS; LocalMonitor 105/105, Doctor 12/12, ConfigCli 5/5 |
| Claude regression | `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~Claude" --no-restore` | PASS; 221/221 |
| Final build | `dotnet build CopilotAgentObservability.slnx` | PASS at `8dd751d`; 0 warnings, 0 errors |
| Final browser bootstrap | `pwsh scripts\test\install-playwright-chromium.ps1` | PASS at `8dd751d` |
| Final full solution | `dotnet test CopilotAgentObservability.slnx --no-restore` | PASS at `8dd751d`; Doctor 235/235, ConfigCli 4,011/4,011, LocalMonitor 1,474/1,474; total 5,720/5,720; 0 failed, 0 skipped |

## Repository-safe live evidence

- Repository revision: `f67e1a0` on `codex/issue-103-copilot-first-trace`.
- Supported surface and version: GitHub Copilot CLI `1.0.71`.
- Execution boundary: one non-interactive, no-tool interaction with content
  capture disabled; no files changed by the source interaction.
- Effective setting sources: process-scoped environment enabled Copilot OTel,
  selected the OTel HTTP JSON exporter, and targeted the loopback Local Monitor.
  No user-scoped setup, credential, authorization header, or resource-attribute
  override was added.
- Verification ID: `019f6d6c-2cd4-7ca1-8b36-35e1cacd9ebb`; expected source
  `github-copilot-cli`; expected adapter `github-copilot-doctor`.
- Exact outcome: the fresh bounded execution produced one enumerated candidate,
  raw record `1`. Exact selection validated the documented Copilot CLI
  `service.name=github-copilot` / span-bearing `github.copilot` scope pair and
  produced three opaque real-source evidence refs: accepted ingest, durable raw
  persistence, and completed projection. No exact native Session identity was
  emitted, so the result honestly reported `unbound` rather than fabricating a
  binding.
- The run was not promoted by latest-record, repository/workspace/cwd/process,
  time proximity, a manually injected `client.kind`, setup success, or a
  synthetic probe. The temporary live harness was deleted and was not committed.

## Acceptance criteria tracking

The fourteen acceptance criteria are tracked as `AC-01` through `AC-14` in
their exact Issue-body order. Evidence is added only after root compares the
requirement with executable output and the reviewed diff; a worker report alone
does not satisfy an AC.

| Criterion | State | Evidence |
| --- | --- | --- |
| AC-01 | Complete | canonical setup Apply/NoChanges or fresh Status starts an exact Doctor verification through the reviewed Copilot orchestrator |
| AC-02 | Complete | VS Code, CLI, and App/SDK canonical fixtures map into the same frozen Doctor contract |
| AC-03 | Complete | setup-only and synthetic cases remain non-ready with no accepted real-source references |
| AC-04 | Complete | exact selected raw, projection disposition, native binding, and content candidates are tracked independently |
| AC-05 | Complete | source tests reject mismatches and expose no latest/repository/workspace/cwd/process/time-proximity selection path |
| AC-06 | Complete | source integration and evidence matrices distinguish restart, protocol, signal, receiver, projection, and unbound states |
| AC-07 | Complete | exact-revision rollback cancellation and deterministic post-rollback non-ready evaluation pass |
| AC-08 | Complete | disabled/unsupported content remains honest advisory state rather than first-trace failure |
| AC-09 | Complete | source-specific `DoctorResult` uses production CLI JSON and human projection paths with the same state/evidence/action |
| AC-10 | Complete | anonymized raw/content/tool/credential/authorization/PII/path no-leak matrix passes |
| AC-11 | Complete | VS Code, CLI, and App/SDK fixture/integration partitions pass |
| AC-12 | Complete | repository-safe Copilot CLI 1.0.71 evidence at `f67e1a0` records exact canonical provenance, accepted/persisted/projected raw record `1`, three opaque real-source refs, and honest unbound Session state |
| AC-13 | Complete | kickoff SHA, dedicated branch/worktree identity, production commits through `8dd751d`, live revision, and exact validation commands are recorded; main integration remains explicitly not performed |
| AC-14 | Complete | final build, Playwright bootstrap, and full solution run at `8dd751d` pass 5,720/5,720 with no failed or skipped tests; the same exact commands are rerun after this ledger-only closeout commit |

## Review state

- Per-task implementer and reviewer must be different agents.
- Final reviews must separately cover requirements/public/Issue contracts,
  security/privacy, concurrency/atomicity/rollback, migration/restart, and the
  whole branch.
- No open review finding is accepted as complete without a recorded disposition
  and re-review where required.

## Unresolved and unverified Issue interfaces

- #104 and #106 worktrees/branches are untouched and their implementations are
  not merged, rebased, or cherry-picked.
- #105 proxy, common UI, Playwright/accessibility closeout, and Release ZIP
  workflow are out of scope and remain unverified.
- The production OTLP metadata provider currently labels ordinary input
  `raw-otlp`; #103 must not claim source attribution until the selected
  raw/Session evidence is validated by source-owned exact values.
- Per-record projection disposition is durable for exact new ingestions. Legacy
  raw rows without exact historical evidence remain unknown rather than being
  inferred from row absence or a global counter.
- Live Copilot CLI evidence is recorded above; the supported CLI run was
  source-attributed exactly and reported honest unbound Session state.
- Feature-branch completion is claimed only for this dedicated branch. Main
  integration is distinct and has not been performed.

## Evidence hygiene

The ledger records only safe task state, commit IDs/ranges, command results,
review verdicts, fixed source identifiers, and unresolved interface names. It
does not contain raw prompts/responses, tool bodies, credentials, authorization
values, PII, sensitive local paths, database paths, or runtime artifacts.
