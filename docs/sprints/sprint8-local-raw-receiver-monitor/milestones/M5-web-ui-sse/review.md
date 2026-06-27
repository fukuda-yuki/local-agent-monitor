# Sprint8 M5 Web UI And SSE — Review

Status: **Implemented** (validated). Review recorded by the orchestrator (Claude)
after self-verifying every delegated change on disk and via build/test. Codex did
the production edits via `--write`; the test contracts were authored by the
orchestrator.

## Scope Reviewed

M5 adds the Local Ingestion Monitor browser UI and a notification-only SSE stream
without changing the M4 sanitized projection, raw-view, or readiness contracts:

- Razor Pages for `/`, `/ingestions`, `/traces`, `/diagnostics`, served by the
  existing `MonitorHost` (one ASP.NET Core process).
- A notification-only SSE endpoint `GET /events` (`text/event-stream`).
- A small browser gap-recovery client (`wwwroot/monitor.js`) and stylesheet
  (`wwwroot/monitor.css`).
- UI/SSE proven sanitized: default pages, the cursor APIs, and SSE never carry raw
  prompt / response / tool content or PII; raw is linked only when
  `--enable-raw-view` is set and is still served solely by
  `GET /traces/{rawRecordId}/raw`.

## Changed Files And Behavior

Production (`src/CopilotAgentObservability.LocalMonitor/`):

- `MonitorHost.cs` — register Razor Pages; register `MonitorOptions`,
  `MonitorHealthState`, and `IMonitorProjectionStore` as singletons for the pages;
  `UseStaticFiles` + `MapRazorPages`; create a `MonitorEventBroker`, inject it into
  `ProjectionWorker`, and map `GET /events`. Pin `ApplicationName` to this assembly
  and call `UseStaticWebAssets()` explicitly (see Deviations).
- `Pages/_ViewImports.cshtml`, `Pages/_ViewStart.cshtml`,
  `Pages/Shared/_Layout.cshtml` — shared layout (title/header "Local Ingestion
  Monitor", nav, `/monitor.css`, `/monitor.js`).
- `Pages/Index.cshtml(.cs)` — overview: readiness status + 10 most-recent sanitized
  ingestions.
- `Pages/Ingestions.cshtml(.cs)` — sanitized ingestion list with `after` cursor;
  a `raw` link per row only when `--enable-raw-view`.
- `Pages/Traces.cshtml(.cs)` — sanitized trace list with `after` cursor.
- `Pages/Diagnostics.cshtml(.cs)` — readiness checks and the `health/ready` /
  `health/live` endpoints.
- `Events/MonitorEvent.cs`, `Events/MonitorEventBroker.cs` — in-memory fan-out of
  notification-only events to per-subscriber bounded (drop-oldest) channels.
- `Projection/ProjectionWorker.cs` — publish exactly one `projection` notification
  per pass when at least one record was newly projected.
- `wwwroot/monitor.css`, `wwwroot/monitor.js` — stylesheet and gap-recovery client.

Tests (`tests/CopilotAgentObservability.LocalMonitor.Tests/`):

- `MonitorUiTests.cs` (new) — route success + `text/html`; overview/diagnostics
  render readiness without raw/PII; list pages sanitized with raw-link gating;
  traces sanitized; negative `after` ⇒ `400`; pages reference `/monitor.js` and the
  script uses the cursor APIs + `EventSource('/events')`; `/monitor.css` served.
- `MonitorSseTests.cs` (new) — `/events` is `text/event-stream`; a notification
  arrives after projection and carries `data: {}` only (no raw/PII/trace id);
  `POST /events` is not accepted (`405`/`404`).
- `ProjectionWorkerTests.cs` (+2) — one notification when records are newly
  projected; none when nothing new was projected.

## Source-Of-Truth Comparison

- `docs/spec.md` / `telemetry-ingestion.md`: SSE is notification-only and not the
  source of truth; reconnect/gap recovery uses `/api/monitor/*` cursors. `monitor.js`
  re-reads the cursor APIs on each notification and never inserts raw into the DOM.
- `security-data-boundaries.md` / D020 (DR6): default views/API/SSE sanitized; raw
  only on the opt-in route, same-origin, inert text. Verified by tests that assert
  marker strings (`SECRET_PROMPT_TEXT_MARKER`, `SECRET_TOOL_ARGS_MARKER`,
  `leak-marker@example.com`) never appear in any default surface. No `Html.Raw`,
  no CORS, no new raw JSON API — consistent with the de-scoped display hardening.

## Deviations From The Plan (justified, no contract change)

1. **Page models are `public sealed` with service resolution in `OnGet`** instead
   of `internal` with constructor injection. Razor generates `public` view classes,
   so an `internal` page model (or public members exposing the `internal` domain
   types `MonitorReadiness` / `MonitorIngestionRow` / `MonitorProjectionPage<>` /
   `MonitorOptions`) fails to compile (CS0051/CS0053). The page models therefore
   stay `public`, expose `internal` properties (read by the same-assembly generated
   view), and resolve `MonitorHealthState` / `IMonitorProjectionStore` /
   `MonitorOptions` from `HttpContext.RequestServices`. The domain types remain
   `internal`; no public shared API was added.
2. **SSE subscription registers synchronously** (`Subscribe()` returns a disposable
   subscription with a `ChannelReader`) and the endpoint flushes a `: connected`
   comment before looping. This makes "subscribed before publish" race-free and
   gives a prompt SSE connect, versus the plan's lazy `IAsyncEnumerable`. Event
   payload is still notification-only (`data: {}`).
3. **Static assets are loaded via `UseStaticWebAssets()` with a pinned
   `ApplicationName`.** The monitor runs in the default Production environment, where
   `WebApplication.CreateBuilder` does not auto-load the static web assets manifest;
   without this, `/monitor.css` and `/monitor.js` would 404 in the real `dotnet run`
   host (a latent bug), and Razor pages would not be discovered in the test host.

## Commands Run And Results

- `dotnet build CopilotAgentObservability.slnx -warnaserror`: 0 warnings, 0 errors.
- `dotnet test CopilotAgentObservability.slnx`: 433 passing, 0 failing, 0 skipped
  (300 Config CLI + 133 LocalMonitor; above the M4 baseline of 421).
- Live smoke (`dotnet run`, default Production environment, `http://127.0.0.1:4399`):
  `/`, `/ingestions`, `/traces`, `/diagnostics` ⇒ `200 text/html`;
  `/monitor.js` ⇒ `200 text/javascript`; `/monitor.css` ⇒ `200 text/css`.

## Sanitization Statement

The default UI pages, the `/api/monitor/*` cursor APIs, and the `/events` SSE
stream carry sanitized metadata only. Raw prompt / response / tool content and PII
remain reachable solely through the opt-in `GET /traces/{rawRecordId}/raw` route
(present only with `--enable-raw-view`, same-origin enforced, `no-store`, inert
HTML-encoded text). No raw JSON API was added.

## Residual Risks (M6)

- DR6 negative security matrix (non-loopback bind, Host validation, cross-origin
  raw denial, API/SSE sanitization under raw-view, raw non-logging, inert
  rendering, CSRF/same-origin for any state-changing route).
- Readiness `503` under sustained queue-full / commit failure / projection-lag.
- Restart recovery / no-loss proof on the same SQLite DB.
- Real VS Code Copilot Chat HTTP/protobuf live validation at the monitor.

M5 is implemented and shippable as a UI/SSE surface, but Sprint8 is not complete
until the M6 security matrix and live validation pass.
