# Raw Store And Normalization Specification

## Scope

This layer converts raw telemetry into repository-local deterministic datasets.
It does not require Langfuse UI.

## Input

Accepted input:

- saved raw OTLP JSON file。
- SQLite raw store created by `ingest-raw`。
- SQLite raw store populated by the `raw-local-receiver` profile。

Raw payloads may include prompt, response, tool arguments / results, path information, identity-bearing attributes, and credential-like strings.
Raw payloads must not be committed.

## Raw Store

Default local path:

```text
data/raw-store.db
```

`data/` is local runtime data.
The SQLite store is not a shared operational database.

Rejected for current default storage scope:

- PostgreSQL as default raw telemetry store。

The `raw-local-receiver` profile owns local receiver behavior.
This layer owns deterministic storage and normalization after raw telemetry is
available.

## Commands

```text
config-cli ingest-raw <raw.json> --db <raw-store.db>
config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
```

`normalize-raw` may read either a raw store or a raw OTLP JSON file.
At least one output option must be provided by commands that require output.

The local receiver may write directly to the SQLite raw store or produce a raw
OTLP file that can be passed to `ingest-raw`.
Either path must preserve the same normalization output contract.

## Normalized Measurement Responsibilities

Normalization must:

- preserve trace-level reference IDs.
- derive `client_kind`, task and experiment attributes when present.
- classify common logical categories such as LLM call, tool call, permission, file operation, shell command, error, user interaction.
- handle unknown span names without failing only because span names drift.
- produce unknown span / attribute evidence for collection health.
- avoid copying raw prompt / response / tool arguments / tool results into repository-safe outputs.

The output contract is defined in [../interfaces/measurement-dataset.md](../interfaces/measurement-dataset.md).

## Validation

Use synthetic fixtures for automated tests.
Live Copilot execution is manual validation and must record environment, settings, trace id or equivalent identifier, confirmed items, and unconfirmed items.
