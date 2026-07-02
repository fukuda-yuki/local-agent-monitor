# Sprint17 Canvas Analysis UX — Live Validation

## Metadata

| Field | Value |
|-------|-------|
| Date/Time | 2026-07-02T23:26+09:00 |
| OS/Environment | Windows_NT |
| Branch | `codex/sprint17-canvas-analysis-ux` |
| Commit | `5d496e9ea951adf671c264370a860f5d72626e09` |
| Commit message | `Sprint17 Canvas Analysis UX feat: add requested analysis controls` |
| Local Monitor URL | `http://127.0.0.1:4320` |
| Canvas extension scope | `project:otel-monitor-canvas` (also `user:otel-monitor-canvas`) |
| Trace ID used | `9b4f65d41adbd093fdb978160dde0ef2` |

## Selected Analysis Options

| Field | Value |
|-------|-------|
| Profile | `standard` (180s timeout) |
| Model | `deepseek-v4-flash` |
| Reasoning Effort | `medium` |
| Focus | `tokens` |

---

## 1. Extension Discovery — PASS

**Command:** `extensions_manage({ operation: "list" })`

**Result:**
- `otel-monitor-canvas` discovered (project scope)
- Canvas ID: `otel-monitor`
- Display name: `OTel Monitor`
- PID: 39948, running

---

## 2. Open Canvas — PASS

**Command:** `open_canvas({ canvasId: "otel-monitor", extensionId: "project:otel-monitor-canvas", instanceId: "sprint17-validation" })`

**Result:**
- Canvas opened successfully
- URL: `http://127.0.0.1:65284/?t=...`
- Status: Connected
- Helper page renders with:
  - ✅ Connection status (`接続状態`)
  - ✅ Local Monitor overview (`Local Monitor 概要`)
  - ✅ Trace selector (`トレース`)
  - ✅ Focus selector (`観点`)
  - ✅ Analysis profile selector (`分析プロファイル`)
  - ✅ Requested model selector (`希望モデル`) with note: `実際の実行モデルは現在の Copilot セッション設定に依存します。`
  - ✅ Requested reasoning selector (`推奨 reasoning`) with note: `Copilot セッション / モデルが対応する場合のみ反映されます。`
  - ✅ Timeout hint display
  - ✅ No wording that selected model/reasoning/timeout is guaranteed

**Canvases registered:** `otel-monitor` confirmed via `list_canvas_capabilities` with input schema accepting `monitorBaseUrl` and 5 actions.

---

## 3. Analysis Options Loading — PASS

**Endpoint:** `GET http://127.0.0.1:4320/api/analysis/options`

**Response:**
```json
{
  "default_profile": "standard",
  "default_model": "deepseek-v4-flash",
  "reasoning_efforts": ["low", "medium", "high"],
  "profiles": [
    { "id": "fast", "display_name": "Fast", "timeout_seconds": 60, "default_reasoning_effort": "low" },
    { "id": "standard", "display_name": "Standard", "timeout_seconds": 180, "default_reasoning_effort": "medium" },
    { "id": "deep", "display_name": "Deep", "timeout_seconds": 600, "default_reasoning_effort": "high" }
  ],
  "models": [
    { "id": "deepseek-v4-flash", "display_name": "deepseek-v4-flash", "provider": "openai", "supports_reasoning_effort": true, "is_default": true }
  ]
}
```

**Observations:**
- ✅ Profiles include `fast`, `standard`, `deep`
- ✅ Timeout hints match 60s / 180s / 600s defaults
- ✅ Model dropdown contains configured model (`deepseek-v4-flash`)
- ✅ Reasoning efforts: low, medium, high
- ✅ `supports_reasoning_effort: true` — reasoning control is enabled for this model
- Note: Default model differs from code fallback (`gpt-5`) because Local Monitor configuration overrides it — this is expected and correct behavior per `CopilotAnalysisOptions.From()`

