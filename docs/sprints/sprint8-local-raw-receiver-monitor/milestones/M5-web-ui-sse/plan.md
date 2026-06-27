# Sprint8 M5 Web UI And SSE Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the Local Ingestion Monitor browser UI and a notification-only SSE stream without changing the sanitized projection, raw-view, or readiness contracts delivered by M4.

**Architecture:** Keep `MonitorHost` as the single ASP.NET Core process. Add Razor Pages for the default UI and keep those pages as thin server-rendered readers over `IMonitorProjectionStore` plus `MonitorHealthState`; the browser uses the existing cursor APIs for gap recovery. Add a bounded in-memory SSE notification broker that publishes "projection changed" notifications after raw records are projected; SSE never carries raw payloads or PII and is not the source of truth.

**Tech Stack:** .NET 10, ASP.NET Core / Kestrel, Razor Pages, minimal APIs, `Channel<T>`, `IHostedService`, xUnit, synthetic OTLP fixtures.

---

## Source Of Truth

Use this order before editing:

1. `docs/requirements.md` - Local Ingestion Monitor, opt-in raw view, validation requirements.
2. `docs/spec.md` - public monitor endpoints, SSE notification stream, raw-detail route.
3. `docs/specifications/layers/telemetry-ingestion.md` - monitor run model, read APIs, health/readiness, SSE notification-only rule.
4. `docs/specifications/security-data-boundaries.md` - sanitized default views, inert text rendering, raw opt-in boundary.
5. `docs/decisions.md` D020 - DR6 local trust model and accepted display-side risk.
6. Existing M4 implementation and tests.

If these conflict, stop and surface the conflict before changing code.

## Scope

M5 owns:

- Razor Pages for `/`, `/ingestions`, `/traces`, and `/diagnostics`.
- A small shared Razor layout and site assets under `wwwroot`.
- A notification-only SSE stream, for example `GET /events`, with reconnect-friendly `id:` fields.
- UI tests proving default pages, API responses, and SSE never include raw prompt / response / tool content or PII.
- UI wiring to the existing opt-in raw route by link only: raw payloads are still served solely by `GET /traces/{rawRecordId}/raw` and only when `--enable-raw-view` is enabled.

M5 does not own:

- New raw JSON APIs.
- Authentication or multi-user authorization.
- Live VS Code validation. That is M6.
- Heavy CSP / XSS hardening. The required baseline is framework default output encoding and no `Html.Raw` / live markup.

## Target Files

- Modify: `src/CopilotAgentObservability.LocalMonitor/CopilotAgentObservability.LocalMonitor.csproj`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/_ViewImports.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Shared/_Layout.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Ingestions.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Ingestions.cshtml.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Diagnostics.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Diagnostics.cshtml.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.css`
- Create: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.js`
- Create: `src/CopilotAgentObservability.LocalMonitor/Events/MonitorEventBroker.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Events/MonitorEvent.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorHostTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSseTests.cs`
- Modify: `docs/sprints/sprint8-local-raw-receiver-monitor/README.md`
- Modify: `docs/task.md`
- Create: `docs/sprints/sprint8-local-raw-receiver-monitor/milestones/M5-web-ui-sse/review.md`

## Implementation Tasks

### Task 1: Add Razor Pages Host Wiring

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/CopilotAgentObservability.LocalMonitor.csproj`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`

- [ ] **Step 1: Write failing route tests.**

```csharp
[Fact]
public async Task UiRoutes_ReturnSuccessfulHtmlPages()
{
    using var temp = new MonitorTempDirectory();
    await using var host = await StartHostAsync(temp);

    foreach (var path in new[] { "/", "/ingestions", "/traces", "/diagnostics" })
    {
        var response = await host.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Local Ingestion Monitor", body);
    }
}
```

