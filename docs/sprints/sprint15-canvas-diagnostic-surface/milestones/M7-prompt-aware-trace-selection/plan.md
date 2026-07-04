# Sprint15 M7: Prompt-aware trace selection

Implements D039 (design confirmed; read it in full in `docs/decisions.md`
before starting, along with the "Sprint15 continuation" section of
`docs/specifications/security-data-boundaries.md`). Adds a new Local
Monitor JSON route, following the exact route-boundary pattern D035 already
established for `/traces/{traceId}/analysis/...`, plus Canvas-side
consumption so the trace dropdown can show the actual prompt instead of only
sanitized metadata.

**Do not start this milestone without an explicit user go-ahead** — D039 is
a design decision, not an implementation authorization (same two-stage
process D037→D038 used for child D).

Target files:

- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` (new route)
- `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorRawViewTests.cs` (additive tests, same file that already covers `/traces/{rawRecordId}/raw`)
- `.github/extensions/otel-monitor-canvas/extension.mjs` (new fetch helper, `/api/traces` route change)
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs` (new pure formatter)
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs` (additive tests)
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs` (additive assertions)
- New `docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M7-prompt-aware-trace-selection/review.md`

## Route: `GET /traces/{traceId}/prompt-label` (Local Monitor)

Read `MonitorHost.cs`'s existing `if (!options.SanitizedOnly) { ... }` block
(contains `/traces/{traceId}/analysis/...` from D035 and
`/traces/{rawRecordId:long}/raw` from D020/D023) in full first — this new
route goes inside that same block, following the exact same shape as its
neighbors.

1. Register inside the existing `if (!options.SanitizedOnly)` block (route
   absent → `404` under `--sanitized-only`, matching every other raw-bearing
   route — no separate flag check needed inside the handler).
2. `IsCrossSiteRequest(context)` check first → `403`
   `{"accepted":false,"error":"cross_origin_forbidden","message":"..."}`,
   matching the exact `WriteFailureAsync`/`WriteNoStoreFailureAsync` shape
   used by every other raw-bearing route (e.g. the message used by
   `Traces.cshtml.cs`'s own `CrossOriginForbidden()`: "same-origin only").
3. Set `Cache-Control: no-store` unconditionally (same as
   `/traces/{traceId}/analysis/runs/{runId}`).
4. Look up raw records: `projectionStore.ListRawRecordsByTraceId(traceId, 1)`
   (the exact call `Traces.cshtml.cs`'s `PopulatePrompts` already makes — do
   not write new store-access logic). Wrap in the existing
   `try { } catch (PersistenceBusyException) { ... 503 persistence_busy ... }`
   pattern used by every other route that touches the store.
5. If records exist, call
   `MonitorPromptExtractor.ExtractPromptLabel(records[0].PayloadJson, traceId)`
   — the exact same call `Traces.cshtml.cs`/`Index.cshtml.cs` already make.
   `MonitorPromptExtractor` is `internal static` in the same
   `CopilotAgentObservability.LocalMonitor` namespace/assembly as
   `MonitorHost.cs`, so no visibility change is needed.
6. Response: `200` with `{ "trace_id": traceId, "prompt_label": label }`
   where `label` may be `null` (no raw record, or extraction returned
   `null`) — this is a normal, non-error outcome, not a `404`. No
   `traceId`-format validation is needed beyond what the store lookup
   already tolerates (an unknown/malformed id simply yields zero records →
   `prompt_label: null`), matching how `/traces/{traceId}/analysis/...`
   handles unknown trace ids.

Do not add this field to `/api/monitor/*` or the SSE stream (D032's
guarantee for that specific family is unchanged — this is a sibling route,
not a modification to any existing sanitized route).

## Canvas extension.mjs changes

- Add `fetchHelperPromptLabel(monitorUrl, traceId)`, mirroring
  `fetchHelperSpans`'s shape: `fetchTextWithTimeout` +
  `monitorApiUrl(monitorUrl, /traces/${encodeURIComponent(traceId)}/prompt-label)`
  + `parseJsonBody`. On a non-OK response (e.g. `404` under
  `--sanitized-only`), do not throw — return `null` (this is an expected,
  gracefully-degrading case, not a hard failure of the whole `/api/traces`
  response).
- In the existing `GET /api/traces` route handler (inside
  `createHelperServer`), after building `items` via
  `fetchHelperTraceRows`/`compactTrace`/`formatTraceLine` as today, fetch a
  prompt label for every item **in parallel** via
  `Promise.all(items.map((item) => fetchHelperPromptLabel(monitorUrl, item.trace_id)))`
  and merge each result in as `prompt_label` on the corresponding item. Keep
  the existing `line` field unchanged — do not remove or alter it.
- No change to any of the 5 `invoke_canvas_action` handlers. No change to
  `/api/trace-detail/:traceId` or `/api/summary` (out of scope for this
  milestone — D039 only covers the dropdown).

## canvas-helpers.mjs changes

Add a pure formatter, e.g. `dropdownOptionLabel(item)`:

```js
export function dropdownOptionLabel(item) {
    const line = item.line ?? "";
    return item.prompt_label ? `${item.prompt_label} — ${line}` : line;
}
```

Update the `<script>` block in `renderHelperHtml` where
`opt.textContent = t.line || t.trace_id;` currently sets the option text —
this logic itself lives in the inline (non-module) client-side script, so
(matching the established precedent from M3/M4's reviews: inline script
cannot `import` from the ES module) either duplicate the same two-line
composition inline, or — preferred, since it is genuinely tiny — keep it
inline as `opt.textContent = (t.prompt_label ? t.prompt_label + " — " : "") + (t.line || t.trace_id);`
and skip adding `dropdownOptionLabel` as a module export if it would never
be reachable from the browser-side script anyway. Decide based on whether
`dropdownOptionLabel` would actually be called from `extension.mjs` (e.g. if
`/api/traces`' server-side response construction also wants a
precomputed combined label field) — if so, keep it as a shared, exported,
tested pure function; if it is only ever needed in the inline client script,
document why a module-level test still adds value (contract-pinning the
exact separator format) even though the inline script duplicates the same
one-liner logic, consistent with the "server pre-formats, client stays
minimal" pattern M3's review already flagged as a deliberate, accepted
deviation.

## Tests

`MonitorRawViewTests.cs` (extend, do not remove/modify existing tests) —
follow the exact `MonitorTempDirectory`/`StartHostAsync(temp, sanitizedOnly:
...)`/`host.Client` pattern already used by `RawDetail_*` tests in this file:

- `PromptLabel_AbsentUnderSanitizedOnly_Returns404`.
- `PromptLabel_ByDefault_ReturnsExtractedLabelWithNoStore` (seed a raw record
  with a `gen_ai.prompt` attribute, assert `200`, `no-store`, and the exact
  expected label in the JSON body).
- `PromptLabel_CrossSiteFetchIsForbidden` (same `Sec-Fetch-Site: cross-site`
  header technique as `RawDetail_CrossSiteFetchIsForbidden`).
- `PromptLabel_UnknownTraceId_ReturnsNullLabelNot404` (confirms the
  "no prompt found" case is `200`/`null`, not an error).

`canvas-helpers.test.mjs` (extend, do not remove/modify existing tests):

- Unit test for whichever formatter is added per the "canvas-helpers.mjs
  changes" section above, covering: prompt present, prompt absent (falls
  back to `line`), both present but `line` empty.

