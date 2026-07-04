# Sprint15 M5 (child D): self-review

Per `docs/agent-guides/review-workflow.md`. This is the most security-sensitive
milestone in Sprint15 (D038): a new page-navigation route,
`GET /raw-preview/:traceId/:spanId`, on the Canvas extension's own loopback
server, which fetches Local Monitor's existing `GET /traces/{rawRecordId}/raw`
server-to-server and re-embeds the already-HTML-encoded fragment verbatim.

## Scope reviewed

- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`: added
  `extractRawPreviewFragment(html)` (pure — returns the substring between the
  first `<pre>` and the last `</pre>`, or `null` if the shape isn't found),
  `renderRawPreviewHtml({ traceId, spanId, fragment, token })` (the success
  page: escapes `traceId`/`spanId`/`token`, embeds `fragment` **verbatim with
  no `escapeHtml()` call**, and a back-link to `/?t={token}`), and
  `renderRawPreviewMessageHtml({ heading, message, token })` (a small fixed
  error/status page for the 400/404/502 branches, all content escaped since
  none of it is raw). Added the "生データを表示（新しいタブ）" link to
  `renderHelperHtml`, reusing the existing `#span` input (no second input
  added), toggled visible/hidden via a small `updateRawPreviewLink()` wired
  to the trace `<select>`'s `change` and the span `<input>`'s `input` events,
  building its `href` client-side and opening via
  `target="_blank" rel="noopener noreferrer"`.
