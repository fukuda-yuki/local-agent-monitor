# Sprint8 M6 Security And Live Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close Sprint8 by proving the DR6 security boundary, readiness behavior under failure, restart recovery, raw non-logging, and real VS Code Copilot Chat HTTP/protobuf receipt at the Local Ingestion Monitor.

**Architecture:** M6 is a verification and hardening milestone, not a new product surface. Expand targeted tests around the existing `MonitorHost`, M4 raw route, and M5 UI/SSE. Record sanitized live evidence in sprint review files only after a real monitor-targeted VS Code run reaches `127.0.0.1:4320` through `profile-vscode-env --target monitor`.

**Tech Stack:** .NET 10, ASP.NET Core / Kestrel, xUnit, synthetic OTLP JSON/protobuf fixtures, `dotnet build`, `dotnet test`, manual VS Code live validation with sanitized evidence.

---

## Source Of Truth

Use this order before editing:

1. `docs/requirements.md` section 10 - required build/test and live validation evidence.
2. `docs/spec.md` - public monitor interfaces and live validation boundary.
3. `docs/specifications/layers/telemetry-ingestion.md` - loopback, Host validation, readiness, monitor-targeted config, live evidence fields.
4. `docs/specifications/security-data-boundaries.md` - mandatory negative tests and raw/PII boundaries.
5. `docs/specifications/interfaces/config-cli.md` - `profile-vscode-env --target monitor` behavior.
6. `docs/decisions.md` D020 - DR5 hard gate and DR6 threat model.
7. M4 and M5 implementation reviews.

If live validation cannot be performed because VS Code, Copilot Chat, or user environment access is unavailable, do not mark Sprint8 complete. Record the blocker and the exact missing evidence.

## Scope

M6 owns:

- Negative security matrix tests for non-loopback bind, Host validation, raw opt-in gating, cross-origin raw denial, API/SSE sanitization, raw non-logging, inert rendering, and CSRF/same-origin behavior for state-changing routes.
- Readiness tests for sustained queue-full, commit failure, and projection-lag-exceeded returning `503` with the pinned body.
- Restart recovery and no-loss proof after stopping and starting the monitor on the same DB.
- Real VS Code Copilot Chat HTTP/protobuf live validation at the monitor, using `profile-vscode-env --profile raw-local-receiver --target monitor`.
- Final review and roadmap closeout.

M6 does not own:

- Adding authentication.
- Adding a bearer token.
- Adding CSP, sanitizers, or a heavy anti-XSS matrix beyond inert framework rendering.
- Changing raw-store, measurement, candidate, or static dashboard schemas.

## Target Files

- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorRawViewTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorProjectionApiTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHealthTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSseTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSecurityBoundaryTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorReadinessFailureTests.cs`
- Create: `docs/sprints/sprint8-local-raw-receiver-monitor/milestones/M6-security-live-validation/live-validation.md`
- Create: `docs/sprints/sprint8-local-raw-receiver-monitor/milestones/M6-security-live-validation/review.md`
- Modify: `docs/sprints/sprint8-local-raw-receiver-monitor/README.md`
- Modify: `docs/task.md`
- Modify: `docs/user-guide/telemetry-collection.md`

## Implementation Tasks

### Task 1: Expand DR6 Security Boundary Tests

**Files:**
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSecurityBoundaryTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorRawViewTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSseTests.cs`

- [ ] **Step 1: Write the negative matrix tests.**

```csharp
[Fact]
public async Task SecurityBoundary_DefaultSurfacesNeverReturnRawOrPii()
{
    using var temp = new MonitorTempDirectory();
    SeedSensitiveProjectedRecord(temp);
    await using var host = await StartHostAsync(temp, enableRawView: true);

    var paths = new[] { "/", "/ingestions", "/traces", "/diagnostics", "/api/monitor/ingestions", "/api/monitor/traces" };
    foreach (var path in paths)
    {
        var body = await host.Client.GetStringAsync(path);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", body);
        Assert.DoesNotContain("SECRET_TOOL_ARGS_MARKER", body);
        Assert.DoesNotContain("leak-marker@example.com", body);
    }
}

[Fact]
public async Task SecurityBoundary_RawRouteIsAbsentWithoutFlagAndCrossOriginForbiddenWithFlag()
{
    using var temp = new MonitorTempDirectory();
    var id = SeedSensitiveProjectedRecord(temp);

    await using var disabled = await StartHostAsync(temp, enableRawView: false);
    Assert.Equal(HttpStatusCode.NotFound, (await disabled.Client.GetAsync($"/traces/{id}/raw")).StatusCode);

    await using var enabled = await StartHostAsync(temp, enableRawView: true);
    using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
    request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
    Assert.Equal(HttpStatusCode.Forbidden, (await enabled.Client.SendAsync(request)).StatusCode);
}
```

