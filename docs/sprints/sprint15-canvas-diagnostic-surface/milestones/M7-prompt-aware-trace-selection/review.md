# Sprint15 M7 (D039): self-review

Per `docs/agent-guides/review-workflow.md`'s deeper-review checklist — this
milestone touches a data-safety boundary: a new raw-bearing Local Monitor
JSON route, `GET /traces/{traceId}/prompt-label`, plus Canvas-side
consumption of it.

## Scope reviewed

- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`: added
  `GET /traces/{traceId}/prompt-label` inside the existing
  `if (!options.SanitizedOnly) { ... }` block, immediately after the
  `/traces/{rawRecordId:long}/raw` route. Same-origin check
  (`IsCrossSiteRequest` → `403 cross_origin_forbidden`), `PersistenceBusyException`
  catch (→ `503 persistence_busy`), and `Cache-Control: no-store` set
  unconditionally on every response (success and both failure branches, via
  `WriteNoStoreFailureAsync`). Reuses `MonitorPromptExtractor.ExtractPromptLabel`
  and `IMonitorProjectionStore.ListRawRecordsByTraceId(traceId, 1)` exactly as
  `Traces.cshtml.cs`'s `PopulatePrompts` already calls them — no new
  extraction logic. Response: `{ "trace_id", "prompt_label" }`,
  `prompt_label` legitimately `null` when no record/prompt is found (not an
  error) — confirmed by test, not just by inspection.
- `docs/decisions.md` (D039): corrected one inaccurate sentence — the
  original decision text claimed a malformed trace-id format should return
  `400`, but the D035 precedent it explicitly claims to follow
  (`/traces/{traceId}/analysis/...`) has no format validation anywhere and
  never returns `400` for a malformed id (confirmed by reading the actual
  route handlers). Corrected to state the real, D035-matching behavior:
  no format validation; unknown/malformed id → `200` / `prompt_label: null`.
  User confirmed this resolution before implementation began.
- `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorRawViewTests.cs`:
  added `SeedRawRecordWithPrompt(temp, traceId, promptText)` (a new,
  parameterized local seed helper — the pre-existing `SeedRawRecord` is fixed
  and non-parameterized, so it could not be reused as-is) and 4 new facts:
  `PromptLabel_AbsentUnderSanitizedOnly_Returns404`,
  `PromptLabel_ByDefault_ReturnsExtractedLabelWithNoStore`,
  `PromptLabel_CrossSiteFetchIsForbidden`,
  `PromptLabel_UnknownTraceId_ReturnsNullLabelNot404`. All 7 pre-existing
  facts in this file are unchanged (11 total).
- `.github/extensions/otel-monitor-canvas/extension.mjs`: added
  `fetchHelperPromptLabel(monitorUrl, traceId)` — unlike the pre-existing
  `fetchHelperTraceRows`/`fetchHelperSpans` (which throw `CanvasError` on a
  non-OK response), this helper wraps its entire body in `try/catch` so both
  a non-OK response and any thrown exception (network failure, timeout)
  resolve to `null`, never throw. Updated the `GET /api/traces` route handler
  to fetch a label per trace in parallel via `Promise.all` and merge
  `prompt_label` onto each item, additive only — `line` unchanged. No other
  route or the 5 `invoke_canvas_action` handlers were touched.
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`: added
  `dropdownOptionLabel(item)`, a pure formatter kept purely for
  contract-pinning via `node --test` (not called from `extension.mjs`, since
  `/api/traces` keeps `prompt_label` and `line` as separate additive fields).
  Updated `renderHelperHtml`'s inline client `<script>` (`opt.textContent`)
  to duplicate the same composition logic, since that script is not an ES
  module and cannot `import` from this file (established M3/M4 precedent).
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`: added 3
  tests for `dropdownOptionLabel` (prompt present, prompt absent → falls back
  to `line`, both present but `line` empty). All 18 pre-existing tests
  unchanged (21 total).
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`:
  added `Extension_DeclaresPromptLabelSurface`, pinning `/prompt-label` and
  `fetchHelperPromptLabel` as present. Confirmed before adding that
  `Extension_ActionsFetchOnlyBoundedMonitorEndpoints` is not an exhaustive
  allowlist (only asserts presence of a few specific substrings), so it did
  not need updating, and confirmed `AssertNoRawReferenceOtherThanAuthorizedPreview`
  checks the literal substring `"/raw"`, which `"/prompt-label"` does not
  contain — no changes needed there. All 12 pre-existing facts unchanged
  (13 total).
