# Issue #102 Task 5 Report — Config CLI Lifecycle

## Identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability\.worktrees\issue-102-cli`
- Branch: `codex/issue-102-cli`
- Starting HEAD: `80fd19ebf497a092a4ae112e3321bcf70bcdd7a4`
- The worktree, branch, HEAD, and starting status matched the Task 5 brief before
  editing. Starting status contained only the untracked Task 5 brief.
- The preferred GPT-5.6 Luna/xhigh runtime was not selectable or verifiable on
  the available dispatch surface.
- No push, PR, main integration, canonical-spec edit, Doctor-domain edit,
  persistence edit, or production SQLite composition was performed.

## Scope implemented

- Added strict parsing and dispatch for the five canonical commands:
  `evaluate`, verification `start`, `status`, `complete`, and `cancel`.
- Added one internal `IDoctorCliApplication` seam. CLI parsing, bounded input,
  and projection stay in the adapter; evaluation/lifecycle behavior is invoked
  once through the injected application. The production no-store composition
  retains direct stateless evaluation and returns the fixed
  `doctor_store_unavailable` result for verification until Task 7 wires the
  approved SQLite-backed application.
- Added strict 65,536-byte plus-sentinel reads, throwing UTF-8, duplicate and
  unknown JSON-property rejection, exact fact/family/observation property
  shape, canonical timestamp/UUIDv7/revision/source parsing, bounded safe
  evidence-reference selection, and rejection of caller-supplied observations
  during completion.
- Added shared canonical JSON or bounded human projection from the one returned
  `DoctorResult`, fixed stderr behavior, the complete exit mapping, sanitized
  exception handling, and the five help entries.
- Added Doctor-specific tests through the real `CliApplication` dispatch. No
  facsimile parser, fake production store, dependency, fallback syntax,
  retry/poll/sleep, or source-specific behavior was added.

## TDD evidence

### RED 1 — lifecycle injection surface

The Doctor CLI tests were written first against the desired production
dispatch seam.