Also add:

```csharp
[Fact]
public async Task SecurityBoundary_NoStateChangingRouteAcceptsCrossOriginWithoutCsrf()
{
    using var temp = new MonitorTempDirectory();
    await using var host = await StartHostAsync(temp, enableRawView: true);

    using var request = new HttpRequestMessage(HttpMethod.Post, "/events");
    request.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");
    var response = await host.Client.SendAsync(request);

    Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
}
```

If M5 added any state-changing route, replace the last assertion with the concrete expected `403` and CSRF error token for that route. If there are no state-changing routes, this test documents that no CSRF-exposed mutation endpoint exists.

- [ ] **Step 2: Run and confirm failures or gaps.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorSecurityBoundaryTests|FullyQualifiedName~MonitorRawViewTests|FullyQualifiedName~MonitorSseTests"
```

Expected: new tests fail until helper seams and any missing assertions are added. If all pass without code changes, continue to Step 4 and record that M6 only added coverage.

- [ ] **Step 3: Implement only required fixes.** Acceptable fixes are narrow:
  - keep raw route flag gating and same-origin checks.
  - ensure `/events` accepts only `GET`.
  - ensure UI pages use Razor encoding and no `Html.Raw`.
  - ensure SSE emits `data: {}` and no raw identifiers.
  - ensure failure bodies do not include paths, user names, or exception type names.

- [ ] **Step 4: Run targeted tests again.** Expected: pass.

- [ ] **Step 5: Commit.**

```powershell
git add tests\CopilotAgentObservability.LocalMonitor.Tests src\CopilotAgentObservability.LocalMonitor
git commit -m "Sprint8 M6: test: cover monitor security boundary"
```

### Task 2: Prove Readiness Failure Semantics Under Saturation

**Files:**
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorReadinessFailureTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHealthTests.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Health/MonitorHealthState.cs` only if tests expose a contract bug.

- [ ] **Step 1: Write readiness failure tests.**

`MutableTimeProvider` and `ReadyState` are defined as `private` members in `MonitorHealthTests.cs`. To use them in `MonitorReadinessFailureTests.cs`, extract them to a shared `internal` helper file in the test project (e.g., `MonitorTestHelpers.cs`) before writing the tests below, or redefine `MutableTimeProvider` in the new file if extraction would be too broad.

```csharp
[Fact]
public async Task Ready_Returns503ForSustainedQueueFullAnd200ForMomentaryBackpressure()
{
    using var temp = new MonitorTempDirectory();
    var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
    var health = ReadyState(time);
    var queue = new IngestionQueue(capacity: 1);
    Assert.True(queue.TryEnqueue(SyntheticRecord(), out _));
    await using var host = await StartHostAsync(temp, health, queue, ingestionStallThresholdSeconds: 2);

    var failedPost = await host.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
    Assert.Equal(HttpStatusCode.ServiceUnavailable, failedPost.StatusCode);

    var momentary = await host.Client.GetAsync("/health/ready");
    Assert.Equal(HttpStatusCode.OK, momentary.StatusCode);
    Assert.Contains("ingestion_backpressure", await momentary.Content.ReadAsStringAsync());

    time.Advance(TimeSpan.FromSeconds(2));
    var sustained = await host.Client.GetAsync("/health/ready");
    Assert.Equal(HttpStatusCode.ServiceUnavailable, sustained.StatusCode);
    Assert.Contains("ingestion_stalled", await sustained.Content.ReadAsStringAsync());
}
```

Add equivalent tests for:

- commit timeout starts the stall window and becomes `503` after the override threshold.
- projection lag below threshold is `200` / `degraded`; at threshold is `503` / `projection_lag_exceeded`.
- `projection_status_unknown` is `503`.

- [ ] **Step 2: Run and confirm failure or coverage gap.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorReadinessFailureTests|FullyQualifiedName~MonitorHealthTests"
```

Expected: new tests compile after helper additions. If existing code already satisfies the contract, tests pass.

- [ ] **Step 3: Fix only contract violations.** Keep the existing body schema:

```json
{
  "status": "ready | degraded | not_ready",
  "checks": {
    "loopback_bound": true,
    "db_open": true,
    "migration_complete": true,
    "writer_running": true,
    "projection_worker_running": true,
    "ingestion_accepting": true,
    "projection_lag_seconds": 0,
    "projection_backlog": 0
  },
  "degraded_reasons": []
}
```

Do not rename reason tokens unless the current source of truth is updated first.

- [ ] **Step 4: Run targeted tests again.** Expected: pass.

- [ ] **Step 5: Commit.**

```powershell
git add tests\CopilotAgentObservability.LocalMonitor.Tests src\CopilotAgentObservability.LocalMonitor\Health\MonitorHealthState.cs
git commit -m "Sprint8 M6: test: verify readiness failure semantics"
```

### Task 3: Prove Restart Recovery And Raw Non-Logging

**Files:**
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorRawViewTests.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` only if tests expose a leak.