- [ ] **Step 2: Run the targeted test and verify it fails.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
```

Expected: `404` for the UI routes or compile failure because `MonitorUiTests` references helpers not yet added.

- [ ] **Step 3: Wire Razor Pages.** In `MonitorHost.Build`, before building the app:

```csharp
builder.Services.AddRazorPages();
```

After the existing exception-handler middleware and the host-header validation middleware, add:

```csharp
app.UseStaticFiles();
app.MapRazorPages();
```

Do not enable CORS. Do not remove existing minimal API route mappings.

- [ ] **Step 4: Ensure Razor content is compiled.** Keep the project as `Microsoft.NET.Sdk.Web`; no package dependency is required for Razor Pages. Only add csproj items if Razor or static files are not included by SDK defaults.

- [ ] **Step 5: Run the test again.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
```

Expected: the route test passes after the pages in Task 2 exist.

- [ ] **Step 6: Commit.**

```powershell
git add src\CopilotAgentObservability.LocalMonitor tests\CopilotAgentObservability.LocalMonitor.Tests
git commit -m "Sprint8 M5: feat: wire monitor razor pages"
```

### Task 2: Add Overview And Diagnostics Pages

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/_ViewImports.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Shared/_Layout.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Diagnostics.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Diagnostics.cshtml.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.css`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`

- [ ] **Step 1: Add tests for overview and diagnostics content.**

```csharp
[Fact]
public async Task OverviewAndDiagnostics_RenderReadinessWithoutRawContent()
{
    using var temp = new MonitorTempDirectory();
    SeedRawWithSensitiveMarkers(temp);
    await using var host = await StartHostAsync(temp, enableRawView: false);

    var overview = await host.Client.GetStringAsync("/");
    var diagnostics = await host.Client.GetStringAsync("/diagnostics");

    Assert.Contains("status", overview);
    Assert.Contains("health/ready", diagnostics);
    Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", overview);
    Assert.DoesNotContain("leak-marker@example.com", diagnostics);
}
```

- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
```

Expected: pages or page models do not exist.

- [ ] **Step 3: Create the shared imports and layout.**

```cshtml
@using CopilotAgentObservability.LocalMonitor
@namespace CopilotAgentObservability.LocalMonitor.Pages
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

Use a compact layout with navigation links to `/`, `/ingestions`, `/traces`, and `/diagnostics`. Do not use `Html.Raw`.

- [ ] **Step 4: Create `IndexModel`.**

```csharp
internal sealed class IndexModel : PageModel
{
    private readonly MonitorHealthState health;
    private readonly IMonitorProjectionStore projectionStore;
    private readonly MonitorOptions options;

    public IndexModel(MonitorHealthState health, IMonitorProjectionStore projectionStore, MonitorOptions options)
    {
        this.health = health;
        this.projectionStore = projectionStore;
        this.options = options;
    }

    public MonitorReadiness Readiness { get; private set; } = null!;
    public IReadOnlyList<MonitorIngestionRow> RecentIngestions { get; private set; } = [];

    public void OnGet()
    {
        Readiness = health.Evaluate(options.IngestionStallThresholdSeconds, options.ProjectionLagThresholdSeconds);
        RecentIngestions = projectionStore.ListMonitorIngestions(afterRawRecordId: 0, limit: 10).Items;
    }
}
```

If `MonitorOptions` is not available from DI, register the existing `options` instance in Task 1 with `builder.Services.AddSingleton(options);`.

- [ ] **Step 5: Create `DiagnosticsModel`.**

```csharp
internal sealed class DiagnosticsModel : PageModel
{
    private readonly MonitorHealthState health;
    private readonly MonitorOptions options;

    public DiagnosticsModel(MonitorHealthState health, MonitorOptions options)
    {
        this.health = health;
        this.options = options;
    }

    public MonitorReadiness Readiness { get; private set; } = null!;

    public void OnGet()
    {
        Readiness = health.Evaluate(options.IngestionStallThresholdSeconds, options.ProjectionLagThresholdSeconds);
    }
}
```

