# Config CLI Interface

`src/CopilotAgentObservability.ConfigCli` exposes repository-local commands for configuration, ingestion, normalization, candidate generation, and dashboard generation.

## Configuration Commands

```text
config-cli vscode-settings
config-cli langfuse-vscode-settings
config-cli collector-vscode-settings
config-cli vscode-env
config-cli langfuse-vscode-env
config-cli collector-vscode-env
config-cli vscode-file-settings <outfile>
config-cli copilot-cli-env
config-cli langfuse-copilot-cli-env
config-cli collector-copilot-cli-env
config-cli langfuse-codex-app-config
config-cli collector-codex-app-config
config-cli validate-resource-attributes <OTEL_RESOURCE_ATTRIBUTES>
```

Configuration commands must emit placeholders instead of real credentials.

## Raw Data Commands

```text
config-cli ingest-raw <raw.json> --db <raw-store.db>
config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
config-cli aggregate-measurements <input.json> [--csv <output.csv>] [--json <output.json>]
```

## Candidate Commands

```text
config-cli generate-diagnosis-candidates <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--include-sensitive-content] [--sensitive-output-dir <dir>] [--csv <output.csv>] [--json <output.json>]
config-cli generate-improvement-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-auto-decisions <improvement-candidates.csv|improvement-candidates.json> [--csv <output.csv>] [--json <output.json>]
config-cli adapt-diagnosis-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> <measurements.csv|measurements.json> [--csv <output.csv>] [--json <output.json>]
```

## Human Review Commands

```text
config-cli validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]
config-cli evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]
config-cli record-human-decisions <evaluations.csv|evaluations.json> <decisions.csv|decisions.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-decision-template <evaluations.csv|evaluations.json> [--csv <output.csv>] [--json <output.json>]
```

## Dashboard Commands

```text
config-cli generate-dashboard-dataset <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--diagnosis-candidates <input.csv|input.json>] [--improvement-candidates <input.csv|input.json>] [--auto-decisions <input.csv|input.json>] [--time-bucket <day|hour|week>] [--csv-dir <output-dir>] [--json <output.json>]
config-cli generate-static-dashboard <dashboard-dataset.json> --out-dir <output-dir> [--snapshot-date <YYYY-MM-DD>] [--title <title>]
```

## Change Rules

- New commands require tests and documentation updates.
- Existing command behavior changes require specification updates.
- Commands must not write secrets or raw sensitive content to repository-safe outputs.
