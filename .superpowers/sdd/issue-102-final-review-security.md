# Issue #102 Final Security / Data-Safety Review

## Re-review verdict

**PASS — 0 Critical, 0 Important, 0 Minor.**

This is the independent re-review of the uncommitted final-fix diff on branch
`codex/issue-102-doctor-core` at committed HEAD
`b41a9a36024244cae08d4aebe7a0ab3762bd4c8d`. The branch and HEAD matched the
requested identity. The worktree contained the expected implementer fix set and
parallel review records; this file is the only file authored by this review.

The prior two Important findings and one Minor finding are resolved. No new
security or data-safety regression was found in the additional optional-context
enrichment or store-interface removal.

The requested GPT-5.6 Luna xhigh runtime was not selectable in this surface.

## Critical findings

None.

## Important findings

None.

## Minor findings

None.

## Prior finding dispositions

### I1 — authoritative evidence-reference validation: resolved

`DoctorValidation.IsValidEvidenceReference` is now the single evidence-reference
safety authority. Direct evaluation reaches it through
`DoctorValidation.IsValidFactSnapshot`; Config CLI uses it for observations and
completion selections; Local Monitor HTTP uses it for observations and
completion selections; persistence uses it for candidate writes, accepted
selection, accepted-reference reads, and candidate reads.

The weaker CLI, HTTP, and store-specific validators were removed. A production
scan found no competing `IsEvidenceReference` / `IsSafeEvidenceReference`
implementation in the Doctor surfaces.

The shared guard preserves the fixed 1..128-character, exact-trim, control,
email, URI, path, raw-content marker, and credential marker boundary. It now
also rejects the previously demonstrated gaps: `user.id`, SSN form, `ghp_`,
`github_pat_`, and credential-bearing standalone Base64. It detects decoded
Base64 credentials without rejecting every Base64-looking opaque identifier.

Independent runtime probing of the built shared domain produced:

- rejected: `user.id=person-001`, `123-45-6789`, `ghp_abcdefghijklmnop`,
  `github_pat_abcdefghijklmnop`, `QWxhZGRpbjpvcGVuIHNlc2FtZQ==`, email, URI,
  local path, and prompt marker examples;
- accepted: `opaque receipt 001`, `YWJjZGVmZ2hpamtsbW5vcA==`,
  `receipt-ingest`, and a 128-character opaque reference.

This pins both the required unsafe cases and the intended false-positive
boundary.

Unsafe candidate writes return fixed `invalid_input` before persistence.
Persisted accepted-reference reads validate each value, and persisted candidate
reads validate the complete candidate. Corrupted unsafe rows therefore throw
inside the store boundary and are mapped to fixed `doctor_store_unavailable`
without projecting or echoing the stored value. Tests also prove the database
row projection remains unchanged on rejected writes.

Evidence:

- `src/CopilotAgentObservability.Doctor/DoctorValidation.cs:55-80,127-190,199-227`
- `src/CopilotAgentObservability.ConfigCli/Cli/DoctorCli.cs:125-135,197-215`
- `src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs:173-189,278-291`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/DoctorStoreValidation.cs:28-46`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorVerificationStore.cs:170-230,233-356,540-592`
- `tests/CopilotAgentObservability.Doctor.Tests/DoctorValidationTests.cs:176-282`
- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorVerificationStoreTests.cs:8-177`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/DoctorRoutesTests.cs:444-500`
- `tests/CopilotAgentObservability.ConfigCli.Tests/DoctorCliTests.cs:369-405`

### I2 — Doctor path-family no-store policy: resolved

The Doctor middleware now sets `Cache-Control: no-store` whenever
`DoctorRoutes.IsDoctorPath` matches, before endpoint dispatch. This covers exact
handlers, framework-generated 405 responses, malformed/unmatched Doctor paths,
and the generic fallback/404 response. Exact handlers continue to set the same
header through `PrepareResponse`.

Invalid Host is intercepted by the earlier host middleware but explicitly uses
`DoctorRoutes.WriteInvalidHostAsync`, which calls `PrepareResponse`. Body-limit
setup failure, content-type/JSON/UTF-8/oversize validation, application errors,
store errors, and caught unexpected application exceptions all use a prepared
Doctor response. The route tests retain those existing checks and add all six
unsupported common methods for each of the five defined routes (30 cases) plus
three malformed/unmatched paths.