- [ ] **Step 6: Implement the pages.** Render readiness fields and recent sanitized ingestion rows with normal Razor expressions such as `@row.TraceId`, `@reason`, and `@Model.Readiness.Status`. Razor encodes these values by default; do not use `Html.Raw`, `MarkupString`, or manually concatenated HTML.

- [ ] **Step 7: Run targeted UI tests.** Expected: pass.

- [ ] **Step 8: Commit.**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\Pages src\CopilotAgentObservability.LocalMonitor\wwwroot tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorUiTests.cs
git commit -m "Sprint8 M5: feat: add monitor overview and diagnostics pages"
```

### Task 3: Add Ingestions And Traces Pages

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Ingestions.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Ingestions.cshtml.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`

- [ ] **Step 1: Add tests for sanitized list pages and raw-link behavior.**

```csharp
[Fact]
public async Task ListPages_RenderSanitizedRowsAndOnlyLinkRawWhenEnabled()
{
    using var temp = new MonitorTempDirectory();
    var rawRecordId = SeedRawWithSensitiveMarkers(temp);

    await using var sanitizedHost = await StartHostAsync(temp, enableRawView: false);
    var sanitizedIngestions = await sanitizedHost.Client.GetStringAsync("/ingestions");
    Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", sanitizedIngestions);
    Assert.DoesNotContain($"/traces/{rawRecordId}/raw", sanitizedIngestions);

    await using var rawHost = await StartHostAsync(temp, enableRawView: true);
    var rawIngestions = await rawHost.Client.GetStringAsync("/ingestions");
    Assert.Contains($"/traces/{rawRecordId}/raw", rawIngestions);
}
```

- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
```

Expected: list pages do not exist or raw-link assertions fail.

- [ ] **Step 3: Create page models.** Use the existing projection store with `after` and `limit` query properties:

```csharp
internal sealed class IngestionsModel : PageModel
{
    private readonly IMonitorProjectionStore projectionStore;
    private readonly MonitorOptions options;

    public IngestionsModel(IMonitorProjectionStore projectionStore, MonitorOptions options)
    {
        this.projectionStore = projectionStore;
        this.options = options;
    }

    [BindProperty(SupportsGet = true)]
    public long After { get; set; }

    public MonitorProjectionPage<MonitorIngestionRow> PageResult { get; private set; } = null!;
    public bool RawViewEnabled => options.EnableRawView;

    public IActionResult OnGet()
    {
        if (After < 0)
        {
            return BadRequest();
        }

        PageResult = projectionStore.ListMonitorIngestions(After, limit: 50);
        return Page();
    }
}
```

Use the same pattern for `TracesModel`, reading `ListMonitorTraces(After, 50)`.

- [ ] **Step 4: Implement pages.** Render tables with sanitized columns only. For ingestions, show a "raw" link only when `RawViewEnabled` is true:

```cshtml
@if (Model.RawViewEnabled)
{
    <a href="/traces/@row.RawRecordId/raw">raw</a>
}
```

Do not render raw payload values or PII. Use normal Razor encoding.

- [ ] **Step 5: Run targeted tests.** Expected: pass.

- [ ] **Step 6: Commit.**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\Pages tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorUiTests.cs
git commit -m "Sprint8 M5: feat: add sanitized monitor list pages"
```

### Task 4: Add Notification-Only SSE

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/Events/MonitorEvent.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Events/MonitorEventBroker.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSseTests.cs`

- [ ] **Step 1: Write SSE tests.**

```csharp
[Fact]
public async Task Events_NotifyAfterProjectionAndDoNotCarryRawOrPii()
{
    using var temp = new MonitorTempDirectory();
    await using var host = await StartHostAsync(temp, projectionPollInterval: TimeSpan.FromMilliseconds(50));

    using var streamTask = host.Client.GetStreamAsync("/events");
    var post = await host.Client.PostAsync("/v1/traces", JsonContent(SensitiveTraceJson));
    Assert.Equal(HttpStatusCode.OK, post.StatusCode);

    var eventText = await ReadUntilAsync(await streamTask, "event: projection", TimeSpan.FromSeconds(10));
    Assert.Contains("event: projection", eventText);
    Assert.Contains("data: {}", eventText);
    Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", eventText);
    Assert.DoesNotContain("leak-marker@example.com", eventText);
}
```

- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSseTests
```