- `docs/specifications/security-data-boundaries.md`: updated the "Sprint15
  continuation" section's status from "design confirmed, implementation not
  yet started" to "implemented, M7" (header and closing status line).
- `docs/sprints/sprint15-canvas-diagnostic-surface/README.md`: updated the
  M7 milestone-table row and the "## M7" section's closing sentence to
  reflect implementation.

No file outside this scope was modified. In particular, none of the 5
`invoke_canvas_action` handlers (`handleMonitorHealth` … `handleGetCacheSummary`,
lines 535+ in `extension.mjs`), `/api/monitor/*`, or `/events` (SSE) were
touched.

## A gotcha found and fixed during implementation (not anticipated in the plan)

A different instance of the "whole-file 'no X substring' contract-test
assertion legitimately triggered by new code" failure class than the one
already known from M5 (M5's was about `/raw`; this was unrelated). My first
draft of `fetchHelperPromptLabel`'s explanatory comment in `extension.mjs`
used the literal phrase `--sanitized-only` (e.g. "a non-OK response (e.g. 404
under --sanitized-only)"), which tripped two pre-existing facts —
`Extension_DoesNotRequireSanitizedOnlyLaunch` and
`Extension_DeclaresM5UiTriggerSurface` — that assert
`Assert.DoesNotContain("--sanitized-only", script)` (the Canvas extension
itself must never claim to require a particular Local Monitor launch flag,
since it works regardless). Unlike M5's case, this was not a legitimate new
exception to an intentional boundary — it was purely incidental comment
wording, so the fix was to reword the comment (no functional code change) to
avoid the literal substring, not to touch the assertions. Confirmed via a
full re-run of `CanvasExtensionContractTests` afterward (13/13 pass).

## Four required re-verifications (per the M7 handoff)

1. **`prompt_label` never reaches any Canvas action response,
   `session.send()` prompt, log, or committed artifact.** Confirmed by grep:
   `prompt_label` appears in `extension.mjs` only at the 3 lines inside
   `fetchHelperPromptLabel` and the `/api/traces` route handler (lines
   135–197); the 5 `invoke_canvas_action` handlers start at line 535 and
   contain no reference to it. `session.send({ prompt })` (line 319) is built
   exclusively from `buildAnalysisPrompt({ traceId, spanId, focus })`, which
   was not touched and takes no prompt-label input. No `console.log` (or any
   other log call) exists in either changed `.mjs` file — confirmed by grep,
   zero matches. No raw/real prompt text was committed anywhere; test
   fixtures use a synthetic payload (`"What does this function do?"`), not
   real telemetry.
2. **The new Local Monitor route is absent (`404`) under `--sanitized-only`.**
   Confirmed both by code placement (inside the same `if
   (!options.SanitizedOnly)` block as every other raw-bearing route, so
   ASP.NET Core minimal-API routing itself never registers it when
   sanitized-only) and by test:
   `PromptLabel_AbsentUnderSanitizedOnly_Returns404` passes.
3. **The new route enforces the same-origin check like every other
   raw-bearing route.** Confirmed by code (the `IsCrossSiteRequest(context)`
   check is the first statement in the handler, matching every sibling
   route) and by test: `PromptLabel_CrossSiteFetchIsForbidden` passes,
   asserting `403` + `cross_origin_forbidden`.
4. **`/api/monitor/*` and the SSE stream (`/events`) are unchanged and still
   carry no prompt field.** Confirmed: these routes are registered at lines
   135–276 of `MonitorHost.cs`, entirely before the `if
   (!options.SanitizedOnly)` block (which starts at line 293) where this
   milestone's only backend change lives — none of those lines were touched.
   `git diff` on `MonitorHost.cs` shows only the new route appended after the
   `/raw` route; no other line changed.

## Two known failure classes re-checked against the actual diff

- **(i) Whole-file "no X substring" assertion legitimately triggered by new
  code:** re-checked — `AssertNoRawReferenceOtherThanAuthorizedPreview`
  strips `/raw-preview` and `/raw\`` before asserting no `/raw` remains;
  `/prompt-label` does not contain the substring `/raw`, so no interaction
  with that helper. The one real instance found this milestone was the
  unrelated `--sanitized-only` comment collision described above, fixed by
  rewording rather than weakening any assertion.
- **(ii) Reusing an existing formatter/helper against a data shape it wasn't
  designed for:** re-checked `fetchHelperPromptLabel` against the actual
  diff (not just the plan) — it wraps its entire body in `try/catch`
  (`extension.mjs` lines 143–153), so both a non-OK response and any thrown
  exception from `fetchTextWithTimeout`/`parseJsonBody` resolve to `null`.
  It was not simplified back to a bare `if (!response.ok) return null`
  outside a try/catch, which would have let a network exception propagate
  through `Promise.all` in the `/api/traces` handler and taken down the
  entire trace list with a `502` — confirmed this did not happen by reading
  the committed code, not just the intent.

## Files checked before implementing

- `docs/decisions.md` D039 in full; `docs/specifications/security-data-boundaries.md`'s
  "Sprint15 continuation" section and the D032 paragraph it cross-references;
  the Sprint15 README's M7 section; M4 and M5's `plan.md`/`review.md` for
  pattern precedent.
- `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs` in full —
  confirmed the exact `if (!options.SanitizedOnly)` block contents, the
  `WriteFailureAsync`/`WriteNoStoreFailureAsync`/`WriteJsonAsync` helpers,
  and that `/traces/{traceId}/analysis/...` truly has no trace-id format
  validation (the basis for the D039 correction above) before writing any
  code.
- `MonitorPromptExtractor.cs`, `IMonitorProjectionStore.cs`, and
  `Traces.cshtml.cs`'s `PopulatePrompts` to confirm exact signatures and the
  call pattern to mirror.
- `RawTelemetryStore.ListRawRecordsByTraceId`'s SQL — discovered it queries
  via the `monitor_spans` projection table (`raw_record_id` mapped from
  `trace_id`), not the raw record's own `TraceId` column directly. This
  meant the new `SeedRawRecordWithPrompt` test helper needed to also call
  `store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record),
  ...)` (confirmed via `MonitorTraceDetailTests.cs`'s existing seed pattern)
  — the first draft only called `ApplyProjection` (matching the pre-existing
  `SeedRawRecord`, which doesn't need span projection since it's only used
  via `GetRawRecordById`) and every "ByDefault" test failed with
  `prompt_label: null` until this was added. Caught by running the filtered
  test suite, not by inspection alone.
- The full pre-change `extension.mjs` and `canvas-helpers.mjs` to reuse
  `fetchTextWithTimeout`, `monitorApiUrl`, `parseJsonBody`, and the
  `fetchHelperTraceRows`/`compactTrace`/`formatTraceLine` pipeline rather
  than reimplementing any of them.

## Validation commands and results

```
dotnet build CopilotAgentObservability.slnx
```
`ビルドに成功しました。 0 個の警告 0 エラー`

```
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorRawViewTests
```
`失敗: 0、合格: 11、スキップ: 0、合計: 11` (7 pre-existing + 4 new)

```
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```
`失敗: 0、合格: 13、スキップ: 0、合計: 13` (12 pre-existing + 1 new)

```
node --check .github/extensions/otel-monitor-canvas/extension.mjs
node --check .github/extensions/otel-monitor-canvas/canvas-helpers.mjs
```
Both: exit 0, no output (pass).

```
node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs
```
`tests 21, pass 21, fail 0` (18 pre-existing + 3 new).

```
dotnet test CopilotAgentObservability.slnx
```
`失敗: 0、合格: 611、スキップ: 0、合計: 611` (606 pre-existing baseline at HEAD +
5 new: 4 in `MonitorRawViewTests` + 1 in `CanvasExtensionContractTests`; the
3 new JS tests are covered by the `node --test` run above, tracked
separately from the .NET suite).

All required validation commands ran and passed; no pre-existing test was
removed or weakened.

## Findings

No blocking issues found. One real, unanticipated gotcha (the
`--sanitized-only` comment-wording collision, see above) was found and fixed
during implementation, not left as a residual risk.

## Residual risks / out of scope

- Live Canvas runtime verification of the updated dropdown (does the prompt
  label actually render correctly in a real Copilot Canvas session) is
  explicitly out of scope for this milestone — it was folded into the
  consolidated Live validation handoff (D038) covering children A–D, which
  has already run (`milestones/M6-live-validation-handoff/live-validation.md`,
  commit `acc0834`) and predates this milestone's dropdown text change. A
  fresh live check of the new prompt-label text specifically has not been
  run; this matches the plan's own scoping ("Live Canvas runtime rendering
  of the updated dropdown is out of this milestone's scope").
- `fetchHelperPromptLabel`'s per-trace fetches run via `Promise.all` with no
  batching or additional rate-limiting beyond the existing
  `MAX_TRACE_LIST_LIMIT` (50) bound on the trace list itself — same
  loopback-communication tradeoff D039 already accepted explicitly ("実測で
  問題が出た場合はバッチ API を別途検討する").
