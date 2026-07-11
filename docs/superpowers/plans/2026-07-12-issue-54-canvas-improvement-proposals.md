# Issue #54 Canvas Improvement Proposals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the evidence-backed, human-controlled Canvas Improve lifecycle defined by Issue #54 without changing the existing `session.send()` analysis boundary.

**Architecture:** Local Monitor owns an additive SQLite proposal store and the sanitized, CSRF-protected proposal routes. The Canvas extension owns only token-gated route proxies and inert-text UI; it starts detailed analysis through the unchanged `/analysis` helper and never reads a Copilot chat response. Proposal status is `candidate` or `recommended` in #54; `verified` remains write-owned by #56.

**Tech Stack:** .NET 10 / ASP.NET Core / Microsoft.Data.Sqlite / xUnit; Node ESM helper and `node --test`; existing Canvas extension contract tests.

## Global Constraints

- Preserve Issue #45 exactly: `POST /analyze` remains `session.send({ prompt })` fire-and-forget; do not call the raw analysis runner, wait for, scrape, or persist a Copilot response.
- Preserve Issue #51 identity/completeness and Issue #53 exact Evidence/Agent ownership; never infer Session, trace, run, event, Skill, test, or review facts.
- Proposal writes are loopback, same-origin, CSRF-protected explicit user actions; never automatically generate, promote, apply, commit, push, or create a PR.
- Persist only the bounded sanitized fields and opaque identifiers in `canvas-improvement-proposals.md`; reject raw content, source fragments, credentials, tokens, PII, and local paths without echoing them.
- Canvas actions, `session.send()` prompts, logs, committed files, CI/static artifacts, and repository-safe summaries stay proposal- and raw-content-free.
- `verified` must return `400` `verification_owned_by_compare` until Issue #56; direct apply/diff/path/snapshot/rollback is exclusively Issue #55.
- No new dependency or compatibility layer. Use inert DOM APIs (`textContent`, `createElement`) and existing per-launch Canvas token routing.
- Implementers do **not** commit. The orchestrator runs independent review, fixes findings, full validation, and the coherent #54 commit after review acceptance.

---

### Task 1: Proposal domain and additive SQLite store

**Files:**

- Modify: `src/CopilotAgentObservability.Telemetry/Sessions/SessionDomain.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SqliteSessionStoreTests.cs`

**Interfaces:**

- Consumes: `ISessionStore`, `ObservedSession`, `ObservedSessionRun`, `ObservedSessionEvent`, and the existing SQLite schema setup.
- Produces: `ImprovementProposal`, `ImprovementProposalEvidenceReference`, `ImprovementProposalStatus`, and store methods `ListImprovementProposals(Guid)`, `CreateImprovementProposal(ImprovementProposal)`, and `UpdateImprovementProposalStatus(Guid, ImprovementProposalStatus, DateTimeOffset)`.

- [ ] **Step 1: Write failing store tests**

```csharp
[Fact]
public void ImprovementProposals_PersistCandidateWithOpaqueReferences()
{
    using var fixture = SessionStoreFixture.Create();
    var proposal = fixture.CreateProposal(status: ImprovementProposalStatus.Candidate);

    fixture.Store.CreateImprovementProposal(proposal);

    var actual = Assert.Single(fixture.Store.ListImprovementProposals(proposal.SourceSessionIds[0]));
    Assert.Equal(proposal.ProposalId, actual.ProposalId);
    Assert.Equal(ImprovementProposalStatus.Candidate, actual.Status);
    Assert.Equal(proposal.EvidenceReferences, actual.EvidenceReferences);
}

[Fact]
public void Promote_WhenAnySourceSessionAlreadyHasRecommendation_Throws()
{
    using var fixture = SessionStoreFixture.Create();
    var existing = fixture.CreateRecommendedProposal();
    fixture.Store.CreateImprovementProposal(existing);
    var competing = fixture.CreateCandidateFor(existing.SourceSessionIds);
    fixture.Store.CreateImprovementProposal(competing);

    Assert.Throws<InvalidOperationException>(() =>
        fixture.Store.UpdateImprovementProposalStatus(
            competing.ProposalId, ImprovementProposalStatus.Recommended, fixture.Now));
}
```

- [ ] **Step 2: Run the new store tests and verify they fail**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SqliteSessionStoreTests
```

Expected: FAIL because the proposal domain/store API does not exist.

- [ ] **Step 3: Add the minimal domain and store implementation**

```csharp
public enum ImprovementProposalStatus { Candidate, Recommended, Verified }

public sealed record ImprovementProposalEvidenceReference(
    string Kind, string ReferenceId);

