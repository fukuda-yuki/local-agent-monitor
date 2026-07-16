# Issue #102 Task 7 Report — Production Integration and Hardening

## Identity

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-doctor-core`
- Starting HEAD: `52794b3b780235081a7465e414ac1315a98f31bf`
- Final state: reviewable uncommitted diff; no commit, push, PR, or main integration
- Canonical specs: unchanged; the implementation matched the current requirements/specifications and no conflict was found

## Outcome

Task 7 connects the reviewed Doctor domain, SQLite persistence, Config CLI, and Local Monitor HTTP lanes through one internal SQLite-backed application service. Production CLI lifecycle commands now use the supplied database. Local Monitor initializes Doctor against the monitor database and shared `TimeProvider`, while Doctor initialization exceptions are isolated to verification lifecycle routes. Stateless evaluation remains the pure shared `DoctorEvaluator` path.

Completion uses the concrete `SqliteDoctorVerificationStore.Complete` trusted-candidate callback. It does not call the explicit `IDoctorVerificationStore.Complete` adapter. Candidate resolution, conversion to typed `DoctorObservation` values, one evaluator invocation, evidence acceptance, and terminal CAS remain inside the store transaction.

The independent Task 7 review's three Important findings are resolved: observation now rejects verification source/adapter mismatches before persistence; both default CLI and default Local Monitor have bounded full lifecycle matrices through the shared service/concrete store; and all 19 Issue #102 evidence files identified by the review use neutral linked-worktree identities.

## RED/GREEN Evidence

1. Shared completion service:
   - RED: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorApplicationServiceTests.Complete_ResolvesTrustedCandidatesAndEvaluatesExactlyOnce --no-restore -v minimal`
   - Result: exit 1, expected `CS0103` because `SqliteDoctorApplicationService` did not exist.
   - GREEN: the same command passed 1/1 after the minimum service/store implementation.
2. Production CLI wiring:
   - The existing interim test `ActualCommand_DefaultProductionLifecycleDoesNotCreateDatabase` failed after wiring (`expected exit 5`, `actual exit 2` because its fixed historical expiry was invalid at the production clock).
   - It was replaced with the Task 7 contract: dynamically bounded expiry and real start/status/cancel against the supplied SQLite database. The Config CLI Doctor suite then passed 152/152.
3. Service lifecycle hardening:
   - Initial run passed 3/4. The single failure was test-fixture evidence: expected `InvalidInput`, actual `EvidenceNotFound`; the `Context` helper had cleared the caller observations the test claimed to supply.
   - Restoring those observations corrected the fixture. No production change was made for that failure. The service suite passed 5/5, including counted evaluation, non-mutation, terminal outcomes, and post-evaluation store failure.
4. Independent-review trust-gate fix:
   - RED: `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~ObserveCandidate_SourceOrAdapterMismatchIsRejectedWithoutPersistence --no-restore -v minimal` failed 2/2; both cases expected `ExpectedSourceMismatch` and received `VerificationActive`.
   - GREEN: after adding the pre-insert source/adapter comparison, the same command passed 2/2. The test proves no persistence, no candidate echo, active revision/evidence unchanged, and the same state after reopening.
   - The expanded real CLI matrix initially exposed a test-only stderr assumption: non-ready `EvaluationCompleted` has `Success=true`, so exit 3 intentionally has empty stderr. Aligning the helper with the established `Success` contract produced GREEN. The default Local Monitor matrix passed on its first execution; no adapter production change was needed.

## Files

### Production

- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorApplicationService.cs` — new shared application semantics, initialization isolation, trusted observation conversion, one-shot evaluation, and fixed result projection.
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorVerificationStore.cs` — absolute-expiry start overload plus observation-time verification source/adapter trust gate before candidate persistence.
- `src/CopilotAgentObservability.ConfigCli/Cli/DoctorCliApplication.cs` — thin production SQLite CLI adapter.
- `src/CopilotAgentObservability.ConfigCli/Cli/DoctorCli.cs` — production default selects the SQLite adapter.
- `src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs` — thin production HTTP adapter and internal-only candidate observation seam.
- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` — monitor DB/shared-clock composition plus isolated initialization-failure factory seam.

### Tests

- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorApplicationServiceTests.cs`
- `tests/CopilotAgentObservability.Doctor.Tests/DoctorCrossSurfaceContractTests.cs`
- `tests/CopilotAgentObservability.Doctor.Tests/DoctorEvaluatorTests.cs` (shares the existing 20-state fixture within the test assembly)
- `tests/CopilotAgentObservability.ConfigCli.Tests/DoctorCliTests.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/DoctorRoutesTests.cs`

