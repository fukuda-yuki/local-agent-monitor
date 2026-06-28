# Sprint10 M6 Live Validation

Status: Pending — user-gated.

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

No user-provided live VS Code Copilot Chat evidence has been recorded for
Sprint10 M6. This remains a completion blocker.