Expected: `/events` returns `404`.

- [ ] **Step 3: Implement `MonitorEventBroker`.** Use bounded channels per subscriber and publish only sanitized event metadata:

```csharp
internal sealed record MonitorEvent(long Id, string Type);

internal sealed class MonitorEventBroker
{
    private long nextId;
    private readonly object gate = new();
    private readonly List<Channel<MonitorEvent>> subscribers = [];

    public async IAsyncEnumerable<MonitorEvent> Subscribe(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<MonitorEvent>(
            new BoundedChannelOptions(16)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        lock (gate)
        {
            subscribers.Add(channel);
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (gate)
            {
                subscribers.Remove(channel);
            }
        }
    }

    public void PublishProjectionChanged()
    {
        var evt = new MonitorEvent(Interlocked.Increment(ref nextId), "projection");
        Channel<MonitorEvent>[] snapshot;
        lock (gate)
        {
            snapshot = subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            subscriber.Writer.TryWrite(evt);
        }
    }
}
```

The event payload for now is always `{}`. Do not include trace ids, raw record ids, span names, attributes, raw content, or PII.

- [ ] **Step 4: Publish after projection changes.** Inject `MonitorEventBroker` into `ProjectionWorker`. Track whether any call to `ApplyProjection(record.Id!.Value, record.Source, record.ReceivedAt, projection, timeProvider.GetUtcNow())` returned `true` during a pass; after the pass status refresh, call `PublishProjectionChanged()` once if at least one record was newly projected.

- [ ] **Step 5: Map `/events`.**

```csharp
app.MapGet("/events", async context =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.ContentType = "text/event-stream";

    await foreach (var evt in eventBroker.Subscribe(context.RequestAborted))
    {
        await context.Response.WriteAsync($"id: {evt.Id}\n", context.RequestAborted);
        await context.Response.WriteAsync($"event: {evt.Type}\n", context.RequestAborted);
        await context.Response.WriteAsync("data: {}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});
```

No `Last-Event-ID` replay is implemented. Reconnect clients use `/api/monitor/*` cursors for gap recovery.

- [ ] **Step 6: Run SSE tests.** Expected: pass.

- [ ] **Step 7: Commit.**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\Events src\CopilotAgentObservability.LocalMonitor\Projection\ProjectionWorker.cs src\CopilotAgentObservability.LocalMonitor\MonitorHost.cs tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorSseTests.cs
git commit -m "Sprint8 M5: feat: add sanitized monitor sse notifications"
```

### Task 5: Add Browser Gap Recovery Script

**Files:**
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.js`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Ingestions.cshtml`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`

- [ ] **Step 1: Add static asset tests.**

```csharp
[Fact]
public async Task Pages_ReferenceMonitorScriptAndScriptUsesCursorApis()
{
    using var temp = new MonitorTempDirectory();
    await using var host = await StartHostAsync(temp);

    var index = await host.Client.GetStringAsync("/");
    var script = await host.Client.GetStringAsync("/monitor.js");

    Assert.Contains("/monitor.js", index);
    Assert.Contains("/api/monitor/ingestions", script);
    Assert.Contains("/api/monitor/traces", script);
    Assert.Contains("new EventSource('/events')", script);
}
```

