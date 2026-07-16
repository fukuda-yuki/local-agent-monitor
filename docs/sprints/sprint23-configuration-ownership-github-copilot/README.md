# Sprint23 - Configuration Ownership and GitHub Copilot Setup

This sprint coordinates Issues #66 and #67. Current product behavior remains
canonical in requirements, spec, and interface documents.

| Milestone | State | Scope |
| --- | --- | --- |
| M0 contract and orchestration | Complete | Canonical contract, plan, durable ledger |
| M1 ownership ledger foundation | Complete | Issue #66 ledger, plan, and redaction contracts |
| M2 transactional mutation | Complete | Issue #66 atomic file, user environment, compensation, rollback |
| M3 shared setup command surface | Complete | Issue #66 generic dispatcher, CLI process surface, repository wrapper, and Issue #67 production composition; direct CLI/wrapper `status_ready`/0 parity is executable |
| M4 GitHub Copilot adapters | Complete | Issue #67 VS Code, CLI, and App/SDK targets plus self-contained Release ZIP Config CLI and packaged-wrapper parity |
| M5 integration closeout | Complete (feature branch) | Security / concurrency / Migration A / Issue contract / repository-safe evidence reviews PASS C0/I0/M0; build 0/0, Playwright exit 0, solution tests 3,532/3,532 (ConfigCli 2,592 + LocalMonitor 940); Migration A N/A with v1 restart byte evidence; real #66→#67 and repository/release parity PASS; HTTP/proxy/UI N/A, no first-trace claim, and macOS/Linux native execution remains unexecuted |

See [ledger.md](ledger.md) for current execution evidence.
The current session-resume boundary is recorded in
[the Issue #67 guided setup plan](../../superpowers/plans/2026-07-14-issue-67-guided-setup/README.md),
starting with T9 final closeout.