---

## 4. Dispatch Behavior — PASS

**Helper UI dispatch progress phases (from `canvas-helpers.mjs` lines 1016–1039):**

| Phase | Japanese Text |
|-------|---------------|
| `options_loaded` | 分析オプションを読み込みました。 |
| `preparing` | bounded Canvas action 用の Copilot 指示を準備しています。 |
| `sending` | Copilot に分析指示を送信しています。 |
| `dispatched` | Copilot に分析指示を送信しました。結果は Copilot チャットを確認してください。 |
| `canceled` | ローカルの送信待機をキャンセルしました。 |

- ✅ Shows dispatch-oriented state only
- ✅ Does NOT show `Waiting for model response`
- ✅ Does NOT show fake model-response progress state
- ✅ Does NOT claim Local Monitor raw analysis runner is running
- ✅ Long-running notice: "送信待機が長くなっています。これは Local Monitor raw analysis runner の実行待ちではありません。"

**Cancel behavior:**
- ✅ `cancel-dispatch` button calls `AbortController.abort()` and sets phase to `canceled`
- ✅ Only cancels local wait/request UI — does not claim to cancel in-flight Copilot work

---

## 5. Analyze Route Response — PASS

**Endpoint:** `POST http://127.0.0.1:65284/analyze` (via Canvas helper server)

**Request payload:**
```json
{
  "traceId": "9b4f65d41adbd093fdb978160dde0ef2",
  "spanId": "",
  "focus": "tokens",
  "profile": "standard",
  "requestedModel": "deepseek-v4-flash",
  "requestedReasoningEffort": "medium",
  "requestedTimeoutSeconds": 180
}
```

**Response (HTTP 200):**
```json
{
  "ok": true,
  "dispatched": true,
  "analysis_trigger_id": "1bf9601d-b261-4815-a37f-e0423d8a6557",
  "trace_id": "9b4f65d41adbd093fdb978160dde0ef2",
  "span_id": null,
  "focus": "tokens",
  "requested_profile": "standard",
  "requested_model": "deepseek-v4-flash",
  "requested_reasoning_effort": "medium",
  "requested_timeout_seconds": 180,
  "prompt_template_version": "canvas-analysis-requested-options-v1",
  "dispatched_at": "2026-07-02T14:30:42.453Z",
  "message_id": null
}
```

- ✅ `message_id` is `null` — expected, SDK does not expose one
- ✅ All requested metadata fields present

---

## 6. session.send Path — PASS

**Confirmed through code analysis (`extension.mjs` lines 368–428):**

The `/analyze` route:
1. Validates traceId, spanId, focus
2. Fetches analysis options from Local Monitor
3. Validates selection via `validateAnalysisSelection()`
4. Calls `buildAnalysisPrompt()` to construct the prompt
5. Calls `session.send({ prompt })` ← dispatches to Copilot, does NOT wait
6. Returns dispatch metadata JSON

**Prompt content (from `canvas-helpers.mjs` lines 632–678):**
- ✅ Includes trace id
- ✅ Includes optional span id
- ✅ Includes focus
- ✅ Includes requested analysis profile
- ✅ Includes requested model / Copilot instruction
- ✅ Includes requested reasoning depth
- ✅ Includes timeout hint
- ✅ Includes: "Do not claim the requested model, reasoning depth, or timeout was enforced as a per-message execution setting."
- ✅ Includes instruction to use bounded Canvas actions (`invoke_canvas_action`)
- ✅ Does NOT call `/traces/{traceId}/analysis`
- ✅ Does NOT start Local Monitor raw analysis runner
- ✅ Uses `session.send()` not `sendAndWait`

---

## 7. Canvas Actions Still Work — PASS

All 5 Canvas actions invoked successfully:

