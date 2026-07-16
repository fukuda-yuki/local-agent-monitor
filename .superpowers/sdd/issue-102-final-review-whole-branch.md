# Issue #102 Final Whole-Branch Re-review

## Verdict

**PASS — 0 Critical, 0 Important, 0 Minor.**

The complete uncommitted final-fix diff over committed HEAD
`b41a9a36024244cae08d4aebe7a0ab3762bd4c8d` resolves all four prior Important
and both prior Minor findings without changing the fixed catalog, shared DTO,
serialization, public command/route count, status/exit mappings, D051 readiness,
migration boundary, dependency graph, or #105 UI/proxy scope. I found no new
correctness, safety, maintainability, test-quality, or repository-hygiene issue.

The requested GPT-5.6 Luna xhigh runtime was not selectable or verifiable in
this agent surface.

## Identity and reviewed scope

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-doctor-core`
- Committed HEAD: `b41a9a36024244cae08d4aebe7a0ab3762bd4c8d`
- Issue base: `8940b34f4e031b894705682dc50c079a9ed5c180`
- Final fix: uncommitted 17-file diff, 557 insertions / 327 deletions.
- Status contained the expected production/spec/test modifications and five
  untracked final-review/fix reports. This review edited only this report.

I read the final fix report and updated security, specification, and
concurrency/migration reviews, then independently inspected the actual diff and
surrounding implementation. Scope included the Doctor evaluator/validation,
CLI/HTTP parsing and projection, SQLite application/store transaction path,
persisted corrupt-data reads, Local Monitor middleware/fallback behavior,
security cross-reference, fixture renames, all owning tests, and unchanged
catalog/DTO/JSON/MonitorHost/solution surfaces.

## Findings

### Critical

None.

### Important

None.

### Minor

None.

## Prior finding dispositions

### I1 — advisory-only unknown facts: resolved

`DoctorEvaluator.UnknownPreventsConclusion` now treats only unknown
`completeness` as terminal-relevant; unknown `content_capture` and `raw_access`
remain reported through `completeness_and_content` in
`missing_fact_families` but do not block `first_trace_ready`
(`src/CopilotAgentObservability.Doctor/DoctorEvaluator.cs:145-149,228-232`). A
known disabled/unsupported capture, sanitized-only raw access, or schema drift
still emits the applicable advisory after the terminal state.

The prior contradictory test was replaced with independent content/raw
counterexamples. Coverage now reaches the direct evaluator, real Config CLI,
real Local Monitor HTTP, and transactional verification completion. Unknown
completeness retains partial behavior. The twenty-state catalog, precedence,
reason codes, DTOs, and JSON serialization were not changed.

### I2 — authoritative evidence-reference safety: resolved

`DoctorValidation.IsValidEvidenceReference` is now the sole authority used by
fact observations, CLI input and completion selections, HTTP input and
completion selections, SQLite candidate writes, selected references, accepted
reference reads, and candidate reads. The CLI-, HTTP-, and store-specific
validators were removed.

The shared validator retains 1..128 length, exact trim, control, email,
URI/path, raw-content, and credential checks and adds identity/credential forms
that previously escaped: `user.id`, SSN form, `ghp_`, `github_pat_`, and decoded
standalone Base64 credentials
(`src/CopilotAgentObservability.Doctor/DoctorValidation.cs:127-190`). Runtime
probing of the rebuilt assembly rejected all five representative unsafe forms
and accepted both `opaque receipt 001` and a non-credential Base64-looking
opaque reference, pinning the false-positive boundary.

Persistence validates complete candidate records on read and accepted
references before projection
(`SqliteDoctorVerificationStore.cs:540-592`). Corrupt unsafe rows therefore map
through the existing store boundary to fixed `doctor_store_unavailable` without
echo; rejected writes leave rows unchanged. Static search found no competing
production reference validator.

The test fixture renames from `trace:...`, `session:...`, `probe:...`, and
`fixture:...` to hyphenated opaque references are correct: the canonical URI
rule already prohibited scheme-shaped references, while the explicit unsafe
URI matrix remains. No migration fixture or product contract was altered.

### I3 — Doctor path-family no-store: resolved

The Doctor middleware now sets `Cache-Control: no-store` for the entire
`/api/doctor` path family before endpoint dispatch
(`src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs:24-42`). This
covers exact handlers, framework 405 responses, unsupported methods,
unmatched/malformed paths and fallback 404s. Invalid Host and exact route error
paths still call `PrepareResponse`, so body-limit, content type, malformed JSON,
application, store, and exception results retain the same policy.

The new route matrix exercises six unsupported common methods across all five
defined routes plus malformed/unmatched paths. It adds no route, fallback DTO,
or MonitorHost behavior change.

### I4 — optional HTTP/CLI context parity and trusted enrichment: resolved

HTTP now uses the same required/optional root-property split as CLI, so
`expected_source_adapter` and `verification_id` may be omitted or explicit null
(`src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs:257-327`). A
non-null verification ID still must match the route ID.

Completion does not trust an omitted/null caller context. Inside the existing
immediate transaction, the store loads the route-selected verification, checks
the required source, rejects any conflicting non-null adapter, resolves every
explicit selected reference, and requires every candidate to match the
persisted source/adapter
(`SqliteDoctorVerificationStore.cs:233-357`). Only then does the one evaluator
callback receive a snapshot enriched with the route verification ID and the
adapter from those transaction-validated candidates
(`SqliteDoctorApplicationService.cs:65-105`). There is no pre-read, latest
selection, heuristic, caller override, or new race window. Non-ready/partial
rollback, accepted ordinals, terminal CAS, and shared clock semantics remain
unchanged.

Tests cover HTTP omission/null permutations, real CLI completion with both
members omitted, conflicting ID/adapter rejection before evaluation, trusted
adapter projection, exactly-once evaluation, and atomic first-ready completion.

### M1 — security discoverability: resolved

`docs/specifications/security-data-boundaries.md:850-858` adds a concise
First-Trace Doctor cross-reference and explicitly leaves behavior authority in
`interfaces/first-trace-doctor.md`. It improves discovery without duplicating
or redefining policy.

### M2 — latent ready-fixed interface adapter: resolved

`SqliteDoctorVerificationStore` no longer implements
`IDoctorVerificationStore`; the fixed-ready completion adapter, ignored
timestamp parameters, separate pre-read candidate path, and adapter-only
exception helpers were removed. The shipped store now has one public completion
method and it requires the atomic decision callback. Reflection coverage and a
production scan confirm no `: IDoctorVerificationStore`, fixed-ready lambda, or
separate `ResolveCandidates` path remains.

The source-neutral public domain interface remains available for later Issues,
but no production type implements the unsafe adapter. `ObserveCandidate`
remains internal with no public CLI/HTTP surface.

## Holistic regression and quality assessment

- The catalog remains exactly 20 entries with unchanged metadata, numeric
  blocker precedence, terminal/advisory ordering, primary state, and v1 reason
  equality.
- `DoctorContracts`, `DoctorCatalog`, `DoctorJson`, verification contracts,
  `MonitorHost`, solution membership, project references, and external
  dependencies have no final-fix diff.
- CLI exit and HTTP status mapping blocks are unchanged. Exactly five commands
  and five routes remain.
- D051 readiness status/body/threshold composition is untouched; Doctor schema
  initialization still degrades verification only, leaving startup, ingestion,
  projection, and stateless evaluation available.
- Existing schema/migration/rollback/CAS/busy/restart tests remain intact and
  pass. The final fix only renames test-created scheme-like references and adds
  corrupt-read/context coverage.
- Centralizing validation and deleting the unused interface adapter reduces
  duplication and production complexity. The optional-context change reuses
  the existing transaction rather than adding a second path or compatibility
  shim.
- No Doctor proxy DTO, Razor, JavaScript, Canvas, public candidate route, source-
  specific enum, live adapter, or new dependency was added.
- Test additions target prior counterexamples and production composition rather
  than test-only alternate behavior. No sleep, polling, or retry coordination
  appears in Doctor source/tests.
- No tracked runtime artifact, real user path, credential, raw prompt/response,
  or tool body was found in the diff.

## Fresh commands and results

- `git branch --show-current; git rev-parse HEAD; git status --short --branch`
  — exact expected branch/HEAD and final-fix status.
- `git diff --stat; git diff --name-status;` full production/docs/test diffs and
  line-numbered surrounding reads — all 17 changed files inspected.
- `dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --no-restore`
  — PASS, 232/232, 0 failed/skipped.
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor --no-restore`
  — PASS, 159/159, 0 failed/skipped.
