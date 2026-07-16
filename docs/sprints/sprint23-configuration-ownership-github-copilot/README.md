# Sprint23 - Configuration Ownership and GitHub Copilot Setup

This sprint coordinates Issues #66 and #67. Current product behavior remains
canonical in requirements, spec, and interface documents.

| Milestone | State | Scope |
| --- | --- | --- |
| M0 contract and orchestration | Complete | Canonical contract, plan, durable ledger |
| M1 ownership ledger foundation | Complete | Issue #66 ledger, plan, and redaction contracts |
| M2 transactional mutation | Complete | Issue #66 atomic file, user environment, compensation, rollback |
| M3 shared setup command surface | Complete | Issue #66 generic dispatcher, 29-code process map, and repository-wrapper transport. Successful status/storage and real #66→#67 behavior are evidenced only by executable in-process production composition with an injected trusted platform; child `LOCALAPPDATA` subprocesses do not isolate production storage. |
| M4 GitHub Copilot adapters | Complete, with artifact-isolation remediation recorded | Issue #67 VS Code, CLI, and App/SDK targets plus the self-contained Release ZIP Config CLI. PATH-stripped packaged-wrapper execution proves executable selection only; it is not isolated status/storage evidence. |
| M5 integration closeout | Validated local main integration; completed by this merge commit | The original full-suite RED exposed compile-snapshot and packaged-status failures from non-isolated production storage; focused package/process remediation is PASS C0/I0/M0 and the exact pinned sequence passed 4,861/4,861. Do not treat the old `status_ready`/0 SHA or package/repository subprocess parity as isolated storage proof. Migration A remains N/A with v1 restart byte evidence; HTTP/proxy/UI N/A, no first-trace claim, and macOS/Linux native execution remains unexecuted. The feature branch is retained; push, PR, and external Issue closure are not performed. |

See [ledger.md](ledger.md) for current execution evidence.
The current session-resume boundary is recorded in
[the Issue #67 guided setup plan](../../superpowers/plans/2026-07-14-issue-67-guided-setup/README.md),
starting with T9 final closeout.
