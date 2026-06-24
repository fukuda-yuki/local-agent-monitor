# Sprint8 M6 Live Validation — BLOCKED

Status: **BLOCKED**. The real VS Code GitHub Copilot Chat live validation has NOT
been performed. Per the M6 plan (Task 5, Step 6), M6 and Sprint8 are therefore
**not** marked complete. This file records the blocker and the exact missing
evidence, not a completion.

## What Is Blocked

The hard gate requires a real VS Code GitHub Copilot Chat interaction to emit OTLP
HTTP/protobuf telemetry to the running monitor at `http://127.0.0.1:4320` (via
`profile-vscode-env --profile raw-local-receiver --target monitor`), and sanitized
evidence of that receipt to be recorded.

This cannot be done by the agent: it needs a human to apply the generated
environment to a VS Code session, hold a Copilot Chat conversation (a real,
credentialed Copilot LLM interaction), and have the Copilot extension export
telemetry. No agent-available tool can drive a credentialed Copilot Chat session
that emits telemetry.

Environment check (this machine, 2026-06-25): VS Code `1.125.1` is installed
(`code` CLI present), but no GitHub Copilot / Copilot Chat extension was detected
under `~/.vscode/extensions`. So even a human operator must first install the
Copilot Chat extension and sign in to a Copilot account before the live run is
possible. Extension install, GitHub authentication, and a real Copilot Chat
interaction are all human/credential-gated actions the agent cannot perform.

## What IS Verified (ready-state evidence)

These confirm the monitor is ready to receive real VS Code telemetry; only the
"real VS Code actually emits it" step is missing.

- Monitor-targeted environment generation is correct:
  `dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor`
  emits `CAO_COLLECTION_PROFILE=raw-local-receiver`,
  `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320` (the monitor port, not `4319`),
  and `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`.
- Real-process end-to-end ingestion loop (synthetic OTLP, not VS Code): with the
  monitor started from the built executable on `http://127.0.0.1:4320`,
  `POST /v1/traces` (OTLP JSON) returned `200 {"accepted":true,"rawRecordId":1}`,
  `GET /api/monitor/ingestions` showed `1` sanitized item, and `GET /health/ready`
  returned `200` with `{"status":"ready", ... ,"degraded_reasons":[]}`.
- Synthetic VS Code-shaped protobuf receipt is covered by automated tests
  (`MonitorHostTests.PostTraces_ValidProtobufPersistsRawRecord` uses
  `OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest`).
- Full suite: `dotnet build` 0/0; `dotnet test` 445 passing.

## Exact Steps To Complete (for the human operator)

1. Start the monitor:
   `dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor-live-validation.db --url http://127.0.0.1:4320`
   (do not commit `data\monitor-live-validation.db`).
2. Generate and apply the monitor-targeted environment in the VS Code shell:
   `dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor`
   then launch `code .` from that shell.
3. Run a small GitHub Copilot Chat interaction in VS Code.
4. Confirm receipt:
   `Invoke-WebRequest http://127.0.0.1:4320/api/monitor/ingestions` (≥ 1 sanitized item)
   and `http://127.0.0.1:4320/health/ready` (`200` once projection catches up).
5. Replace this file with the completion record below, filling every field.

## Completion Record Template (fill on success)

```markdown
# Sprint8 M6 Live Validation

Date:
Environment:
VS Code Version:
GitHub Copilot Extension Version:
Monitor Command:
Collection Profile:
Target:
Endpoint:
Raw View Enabled:
Trace Or Raw Record Identifier:
Confirmed:
- HTTP/protobuf telemetry reached LocalMonitor at 127.0.0.1:4320.
- Projection produced sanitized monitor rows.
- Langfuse was not required for this monitor path.

Unconfirmed:
- Metrics/logs signals, unless explicitly observed.

Repository Safety:
- No raw prompt, response, tool arguments, tool results, credentials, or sensitive local paths are recorded here.
```

Do not record prompt text, response text, tool arguments, tool results,
credentials, local sensitive paths, or raw payloads.
