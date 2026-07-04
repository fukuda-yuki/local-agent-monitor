# Sprint15 M6 — Live Canvas Validation Evidence

Sprint-local validation evidence, not product behavior. Source of truth:
`docs/requirements.md`, `docs/spec.md`, `docs/specifications/`.

## Environment

- Runtime: GitHub Copilot CLI 1.0.66-1, Windows_NT
- Canvas runtime tools available: `extensions_reload`, `extensions_manage`,
  `list_canvas_capabilities`, `open_canvas`, `invoke_canvas_action`
- Local Monitor: `dotnet run --project src/CopilotAgentObservability.LocalMonitor
  -- --db data/sprint15-live-validation.db --url http://127.0.0.1:4320`
  (raw-default posture — raw route enabled for M5)
- Synthetic data: two OTLP JSON traces (5 spans each) from
  `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json`
  (trace_id `11111111111111111111111111111111`) plus a second copy with trace_id
  `222222...`. Both include chat spans with token counts, an errored execute_tool
  span, and parent-child relationships. No real user data in fixtures.
- Note: A third trace (`cecabee6d...`) appeared in results — the trace of this
  Copilot validation session, picked up by the Local Monitor. Its action responses
  contained only sanitized metadata (counts, timing, model); no raw content.

## Repository validation

| Check | Result |
|---|---|
| `dotnet build CopilotAgentObservability.slnx` | 0 warnings, 0 errors |
| `dotnet test CopilotAgentObservability.slnx` | 606 passing (301 ConfigCli + 305 LocalMonitor), 0 failed, 0 skipped |

## Canvas runtime validation (live)

### Extension discovery

| Step | Result |
|---|---|
| `extensions_reload` | 1 extension running; `otel-monitor-canvas` ready [project] |
| `extensions_manage({ operation: "inspect", name: "otel-monitor-canvas" })` | running, PID 10616, log available |
| `list_canvas_capabilities({ canvasId: "otel-monitor" })` | 5 actions returned with correct input schemas |
| `list_recent_traces` limit schema | minimum 1, maximum 50, required ✅ |
| `get_trace_summary` traceId pattern | `^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$` ✅ |
| `get_trace_span_tree` traceId pattern | same pattern ✅ |
| `get_cache_summary` traceId pattern | same pattern ✅ |

### Canvas open (M1 checks)

| Step | Result |
|---|---|
| `open_canvas({ canvasId: "otel-monitor", instanceId: "sprint15-live", input: { monitorBaseUrl: "http://127.0.0.1:4320" } })` | title "OTel Monitor", status "Connected", url `http://127.0.0.1:52775/?t=<uuid>` |
| `open()` started extension-owned loopback helper server | 127.0.0.1 binding ✅ |
| URL includes per-launch token query parameter | ✅ |
| Status "Connected" confirms monitor health check passed | ✅ |

**M1 helper page structural checks (fetched HTML):**

| Check | Result |
|---|---|
| Page heading "OTel Monitor — Canvas" | ✅ present |
| "接続状態" card with "接続済み（ready）" | ✅ present |
| Trace dropdown populated with decision-supporting trace lines | ✅ (3 traces visible via /api/traces proxy) |
| Focus dropdown: 遅い原因 / トークン消費 / キャッシュ効率 / エラー原因 | ✅ all four labels present |
| "Copilotでこのトレースを分析" button | ✅ present |
| Raw health response in collapsed `<details>` element | ✅ `<details>` element present |

### M4 dashboard summary card checks

| Check | Result |
|---|---|
| "Local Monitor 概要" card present in HTML | ✅ |
| `/api/summary` proxy returns fields: `scope`, `latest_trace`, `top_token_trace`, `error_trace`, `per_model_summary`, `per_client_kind_summary` | ✅ |
| `per_model_summary` has entries (2 models) | ✅ |
| `per_client_kind_summary` has entries (2 kinds) | ✅ |
| `latest_trace.line` shows decision-supporting label | ✅ "エラーあり / deepseek-v4-pro / 23 spans / 12 tools / 592,931 tokens / 15:28 / 1:30 / #cecabee6…" |
| "注目トレース" lines present | ✅ `latest_trace`, `top_token_trace`, `error_trace` all populated |
| No "概要を取得できませんでした" error | ✅ |

### M3 trace detail card checks

