# Issue #56 Effect Comparison Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add exact-linked objective evaluation receipts, user-confirmed pre/post cohorts, deterministic quality-first verdicts, atomic effect receipts/Verified transitions, and the Canvas Compare workspace.

**Architecture:** Extend the existing Session domain/store with additive revision, objective-evaluation, cohort, and effect records. Keep verdict calculation in a pure `EffectVerdictEngine`; Local Monitor routes validate policy and exact identity, SQLite owns atomic revalidation/persistence, and the token-gated Canvas helper owns explicit cohort confirmation and display only.

**Tech Stack:** .NET 10/C#, ASP.NET Core minimal routes, SQLite, xUnit, Node `node:test`, emitted Canvas helper DOM tests.

## Global Constraints

- Canonical contract: `docs/specifications/interfaces/canvas-effect-comparison.md` and D055.
- No new NuGet/npm/runtime dependency and no lockfile update.
- Verdicts are exactly `improved`, `no_change`, `regressed`, `insufficient_evidence`; no composite score.
- Included Sessions are exact-bound, terminal, `full`, explicitly user-confirmed, and have decisive human/objective evidence.
- Pre/post minimum is exactly three each; missing/ambiguous/conflicting evidence is never imputed.
- Quality pass-rate/severe checks precede efficiency. Exactly 10% improvement is material; worsening is material only above 10%.
- Objective receipts bind exact Session/Run/trace/evidence. Repository/timestamp proximity and unlinked normalized `success_status` are forbidden evidence.
- Only an active, post-hash-current Issue #55 receipt for the pinned proposal revision is eligible.
- `improved` receipt + proposal `verified` transition is one SQLite transaction; rollback makes the receipt historical/inactive.
- All writes are loopback/Host validated, same-origin, CSRF, exact JSON, 1 MiB, no-store. Fixed errors never echo rejected values or exceptions.
- Canvas actions, `session.send()`, logs, URLs, committed/repository-safe output, CI/static artifacts receive no cohort/evaluation/path/source/diff/raw payload.

---

### Task 1: Pin Proposal Revisions And Expose Sanitized Application Receipts

**Files:**
- Modify: `src/CopilotAgentObservability.Telemetry/Sessions/SessionDomain.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/ProposalApplyRequest.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/ProposalApply/ProposalApplyService.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SqliteSessionStoreTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionImprovementProposalRouteTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionProposalApplyRouteTests.cs`

**Interfaces:**
- Consumes: existing `ImprovementProposal`, `ProposalApplyDraftMetadata`, `ProposalApplyLinkage`, apply hash/root validation.
- Produces: proposal `Revision`, pinned revision on draft/apply linkage, `ProposalApplicationReceipt`, and sanitized receipt query.

- [ ] **Step 1: Write failing proposal-revision and migration tests**

Add assertions equivalent to:

```csharp
Assert.Equal(1, created.Revision);
store.UpdateImprovementProposalStatus(created.ProposalId, ImprovementProposalStatus.Recommended, now);
Assert.Equal(2, store.GetImprovementProposal(created.ProposalId)!.Revision);
Assert.Equal(1, migratedLegacyProposal.Revision);
```

Also prove candidate/recommended transitions increment once, rejected/no-op transitions do not, and Verified is still compare-owned.

- [ ] **Step 2: Run RED tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SqliteSessionStoreTests|FullyQualifiedName~SessionImprovementProposalRouteTests"
```

Expected: FAIL because `Revision` and migration columns do not exist.

- [ ] **Step 3: Add exact domain shapes and additive migration**

Use these signatures consistently:

```csharp
public sealed record ImprovementProposal(
    Guid ProposalId, int Revision, ImprovementProposalStatus Status,
    string TargetKind, string TargetLabel, string Title, string Summary,
    string ExpectedEffect, string RiskNote,
    IReadOnlyList<Guid> SourceSessionIds,
    IReadOnlyList<ImprovementProposalEvidenceReference> EvidenceReferences,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? RecommendedAt, DateTimeOffset? VerifiedAt);

public sealed record ProposalApplicationReceipt(
    Guid ApplyId, Guid DraftId, Guid ProposalId, int ProposalRevision,
    int SelectionRevision, DateTimeOffset AppliedAt, int FileCount,
    string State, string CurrentState);
