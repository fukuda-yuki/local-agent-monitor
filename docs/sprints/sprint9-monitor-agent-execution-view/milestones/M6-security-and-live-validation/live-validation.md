# Sprint9 M6 Live Validation

Validates the **raw-default-on** posture and the new agent-execution surfaces
(trace-detail page + per-span read API) against a real GitHub Copilot client.

- **Part A — GitHub Copilot CLI:** **COMPLETE** (2026-06-28).
- **Part B — VS Code GitHub Copilot Chat:** **PENDING USER** (human-gated; the
  agent cannot drive the VS Code extension UI — checklist below).

Repository safety: no raw prompt, response, tool arguments/results, credentials,
or sensitive local paths are recorded here. The monitor DB and run logs live
under a scratch directory and are not committed. The resource attributes use the
ConfigCli placeholder `user.email=user@example.com` (synthetic PII), recorded
deliberately to demonstrate that PII is excluded from the sanitized surfaces.

---

## Part A — GitHub Copilot CLI (COMPLETE, 2026-06-28)

Date: 2026-06-28
Environment: Windows 11 Pro 10.0.26200, PowerShell 7
.NET SDK: 10.0.300-preview.0.26177.108
GitHub Copilot CLI version: **1.0.65** (standalone `copilot`, not the `gh copilot` extension)
Monitor command: `dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db <scratch>\m6-live.db --url http://127.0.0.1:4320`
Monitor port: **4320**
`--sanitized-only`: **off** (raw-default-on posture under test)
Collection profile: `raw-local-receiver` (CLI default endpoint 4319 overridden to the monitor at 4320)
Client kind: **copilot-cli**

Environment variables applied (from `profile-copilot-cli-env --profile
raw-local-receiver`, with the endpoint overridden to the monitor port):

```
COPILOT_OTEL_ENABLED=true
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4320
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true
OTEL_RESOURCE_ATTRIBUTES=user.id=example-user,user.email=user@example.com,team.id=platform,department=engineering,client.kind=copilot-cli,experiment.id=baseline
```

Copilot CLI command run (in an isolated scratch working directory):

```
copilot -C <scratch>\copilot-work --allow-all-tools --no-color -p "Create a file named notes.txt ... then read it back and run a shell command to print the current directory. Keep it brief."
```

CLI result: exit code 0; the run used `Edit` (apply_patch), `Read` (view), and a
`powershell` shell command, and reported token usage (↑ 46.3k / ↓ 403).

### Evidence — endpoint shape and ids

- Trace ID: `6301f12dc148a6fedb547736fb11fa63`
- Raw record IDs: `1`, `2` (telemetry arrived as two OTLP exports for the one trace)
- `GET /health/ready` → `200`, all checks pass, `degraded_reasons: []`.

`GET /api/monitor/traces` (sanitized rollup) for the trace:

| field | value |
| --- | --- |
| `client_kind` | `copilot-cli` |
| `span_count` | 7 |
| `tool_call_count` | 3 |
| `turn_count` | 3 |
| `agent_invocation_count` | 1 |
| `error_count` | 0 |
| `total_tokens` | 46684 |

### Evidence — sub-agent child-span hierarchy (confirmed)

`GET /api/monitor/traces/6301f12dc148a6fedb547736fb11fa63/spans` (sanitized,
per-span). All six leaf spans link to the single `invoke_agent` span via
`parent_span_id`:

| span_id | parent_span_id | operation | category | tool_name | total_tokens |
| --- | --- | --- | --- | --- | --- |
| `4ede521a93656294` | *(root)* | invoke_agent | agent_invocation | | 46684 |
| `e6e84eb0dc842aae` | `4ede521a93656294` | chat | llm_call | | 15379 |
| `d5329fde760c8fc1` | `4ede521a93656294` | chat | llm_call | | 15601 |
| `53491cef65086cdf` | `4ede521a93656294` | chat | llm_call | | 15704 |
| `8de677d735cc6c04` | `4ede521a93656294` | execute_tool | tool_call | apply_patch | |
| `740a0d3944659c6b` | `4ede521a93656294` | execute_tool | tool_call | view | |
| `ab9650c8a9626307` | `4ede521a93656294` | execute_tool | tool_call | powershell | |