public sealed record ImprovementProposal(
    Guid ProposalId,
    ImprovementProposalStatus Status,
    string TargetKind,
    string TargetLabel,
    string Title,
    string Summary,
    string ExpectedEffect,
    string RiskNote,
    IReadOnlyList<Guid> SourceSessionIds,
    IReadOnlyList<ImprovementProposalEvidenceReference> EvidenceReferences,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RecommendedAt,
    DateTimeOffset? VerifiedAt);
```

Create the three additive tables from the spec, with `improvement_proposal_sessions.session_id` as a foreign key. Use a single SQLite transaction for promotion: re-read source sessions and references, reject an existing recommendation for any linked Session, then update the status and `recommended_at`. The store must reject `Verified` writes and never access `session_event_content`.

- [ ] **Step 4: Run the focused store suite**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SqliteSessionStoreTests
```

Expected: PASS, including prior Session-store regressions.

- [ ] **Step 5: Self-review the persistence boundary**

Confirm that the migration is additive, foreign keys stay enabled, rollback-on-exception occurs through the SQLite transaction, and no proposal table contains raw text, file paths, or content JSON.

### Task 2: Sanitized proposal HTTP contract

**Files:**

- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Sessions/ImprovementProposalRequest.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionWorkspaceRouteTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/SessionImprovementProposalRouteTests.cs`

**Interfaces:**

- Consumes: Task 1 `ISessionStore` proposal APIs, `MonitorHost.IsCrossSiteRequest`, `MonitorHost.HasMonitorCsrfHeader`, `SessionRoutes.MaximumBodyBytes`.
- Produces: the three Issue #54 routes and exact fixed-error JSON bodies specified by `canvas-improvement-proposals.md`.

- [ ] **Step 1: Write failing HTTP contract tests**

```csharp
[Fact]
public async Task CreateProposal_ReturnsCreatedSanitizedObject_WhenRequestIsValid()
{
    using var host = await LocalMonitorHost.StartAsync();
    var response = await host.Client.PostAsJsonAsync(
        "/api/session-workspace/improvement-proposals",
        ValidCandidate(), HostHeaders.SameOriginCsrf);

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert.Equal("candidate", body.RootElement.GetProperty("status").GetString());
    Assert.False(body.RootElement.TryGetProperty("content", out _));
}

[Theory]
[InlineData("verified", "verification_owned_by_compare")]
[InlineData("recommended", "insufficient_recommendation_evidence")]
public async Task UpdateProposalStatus_RejectsInvalidTransition(string status, string error)
{
    var response = await PutStatusAsync(status);
    await AssertFixedError(response, HttpStatusCode.BadRequest, error);
}
```

- [ ] **Step 2: Run the route tests and verify they fail**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SessionImprovementProposalRouteTests
```

Expected: FAIL because proposal routes are not mapped.

- [ ] **Step 3: Implement bounded parsing, validation, and routes**

Use `JsonUnmappedMemberHandling.Disallow` and an explicit request DTO. Enforce UUIDv7 Session IDs, target-kind allowlist, all field limits, 1..10 evidence references, distinct source Sessions, exact-bound/terminal Session checks, existing Run/Event/Trace ownership checks, and unsafe-value rejection. Reuse existing same-origin, CSRF, content-type, and bounded-body patterns; every rejected request must use `Failure(context, status, code)` without echoing input.

```csharp
app.MapPost("/api/session-workspace/improvement-proposals", async context =>
{
    if (MonitorHost.IsCrossSiteRequest(context)) { await Failure(context, 403, "cross_origin_forbidden"); return; }
    if (!MonitorHost.HasMonitorCsrfHeader(context)) { await Failure(context, 403, "csrf_required"); return; }
    // Validate a strict ImprovementProposalRequest, create a Candidate, then return 201.
});
```

Map the `GET` list route before the existing `/sessions/{sessionId}` parameterized route and map the status `PUT` route with a non-ambiguous fixed suffix. Set `Cache-Control: no-store` on all three proposal responses.

- [ ] **Step 4: Run focused routes and nearby regression tests**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SessionImprovementProposalRouteTests|FullyQualifiedName~SessionWorkspaceRouteTests|FullyQualifiedName~SessionHumanEvaluationRouteTests"
```

Expected: PASS, including cross-site/CSRF, malformed JSON, missing evidence, unsafe text, stale/missing references, recommendation collision, and no-echo assertions.

- [ ] **Step 5: Self-review route safety**

Verify that a `verified` request cannot mutate state, every mutation revalidates state inside the store transaction, and every GET/POST/PUT response is metadata-only and no-store.

### Task 3: Token-gated Canvas proxy and Improve UI

**Files:**

- Modify: `.github/extensions/otel-monitor-canvas/extension.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-workspace-helpers.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-workspace-helpers.test.mjs`
- Modify: `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`

**Interfaces:**

- Consumes: Task 2 monitor routes; existing helper `request()` that attaches the Canvas token; existing token/loopback monitor checks and `renderWorkspaceHtml`.
- Produces: token-gated helper proxy routes and an Improve tab that can list, create, and explicitly promote sanitized proposals without exposing them to Canvas actions or analysis prompts.

- [ ] **Step 1: Write failing Node tests**

```js
test("Improve shows an honest unavailable state for an unbound session", () => {
  const html = renderWorkspaceHtml({ monitorUrl, healthState: "ready", token, nativeSessionId });
  assert.match(html, /改善案を作成/);
  assert.match(html, /native binding/);
});