`CanvasExtensionContractTests.cs` (extend, following the M3/M4/M5 pattern):

- Assert `/prompt-label` and `fetchHelperPromptLabel` are present.
- Assert the `/api/traces` route still fetches only the previously-listed
  bounded endpoints **plus** this new one — update
  `Extension_ActionsFetchOnlyBoundedMonitorEndpoints` only if it currently
  enumerates an exhaustive allowlist of fetched paths (re-check before
  editing; do not weaken it beyond adding this one new expected path).
  `AssertNoRawReferenceOtherThanAuthorizedPreview` (added in M5) already
  scopes the blanket "no `/raw`" check correctly and does not need touching
  for this change, since `/prompt-label` contains no `/raw` substring.
- Keep all existing M1–M6 assertions unchanged.

## Validation

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorRawViewTests
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
node --check .github/extensions/otel-monitor-canvas/extension.mjs
node --check .github/extensions/otel-monitor-canvas/canvas-helpers.mjs
node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs
dotnet test CopilotAgentObservability.slnx
```

Record a self-review in `review.md` next to this file per
`docs/agent-guides/review-workflow.md`, explicitly re-verifying: (1) no
`prompt_label` (or any raw-derived field) reaches any Canvas **action**
response, `session.send()` prompt, log, or committed file; (2) the new route
is absent (`404`) under `--sanitized-only`; (3) the new route enforces
same-origin like every other raw-bearing route; (4) `/api/monitor/*` and SSE
are unchanged. Live Canvas runtime rendering of the updated dropdown is out
of this milestone's scope — folded into the same consolidated Live
validation handoff (D038) as every other Sprint15 child, so update
`milestones/M6-live-validation-handoff/prompt.md` to also mention checking
the prompt-aware dropdown text if this milestone lands before that handoff
is used.