```

Add `revision INTEGER NOT NULL DEFAULT 1` to `improvement_proposals`,
`proposal_revision INTEGER NOT NULL DEFAULT 1` to drafts/applies as required,
and include proposal revision in the approval digest input.

- [ ] **Step 4: Add receipt read and exact active-state derivation**

Add store/service methods with these contracts:

```csharp
IReadOnlyList<ProposalApplyLinkage> ListProposalApplyLinkages(Guid proposalId);
IReadOnlyList<ProposalApplicationReceipt> ListApplicationReceipts(Guid proposalId);
```

The service returns `active`, `rolled_back`, `pending`, `stale`, or
`unavailable`; it reuses root/reparse/post-hash validation and never returns
root IDs, paths, hashes, diff, source, or snapshots.

- [ ] **Step 5: Add and test the sanitized route**

Map exactly:

```text
GET /api/session-workspace/proposal-applies/receipts?proposal_id={uuidv7}
```

Return `{ "items": [...] }`, no-store, fixed invalid/not-found errors, and no
path/hash/source sentinels. Test applied, rolled-back, stale external edit,
missing configured root, legacy revision, and malformed query.

- [ ] **Step 6: Run focused tests and commit**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SqliteSessionStoreTests|FullyQualifiedName~SessionImprovementProposalRouteTests|FullyQualifiedName~SessionProposalApplyRouteTests|FullyQualifiedName~ProposalApplyServiceTests"
git diff --check
git commit -m "Issue #56: feat(monitor): pin proposal application revisions" -m "Effect comparison needs one immutable proposal/application identity; timestamps and current lifecycle state cannot substitute for the revision that was actually applied."
```

Expected: PASS; no warnings from changed code.

---

### Task 2: Store Exact Objective Evaluation Receipts

**Files:**
- Create: `src/CopilotAgentObservability.Telemetry/Sessions/EffectComparisonDomain.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Sessions/ObjectiveEvaluationRequest.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Sessions/SessionDomain.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionObjectiveEvaluationRouteTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SqliteSessionStoreTests.cs`

**Interfaces:**
- Consumes: exact Session detail, Runs, Events, proposal evidence-reference validation patterns, request policy helpers.
- Produces: immutable `ObjectiveEvaluationReceipt` and evidence rows queried by Session.

- [ ] **Step 1: Add failing exact-link and validation tests**

Cover pass/normal success plus wrong Session, wrong Run, null/mismatched trace,
unbound/partial/rich/active Session, cross-Session evidence, invalid identifier,
pass+severe, duplicate evidence, >10 evidence, secret/path marker, duplicate
receipt identity, and no-echo errors.

- [ ] **Step 2: Run RED tests**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SessionObjectiveEvaluationRouteTests
```

Expected: FAIL because routes/domain/store do not exist.

- [ ] **Step 3: Implement immutable domain and request parser**

Define:

```csharp
public enum ObjectiveResult { Pass, Fail }
public enum ObjectiveSeverity { Normal, Severe }

public sealed record ObjectiveEvaluationEvidence(string Kind, string ReferenceId);

public sealed record ObjectiveEvaluationReceipt(
    Guid ObjectiveEvaluationId, Guid SessionId, Guid RunId, string TraceId,
    ObjectiveResult Result, ObjectiveSeverity Severity,
    string EvaluatorId, string EvaluatorVersion, string CriterionId,
    string CaseKey, IReadOnlyList<ObjectiveEvaluationEvidence> Evidence,
    DateTimeOffset RecordedAt);
```

Parse an exact JSON object with no unknown fields. Apply
`^[A-Za-z0-9][A-Za-z0-9._:-]*$` and spec lengths; use existing unsafe-value
checks without accepting free-form notes.

- [ ] **Step 4: Add additive tables and exact transaction validation**

Create `objective_evaluations` and `objective_evaluation_evidence` with Session
and Run FKs and immutable insert semantics. Validate terminal/full Session,
Run ownership, exact nonblank trace equality, and evidence ownership in the
same SQLite transaction that inserts the receipt.

- [ ] **Step 5: Map POST/GET routes and prove policy**

```text
POST /api/session-workspace/objective-evaluations
GET  /api/session-workspace/objective-evaluations?session_id={uuidv7}
```

POST requires same-origin/CSRF/exact JSON/1 MiB/no-store. GET is sanitized and
no-store. Responses contain the specified fields only. Errors are fixed and
never echo identifiers, paths, raw content, or exceptions.

- [ ] **Step 6: Run focused tests and commit**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SessionObjectiveEvaluationRouteTests|FullyQualifiedName~SqliteSessionStoreTests"
git diff --check
git commit -m "Issue #56: feat(monitor): record exact objective evaluations" -m "Normalized pass labels are not Session evidence. Immutable Run/trace receipts provide the exact quality facts required for comparison without inferring identity."
```

