# Config CLI Interface

`src/CopilotAgentObservability.ConfigCli` exposes repository-local commands for configuration, ingestion, normalization, candidate generation, and dashboard generation.

## Configuration Commands

```text
config-cli list-collection-profiles
config-cli profile-vscode-env [--profile <collection-profile>] [--target <receiver|monitor>] [--endpoint <loopback-http-url>]
config-cli profile-copilot-cli-env [--profile <collection-profile>]
config-cli profile-codex-app-config [--profile <collection-profile>]
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

`--profile` uses the values defined in
[collection-profiles.md](collection-profiles.md).
When `--profile` is omitted, profile-aware commands read
`CAO_COLLECTION_PROFILE`.
If neither is set, profile-aware commands must fail with a deterministic error
instead of silently choosing a profile.

Existing explicit commands such as `langfuse-vscode-env` and
`collector-vscode-env` remain supported compatibility entry points.

For the `raw-local-receiver` profile, `profile-vscode-env` selects which local
raw target the generated VS Code environment points at:

- `--target receiver` (default): the Config CLI receiver, `http://127.0.0.1:4319`
  (unchanged behavior).
- `--target monitor`: the Local Ingestion Monitor, `http://127.0.0.1:4320`.
- `--endpoint <loopback-http-url>`: explicit override (must be loopback) for a
  non-default monitor / receiver port; overrides the `--target` default.

`--target` and `--endpoint` apply only to `raw-local-receiver`; combining them
with another profile fails with a deterministic error.

`list-collection-profiles` lists all product profile values. Sprint6
profile-aware output commands returned a deterministic error for
`raw-local-receiver`; Sprint7 replaces that reserved error with generated
configuration that points to the local receiver endpoint and does not emit
Langfuse credentials, Collector headers, or remote endpoints.

## Raw Data Commands

```text
config-cli ingest-raw <raw.json> --db <raw-store.db>
config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
config-cli aggregate-measurements <input.json> [--csv <output.csv>] [--json <output.json>]
config-cli serve-raw-local-receiver [--db <raw-store.db>] [--url <loopback-http-url>]
```

`serve-raw-local-receiver` starts the initial repository-local foreground
receiver for the `raw-local-receiver` profile. Defaults:

```text
--db data/raw-store.db
--url http://127.0.0.1:4319
```

The receiver must reject non-loopback bind URLs. It accepts OTLP HTTP trace
payloads on `/v1/traces`, persists received telemetry as local runtime raw
store data, and leaves normalized measurement, candidate, and dashboard
dataset schemas unchanged.

`serve-raw-local-receiver` is retained and runs **side-by-side** with the Local
Ingestion Monitor (Sprint8). The monitor is a **separate ASP.NET Core process**
(`src/CopilotAgentObservability.LocalMonitor`), not a Config CLI subcommand; it
binds a distinct loopback port (default `http://127.0.0.1:4320`, avoiding the
Collector `4318` and this receiver's `4319`) while this receiver keeps
`http://127.0.0.1:4319`. The monitor run interface, ports, and
health endpoints are specified in
[../layers/telemetry-ingestion.md](../layers/telemetry-ingestion.md), and its
raw / PII boundary in
[../security-data-boundaries.md](../security-data-boundaries.md). To point VS
Code at the monitor, generate the environment with `profile-vscode-env --profile
raw-local-receiver --target monitor`; the default `--target receiver` keeps
emitting `4319`.

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
