# Sprint22 Durable Ledger

Updated: 2026-07-13

## Task state

| Task | Issue | State | Commit range | Focused/full tests | Review | Unresolved minor |
| --- | --- | --- | --- | --- | --- | --- |
| Contract and plan | #62-#65 | Complete | `2105d46..7f33709` | SourceCapabilityContractTests 13/13 / full 1,250 | Plan Spec PASS; Executability APPROVED | outline-level test method detail (non-blocking) |
| CRLF contract-test baseline fix | #62 prerequisite | Complete | `3fa8340..2105d46` | SourceCapabilityContractTests 13/13 / not run | Spec PASS, Quality APPROVED | none |
| CRLF roadmap-row baseline fix | #62 prerequisite | Complete | `67af37d..6aa6d43` | SourceCapabilityContractTests 13/13; ConfigCli 314/314 / full 1,250 | Spec PASS, Quality APPROVED | none |
| Monitor v1-v4 migration fixtures | #62 | Complete (Task 2) | `7f5eae6..b93e79a` | MonitorSchemaMigrationFixtureTests 16/16; reproducible hashes 4/4 | Spec PASS; Migration Quality APPROVED | none |
| Schema observation migration/store | #62 | Complete (Task 3A) | `a0350ae..e47cd10` | migration/store 32/32; raw store 13/13; build 0 warnings | Spec PASS; Migration-Concurrency-Security APPROVED | writer integration remains Task 3B |
| Atomic ingest writer integration | #62 | Complete (Task 4A/3B) | `e47cd10..f5f0a85` | focused 61/61; store/migration 32/32; build 0 warnings | Spec/Code/Atomicity/Security APPROVED | projection disposition remains Task 4B |
| Compatibility projection disposition | #62 | Complete (Task 4B1/4B2) | `f5f0a85..33f551b` | 823/823 + 838/838 + 886/886; full LocalMonitor 1065; build 0 | Spec/Code/Security/Atomicity/Test APPROVED | none |
| Structural inventory contract | #62 | Complete (Task 1A) | `a4530f4..7f5eae6` | contract gate; focused baseline 34/34 | Spec PASS; Plan Executability APPROVED | none |
| Closed compatibility domain | #62 | Complete (Task 1B) | `b1a7f28..f8eb68a` | integrated ConfigCli 55/55; solution build 0 warnings | Spec PASS; Quality APPROVED | none |
| Original-input OTLP walkers | #62 | Complete (Tasks 1C1/1C2) | `b1a7f28..f8eb68a` | ConfigCli 55/55; MonitorHost 34/34; solution build 0 warnings | Spec PASS; Quality APPROVED | none |
| Diagnostics and fidelity gate | #62 | Complete (Task 5/6A) | `68069e2..8dfdede` | fidelity 82/82; diagnostics 98/98; readiness 24/24; ConfigCli 1198; LocalMonitor 1092; build 0 | Spec/Code/Security/Readiness APPROVED | live Copilot export blocker recorded |
| Claude interactive inventory | #63 | Complete with live follow-up (Task 7) | `dc8971b..4b60ed4` | SourceCapabilityContractTests 13/13; no live TTY execution | Spec PASS; Evidence/Data-safety APPROVED | `task-7-interactive-claude-code-live-inventory-in-tty-authenticated-session` |
| Claude print inventory | #63 | Complete with live follow-up (Task 8) | `08f3349..a4530f4` | bounded `claude -p` exit 0; SourceCapabilityContractTests 13/13; no structural telemetry emitted | Spec PASS; Evidence/Data-safety APPROVED | `claude-code-print-otel-hook-structure-capture` |
| Claude Agent SDK inventory | #63 | Complete with live follow-up (Task 9) | `4b60ed4..08f3349` | runtime/package detection; SourceCapabilityContractTests 13/13; SDK not executed | Spec PASS; Evidence/Data-safety APPROVED | `#63-live-agent-sdk-inventory` |
| Claude adapter contract artifacts | #63 | Complete (Task 10A-12) | `0e22d36..9dd7a62` | Hook 19/19; OTLP 49/49; fixture hashes 6/6 + 12/12; full LocalMonitor 1062 | Producer/contract APPROVED | live follow-ups remain explicit |
| Session v1-v5 migration fixtures and v3 restart fix | #64 | Complete (Task 13) | `b93e79a..b1a7f28` | fixture 5/5; combined store/fixture 71/71 | Spec PASS; Migration Quality APPROVED | none |
| Session v6-v10 migration fixtures | #64 | Complete (Task 14A) | `f8eb68a..af22ee8` | fixture 10/10; combined 76/76 | Spec PASS; Migration Quality APPROVED | none |
| Session stamped-v10 lineages | #64 | Complete (Task 14B) | `af22ee8..a0350ae` | fixture 13/13; combined 79/79 | Spec PASS; Migration Quality APPROVED | none |
| Session provenance envelope | #64 | Complete (Task 15A/15B) | `aacd358..aae5a1c` | 115/115; schema matrix 71/71; core 231/231; adjacent 127/127; build 0 | Spec/Migration/Security/Compatibility/Tests APPROVED | no dedicated REAL 11.4 or duplicate-version row (non-blocking) |
| Claude Hook adapter | #64 | Complete (Task 16A/16B) | `0b6c453..67f1527` | mapper 296/296; forwarder 79/79; build 0 | Spec/Code/Security/Tests APPROVED | none |
| Claude OTLP adapter | #64 | Complete (Task 17A/17B) | `c02be2b..cbb2128` | adapter 15/15; enrichment 10/10; related 311/311; build 0 | Spec/Code/Security/Tests APPROVED | complete trace-context positive blocked by DTO lacking traceparent/tracestate |
| Exact binding and dedupe | #64 | In progress (Task 18) | - | deterministic concurrency matrix pending | pending independent concurrency/security review | cursor/aggregate crash boundary remains |
| Projection and HTTP DTO | #65 | Pending | - | - | - | - |
| UI and Playwright | #65 | Pending | - | - | - | - |
| Final integration | #62-#65 | Pending | - | - | - | - |

