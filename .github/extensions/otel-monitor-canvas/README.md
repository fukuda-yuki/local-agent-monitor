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

Copy this whole folder, including `extension.mjs`, its Canvas/Session/Evidence
helper and test modules, `canvas.json`, this README, and `assets/preview.png`.
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

## Session Workspace Evidence

The Canvas root page is the Session Workspace. Review shows exact binding,
instruction state, deterministic quality gates, and human evaluation. Evidence
uses only non-null trace IDs already recorded in the selected Session's runs.
For each trace it loads the sanitized Agent graph and every sanitized spans
page, keeps trace forests separate, and combines spans with Session events in a
linked timeline. Session events remain unowned; Agent ownership comes only from
the Local Monitor `span_ownership` graph response.

Agent graph and spans are loaded independently. If either source fails, the
other source remains visible with an explicit per-source error. Numeric span
cursors are followed until the Monitor returns `next_cursor: null`.

The inspector shows sanitized Agent/span/event fields. Missing typed Skill,
test, or review metadata is unavailable and is never guessed from names or
output. With no linked trace, Session events remain visible while the graph is
unavailable. `--sanitized-only` preserves this metadata view. Evidence adds no
raw content proxy and does not change Canvas actions.

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
responses remain bounded DTOs over existing sanitized monitor APIs. The local
token-gated helper page may display prompt labels and selected-trace
prompt/response previews for the same local user, but those labels/previews must
not be copied into action responses, logs, committed files, screenshots intended
for repository evidence, or static artifacts.
