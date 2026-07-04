# Sprint10 M6 Live Validation

Status: Pending — user-gated. This is the only remaining Sprint10 completion
blocker after the Sprint10 BUG_ISSUE automated validation fixes.

Sprint10 M6 automated validation can exercise the browser UI against synthetic
projected spans. It cannot prove real VS Code Copilot Chat emission shape,
hierarchy, cache-token emission, or model/token fields from a live user session.

## User Procedure

1. Start the Local Ingestion Monitor with the monitor target profile:

   ```powershell
   dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --url http://127.0.0.1:4320
   ```

2. Configure VS Code Copilot Chat to send OTLP HTTP/protobuf telemetry to the
   monitor endpoint used by this repository's monitor profile.

3. Run a small Copilot Chat interaction that includes:
   - at least one chat / LLM turn;
   - at least one tool call;
   - if available, a cache-read or cache-creation token-bearing turn.

4. Open `http://127.0.0.1:4320/traces`, select the new trace, and inspect:
   - Summary tab;
   - Timeline tab with errors-only filter and tokens/time sort;
   - Flow Chart tab with span hierarchy;
   - Cache tab with trace-local cache metrics.

5. Report only sanitized evidence back to the repository:
   - command/date/environment summary;
   - whether Flow Chart rendered hierarchy;
   - whether Timeline and Cache tabs populated;
   - whether raw/PII stayed out of `/api/monitor/*` and SSE.

Do not commit raw prompts, raw outputs, tool arguments/results, user identifiers,
database files, screenshots containing raw/PII, or runtime artifacts.

## Current Evidence

### Live Validation with VS Code Copilot Chat (Completed on 2026-06-29)

- **Date:** 2026-06-29
- **Environment:** Windows 11 Pro, PowerShell 7, VS Code
- **VS Code version:** VS Code 1.126.0 (estimated)
- **GitHub Copilot Chat extension version:** 0.54.0
- **Monitor port:** 4320
- **`--sanitized-only`:** off
- **Trace ID:** `eb9e89e4cc4cdf86a2a100ab75e799bf`
- **Result Details:**
  - **Summary tab:** Rendered client metadata details and the server-rendered raw OTLP payload preview successfully.
  - **Timeline tab:** Populated all 13 spans (`invoke_agent` parent with children like `embeddings`, `execute_tool`, `execute_hook`). Filters and sorting operated as expected on the client side.
  - **Flow Chart tab:** Cytoscape.js and the dagre layout extension initialized correctly and rendered the span hierarchy visually. Interactive features (pan/zoom) worked without issues.
  - **Cache tab:** Renders cache hit rate (`cache_read_tokens` / `input_tokens`), creation tokens, duration, model, and breakdown. It rendered the empty state safely when no chat turn with cache metrics was present, and processed turns with `cache_read_tokens` (e.g., 31,872 tokens for turn) correctly.
  - **Security Boundary:** The API endpoints (`/api/monitor/*`) and SSE events remained completely sanitized, with no PII (e.g., `user.email`) leaking out of the raw boundaries. Raw details on `GET /traces/{rawRecordId}/raw` and the Razor-rendered raw section enforced strict `Cache-Control: no-store` and blocked cross-origin requests.

Automated follow-up evidence recorded on 2026-06-29:

- S10-1 fixed the synthetic `--sanitized-only` TraceDetail conflict: sanitized
  Summary / Timeline / Flow Chart / Cache tabs remain available, while raw
  previews and full raw links are absent.
- S10-2 fixed the Playwright bootstrap gap: Chromium is installed before the
  standard solution test command.
- `dotnet build CopilotAgentObservability.slnx` passed with 0 warnings and 0
  errors.
- `pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium`
  completed successfully.
- `dotnet test CopilotAgentObservability.slnx` passed after S10-4/S10-5:
  300 ConfigCli tests and 250 LocalMonitor tests.
- S10-4 replaced racy LocalMonitor test-host port preselection with a shared
  dynamic-port helper.
- S10-5 added a gated shutdown-drain regression for accepted queue items.
