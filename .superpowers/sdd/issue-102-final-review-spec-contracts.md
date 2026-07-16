# Issue #102 Final Specification and Contract Review

## Verdict

**PASS — 0 Critical, 0 Important, 0 Minor.**

The uncommitted final-fix diff over HEAD
`b41a9a36024244cae08d4aebe7a0ab3762bd4c8d` resolves the prior advisory-only
unknown finding without changing the fixed catalog, DTOs, serialization, or
surface mappings. No production, test, or product-document file was edited by
this review.

## Identity and scope

- Branch: `codex/issue-102-doctor-core`
- Reviewed HEAD: `b41a9a36024244cae08d4aebe7a0ab3762bd4c8d`
- Base and observed merge-base:
  `8940b34f4e031b894705682dc50c079a9ed5c180`
- Initial worktree status: clean
- Re-review status: the expected final-fix production/spec/test files are
  modified and the final-review records are untracked; the committed HEAD is
  unchanged.
- Preferred runtime requested: GPT-5.6 Luna xhigh. The available agent surface
  exposes no model or reasoning selector, so that preference could not be
  selected or verified.
- Review scope: the complete `base..HEAD` changed-file manifest and the full
  public/shared Doctor contract surface, including canonical requirements,
  specification, First-Trace Doctor and Config CLI interfaces, architecture,
  D059/D060 and D051 isolation, implementation plan/design, shared domain,
  SQLite application/store boundary, real CLI/HTTP composition, owning tests,
  and derived workflow/user/roadmap/ledger documentation.

## Re-review findings

### Critical

None.

### Important

None.

### Minor

None.

## Resolved prior finding

### I1. Advisory-only content/raw unknowns incorrectly prevented `first_trace_ready`

Resolved. `DoctorEvaluator.UnknownPreventsConclusion` and
`IsFirstTraceReady` now make only `completeness=unknown` terminal-relevant;
unknown `content_capture` and `raw_access` remain represented by the ordered
`completeness_and_content` missing family and do not emit a negative advisory.
The operative checks are at
`src/CopilotAgentObservability.Doctor/DoctorEvaluator.cs:147-149` and
`:228-232`.

Fresh executable evidence covers the two advisory members independently:

- direct evaluator:
  `DoctorEvaluatorTests.Evaluate_AdvisoryOnlyUnknownContentFact_PreservesFirstTraceReady`;
- real Config CLI:
  `DoctorCliTests.Evaluate_AdvisoryOnlyUnknownContentFact_RealCommandReturnsFirstTraceReady`;
- real Local Monitor HTTP:
  `DoctorRoutesTests.Evaluate_AdvisoryOnlyUnknownContentFact_ReturnsFirstTraceReady`;
- SQLite application/store completion:
  `DoctorApplicationServiceTests.Complete_AdvisoryOnlyUnknownContentContext_CompletesAtomically`.

Each path reaches `first_trace_ready`, retains
`missing_fact_families=["completeness_and_content"]`, and the shared evaluator's
state list contains no unknown advisory. The direct test pins the exact state
list; real CLI/HTTP and SQLite use that same evaluator/result without adapter
reinterpretation. The existing explicit-family counterexample keeps
`completeness=unknown` partial. Existing tests also retain known advisory order
and D059's `first_trace_ready` plus unrelated `schema_drift_detected` behavior.

HTTP snapshot parsing now matches the canonical/CLI optional-property contract:
`expected_source_adapter` and `verification_id` may be omitted or null. For
completion, a conflicting non-null verification ID is invalid input and a
conflicting non-null adapter is rejected as `expected_source_mismatch` before
evaluation. When either property is omitted/null, the immediate completion
transaction validates every selected candidate against the persisted
verification, then enriches the single evaluator snapshot from the trusted
candidate adapter and route verification ID. The real HTTP omission matrix,
real CLI production completion, concrete service conflict test, and existing
exactly-once evaluator test cover this path.
The relevant production checks/enrichment are at
`src/CopilotAgentObservability.LocalMonitor/DoctorRoutes.cs:266-271`,
`src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorVerificationStore.cs:275-282`,
and
`src/CopilotAgentObservability.Persistence.Sqlite/Doctor/SqliteDoctorApplicationService.cs:65-86`.

## Contract audit summary

No Critical, Important, or Minor discrepancy remains in the requested contract
areas:

- Twelve fact families are present in canonical order with explicit nullable
  family/`unknown` semantics.
- The catalog contains exactly 20 states with the specified metadata, numeric
  blocking precedence, terminal selection, advisory order, primary-state rule,
  and one-element reason code equal to state code.
