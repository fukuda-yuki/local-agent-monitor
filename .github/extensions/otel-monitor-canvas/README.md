# OTel Monitor Canvas Extension

This folder is the copyable GitHub Copilot app Canvas extension distribution
unit for the Copilot Agent Observability Local Ingestion Monitor.

## Scope

Project-scoped copy:

```text
.github/extensions/otel-monitor-canvas/
```

User-scoped copy:

```text
$COPILOT_HOME/extensions/otel-monitor-canvas/
```

Copy this whole folder, including `extension.mjs`, `canvas-helpers.mjs`,
`canvas-helpers.test.mjs`, `canvas.json`, this README, and `assets/preview.png`.
Do not create a second mirror folder in this repository.

## Prerequisites

Start the Local Ingestion Monitor before opening the Canvas extension. The
default monitor URL is:

```text
http://127.0.0.1:4320
```

The extension accepts a `monitorBaseUrl` Canvas input when a different loopback
port is used. The URL must stay loopback-only (`127.0.0.1`, `localhost`, or
`::1`). The extension can be used with the normal raw-default Local Monitor.
`--sanitized-only` remains an optional Local Monitor mode; it is not required by
this Canvas adapter.

## Requested Analysis Options

The helper page can show requested analysis profile, requested model, requested
reasoning effort, and timeout hint controls. These options come from the Local
Monitor sanitized endpoint:

```text
GET /api/analysis/options
```

They are used in the generated `session.send()` instruction and in dispatch
metadata. They are not per-message execution controls: the actual model and
reasoning behavior depend on the current GitHub Copilot session/runtime. The
helper does not start the Local Monitor raw analysis runner, does not wait for a
model response, and does not store final analysis result metadata.

Example local configuration:

```json
{
  "CopilotAnalysis": {
    "DefaultModel": "gpt-5.5",
    "Models": {
      "gpt-5.5": {
        "DisplayName": "GPT-5.5",
        "Provider": "openai",
        "SupportsReasoningEffort": true
      },
      "glm-5.2": {
        "DisplayName": "GLM-5.2",
        "Provider": "opencode-go",
        "SupportsReasoningEffort": false
      }
    }
  }
}
```

## Reload

After copying or editing the extension, reload or restart the GitHub Copilot
app extension runtime so it re-discovers the folder. In environments with
Canvas runtime tools, use the runtime reload/list/inspect flow and then open the
`otel-monitor` canvas.

## Data Safety

Do not copy, commit, or attach runtime artifacts with this extension:

- Local Monitor DB files, logs, pid files, state files, or generated output.
- Raw telemetry, raw prompt or response bodies, tool arguments or tool results,
  raw OTLP payloads, or screenshots containing real captured data.
- Credentials, secrets, API keys, bearer values, authorization headers, or
  per-launch helper tokens.
- Local absolute machine paths, user profile paths, or sensitive bundle paths.

The checked-in preview image uses synthetic mock data only. Canvas action
responses remain bounded DTOs over existing sanitized monitor APIs.