---

### Task 3: Implement The Pure Quality-First Verdict Engine

**Files:**
- Create: `src/CopilotAgentObservability.Telemetry/Sessions/EffectVerdictEngine.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/EffectVerdictEngineTests.cs`

**Interfaces:**
- Consumes: already validated immutable cohort facts, not stores or HTTP.
- Produces: `EffectVerdictResult Evaluate(EffectComparisonFacts facts)` with exact fractions, decimal medians/deltas, and fixed reason codes.

- [ ] **Step 1: Define the pure input/output records and table-driven RED tests**

Use:

```csharp
public enum EffectVerdict { Improved, NoChange, Regressed, InsufficientEvidence }

public sealed record SessionEffectFacts(
    Guid SessionId, string Side, string CaseKey, bool QualityPass,
    bool SevereFailure, long? DurationMs, long? TotalTokens,
    IReadOnlyList<string> EvidenceIds);

public sealed record EffectComparisonFacts(
    bool LinkageValid, IReadOnlyList<SessionEffectFacts> Pre,
    IReadOnlyList<SessionEffectFacts> Post,
    IReadOnlyList<string> InsufficiencyReasons);

public sealed record EffectVerdictResult(
    EffectVerdict Verdict, int PrePass, int PreCount, int PostPass,
    int PostCount, decimal? PreDurationMedian, decimal? PostDurationMedian,
    decimal? DurationDelta, decimal? PreTokenMedian,
    decimal? PostTokenMedian, decimal? TokenDelta,
    IReadOnlyList<string> Reasons);
```

Tests must cover linkage failure; 2x3, 3x2, exact 3x3; severe post failure;
quality better/worse/equal; exact integer cross multiplication with unequal
cohort sizes; odd/even medians; zero/missing/incomplete metrics; +9.999%, +10%,
-10%, less than -10%; one metric improved with other neutral; material mixed;
quality regression despite large efficiency gain; quality improvement despite
efficiency loss; deterministic ordering and no floating-point rounding drift.

- [ ] **Step 2: Run RED tests**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~EffectVerdictEngineTests
```

- [ ] **Step 3: Implement minimal deterministic engine**

Implement median with sorted `decimal`, quality ratios using integer cross
multiplication, and deltas as `(pre - post) / pre`. Apply spec precedence
without a weighted score or tolerance beyond the exact 10% rules.

- [ ] **Step 4: Run tests, mutation-style self-review, and commit**

Flip each comparison operator mentally and confirm a test fails for it.

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~EffectVerdictEngineTests
git diff --check
git commit -m "Issue #56: feat(compare): add quality-first verdict engine" -m "A pure lexicographic engine makes evidence sufficiency, quality precedence, and the 10 percent boundary independently reviewable and immune to HTTP or persistence behavior."
```

---

### Task 4: Persist Confirmed Cohorts And Atomic Effect Receipts

**Files:**
- Modify: `src/CopilotAgentObservability.Telemetry/Sessions/EffectComparisonDomain.cs`
- Modify: `src/CopilotAgentObservability.Telemetry/Sessions/SessionDomain.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/SqliteEffectComparisonStoreTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/ProposalApplyServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3 records and `EffectVerdictEngine`.
- Produces: immutable cohort revision, effect receipt, atomic compare/verify operation, rollback-derived invalidation.

- [ ] **Step 1: Write RED persistence and race tests**

Cover one-session-one-classification, fixed exclusion reasons, boundary overlap,
case-key group projection, captured human/objective evidence identity,
proposal/application/cohort/evidence staleness, successful non-improved receipt,
improved+Verified atomicity, injected failure rollback, concurrent rollback,
and successful later rollback deriving `verification_state=invalidated`.

- [ ] **Step 2: Run RED tests**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SqliteEffectComparisonStoreTests
```

- [ ] **Step 3: Add domain/store contracts**

