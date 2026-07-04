# Sprint15 M2: Canvas dashboard summary (child B)

Implements child B per D037. New sanitized aggregate endpoint
`GET /api/monitor/summary`, backed by a new shared service also used by the
Razor `Index` PageModel. No Canvas-side change in this milestone (the Canvas
adapter consuming this endpoint is a later, separate step — out of scope
here; this milestone only adds the Local Monitor endpoint + service + Index
refactor).

Target files:

- New: `src/CopilotAgentObservability.LocalMonitor/Projection/MonitorSummaryService.cs`
  (or another file under the LocalMonitor project — pick a location consistent
  with existing namespaces; `internal sealed class`, same visibility pattern as
  `IMonitorProjectionStore`).
- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` (new route)
- `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml.cs` (refactor
  to call the new service instead of computing `LatestTrace`/`TopTokenTrace`/
  `ErrorTrace` inline)
- New tests under `tests/CopilotAgentObservability.LocalMonitor.Tests/`
- `docs/specifications/security-data-boundaries.md` already documents the
  resolved field allowlist for this endpoint (D037, search "Child B —
  `GET /api/monitor/summary`") — no further spec edit needed unless the
  implementation must deviate, in which case update the spec first and note
  why.

## Service design

Add `MonitorSummaryService` (constructor takes `IMonitorProjectionStore`).
Public method, e.g.:

```csharp
internal sealed record MonitorSummary(
    int Limit,
    int TraceCount,
    MonitorTraceRow? LatestTrace,
    MonitorTraceRow? TopTokenTrace,
    MonitorTraceRow? ErrorTrace,
    IReadOnlyList<MonitorModelSummary> PerModelSummary,
    IReadOnlyList<MonitorClientKindSummary> PerClientKindSummary);

internal sealed record MonitorModelSummary(string Model, int TraceCount, long TotalTokens, int ErrorCount);
internal sealed record MonitorClientKindSummary(string ClientKind, int TraceCount, long TotalTokens, int ErrorCount);

