# Sprint15 live-validation handoff prompt (D038)

This file is the self-contained prompt for a **GitHub Copilot app session**
(a different environment from Claude Code — it has the `extensions_manage`,
`open_canvas`, and `invoke_canvas_action` Canvas runtime tools that Claude
Code does not). Paste the section below, starting at "Copy from here", as
the initial message to that session. Everything above that line is context
for whoever hands this off, not part of the prompt itself.

---

## Copy from here

You are validating Sprint15 ("Canvas Diagnostic Surface") of the
`copilot-agent-observability` repository. Five children (A–D, one dropped)
were implemented and automated-tested by a separate Claude Code session, in
a repository whose `AGENTS.md` you should follow (English for agent-facing
notes, ask before irreversible actions, commit in small scoped steps, never
push/tag/open a PR). **Your job is exactly one thing: run the live Canvas
runtime verification that only a GitHub Copilot app session can do, and
record the results.** Do not modify `extension.mjs`, `canvas-helpers.mjs`,
or any Local Monitor source file. If something looks broken, record it as a
finding in the evidence file — do not attempt to fix it in this session.

### What was built (context, already implemented and committed)

The Canvas extension lives at `.github/extensions/otel-monitor-canvas/`
(`extension.mjs` + `canvas-helpers.mjs`). It exposes 5 bounded
`invoke_canvas_action` actions (`monitor_health`, `list_recent_traces`,
`get_trace_summary`, `get_trace_span_tree`, `get_cache_summary`) plus an
extension-owned loopback helper page (opened via `open_canvas`) with:

- **M1** (child A): decision-supporting trace line, Japanese focus labels
  (遅い原因/トークン消費/キャッシュ効率/エラー原因) and button
  ("Copilotでこのトレースを分析"), concrete health/error guidance, collapsed
  raw health response.
- **M4** (child B remainder): a "Local Monitor 概要" dashboard-summary card,
  populated by a `GET /api/summary` proxy on the helper server.
- **M3** (child C): a "選択したトレースの要約" trace-detail card, populated by
  a `GET /api/trace-detail/:traceId` proxy, shown when a trace is picked from
  the dropdown.
- **M5** (child D): a "生データを表示（新しいタブ）" link, enabled once a
  trace is selected AND a span id is typed into the existing span-id input.
  It opens `GET /raw-preview/:traceId/:spanId` (a real page-navigation link,
  new tab) — a Canvas-owned page that fetches Local Monitor's existing
  `GET /traces/{rawRecordId}/raw` server-to-server and re-embeds the
  already-HTML-encoded payload verbatim.
- Child E (session-to-trace correlation) was dropped — do not test it, it
  does not exist.

All of the above is unit/contract tested (`node --test`, `dotnet test`) but
**never exercised inside a real Copilot Canvas runtime** — that gap is what
you are closing.

### Step 1 — repository sanity check

From the repository root:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Record the pass/fail counts. If either fails, stop and report — do not
proceed to Canvas validation against a broken build.

### Step 2 — start the Local Monitor with synthetic seed data

Start the Local Monitor in its normal raw-default posture (NOT
`--sanitized-only` — M5's raw-preview link needs the raw route enabled) on a
scratch database, default port `4320`:

```powershell
dotnet run --project src/CopilotAgentObservability.LocalMonitor -- --db data/sprint15-live-validation.db --url http://127.0.0.1:4320
```

Seed it with the repository's existing synthetic OTLP fixture (no real user
data — this file is already checked in and safe to reuse):

```powershell
Invoke-WebRequest -Uri http://127.0.0.1:4320/v1/traces -Method Post -ContentType "application/json" -InFile "tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json"
```

This creates one trace (`trace_id = 11111111111111111111111111111111`) with
5 spans, including a `chat` span (`span_id = 3333333333333333`, has
`input_tokens`/`output_tokens`) and an errored `execute_tool` span
(`span_id = 4444444444444444`, `status: error`). Use `span_id
3333333333333333` when exercising the M5 raw-preview link below.

