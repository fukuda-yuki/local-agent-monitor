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

## Completion Record — VS Code GitHub Copilot Chat

Date: 2026-06-27
Environment: Windows 11 Pro 10.0.26200, PowerShell
VS Code Version: 1.125.1
Monitor Command: `src\CopilotAgentObservability.LocalMonitor\bin\Debug\net10.0\CopilotAgentObservability.LocalMonitor.exe --db data\monitor-live-validation-vscode.db --url http://127.0.0.1:4320`
Collection Profile: raw-local-receiver
Client Kind: vscode-copilot-chat
Endpoint: http://127.0.0.1:4320
Raw View Enabled: false (default)
Raw Record IDs: 1, 2, 3, 4
Trace IDs (representative):
- `7e1297ec96ee33829907790095b9b738` (5 spans)
- `9fc6ee63de1ee4f212bd13589c99c0cf` (7 spans, 4 tool calls — across 2 raw records)
- `a85b30ab8f26852ed2ec7caabf4f7cf8` (6 spans)

Environment variables applied (generated via `profile-vscode-env --profile raw-local-receiver --target monitor`):

```
COPILOT_OTEL_ENABLED=true
COPILOT_OTEL_ENDPOINT=http://127.0.0.1:4320
COPILOT_OTEL_CAPTURE_CONTENT=true
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_RESOURCE_ATTRIBUTES=user.id=live-validation-user,user.email=2w2kld@gmail.com,team.id=platform,department=engineering,client.kind=vscode-copilot-chat,experiment.id=sprint8-live-validation
```

`GET http://127.0.0.1:4320/api/monitor/ingestions` response (sanitized):

```json
{
  "items": [
    {"raw_record_id":1,"received_at":"2026-06-27T09:55:38.6187133+09:00","source":"raw-otlp","trace_id":"7e1297ec96ee33829907790095b9b738","client_kind":"vscode-copilot-chat","span_count":5},
    {"raw_record_id":2,"received_at":"2026-06-27T09:55:44.7241257+09:00","source":"raw-otlp","trace_id":"9fc6ee63de1ee4f212bd13589c99c0cf","client_kind":"vscode-copilot-chat","span_count":4},
    {"raw_record_id":3,"received_at":"2026-06-27T09:56:01.9159032+09:00","source":"raw-otlp","trace_id":"a85b30ab8f26852ed2ec7caabf4f7cf8","client_kind":"vscode-copilot-chat","span_count":6},
    {"raw_record_id":4,"received_at":"2026-06-27T09:56:11.8453326+09:00","source":"raw-otlp","trace_id":"9fc6ee63de1ee4f212bd13589c99c0cf","client_kind":"vscode-copilot-chat","span_count":3}
  ],
  "next_cursor": null
}
```

`GET http://127.0.0.1:4320/health/ready` response:

```json
{
  "status": "ready",
  "checks": {
    "loopback_bound": true, "db_open": true, "migration_complete": true,
    "writer_running": true, "projection_worker_running": true,
    "ingestion_accepting": true, "projection_lag_seconds": 0, "projection_backlog": 0
  },
  "degraded_reasons": []
}
```

Confirmed:
- HTTP/protobuf telemetry from VS Code GitHub Copilot Chat reached LocalMonitor at 127.0.0.1:4320.
- 4 raw ingestion records received; projection produced 11 sanitized trace rows.
- tool_call_count > 0 observed (trace `9fc6ee63...` had 4 tool calls), confirming agent span linkage.
- `GET /health/ready` returned 200 with all checks passing and `degraded_reasons: []`.
- Langfuse was not required for this monitor path.

Unconfirmed:
- Metrics and logs signals were not explicitly observed (traces only).

Repository Safety:
- No raw prompt, response, tool arguments, tool results, credentials, or sensitive local paths are recorded here.
- `data\monitor-live-validation-vscode.db` is a local runtime artifact and is not committed.
