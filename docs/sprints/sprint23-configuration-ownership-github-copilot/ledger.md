# Sprint23 Durable Ledger

Updated: 2026-07-13

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
| Reverse compensation (Task 5c) | #66 | Complete | `c3e0314..9c82edc` | SetupCompensationTests 24/24; apply/compensation/journal/file/environment 312/312; all Setup 572/572; ConfigCli 900/900; build 0/0 | Spec PASS; Quality/Security/Concurrency APPROVED after journal-ordering review | restart recovery of durable nonterminal/terminal-pending evidence remains Task 5d |
| Prepared artifact reuse prerequisites (Tasks 5d0a-c) | #66 | Complete | `58945e4..08d716c` | file/platform 118/118; environment 87/87; journal 129/129; storage 181/181; all Setup 637/637; ConfigCli 965/965; build 0/0 at prerequisite review | Each task Spec PASS; Quality/Security/Concurrency APPROVED | Apply wiring and restart coordinator remain Task 5d0d/5d |
| Prepared Apply artifact resume (Task 5d0d) | #66 | Complete | `715858f..ec7c980` | SetupApplyTests 85/85; related transaction 408/408; all Setup 688/688; ConfigCli 1016/1016; build 0/0 | Spec PASS; Quality/Security/Concurrency APPROVED | restart recovery coordinator remains Task 5d |
| Terminal recovery and notification replay (Tasks 5d0e/5d1) | #66 | Complete | `7b86b1c..88a87c2` | SetupRecoveryTests 73/73; environment 95/95; combined transaction 183/183; all Setup 779/779; ConfigCli 1107/1107; build 0/0 | Spec PASS; Quality/Security/Concurrency APPROVED after terminal-evidence review | active Apply/compensation and rollback recovery remain Tasks 5d2/5d3 |
| Active Apply restart recovery (Tasks 5d2a-b) | #66 | Complete | `e083462..80004d9` | SetupRecoveryTests 168/168; Apply+Compensation+Recovery 278/278; all Setup 874/874; ConfigCli 1202/1202; build 0/0 | Spec PASS; Quality/Security/Concurrency APPROVED | rollback recovery remains Task 5d3 |
| Interrupted file rollback restart recovery (Task 5d3a) | #66 | Complete | `3f0aaa1..4659c22` (includes `1484280`, `f9665d3`) | focused matrix 5/5; SetupRecoveryTests 212/212; Recovery+Journal 359/359; all Setup 934/934; ConfigCli 1262/1262; build 0 warnings/0 errors; `git diff --check` clean | Spec PASS; Quality/Security/Concurrency APPROVED; independent final review PASS/APPROVED; no findings | none |
| Interrupted single-environment rollback restart recovery (Task 5d3b1) | #66 | Complete | `26ff098..b044798` | forged NoOp 3/3; single environment 33/33; SetupRecoveryTests 245/245; related transaction 487/487; all Setup 967/967; ConfigCli 1295/1295; build 0 warnings/0 errors; `git diff --check` clean | Spec PASS; Quality/Security/Concurrency APPROVED; independent final review PASS/APPROVED; no findings | mixed active/committed rollback recovery and the normal rollback producer remain |
| Mixed active/committed rollback restart recovery (Task 5d3b2a) | #66 | Complete | `b74029b` | focused 10/10; SetupRecoveryTests 254/254; related transaction 576/576; all Setup 976/976; ConfigCli 1304/1304; build 0 warnings/0 errors; `git diff --check` clean | Spec PASS; Quality/Security/Concurrency APPROVED; independent final review PASS/APPROVED; no findings | committed lagging-ledger reconciliation and notification ambiguity remain; normal rollback producer remains Task 6a |
| Committed lagging-ledger rollback reconciliation (Task 5d3b2b1) | #66 | Complete | `c4d2580` (test-only) | focused 12/12; SetupRecoveryTests 266/266; related transaction 603/603; all Setup 988/988; ConfigCli 1316/1316; build 0 warnings/0 errors; `git diff --check` clean | Spec PASS; Quality/Security/Concurrency APPROVED; independent final review PASS/APPROVED; no findings | notification ambiguity remains; normal rollback producer remains Task 6a |
| Rollback notification ambiguity and replay recovery (Task 5d3b2b2) | #66 | Complete | `4660b45` (test-only) | focused 9/9 and 90/90 repeated; SetupRecoveryTests 274/274; related transaction 786/786; all Setup 996/996; ConfigCli 1324/1324; build 0 warnings/0 errors; `git diff --check` clean | Spec PASS; Quality/Security/Concurrency APPROVED; independent final review PASS/APPROVED; no findings | normal rollback producer remains Task 6a; Windows broadcast represented by deterministic seam |
| Restart recovery coordinator (Task 5d) | #66 | Complete | `715858f..4660b45` | terminal, active Apply, file rollback, single-environment rollback, mixed rollback, lagging-ledger reconciliation, and notification ambiguity/replay matrices complete; latest SetupRecoveryTests 274/274; related transaction 786/786; build 0 warnings/0 errors | Spec PASS; Quality/Security/Concurrency APPROVED; independent final review PASS/APPROVED; no findings | normal rollback producer remains Task 6a |
| Rollback journal supersession primitive (Task 6a0a) | #66 | Complete | `e277613` | focused 26/26; Journal 173/173; Storage+Journal 231/231; all Setup 1,022/1,022; ConfigCli 1,350/1,350; build 0 warnings/0 errors; `git diff --check` clean | Spec PASS; independent implementation, security, concurrency, and integration reviews PASS/APPROVED; no findings | fault/rebind/concurrency coverage remains Task 6a0b; normal rollback coordinator remains Task 6a |
| Setup lock operation lifetime (Lock A-C) | #66 | Complete | `08d716c..d06ffcc` | apply/compensation/runtime/storage/journal 286/286; all Setup 663/663; ConfigCli 991/991; build 0/0 | Spec/internal PASS; Quality/Security/Concurrency APPROVED after disposal-order re-audit | Windows cross-process executed; Linux/macOS runtime not executed |
| Ledger schema and redaction | #66 | Complete through Task 2c | `0daee69..dcb7191` | v1 fixture write-close-reopen; unknown/corrupt/no-v0; boundary faults | PASS/APPROVED | none |
| Atomic file mutation | #66 | Complete through Task 3b | `291b3bf..2ed9e8a` | typed hash, path/reparse, backup/temp/replace/restore fault matrix | PASS/APPROVED | three-OS runtime evidence gap noted above |
| User environment mutation | #66 | Complete through Task 4 | `f4f55ec..620448a` | full 3x3 state, backup, member fault, notification boundary matrix | PASS/APPROVED | coordinator journaling/recovery remains Task 5 |
| Transaction and rollback | #66 | In progress through Task 6a0a | `2d88eff..e277613` | journal/store, exact artifact reuse, terminal/active Apply recovery, file, single-environment, mixed active/committed rollback recovery, committed lagging-ledger reconciliation, notification ambiguity/replay recovery, apply/compensation, lock-lifetime evidence, and rollback journal supersession primitive evidence above | Tasks through 6a0a and Lock A-C PASS/APPROVED | fault/rebind/concurrency coverage remains Task 6a0b; normal rollback producer remains Task 6a |
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
| Stale plan/apply and rollback | Barrier-controlled hash changes without sleeps | Apply preflight and post-mutation full-target verification covered by SetupApplyTests 55/55; file, single-environment, mixed rollback restart recovery, committed lagging-ledger reconciliation, and notification ambiguity/replay recovery cover stale/no-op/third-party/unavailable classification and retry admission in SetupRecoveryTests 274/274; normal rollback remains pending |
| Atomic file update | Backup/temp/replace fault injection and restart-visible state | SetupFileStepTests 65/65; capture/durable-backup/write split plus fault/rebind matrix; independent review PASS/APPROVED |
| Partial compensation | Deterministic multi-target failure at each boundary | SetupCompensationTests 24/24 cover file plus ENV_A/B/C forward/restore faults, reverse order, third-party preservation, restored/partial journal-before-ledger ordering; independent review PASS/APPROVED |
| Crash recovery | Pre-file/per-env-member mutation and restore intents; deterministic faults before/during/after each step; close/reopen recovery classifies prior/desired/partial/foreign states | Apply terminal, notification-only, file active, environment active, mixed recovery, interrupted file/single-environment/mixed rollback, committed lagging-ledger reconciliation, and notification ambiguity/replay recovery covered by SetupRecoveryTests 274/274; Recovery+Journal 786/786; actual Apply producer→reopened Recovery consumer passes; Windows broadcast represented by deterministic seam |
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
- The normal rollback producer is not yet implemented or independently
  reviewed; file, single-environment, mixed active/committed rollback
  recovery, committed lagging-ledger reconciliation, and notification
  ambiguity/replay recovery evidence is complete through Task 5d3b2b2.
- Supported keys and floors are fixed from official sources: stable VS Code
  1.128+ for apply and terminal Copilot CLI 1.0.4+.
- HTTP, proxy, and UI DTOs are explicitly N/A; independent re-review is pending.
- App/SDK guidance output has not yet consumed a real #66 plan DTO.
- Re-audit corrections define per-step apply/rollback intents, recovery-result
  correlation, failed-recovery status projection, apply-time endpoint/version/
  policy revalidation, exact 100-entry ordering, and replayable notification;
  they have not yet passed independent review.
