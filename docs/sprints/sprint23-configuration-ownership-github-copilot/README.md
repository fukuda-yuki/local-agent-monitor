# Sprint23 - Configuration Ownership and GitHub Copilot Setup

This sprint coordinates Issues #66 and #67. Current product behavior remains
canonical in requirements, spec, and interface documents.

| Milestone | State | Scope |
| --- | --- | --- |
| M0 contract and orchestration | Complete | Canonical contract, plan, durable ledger |
| M1 ownership ledger foundation | Complete | Issue #66 ledger, plan, and redaction contracts |
| M2 transactional mutation | Complete | Issue #66 atomic file, user environment, compensation, rollback |
| M3 shared setup command surface | T2 gate passed; final composition pending | Issue #66 generic dispatcher, CLI process surface, and byte-faithful repository wrapper passed full validation; Issue #67 T7 must still wire production composition and prove direct-CLI/wrapper `status_ready`/0 parity before final completion |
| M4 GitHub Copilot adapters | Pending | Issue #67 VS Code, CLI, and App/SDK targets |
| M5 integration closeout | Pending | Cross-surface tests, independent reviews, full validation |

See [ledger.md](ledger.md) for current execution evidence.
The current session-resume boundary is recorded in
[the M3 implementation handoff](milestones/M3-shared-setup-command-surface/handoff-prompt.md).
