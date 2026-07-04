# Sprint15 M7 live-validation handoff prompt (D039)

Self-contained prompt for a GitHub Copilot app session (a different
environment from Claude Code — it has `extensions_manage`, `open_canvas`,
`invoke_canvas_action`). Paste the section below "Copy from here" as the
initial message to that session.

Scope note: Sprint15's consolidated live validation for children A–D
(M1–M5) already ran and is recorded at
`milestones/M6-live-validation-handoff/live-validation.md` (commit
`acc0834`) — all 5 actions, the helper page, Japanese UI, and the
raw-preview link were already verified live. **This is a smaller, targeted
follow-up for M7 only**: the trace dropdown now shows a prompt label
alongside its existing decision-supporting line, backed by a new Local
Monitor route. Do not redo the full M1–M5 checklist; focus on what's new.

---

## Copy from here

You are validating Sprint15 milestone M7 ("prompt-aware trace selection",
D039) of the `copilot-agent-observability` repository, on top of an
already-validated Sprint15 M1–M6 baseline (see
`docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M6-live-validation-handoff/live-validation.md`
for that prior evidence — you do not need to re-verify it). Your job is
narrow: confirm the new prompt-label behavior actually works end-to-end in
a real Canvas runtime, and record findings. Do not modify
`extension.mjs`, `canvas-helpers.mjs`, or any Local Monitor source file —
if something looks broken, record it as a finding, don't fix it here.

### What's new since M6 (context)

- A new Local Monitor route, `GET /traces/{traceId}/prompt-label`, returns
  `{ "trace_id": "...", "prompt_label": "..." | null }` (same-origin
  checked, `no-store`, absent/`404` under `--sanitized-only`). It extracts a
  short (≤120 char) representative prompt from the trace's raw OTLP
  payload's `gen_ai.prompt` span attribute, reusing the same
  `MonitorPromptExtractor` the dashboard/trace-list pages already use
  (D032).
- The Canvas extension's `GET /api/traces` route (used only by the helper
  page's own trace dropdown, not a Canvas action) now fetches this label
  per trace, in parallel, and adds `prompt_label` to each item — additive,
  the existing `line` field is unchanged.
- The dropdown's `<option>` text is now
  `"${prompt_label} — ${line}"` when a label is available, or just the
  existing `line` (unchanged) when it isn't.
- None of the 5 `invoke_canvas_action` handlers changed. `prompt_label`
  never appears in any action response.

### Step 1 — repository sanity check

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Expect 0 warnings/errors and all tests passing (611 at the time this prompt
was written — some drift is fine, just confirm 0 failures). Stop and report
if either fails.

### Step 2 — start the Local Monitor and seed a trace WITH a real prompt

Start fresh (raw-default posture, not `--sanitized-only` — the new route
needs raw enabled):

```powershell
dotnet run --project src/CopilotAgentObservability.LocalMonitor -- --db data/sprint15-m7-live-validation.db --url http://127.0.0.1:4320
```