### Token rollup (no double count) — confirmed

Per-turn tokens come from the three `chat` spans (15379 / 15601 / 15704). The
trace-level `total_tokens` is the `invoke_agent` agent-level total (46684), taken
from the agent span — not re-summed on top of the `chat` per-call tokens. This
matches the no-double-count rule.

### Sanitization / security boundary — confirmed live

- **PII excluded from sanitized surfaces:** the synthetic PII
  (`user@example.com`, `example-user`) is **absent** from
  `/api/monitor/ingestions`, `/api/monitor/traces`, and
  `/api/monitor/traces/{traceId}/spans`.
- **Real tool names sanitized and kept:** `apply_patch`, `view`, `powershell`
  passed the free-form-name guard and appear in the projection.
- **Raw-bearing route serves raw with `no-store`:** `GET /traces/1/raw` →
  `200`, `Cache-Control: no-store`, and (by design, on this raw-bearing route)
  the raw payload does contain the resource-level PII email.
- **Raw-bearing route rejects cross-site:** `GET /traces/1/raw` with
  `Sec-Fetch-Site: cross-site` → `403`.
- **Trace-detail page:** `GET /traces/6301f12dc148a6fedb547736fb11fa63` →
  `200`, `Cache-Control: no-store`.

### Confirmed

- HTTP/protobuf telemetry from GitHub Copilot CLI 1.0.65 reached the monitor at
  `127.0.0.1:4320` under the raw-default-on posture.
- The agent-execution **child-span hierarchy** is observable: one `invoke_agent`
  parent with `chat` + `execute_tool` children linked by `parent_span_id`.
- Real tool / LLM / token emission projected into sanitized rows; PII excluded
  from every sanitized read surface; raw + `no-store` + cross-site `403` enforced
  on the raw-bearing routes.

### Not covered by this CLI run (honest scope)

- **MCP tool spans** (`mcp_tool_name` / `mcp_server_hash`): the CLI run used
  built-in tools only; no MCP server was invoked, so the MCP path was not
  exercised live here. (It is covered by the synthetic per-attribute
  sanitization tests, and is a candidate for Part B.)
- **Nested sub-agent** (a child `invoke_agent` under the parent `invoke_agent`):
  this run emitted a single top-level `invoke_agent`. The *agent → tool/LLM*
  child hierarchy is confirmed; a *nested agent → sub-agent* hierarchy is best
  exercised via VS Code Copilot Chat (Part B).
- Metrics / logs OTLP signals were not observed (traces only).

---

## Part B — VS Code GitHub Copilot Chat (PENDING USER CONFIRMATION)

The following VS Code evidence was recorded in this working tree, but the
human-gated run has not been confirmed in the milestone plan/review record.
Until the user confirms that this run was actually performed, Part B remains
pending and this section is treated as candidate evidence, not closure evidence.

Date: 2026-06-28
Environment: Windows 11 Pro 10.0.26200, PowerShell 7
VS Code version: **1.126.0**
GitHub Copilot Chat extension version: **0.54.0**
Monitor port: **4320**
`--sanitized-only`: **off**

### Evidence Checklist

- [x] **datetime**: 2026-06-28
- [x] **VS Code version + GitHub Copilot Chat extension version**: VS Code 1.126.0, Copilot Chat 0.54.0.
- [x] **monitor port (expect 4320) and `--sanitized-only` off**: Confirmed.
- [x] **trace id(s) / raw record id(s)**: Trace ID: `d5f1e865c74fb247793c070f550de290`, Raw Record IDs: `6`, `8`.
- [x] **`/api/monitor/traces` shows `agent_invocation_count ≥ 1` and `tool_call_count ≥ 1`**: Confirmed (`agent_invocation_count`: 2, `tool_call_count`: 8).
- [x] **`/api/monitor/traces/{traceId}/spans` shows the sub-agent child-span hierarchy**: Confirmed (sub-agent `Explore` is nested under `runSubagent` tool call of the parent `GitHub Copilot Chat` invocation).
- [x] **(if MCP used) `mcp_tool_name` present and sanitized; `mcp_server_hash` is a hash only**: N/A (MCP tools were not used in this run).
- [x] **PII (`user.email`) absent from `/api/monitor/*`; present only on `GET /traces/{rawRecordId}/raw` and the trace-detail page, both with `Cache-Control: no-store`**: Confirmed.
- [x] **cross-site fetch of a raw-bearing route → `403`**: Confirmed (returned 403 Forbidden with `cross_origin_forbidden` message).

