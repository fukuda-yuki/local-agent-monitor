# Sprint13 Live Validation - 2026-06-30

Environment:

- Repository branch: `codex/sprint13-local-monitor-copilot-raw-analysis`
- Local Monitor URL: `http://127.0.0.1:4325`
- Runtime DB: `artifacts\sprint13-live-validation\monitor-live.db`
- .NET SDK: repository-pinned .NET 10 preview SDK in this workspace
- GitHub Copilot SDK package: `GitHub.Copilot.SDK` 1.0.4

Synthetic input:

- trace id: `sprint13live001`
- raw record id: `1`
- analysis focus: `latency`
- analysis run id: `1`
- synthetic raw markers:
  - `S13_RAW_PROMPT_MARKER`
  - `S13_RAW_RESPONSE_MARKER`
  - `S13_RAW_TOOL_ARGS_MARKER`
  - `S13_RAW_TOOL_RESULT_MARKER`
  - `sprint13-live@example.test`

Confirmed:

- Local Monitor started on loopback.
- `GET /health/live` returned `200`.
- `POST /v1/traces` returned `200`.
- TraceDetail for `sprint13live001` became available.
- `POST /traces/sprint13live001/analysis` created analysis run `1`.
- `GET /traces/sprint13live001/analysis/runs/1/safe-summary` did not include
  `S13_RAW_` markers.
- The safe summary did not include `sprint13-live@example.test`.

Unconfirmed / failed:

- The .NET Copilot SDK analysis run reached terminal status `failed`.
- Stored error: `The .NET Copilot SDK analysis runner failed before returning a result.`
- A signed-in Copilot SDK session returning model output was not confirmed in
  this environment.

## BYOK Retest - 2026-06-30

Configuration:

- `CopilotAnalysis:Model`: `glm-5.2`
- `CopilotAnalysis:Provider:Type`: `openai`
- `CopilotAnalysis:Provider:WireApi`: `completions`
- `CopilotAnalysis:Provider:BaseUrl`: configured through user-secrets
- `CopilotAnalysis:Provider:ApiKey`: configured through user-secrets

First BYOK attempt:

- trace id: `sprint13byok001`
- analysis run id: `1`
- terminal status: `failed`
- error: `InvalidOperationException: Session error: I/O error: アクセスが拒否されました。 (os error 5)`
- phase evidence showed the failure occurred after `sending_message`.

Fix applied before retest:

- Local Monitor now loads user-secrets explicitly.
- Local Monitor passes a writable `BaseDirectory` to `CopilotClientOptions`.
- Runner records SDK phases and sanitized SDK errors in local analysis events.

Successful BYOK retest:

- monitor URL: `http://127.0.0.1:4327`
- trace id: `sprint13byok002`
- analysis run id: `1`
- terminal status: `succeeded`
- result length: `2338`
- SDK phases recorded: `loading_local_tool_data`, `starting_client`,
  `creating_session`, `sending_message`, `session_completed`
- repository-safe summary contained no `S13_BYOK2_RAW_` markers.
- repository-safe summary contained no `sprint13-byok2@example.test`.

Notes:

- The failure occurred after run creation and before a textual Copilot analysis
  result was persisted.
- Existing repository-safe summary generation remained raw-free despite the
  failed SDK run.
- BYOK plus explicit SDK state directory resolved the SDK execution failure in
  this environment.
- The default GitHub Copilot quota-backed provider path was intentionally not
  revalidated after BYOK success; the user removed that path from the Sprint13
  validation gate on 2026-06-30.
