# M4 Evidence: Redacted real-trace E2E

## Status

- Status: in progress.
- Status: complete.
- Started at: 2026-06-11.
- Confirmed at: 2026-06-11 23:10 JST.
- Repository input status: raw, redacted, and generated E2E artifacts stayed under ignored local `tmp\` paths and were not committed.

## Environment

- Repository: `C:\Users\mwam0\Documents\Codex\copilot-agent-observability`
- Shell: PowerShell
- OS context: Windows
- Local runtime data directory: `tmp\m4-redacted-real-trace-e2e\`
- Raw store output: `tmp\m4-redacted-real-trace-e2e\raw-store.db`
- Measurement outputs:
  - `tmp\m4-redacted-real-trace-e2e\measurements.csv`
  - `tmp\m4-redacted-real-trace-e2e\measurements.json`

`tmp\` and `*.db` are ignored by `.gitignore`.

## Input Acquisition Procedure

Use an already collected Copilot OTLP JSON payload or export a Copilot trace payload from the local observability backend. Before using it in this repository, place only a redacted copy under:

```powershell
New-Item -ItemType Directory -Force tmp\m4-redacted-real-trace-e2e | Out-Null
# Copy the redacted payload here after reviewing it:
# tmp\m4-redacted-real-trace-e2e\redacted-raw.json
```

Do not place an unredacted payload in tracked files. If an unredacted payload must be created temporarily, keep it under ignored local runtime paths such as `tmp\`, create the redacted copy, and delete the unredacted payload before preserving evidence.

## Redaction Policy

The redacted input must preserve OTLP shape enough for `ingest-raw` and `normalize-raw`, while replacing sensitive values.

Required removals or replacements:

- Replace real prompt and response content with placeholders such as `[REDACTED_PROMPT]` and `[REDACTED_RESPONSE]`.
- Replace tool arguments and tool results with placeholders such as `[REDACTED_TOOL_ARGUMENTS]` and `[REDACTED_TOOL_RESULTS]`.
- Remove or replace credentials, secrets, tokens, authorization headers, cookies, and connection strings.
- Remove Base64 encoded authorization headers and do not replace them with real-looking encoded values.
- Replace real `user.id`, `user.email`, user names, organization names, and other real identity values with example identities.
- Keep non-sensitive structure, attribute names, span names, timing fields, token counts, status codes, and trace ids if the trace id is not sensitive in the local context. If trace ids are considered sensitive, replace them consistently with fixed placeholder ids.

Before running the E2E, inspect the redacted file for obvious forbidden strings:

```powershell
rg -n "Authorization|Basic |Bearer |secret|token|password|cookie|Set-Cookie|BEGIN |PRIVATE KEY|@|prompt|response|tool arguments|tool results" tmp\m4-redacted-real-trace-e2e\redacted-raw.json
```

Any match must be reviewed. Benign matches such as redacted placeholder keys are allowed only if the value is sanitized.

## E2E Commands

```powershell
New-Item -ItemType Directory -Force tmp\m4-redacted-real-trace-e2e | Out-Null

dotnet run --project src\CopilotAgentObservability.ConfigCli -- `
  ingest-raw tmp\m4-redacted-real-trace-e2e\redacted-raw.json `
  --db tmp\m4-redacted-real-trace-e2e\raw-store.db

dotnet run --project src\CopilotAgentObservability.ConfigCli -- `
  normalize-raw tmp\m4-redacted-real-trace-e2e\raw-store.db `
  --csv tmp\m4-redacted-real-trace-e2e\measurements.csv `
  --json tmp\m4-redacted-real-trace-e2e\measurements.json
```

If downstream validation is performed, create a sanitized diagnosis input under `tmp\m4-redacted-real-trace-e2e\` and run:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- `
  validate-diagnoses tmp\m4-redacted-real-trace-e2e\diagnoses.json `
  --json tmp\m4-redacted-real-trace-e2e\validated-diagnoses.json

dotnet run --project src\CopilotAgentObservability.ConfigCli -- `
  generate-improvement-proposals tmp\m4-redacted-real-trace-e2e\validated-diagnoses.json `
  --json tmp\m4-redacted-real-trace-e2e\proposals.json

dotnet run --project src\CopilotAgentObservability.ConfigCli -- `
  evaluate-improvement-proposals tmp\m4-redacted-real-trace-e2e\proposals.json `
  --json tmp\m4-redacted-real-trace-e2e\evaluations.json

dotnet run --project src\CopilotAgentObservability.ConfigCli -- `
  generate-decision-template tmp\m4-redacted-real-trace-e2e\evaluations.json `
  --json tmp\m4-redacted-real-trace-e2e\decision-template.json
```

## Evidence To Record After Execution

- Confirmation timestamp.
- Input source type, for example VS Code Copilot Chat OTLP JSON or Copilot CLI OTLP JSON.
- Redaction summary.
- Exact commands executed.
- Generated local files.
- `ingest-raw` exit result.
- `normalize-raw` exit result.
- Measurement fields confirmed:
  - `trace_id`
  - `client_kind`
  - token columns
  - `duration_ms`
  - `error_count`
  - `tool_call_count`
- Missing or null fields and likely reason.
- Whether downstream workflow was run.
- Forbidden-content scan result for repository diff and evidence.