internal sealed class MonitorSummaryService
{
    public MonitorSummary BuildSummary(int limit) { ... }
}
```

`BuildSummary(limit)`:

1. Fetch `store.ListMonitorTraces(0, limit)` (same call `Index.cshtml.cs`
   already makes for `RecentTraces`, and the same call pattern
   `/api/monitor/traces` uses).
2. Compute, over that in-memory page (no new SQL):
   - `LatestTrace` = first item (rows are newest-first, matching existing
     `Index.cshtml.cs` behavior: `RecentTraces.Count > 0 ? RecentTraces[0] : null`).
   - `TopTokenTrace` = row with max `TotalTokens` among `TotalTokens > 0`
     (reuse the exact LINQ from `Index.cshtml.cs` `OnGet()`).
   - `ErrorTrace` = first row with `ErrorCount > 0` (reuse the exact LINQ).
   - `PerModelSummary`: group by `row.PrimaryModel ?? "unknown"`, project
     `Model`, `TraceCount` (group count), `TotalTokens` (sum of
     `row.TotalTokens ?? 0`), `ErrorCount` (sum of traces in the group with
     `ErrorCount > 0`... actually use `sum(row.ErrorCount ?? 0)` for
     consistency with token summation — pick one and document it in code).
     Order by `TraceCount` descending, then `TotalTokens` descending.
   - `PerClientKindSummary`: same grouping shape keyed by
     `row.ClientKind ?? "unknown"`.
   - `TraceCount` = `page.Items.Count` (the actual fetched count, which may be
     less than `limit` if fewer traces exist).
3. Verify (in a unit test) that `PerModelSummary` and `PerClientKindSummary`
   `TraceCount` values each sum to the overall `TraceCount` — this is the
   D037-mandated reconciliation property.

`Index.cshtml.cs` `OnGet()`: replace the three inline LINQ blocks
(`LatestTrace`/`TopTokenTrace`/`ErrorTrace` computation) with one call to
`MonitorSummaryService.BuildSummary(RecentLimit)` (reuse the existing
`RecentLimit = 10` constant — do not change the dashboard's visible row
count). Keep `RecentTraces` populated exactly as today (the Razor page
still needs the full row list to render its trace table); only the three
highlight properties' computation moves into the shared service. Do not
change `Index.cshtml`'s rendering — the three highlight properties keep
their existing names/types (`MonitorTraceRow?`) so the Razor view is
unaffected. Register `MonitorSummaryService` via DI in `Program.cs` if the
codebase's existing pattern is constructor injection through
`HttpContext.RequestServices` (match how `IMonitorProjectionStore` /
`MonitorOptions` are already registered and resolved — check `Program.cs`
before assuming Scoped/Singleton lifetime; `IMonitorProjectionStore`
wraps a SQLite connection-per-call so the service itself is likely safe as
Singleton, but confirm against the existing registration of
`IMonitorProjectionStore`).

## Route: `GET /api/monitor/summary`

Add immediately after the existing `/api/monitor/traces/{traceId}/spans`
route registration in `MonitorHost.cs`, following the exact same structural
pattern as `/api/monitor/traces` (loopback validation already happens
upstream in the pipeline — confirm by reading how `/api/monitor/traces` is
reached; do not duplicate loopback/Host-header checks if they're applied at
a higher middleware level already covering all `/api/monitor/*` routes).

- Query param: `limit` only (no `after`/cursor). Default 50, range 1–200 —
  reuse the same validation shape as `TryParseCursorQuery`'s limit clamp
  (lines ~653–674 of `MonitorHost.cs`), but write a smaller
  `TryParseLimitQuery(context, out int limit)` helper (or inline parse) since
  there is no cursor for this endpoint. Invalid `limit` → same `400`
  `invalid_query` shape used elsewhere (`WriteFailureAsync`), with a message
  describing the `limit` constraint only (no `after` mention, since this
  route doesn't take one).
- Handler calls `MonitorSummaryService.BuildSummary(limit)` and serializes:

  ```csharp
  await WriteJsonAsync(context, new
  {
      scope = new { limit, trace_count = summary.TraceCount },
      latest_trace = ToTraceDto(summary.LatestTrace),
      top_token_trace = ToTraceDto(summary.TopTokenTrace),
      error_trace = ToTraceDto(summary.ErrorTrace),
      per_model_summary = summary.PerModelSummary.Select(m => new
      {
          model = m.Model, trace_count = m.TraceCount,
          total_tokens = m.TotalTokens, error_count = m.ErrorCount,
      }),
      per_client_kind_summary = summary.PerClientKindSummary.Select(c => new
      {
          client_kind = c.ClientKind, trace_count = c.TraceCount,
          total_tokens = c.TotalTokens, error_count = c.ErrorCount,
      }),
  });
  ```

  `ToTraceDto` should project the same field set already used by
  `/api/monitor/traces`'s `items.Select(row => new { ... })` projection
  (trace_id, client_kind, status-relevant fields, token/duration/model
  fields) — reuse that exact anonymous-object shape (extract it to a small
  private static helper if convenient) so the summary's embedded trace
  objects match `compactTrace` shape used elsewhere. `null` when no such
  trace exists (e.g. no traces at all, or no trace has `ErrorCount > 0`).
- Wrap `PersistenceBusyException` the same way as the other `/api/monitor/*`
  routes (`503` `persistence_busy`).
- No `Cache-Control: no-store`, no same-origin check — this is a sanitized,
  non-raw-bearing endpoint (same posture as `/api/monitor/traces`).

## Tests

Add a new test file (e.g. `MonitorSummaryEndpointTests.cs` or extend an
existing `MonitorHost`-route test file if one already exists — check
`tests/CopilotAgentObservability.LocalMonitor.Tests/` for the existing
pattern used to test `/api/monitor/traces` and mirror it exactly, including
how the test host/server is spun up).

Cover:

- Empty store → `trace_count: 0`, all three highlight fields `null`, both
  summary arrays empty.
- Multiple traces across 2+ models and 2+ client kinds → per-model and
  per-client-kind `trace_count` sums each equal `scope.trace_count`.
- A trace with `ErrorCount > 0` → appears as `error_trace`.
- `limit` query validation: `limit=0`, `limit=201`, `limit=abc` → `400`
  `invalid_query`; omitted `limit` → defaults to 50.
- Response contains no raw/PII field names (same style of assertion as
  existing `/api/monitor/traces` tests, if any exist — check for precedent).
- Unit test for `MonitorSummaryService.BuildSummary` directly (not just
  through HTTP) covering the "unknown" bucket for null `PrimaryModel`/
  `ClientKind`.

## Validation

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj
```

Record a short self-review in `review.md` next to this file (scope, files
touched, validation commands + results, findings, residual risks) per
`docs/agent-guides/review-workflow.md`. Do not touch
`.github/extensions/otel-monitor-canvas/*` or
`tests/.../CanvasExtensionContractTests.cs` — that is M3's (child C) scope.