Optional but recommended for a richer M4 dashboard-card check (multiple
traces / models in the summary): copy the fixture, change every occurrence
of `11111111111111111111111111111111` to a different 32-hex-char id (e.g.
all `2`s), optionally change `client.kind` to a different value, and POST
that copy too, so the dashboard has 2 traces to summarize.

Confirm readiness before continuing:

```powershell
Invoke-WebRequest -Uri http://127.0.0.1:4320/health/ready
```
Expect `200` with `"status":"ready"`.

### Step 3 — Canvas extension discovery

```
extensions_manage({ operation: "reload" })
extensions_manage({ operation: "inspect", name: "otel-monitor-canvas" })
list_canvas_capabilities({ canvasId: "otel-monitor" })
```

Record: is the extension found, running, and does it report exactly the 5
actions above with the expected input schemas (traceId pattern
`^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$`, `list_recent_traces`'s `limit`
1–50)?

### Step 4 — open the Canvas and check the helper page

```
open_canvas({ canvasId: "otel-monitor", instanceId: "sprint15-live", input: { monitorBaseUrl: "http://127.0.0.1:4320" } })
```

Record the returned `title`/`status`/`url`. The `url` should be
`http://127.0.0.1:<port>/?t=<token>` — open it (however this session can
render/interact with a page; if you have a browser-capable tool, use it,
otherwise fetch and read the HTML) and check, in order:

**M1 checks**
- Page heading "OTel Monitor — Canvas", a "接続状態" card showing "接続済み
  （ready）".
- Trace dropdown populated with one (or two, if you seeded a second trace)
  decision-supporting line(s) — status / model / spans / tools / tokens /
  time / duration / short id — not just a bare trace id.