## Current Findings

- 2026-06-11: GitHub Copilot CLI 1.0.57 was available locally and supports `COPILOT_OTEL_FILE_EXPORTER_PATH`.
- 2026-06-11: VS Code was launched for optional user-driven Chat verification, and a read-only telemetry-check prompt was placed on the clipboard.
- 2026-06-11: A read-only Copilot CLI prompt executed successfully with OTel file exporter enabled. The prompt asked Copilot CLI to run `dotnet --version`, avoid file edits, avoid repository reads beyond necessity, and return only the .NET SDK version.
- 2026-06-11: The Copilot CLI file exporter produced real Copilot CLI span lines with `execute_tool report_intent`, `execute_tool powershell`, `chat gpt-5.4-mini`, and `invoke_agent`.
- 2026-06-11: The file exporter JSONL was converted to an OTLP JSON envelope because `ingest-raw` requires a top-level `resourceSpans` array.
- 2026-06-11: Redaction preserved OTLP structure, span names, timings, token counts, status, `client.kind`, `experiment.id`, and task attributes. Redaction replaced prompt / response messages, system instructions, tool definitions, tool call arguments / results, tool parameter command, GitHub org / repo / branch / commit identifiers, conversation / interaction / service request IDs, and end-user identity values.
- 2026-06-11: The content-capturing raw Copilot JSONL and Copilot CLI session output were deleted after `redacted-raw.json` was generated.
- 2026-06-11: `rg -n "mwam0|Authorization|Basic |Bearer |secret|password|cookie|Set-Cookie|BEGIN |PRIVATE KEY|github\.com|Documents|Codex|copilot-agent-observability|Read-only telemetry check|dotnet --version" tmp\m4-redacted-real-trace-e2e\redacted-raw.json` returned no matches.
- 2026-06-11: `ingest-raw tmp\m4-redacted-real-trace-e2e\redacted-raw.json --db tmp\m4-redacted-real-trace-e2e\raw-store.db` succeeded with `Ingested 1 raw telemetry record(s).`
- 2026-06-11: `normalize-raw tmp\m4-redacted-real-trace-e2e\raw-store.db --csv tmp\m4-redacted-real-trace-e2e\measurements.csv --json tmp\m4-redacted-real-trace-e2e\measurements.json` succeeded with `Normalized 1 raw measurement row(s).`

## Confirmed Measurement Row

| Field | Value |
| --- | --- |
| `trace_id` | `0e0c15ad877bcb21b5ba78795b3774d3` |
| `experiment_id` | `sprint2.5-m4` |
| `client_kind` | `copilot-cli` |
| `task_id` | `m4-redacted-real-trace-e2e` |
| `input_tokens` | `71468` |
| `output_tokens` | `724` |
| `total_tokens` | `72192` |
| `turn_count` | `2` |
| `tool_call_count` | `2` |
| `duration_ms` | `8241` |
| `error_count` | `0` |
| `success_status` | `not-evaluated` |

## Downstream Workflow

Sanitized diagnosis input was created from the measurement row under `tmp\m4-redacted-real-trace-e2e\diagnoses.json`.

The following commands succeeded:

```powershell
dotnet run --project src\CopilotAgentObservability.ConfigCli -- validate-diagnoses tmp\m4-redacted-real-trace-e2e\diagnoses.json --json tmp\m4-redacted-real-trace-e2e\validated-diagnoses.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-improvement-proposals tmp\m4-redacted-real-trace-e2e\validated-diagnoses.json --json tmp\m4-redacted-real-trace-e2e\proposals.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- evaluate-improvement-proposals tmp\m4-redacted-real-trace-e2e\proposals.json --json tmp\m4-redacted-real-trace-e2e\evaluations.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- generate-decision-template tmp\m4-redacted-real-trace-e2e\evaluations.json --json tmp\m4-redacted-real-trace-e2e\decision-template.json
dotnet run --project src\CopilotAgentObservability.ConfigCli -- record-human-decisions tmp\m4-redacted-real-trace-e2e\evaluations.json tmp\m4-redacted-real-trace-e2e\decisions.json --json tmp\m4-redacted-real-trace-e2e\human-decisions.json
```

Observed output:

- `Validated 1 diagnosis record(s).`
- `Generated 1 improvement proposal record(s).`
- `Evaluated 1 improvement proposal record(s).`
- `Generated decision template with 1 row(s).`
- `Recorded 1 human decision(s).`

## Generated Local Artifacts

Ignored local artifacts under `tmp\m4-redacted-real-trace-e2e\`:

- `redacted-raw.json`
- `raw-store.db`
- `measurements.csv`
- `measurements.json`
- `diagnoses.json`
- `validated-diagnoses.json`
- `proposals.json`
- `evaluations.json`
- `decision-template.json`
- `decisions.json`
- `human-decisions.json`

Deleted local artifacts:

- `copilot-otel.jsonl`
- `copilot-output.jsonl`

## Not Yet Verified

- VS Code Copilot Chat OTel capture was not executed by the agent because it requires user-side Chat UI interaction. VS Code was launched and the read-only prompt was placed on the clipboard.
- The redacted real-trace E2E used GitHub Copilot CLI, not VS Code Copilot Chat.