- [ ] **Step 1: Add restart recovery test.**

```csharp
[Fact]
public async Task RestartRecovery_ReusesDatabaseAndCatchesUpProjection()
{
    using var temp = new MonitorTempDirectory();

    await using (var first = await StartHostAsync(temp, projectionPollInterval: TimeSpan.FromMilliseconds(50)))
    {
        var response = await first.Client.PostAsync("/v1/traces", JsonContent(ValidTraceJson()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await WaitForIngestionProjectionCountAsync(first, expected: 1));
    }

    await using var second = await StartHostAsync(temp, projectionPollInterval: TimeSpan.FromMilliseconds(50));
    Assert.Equal(1, await WaitForIngestionProjectionCountAsync(second, expected: 1));
    var ready = await second.Client.GetAsync("/health/ready");
    Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
}
```

- [ ] **Step 2: Add raw non-logging assertions.** Capture in-memory test logs if a logger provider exists in the test harness. If the current host clears providers and has no request logging, assert this explicitly:

```csharp
[Fact]
public async Task RawPayloadMarkers_DoNotAppearInErrorResponsesOrKnownRuntimeOutputs()
{
    using var temp = new MonitorTempDirectory();
    await using var host = await StartHostAsync(temp, enableRawView: true);

    var response = await host.Client.PostAsync("/v1/traces", JsonContent(SensitiveTraceJson));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var bad = await host.Client.PostAsync("/v1/traces", JsonContent("{"));
    var body = await bad.Content.ReadAsStringAsync();
    Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", body);
    Assert.DoesNotContain(temp.DatabasePath, body);
    Assert.DoesNotContain(Environment.UserName, body);
}
```

- [ ] **Step 3: Run targeted tests.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~MonitorHostTests|FullyQualifiedName~MonitorRawViewTests"
```

Expected: pass or expose a leak to fix.

- [ ] **Step 4: Fix only observed leak/recovery issues.** Do not add new logging. Keep `builder.Logging.ClearProviders()` unless current specs require a change.

- [ ] **Step 5: Commit.**

```powershell
git add tests\CopilotAgentObservability.LocalMonitor.Tests src\CopilotAgentObservability.LocalMonitor\MonitorHost.cs
git commit -m "Sprint8 M6: test: cover monitor restart and non-logging"
```

### Task 4: Run Synthetic Full Validation

**Files:**
- No code files unless tests fail for a real defect.

- [ ] **Step 1: Run LocalMonitor targeted tests.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj
```

Expected: all LocalMonitor tests pass.

- [ ] **Step 2: Run required repository validation.**

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Expected: build succeeds with `0` errors and `0` warnings; all tests pass.

- [ ] **Step 3: Stop if validation fails.** Do not substitute another command. Fix the failure or record the exact failed command and output in `review.md`.

### Task 5: Perform Live VS Code Monitor Validation

**Files:**
- Create: `docs/sprints/sprint8-local-raw-receiver-monitor/milestones/M6-security-live-validation/live-validation.md`

- [ ] **Step 1: Start the monitor.**

```powershell
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor-live-validation.db --url http://127.0.0.1:4320
```

Expected console output:

```text
Local monitor listening on http://127.0.0.1:4320.
Raw store: data\monitor-live-validation.db
Press Ctrl+C to stop.
```

Do not commit `data\monitor-live-validation.db`.