```csharp
public sealed record EffectCohortSession(
    Guid SessionId, string Classification, string CaseKey, string? ExclusionReason);

public sealed record EffectComparisonRequest(
    Guid ProposalId, int ProposalRevision, Guid ApplyId,
    IReadOnlyList<EffectCohortSession> Sessions);

public sealed record EffectReceipt(
    Guid ComparisonId, int CohortRevision, Guid ProposalId,
    int ProposalRevision, Guid ApplyId, EffectVerdictResult Result,
    string VerificationState, DateTimeOffset RecordedAt);

EffectReceipt RecordEffectComparison(
    EffectComparisonRequest request, DateTimeOffset recordedAt);
```

The store transaction re-reads all referenced rows, constructs facts from the
same snapshot, calls the pure engine, inserts cohort/evidence/receipt rows, and
sets proposal Verified only for Improved.

- [ ] **Step 4: Add additive schema and rollback invalidation derivation**

Create `effect_comparisons`, `effect_comparison_sessions`,
`effect_comparison_evidence`, and `effect_receipts`. Do not copy raw values or
paths. Derive inactive verification by joining the apply row state; do not
delete history or mutate receipt verdict.

- [ ] **Step 5: Run focused tests and commit**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SqliteEffectComparisonStoreTests|FullyQualifiedName~ProposalApplyServiceTests|FullyQualifiedName~EffectVerdictEngineTests"
git diff --check
git commit -m "Issue #56: feat(compare): persist atomic effect receipts" -m "The verdict and Verified maturity must share one SQLite snapshot so rollback, evidence, cohort, and proposal revision races cannot create unsupported verification."
```

---

### Task 5: Expose Candidate And Comparison HTTP Workflows

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/Sessions/EffectComparisonRequestParser.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Sessions/EffectComparisonRoutes.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionEffectComparisonRouteTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 4 store/service contracts.
- Produces: candidate, comparison create/read DTOs with fixed policy/errors.

- [ ] **Step 1: Write complete route RED matrix**

Test every method/path, same-origin, CSRF, no-store before rejection, exact
JSON, unknown/duplicate fields, 1 MiB declared/streamed body, UUID/revision,
classification/exclusion/case-key validation, duplicate Session, candidate
non-authority, no repository/timestamp-only suggestion, all fixed errors, and
no rejected-value/exception/path/source/diff/raw echo.

- [ ] **Step 2: Run RED tests**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SessionEffectComparisonRouteTests
```

- [ ] **Step 3: Implement exact routes**

```text
GET  /api/session-workspace/effect-comparisons/candidates?proposal_id={id}&apply_id={id}
POST /api/session-workspace/effect-comparisons
GET  /api/session-workspace/effect-comparisons/{comparisonId}
```

Candidates include sanitized Session metadata, exact evidence availability,
boundary eligibility, and suggestion reasons only. POST is the explicit cohort
confirmation and verdict action. GET returns summary, included/excluded rows,
case-key groups, identical evidence IDs, and derived verification state.

- [ ] **Step 4: Prove acceptance and boundary scenarios**

Create real SQLite/HTTP tests for exact 3x3 improved, no-change, regressed,
severe, insufficient 2x3, partial/unbound/nonterminal, missing/conflicting
quality, rollback, post-hash stale, stale proposal/cohort/evidence, concurrent
rollback, and summary/drill-down evidence equality.

- [ ] **Step 5: Run focused tests and commit**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SessionEffectComparisonRouteTests|FullyQualifiedName~SessionObjectiveEvaluationRouteTests|FullyQualifiedName~SessionProposalApplyRouteTests"
git diff --check
git commit -m "Issue #56: feat(monitor): add effect comparison routes" -m "Explicit cohort confirmation and fixed sanitized responses keep candidate suggestions non-authoritative while exposing the exact evidence behind each verdict."
```

---

### Task 6: Implement The Token-Gated Canvas Compare Workspace

**Files:**
- Modify: `.github/extensions/otel-monitor-canvas/extension.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-workspace-helpers.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-workspace-helpers.test.mjs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 5 exact routes/DTOs.
- Produces: Compare UI, closed proxy allow-list, emitted-IIFE runtime evidence.

- [ ] **Step 1: Add proxy/UI RED tests**

Start the real VM helper server and execute the emitted workspace IIFE. Cover
token/no-store/loopback/CSRF/header stripping, only the six #56 routes, exact
methods/query/IDs, JSON/size/body shaping, sanitized/no-echo mapping, absent
application state, candidate-only display, manual classification/exclusion,
case-key grouping, explicit confirmation, every verdict/reason, evidence-ref
identity, rollback invalidation, stale response, request counts, and no
automatic comparison/Verified.

