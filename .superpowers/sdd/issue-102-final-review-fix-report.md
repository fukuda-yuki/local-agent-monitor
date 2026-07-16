# Issue #102 Final Review Fix Report

## Identity and execution boundary

- Branch: `codex/issue-102-doctor-core`
- Required starting HEAD: `b41a9a36024244cae08d4aebe7a0ab3762bd4c8d`
- Starting status: only the four expected final-review reports were untracked.
- This fix set is intentionally uncommitted for independent re-review.
- No push, PR, main integration, source-specific adapter, live trace, proxy, or UI work was performed.
- The requested GPT-5.6 Luna xhigh runtime was not selectable or verifiable in this agent surface.

## Finding dispositions

### 1. Advisory-only unknown facts

Resolved in `DoctorEvaluator.cs`. Unknown `content_capture` and `raw_access`
remain represented by `completeness_and_content` in `missing_fact_families`, but
no longer block `first_trace_ready` and emit no advisory unless a known negative
value exists. Unknown `completeness` remains terminal-relevant and can still
produce the fixed partial result when the real receipt set would otherwise
support first-ready.

Evidence added:

- direct evaluator counterexamples for content and raw independently;
- real Config CLI evaluate counterexamples;
- real Local Monitor HTTP evaluate counterexamples;
- SQLite application/store completion counterexamples proving the active row
  reaches one atomic completed transition.

Initial RED: 0/2 direct evaluator cases passed. GREEN: 2/2. The combined fresh
cross-surface focused sets later passed as Doctor 7/7, Config CLI 3/3, and Local
Monitor 17/17.

### 2. Authoritative evidence-reference validation

`DoctorValidation.IsValidEvidenceReference` is now the single authority used by
the domain, Config CLI, Local Monitor HTTP parsing, SQLite candidate writes,
accepted selection, and persisted reads. The weaker CLI, HTTP, and persistence
reference validators were removed. The shared guard retains the 1..128, exact
trim, control-character, URI/path/email/raw-marker rules and adds repository
identity/credential recognition, including `user.id`, SSN form, `ghp_` /
`github_pat_`, Basic/Bearer markers, and decoded standalone Base64 credentials.
A non-credential Base64-looking opaque reference and an internal-space opaque
reference remain accepted, pinning the false-positive boundary.

Persisted candidate and accepted-reference reads now validate stored values and
map corrupted unsafe data to the fixed `doctor_store_unavailable` result without
echo. Unsafe writes leave the database byte/row projection unchanged.

Initial RED:

- shared/store: 8 new failures across the four required examples;
- HTTP: 5/8 cases failed, including the four required examples;
- corrupted persisted read: 0/4 passed.

GREEN:

- shared/store safety matrix: 79/79;
- HTTP safety matrix: 8/8;
- CLI direct/complete safety matrix: 64/64;
- corrupted persisted reads: 4/4.

The authoritative URI rule exposed legacy intended-safe test references such as
`trace:...`, `session:...`, and `evidence:1`. Only those safe fixtures were
renamed to opaque hyphen references; the explicit unsafe URI matrix remains.

### 3. Doctor path-family no-store policy

The `/api/doctor` path-family middleware now writes `Cache-Control: no-store`
before dispatch. It therefore covers exact handlers, framework 405 responses,
fallback/unmatched and malformed paths, Host/body/error paths, and downstream
exception handling without inventing new public routes or response DTOs.

Initial RED failed at `GET /api/doctor/evaluations`. GREEN is one deterministic
test that exercises five defined routes against six unsupported common methods
each, plus three unmatched/malformed Doctor paths: 1/1 passed.

### 4. Optional HTTP properties and trusted completion context

HTTP fact-shape parsing now uses the same required/optional split as CLI:
`expected_source_adapter` and `verification_id` may be omitted or explicitly
null. A non-null completion verification ID must equal the route ID.