The M6 handoff's synthetic fixture
(`tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json`)
does **not** contain a `gen_ai.prompt` attribute (its "prompt"-named
attributes are deliberately different keys, used to test that non-matching
fields don't leak) — POSTing it alone will not produce a `prompt_label`.
Instead, save the following as e.g. `data/m7-prompt-trace.json` and POST it:

```json
{
  "resourceSpans": [
    {
      "resource": {
        "attributes": [
          { "key": "user.id", "value": { "stringValue": "m7-live-user" } },
          { "key": "user.email", "value": { "stringValue": "m7-live@example.test" } },
          { "key": "team.id", "value": { "stringValue": "team-m7" } },
          { "key": "department", "value": { "stringValue": "eng" } },
          { "key": "client.kind", "value": { "stringValue": "vscode-copilot-chat" } },
          { "key": "experiment.id", "value": { "stringValue": "m7-live" } }
        ]
      },
      "scopeSpans": [
        {
          "scope": { "name": "m7-live-validation" },
          "spans": [
            {
              "traceId": "22222222222222222222222222222222",
              "spanId": "aaaaaaaaaaaaaaaa",
              "name": "chat",
              "kind": 1,
              "startTimeUnixNano": "1751328000000000000",
              "endTimeUnixNano": "1751328005000000000",
              "attributes": [
                { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
                { "key": "gen_ai.prompt", "value": { "stringValue": "What does this function do?" } },
                { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "42" } },
                { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "17" } }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

```powershell
Invoke-WebRequest -Uri http://127.0.0.1:4320/v1/traces -Method Post -ContentType "application/json" -InFile "data/m7-prompt-trace.json"
```

Also POST the M6 fixture too (for a second, prompt-less trace, to confirm
the fallback path):

```powershell
Invoke-WebRequest -Uri http://127.0.0.1:4320/v1/traces -Method Post -ContentType "application/json" -InFile "tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json"
```

Confirm readiness: `Invoke-WebRequest -Uri http://127.0.0.1:4320/health/ready` → `200`.

### Step 3 — verify the new Local Monitor route directly (before touching Canvas)

```powershell
Invoke-WebRequest -Uri "http://127.0.0.1:4320/traces/22222222222222222222222222222222/prompt-label"
```
Expect `200`, body `{"trace_id":"22222222222222222222222222222222","prompt_label":"What does this function do?"}`, and a `Cache-Control: no-store` response header.

```powershell
Invoke-WebRequest -Uri "http://127.0.0.1:4320/traces/11111111111111111111111111111111/prompt-label"
```
(The M6 fixture's trace id, which has no `gen_ai.prompt` attribute.) Expect
`200`, `prompt_label: null` — **not** a `404` or error; this is a normal
outcome.

```powershell
Invoke-WebRequest -Uri "http://127.0.0.1:4320/traces/does-not-exist-at-all/prompt-label"
```
Expect `200`, `prompt_label: null` (unknown trace id — no format validation,
matches the `/traces/{traceId}/analysis/...` sibling route's own behavior;
this is intentional, not a bug).

### Step 4 — Canvas: open the helper page and check the dropdown

```
extensions_manage({ operation: "reload" })
open_canvas({ canvasId: "otel-monitor", instanceId: "m7-live", input: { monitorBaseUrl: "http://127.0.0.1:4320" } })
```

Open the returned helper page URL and check the trace dropdown:

- The trace with `traceId 22222222222222222222222222222222` should show as
  `"What does this function do? — OK / ... "` (prompt label, an em dash
  " — ", then the existing decision-supporting line — status/model/spans/
  tools/tokens/time/duration/short id).
- The M6 fixture's trace (`11111111111111111111111111111111`) should show
  **only** its existing decision-supporting line, with no prompt prefix (no
  label was available for it) — confirm there's no stray `"null — "` or
  `"undefined — "` text.
- Negative/robustness check: if your tooling can inspect network requests,
  confirm the `/api/traces` fetch that populates this dropdown still
  completes and returns `200` even though it now does N parallel
  server-to-server fetches to `/prompt-label` under the hood — the whole
  list must not fail or hang just because one trace's label lookup is slow
  or fails.

### Step 5 — raw/PII negative check (the same invariant every prior milestone re-checks)

Invoke a couple of the 5 bounded actions and confirm `prompt_label` (or any
prompt text) does **not** appear in any action response:

```
invoke_canvas_action({ canvasId: "otel-monitor", instanceId: "m7-live", action: "list_recent_traces", input: { limit: 10 } })
invoke_canvas_action({ canvasId: "otel-monitor", instanceId: "m7-live", action: "get_trace_summary", input: { traceId: "22222222222222222222222222222222" } })
```

Expect: neither response contains `prompt_label`, `"What does this function
do?"`, or any other prompt-like text — only the same bounded sanitized
fields as before M7 (trace/span ids, counts, timings, status, model names).

### Step 6 — record evidence and stop

Write findings into a new file,
`docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M7-prompt-aware-trace-selection/live-validation.md`,
following the format of the M6 evidence file referenced above (environment,
repository validation, the new-route checks from Step 3, the dropdown
rendering check from Step 4, the raw/PII negative check from Step 5, a
summary table). Then create a single local commit for that file only
(message starting with `Sprint15:`) and stop — do not push, tag, or open a
pull request. If anything in Steps 3–5 failed or showed a boundary
violation (e.g. `prompt_label` leaking into an action response), say so
plainly instead of treating the milestone as verified.

## Copy ends here
