# M2 dashboard summary — self-review

Per `docs/agent-guides/review-workflow.md`. Implementation per
`plan.md` in this directory and the confirmed contract in
`docs/decisions.md` D037 ("Child B — `GET /api/monitor/summary`").

## Scope reviewed

New sanitized aggregate endpoint `GET /api/monitor/summary` backed by a new
shared `MonitorSummaryService`, also used by the Razor `Index` page to
replace its three inline highlight-computation LINQ blocks
(`LatestTrace` / `TopTokenTrace` / `ErrorTrace`).

## Files changed

- `src/CopilotAgentObservability.LocalMonitor/Projection/MonitorSummaryService.cs`
  (new). `MonitorSummary` / `MonitorModelSummary` / `MonitorClientKindSummary`
  records and `MonitorSummaryService.BuildSummary(limit)`, matching the plan's
  service shape exactly (including `int ErrorCount` per the plan's record
  signature, safe because the window is bounded to at most 200 rows).
- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`:
  - DI registration: `builder.Services.AddSingleton(summaryService)`
    immediately after the existing `projectionStore` singleton registration
    (same pattern, same lifetime — `IMonitorProjectionStore` is already
    Singleton and safe for concurrent use, so the new service built on top of
    it is Singleton too).
  - New route `GET /api/monitor/summary`, added immediately after
    `/api/monitor/traces/{traceId}/spans` as the plan specifies. Query-param
    validation (`limit` only, default 50, range 1–200) via new
    `TryParseLimitQuery` / `WriteInvalidLimitQueryAsync` helpers mirroring the
    existing `TryParseCursorQuery` clamp logic but without an `after` cursor.
    `PersistenceBusyException` mapped to `503 persistence_busy` exactly like
    the other `/api/monitor/*` routes. No same-origin/`Cache-Control: no-store`
    (sanitized, not raw-bearing — same posture as `/api/monitor/traces`).
  - Extracted a `ToTraceDto` private static helper from the existing
    `/api/monitor/traces` inline anonymous-object projection so the same
    `compactTrace` field set is reused for the embedded `latest_trace` /
    `top_token_trace` / `error_trace` objects in the summary response (returns
    `null` for a `null` row). `/api/monitor/traces`'s own handler was updated
    to call the same helper (`page.Items.Select(ToTraceDto)`), so there is now
    exactly one place that defines the trace projection shape instead of two
    copies.
- `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml.cs`:
  replaced the three inline LINQ blocks with one
  `MonitorSummaryService.BuildSummary(RecentLimit)` call resolved via
  `HttpContext.RequestServices` (matching how the page already resolves
  `IMonitorProjectionStore` / `MonitorOptions`). `Index.cshtml` was not
  touched; the three highlight properties keep their existing names/types
  (`MonitorTraceRow?`).
- `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSummaryEndpointTests.cs`
  (new). HTTP-level coverage (empty store, multi-model/multi-client-kind
  subtotal reconciliation, error-trace surfacing, unknown-bucket grouping,
  invalid/omitted `limit`, no raw/PII leakage) plus one direct unit test of
  `MonitorSummaryService.BuildSummary` against an in-memory fake
  `IMonitorProjectionStore` for the null-`PrimaryModel`/`ClientKind` →
  `"unknown"` case, per the plan's explicit list.

## Contract check against D037

Verified the response shape field-by-field against
`docs/specifications/security-data-boundaries.md` ("Child B —
`GET /api/monitor/summary`") and `docs/decisions.md` D037: `scope.limit` /
`scope.trace_count`, `latest_trace` / `top_token_trace` / `error_trace`
(`compactTrace`-shaped or `null`), `per_model_summary` /
`per_client_kind_summary` (`model`/`client_kind`, `trace_count`,
`total_tokens`, `error_count`). No `readiness` field. No new SQL surface —
aggregation runs in memory over `ListMonitorTraces(0, limit)`, the same call
`/api/monitor/traces` and the Razor page already make. No ambiguity or
conflict between plan.md and D037 was found; no stop condition was triggered.

## Validation commands run

```powershell
dotnet build CopilotAgentObservability.slnx
```
Result: build succeeded, 0 warnings, 0 errors.

```powershell
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj
```
Result: passed, 302 total (0 failed, 0 skipped), including all pre-existing
tests (Playwright browser smoke tests included in that count — the test
environment already had Playwright Chromium available, so
`install-playwright-chromium.ps1` was not separately invoked in this run).

Targeted re-run of the new tests alone:
```powershell
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~Summary"
```
Result: 14 passed (9 `MonitorSummaryEndpointTests` + the pre-existing
`MonitorAnalysisRouteTests.RepositorySafeSummary_DoesNotIncludeRawMarkers` /
`MonitorAnalysisStoreTests.*` / `CanvasExtensionContractTests.*Summary*`
matches swept in by the name filter), 0 failed.

## Findings

No blocking issues found.

- `git diff --stat` confirms only the owned-scope files changed:
  `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`,
  `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml.cs`, plus the
  two new files. `Index.cshtml`, `Program.cs`,
  `.github/extensions/otel-monitor-canvas/**`, and
  `tests/.../CanvasExtensionContractTests.cs` were not touched (those last two
  are concurrently modified by the M3/child-C agent, confirmed untouched by
  this change set).
- `Program.cs` did not need a change: DI registration in this codebase
  happens in `MonitorHost.Build`, not `Program.cs` (confirmed by reading both
  files before editing); `IMonitorProjectionStore` follows the same pattern.

## Residual risks / unverified scope

- No live VS Code Copilot Chat / Canvas extension validation was performed
  (out of scope for this milestone — the plan explicitly defers the Canvas
  adapter consuming this endpoint to a later step).
- The new per-model/per-client-kind summary arrays are exposed only via the
  API; no new Index page panel was added, per the plan's explicit scope
  boundary ("Index ページへの新規パネル追加は本決定のスコープ外").
