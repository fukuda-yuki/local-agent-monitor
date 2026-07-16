# Issue #102 Doctor Core Implementation Plan

> **For agentic workers:** Use `superpowers:subagent-driven-development` to execute this plan task by task, and use `superpowers:test-driven-development` before changing production code.

**Goal:** Deliver the shared, deterministic Doctor core on `codex/issue-102-doctor-core`, with identical domain results across direct, CLI, and HTTP surfaces and an atomic SQLite verification lifecycle.

**Architecture:** A new dependency-light Doctor project owns the public contract, validation, deterministic state engine, serialization, human projection, and verification interfaces. Config CLI and Local Monitor are adapters over that shared result. Persistence.Sqlite owns the Doctor v1 schema and transactional lifecycle. Source-specific producers supply facts and candidate evidence only through shared contracts in later Issues.

**Tech stack:** .NET 10, ASP.NET Core minimal APIs, `Microsoft.Data.Sqlite`, xUnit, PowerShell.

## Execution Identity and Boundaries

- Primary worktree: the linked worktree containing this file.
- Primary branch: `codex/issue-102-doctor-core`.
- Base: `8940b34f4e031b894705682dc50c079a9ed5c180`.
- Approved design commit: `3e0760b961465fa78c9f1e6cc48d9f8002cd34d9`.
- In scope: canonical specifications, shared Doctor domain, all 20 fixed states, deterministic evaluator, CLI, HTTP, SQLite Doctor v1 lifecycle, migration/restart, atomicity/rollback/stale-state/concurrency tests, security/no-leak tests, derived documentation, and durable evidence.
- Out of scope: Issues #68, #100, #103, #104, and #105 implementation; source-specific adapters; live first-trace evidence; proxy DTOs; Razor/JavaScript/Canvas UI; push, PR, or `main` integration.
- Completion means verified local feature-branch completion. It does not mean integration into `main`.
- Do not add external dependencies, compatibility shims, permissive parsing, heuristic matching, source-specific state enums, sleep, polling, or retry-based tests.
- Use synthetic or anonymized fixtures only. Never persist or report raw prompts, responses, credentials, authorization values, local database paths, exception text, or PII.

## Contract Table: DTOs, Producers, and Consumers

This table is the first implementation checkpoint. Any change to a row requires a canonical specification update before code changes.

| Contract | Owner / producer | Direct consumer | CLI consumer | HTTP consumer | Persistence consumer | Later-Issue producer |
| --- | --- | --- | --- | --- | --- | --- |
| `DoctorFactSnapshot` with 12 explicit-known/unknown fact families | Doctor callers | `DoctorEvaluator` | input-file parser | evaluation request parser | none | #103 and #104 adapters |
| `DoctorEvaluation` and ordered `DoctorState` values | `DoctorEvaluator` | tests and composition | JSON/human projectors | response projector | verification completion | #105 UI through a later proxy boundary |
| `DoctorResult` (`doctor.v1`) | Doctor application service | direct executable tests | stdout projector and exit mapping | JSON response and status mapping | none | #105 may consume without redefining it |
| `DoctorVerification` | verification store/service | lifecycle tests | start/status/complete/cancel | start/status/complete/cancel | SQLite row mapper | #103/#104 verification flows |
| `DoctorEvidenceCandidate` / `ObserveCandidate` | trusted source-specific child integration | verification service | no public command | no public route | candidate row writer | #103/#104 only |
| fixed error code | domain/application/store adapter | assertions | exit-code mapper | HTTP status mapper | typed failures | all later consumers |

All surfaces must project the same already-evaluated `DoctorResult`; adapters must not re-evaluate facts or redefine state ordering.

## Public Contract

### Shared result

`DoctorResult` has `schema_version = "doctor.v1"`, `success`, `code`, optional `evaluation`, and optional `verification`. An evaluation includes source, primary state, ordered states, and missing fact families. Every state contains `schema_version`, `state_code`, `severity`, `source_surface`, `evidence_refs`, `reason_codes`, `next_action`, `retryability`, `observed_at`, and `verification_id`.

A verification includes its ID, expected source/adapter, effective state, revision, start/expiry/terminal timestamps, and accepted evidence references.

Limits:

