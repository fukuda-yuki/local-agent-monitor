# Issue #102 Task 7 Brief — Production Integration and Hardening

## Identity

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-doctor-core`
- Starting HEAD: `52794b3b780235081a7465e414ac1315a98f31bf`
- Preferred runtime: GPT-5.6 Luna with xhigh reasoning; dispatch cannot enforce or verify it.
- Report path: `.superpowers/sdd/issue-102-task-07-report.md`
- Report destination: `/root`

## Purpose

Connect the four independently reviewed Doctor lanes into one production lifecycle while preserving the shared result, atomic trusted-evidence path, Local Monitor startup/readiness isolation, and surface/security boundaries. Add integration tests that expose cross-lane mismatches rather than re-testing adapters in isolation.

## Required Reading and Identity

Before editing, verify worktree/branch/HEAD/status. Read AGENTS.md, workflow/TDD, requirements/spec, complete Doctor/CLI/security/readiness/persistence interfaces, architecture/D059/D060, Task 3–6 reports and final review evidence in their child worktrees, current domain/store/CLI/HTTP source/tests, and this brief.

Important reviewed handoff: production completion must use `SqliteDoctorVerificationStore.Complete`'s concrete trusted-candidate/evaluator callback path with the same injected `TimeProvider`. Do not use the explicit `IDoctorVerificationStore.Complete` adapter that hard-codes Ready and can bypass evaluation.

## Scope and Ownership

- Add a shared source-neutral verification application/service in the lowest appropriate layer, plus thin production adapters for `IDoctorCliApplication` and `IDoctorHttpApplication`.
- Wire Config CLI lifecycle commands to the SQLite-backed service for the supplied database path.
- Wire Local Monitor to initialize Doctor schema/store/application using its monitor database and shared `TimeProvider`.
- Add only the minimum internal factory/test seams needed to inject store initialization failure and trusted candidates.
- Add/update Doctor integration tests in Doctor.Tests, ConfigCli.Tests, and LocalMonitor.Tests as appropriate.
- Update solution/project/IVT files only if necessary; no external dependency.

Do not edit canonical specs unless an actual conflict is discovered; stop/report rather than invent behavior. Do not add proxy DTO, Razor, JavaScript, Canvas/UI, source-specific adapter, live trace, public candidate route/command, source enum, heuristic matching, fallback/compatibility shim, or unrelated refactor.

## Production Lifecycle Contract

- Stateless evaluation always uses the shared pure `DoctorEvaluator`, even when Doctor schema initialization is busy/unavailable.
- CLI and Local Monitor lifecycle operations use the same application semantics and fixed `DoctorResult` codes/projections.
- `Start`: use one injected clock for `started_at`, derive/validate `expires_at - now` within 1..30 minutes, create schema idempotently, UUIDv7/revision 1/active.
- `Status`: derive expiry from the same clock; map not-found/busy/unavailable/active/expired exactly.
- Internal `ObserveCandidate`: source-neutral future #103/#104 seam only; no public CLI/HTTP route. Enforce expected verification/source/adapter and trusted persisted class/kind.
- `Complete`: validate route/command ID, revision, snapshot verification context/source/adapter, empty caller observations, and selected refs. Within the store transaction resolve candidates, convert them to typed trusted `DoctorObservation` values, evaluate exactly once, capture that exact result, and return:
  - non-ready/partial with unchanged active verification/evidence/revision;
  - ready with evidence acceptance and lifecycle CAS committed atomically;
  - fixed conflict/expiry/source/evidence/busy/unavailable outcomes otherwise.
- `Cancel`: use concrete CAS path and same clock; fixed outcomes.
- Never expose database/input paths, candidates' trusted metadata beyond canonical observations, raw/body/PII/credential/authorization/SQLite/parser/type/exception text.

## Local Monitor Isolation and D051

- Doctor schema initialization failure must not fail `MonitorHost.Build`, server startup, ingestion/projection, stateless evaluation, or any existing readiness check.
- Verification routes alone return `doctor_store_busy`/`doctor_store_unavailable` as applicable.
- Add executable byte/status/header comparison of `GET /health/ready` before and after Doctor start/status/complete/cancel and between equivalent hosts with Doctor store available versus injected initialization failure. Existing D051 thresholds, config names, reasons, fields, status mapping, and body bytes must remain unchanged.
- Keep existing loopback/Host, mutation-only same-origin/CSRF, body-limit/no-store controls unchanged.

## Early Cross-Surface and Safety Tests — TDD

Write failing integration tests before production wiring. Required evidence:

1. All 20 catalog states (including terminal/advisory combinations) use production DTOs and produce equal canonical machine results through direct evaluation, real Config CLI evaluate, and real Local Monitor evaluation. Prove adapters do not reorder/re-evaluate; repeated JSON is byte-stable.
2. A full lifecycle crosses real production composition: start, status, internal candidate observation, non-ready/partial no-mutation, ready complete, terminal conflicts, expiry, cancel, stale race outcome. Cover both CLI and HTTP projections over shared service/store semantics without adding a candidate public surface.
3. Counted evaluator proof: completion evaluates once after trusted candidate resolution; CLI/HTTP projection does not evaluate again.
4. Raw/secret/PII/local/database/input path/authorization/exception/type/SQLite marker negative matrix across CLI stdout/stderr, HTTP body/headers, store outcomes, and logs where applicable.
5. D051 byte/status/threshold isolation described above, including store unavailable.
6. Compile/shape test demonstrates #103/#104 can provide all 12 facts and `DoctorEvidenceCandidate`/internal observation without source-specific enum or heuristic. D059 unrelated schema drift still permits exact ready with advisory.
7. Surface-boundary test/diff proves no proxy/UI/public candidate command/route was added.
8. No sleep, polling, or retry; use `TimeProvider`, barriers/checkpoints, deterministic locks, and direct state control.

## Completion Conditions

- Production CLI no longer defaults verification to unavailable when the Doctor store is healthy.
- Production Local Monitor lifecycle routes use the SQLite store and remain isolated on Doctor initialization failure.
- All integration/focused suites pass with zero failed/skipped tests; build has zero warnings/errors.
- Root-visible diff is scoped, no runtime artifact, no canonical/spec drift, and no later-Issue implementation.
- Self-review covers security, atomicity, shared clock/evaluator, migration initialization order, D051, no-leak, #103/#104/#105 boundaries, test quality, and unnecessary scope.
- Leave a reviewable uncommitted diff. Do not commit; independent review occurs first.

## Verification

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Doctor
dotnet build CopilotAgentObservability.slnx
rg -n "Thread\.Sleep|Task\.Delay|retry|poll" tests\CopilotAgentObservability.Doctor.Tests tests\CopilotAgentObservability.ConfigCli.Tests\Doctor* tests\CopilotAgentObservability.LocalMonitor.Tests\Doctor*
git diff --check
git status --short --branch --untracked-files=all
```

## Report

Write `.superpowers/sdd/issue-102-task-07-report.md` with identity, RED/GREEN cycles, files, production flow, cross-surface/lifecycle/D051/safety/handoff matrices, exact commands/counts, self-review, unresolved items, and explicit later-Issue non-scope. Report to `/root`; do not commit/push/PR/main-integrate.
