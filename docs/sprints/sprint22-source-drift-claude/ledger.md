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
| Exact binding and dedupe | #64 | Complete (Task 18) | `97bdd36` | Task18 10/10; Task18+security 13/13; combined 134/134; build 0 | Spec/Concurrency/Security APPROVED | complete trace-context positive remains deferred by DTO contract |
| Early #62/#64 integration review | #62/#64 | Complete with explicit follow-ups (Task 19) | `67af37d..97bdd36` | ConfigCli 909/909; LocalMonitor 395/395; build 0 | Important findings resolved in canonical binding spec; Task20 gate reviewed | complete trace-context DTO remains deferred; Task20 must never exact-link trace-id-only |
| Projection and HTTP DTO | #65 | Complete (Tasks 20-21) | `3bb2c9e..7bacf7e` | graph 33/33; backend/near-miss 44/44; build 0 | Independent graph/DTO review PASS | trace-id-only stays hook_only/otel_only |
| Canvas/UI and Playwright | #65 | Complete (Task 22) | `2a2a252..cf12a2a` | Canvas contract 28/28; Node 31/31; Playwright 18/18; build 0 | Independent UI review PASS | live Claude producer evidence remains blocked |
| User documentation and live-validation closeout | #65 | Complete with explicit live blockers (Task 23) | this docs closeout | `git diff --check` PASS; repository-safe docs review | Exact blockers and named follow-ups recorded for all three Claude surfaces | interactive TTY unavailable; print emitted no structural telemetry; Agent SDK package/runtime/credential unavailable |
| Final integration | #62-#65 | Complete | `2e4743f..577a39a` | build PASS; Playwright bootstrap PASS; full solution 2,577/2,577 PASS; diff-check PASS | independent security and Canvas reviews PASS; task-level spec/code reviews recorded | live producer follow-ups and complete trace-context DTO are explicit non-blocking follow-ups |

## Requirement-to-test evidence

| Requirement | Planned executable evidence | Result |
| --- | --- | --- |
| Additive migration and restart | Actual shipped-version DB fixtures, migrate-close-reopen assertions | PASS: Monitor v1-v4 and Session v1-v11 fixture suites |
| Bounded unknown metadata | Store/evaluator tests with overflow and raw-marker negatives | PASS: diagnostics and source contract suites |
| Drift versus failure states | Diagnostic state-table tests | PASS: ConfigCli/LocalMonitor contract and readiness suites |
| Copilot fidelity | Producer fixture golden comparison | PASS: fidelity 82/82 |
| Claude source identity | Producer-to-store byte-equivalent ID tests | PASS: Hook/OTLP fixtures and exact binding suites |
| Exact binding only | Positive exact cases plus repository/cwd/time negative cases | PASS: Task17B 10/10; complete trace-context remains blocked by DTO |
| Deterministic concurrency | Barrier-controlled duplicate/transaction tests, no sleeps | PASS: Task18 10/10 + security matrix 13/13 |
| Raw/sanitized boundary | API/SSE/log/golden negative matrix | PASS: diagnostics/API/UI focused suites |
| Claude UI states | Playwright list/detail/empty/error/degraded cases | PASS: Playwright 18/18; Canvas 28/28; Node 31/31 |
| Full regression | Build, Playwright bootstrap, full solution tests | PASS: build 0 warnings; bootstrap PASS; solution 2,577/2,577 |

## Unverified Issue interfaces

- Complete byte-equivalent trace-context binding awaits an additive Session DTO contract carrying complete context; trace_id-only remains explicitly insufficient.
- Canonical exact-binding contract now reflects shipped Claude source values and explicitly defers the complete trace-context row.
- Interactive CLI, `claude -p`, and Agent SDK live availability remains
  unverified. Task 23 records the exact blockers and named follow-ups in
  [M5 live-validation](milestones/M5-integration/live-validation.md): no
  interactive TTY, no structural telemetry from the bounded print run, and
  no available Agent SDK package/runtime/credential.
- Source observation DTO has not yet been exercised through storage, HTTP, and
  UI in one single cross-surface test; the storage, HTTP, and UI contracts are
  covered by adjacent executable suites.

## Baseline validation

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | PASS, 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | PASS, Chromium 1228 installed in worktree artifacts |
| `dotnet test CopilotAgentObservability.slnx` | PASS, ConfigCli 1,198 + LocalMonitor 1,379 = 2,577 |