Command:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor
```

Result: exit 1. Production projects compiled, then the test project failed with
`CS0246` for the missing `IDoctorCliApplication` and
`DoctorCompletionInput`. This was the expected missing adapter/injection API,
not a fixture or test syntax failure.

After the minimum five-command adapter was added, the same command passed 46/46.

### RED 2 — absent JSON members and filesystem argument failures

Tests were added before the follow-up production change for a missing fact
family, a missing family member, and a filesystem path containing a NUL.

The same focused command exited 1 with exactly three expected failures:

- missing family: expected exit 2, actual 0;
- missing family member: expected exit 2, actual 0;
- invalid filesystem path: expected exit 2, actual 5.

Exact JSON-shape checks and input-exception classification made the same command
pass 49/49.

### RED 3 — direct evaluation verification context

Self-review added a test before production code proving that direct
`doctor evaluate` must not accept caller-supplied persisted verification
context. The focused command exited 1 with one expected failure: expected exit
2, actual 0. Rejecting non-null `verification_id` on direct evaluation made the
final focused command pass 50/50.

## Five-command, exit, and leak matrix

| Surface | Inputs/dispatch proved | Result/exit proved | Leak boundary proved |
| --- | --- | --- | --- |
| `doctor evaluate` | strict bounded fact file; injected typed snapshot; no verification context | first-trace ready 0; non-ready 3; partial 3; validation/schema 2 | input path/body, invalid UTF-8/JSON, and exception detail absent |
| `verification start` | database, source/optional adapter, canonical expiry in arbitrary documented option order | `verification_started` 0; invalid lexical options 2; store/internal 5 | database path and unsafe option values absent |
| `verification status` | database and canonical lowercase UUIDv7 | active 0; missing/expired conflict 4; store/internal 5 | database path, SQLite/type/exception/credential markers absent |
| `verification complete` | database, UUIDv7, positive revision, strict bounded completion file; empty caller observations; 1..16 distinct safe refs | completed 0; non-ready/partial 3; stale/terminal/source/evidence conflict 4 | database/input path, rejected JSON, trusted candidate metadata, and exception markers absent |
| `verification cancel` | database, UUIDv7, positive revision | cancelled 0; stale/expired/terminal 4; store/internal 5 | database path and exception markers absent |

The fixed result-code theory covers every invalid/schema, verification
not-found/stale/expired/already-terminal/source/evidence conflict, Doctor store,
and internal result code. Lifecycle command tests cover all four successful
verification codes. JSON and human runs return projections of the same injected
result and each invoke the application exactly once. Successful results leave
stderr empty; non-success stderr is only the fixed snake-case code and newline.

## Exact validation results

All commands ran from the isolated worktree root.

1. `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor`
   - Final PASS: 50 passed, 0 failed, 0 skipped.
2. `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj`
   - The first pre-build run executed 3,533 tests and had one existing
     compile-asset precondition failure instructing that the solution be built
     first; 3,532 tests passed.
   - After the required solution build, PASS: 3,534 passed, 0 failed, 0 skipped.
3. `dotnet build CopilotAgentObservability.slnx`
   - Final PASS: 0 warnings, 0 errors.
4. `git diff --check`
   - PASS: exit 0, no output.
5. `git status --short --branch`
   - PASS: branch remained `codex/issue-102-cli`; only the intended Task 5
     production/test/report files and the preserved untracked brief were
     present.

`NETSDK1057` preview-support-policy lines were informational SDK messages; the
build summary remained 0 warnings and 0 errors.

## Self-review

Verdict: PASS; no blocking Task 5 finding remained.

- Spec/behavior: compared the diff against `docs/requirements.md`,
  `docs/spec.md`, both canonical Doctor/Config CLI interface specs,
  `docs/architecture.md`, and D060. All five commands, option vocabularies,
  bounded file rules, projection rule, stderr rule, and exit categories are
  represented by production dispatch and tests.
- Strictness: missing/duplicate/unknown properties, invalid UTF-8/JSON,
  non-canonical timestamp/UUID, non-positive/overflow/sign-prefixed revision,
  invalid/overlong source tokens, unsafe completion observations, duplicate
  evidence refs, direct verification context, and 65,537-byte input fail
  closed. Exactly 65,536 bytes succeeds.
- Safety: inspected command, application seam, and tests for database/input
  path, body, exception/type, SQLite, credential, authorization, PII, and raw
  marker projection. Failures are synthesized from fixed `DoctorResultCode`
  values only; raw exceptions are not printed or logged.
- Shared projection: the CLI never selects or reorders Doctor states and never
  re-evaluates an application result. JSON uses `DoctorJson.SerializeResult`;
  human output uses `DoctorHumanProjector.Project` on that same result.
- Scope/dependencies: only Doctor CLI files, the minimum `CliApplication` and
  help seam, Doctor-specific tests, and this report changed. No project file,
  package, lockfile, Doctor domain/evaluator, Persistence.Sqlite, Local Monitor,
  specification, ledger, or solution file changed.
- Integration seam: `IDoctorCliApplication` retains the database path input so
  Task 7 can compose the approved store without introducing a second parser or
  DTO. The adapter contains no SQLite type, store construction, schema logic,
  retry, polling, or clock.

## Unresolved items and unverified handoffs

No unresolved item remains within the Task 5 adapter scope.

- Task 7 must replace the no-store verification result with the approved
  SQLite-backed application composition and prove the injected clock's 1..30
  minute window, lifecycle CAS/evidence semantics, and busy/unavailable
  classification. This task intentionally did not create or open a database.
- Persistence schema/migration/restart/concurrency, Local Monitor HTTP routes,
  source-specific candidate producers, live trace evidence, readiness
  regression, proxy/UI, and cross-surface Task 2 coverage remain owned by their
  assigned Issue #102 tasks.
- No live source interaction, production database, push, PR, or main integration
  was performed or represented as validation evidence.