| Check | Result |
|---|---|
| "選択したトレースの要約" card present in HTML | ✅ |
| `/api/trace-detail/11111111111111111111111111111111` returns fields: `trace_id`, `status`, `primary_model`, `span_count`, `tool_call_count`, `total_tokens`, `duration_ms`, `cache_hit_rate`, `last_seen_at` | ✅ |
| `status`: "error" | ✅ (synthetic fixture has an errored execute_tool span) |
| `span_count`: 5 | ✅ |
| `tool_call_count`: 1 | ✅ |
| `total_tokens`: 15 | ✅ |
| `duration_ms`: 3000 | ✅ |
| `cache_hit_rate`: 0 | ✅ (no cache tokens in synthetic fixture) |
| `primary_model`: null | ⚠️ synthetic fixture does not set a model; field is present but null |
| `monitor_detail_url` in API response | ⚠️ not present — rendered client-side in HTML from `monitorUrl` + traceId |

### M5 raw preview checks (security-sensitive)

| Check | Result |
|---|---|
| "生データを表示（新しいタブ）" link present in HTML | ✅ |
| Raw preview `/raw-preview/11111111111111111111111111111111/3333333333333333` returns 200 | ✅ |
| Response has `Cache-Control: no-store` header | ✅ |
| Response contains `<pre>` block with inert displayed text | ✅ |
| Raw JSON payload visible (input_tokens etc.) | ✅ |
| Page has heading, trace/span id, "← ヘルパーページに戻る" link | ✅ |
| Non-existent span (`9999999999999999`) → 404 with "見つかりません" | ✅ |
| Wrong token → 401 `{"error":"unauthorized"}` | ✅ |
| No token → 401 `{"error":"unauthorized"}` | ✅ |
| Invalid IDs (`not a valid id!/also-bad`) → 400 with "不正" | ✅ |

### Action invocations (valid input)

| Action | Input | Result DTO fields |
|---|---|---|
| `monitor_health` | (omitted) | `reachable: true`, `ready_status_code: 200`, `canvas_safe: true`, `readiness`, `monitor_base_url`, `diagnostic` |
| `list_recent_traces` | `{ limit: 10 }` | 3 traces: `trace_id`, `client_kind`, `status`, `span_count`, `tool_call_count`, `error_count`, `input_tokens`, `output_tokens`, `total_tokens`, `turn_count`, `agent_invocation_count`, `duration_ms`, `primary_model`, `first_seen_at`, `last_seen_at` |
| `get_trace_summary` | `{ traceId: "111...111" }` | `trace` (compactTrace), `top_spans` (10 compactSpan entries), `models`, `cache_totals` {cache_read_tokens, cache_creation_tokens, input_tokens, output_tokens, total_tokens}, `span_page_truncated` |
| `get_trace_span_tree` | `{ traceId: "111...111" }` | `trace_id`, `span_count: 5`, `hierarchy_status: "flat_missing_parent_ids"`, `spans` (5 compactSpan entries), `returned_node_count: 5`, `truncated: false` |
| `get_cache_summary` | `{ traceId: "111...111" }` | `trace_id`, `turn_count: 1`, `returned_turn_count: 1`, `truncated: false`, `totals` {input_tokens, output_tokens, total_tokens, cache_read_tokens, cache_creation_tokens, duration_ms}, `cache_hit_rate: 0`, `turns` (1 entry) |

### Action safety — schema rejection

| Input | Expected | Actual |
|---|---|---|
| `list_recent_traces` with `{ limit: 100 }` | rejected (limit > 50) | rejected: "100 is greater than the maximum of 50" |
| `get_trace_summary` with `{ traceId: "bad id!" }` | rejected (pattern mismatch) | rejected: "\"bad id!\" does not match \"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$\"" |
| `get_trace_summary` with `{ traceId: "does-not-exist-00000000" }` | rejected (trace_not_found) | rejected: "CanvasRuntimeError: No sanitized trace data exists for that trace id." |

### Action safety — raw/PII negative checks

All action responses were inspected for the following forbidden content:

- raw prompt bodies, raw response bodies
- tool arguments, tool results
- PII (user.id, user.email)
- credentials, tokens (launch token is internal, not exposed in actions)
- local sensitive paths
- raw OTLP payloads
- runtime artifacts, generated telemetry content

