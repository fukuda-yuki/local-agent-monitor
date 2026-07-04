# Sprint15 M5: Canvas raw preview (child D)

Implements child D per D038 (implementation authorized, concrete design
resolved — this is no longer just a design template). Read D038 in
`docs/decisions.md` in full before starting; this plan operationalizes it.
Implement this AFTER M4 lands (both touch the same two Canvas files —
sequential, not parallel, to avoid a merge conflict).

Target files:

- `.github/extensions/otel-monitor-canvas/extension.mjs` (new page-navigation route)
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs` (new pure extraction helper + `renderHelperHtml` link/template additions)
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs` (additive tests)
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs` (additive assertions)
- New `docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M5-raw-preview/review.md`

Do NOT touch `src/CopilotAgentObservability.LocalMonitor/**` — no new Local
Monitor endpoint is added or changed. This milestone only adds a new route on
the Canvas extension's OWN loopback server and consumes Local Monitor's
EXISTING `GET /traces/{rawRecordId}/raw` route unchanged.

## Why this is safe (read before implementing — do not deviate from this)

`src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`'s
`GET /traces/{rawRecordId}/raw` handler returns a **fixed-format** HTML page:

```
<!DOCTYPE html><html><head><meta charset="utf-8"><title>Raw record {id}</title></head><body><pre>{encodedPayload}</pre></body></html>
```

where `encodedPayload = HtmlEncoder.Default.Encode(record.PayloadJson)`. Because
the payload is already HTML-encoded, the only literal `<` and `>` characters
in the whole response belong to the fixed template tags — the payload itself
cannot contain an unescaped `<pre>` or `</pre>`. This means a simple
"substring between the first `<pre>` and the last `</pre>`" extraction is
unambiguous and safe, and the extracted string can be **re-embedded verbatim
into a new `<pre>` element without any further encoding or decoding** — it is
already exactly the encoded text that belongs inside a `<pre>`. Do not decode
it (e.g. do not run it through an HTML-entity-decode function) and do not
re-run it through `escapeHtml()` — either would corrupt it (double-decode
would un-escape it back to unsafe raw HTML; double-encode would show garbled
`&amp;lt;` style text to the user).

The extension's Node process fetching that route via `fetch()` sends no
`Origin`/`Sec-Fetch-Site` headers (those are browser-set, not something a
plain server-side `fetch()` call adds), so `MonitorHost.IsCrossSiteRequest`
does not block it. This is intentional and within the already-accepted risk
in `docs/specifications/security-data-boundaries.md` ("another process
running as the same local user reading raw via loopback" — accepted, not a
new risk). Do not add any workaround or bypass code for this; it's simply how
a same-machine server-to-server fetch already behaves against that route.

## New route: `GET /raw-preview/:traceId/:spanId`

This is a **page-navigation route**, not a JSON API — it must be reached only
via a plain `<a href="...">` link click (real browser navigation), never via
client-side `fetch()` + `innerHTML`. This is the core boundary requirement:
client-side JS in the helper page must never receive raw content as JSON.

Add to `createHelperServer`'s request handler in `extension.mjs`, after the
existing routes. Path shape: `/raw-preview/{traceId}/{spanId}` (both path
segments, URL-encoded by the link's `href`; token still passed as `?t=...`
since it's a normal `<a>` navigation, same as the main helper page URL
`http://127.0.0.1:{port}/?t={token}` already does).

1. Parse `traceId`/`spanId` out of the path (`decodeURIComponent` each
   segment). Validate both against the existing `matchesTraceId()` (which
   uses `TRACE_ID_PATTERN` — reused for span ids too, same as the existing
   `/analyze` route already does for its optional `spanId`). Invalid →
   respond with a small HTML error page (`400`), not a JSON error (this route
   is HTML-only end to end, unlike the JSON API routes).
2. Validate `isLoopbackUrl(monitorUrl)`, same as every other route.
3. Fetch the trace's spans via the existing `fetchHelperSpans(monitorUrl,
   traceId)` helper (already added in M3 — reuse it, do not duplicate).
4. Find the span whose `span_id` matches the requested `spanId`. If not
   found → `404` HTML error page ("指定した span が見つかりません").
   The span row (from `/api/monitor/traces/{traceId}/spans`) already
   includes `raw_record_id` — use it directly.
5. Fetch `GET {monitorUrl}/traces/{rawRecordId}/raw` via
   `fetchTextWithTimeout`. Two failure cases to distinguish:
   - Non-2xx with a body that looks like the Local Monitor's own `404`
     JSON error (`raw_record_not_found`) → `404` HTML error page.
   - Non-2xx in a way that suggests `--sanitized-only` (the route doesn't
     exist at all under that mode, per `docs/decisions.md` D023) → a
     specific HTML message: "raw は利用できません（Local Monitor が
     --sanitized-only で起動しています）。" Do not guess silently; if you
     cannot reliably distinguish "route absent because sanitized-only" from
     "route absent for another reason" by status code alone, use one
     generic-but-honest message that covers both ("raw を取得できませんでした
     （Local Monitor 側で raw が無効になっているか、一時的に利用できません）")
     rather than fabricating a false-confidence diagnosis.
6. On success, extract the fragment: add a pure helper to `canvas-helpers.mjs`,
   e.g. `extractRawPreviewFragment(html)`, implementing exactly the "substring
   between the first `<pre>` and the last `</pre>`" rule described above.
   Return `null` if the pattern isn't found (defensive — if the Local
   Monitor's raw route's HTML shape ever changes, fail loudly with a clear
   error page rather than embedding something unexpected).
7. Render a small, fixed HTML page (own function in `canvas-helpers.mjs`,
   e.g. `renderRawPreviewHtml({ traceId, spanId, fragment, monitorUrl, token })`):
   - `Cache-Control: no-store` response header (set in `extension.mjs`'s
     route handler, matching how Local Monitor's own raw-bearing routes set
     it).
   - A heading identifying the trace/span (escape `traceId`/`spanId` with
     `escapeHtml` — those come from the URL, not from the raw payload).
   - The extracted `fragment` embedded **verbatim** inside a `<pre>` (no
     `escapeHtml()` call on it — see "Why this is safe" above).
   - A "← ヘルパーページに戻る" link back to `/?t={token}`.
   - No other dynamic content on this page besides `traceId`, `spanId`, and
     the verbatim fragment.

## Helper page link (canvas-helpers.mjs `renderHelperHtml`)

- Reuse the EXISTING optional span-id `<input type="text" id="span"
  placeholder="任意の span id" />` already in the analyze card (do not add a
  second span-id input).
- Add a link/button "生データを表示（新しいタブ）" near that input, enabled
  only when both a trace is selected AND the span input is non-empty (client-
  side JS: toggle a `disabled`/hidden state on `input`/`change` of both
  fields — mirror how the existing `analyze` button already reacts to trace
  selection). The link's `href` is built client-side from the current
  `traceSel.value`, `spanInput.value`, and the page's own `token`:
  `/raw-preview/` + encodeURIComponent(traceId) + `/` + encodeURIComponent(spanId) + `?t=` + token`,
  and it must open in a new tab (`target="_blank" rel="noopener noreferrer"`,
  same attributes already used for the other external-ish links on this
  page) so the user doesn't lose the helper page's state.
- No other change to the existing analyze flow.

## Tests

`canvas-helpers.test.mjs` (extend, do not remove/modify existing tests):

- `extractRawPreviewFragment(html)`: given a sample HTML string matching the
  exact Local Monitor template (`<pre>{"some":"json"}</pre>` — encode a
  sample JSON string the same way `HtmlEncoder.Default.Encode` would, e.g.
  encode `<`/`>`/`&`/`"` manually in the test fixture to mirror real output)
  returns the exact inner fragment. Given HTML without a `<pre>...</pre>`
  pair, returns `null`.
- `renderRawPreviewHtml(...)`: output contains the verbatim fragment
  unmodified (assert the exact encoded substring appears byte-for-byte in
  the output — this catches any accidental re-encoding/decoding regression),
  contains the escaped `traceId`/`spanId`, and contains no `/raw` JSON
  reference (this is a full HTML page, not a JSON response, so the usual
  "no `/raw` substring" contract-test style assertion doesn't directly
  apply here — instead assert this function's output does NOT include a
  `<script>` tag that fetches JSON raw content, i.e. no client-side raw
  fetch is introduced).
- `renderHelperHtml(...)`: output contains the new link's Japanese text
  ("生データを表示" or whatever label is chosen).

`CanvasExtensionContractTests.cs` (extend, following M3/M4's pattern):

- Assert `/raw-preview/` route string is present.
- Assert `extractRawPreviewFragment` is present.
- Assert the link's Japanese text is present.
- Add an assertion that this route is reached via `target="_blank"` navigation
  (i.e. assert the helper page's script does NOT `fetch()` the
  `/raw-preview/` path — grep for the absence of
  `fetch("/raw-preview` or similar in the script, to pin "page navigation
  only, not JSON fetch" as a contract).
- Keep all existing M1/M3/M4 assertions unchanged.

## Validation

```powershell
node --check .github/extensions/otel-monitor-canvas/extension.mjs
node --check .github/extensions/otel-monitor-canvas/canvas-helpers.mjs
node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs
dotnet build CopilotAgentObservability.slnx
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```

Record a self-review in `review.md` next to this file per
`docs/agent-guides/review-workflow.md`. This is the most security-sensitive
milestone in Sprint15 — the self-review MUST explicitly re-verify: (1) the
extracted fragment is never decoded or re-encoded, (2) the route is only
reachable by page navigation (no client-side JSON fetch of raw content),
(3) `Cache-Control: no-store` is set, (4) no raw content reaches any Canvas
**action** response, log, or committed file. If any of these four cannot be
confirmed, stop and report rather than shipping it. Live Canvas runtime
verification (does the link actually work end-to-end inside a real Copilot
app session) is explicitly OUT of this milestone's scope — covered once, for
all of A–D together, by the Sprint15 README's "Live validation handoff"
(D038).