### Evidence — sub-agent child-span hierarchy (confirmed)

`GET /api/monitor/traces/d5f1e865c74fb247793c070f550de290/spans` (sanitized, per-span):

| span_id | parent_span_id | operation | category | tool_name | agent_name | total_tokens |
| --- | --- | --- | --- | --- | --- | --- |
| `f69922281b20578c` | *(root)* | invoke_agent | agent_invocation | | GitHub Copilot Chat | 146074 |
| `6c199dda9e85aab7` | `f69922281b20578c` | embeddings | unknown | | | |
| `0b38b9e79f390cbc` | `f69922281b20578c` | execute_tool | tool_call | manage_todo_list | | |
| `395f9b6f48512b1a` | `f69922281b20578c` | chat | llm_call | | panel/editAgent | 28577 |
| `61b056007da769da` | `f69922281b20578c` | execute_tool | tool_call | list_dir | | |
| `9a98baa9f96a6b94` | `f69922281b20578c` | execute_tool | tool_call | runSubagent | | |
| `d6db467b4e142d9f` | `9a98baa9f96a6b94` | invoke_agent | agent_invocation | | Explore | 29774 |
| `bffd7d8419784e79` | `d6db467b4e142d9f` | chat | llm_call | | tool/runSubagent-Explore | 14637 |
| `4faa083e54e803c4` | `d6db467b4e142d9f` | execute_tool | tool_call | list_dir | | |
| `3b9760c11a614c04` | `d6db467b4e142d9f` | chat | llm_call | | tool/runSubagent-Explore | 15137 |
| `73110109cd6799b9` | `f69922281b20578c` | embeddings | unknown | | | |
| `03e7cd5a18e49c4a` | `f69922281b20578c` | chat | llm_call | | panel/editAgent | 29059 |
| `4f26da2a1c6f631e` | `f69922281b20578c` | execute_tool | tool_call | create_file | | |
| `9579735c48225f0d` | `f69922281b20578c` | chat | llm_call | | panel/editAgent | 29196 |
| `028724135abbbf1a` | `f69922281b20578c` | execute_tool | tool_call | apply_patch | | |
| `208d0deb12239efa` | `f69922281b20578c` | chat | llm_call | | panel/editAgent | 29547 |
| `9ee285ec715efb83` | `f69922281b20578c` | execute_tool | tool_call | read_file | | |
| `249b61cad0f2057a` | `f69922281b20578c` | execute_tool | tool_call | run_in_terminal | | |
| `02b64be1705a5374` | `f69922281b20578c` | chat | llm_call | | panel/editAgent | 29695 |

### Sanitization / security boundary — confirmed live

- **PII excluded from sanitized surfaces:** The synthetic PII (`user@example.com`, `example-user`) is **absent** from `/api/monitor/ingestions`, `/api/monitor/traces`, and `/api/monitor/traces/{traceId}/spans`.
- **Real tool names sanitized and kept:** Real tools (`read_file`, `create_file`, `apply_patch`, `run_in_terminal`, `list_dir`, `runSubagent`, `manage_todo_list`) passed the sanitization filter and appear in the projection.
- **Raw-bearing route serves raw with `no-store`:** `GET /traces/6/raw` → `200`, `Cache-Control: no-store`, and the raw payload contains the resource-level PII email.
- **Raw-bearing route rejects cross-site:** `GET /traces/6/raw` with `Sec-Fetch-Site: cross-site` → `403 Forbidden` (`cross_origin_forbidden`).
- **Trace-detail page:** `GET /traces/d5f1e865c74fb247793c070f550de290` → `200`, `Cache-Control: no-store`.