- source and adapter: `[a-z0-9][a-z0-9._-]{0,63}`;
- evidence reference: at most 128 characters and at most 16 accepted entries;
- verification candidates: at most 100;
- CLI input file and HTTP request body: at most 64 KiB;
- verification window: 1 through 30 minutes;
- identifiers: canonical lowercase UUIDv7;
- timestamps: UTC RFC 3339 round-trip form.

### Fixed state catalog

| State | Severity | Retryability | Next action |
| --- | --- | --- | --- |
| `monitor_not_installed` | `error` | `after_action` | `install_monitor` |
| `monitor_not_running` | `error` | `after_action` | `start_monitor` |
| `receiver_not_bound` | `error` | `after_action` | `restart_monitor` |
| `port_owned_by_foreign_process` | `error` | `after_action` | `free_or_change_port` |
| `endpoint_mismatch` | `error` | `after_action` | `update_source_endpoint` |
| `protocol_mismatch` | `error` | `after_action` | `use_http_protobuf` |
| `signal_disabled` | `error` | `after_action` | `enable_trace_signal` |
| `unsupported_source_version` | `error` | `after_action` | `use_supported_source_version` |
| `feature_unavailable` | `error` | `after_action` | `use_supported_source_surface` |
| `agent_restart_required` | `warning` | `after_action` | `restart_source_process` |
| `endpoint_unreachable` | `error` | `after_action` | `verify_endpoint_reachability` |
| `payload_rejected` | `error` | `after_action` | `inspect_rejected_payload` |
| `raw_persisted_projection_pending` | `warning` | `automatic` | `wait_for_projection` |
| `projection_failed` | `error` | `after_action` | `open_projection_diagnostics` |
| `session_unbound` | `error` | `after_action` | `select_exact_session` |
| `content_capture_disabled` | `warning` | `after_action` | `enable_content_capture_if_desired` |
| `sanitized_only_raw_unavailable` | `warning` | `after_action` | `restart_without_sanitized_only_if_desired` |
| `schema_drift_detected` | `warning` | `after_action` | `review_source_diagnostics` |
| `ready_no_real_trace` | `info` | `after_action` | `run_bounded_source_interaction` |
| `first_trace_ready` | `info` | `none` | `open_verified_trace_or_session` |

Primary selection is the first blocking state, otherwise terminal state 19 or 20. Content, sanitized-only, and schema-drift states are advisories placed after the terminal state. Unrelated schema drift alone does not fail exact verification. In v1, each reason code equals its state code.

### CLI and HTTP

CLI commands:

```text
doctor evaluate --input <file> [--json]
doctor verification start --database <file> --source-surface <value>
  [--source-adapter <value>] --expires-at <RFC3339> [--json]
doctor verification status --database <file> --verification-id <UUIDv7> [--json]
doctor verification complete --database <file> --verification-id <UUIDv7>
  --expected-revision <positive-int> --input <file> [--json]
doctor verification cancel --database <file> --verification-id <UUIDv7>
  --expected-revision <positive-int> [--json]
```

HTTP routes:

```text
POST /api/doctor/evaluations
POST /api/doctor/verifications
GET  /api/doctor/verifications/{verificationId}
POST /api/doctor/verifications/{verificationId}/complete
POST /api/doctor/verifications/{verificationId}/cancel
```

Valid diagnosis is HTTP 200 and partial semantic input is 422. Start is 201; status, complete, and cancel are 200. Malformed is 400, missing 404, stale/terminal conflict 409, expired 410, and store busy/unavailable 503. State-changing routes require same-origin and `x-monitor-csrf: local-monitor`. Every response uses `Cache-Control: no-store` and omits paths, raw data, PII, credentials, and exception details.

CLI exits are fixed: ready/completed operation 0, invalid input 2, non-ready/partial 3, verification conflict 4, store/internal failure 5.

### SQLite

Create `schema_version(component='doctor', version=1)`, `doctor_verifications`, and `doctor_verification_evidence`. The evidence table stores both candidates and accepted evidence using accepted flag/ordinal. `ObserveCandidate` is internal and has no #102 public route. Complete selects only existing candidates and rejects missing, expired, source-mismatched, and synthetic-only evidence. Evidence acceptance and lifecycle compare-and-swap commit in one transaction.

Persist active/completed/cancelled states; derive expiry from `TimeProvider`. Doctor schema failure degrades verification routes only. Local Monitor startup, ingestion, the evaluation route, and D051 readiness remain unchanged.

