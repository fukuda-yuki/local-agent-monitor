# Retention Raw Replay Test Clock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `RetentionRawReplayStoreTests` deterministic after the fixture's seven-day TTL boundary.

**Architecture:** Keep production code unchanged. The test fixture owns one fixed `TimeProvider` and passes it to both the Retention catalog and every raw replay store, including reopened-catalog cases.

**Tech Stack:** C# 14, .NET 10, xUnit, `TimeProvider`

## Global Constraints

- Do not change runtime behavior, TTL, routes, schemas, or public error mappings.
- Do not derive fixture time from the system clock.
- Use the existing four failures as RED evidence.

---

### Task 1: Share the fixture clock with every raw replay store

**Files:**
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/RetentionRawReplayStoreTests.cs`
- Test: `tests/CopilotAgentObservability.ConfigCli.Tests/RetentionRawReplayStoreTests.cs`

**Interfaces:**
- Consumes: `RetentionRawReplayStore(RetentionCatalogStore, string?, TimeProvider?)`
- Produces: deterministic test setup using one fixture-owned `TimeProvider`

- [x] **Step 1: Confirm the RED state**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~RetentionRawReplayStoreTests
```

Expected: four failures reporting `Denied` or `replay_store_denied`, with two passing cases.

- [x] **Step 2: Add and reuse the fixture clock**

Add this property and initialize it before the context/catalog:

```csharp
public TimeProvider TimeProvider { get; }

TimeProvider = new FixedTimeProvider(Now);
Context = RetentionCatalogContext.InitializeNewOwnedDatabase(DatabasePath, TimeProvider);
Catalog = new RetentionCatalogStore(Context, TimeProvider);
```

Pass `fixture.TimeProvider` as the third constructor argument at every
`new RetentionRawReplayStore(...)` call in the test class.

- [x] **Step 3: Verify GREEN for the regression**

Run the Step 1 command again.

Expected: six passed, zero failed.

- [x] **Step 4: Verify the owning test project**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
```

Expected: zero failed.

- [x] **Step 5: Run repository validation and review**

Run the repository `validate` skill commands in order, then inspect
`git diff --check`, the exact diff, and working-tree status. The complete
solution test must report zero failures before completion.

- [x] **Step 6: Commit the tested fix**

Stage only the test file and this plan, then create a local commit with the
work item prefix `Retention raw replay test clock`.