## Requirement-to-test evidence

| Requirement | Planned executable evidence | Result |
| --- | --- | --- |
| Additive migration and restart | Actual shipped-version DB fixtures, migrate-close-reopen assertions | Pending |
| Bounded unknown metadata | Store/evaluator tests with overflow and raw-marker negatives | Pending |
| Drift versus failure states | Diagnostic state-table tests | Pending |
| Copilot fidelity | Producer fixture golden comparison | Pending |
| Claude source identity | Producer-to-store byte-equivalent ID tests | Pending |
| Exact binding only | Positive exact cases plus repository/cwd/time negative cases | PASS: Task17B 10/10; complete trace-context remains blocked by DTO |
| Deterministic concurrency | Barrier-controlled duplicate/transaction tests, no sleeps | Pending Task18 |
| Raw/sanitized boundary | API/SSE/log/golden negative matrix | Pending |
| Claude UI states | Playwright list/detail/empty/error/degraded cases | Pending |
| Full regression | Build, Playwright bootstrap, full solution tests | Pending |

## Unverified Issue interfaces

- Complete byte-equivalent trace-context binding awaits an additive Session DTO contract carrying complete context; trace_id-only remains explicitly insufficient.
- Task18 cross-writer cursor/aggregate atomicity remains unverified until barrier tests complete.
- Interactive CLI, `claude -p`, and Agent SDK live availability is unverified;
  unavailable cases will receive a separate follow-up task with an exact
  blocker.
- Source observation DTO has not yet been exercised through storage, HTTP, and
  UI in one executable test.

## Baseline validation

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | PASS, 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | PASS, Chromium 1228 installed in worktree artifacts |
| `dotnet test CopilotAgentObservability.slnx` | PASS, ConfigCli 314 + LocalMonitor 936 = 1,250 |