- The fixed partial projection shape, advisory-only unknown behavior,
  unrelated-schema-drift advisory behavior, deterministic `doctor.v1`
  serialization, and shared production DTO identity are implemented and
  exercised.
- Limits and lexical rules cover the 65,536-byte sentinel read, 16 direct
  observations/accepted references, 100 candidates, source tokens, canonical
  lowercase UUIDv7, canonical seven-digit UTC RFC 3339 input, strict properties,
  duplicate rejection, and fixed enum spellings.
- Exactly five Config CLI commands and five Local Monitor routes are exposed,
  with the fixed exit/HTTP mappings, no-store responses, and mutation
  same-origin/CSRF protection.
- Start/status/complete/cancel projections use the shared result; complete
  resolves persisted candidates to trusted observations and invokes the
  evaluator once inside the transactional decision path. Non-ready and partial
  attempts retain the unchanged active verification.
- The #103/#104 handoff compiles through shared source-neutral fact,
  observation, candidate, class, and kind types. No source-specific Doctor enum,
  latest/repository/workspace/cwd/timestamp heuristic, or public candidate
  route/command was found.
- No #105 proxy DTO, Razor, JavaScript, Canvas, or Doctor UI was added. No #68,
  #100, #103, #104, #105, or live-trace implementation was found in the branch.
- The `GET /health/ready` handler body/status/threshold projection is unchanged;
  Doctor store failure falls back only for Doctor verification composition and
  does not gate monitor startup, ingestion, or stateless evaluation.
- Derived user/workflow/roadmap/ledger documents correctly distinguish feature-
  branch evidence from unperformed `main` integration, push/PR, Issue closure,
  source-specific live validation, and first-real-trace completion.

## Commands and observed results

```text
git branch --show-current
git rev-parse HEAD
git status --short
git merge-base 8940b34f4e031b894705682dc50c079a9ed5c180 HEAD
```

Observed the expected branch, HEAD, merge-base, and an initially clean status.

```text
git diff --stat 8940b34f4e031b894705682dc50c079a9ed5c180..HEAD
git diff --check 8940b34f4e031b894705682dc50c079a9ed5c180..HEAD
git diff --unified=30 8940b34f4e031b894705682dc50c079a9ed5c180..HEAD -- src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs
```

Observed the 78-file branch manifest, no whitespace error, and no change to the
D051 readiness handler itself.

```text
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter "FullyQualifiedName~DoctorCatalogTests|FullyQualifiedName~DoctorEvaluatorTests|FullyQualifiedName~DoctorCrossSurfaceContractTests"
```

Succeeded: 37 passed, 0 failed, 0 skipped. At the initial review this included
the then-contradictory I1 test, so that historical green result did not establish
canonical compliance.

```text
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~DoctorCliTests
```

Succeeded: 152 passed, 0 failed, 0 skipped.

```text
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~DoctorRoutesTests
```

Succeeded: 49 passed, 0 failed, 0 skipped.

At the initial review, root-provided pinned validation evidence was
5,294/5,294 for the pre-fix full solution. The initial focused contract tests
above do not substitute for that evidence.

### Final-fix re-review

```text
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj
```

Fresh result: 232 passed, 0 failed, 0 skipped.

```text
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor
```

Fresh result: 159 passed, 0 failed, 0 skipped.

```text
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Doctor
```

Fresh result: 66 passed, 0 failed, 0 skipped.

```text
git diff --check
git diff --name-only -- src/CopilotAgentObservability.Doctor/DoctorCatalog.cs src/CopilotAgentObservability.Doctor/DoctorContracts.cs src/CopilotAgentObservability.Doctor/DoctorJson.cs src/CopilotAgentObservability.Doctor/DoctorVerificationContracts.cs
```

Fresh result: no whitespace error and no diff in the fixed catalog, shared DTO,
JSON serializer, or verification-contract ownership files. The CLI/HTTP status
and exit mapping blocks are also unchanged in the inspected diff.

The final-fix report records a subsequent pinned build, Playwright bootstrap,
and full-solution rerun at 5,334/5,334. This reviewer independently reran the
three owning focused suites above, not that full pinned sequence.

## Residuals

- Source-specific #103/#104 producers and live GitHub Copilot/Claude Code
  evidence remain intentionally unimplemented and unverified.
- #105 proxy/UI remains intentionally absent and unverified.
- `main` integration, push, PR creation, external Issue closure, and a real
  first-trace workflow were not performed or claimed.
- The final-fix diff remains uncommitted pending root integration and durable
  ledger/progress updates.
