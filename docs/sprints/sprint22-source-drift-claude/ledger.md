# Sprint22 Durable Ledger

Updated: 2026-07-12

## Task state

| Task | Issue | State | Commit range | Focused/full tests | Review | Unresolved minor |
| --- | --- | --- | --- | --- | --- | --- |
| Contract and plan | #62-#65 | In progress | uncommitted | SourceCapabilityContractTests 13/13 / not run | self-review complete | none |
| CRLF contract-test baseline fix | #62 prerequisite | Complete | `3fa8340..2105d46` | SourceCapabilityContractTests 13/13 / not run | Spec PASS, Quality APPROVED | none |
| Schema observation migration | #62 | Pending | - | - | - | - |
| Unknown representation | #62 | Pending | - | - | - | - |
| Diagnostics and fidelity gate | #62 | Pending | - | - | - | - |
| Claude producer inventory | #63 | Pending | - | - | - | live environment may move to follow-up |
| Claude OTLP adapter | #64 | Pending | - | - | - | - |
| Claude Hook adapter | #64 | Pending | - | - | - | - |
| Exact binding and dedupe | #64 | Pending | - | - | - | - |
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
| Exact binding only | Positive exact cases plus repository/cwd/time negative cases | Pending |
| Deterministic concurrency | Barrier-controlled duplicate/transaction tests, no sleeps | Pending |
| Raw/sanitized boundary | API/SSE/log/golden negative matrix | Pending |
| Claude UI states | Playwright list/detail/empty/error/degraded cases | Pending |
| Full regression | Build, Playwright bootstrap, full solution tests | Pending |

## Unverified Issue interfaces

- Exact Claude OTLP signal path and media type await the #63 producer inventory.
- Exact Claude Hook envelope and event names await the #63 producer inventory.
- Interactive CLI, `claude -p`, and Agent SDK live availability is unverified;
  unavailable cases will receive a separate follow-up task with an exact
  blocker.
- Source observation DTO has not yet been exercised through storage, HTTP, and
  UI in one executable test.
