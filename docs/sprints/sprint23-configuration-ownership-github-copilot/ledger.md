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
| JSONC and bounded TOML codecs (Task 3a) | #66 | Complete | `93398bb..97f757f` | SetupDocumentTests 51/51; ConfigSamplesTests 49/49; all Setup 244/244; ConfigCli 572/572 | Spec PASS; Quality/Security APPROVED | JSONC BOM handling deferred to file layer; bounded TOML non-goals explicit |
| Path/hash/atomic file step (Task 3b) | #66 | Complete | `291b3bf..2ed9e8a` | SetupFileStepTests 63/63; SetupPlatformTests 26/26; all Setup 313/313; ConfigCli 641/641; build 0/0 | Implementation Spec PASS; Quality/Security APPROVED after re-audit | Windows native executed; Linux statx ABI probed/static reviewed and macOS attrlist static reviewed, but Linux/macOS .NET branches not executed |
| Current-user environment member step (Task 4) | #66 | Complete | `f4f55ec..620448a` | SetupEnvironmentStepTests 69/69; SetupPlatformTests 36/36; all Setup 392/392; ConfigCli 720/720; build 0/0 | Spec PASS; Quality/Security APPROVED | actual Windows broadcast delivery not integration-tested; injected fixed failure/replay verified |
| Transaction journal v1 store (Task 5a/5a1) | #66 | Complete | `2d88eff..c394b28` | SetupJournalStoreTests 99/99; storage+journal 143/143; all Setup 518/518; ConfigCli 846/846; build 0/0 on reviewed descendant | Spec PASS; Quality/Security APPROVED after persistence and cross-task notification/compensation re-audits | sudden OS power loss is represented by deterministic atomic fault seams, not directly induced |
| File backup/apply split (Task 5b0 prerequisite) | #66 | Complete | `6df14ae..b763ded` | SetupFileStepTests 65/65; all Setup 462/462; ConfigCli 790/790; build 0/0 | Spec PASS; Quality/Security APPROVED | Linux/macOS native metadata branches unchanged and not executed on Windows host |
| Apply preflight and forward intents (Task 5b) | #66 | Complete | `c394b28..34025d8` | SetupApplyTests 55/55; journal/file/environment/apply 288/288; all Setup 548/548; ConfigCli 876/876; build 0/0 | Spec PASS; Quality/Security/Concurrency APPROVED after ownership-boundary contract re-audit | forward failures deliberately retain durable evidence for Tasks 5c/5d |
| Ledger schema and redaction | #66 | Complete through Task 2c | `0daee69..dcb7191` | v1 fixture write-close-reopen; unknown/corrupt/no-v0; boundary faults | PASS/APPROVED | none |
| Atomic file mutation | #66 | Complete through Task 3b | `291b3bf..2ed9e8a` | typed hash, path/reparse, backup/temp/replace/restore fault matrix | PASS/APPROVED | three-OS runtime evidence gap noted above |
| User environment mutation | #66 | Complete through Task 4 | `f4f55ec..620448a` | full 3x3 state, backup, member fault, notification boundary matrix | PASS/APPROVED | coordinator journaling/recovery remains Task 5 |
| Transaction and rollback | #66 | In progress through Task 5b | `2d88eff..34025d8` | journal/store, file-backup, and apply evidence above | Tasks 5a1/5b0/5b PASS/APPROVED | compensation, recovery, rollback remain |
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
| Versioned ownership ledger | Schema fixtures, unknown-version rejection, close/reopen persistence | SetupStorageTests 44/44 and SetupJournalStoreTests 99/99; v1 only, bounded read, notification replay state, truthful compensation phases, atomic retry/reopen; independent review PASS/APPROVED |
| Public setup wire contract | Canonical literal plan/status/error fixtures, every enum mapping, fixed-code catalog, sole serializer | SetupContractShapeTests 47/47; independent review PASS/APPROVED |
| Repository-safe DTO validation | Exact/over bounds, canonical manifest membership, origin-only endpoint, fixed non-echo failures, recovery/lifecycle matrix | SetupContractValidationTests 74/74; independent review PASS/APPROVED |
| Stale plan/apply and rollback | Barrier-controlled hash changes without sleeps | Apply preflight and post-mutation full-target verification covered by SetupApplyTests 55/55; rollback remains pending |
| Atomic file update | Backup/temp/replace fault injection and restart-visible state | SetupFileStepTests 65/65; capture/durable-backup/write split plus fault/rebind matrix; independent review PASS/APPROVED |
| Partial compensation | Deterministic multi-target failure at each boundary | Pending |
| Crash recovery | Pre-file/per-env-member mutation and restore intents; deterministic faults before/during/after each step; close/reopen recovery classifies prior/desired/partial/foreign states | Pending |
| Recovery result correlation | Producer DTO tests distinguish requested/created and recovered change-set IDs for plan/apply/rollback/status, including failed-recovery status projection | Pending |
| Status hard cap | 99/100/101 fixtures, recovery-blocking/planned/terminal priority, UUID tie-break, and filter-before-cap | Pending |
| Apply-time revalidation | Endpoint ownership, supported version, and managed state changed after plan produce no backup/journal/ledger transition/write | Generic required revalidator boundary and fixed no-artifact matrix pass in SetupApplyTests; real GitHub Copilot implementation remains Task 8b |
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