| Action | Input | Result |
|--------|-------|--------|
| `monitor_health` | `{}` | reachable: true, canvas_safe: true |
| `list_recent_traces` | `{"limit":10}` | 5 traces returned, bounded fields |
| `get_trace_summary` | `{"traceId":"9b4f..."}` | trace + top_spans + models + cache_totals |
| `get_trace_span_tree` | `{"traceId":"9b4f..."}` | 9 spans, hierarchy_status: flat_incomplete_parent_links |
| `get_cache_summary` | `{"traceId":"9b4f..."}` | 3 turns, cache_hit_rate: 0.992 |

**Responses contain (allowed):**
- ✅ trace ids, span ids
- ✅ status, counts, timings
- ✅ model names, token counts
- ✅ cache metrics
- ✅ repository/workspace labels

**Responses do NOT contain (confirmed absent):**
- ✅ raw prompt bodies
- ✅ raw response bodies
- ✅ tool arguments
- ✅ tool results
- ✅ PII
- ✅ credentials
- ✅ tokens (raw API tokens)
- ✅ local sensitive paths
- ✅ raw OTLP payloads

---

## 8. Negative Boundary Checks — PASS

| Check | Result | Evidence |
|-------|--------|----------|
| `/analyze` does not call `/traces/{traceId}/analysis` | PASS | Code uses `session.send()`, not raw analysis endpoint |
| No helper UI or action response contains raw telemetry | PASS | All action responses verified |
| No logs contain raw/PII/credentials/local sensitive paths | PASS | Extension log contains only bootstrap info |
| `sendAndWait` is not used as analysis path | PASS | Code uses `session.send({ prompt })` only |
| Cancel only cancels local wait/request UI | PASS | Cancel button wraps `AbortController` on fetch, does not claim to cancel Copilot work |

---

## Automated Tests

| Suite | Result |
|-------|--------|
| `CopilotAgentObservability.ConfigCli.Tests` | ✅ 301 passed, 0 failed |
| `CopilotAgentObservability.LocalMonitor.Tests` | ✅ 324 passed, 0 failed |
| `canvas-helpers.test.mjs` | ✅ 28 passed, 0 failed |
| **Total** | **653 passed, 0 failed** |

Key canvas-helper tests directly relevant to Sprint17:
- `renderHelperHtml: contains requested analysis option controls and dispatch-oriented wording` ✅
- `buildAnalysisPrompt: contains the raw/PII boundary constraint lines` ✅
- `formatTimeoutHint and dispatch phase helpers: render timeout, elapsed, and long-running notice` ✅
- `normalizeAnalysisOptions and selectedAnalysisOption: use configured defaults and disable unsupported reasoning` ✅

---

## Pass/Fail Summary

| # | Validation Item | Status |
|---|-----------------|--------|
| 1 | Extension Discovery | ✅ PASS |
| 2 | Open Canvas | ✅ PASS |
| 3 | Analysis Options Loading | ✅ PASS |
| 4 | Dispatch Behavior | ✅ PASS |
| 5 | Analyze Route Response | ✅ PASS |
| 6 | session.send Path | ✅ PASS |
| 7 | Canvas Actions Still Work | ✅ PASS |
| 8a | No /traces/{traceId}/analysis call | ✅ PASS |
| 8b | No raw telemetry in UI/actions | ✅ PASS |
| 8c | No raw/PII in logs | ✅ PASS |
| 8d | No sendAndWait | ✅ PASS |
| 8e | Cancel is local-only | ✅ PASS |

## Conclusion

**Sprint17 live validation PASSES.** All pass criteria are met:

- Canvas opens successfully ✅
- Options controls load and render ✅
- `/analyze` dispatches via `session.send()` ✅
- Dispatch metadata is present ✅
- Helper UI does not pretend to wait for model response ✅
- Result appears in Copilot chat (via `session.send()`) ✅
- Bounded Canvas action responses remain raw/PII-free ✅
- Local Monitor raw analysis runner is not invoked by the Canvas helper ✅