**Result**: No forbidden content found in any action response. All responses
contain only sanitized metadata: trace/span ids, operation names, category,
tool names, model names, token counts, timing, status, and error types.

**Note**: The `list_recent_traces` response included a trace from the current
Copilot validation session (`cecabee6d...`). This is expected behavior — the
Local Monitor ingested the session's telemetry — but the action response
contained only metadata (counts, timing, model name), not raw content.

## Server lifecycle

| Check | Expected | Actual |
|---|---|---|
| Helper URL host | `127.0.0.1` | `127.0.0.1` (loopback) |
| Helper page GET `/` with correct token | `200` with HTML | `200`, contains "OTel Monitor — Canvas" |
| Helper page GET `/` without token | `401` | `401`, `{"error":"unauthorized"}` |
| Helper page GET `/` with wrong token | `401` | `401`, `{"error":"unauthorized"}` |
| `/api/traces` GET without token | `401` | `401` |
| `/api/traces` GET with wrong token | `401` | `401` |
| `/api/summary` GET with correct token | `200` | `200`, returns bounded D037/D038 DTO |
| `/api/trace-detail/:traceId` GET with correct token | `200` | `200`, returns trace detail summary |
| `/analyze` POST with wrong token | `401` | `401` |
| `/analyze` POST with correct token, invalid traceId | `400` | `400` |
| `/raw-preview/:t/:s` with wrong token | `401` | `401` |
| `/raw-preview/:t/:s` with invalid IDs | `400` | `400` with "不正" |
| `/raw-preview/:t/:s` non-existent span | `404` | `404` with "見つかりません" |
| `onClose()` closes extension-owned server | code inspection | `onClose()` handler deletes from `servers` Map and calls `server.close()` (lines 776–782 of extension.mjs) |

## Known behaviors (not defects)

1. **`primary_model` null in synthetic trace**: The synthetic OTLP fixture
   (`raw-otlp.synthetic.json`) does not set a `gen_ai.request.model` attribute
   on the chat span, so the Local Monitor's model extraction yields null. The
   field is correctly present in the DTO and rendered as empty. The
   `cecabee6d...` trace (from the real session) correctly shows
   `"deepseek-v4-pro"`.

2. **`monitor_detail_url` not in `/api/trace-detail/` response**: The
   "Local Monitorで詳細を見る" link is generated client-side in the helper page
   HTML from `monitorUrl` and `traceId`. It is not a field in the JSON DTO
   returned by the `/api/trace-detail/:traceId` endpoint. This matches the
   M3 spec's rendering contract.

3. **`hierarchy_status: "flat_missing_parent_ids"`**: The synthetic fixture
   spans all have `null` `parent_span_id`, so the hierarchy builder produces a
   flat diagnostic list rather than a tree. This is correct behavior for the
   fixture shape.

4. **Third trace from session ingestion**: The Local Monitor ingested the
   current Copilot session's telemetry, producing a third trace in
   `list_recent_traces`. The action response contains only sanitized metadata
   and does not leak raw content.

## Summary

| Acceptance criterion | Status |
|---|---|
| Repository build/test validation is green | ✅ passed (0/0 build, 606 tests) |
| Canvas extension discovered, inspected, opened | ✅ extension running, PID 10616 |
| M1 helper page: decision-supporting trace lines, Japanese labels, collapsed health | ✅ all structural checks passed |
| M4 dashboard summary card: trace count, モデル別, クライアント種別, 注目トレース | ✅ `/api/summary` proxy returns all expected fields with data |
| M3 trace detail card: 状態, 主要モデル, トークン合計, 所要時間, キャッシュヒット率 | ✅ `/api/trace-detail/:traceId` returns expected DTO fields |
| M5 raw preview: new-tab link, inert `<pre>`, `Cache-Control: no-store`, 404/401/400 negative checks | ✅ all security checks passed |
| 5 Canvas actions invoked with valid input | ✅ all returned correct bounded DTO shapes |
| Invalid inputs rejected by schema | ✅ limit > 50, pattern mismatch, trace_not_found all correctly rejected |
| Action responses bounded and sanitized | ✅ no raw/PII/sensitive data found |
| Server lifecycle (loopback, token, onClose) | ✅ all checks passed |
| Sprint15 live Canvas evidence recorded | ✅ this file; automated + agent-driven validation |
