# Issue #102 Task 2 Brief — Thin Executable Cross-Surface Slice

## Identity

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-doctor-core`
- Starting HEAD: `287f0c302ccbd24b9cef91acb98747d8635319f7`
- Preferred runtime: GPT-5.6 Luna with xhigh reasoning. The dispatch API cannot enforce or verify the model selection.
- Report path: `.superpowers/sdd/issue-102-task-02-implementer-report.md`

## Purpose

Create the first executable source-independent Doctor vertical slice. One sanitized `monitor_not_running` fact fixture must produce the same shared `DoctorResult` through the direct evaluator, a real Config CLI `doctor evaluate` invocation, and the real Local Monitor `POST /api/doctor/evaluations` route.

## Scope

- Add `src/CopilotAgentObservability.Doctor/` with only the contracts, JSON serializer/options, minimal `monitor_not_running` evaluator behavior, human projector, and verification interfaces needed by later lanes.
- Add `tests/CopilotAgentObservability.Doctor.Tests/` with the production DTO fixture and `DoctorCrossSurfaceContractTests`.
- Add minimal `doctor evaluate --input <file> [--json]` dispatch/composition in Config CLI.
- Add minimal `POST /api/doctor/evaluations` registration/composition in Local Monitor.
- Add solution/project references and narrowly required `InternalsVisibleTo` declarations once.
- Update only project/solution files necessary to compile and run this slice.

Read before editing: AGENTS.md, repository workflow, canonical requirements/spec, `docs/specifications/interfaces/first-trace-doctor.md`, `docs/specifications/interfaces/config-cli.md`, security/readiness specs, architecture/D060, target project/test files, and this brief. Use `superpowers:test-driven-development` exactly.

## Non-Scope

- No verification persistence or lifecycle command/route beyond compile-time interfaces.
- No remaining 19-state implementation, general validation matrix, migration, candidate observation, source-specific adapter, live trace, proxy DTO, Razor, JavaScript, Canvas, or UI.
- No refactor/reorganization of existing Config CLI dispatch or `MonitorHost` beyond the smallest additive seam.
- No external dependency, fallback parser, compatibility shim, heuristic selection, sleep/poll/retry, push, PR, issue close, or `main` integration.
- Do not edit canonical specifications unless you discover a direct contradiction; stop and report such a conflict instead.

## Constraints

- Tests come first. Do not write production Doctor/CLI/HTTP code before observing a failing cross-surface test.
- The initial RED must fail for the expected missing command/route/shared implementation, not a typo, missing fixture, invalid project setup, or unrelated baseline failure. Record the exact command and meaningful failure text.
- Use one small synthetic JSON fixture with every required fact family explicitly known enough to select only `monitor_not_running`; no real paths/raw content/PII/credentials.
- The direct call, CLI JSON, and HTTP response must deserialize to equal production `DoctorResult` values. Do not compare hand-maintained facsimile DTOs.
- Canonical JSON must be byte deterministic for repeated serialization of the same result.
- Human CLI output must be produced by a projector that consumes the same already-evaluated `DoctorResult`; prove it does not re-evaluate facts.
- Valid non-ready diagnosis: shared result `success=true`, `code=evaluation_completed`, primary/state `monitor_not_running`; CLI exit 3; HTTP 200.
- HTTP response must be `application/json`, `Cache-Control: no-store`, sanitized, and use the canonical result. Evaluation is stateless and must not depend on a Doctor store.
- Keep new public types source-neutral and implementation-minimal while matching nullability and lexical shapes in the canonical spec.
- Agent-facing files are English. Use `apply_patch` for edits.
- Do not commit. Leave a reviewable diff after focused tests/build and write the report.

## TDD Sequence

1. Add the new test project/fixture and a single cross-surface contract test (split helpers only as needed).
2. Run the focused command and observe RED caused by missing production Doctor command/route/domain surface.
3. Add the minimum new Doctor project/contracts/evaluator/serializer/projector and adapter composition to make the test compile and pass.
4. Run the same focused command and observe GREEN.
5. Refactor only duplication exposed by the green test, then rerun it.
6. Run the solution build and inspect for zero warnings/errors.

## Completion Conditions

- `DoctorCrossSurfaceContractTests` exercises direct production evaluation, real Config CLI dispatch/invocation, and a real Local Monitor HTTP route.
- All three machine projections deserialize as the same `DoctorResult` and contain the canonical monitor-not-running state metadata.
- Repeated serialization is byte-identical.
- Human output derives from the same result and is bounded/sanitized.
- CLI exit 3 and HTTP 200/no-store are asserted.
- No out-of-scope lifecycle/store/source/UI behavior is implemented.
- Focused test and build pass with pristine output.
- Self-review checks API surface, test quality, leak boundaries, unnecessary scope, and existing readiness/route non-regression.

## Verification Commands

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter FullyQualifiedName~DoctorCrossSurfaceContractTests
dotnet build CopilotAgentObservability.slnx
git diff --check
git status --short --branch
```

## Report

Write `.superpowers/sdd/issue-102-task-02-implementer-report.md` with identity, files changed, RED command/failure evidence, GREEN/final command results, DTO/producer/consumer proof, self-review verdict, unresolved findings, and unverified Task 3–8/#103/#104/#105 interfaces. Then report to `/root`. Do not commit.
