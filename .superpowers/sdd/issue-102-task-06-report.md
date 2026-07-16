# Issue #102 Task 6 Report — Local Monitor Doctor API

## Identity

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-http`
- Starting HEAD: `80fd19ebf497a092a4ae112e3321bcf70bcdd7a4`
- Preferred runtime request: GPT-5.6 Luna with xhigh reasoning was not enforceable or independently verifiable.
- Scope: Local Monitor Doctor routes, injected application seam, Doctor-specific real-host tests, and the smallest Host-header projection change.

## Implementation

- Exposed all five canonical routes:
  - `POST /api/doctor/evaluations`
  - `POST /api/doctor/verifications`
  - `GET /api/doctor/verifications/{verificationId}`
  - `POST /api/doctor/verifications/{verificationId}/complete`
  - `POST /api/doctor/verifications/{verificationId}/cancel`
- Added `IDoctorHttpApplication` as the Local Monitor injection seam and registered the injected/default instance in `MonitorHost`.
- Kept the production default stateless for evaluation. Verification operations return sanitized `doctor_store_unavailable` until Task 7 supplies the production store/application composition.
- Did not compose SQLite Doctor persistence in this task. Existing Persistence.Sqlite references and all unrelated production wiring remain intact.
- Added bounded UTF-8 JSON transport parsing with a 65,536-byte plus-sentinel read, exact object shapes, recursive duplicate rejection, unknown-field rejection, canonical enum/timestamp/UUIDv7/source/revision/reference validation, and completion-specific empty-observation enforcement.
- Added one sanitized invocation boundary and the fixed HTTP status mapping over the shared `DoctorResult`.
- Preserved the global loopback bind and Host-header middleware. Invalid Host responses on Doctor paths now retain the shared Doctor response, `application/json`, and `Cache-Control: no-store`; non-Doctor Host responses are unchanged.

## TDD Evidence

### RED

1. Missing start route:
   - Command: `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~DoctorRoutesTests.PostVerificationWithoutInjectedApplicationReturnsSanitizedUnavailable`
   - Result: failed as expected; expected `503 ServiceUnavailable`, actual unmapped `404 NotFound`.
2. Five-route injection:
   - Command: `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~DoctorRoutesTests`
   - Result: 1 failed / 1 passed; status route was unmapped (`404`) and evaluation did not use the injected application.
3. Transport/security/status/leak matrix:
   - Same filtered command after adding the real-host matrix.
   - Result: 12 failed / 32 passed. Expected failures covered unvalidated UUIDv7, source/timestamp, revision, references, unknown/duplicate fields, Doctor Host response headers, and sanitized exception/input behavior.
4. Canonical enum spelling:
   - Command: `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~DoctorRoutesTests.InvalidTransportIsRejectedBeforeTheApplication"`
   - Result: 1 failed / 14 passed; non-canonical `Installed` was incorrectly accepted as HTTP 200.

### GREEN

- Missing start route: 1/1 passed after the minimal start/default-unavailable seam.
- Five-route injection: 2/2 passed after mapping all routes through the injected application.
- Full Doctor route suite: 45/45 passed after strict transport, security, status, Host, and leak boundaries were implemented.

## Route / Status / Security / Leak Matrix

| Boundary | Evidence |
| --- | --- |
| Evaluation | Injected application, strict `DoctorFactSnapshot`, no CSRF/same-origin requirement, valid non-ready diagnosis remains HTTP 200. |
| Verification start | Injected application, strict JSON/source/adapter/timestamp, same-origin plus exact CSRF, success 201. |
| Verification status | Canonical lowercase UUIDv7, no CSRF/same-origin requirement, success 200. |
| Verification complete | Canonical UUIDv7, positive revision, exact completion shape, empty caller observations, 1..16 distinct safe references, same-origin plus exact CSRF, success 200. |
| Verification cancel | Canonical UUIDv7, exact positive-revision body, same-origin plus exact CSRF, success 200. |
| 400 | Media, malformed/oversize/invalid UTF-8 JSON, duplicate/unknown fields, non-canonical enum/timestamp/source/UUIDv7, invalid arguments/input/schema. |
| 404 | `verification_not_found`. |
| 409 | Stale, already terminal, source mismatch, and missing/conflicting evidence. |
| 410 | Verification/evidence expiry. |
| 422 | `partial_fact_snapshot`. |
| 503 | Doctor store busy/unavailable, including the Task 6 production verification default. |
| 500 | Sanitized `internal_error` for unexpected application/serialization failures. |
| 403 | Missing/wrong CSRF or cross-site mutation; reads remain mutation-policy free. |
| Response headers | Every exercised Doctor status, including security and invalid-Host failures, is `application/json` with `Cache-Control: no-store`. |
| Negative leak matrix | Rejected body and thrown exception markers for prompt, response, tool argument, PII/email, bearer credential, absolute database path, SQLite, and parser/type names are absent from responses. |
| Host/readiness retention | Non-loopback Host is still rejected before the application. `/health/ready`, readiness thresholds/body/status, ingestion, session, trace, and other monitor routes were not changed. |

## Exact Validation Results

1. `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Doctor`
   - Passed: 45; failed: 0; skipped: 0.
2. `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj`
   - Initial required run: passed 1,398; failed 40 because the repository-scoped Playwright Chromium executable was absent.
   - Required prerequisite run: `pwsh scripts\test\install-playwright-chromium.ps1` completed successfully.
   - Fresh rerun of the exact test command: passed 1,438; failed 0; skipped 0.
3. `dotnet build CopilotAgentObservability.slnx`
   - Succeeded with 0 warnings and 0 errors.
4. `git diff --check`
   - Passed with no output.
5. `git status --short --branch`
   - Confirmed branch `codex/issue-102-http` and only Task 6 source/test/report work plus the untracked dispatch brief before staging.

## Self-Review

- Route/status: all five routes use one injected application contract and one fixed result-code mapping; evaluation severity does not alter HTTP 200.
- Security: only start/complete/cancel apply existing same-origin classification plus exact `x-monitor-csrf: local-monitor`; status/evaluation do not. Existing loopback/Host protection is retained.
- Transport: request bodies are JSON-only, read once through the 64 KiB plus-sentinel bound, decoded with strict UTF-8, and checked before application invocation.
- Leak boundary: request/exception values are never placed in result bodies or logs; unexpected application failures are collapsed to canonical `internal_error`.
- Readiness/regression: no readiness, ingestion, session, trace, UI, CLI, domain, persistence, spec, solution, dependency, retry, fallback, heuristic, or compatibility behavior was changed.
- Scope: changed only `DoctorRoutes.cs`, the minimal `MonitorHost.cs` injection/Host seam, Doctor route tests, and this report.

## Unresolved Items / Handoffs

- Task 7 must replace the default verification-unavailable application with production Doctor application/store composition over SQLite. This task intentionally provides the injection seam and does not claim production verification persistence.
- The Task 2 Doctor cross-surface test, source adapters, live trace evidence, proxy/UI, and production integration validation remain outside Task 6.
- No push, pull request, main integration, or remote mutation was performed.