Evidence:

- `src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs:17-56,58-246,500-531,585-669`
- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs:154-185,1006-1009,1399-1409`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/DoctorRoutesTests.cs:323-377,444-543,670-740,745-811,867-872`

### M1 — security-boundary discoverability: resolved

`docs/specifications/security-data-boundaries.md` now contains a concise
First-Trace Doctor boundary entry. It links to
`interfaces/first-trace-doctor.md`, explicitly leaves behavior authority there,
and does not duplicate or reinterpret the contract.

Evidence: `docs/specifications/security-data-boundaries.md:850-858`.

## Additional security review

### Optional completion context enrichment

Allowing `expected_source_adapter` and `verification_id` to be omitted or null
does not create a caller-trust bypass:

- a supplied non-null verification ID must equal the route/command ID;
- the snapshot's required source surface must match the persisted verification;
- a supplied non-null adapter must match the persisted adapter;
- every selected candidate is resolved inside the immediate transaction and
  must match the persisted verification source and adapter;
- only after those checks does the single evaluator callback receive the route
  verification ID and the adapter from the validated persisted candidates.

There is no pre-read, latest-candidate inference, repository/timestamp heuristic,
or caller override of candidate class, kind, source, adapter, timestamp, or
expiry. Candidate selection remains explicit and bounded.

Evidence:

- `src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs:257-293,296-327`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorApplicationService.cs:50-105`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorVerificationStore.cs:233-356`
- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorApplicationServiceTests.cs:5-126,219-260`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/DoctorRoutesTests.cs:544-670`

### Completion interface removal

The production SQLite store no longer implements `IDoctorVerificationStore`.
The explicit interface adapter that supplied a fixed ready decision and ignored
its completion timestamp was removed together with its separate candidate-read
path and exception helpers. The concrete production store exposes one public
`Complete` method, and that method requires the atomic decision callback.

The source-neutral public domain contracts remain available for later producer
issues, but no production type implements the bypass interface. Production
scans found no `: IDoctorVerificationStore` or fixed-ready completion lambda.
`ObserveCandidate` remains an internal composition operation and has no public
CLI command or HTTP route.

Evidence:

- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorVerificationStore.cs:5,233-356`
- `src/CopilotAgentObservability.Persistence.Sqlite/Doctor/DoctorStoreContracts.cs:5-18`
- `tests/CopilotAgentObservability.Doctor.Tests/Persistence/DoctorApplicationServiceTests.cs:5-20`

## Validation commands and results

- Identity/status: `git branch --show-current`, `git rev-parse HEAD`,
  `git status --short` — expected branch and HEAD; expected uncommitted fix set.
- Actual uncommitted diff: `git diff --stat`, `git diff --name-status`, focused
  `git diff`, line-numbered source/test reads — reviewed all 17 changed files.
- Doctor evaluator/validation/application/store focused tests — **189 passed,
  0 failed, 0 skipped**.
- Local Monitor `DoctorRoutesTests` — **66 passed, 0 failed, 0 skipped**.
- Config CLI `DoctorCliTests` — **159 passed, 0 failed, 0 skipped**.
- Runtime shared-validator unsafe/safe probe — expected rejection/acceptance for
  every value listed above.
- Validator-authority scan — all evidence-reference checks route to
  `DoctorValidation.IsValidEvidenceReference`; no competing guard found.
- Interface/public-surface scan — no fixed-ready callback, production interface
  implementation, public candidate route, proxy, or UI addition found.
- Tracked-diff leak scan — matches were limited to intentional synthetic
  negative-test markers; no real user path, credential, prompt/response/tool
  body, or runtime artifact found.
- `git diff --check` — passed.

The implementer fix report records a final pinned full-suite pass of
**5,334 / 5,334**, build with 0 warnings / 0 errors, and successful Playwright
Chromium bootstrap. This independent re-review reran the focused security and
cross-surface suites above rather than repeating the full suite.

## Residuals / unverified scope

- Live GitHub Copilot and Claude Code candidate producers remain Issues #103 and
  #104; proxy/UI remains Issue #105.
- No real telemetry, credentials, prompts, responses, PII, or user database were
  used.
- No production, test, spec, or other review file was edited by this reviewer.
- No commit, push, PR, main integration, or Issue transition was performed.