## Task 0 — Execution Preflight and Durable Plan

Root agent:

1. Verify linked worktree, branch, HEAD, submodule state, and clean status with `superpowers:using-git-worktrees`.
2. Run the pinned build, Playwright bootstrap, and full solution tests on the unchanged design HEAD.
3. If any command fails, stop feature work and use `superpowers:systematic-debugging`; never substitute another command as success.
4. Save this plan, create `.superpowers/sdd/progress.md`, and update `docs/sprints/issue-102-doctor-core/ledger.md`.
5. Self-review the documentation diff and run `git diff --check`.
6. Commit: `Issue #102: docs(doctor): record implementation plan`.

## Task 1 — Canonical Specification First

Use a fresh implementer and a separate read-only reviewer.

Files:

- `docs/requirements.md`
- `docs/spec.md`
- `docs/specifications/interfaces/first-trace-doctor.md`
- `docs/specifications/interfaces/config-cli.md`
- `docs/specifications/README.md`
- `docs/architecture.md`
- `docs/decisions.md` (D060)

D060 fixes the shared domain, explicit verification, separation from D051 readiness, and the #105 proxy/UI handoff. Promote every contract, limit, status/exit mapping, security boundary, and migration rule above into canonical sources.

Validation:

```powershell
git diff --check
rg -n "doctor.v1|/api/doctor|first_trace_ready|D060" docs
```

Commit after independent approval: `Issue #102: docs(doctor): specify deterministic core contract`.

## Task 2 — Thin Executable Cross-Surface Slice

Use a fresh implementer, TDD, and a separate reviewer.

1. Add `src/CopilotAgentObservability.Doctor/` for contracts, serializer, minimal evaluator, human projector, and verification interfaces.
2. Add `tests/CopilotAgentObservability.Doctor.Tests/` with a production DTO fixture and cross-surface test.
3. Add only minimal `doctor evaluate` composition to Config CLI and `POST /api/doctor/evaluations` to Local Monitor.
4. Add solution/project references and necessary `InternalsVisibleTo` once; add no external dependency.
5. RED: pass one sanitized fixture through the direct evaluator, real CLI invocation, and real HTTP route, observing missing command/route failure.
6. GREEN: converge the `monitor_not_running` slice on one equal `DoctorResult`; prove DTO equality, byte-deterministic JSON, and human projection from the same result.

Validation:

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorCrossSurfaceContractTests
dotnet build CopilotAgentObservability.slnx
```

Commit after approval: `Issue #102: feat(doctor): add shared cross-surface slice`.

## Tasks 3–6 — Independent Worktree Lanes

Create child worktrees from the approved Task 2 commit. Verify branch, HEAD, and clean status before every dispatch and again before integration. Never let two agents edit one worktree. Run at most three workers concurrently, then dispatch Task 6 when a slot is available. Every task brief must contain purpose, scope, non-scope, constraints, completion conditions, verification commands, worktree, branch, HEAD, and report path. Every implementer uses TDD, self-review, and a local commit; a different agent reviews the brief, report, diff, and tests. Only approved commits may be cherry-picked.

### Task 3 — Domain evaluator

- Branch: `codex/issue-102-domain`.
- Ownership: Doctor project and Doctor.Tests domain test files only.
- Implement explicit unknown states for 12 fact families, the fixed catalog, blocking/terminal/advisory ordering, partial facts, synthetic/real separation, validation bounds, deterministic serialization, and leak prevention.
- Do not add source-specific states or heuristic matching.
- Tests: `DoctorCatalogTests`, `DoctorEvaluatorTests`, `DoctorValidationTests`, `DoctorDeterminismTests`.
- Commit: `Issue #102: feat(doctor): implement deterministic state engine`.

### Task 4 — SQLite lifecycle and migration

- Branch: `codex/issue-102-store`.
- Ownership: `Persistence.Sqlite/Doctor` and Doctor persistence/migration tests only.
- Implement schema v1, observe candidate, start/get, complete/cancel CAS, derived expiry, and busy/unavailable mapping.
- Add migration and transition checkpoints; prove exact rollback on injected faults.
- Runtime-copy committed monitor v1-v4 fixtures without regenerating or modifying them.
- Test v1-v4 to monitor v5 plus Doctor v1, close/reopen, second startup, manifest/sentinel/integrity preservation, migration rollback, evidence/lifecycle atomic rollback, barrier-controlled complete/cancel race, stale revision, and controlled SQLite write lock. Do not sleep, poll, or retry.
- Commit: `Issue #102: feat(doctor): persist verification lifecycle`.

