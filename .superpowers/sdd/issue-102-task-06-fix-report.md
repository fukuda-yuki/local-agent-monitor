# Issue #102 Task 6 Fix Report — Canonical Doctor Body Limit

## Identity

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-http`
- Starting HEAD: `07e9cceb0674f0af968116b92fcae909da500070`
- Review finding: the supported global request-body limit could reject a valid Doctor request before the Doctor-owned 64 KiB reader and return the ingestion-shaped 413 response.
- Scope: Doctor request-limit routing, Doctor-specific real-host tests/support, and this fix report.

## Root Cause and Fix

- Kestrel applied `MonitorOptions.MaxRequestBodyBytes` before a Doctor handler read the body. When that configured value was below 65,536, the global exception handler produced a generic 413 instead of the fixed Doctor 400/shared-result/no-store contract.
- A Doctor-only middleware now runs before body reads on the four Doctor POST body routes. It obtains `IHttpMaxRequestBodySizeFeature` and raises only an insufficient finite per-request limit to 65,537 bytes: the canonical 65,536-byte maximum plus the sentinel needed by the route-owned reader.
- An already sufficient limit and an unlimited per-request value remain unchanged. Non-Doctor routes never enter the override and retain the configured global limit.
- An unavailable, read-only, or rejected feature mutation is collapsed to the fixed sanitized Doctor `internal_error` response with `application/json` and `Cache-Control: no-store` rather than escaping through a generic response.
- The middleware predicate is restricted to the four mapped Doctor body routes; it does not raise the limit for unknown Doctor-prefixed paths or the GET status route.

## TDD Evidence

### RED

- Command: `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~DoctorRoutesTests.DoctorBoundary`
- Configuration: real Kestrel host with `MaxRequestBodyBytes = 1,024`.
- Result before the fix: 1 failed / 1 passed. The valid exact 65,536-byte Doctor evaluation expected HTTP 200 but received the generic HTTP 413; the companion `/v1/traces` test already proved the global 1,024-byte limit remained active.

### GREEN

- The same focused command passed 2/2.
- Exact 65,536-byte valid Doctor JSON reached the injected application and returned canonical HTTP 200 `evaluation_completed` with non-ready `monitor_not_running` evaluation state, `application/json`, and `no-store`.
- Exact 65,537-byte Doctor JSON returned HTTP 400 and the fixed body `{"schema_version":"doctor.v1","success":false,"code":"invalid_input","evaluation":null,"verification":null}` with `application/json` and `no-store`; the body contains no ingestion `request_too_large` response.
- `/v1/traces` with 1,025 bytes retained the configured global-limit HTTP 413 and ingestion `request_too_large` contract.
- Full Doctor regression passed 47/47, covering all five route, transport, security, status, header, and leak contracts.

## Validation

1. `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~DoctorRoutesTests.DoctorBoundary`
   - Passed 2; failed 0; skipped 0.
2. `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Doctor`
   - Passed 47; failed 0; skipped 0.
3. `pwsh scripts\test\install-playwright-chromium.ps1`
   - Exit 0.
4. `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj`
   - One intermediate run had an unrelated `RestartRecovery_ReusesDatabaseAndCatchesUpProjection` readiness assertion return 503. The unchanged test passed 1/1 in isolation. A fresh required full-suite run passed 1,440; failed 0; skipped 0. After the final scope refinement, another fresh full-suite run also passed 1,440/1,440.
5. `dotnet build CopilotAgentObservability.slnx`
   - Final build succeeded with 0 warnings and 0 errors. The SDK emitted informational preview-SDK messages only.
6. `git diff --check`
   - Passed with no output before the report was written; repeated in the final artifact checks.

## Self-Review

- Functional correctness: only insufficient finite limits are raised, exactly to the plus-sentinel value, before any Doctor body read. The Doctor reader remains the owner of the exact 64 KiB decision and fixed 400 mapping.
- Regression risk: the middleware predicate covers evaluation, verification start, completion, and cancellation POST paths. The status GET and non-Doctor paths are untouched; the real-host trace regression proves the global cap remains active outside Doctor.
- Security and data safety: the override does not weaken loopback, Host-header, same-origin, or CSRF enforcement. Failure output uses only the fixed Doctor error shape and does not include body, path, exception, credential, PII, or database content.
- Maintainability: the fix uses the server's per-request feature directly, adds no dependency, host-wide refactor, compatibility path, fallback, sleep, polling, retry, or heuristic.
- Scope: no specification, Doctor domain, CLI, persistence, readiness, UI, dependency, lockfile, or unrelated host behavior was changed.
- Findings: no blocking issue remained after direct diff review. The unavailable/read-only feature branch is not injectable through the existing real-Kestrel test harness without adding a broader host seam; its deterministic canonical-error branch was inspected directly, while the supported Kestrel path is covered by the low-global-limit real-host test.

## Handoff

- Task 7 still owns production Doctor application/store composition. Source adapters, live first-trace evidence, proxy/UI, push, pull request, and integration remain out of scope.
- No remote or integration operation was performed.