## Production Flow

1. `Start` initializes schema idempotently, reads the configured clock once, validates `expires_at - now` in the inclusive 1..30 minute window, and creates UUIDv7/revision-1 active state.
2. `Status` reads the same store/clock and projects active, terminal, expired, missing, busy, or unavailable outcomes.
3. Internal `ObserveCandidate` persists validated source-neutral candidates only after matching the active verification's expected source and adapter. Mismatches return canonical `expected_source_mismatch` before insert. The seam remains internal; no public CLI command or HTTP route was added.
4. `Complete` rejects non-empty caller observations and mismatched verification context before evaluation. The concrete store resolves selected candidates and source/adapter/expiry constraints in its immediate transaction. The service converts resolved candidates into typed trusted observations and invokes the evaluator exactly once. Non-ready/partial rolls back without evidence/revision mutation; ready accepts ordered evidence and commits terminal CAS atomically. Post-evaluation store failures return only the sanitized store outcome.
5. `Cancel` uses the concrete store CAS and the same injected clock.

## Cross-Surface and Lifecycle Matrix

| Area | Executable evidence |
| --- | --- |
| All 20 catalog states | One production-DTO loop compares exact canonical result JSON from direct evaluator, real Config CLI evaluate, and real Local Monitor evaluate. Re-serialization is byte-stable, including terminal/advisory combinations. |
| Real CLI lifecycle | One default `CliApplication.Run` matrix covers non-ready, partial, stale revision, ready completion, completed conflict, derived expiry, cancellation, and cancelled conflict through the SQLite application/concrete store. It asserts exit/result JSON and revision/state/accepted-evidence non-mutation. |
| Real HTTP lifecycle | One default Local Monitor route matrix covers the same outcomes through the monitor DB/shared clock, asserting fixed HTTP statuses/result JSON and concrete-store state. The separate D051 test retains exact readiness isolation evidence. |
| Non-ready and partial | Both real surfaces return the exact evaluator projection with active revision 1 and no accepted evidence; a subsequent ready completion succeeds. |
| Ready and terminal conflict | Ready commits revision 2/completed and ordered accepted refs; repeated completion returns `verification_already_completed`. |
| Cancel, expiry, stale | Deterministic-clock tests cover stale revision, successful cancel, already-cancelled, expired status, and expired cancellation. Existing store checkpoint/race tests remain green in the full Doctor suite. |
| Counted evaluator | Ready completion proves one call after trusted resolution; invalid caller context proves zero calls; post-evaluation store failure proves one call with rollback/sanitized unavailable result. Adapters only project the returned result. |

## D051 Isolation

The HTTP integration test captures readiness as status, `Content-Type`, `Cache-Control`, and exact body bytes (hex encoding for value equality). It proves equality:

- before and after Doctor start;
- after status;
- after ready completion;
- after cancel;
- between an available Doctor host and a host whose Doctor initialization factory throws.

On the failed-initialization host, verification returns sanitized `doctor_store_unavailable`, stateless evaluation still returns normally, host startup succeeds, and readiness bytes/status/headers remain identical. Existing readiness thresholds, fields, reasons, status mapping, and configuration are untouched.

## Safety and Boundary Matrix