Production completion accepts omitted/null adapter and verification context but
does not trust it. Inside the existing immediate completion transaction, the
store checks the required source and rejects any conflicting non-null adapter;
every selected candidate must match the persisted verification source/adapter.
The single evaluator callback then receives the route verification ID and the
adapter from those transaction-validated persisted candidates. This preserves
one evaluation, CAS, rollback, and shared-TimeProvider semantics without a
pre-read, heuristic, or race window.

Initial real-HTTP RED: 0/6 omission cases passed. GREEN: 6/6. Additional real
CLI production completion with both optional properties omitted passed and
projected the persisted adapter. A concrete application/store test pins a
conflicting non-null adapter as `expected_source_mismatch` before evaluation.

### 5. Security documentation discoverability

`docs/specifications/security-data-boundaries.md` now contains one concise
First-Trace Doctor cross-reference. It explicitly leaves behavior authority in
`interfaces/first-trace-doctor.md` and does not duplicate the contract.

### 6. Latent interface completion bypass

`SqliteDoctorVerificationStore` no longer implements
`IDoctorVerificationStore`; the ready-fixed/timestamp-ignoring explicit adapter,
its separate candidate read path, and adapter-only exception helpers were
removed. The public source-neutral fact, observation, candidate, verification,
clock, and store contracts remain available for later Issues. Shipped production
composition continues to use the concrete callback-based completion method.

Initial reflection RED found the bypass interface. GREEN reflection verifies
that the production store implements no such interface and that its sole public
completion method requires the atomic decision callback. Production scan finds
no `_ => DoctorCompletionDecision.Ready` or `: IDoctorVerificationStore` match.

## Validation evidence

| Command / scope | Result |
| --- | --- |
| Doctor project full | PASS, 232/232, 0 failed, 0 skipped |
| Config CLI Doctor filter | PASS, 159/159, 0 failed, 0 skipped |
| Local Monitor Doctor filter | PASS, 66/66, 0 failed, 0 skipped |
| `dotnet build CopilotAgentObservability.slnx` | PASS, 0 warnings, 0 errors |
| Playwright Chromium bootstrap | PASS, exit 0 |
| `dotnet test CopilotAgentObservability.slnx` final run | PASS, 5,334/5,334, 0 failed, 0 skipped |
| `git diff --check` | PASS |
| changed Doctor test wait/retry/poll scan | no matches |
| tracked-diff machine-local path scan | no matches |
| production ready-fixed/interface bypass scan | no matches |
| Doctor public candidate / proxy / UI scope scan | no matches |

The first full-solution attempt had one unrelated timeout in
`SetupWrapperTests.SetupWrapper_ForwardsFailureStderrAndExitCode` while the three
test projects ran concurrently: Doctor 232 and Local Monitor 1,459 passed, and
Config CLI passed 3,642/3,643. No setup/wrapper file is in this diff. Systematic
diagnosis ran that exact test alone: it passed 1/1 in 10 seconds. No unrelated
setup change was made. The exact pinned full-solution command was then rerun and
passed Doctor 232, Local Monitor 1,459, and Config CLI 3,643 (5,334 total).

## Files changed

Production/spec:

- `docs/specifications/security-data-boundaries.md`
- `src/CopilotAgentObservability.Doctor/DoctorEvaluator.cs`
- `src/CopilotAgentObservability.Doctor/DoctorValidation.cs`
- `src/CopilotAgentObservability.ConfigCli/Cli/DoctorCli.cs`
- `src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/DoctorStoreContracts.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/DoctorStoreValidation.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorApplicationService.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorVerificationStore.cs`

Owning tests:

- `tests/CopilotAgentObservability.Doctor.Tests/DoctorEvaluatorTests.cs`
- `tests/CopilotAgentObservability.Doctor.Tests/DoctorValidationTests.cs`
- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorApplicationServiceTests.cs`
- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorMigrationTests.cs`
- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorSchemaTests.cs`
- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorVerificationStoreTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/DoctorCliTests.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/DoctorRoutesTests.cs`

## Residual / unverified scope

- Live GitHub Copilot and Claude Code producers remain Issues #103/#104.
- Proxy/UI remains Issue #105.
- Main integration, push, PR, and Issue closure were not performed.
- Independent final re-review and durable ledger/progress updates are root-owned.
