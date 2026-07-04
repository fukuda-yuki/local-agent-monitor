# M6 Evidence: Collaborative real-trace E2E

## Status

- Started at: 2026-06-18 JST.
- Current state: complete. GitHub Copilot CLI and VS Code Copilot Chat real-trace E2E both completed through auto-decision generation.
- Repository input status: raw, redacted, generated, and sensitive E2E artifacts stayed under ignored local `tmp\` paths and were not committed.

## Environment

- Repository: `copilot-agent-observability`
- Shell: PowerShell
- OS context: Windows
- GitHub Copilot CLI: `GitHub Copilot CLI 1.0.63`
- OTel exporter path used for CLI side: `COPILOT_OTEL_FILE_EXPORTER_PATH`
- Local runtime data directory: `tmp\sprint3-m6-real-trace-e2e\20260618-cli\`

`tmp\` and `*.db` are ignored by `.gitignore`.

## Data Handling

- Content capture was enabled only for the local CLI run.
- Unredacted Copilot CLI OTel JSONL and session output were kept only under ignored `tmp\` paths.
- The file exporter JSONL was converted into a top-level `resourceSpans` OTLP JSON envelope before `ingest-raw`.
- Trace IDs and span IDs were anonymized during conversion. The preserved anonymous trace id is `00000000000000000000000000000001`.
- Prompt, response, system instructions, tool schema, tool call arguments, tool results, identity, repository identifiers, branch / commit identifiers, service request ids, interaction ids, and tool call ids were replaced with placeholder values.
- The unredacted `copilot-otel.raw.jsonl` and `copilot-output.raw.jsonl` files were deleted after `redacted-raw.json` and generated outputs were created.

The redacted raw input scan returned no matches for the checked local identity, auth, secret, raw prompt, raw command, local path, GitHub URL, or synthetic email strings.

## CLI Execution

The CLI run used a read-only prompt that asked Copilot CLI to run `dotnet --version`, avoid file edits, and return the SDK version plus a no-edit confirmation.

```powershell
copilot -C . -p "<read-only Sprint3 M6 telemetry prompt>" --allow-tool='shell(dotnet --version)' --deny-tool='write' --deny-tool='edit' --disable-builtin-mcps --no-custom-instructions --no-auto-update --output-format json
```

The command completed with exit code `0`.

`copilot help monitoring` confirmed that OTel activates when `COPILOT_OTEL_FILE_EXPORTER_PATH` is set and that the CLI can write all signals to JSON-lines using the file exporter.

## CLI Pipeline Commands

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw tmp\sprint3-m6-real-trace-e2e\20260618-cli\redacted-raw.json --db tmp\sprint3-m6-real-trace-e2e\20260618-cli\raw-store.db
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tmp\sprint3-m6-real-trace-e2e\20260618-cli\raw-store.db --csv tmp\sprint3-m6-real-trace-e2e\20260618-cli\measurements.csv --json tmp\sprint3-m6-real-trace-e2e\20260618-cli\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-diagnosis-candidates tmp\sprint3-m6-real-trace-e2e\20260618-cli\measurements.json --raw tmp\sprint3-m6-real-trace-e2e\20260618-cli\redacted-raw.json --include-sensitive-content --sensitive-output-dir tmp\sprint3-m6-real-trace-e2e\20260618-cli\sensitive-bundle --json tmp\sprint3-m6-real-trace-e2e\20260618-cli\diagnosis-candidates.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-improvement-candidates tmp\sprint3-m6-real-trace-e2e\20260618-cli\diagnosis-candidates.json --json tmp\sprint3-m6-real-trace-e2e\20260618-cli\improvement-candidates.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-auto-decisions tmp\sprint3-m6-real-trace-e2e\20260618-cli\improvement-candidates.json --json tmp\sprint3-m6-real-trace-e2e\20260618-cli\auto-decisions.json
```

Results:

- `ingest-raw`: `Ingested 1 raw telemetry record(s).`
- `normalize-raw`: `Normalized 11 raw measurement row(s).`
- `generate-diagnosis-candidates`: `Generated 11 diagnosis candidate record(s).`
- `generate-improvement-candidates`: `Generated 10 improvement candidate record(s).`
- `generate-auto-decisions`: `Generated 10 auto-decision record(s).`

## Confirmed CLI Measurement

Primary nonblank trace row:

| Field | Value |
| --- | --- |
| `trace_id` | `00000000000000000000000000000001` |
| `client_kind` | `copilot-cli` |
| `experiment_id` | `sprint3-m6` |
| `task_id` | `sprint3-m6-cli-real-trace` |
| `input_tokens` | `59976` |
| `output_tokens` | `690` |
| `total_tokens` | `60666` |
| `turn_count` | `2` |
| `tool_call_count` | `2` |
| `duration_ms` | `5581` |
| `error_count` | `0` |
| `success_status` | `not-evaluated` |

The file exporter output also produced 10 normalized rows with missing trace context. Those rows were accepted by the Sprint3 candidate pipeline and produced `DIAG-METADATA-MISSING-TRACE-CONTEXT-V1` candidates.

## Candidate Output Summary

Diagnosis candidates:

| Rule | Count |
| --- | ---: |
| `DIAG-CONTENT-SENSITIVE-LEAK-V1` | 1 |
| `DIAG-METADATA-MISSING-TRACE-CONTEXT-V1` | 10 |

Candidate statuses:

| Status | Count |
| --- | ---: |
| `auto-eligible` | 10 |
| `blocked` | 1 |

Auto-decision statuses:

| Status | Count |
| --- | ---: |
| `auto-approved` | 10 |

