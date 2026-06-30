# Sprint15 M1: Canvas helper UX (child A)

Implements child A from the Sprint15 README. Display boundary unchanged. Only
the extension-owned helper page presentation and its contract test change. The
five action handlers and the bounded DTO shapes (`compactTrace` / `compactSpan` /
`cacheTurn` / `sanitizeDto`) are not modified.

Target files:

- `.github/extensions/otel-monitor-canvas/extension.mjs`
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs` (new)
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs` (new)
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`

## A0. F8 prerequisite — JS smoke scaffold

- Extract the side-effect-free pure functions out of `extension.mjs` into a new
  `canvas-helpers.mjs` ESM module that does **not** import
  `@github/copilot-sdk/extension` and has **no** top-level `await joinSession`:
  `escapeHtml`, `renderHelperHtml`, `buildAnalysisPrompt`, `compactTrace`,
  `compactSpan`, `cacheTurn`, plus the new presentation formatters (A1).
  `extension.mjs` imports them and keeps the `joinSession` / `createServer`
  wiring, the action handlers, and the fetch/loopback/token logic.
- `CanvasError` is re-exported/shared so `canvas-helpers.mjs` does not need the
  SDK; the pure module either takes plain objects or imports a tiny local error
  shim. Keep the public action behavior identical.
- Add `canvas-helpers.test.mjs` (run with `node --test`):
  - `renderHelperHtml(...)` for a `ready` state contains the Japanese button /
    heading, the four Japanese focus labels with their unchanged enum values,
    and contains neither `/raw` nor any raw field name;
  - `renderHelperHtml(...)` for `unreachable` and `not_ready` states contains the
    state-specific message and the next-action guidance (health URL, etc.);
  - the trace-line formatter renders the expected one-line string for a sample
    `compactTrace` row;
  - `buildAnalysisPrompt(...)` contains the raw/PII boundary constraint lines;
  - `compactTrace` exposes the expected sanitized field set and no raw key.
- Add a node-runner test to `CanvasExtensionContractTests.cs` that shells out to
  `node --check extension.mjs`, `node --check canvas-helpers.mjs`, and
  `node --test canvas-helpers.test.mjs`, asserting exit code 0. If `node` is not
  on PATH the test fails with a clear message (the extension is node-based, so
  node is expected).
- Update `ReadExtension()` to read and concatenate `extension.mjs` +
  `canvas-helpers.mjs` so existing substring assertions remain valid after the
  split.

## A1. Decision-supporting trace line

Replace the dropdown option text (currently
`t.trace_id + " — " + status + " — spans:N"`) with a formatted one-liner driven
by new pure formatters in `canvas-helpers.mjs`:

- `statusLabel(status)` → `OK` / `エラーあり`
- `formatTokens(n)` → grouped digits (e.g. `8,420`)
- `formatDuration(ms)` → `18.2s` / `1:23` style
- `formatClock(lastSeenAt)` → `HH:MM`
- `shortTraceId(traceId)` → `#` + first 8 chars + `…`
- `formatTraceLine(row)` → e.g.
  `エラーあり / gpt-5 / 12 spans / 3 tools / 8,420 tokens / 14:32 / 18.2s / #abc123`

All inputs are existing `compactTrace` fields (`status`, `primary_model`,
`span_count`, `tool_call_count`, `total_tokens`, `duration_ms`, `last_seen_at`,
`trace_id`). No new monitor endpoint. The C# `MonitorViewFormat` is not reachable
from JS, so small presentation helpers live in JS; this is presentation, not a
monitor-UI re-implementation.

## A2. Japanese focus labels (enum values unchanged)

Option display text becomes 遅い原因 / トークン消費 / キャッシュ効率 / エラー原因.
`<option value>` stays `latency` / `tokens` / `cache` / `errors`; `FOCUS_VALUES`
and `buildAnalysisPrompt`'s `focusActions` keys are unchanged.

## A3. Japanese button / heading

Button → `Copilotでこのトレースを分析`; the analyze card heading is updated to
match. The English Copilot-facing instruction text inside `buildAnalysisPrompt`
is kept (it is a functional instruction, not UI chrome).

## A4. Concrete health / error guidance

Thread a structured health state (`ready` / `not_ready` / `unreachable`) from
`open()` into `renderHelperHtml` instead of a free-form status string. State
banners:

- unreachable → `Local Monitor が起動していません。`
- not_ready (HTTP 503) → `Local Monitor は起動していますが ready ではありません。`

Each error banner lists the next actions: the `…/health/ready` URL (derived from
`monitorUrl`), a start command, DB path / port / URL configuration checks, and
the monitor base URL the extension references (already shown in the Connection
card).

## A5. Collapse the health response

Wrap the raw health `<pre>` in a `<details>` collapsed by default; rename the
card heading to `Local Monitor の接続状態`.

## A6. Japanese monitor-page link + posture note

`Local Monitor をブラウザで開く` for the monitor-page link. The posture note is
Japanese, but the boundary-invariant sentence
`Canvas action responses and logs must not contain raw telemetry or PII.` is
kept as an exported constant in `canvas-helpers.mjs` and rendered into the page,
so the contract test can pin the invariant while the UI reads in Japanese.

## A7. Contract-test update

- Update UI-string assertions to the new Japanese text (button / heading).
- Keep boundary-invariant assertions: no `/raw`, no `console.log`, no
  `--sanitized-only`, `normal raw-default Local Monitor` (in the
  `handleMonitorHealth` DTO, unchanged), the A6 boundary constant,
  `session.send({ prompt })`, `x-canvas-token`, `randomUUID`, the focus enum
  values, and the `hierarchy_status` / `cache_hit_rate` bounds.
- Add assertions for the new formatters and the node smoke runner.

## Verification

See the Sprint15 README "Validation" section: `dotnet build`, Playwright
install, full `dotnet test`, plus `node --check` on both `.mjs` files and
`node --test` on the smoke file. Canvas runtime live validation is human-gated
and recorded as pending.