- [ ] **Step 2: Run RED Node/contract tests**

```powershell
node --test .github\extensions\otel-monitor-canvas\canvas-workspace-helpers.test.mjs
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```

- [ ] **Step 3: Add the strict helper proxy**

Allow only the exact #56 route/method combinations. Require the launch token,
set no-store before validation, validate loopback, strip browser auth/CSRF,
inject server CSRF, enforce body/media limits, use fixed safe mapping, and
never proxy an arbitrary URL/path/method.

- [ ] **Step 4: Replace Compare placeholder with explicit workflow**

Render application selection, candidate eligibility/reasons, pre/post/excluded
controls, fixed exclusion reason, case key, evidence links, cohort summary,
explicit `比較を確定`, fixed verdict/reason, metrics, and pair drill-down. Use
`textContent`/attribute APIs only. Keep Improve/apply and all Canvas actions /
`session.send()` unchanged.

- [ ] **Step 5: Run focused tests and commit**

```powershell
node --test .github\extensions\otel-monitor-canvas\canvas-workspace-helpers.test.mjs
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~CanvasExtensionContractTests|FullyQualifiedName~SessionEffectComparisonRouteTests"
git diff --check
git commit -m "Issue #56: feat(canvas): add explicit effect comparison" -m "Compare needs a local evidence-review surface without granting Copilot actions authority to choose cohorts or record Verified outcomes."
```

---

### Task 7: Boundary Matrix, User Guide, Full Validation, And Closeout

**Files:**
- Modify: `docs/user-guide/local-monitor.md`
- Modify: `docs/task.md`
- Modify tests from Tasks 1–6 only when the final matrix exposes a real gap.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: user workflow, complete boundary evidence, verified #56 closeout.

- [ ] **Step 1: Audit the acceptance matrix against executable tests**

Record evidence for: exact receipt linkage; user-confirmed cohort; included /
excluded reasons; proposal/application/time traceability; quality precedence;
all four verdicts; missing/partial/rollback/severe negatives; exact 3x3 and
10% boundaries; summary/pair evidence identity; atomic Verified and races;
Canvas authority/no-leak.

- [ ] **Step 2: Add any missing RED boundary test and minimal fix**

Do not add substitute smoke assertions. Every missing item must execute the
real pure engine, SQLite transaction, HTTP route, helper server, or emitted UI
boundary that owns it.

- [ ] **Step 3: Update the Japanese user guide and roadmap**

Document objective receipt fields, application eligibility, candidate vs
confirmed cohort, pre/post/excluded rules, fixed verdicts, quality precedence,
10% threshold, insufficiency, historical/inactive rollback behavior, and no
automatic Verified/git/action authority. Mark Issue #56 complete only after
validation.

- [ ] **Step 4: Run the focused comparison suite**

```powershell
node --test .github\extensions\otel-monitor-canvas\canvas-workspace-helpers.test.mjs
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~EffectVerdictEngineTests|FullyQualifiedName~SqliteEffectComparisonStoreTests|FullyQualifiedName~SessionObjectiveEvaluationRouteTests|FullyQualifiedName~SessionEffectComparisonRouteTests|FullyQualifiedName~SessionProposalApplyRouteTests|FullyQualifiedName~CanvasExtensionContractTests"
```

Expected: all pass with no skipped required case.

- [ ] **Step 5: Run exact repository validation**

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
git diff --check
```

Expected: build 0 warnings/0 errors, Playwright install succeeds, every test
passes. If a required command fails, fix the #56 regression and rerun the
entire sequence; do not substitute a different command.

- [ ] **Step 6: Commit closeout**

```powershell
git commit -m "Issue #56: test(compare): verify quality-first boundaries"
```

Write focused/full counts and review evidence to `tmp/sdd/issue56-closeout-report.md`.

## Review Gates

1. After every task, a different Terra High reviewer reads the task brief,
   implementer report, and full task diff and returns separate spec-compliance
   and task-quality verdicts.
2. Critical/Important findings return to a Terra Medium fixer; covering tests
   and a re-review are mandatory before the next task.
3. After Task 6, a fresh Terra High quality specialist reviews all #56 code for
   evidence sufficiency, lexicographic quality precedence, threshold arithmetic,
   cohort truthfulness, rollback/revision races, and Canvas non-authority.
4. After Task 7 and exact full validation, a fresh Terra High reviewer audits
   the complete #54–#56 range before the goal is marked complete.
