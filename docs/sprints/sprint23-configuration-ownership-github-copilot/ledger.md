# Sprint23 Durable Ledger

Updated: 2026-07-12

## Task state

| Task | Issue | State | Commit range | Focused/full tests | Review | Unresolved minor |
| --- | --- | --- | --- | --- | --- | --- |
| Contract and plan | #66-#67 | Complete; Task 1a ready | pending contract commit | SourceCapabilityContractTests 13/13; `git diff --check` exit 0 | Spec PASS; architecture/security APPROVED; Plan PASS; no findings | repository dispatch skill path is absent, using Superpowers SDD |
| CRLF contract-test baseline fix | prerequisite | Complete | `3fa8340..3505565` | SourceCapabilityContractTests 13/13 / full 1,250/1,250 | Spec PASS, Quality APPROVED | none |
| Ledger schema and redaction | #66 | Pending | - | - | - | - |
| Atomic file mutation | #66 | Pending | - | - | - | - |
| User environment mutation | #66 | Pending | - | - | - | - |
| Transaction and rollback | #66 | Pending | - | - | - | - |
| Shared setup commands | #66 | Pending | - | - | - | - |
| Issue interface executable test | #66-#67 | Pending | - | - | - | - |
| Copilot detection and precedence | #67 | Pending | - | - | - | - |
| VS Code adapter | #67 | Pending | - | - | - | - |
| Copilot CLI adapter | #67 | Pending | - | - | - | - |
| App/SDK guidance adapter | #67 | Pending | - | - | - | - |
| Release/repository integration | #67 | Pending | - | - | - | - |
| Final integration | #66-#67 | Pending | - | - | - | - |

## Requirement-to-test evidence

| Requirement | Planned executable evidence | Result |
| --- | --- | --- |
| Versioned ownership ledger | Schema fixtures, unknown-version rejection, close/reopen persistence | Pending |
| Stale plan/apply and rollback | Barrier-controlled hash changes without sleeps | Pending |
| Atomic file update | Backup/temp/replace fault injection and restart-visible state | Pending |
| Partial compensation | Deterministic multi-target failure at each boundary | Pending |
| Crash recovery | Pre-file/per-env-member mutation and restore intents; deterministic faults before/during/after each step; close/reopen recovery classifies prior/desired/partial/foreign states | Pending |
| Recovery result correlation | Producer DTO tests distinguish requested/created and recovered change-set IDs for plan/apply/rollback/status, including failed-recovery status projection | Pending |
| Status hard cap | 99/100/101 fixtures, recovery-blocking/planned/terminal priority, UUID tie-break, and filter-before-cap | Pending |
| Apply-time revalidation | Endpoint ownership, supported version, and managed state changed after plan produce no backup/journal/ledger transition/write | Pending |
| Final-state race | Barrier edits after mutation completion and after rollback preflight are preserved; apply does not commit and restore does not overwrite | Pending |
| Status aggregation | Lifecycle reference matrix plus all-desired/all-previous/desired+previous/third-party/unavailable aggregate target cases prove change-set state and partial rollback unavailable | Pending |
| Environment notification | Deterministic faults before/after delivery prove no early delivery and permit replay after recovery | Pending |
| Ownership preservation | Unowned JSON/TOML/environment values survive apply and rollback | Pending |
| Secret-safe evidence | Negative assertions across plan, ledger, logs, and wrapper output | Pending |
| Copilot precedence | Producer DTO tests for policy, environment, and user settings | Pending |
| Content-capture opt-in | Default no-change plus explicit-option warning tests | Pending |
| Cross-surface contract | Real #66 producer DTO consumed by all #67 adapters | Pending |
| Full regression | Build, Playwright bootstrap, full solution tests | Baseline build 0/0, bootstrap exit 0, full 1,250/1,250 |

## Unverified Issue interfaces

- The canonical physical-target/member-change DTO is defined but has not yet
  been compiled or consumed across the CLI/PowerShell boundary.
- Supported keys and floors are fixed from official sources: stable VS Code
  1.128+ for apply and terminal Copilot CLI 1.0.4+.
- HTTP, proxy, and UI DTOs are explicitly N/A; independent re-review is pending.
- App/SDK guidance output has not yet consumed a real #66 plan DTO.
- Re-audit corrections define per-step apply/rollback intents, recovery-result
  correlation, failed-recovery status projection, apply-time endpoint/version/
  policy revalidation, exact 100-entry ordering, and replayable notification;
  they have not yet passed independent review.