test("proposal proxy adds monitor CSRF but never sends proposal text to session.send", async () => {
  const result = await invokeHelperProposalCreate(validCandidate);
  assert.equal(result.status, 201);
  assert.equal(sessionSendCalls.length, 0);
  assert.equal(actionPayloads.some(payload => "summary" in payload), false);
});
```

- [ ] **Step 2: Run Node tests and verify they fail**

Run:

```powershell
node --test .github\extensions\otel-monitor-canvas\canvas-workspace-helpers.test.mjs .github\extensions\otel-monitor-canvas\canvas-helpers.test.mjs
```

Expected: FAIL because the Improve tab and proposal proxies do not exist.

- [ ] **Step 3: Implement proxy routes and UI state**

In `extension.mjs`, add only token-gated loopback routes for proposal list,
create, and status update. Validate path/query IDs and forward request bodies
unchanged only after size/JSON checks; inject `x-monitor-csrf: local-monitor`
server-side. Do not add a Canvas action, logging, or `session.send()` data.

In `canvas-workspace-helpers.mjs`, replace only the Improve placeholder. Load
proposals when Improve is selected; render all user text through `textContent`.
Show the existing `/analysis` link for a terminal native-bound Session, a
structured Candidate form, one Recommended card, and collapsed Candidate
alternatives. Disable creation/promotion for unbound, active, or unavailable
evidence states. Keep Compare as its existing placeholder.

- [ ] **Step 4: Run extension and Canvas contract tests**

Run:

```powershell
node --check .github\extensions\otel-monitor-canvas\extension.mjs
node --test .github\extensions\otel-monitor-canvas\canvas-workspace-helpers.test.mjs .github\extensions\otel-monitor-canvas\canvas-helpers.test.mjs
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```

Expected: PASS. Contract tests must demonstrate token requirement, loopback
monitor validation, no raw/PII/path leakage, no Canvas action expansion, and
the unchanged `session.send()` analysis route.

- [ ] **Step 5: Self-review browser boundary**

Confirm every dynamic display path uses `textContent`/`createElement`, proposal
responses are `no-store`, and the browser helper cannot invoke a file operation,
git operation, or raw-analysis route.

### Task 4: Cross-layer contract and specification verification

**Files:**

- Test: all Task 1–3 test files and the repository validation suite.

**Interfaces:**

- Consumes: complete Task 1–3 behavior and the canonical Issue #54 documents.
- Produces: a reviewed, internally consistent Issue #54 deliverable ready for independent review.

- [ ] **Step 1: Write or extend an integration assertion for the end-to-end contract**

```csharp
[Fact]
public async Task CanvasProposalLifecycle_IsMetadataOnlyAndNeverEnablesApply()
{
    var candidate = await CreateCandidateThroughCanvasProxyAsync();
    await PromoteWithTwoExactSessionsAsync(candidate.ProposalId);

    var page = await GetCanvasWorkspaceAsync();
    Assert.Contains("Recommended", page);
    Assert.DoesNotContain("rollback", page, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("git commit", page, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the focused cross-layer suite**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SessionImprovementProposalRouteTests|FullyQualifiedName~SqliteSessionStoreTests|FullyQualifiedName~CanvasExtensionContractTests"
```

Expected: PASS.

- [ ] **Step 3: Compare implementation to the canonical specification**

Check every route, response field, status transition, no-store/CSRF behavior,
and non-goal against `canvas-improvement-proposals.md`. Fix the code/tests for
any mismatch; this task does not alter the approved specification.

- [ ] **Step 4: Leave the deliverable ready for independent review**

Report exact files changed, focused test commands/results, and any remaining
live Canvas validation limitation. Do not commit; the orchestrator dispatches a
fresh Terra High reviewer next.

## Plan Self-Review

- Spec coverage: Tasks 1–3 map respectively to local lifecycle persistence,
  HTTP safety contract, and Canvas UI/proxy behavior; Task 4 covers the
  cross-layer boundary. Every #54 status, safety, and non-goal requirement has
  a test-bearing task.
- Placeholder scan: no unresolved implementation placeholder or unspecified
  error behavior remains in the plan.
- Type consistency: domain types in Task 1 are the only proposal types used by
  Task 2; Task 3 consumes Task 2 JSON routes; `verified` remains unavailable
  until the Issue #56 contract.