- `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Doctor --no-restore`
  — PASS, 66/66, 0 failed/skipped.
- `git diff --check` — PASS.
- Windows-safe expanded Doctor scan for `Thread.Sleep`, `Task.Delay`, `retry`,
  and `poll` — no matches.
- Validator-authority scan — no competing CLI/HTTP/store validator.
- Production interface/bypass scan — no interface implementation, fixed-ready
  callback, or separate candidate-resolution path.
- Immutable-surface scan — no diff in solution, catalog, DTOs, JSON,
  verification contracts, or MonitorHost.
- UI/proxy scope scan — no matching diff.
- Runtime shared-validator probe — unsafe cases rejected and intended opaque
  safe cases accepted.

The fix report records the pinned build passing with 0 warnings/errors,
Playwright bootstrap exit 0, and the exact full-solution rerun passing
5,334/5,334 with 0 failures/skips after one unrelated setup-wrapper timeout was
isolated. This review independently reran the three owning focused suites above;
it did not substitute them for or rerun the pinned full suite.

## Residuals and unverified scope

- Live GitHub Copilot and Claude Code candidate producers/first traces remain
  Issues #103/#104 and are intentionally unverified.
- Proxy/UI remains Issue #105 and is intentionally absent.
- The public source-neutral store interface has no production SQLite
  implementation; later work must preserve the evaluate-before-terminal atomic
  decision rather than reintroduce the removed adapter.
- Main integration, commit of the final fix, push, PR creation, external Issue
  closure, and live first-trace completion were not performed or claimed.