- Focus dropdown shows Japanese labels 遅い原因/トークン消費/キャッシュ効率/
  エラー原因 (the underlying values must stay `latency`/`tokens`/`cache`/
  `errors` — you don't need to inspect this unless something looks wrong).
- The raw health response is inside a collapsed `<details>` element, not
  shown by default.

**M4 checks**
- A "Local Monitor 概要" card is present, above the trace-detail card, and
  (after its own fetch resolves) shows a trace-count line, at least one
  entry under "モデル別"/"クライアント種別", and at least one line under
  "注目トレース". If it shows "概要を取得できませんでした" instead, that's a
  finding — investigate whether the Local Monitor is reachable at the
  configured URL before concluding it's a code defect.

**M3 checks**
- Select the seeded trace from the dropdown. The "選択したトレースの要約"
  card should populate (状態/主要モデル/トークン合計/所要時間/キャッシュヒット率)
  and a "Local Monitorで詳細を見る" link should appear, pointing at
  `http://127.0.0.1:4320/traces/11111111111111111111111111111111`.

**M5 checks (the security-sensitive one)**
- With the seeded trace selected, type `3333333333333333` into the "span id
  （任意）" input. The "生データを表示（新しいタブ）" link should become
  visible/enabled only now (it must NOT be visible before both a trace is
  selected and a span id is entered).
- Click it. It should open a **new tab** (do not treat this as a background
  fetch — if your tooling can distinguish a real navigation from an XHR/
  fetch, confirm this was a navigation, not a fetch call) showing a small
  page with a heading, the trace/span id, a "← ヘルパーページに戻る" link,
  and a `<pre>` block containing the raw OTLP JSON payload as **inert
  displayed text** (not executed, not interactive) — this should look like
  the same synthetic payload you POSTed in Step 2.
- If your tooling can inspect response headers, confirm this response has
  `Cache-Control: no-store`.
- Negative check: manually navigate to
  `http://127.0.0.1:<port>/raw-preview/11111111111111111111111111111111/9999999999999999?t=<token>`
  (same token, a span id that does not exist) — expect a small HTML page
  saying the span was not found (404), not a crash or a raw dump.
- Negative check: navigate to the same raw-preview URL with the token query
  parameter altered/removed — expect `401`, same as every other route on
  this server.
- Negative check: navigate to
  `http://127.0.0.1:<port>/raw-preview/not a valid id!/also-bad?t=<token>`
  — expect a small `400` HTML page, not a server error.

### Step 5 — invoke each of the 5 bounded actions (valid input)

```
invoke_canvas_action({ canvasId: "otel-monitor", instanceId: "sprint15-live", action: "monitor_health" })
invoke_canvas_action({ canvasId: "otel-monitor", instanceId: "sprint15-live", action: "list_recent_traces", input: { limit: 10 } })
invoke_canvas_action({ canvasId: "otel-monitor", instanceId: "sprint15-live", action: "get_trace_summary", input: { traceId: "11111111111111111111111111111111" } })
invoke_canvas_action({ canvasId: "otel-monitor", instanceId: "sprint15-live", action: "get_trace_span_tree", input: { traceId: "11111111111111111111111111111111" } })
invoke_canvas_action({ canvasId: "otel-monitor", instanceId: "sprint15-live", action: "get_cache_summary", input: { traceId: "11111111111111111111111111111111" } })
```

For each, record the shape of the returned DTO (field names only, not full
payload dumps if they're large) and confirm it matches what the action's
`inputSchema`/handler in `extension.mjs` promises (see the file for the
exact shape if unsure).

### Step 6 — invoke each action with invalid input (expect rejection)

```
invoke_canvas_action({ ..., action: "list_recent_traces", input: { limit: 100 } })       // expect: rejected, limit > 50
invoke_canvas_action({ ..., action: "get_trace_summary", input: { traceId: "bad id!" } }) // expect: rejected, pattern mismatch
invoke_canvas_action({ ..., action: "get_trace_summary", input: { traceId: "does-not-exist-00000000" } }) // expect: trace_not_found error, not a crash
```

Record the exact rejection message for each.

### Step 7 — raw/PII negative check across all action responses

Re-inspect every response from Steps 5–6 for any of: raw prompt/response
bodies, tool arguments/results, PII (`user.id`/`user.email`), credentials,
tokens, local file paths, raw OTLP payloads. None should be present — only
sanitized metadata (ids, names, counts, timings, status). Record "none
found" or list exactly what leaked (a leak here is a stop-and-report
finding, not something to patch yourself).

### Step 8 — server lifecycle / token checks

```powershell
# No token -> 401
Invoke-WebRequest -Uri "http://127.0.0.1:<port>/" -SkipHttpErrorCheck
# Wrong token -> 401
Invoke-WebRequest -Uri "http://127.0.0.1:<port>/?t=wrong-token" -SkipHttpErrorCheck
```

Confirm both return `401`, and that `onClose` behavior can be reasoned about
via code inspection (you don't need to force-close the canvas instance to
verify this — `CanvasExtensionContractTests` already covers it).

### Step 9 — record evidence and stop

Write your findings into a new file,
`docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M6-live-validation-handoff/live-validation.md`,
following the format of
`docs/sprints/sprint11-github-copilot-canvas-adapter-poc/milestones/M6-validation-and-docs/live-validation.md`
(environment, repository validation, Canvas runtime validation broken into
the same step groups as above, a raw/PII negative-check section, a server
lifecycle section, and a summary table). Do not paste large raw payload
dumps into the evidence file — field-name/shape summaries are enough,
consistent with the repository's "no raw content in committed docs" rule.

Once the evidence file is written, create a single local commit for it only
(message starting with `Sprint15:` per this repo's commit convention) and
stop. Do not push, tag, or open a pull request. If any of the checks above
failed or found a boundary violation, say so clearly in your final message
instead of treating the milestone as complete.

## Copy ends here
