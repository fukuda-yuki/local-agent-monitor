# Sprint23 Durable Ledger

Updated: 2026-07-12

## Task state

| Task | Issue | State | Commit range | Focused/full tests | Review | Unresolved minor |
| --- | --- | --- | --- | --- | --- | --- |
| Contract and plan | #66-#67 | Complete | `3505565..63deb36` | SourceCapabilityContractTests 13/13; `git diff --check` exit 0 | Spec PASS; architecture/security APPROVED; Plan PASS; no findings | repository dispatch skill path is absent, using Superpowers SDD |
| CRLF contract-test baseline fix | prerequisite | Complete | `3fa8340..3505565` | SourceCapabilityContractTests 13/13 / full 1,250/1,250 | Spec PASS, Quality APPROVED | none |
| Setup result DTO and serializer (Task 1a) | #66 | Complete | `63deb36..98af0cc` | SetupContractShapeTests 47/47; `git diff --check` exit 0 | Spec PASS, Quality APPROVED; no findings after two required test-design re-audits | none |
| Setup result validation (Task 1b) | #66 | Complete | `e62330b..92ced23` | SetupContractValidationTests 74/74; SetupContractShapeTests 47/47; ConfigCli 449/449 | Spec PASS; Security/Quality APPROVED after dependency re-audit | target pairing and CLI input normalization remain for Tasks 8b/7a |
| Runtime #61 manifest loader (Task 8a, pulled forward) | #67 dependency | Complete | `cce9a72..e7d44e2` | SourceCapabilityRuntimeTests 14/14; SourceCapabilityContractTests 13/13; ConfigCli 432/432 at task review | Spec PASS; Quality/Security APPROVED | target pairing remains for Task 8b real-producer gate |
| Platform contracts and deterministic fake (Task 2a) | #66 | Complete | `2755269..fd36d38` | SetupPlatformTests 15/15; ConfigCli 464/464 | Spec PASS; Quality/Security APPROVED after concurrency re-audit | actual Windows notification delivery not integration-tested; injected classification verified |
| Runtime paths and exclusive lock (Task 2b) | #66 | Complete | `9d962ff..ed820bb` | SetupRuntimeTests 8/8; SetupPlatformTests 20/20; ConfigCli 477/477 | Spec PASS; Quality/Security APPROVED after cross-process test re-audit | Linux/macOS runtime not executed on Windows host; mandatory cross-platform test no longer skips |
| Private plan and ledger v1 stores (Task 2c) | #66 | Complete | `0daee69..dcb7191` | SetupStorageTests 44/44; all Setup 193/193; ConfigCli 521/521; build 0/0 | Spec PASS; Quality/Security APPROVED | private desired-state schema is text JSON/env focused; binary expansion requires future schema |
| Ledger schema and redaction | #66 | Complete through Task 2c | `0daee69..dcb7191` | v1 fixture write-close-reopen; unknown/corrupt/no-v0; boundary faults | PASS/APPROVED | none |
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
| Public setup wire contract | Canonical literal plan/status/error fixtures, every enum mapping, fixed-code catalog, sole serializer | SetupContractShapeTests 47/47; independent review PASS/APPROVED |
| Repository-safe DTO validation | Exact/over bounds, canonical manifest membership, origin-only endpoint, fixed non-echo failures, recovery/lifecycle matrix | SetupContractValidationTests 74/74; independent review PASS/APPROVED |
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