### Task 5 — Config CLI lifecycle

- Branch: `codex/issue-102-cli`.
- Ownership: `ConfigCli/Doctor`, CLI dispatch/help, and ConfigCli Doctor tests only.
- Implement all five commands, strict option parsing, 64 KiB bound, JSON/human projection, and fixed exit codes.
- Never emit path, raw JSON, SQLite, credential, or exception text to stdout/stderr.
- Keep production store wiring for Task 7; use injected interfaces in this lane.
- Commit: `Issue #102: feat(cli): add doctor commands`.

### Task 6 — Local Monitor Doctor API

- Branch: `codex/issue-102-http`.
- Ownership: `LocalMonitor/Doctor`, minimal host registration, and Doctor HTTP tests only.
- Implement all five routes, body bounds, content type checks, same-origin/CSRF, no-store, and fixed status mapping with injected evaluator/store.
- Do not reorganize `MonitorHost`, add Razor/JavaScript/Canvas, or add proxy DTOs.
- Commit: `Issue #102: feat(monitor): expose doctor API`.

## Task 7 — Primary-Branch Integration and Hardening

After all four independent reviews pass, root verifies each worktree identity/status and cherry-picks approved commits in domain, store, CLI, HTTP order. A fresh integration implementer then:

1. wires Config CLI composition to `SqliteDoctorVerificationStore`;
2. isolates Doctor schema success/failure in MonitorHost and registers evaluation/verification services;
3. extends cross-surface coverage through all 20 states and lifecycle results;
4. proves CLI JSON, human, and HTTP project the same result without re-evaluation;
5. byte-compares D051 readiness body/status/threshold before and after verification transitions and store-unavailable mode;
6. runs a negative matrix for raw data, secrets, PII, local paths, authorization, and exception markers;
7. proves #103/#104 can supply facts/candidates without a source-specific enum;
8. proves proxy/UI additions are absent through diff and surface-boundary tests.

Validation:

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Doctor
rg -n "Thread\.Sleep|Task\.Delay|retry|poll" tests\*Doctor*.cs
git diff --check
```

Commit after review: `Issue #102: test(doctor): close integration and safety matrix`.

## Task 8 — Derived Documentation and Ledger

Use a fresh documentation implementer and a separate specification reviewer.

1. Add the evaluate/verification workflow, CSRF, sanitized output, and first-trace non-guarantee to the Local Monitor user guide.
2. Add focused smoke commands to the repository workflow.
3. Record feature-branch implementation/validation in `docs/task.md` without claiming Issue closure or `main` integration.
4. Record commit ranges, focused/full results, reviewer verdicts, accepted minor findings, and unverified #103/#104/#105 interfaces in the durable ledger.
5. Review derived documentation in both directions against canonical specifications.

Commit after approval: `Issue #102: docs(doctor): document core workflow`.

## Validation and Independent Final Review

Root must run and inspect:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
git diff --check
git status --short --branch
```

Required evidence is exit 0, zero build warnings/errors, zero failed/skipped tests, and no tracked runtime artifacts. A failed required command triggers `superpowers:systematic-debugging`; no alternate command substitutes for it.

Run four read-only reviews on the same HEAD:

1. security/data safety: raw/PII/path/credential leakage, CSRF, same-origin, errors/logs;
2. concurrency/migration: CAS, busy, rollback, v1-v4 restart, no sleep/retry, D051 isolation;
3. specification/Issue contracts: 20 states, DTO identity, CLI/HTTP mappings, #103/#104 handoff, #105 proxy/UI boundary;
4. whole branch: maintainability, test quality, unnecessary scope, and accepted minor findings.

Consolidate Critical/Important findings into one fix task, rerun focused and full validation, and return the same reviewers to the changed HEAD. Root independently compares canonical requirements, full diff, commit log, and test output requirement by requirement. Record the audit in `Issue #102: docs(doctor): record validation closeout`.

Completion requires every task and final review to pass, pinned validation to pass, durable evidence to contain commit ranges/results/residuals, the feature worktree to be clean, local commits only, and a successful durable-goal audit. Report feature-branch completion separately from the still-unperformed `main` integration.