- [ ] **Step 2: Generate the official VS Code environment.**

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
```

Expected: `CAO_COLLECTION_PROFILE=raw-local-receiver` and `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320` or the documented equivalent for VS Code. It must not point to `4319`.

- [ ] **Step 3: Run a small VS Code GitHub Copilot Chat interaction.** Use the generated monitor-targeted environment. Record only:
  - date/time.
  - OS and VS Code version.
  - GitHub Copilot extension version.
  - profile value.
  - target value.
  - endpoint shape.
  - monitor command.
  - raw record id or trace id.
  - whether `--enable-raw-view` was set.
  - confirmed signals and unconfirmed signals.

Do not record prompt text, response text, tool arguments, tool results, credentials, local sensitive paths, or raw payloads.

- [ ] **Step 4: Confirm receipt at the monitor.**

```powershell
curl http://127.0.0.1:4320/api/monitor/ingestions
curl http://127.0.0.1:4320/health/ready
```

Expected: at least one sanitized ingestion item and `/health/ready` returns `200` once projection catches up. If `curl` is not available, use the browser or PowerShell `Invoke-WebRequest`; record the tool actually used. This is diagnostic evidence for the manual live gate and does not replace `dotnet build` / `dotnet test`.

- [ ] **Step 5: Write sanitized `live-validation.md`.** Use this exact structure:

```markdown
# Sprint8 M6 Live Validation

Date:
Environment:
VS Code Version:
GitHub Copilot Extension Version:
Monitor Command:
Collection Profile:
Target:
Endpoint:
Raw View Enabled:
Trace Or Raw Record Identifier:
Confirmed:
- HTTP/protobuf telemetry reached LocalMonitor at 127.0.0.1:4320.
- Projection produced sanitized monitor rows.
- Langfuse was not required for this monitor path.

Unconfirmed:
- Metrics/logs signals, unless explicitly observed.

Repository Safety:
- No raw prompt, response, tool arguments, tool results, credentials, or sensitive local paths are recorded here.
```

- [ ] **Step 6: If live validation cannot run, stop.** Do not mark M6 or Sprint8 complete. Record a blocker instead of a completion review.

### Task 6: Final Review, User Guide, And Roadmap Closeout

**Files:**
- Modify: `docs/user-guide/telemetry-collection.md`
- Modify: `docs/sprints/sprint8-local-raw-receiver-monitor/README.md`
- Modify: `docs/task.md`
- Create: `docs/sprints/sprint8-local-raw-receiver-monitor/milestones/M6-security-live-validation/review.md`

- [ ] **Step 1: Update the user guide.** Ensure `docs/user-guide/telemetry-collection.md` shows the monitor path and explicitly uses:

```powershell
dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\raw-store.db --url http://127.0.0.1:4320
dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor
```

State that `--enable-raw-view` is optional, off by default, loopback-only, and never for repository-safe artifacts.

- [ ] **Step 2: Update sprint and roadmap docs.** Mark M6 complete only if Task 5 produced live evidence. Mark Sprint8 complete only if M5 is already implemented and M6 validation is green.

- [ ] **Step 3: Write `review.md`.** Include:
  - scope reviewed.
  - source-of-truth comparison.
  - negative security matrix result.
  - readiness saturation result.
  - restart recovery result.
  - raw non-logging result.
  - live validation evidence summary, referencing `live-validation.md`.
  - commands run and results.
  - residual risks accepted by D020.

- [ ] **Step 4: Run final validation after docs.**

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Expected: build succeeds with `0` errors and `0` warnings; all tests pass.

- [ ] **Step 5: Commit.**

```powershell
git add docs\user-guide\telemetry-collection.md docs\sprints\sprint8-local-raw-receiver-monitor docs\task.md tests\CopilotAgentObservability.LocalMonitor.Tests src\CopilotAgentObservability.LocalMonitor
git commit -m "Sprint8 M6: docs: record security and live validation"
```

## Final Acceptance

M6 is complete only when:

- DR6 negative security matrix is covered by tests.
- default UI, API, and SSE never return raw / PII.
- raw route remains absent without `--enable-raw-view`, same-origin enforced with it, and inertly rendered.
- readiness returns `503` for sustained ingestion stall and projection lag exceeded, with the pinned body.
- restart recovery is proven on the same SQLite DB.
- raw payload markers do not appear in error responses, logs, or repository-safe outputs.
- real VS Code Copilot Chat telemetry reaches `http://127.0.0.1:4320` using `profile-vscode-env --target monitor`, and sanitized evidence is recorded.
- `dotnet build CopilotAgentObservability.slnx` and `dotnet test CopilotAgentObservability.slnx` pass after all changes.
- Sprint docs and roadmap are updated.

## Self-Review Notes

- **Spec coverage:** This plan covers Sprint8 M6 in `requirements-and-replan.md`, `README.md`, `requirements.md` section 10, `telemetry-ingestion.md`, `security-data-boundaries.md`, and D020.
- **Placeholder scan:** Live validation has an explicit blocker rule; the plan must not be marked complete without evidence.
- **Type consistency:** New test classes are `MonitorSecurityBoundaryTests` and `MonitorReadinessFailureTests`. No new production API is introduced unless tests expose a source-of-truth defect.