| Boundary | Evidence/result |
| --- | --- |
| Caller-controlled observations | Completion rejects them before candidate resolution/evaluation. |
| Trusted metadata | Observation-time source/adapter mismatches are rejected without persistence or echo. Only verification-authorized, store-resolved candidates become canonical observations; complete-time defense remains. |
| Exception/SQLite/path leakage | Initialization exception marker (`sqlite SECRET_PATH`) is absent from HTTP output; post-evaluation exception marker is reduced to a canonical unavailable result. Existing CLI/HTTP invalid-input and leak matrices remain green. |
| Public surface | Static review found no candidate command or route, proxy DTO, Razor/JS/UI change, source enum, or heuristic matching. `ObserveCandidate` remains internal. |
| Security controls | Doctor route parsing, body limit, Host validation, mutation-only same-origin/CSRF, and `no-store` behavior were not changed; the focused HTTP suite passed. |
| Specs and later issues | No docs/spec, proxy/UI, or #105 work changed. The internal candidate seam and full 12-family snapshot shape are sufficient handoff points for #103/#104 without implementing their source-specific ingestion. D059 exact-ready-with-schema-drift coverage remains green. |
| Preserved evidence | The required fixed-string machine-path scan returns no matches after sanitizing 19 Issue #102 identity lines. Branch and HEAD evidence is preserved. |

## Validation

Fresh commands run from the repository root:

| Command | Result |
| --- | --- |
| `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj` | Passed 216/216; failed 0; skipped 0. |
| `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor` | Passed 152/152; failed 0; skipped 0. |
| `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Doctor` | Passed 49/49; failed 0; skipped 0. |
| `dotnet build CopilotAgentObservability.slnx` | Succeeded; 0 warnings; 0 errors. Informational preview-SDK `NETSDK1057` messages only. |
| `pwsh scripts\test\install-playwright-chromium.ps1` | Exit 0. |
| `dotnet test CopilotAgentObservability.slnx` | Passed 5,294/5,294 total: Doctor 216, LocalMonitor 1,442, ConfigCli 3,636; failed 0; skipped 0. |
| Required fixed-string machine-path scan over `.superpowers`, Issue #102 sprint evidence, and `docs/superpowers` | No matches. |
| `git diff --check` | Clean. |

The brief's literal command `rg -n "Thread\.Sleep|Task\.Delay|retry|poll" ...\Doctor*` is not executable as written by native `rg` on Windows PowerShell: the `Doctor*` path operands are not expanded and produce OS error 123; its broad `retry` alternative also matches the existing identifier `retryability`. This failed command is not reported as success. A diagnostic run using the explicitly expanded Doctor file list and word-boundary pattern found no `Thread.Sleep`, `Task.Delay`, `retry`, or `poll` usage (`rg` exit 1 = no matches).

## Self-Review

- Atomicity: completion calls the concrete callback overload, and only that overload; the explicit interface completion adapter remains unused by the new application path.
- Shared clock: Local Monitor passes its host `TimeProvider` to the Doctor store; CLI uses `TimeProvider.System`. Absolute-expiry start reads that provider once.
- Evaluator: production evaluation uses `DoctorEvaluator`; completion invokes it once after resolution and returns the captured result on evaluator outcomes. Store failures after evaluation do not return a mixed success/error result.
- Migration/startup: Doctor schema creation is idempotent and occurs during application creation. Store-returned busy/unavailable is retained for lifecycle routes; thrown initialization failure is caught without failing host construction.
- D051: exact readiness bytes/status/headers are pinned across lifecycle and initialization failure.
- Security/no-leak: no raw payload, body, path, candidate metadata, credential, authorization, parser/type, exception, or SQLite text is added to canonical results/logs.
- Observation trust: source and adapter are compared to the loaded active verification inside the immediate transaction before any insert; complete-time checks are retained as defense in depth.
- Scope: no dependency, project/solution/IVT, canonical spec, proxy, UI, public candidate surface, compatibility fallback, or unrelated refactor was added.
- Test quality: deterministic clocks/direct state/checkpoints are used; no sleep, polling, or retry was added.

## Unresolved Items

- No product-code or test failure remains in the executed suites.
- The only validation caveat is the brief's Windows-incompatible literal wildcard `rg` command documented above; the expanded diagnostic scan is clean but is not represented as a substitute success for the literal command.
- The user-supplied `.superpowers/sdd/issue-102-task-07-brief.md` remains untracked; its sole modification is the required neutral worktree identity sanitization.
