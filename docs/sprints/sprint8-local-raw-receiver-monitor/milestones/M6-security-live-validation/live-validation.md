# Sprint8 M6 Live Validation

## GitHub Copilot CLI — COMPLETED (2026-06-27)

Status: **COMPLETE** for the GitHub Copilot CLI source path.
Status: **PENDING** for the VS Code GitHub Copilot Chat source path (see below).

---

## Completion Record — GitHub Copilot CLI

Date: 2026-06-27
Environment: Windows 11 Pro 10.0.26200, PowerShell
GitHub Copilot CLI Version: 1.0.64
Monitor Command: `dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor-live-validation.db --url http://127.0.0.1:4319`
Collection Profile: raw-local-receiver
Client Kind: copilot-cli
Endpoint: http://127.0.0.1:4319
Raw View Enabled: false (default)
Trace ID: 2621a34469989538231578952f69b4de
Raw Record ID: 1
Span Count: 2

Environment variables applied (generated via `profile-copilot-cli-env --profile raw-local-receiver`):

```
COPILOT_OTEL_ENABLED=true
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4319
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true
OTEL_RESOURCE_ATTRIBUTES=user.id=live-validation-user,user.email=2w2kld@gmail.com,team.id=platform,department=engineering,client.kind=copilot-cli,experiment.id=sprint8-live-validation
```

Copilot CLI command run:

```
gh copilot -- -p "What is 2 + 2? Respond briefly." --no-ask-user -s
```

Evidence:

`GET http://127.0.0.1:4319/api/monitor/ingestions` response:

```json
{
  "items": [
    {
      "raw_record_id": 1,
      "received_at": "2026-06-27T09:47:03.9216451+09:00",
      "source": "raw-otlp",
      "trace_id": "2621a34469989538231578952f69b4de",
      "client_kind": "copilot-cli",
      "span_count": 2,
      "projected_at": "2026-06-27T09:47:04.0079616+09:00"
    }
  ],
  "next_cursor": null
}
```

`GET http://127.0.0.1:4319/health/ready` response:

```json
{
  "status": "ready",
  "checks": {
    "loopback_bound": true,
    "db_open": true,
    "migration_complete": true,
    "writer_running": true,
    "projection_worker_running": true,
    "ingestion_accepting": true,
    "projection_lag_seconds": 0,
    "projection_backlog": 0
  },
  "degraded_reasons": []
}
```

Confirmed:
- HTTP/protobuf telemetry from GitHub Copilot CLI 1.0.64 reached LocalMonitor at 127.0.0.1:4319.
- Ingestion response `{"accepted":true}` returned from `POST /v1/traces`.
- Projection produced sanitized monitor rows (`/api/monitor/ingestions` and `/api/monitor/traces` both showed 1 item).
- `GET /health/ready` returned 200 with all checks passing and `degraded_reasons: []`.
- Langfuse was not required for this monitor path.

Unconfirmed:
- Metrics and logs signals were not explicitly observed (traces only).
- VS Code GitHub Copilot Chat source path — see below.

Repository Safety:
- No raw prompt, response, tool arguments, tool results, credentials, or sensitive local paths are recorded here.
- `data\monitor-live-validation.db` is a local runtime artifact and is not committed.

---

## VS Code GitHub Copilot Chat — STILL PENDING

The VS Code Copilot Chat path requires a human operator with the GitHub Copilot Chat
extension installed and a signed-in Copilot account.

Environment check (2026-06-25): VS Code 1.125.1 is installed (`code` CLI present),
but no GitHub Copilot / Copilot Chat extension was detected under `~/.vscode/extensions`.

Steps to complete:

1. Install the GitHub Copilot Chat extension in VS Code and sign in with a GitHub
   account that has an active Copilot subscription.
2. Start the monitor:
   `dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db data\monitor-live-validation-vscode.db --url http://127.0.0.1:4320`
3. Generate and apply the monitor-targeted environment in the VS Code shell:
   `dotnet run --project src\CopilotAgentObservability.ConfigCli -- profile-vscode-env --profile raw-local-receiver --target monitor`
   then launch `code .` from that shell.
4. Run a small GitHub Copilot Chat interaction in VS Code.
5. Confirm receipt:
   `Invoke-WebRequest http://127.0.0.1:4320/api/monitor/ingestions` (≥ 1 sanitized item)
   and `http://127.0.0.1:4320/health/ready` (`200` with no `degraded_reasons`).
6. Append a completion record here (same format as the Copilot CLI record above),
   filling VS Code Version, Copilot Extension Version, and the trace/record identifier.

Do not record prompt text, response text, tool arguments, tool results,
credentials, local sensitive paths, or raw payloads.
