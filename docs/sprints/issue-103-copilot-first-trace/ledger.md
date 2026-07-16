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
- Feature-branch completion: not yet complete
- Main integration: not performed
- Push / pull request / external Issue changes: not performed

## Task state

| Issue checklist section | State | Commit range | Focused / full tests | Independent review | Unresolved |
| --- | --- | --- | --- | --- | --- |
| 103-A | Complete | contract/RED `31f9c9a`; mapper GREEN `23771e2` | mapper/setup/status 244/244 PASS; mapper-composed direct/CLI/HTTP 1/1 PASS | contract/spec/RED and definitive mapper re-reviews PASS C0/I0/M0 | none; Diverged status fixture remains a 103-D matrix item |
| 103-B | Complete | candidate RED `e74545c`; GREEN included in the next ledger checkpoint | candidate 40/40 PASS; mapper/candidate 73/73 PASS; Doctor 233/233 PASS; ConfigCli 3,930/3,930 PASS; diff check PASS | final production review APPROVE C0/I0/M1 | simultaneous Observe and controlled SQLite-lock behavior remain a 103-D matrix item |
| 103-C | Complete | RED/spec and GREEN `c7c4b75` | orchestrator 36/36 PASS; Copilot Doctor 136/136 PASS; ConfigCli 3,993/3,993 PASS; Doctor 233/233 PASS | final production re-review PASS C0/I0/M0 | none |
| 103-D | Complete | projection RED `8650465`; projection GREEN `0f85b8b`; source matrices included in the next ledger checkpoint | Copilot Doctor 136/136; evidence matrix 16/16; integration matrix 8/8; concurrency 3/3; ConfigCli 3,993/3,993; Doctor 233/233; LocalMonitor projection/migration 104/104; Claude 221/221; no sleep/retry | projection, candidate, concurrency, evidence/privacy, and integration final reviews PASS C0/I0/M0 | rejected payload and accepted-without-persistence remain shared ingestion/Doctor evidence because neither has a selectable persisted raw ID |
| 103-E | Pending | pending | pending | pending | supported live surface or exact external blocker |

## Validation evidence

| Gate | Command | Result |
| --- | --- | --- |
| Kickoff build | `dotnet build CopilotAgentObservability.slnx` | PASS; 0 warnings, 0 errors |
| Kickoff browser bootstrap | `pwsh scripts\test\install-playwright-chromium.ps1` | PASS |
| Kickoff full suite | `dotnet test CopilotAgentObservability.slnx` | PASS; Doctor 232/232, ConfigCli 3,857/3,857, LocalMonitor 1,460/1,460; total 5,549/5,549; 0 failed, 0 skipped |
| Copilot source focused | `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~GitHubCopilotDoctor" --no-restore` | PASS; 136/136 |
| ConfigCli full | `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --no-restore` | PASS; 3,993/3,993. An initial 304-second no-output timeout was diagnosed with `--blame-hang`; the diagnostic run and exact rerun passed in 52 seconds. |
| Doctor full | `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --no-restore` | PASS; 233/233 |
| Shared projection/migration | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~ProjectionDisposition|FullyQualifiedName~SchemaMigration|FullyQualifiedName~DoctorMigration" --no-restore` | PASS; 104/104 |
| Claude regression | `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~Claude" --no-restore` | PASS; 221/221 |

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
| AC-12 | Pending | supported-surface live closeout not yet executed |
| AC-13 | In progress | kickoff identity and commits are recorded; final commands and closeout revision remain pending |
| AC-14 | Baseline only | kickoff commands pass; final committed-HEAD build/Playwright/full solution rerun still required |

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
- Live supported GitHub Copilot evidence has not yet been recorded.
- Feature-branch completion and main integration are distinct; neither is
  currently claimed.

## Evidence hygiene

The ledger records only safe task state, commit IDs/ranges, command results,
review verdicts, fixed source identifiers, and unresolved interface names. It
does not contain raw prompts/responses, tool bodies, credentials, authorization
values, PII, sensitive local paths, database paths, or runtime artifacts.