- `.github/extensions/otel-monitor-canvas/extension.mjs`: added
  `GET /raw-preview/:traceId/:spanId` inside `createHelperServer`'s handler,
  after the existing routes, gated by the same top-of-handler
  `x-canvas-token` check that already guards every route on this server (the
  new route adds no separate auth path). The handler: (1) parses and
  validates both path segments with the existing `matchesTraceId()` (reused
  for span ids, like `/analyze` already does) → `400` HTML on failure; (2)
  validates `isLoopbackUrl(monitorUrl)`; (3) reuses the existing
  `fetchHelperSpans(monitorUrl, traceId)` (from M3 — not duplicated) to find
  the span and its `raw_record_id` → `404` HTML if not found; (4) fetches
  `GET {monitorUrl}/traces/{rawRecordId}/raw` via the existing
  `fetchTextWithTimeout`, distinguishing a body containing
  `raw_record_not_found` (→ a specific 404 HTML page) from any other non-OK
  response or thrown `CanvasError` (→ one generic, honest message covering
  both "`--sanitized-only`" and "temporarily unavailable", per the plan's
  explicit instruction not to fabricate a false-confidence diagnosis); (5)
  extracts the fragment via `extractRawPreviewFragment` → `502` HTML if
  `null` (fail loudly rather than embed something unexpected); (6) sets
  `Cache-Control: no-store` (set unconditionally at the top of the route,
  before any branch, so every response from this route — success and error
  alike — carries it) and returns `renderRawPreviewHtml(...)`.
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`: added 4
  tests — `extractRawPreviewFragment` on a manually-HTML-encoded fixture
  (exact fragment) and on HTML without a `<pre>...</pre>` pair (`null` for
  missing, empty, `null`, and `undefined` input); `renderRawPreviewHtml`
  asserting the fragment appears byte-for-byte (`html.includes(...)`, not a
  regex, to catch any accidental re-encoding/decoding), escaped
  `traceId`/`spanId`, and no `<script>`/`fetch(` in the output;
  `renderHelperHtml` asserting the new link's Japanese text and that the
  script does not `fetch("/raw-preview`. Additive only — all 13 pre-existing
  tests are unchanged and still present (17 total).
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`:
  added one new `[Fact]`, `Extension_DeclaresRawPreviewSurface`, pinning the
  new route, the two new pure-helper names, the Japanese link text,
  `Cache-Control`/`no-store`, and the absence of a client-side
  `fetch("/raw-preview` / `fetch('/raw-preview`. See "Important scoping
  change" below for how this interacts with the pre-existing `/raw`-absence
  checks.

No file outside the owned write scope was modified. In particular,
`src/CopilotAgentObservability.LocalMonitor/**` was not touched — no new
Local Monitor endpoint was added; this milestone only adds a route on the
Canvas extension's own server and consumes the existing
`GET /traces/{rawRecordId}/raw` unchanged.

## Important scoping change to pre-existing contract-test assertions

Discovered during planning (before writing any M5 code): three pre-existing
`CanvasExtensionContractTests.cs` facts —
`Extension_ActionsFetchOnlyBoundedMonitorEndpoints`,
`Extension_DeclaresM5UiTriggerSurface` (Sprint11-era, misnamed relative to
this sprint's M5), and `Extension_DeclaresTraceDetailSummaryCardSurface` —
each asserted `Assert.DoesNotContain("/raw", script)` over the **entire**
combined `extension.mjs` + `canvas-helpers.mjs` source. `Extension_
DeclaresDashboardSummaryCardSurface` (added in this sprint's M4) has the same
assertion. Implementing M5 as designed necessarily introduces the substring
`/raw` twice — the route path `/raw-preview/` and the fetch target
`` `/traces/${span.raw_record_id}/raw` `` — so all four facts would fail
against the new file content if left untouched.

This is not a boundary weakening: D038 explicitly authorizes exactly this one
new raw-fetching code path while the 5 Canvas actions, the M3 trace-detail
route, and the M4 summary proxy must all remain raw-free. Rather than
deleting the checks, they were **scoped precisely** via a new shared helper,
`AssertNoRawReferenceOtherThanAuthorizedPreview(script)`, which strips the two
D038-authorized substrings (`/raw-preview` and the fetch-target template
literal, matched by its distinctive `/raw\`` — backtick-terminated — suffix)
from the script text and then asserts no `/raw` remains. All four
pre-existing facts now call this helper instead of the bare
`Assert.DoesNotContain("/raw", script)`. The new
`Extension_DeclaresRawPreviewSurface` fact independently pins that both
stripped substrings are actually present (`/raw-preview/` and the two
extractor/renderer function names), so stripping them in the other four facts
does not hide their absence — it only relaxes the specific, already-approved
exception.

One iteration was needed to get the stripping right: an initial regex-based
attempt (`/raw(?!-preview)`) only excluded the route-path occurrence and
missed the fetch-target occurrence, so it initially failed on 4 facts; a
second pass also missed that my own explanatory comment above
`renderRawPreviewMessageHtml` ("span/raw-record not found") accidentally
contained the substring `/raw` too, requiring a wording fix (comment only,
no functional change). All contract tests pass after both fixes — see
Validation below.

## Four required security self-checks (per the M5 plan)

1. **The extracted fragment is never decoded or re-encoded.**
   Confirmed: `extractRawPreviewFragment` performs a plain `String.slice`
   between literal `<pre>`/`</pre>` markers with no entity-decoding call
   anywhere in its body. `renderRawPreviewHtml` interpolates `${fragment}`
   directly into the template literal with no `escapeHtml()` call and no
   other transform. The new test
   `renderRawPreviewHtml: embeds the fragment verbatim...` asserts
   `html.includes(encodedFragment)` — an exact, byte-for-byte substring
   check, not a regex or normalized comparison, so any accidental
   double-encode/decode would fail it.
2. **The route is reachable only by page navigation; no client-side JSON
   fetch of raw content.** Confirmed: the `/raw-preview/:traceId/:spanId`
   handler in `extension.mjs` always responds with
   `Content-Type: text/html` and `res.end(<html string>)`, never
   `sendJson`. The helper page's client-side script only ever assigns
   `rawPreviewLink.href = "/raw-preview/" + ...` (a plain `<a>` element) and
   never calls `fetch()` on that path. Pinned by the JS test
   `does not client-side fetch() /raw-preview` and by the contract-test fact
   asserting the absence of `fetch("/raw-preview`/`fetch('/raw-preview` in
   the combined script.
3. **`Cache-Control: no-store` is set.** Confirmed: `res.setHeader
   ("Cache-Control", "no-store")` runs unconditionally as the first two
   lines of the route handler, before the segment-count check, so it is
   present on every response this route can produce (400/404/502/200
   alike), not only the success path.
4. **No raw content reaches any Canvas action response, log, or committed
   file.** Confirmed: the 5 `invoke_canvas_action` handlers
   (`handleMonitorHealth` … `handleGetCacheSummary`) and their `sanitizeDto`
   usage are byte-for-byte unchanged — the new route lives entirely inside
   `createHelperServer`'s plain HTTP handler, a separate code path from the
   action registration. No `console.log` (or any other log call) was added;
   grep of both changed files confirms no new logging call exists. No raw
   content was written to any test fixture, doc, or committed file — the
   fragment used in tests is a synthetic, manually-HTML-encoded sample
   string, not real telemetry.

If any of these four had failed to hold, the plan requires stopping rather
than shipping; all four held, so the milestone proceeds.

## Files checked

- Read `docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M5-raw-preview/plan.md`
  and the D038 (child D) section of `docs/decisions.md` in full before
  implementing, including the "Why this is safe" section explaining the
  `<pre>`/`</pre>` extraction rationale.
- Read `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`'s
  `GET /traces/{rawRecordId}/raw` handler (confirmed the exact fixed HTML
  shape: `<!DOCTYPE html>...<pre>{HtmlEncoder.Default.Encode(payload)}</pre>
  ...`) and its `WriteFailureAsync` helper (confirmed the 404 JSON body
  literally contains `"error":"raw_record_not_found"`, making the substring
  match in step (4) of the route handler reliable).
- Read the full pre-change `extension.mjs` and `canvas-helpers.mjs` to reuse
  `matchesTraceId`, `fetchHelperSpans`, `fetchTextWithTimeout`,
  `monitorApiUrl`, `isLoopbackUrl`, and `escapeHtml` rather than
  reimplementing any of them.
- Confirmed the 5 Canvas actions and all M1/M3/M4 routes/handlers are
  byte-for-byte unchanged; the new route is an HTTP route on the helper
  page's own server, not a new `invoke_canvas_action` action, and no new
  Local Monitor endpoint was added.

## Validation commands and results

```
node --check .github/extensions/otel-monitor-canvas/extension.mjs
node --check .github/extensions/otel-monitor-canvas/canvas-helpers.mjs
```
Both: exit 0, no output (pass).

```
node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs
```
`tests 17, pass 17, fail 0` (13 pre-existing + 4 new).

```
dotnet build CopilotAgentObservability.slnx
```
`ビルドに成功しました。 0 個の警告 0 エラー`.

```
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```
`失敗: 0、合格: 12、スキップ: 0、合計: 12` (11 pre-existing + 1 new).

All required validation commands ran and passed; no pre-existing test was
removed, and every pre-existing assertion's *intent* is preserved (see
"Important scoping change" above for the one assertion that was
re-implemented, not weakened, to accommodate D038's single authorized
exception).

## Findings

No blocking issues found beyond the scoping-assertion fix already described
above (which was anticipated during planning, not discovered as a surprise
during implementation).

## Residual risks / out of scope

- Live Canvas runtime verification of the raw-preview link (does clicking it
  inside a real Copilot Canvas session actually open a correct new tab with
  the right content) is explicitly out of scope for this milestone — it is
  covered once, for all of Sprint15's children (A–D) together, by the
  README's "Live validation handoff" (D038). `node --test` and the .NET
  contract tests validate the pure helper output and route logic; they do
  not execute the inline client-side `<script>` or a real browser
  navigation.
- The distinguishing check between "`--sanitized-only` disabled the raw
  route" and "the raw route is failing for another reason" is, per the
  plan's own instruction, intentionally a single honest generic message
  rather than a confident diagnosis, since the two cases cannot be reliably
  told apart by status code alone from outside the Local Monitor process.
- Accepted risk (per D038 / `security-data-boundaries.md`, not introduced by
  this milestone): the extension's server-to-server `fetch()` of Local
  Monitor's raw route sends no `Origin`/`Sec-Fetch-Site` headers, so it is
  not blocked by `MonitorHost.IsCrossSiteRequest` — this is the pre-existing
  accepted "same local user, different process, loopback" risk category, not
  a new one.