- [ ] **Step 2: Run and confirm failure.**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorUiTests
```

Expected: script is missing or does not reference the cursor APIs.

- [ ] **Step 3: Implement `monitor.js`.** Keep it small:

```javascript
(() => {
  const state = { ingestions: 0, traces: 0 };
  async function refresh(path, key) {
    const response = await fetch(`${path}?after=${state[key]}&limit=50`, { cache: 'no-store' });
    if (!response.ok) return;
    const page = await response.json();
    for (const item of page.items) {
      state[key] = Math.max(state[key], item.rawRecordId ?? item.id ?? 0);
    }
    document.dispatchEvent(new CustomEvent('cao-monitor-refresh', { detail: { path, count: page.items.length } }));
  }
  const events = new EventSource('/events');
  events.addEventListener('projection', () => {
    refresh('/api/monitor/ingestions', 'ingestions');
    refresh('/api/monitor/traces', 'traces');
  });
})();
```

The script does not insert raw payloads into the DOM. It only updates internal cursors and emits a local event for future UI refresh behavior.

- [ ] **Step 4: Reference the script from pages.** Add `<script src="/monitor.js"></script>` in the layout or target pages. Do not add inline script.

- [ ] **Step 5: Run targeted tests.** Expected: pass.

- [ ] **Step 6: Commit.**

```powershell
git add src\CopilotAgentObservability.LocalMonitor\wwwroot\monitor.js src\CopilotAgentObservability.LocalMonitor\Pages tests\CopilotAgentObservability.LocalMonitor.Tests\MonitorUiTests.cs
git commit -m "Sprint8 M5: feat: add monitor event gap recovery client"
```

### Task 6: M5 Tracking, Review, And Validation

**Files:**
- Modify: `docs/sprints/sprint8-local-raw-receiver-monitor/README.md`
- Modify: `docs/task.md`
- Create: `docs/sprints/sprint8-local-raw-receiver-monitor/milestones/M5-web-ui-sse/review.md`

- [ ] **Step 1: Run full required validation.**

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Expected: build succeeds with `0` errors and `0` warnings; tests pass and the count is above the M4 baseline of `421`.

- [ ] **Step 2: Update tracking docs.** Mark M5 implemented only after validation passes. Keep M6 pending and explicitly list remaining scope: DR6 negative matrix, CSRF-less state-change rejection if any state-changing endpoint exists, raw non-logging audit, readiness saturation checks, restart recovery closeout, and live VS Code evidence.

- [ ] **Step 3: Write `review.md`.** Include:
  - scope reviewed.
  - changed files and behavior.
  - source-of-truth comparison.
  - commands run and results.
  - statement that UI/SSE surfaces are sanitized and raw remains only on the opt-in raw route.
  - residual M6 risks.

- [ ] **Step 4: Commit.**

```powershell
git add docs\sprints\sprint8-local-raw-receiver-monitor\README.md docs\task.md docs\sprints\sprint8-local-raw-receiver-monitor\milestones\M5-web-ui-sse\review.md
git commit -m "Sprint8 M5: docs: record milestone review"
```

## Final Acceptance

M5 is complete only when:

- `/`, `/ingestions`, `/traces`, and `/diagnostics` return HTML pages.
- default pages render sanitized metadata only and no raw / PII.
- raw links appear only when `--enable-raw-view` is enabled, and raw content is still served only by `GET /traces/{rawRecordId}/raw`.
- `/events` is `text/event-stream`, emits notification-only projection events, and never carries raw / PII.
- reconnect/gap recovery uses `/api/monitor/*` cursors.
- `dotnet build CopilotAgentObservability.slnx` and `dotnet test CopilotAgentObservability.slnx` pass.
- M5 review and roadmap docs are updated.

## Self-Review Notes

- **Spec coverage:** This plan covers the Sprint8 M5 Web UI + SSE milestone in `requirements-and-replan.md`, `README.md`, `spec.md`, `telemetry-ingestion.md`, and `security-data-boundaries.md`.
- **Placeholder scan:** No task depends on unspecified future work. Each code task has concrete tests, files, and commands.
- **Type consistency:** Stable names are `MonitorEvent`, `MonitorEventBroker`, `MonitorUiTests`, and `MonitorSseTests`; existing types remain `MonitorHost`, `MonitorHealthState`, `IMonitorProjectionStore`, `MonitorProjectionPage<T>`, `MonitorIngestionRow`, and `MonitorTraceRow`.