The blocked sensitive-leak candidate did not flow into improvement candidate generation. The 10 metadata candidates flowed to `auto-approved` records with Sprint3-local `record-for-sprint4-planning` next action only.

## Sensitive Bundle

`--include-sensitive-content` generated `tmp\sprint3-m6-real-trace-e2e\20260618-cli\sensitive-bundle\manifest.json`.

Manifest review:

- `content_included=true`
- `evidence_index` count: `1`
- `delete_target_paths` count: `1`
- The delete target was the local ignored sensitive bundle root.

After manifest review, the generated `sensitive-bundle` directory was deleted. Raw unredacted CLI OTel JSONL and raw Copilot session output were also deleted.

## VS Code Copilot Chat Handoff

The agent prepared a temporary workspace under `tmp\sprint3-m6-real-trace-e2e\20260618-vscode\workspace\`, wrote VS Code file exporter settings there, copied the read-only prompt to the clipboard, and launched VS Code.

The user performed the final Chat send action. This preserved the user consent point for the logged-in Copilot Chat UI while allowing the agent to handle setup and analysis.

VS Code Chat file exporter output was produced at `tmp\sprint3-m6-real-trace-e2e\20260618-vscode\copilot-chat-otel.jsonl`. The output shape was JSONL log records with `spanContext`, `_body`, attributes, and resource raw attributes. The agent converted these records into an anonymized and redacted OTLP `resourceSpans` envelope before running the pipeline.

The redacted raw input scan returned no matches for the checked local identity, auth, secret, raw prompt phrase, local path, GitHub URL, or synthetic email strings.

## VS Code Chat Pipeline Commands

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- ingest-raw tmp\sprint3-m6-real-trace-e2e\20260618-vscode\redacted-raw.json --db tmp\sprint3-m6-real-trace-e2e\20260618-vscode\raw-store.db
dotnet run --project src\CopilotAgentObservability.ConfigCli -- normalize-raw tmp\sprint3-m6-real-trace-e2e\20260618-vscode\raw-store.db --csv tmp\sprint3-m6-real-trace-e2e\20260618-vscode\measurements.csv --json tmp\sprint3-m6-real-trace-e2e\20260618-vscode\measurements.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-diagnosis-candidates tmp\sprint3-m6-real-trace-e2e\20260618-vscode\measurements.json --raw tmp\sprint3-m6-real-trace-e2e\20260618-vscode\redacted-raw.json --include-sensitive-content --sensitive-output-dir tmp\sprint3-m6-real-trace-e2e\20260618-vscode\sensitive-bundle --json tmp\sprint3-m6-real-trace-e2e\20260618-vscode\diagnosis-candidates.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-improvement-candidates tmp\sprint3-m6-real-trace-e2e\20260618-vscode\diagnosis-candidates.json --json tmp\sprint3-m6-real-trace-e2e\20260618-vscode\improvement-candidates.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-auto-decisions tmp\sprint3-m6-real-trace-e2e\20260618-vscode\improvement-candidates.json --json tmp\sprint3-m6-real-trace-e2e\20260618-vscode\auto-decisions.json
```

Results:

- `ingest-raw`: `Ingested 1 raw telemetry record(s).`
- `normalize-raw`: `Normalized 27 raw measurement row(s).`
- `generate-diagnosis-candidates`: `Generated 27 diagnosis candidate record(s).`
- `generate-improvement-candidates`: `Generated 26 improvement candidate record(s).`
- `generate-auto-decisions`: `Generated 26 auto-decision record(s).`

## Confirmed VS Code Chat Measurement

Primary nonblank trace row:

| Field | Value |
| --- | --- |
| `trace_id` | `000000000000000000000000000003e9` |
| `client_kind` | `vscode-copilot-chat` |
| `experiment_id` | `sprint3-m6` |
| `task_id` | `sprint3-m6-vscode-real-trace` |
| `input_tokens` | empty |
| `output_tokens` | `1598` |
| `total_tokens` | empty |
| `turn_count` | `5` |
| `tool_call_count` | `0` |
| `duration_ms` | `12017` |
| `error_count` | `0` |
| `success_status` | `not-evaluated` |

The VS Code Chat file exporter output also produced 26 normalized rows with missing trace context. Those rows were accepted by the Sprint3 candidate pipeline and produced `DIAG-METADATA-MISSING-TRACE-CONTEXT-V1` candidates.

## VS Code Chat Candidate Output Summary

Diagnosis candidates:

| Rule | Count |
| --- | ---: |
| `DIAG-CONTENT-SENSITIVE-LEAK-V1` | 1 |
| `DIAG-METADATA-MISSING-TRACE-CONTEXT-V1` | 26 |

Candidate statuses:

| Status | Count |
| --- | ---: |
| `auto-eligible` | 26 |
| `blocked` | 1 |

Auto-decision statuses:

| Status | Count |
| --- | ---: |
| `auto-approved` | 26 |

The blocked sensitive-leak candidate did not flow into improvement candidate generation. The 26 metadata candidates flowed to `auto-approved` records with Sprint3-local `record-for-sprint4-planning` next action only.

## VS Code Chat Sensitive Bundle

`--include-sensitive-content` generated `tmp\sprint3-m6-real-trace-e2e\20260618-vscode\sensitive-bundle\manifest.json`.

Manifest review:

- `content_included=true`
- `evidence_index` count: `1`
- `delete_target_paths` count: `1`
- The delete target was the local ignored sensitive bundle root.

After manifest review, the generated `sensitive-bundle` directory was deleted. The raw unredacted VS Code Chat OTel JSONL was also deleted.

## Completion

M6 is complete for both required client kinds:

- `copilot-cli`
- `vscode-copilot-chat`
