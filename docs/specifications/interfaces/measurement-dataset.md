# Normalized Measurement Dataset Interface

Normalized measurement output is the deterministic, repository-safe dataset generated from raw OTLP JSON or the local SQLite raw store.

## Producers

The dataset is produced by:

```text
config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]
config-cli aggregate-measurements <input.json> [--csv <output.csv>] [--json <output.json>]
```

`normalize-raw` is the preferred path for OTLP payloads.
`aggregate-measurements` remains supported for existing Langfuse-style trace JSON fixtures.

## Formats

CSV output uses the column order below.
JSON output is an array of objects using the same snake_case property names.

```text
trace_id,experiment_id,client_kind,task_id,task_category,task_run_index,experiment_condition,prompt_version,agent_variant,repo_snapshot,input_tokens,output_tokens,total_tokens,turn_count,tool_call_count,duration_ms,error_count,success_status,evaluator_id,evaluation_notes,evaluated_at,unknown_spans_json,unknown_attributes_json,aggregation_notes
```

## Field Semantics

| Field | Semantics |
| --- | --- |
| `trace_id` | Trace-level reference carried from the source payload when available. |
| `experiment_id` | Experiment identifier from resource attributes or trace metadata. |
| `client_kind` | Client family such as `vscode-copilot-chat`, `copilot-cli`, `codex-app`, or `unknown`. |
| `task_id` | Optional task identifier used for repeatable comparison. |
| `task_category` | Optional task grouping. |
| `task_run_index` | Optional run number for repeated task execution. |
| `experiment_condition` | Baseline, variant, or other condition label. |
| `prompt_version` | Prompt or instruction version label. |
| `agent_variant` | Agent, skill, wrapper, or configuration variant label. |
| `repo_snapshot` | Repository state reference such as branch, commit, or synthetic fixture id. |
| `input_tokens` | Input token count when available or derived. |
| `output_tokens` | Output token count when available or derived. |
| `total_tokens` | Total token count when available or derived. |
| `turn_count` | Count of conversation or agent turns represented by the trace. |
| `tool_call_count` | Count of tool operations represented by the trace. |
| `duration_ms` | Trace duration in milliseconds when available or derived. |
| `error_count` | Number of error events or spans mapped into the measurement. |
| `success_status` | Normalized evaluation status. Generated rows default to `not-evaluated`; downstream commands also consume manually supplied status labels such as `pass` and `excluded`. |
| `evaluator_id` | Optional evaluator identity for manually assessed rows. |
| `evaluation_notes` | Optional sanitized evaluator notes. |
| `evaluated_at` | Optional ISO-8601 evaluation timestamp. |
| `unknown_spans_json` | JSON array with unmapped span evidence for collection-health reporting. |
| `unknown_attributes_json` | JSON object with unmapped attribute evidence for collection-health reporting. |
| `aggregation_notes` | Sanitized notes about normalization or aggregation decisions. |

## Safety Boundary

This dataset is designed for repository storage when generated from synthetic fixtures or sanitized real data.
It must not contain raw prompt, raw response, system prompt text, tool arguments, tool results, source-code fragments from observed sessions, credentials, or sensitive bundle paths.

`unknown_spans_json`, `unknown_attributes_json`, `evaluation_notes`, and `aggregation_notes` require the same review as other repository-bound outputs because they can accidentally carry source payload values if normalization logic changes.

## Required-Attribute Health Scope

The expected collection attributes (`user.id`, `user.email`, `team.id`, `department`, `client.kind`, `experiment.id`) are defined in [../../requirements.md](../../requirements.md) and [../layers/telemetry-ingestion.md](../layers/telemetry-ingestion.md). This follows a **2層モデル**: the 6 attributes remain expected collection metadata, but repository-safe automatic missing-attribute validation covers only `client.kind` and `experiment.id`. This repository-safe dataset omits `user_id`, `user_email`, `team_id`, and `department` columns by design (PII / organization attributes are not repository-safe). `team.id` and `department`, when present, are retained as unknown resource attributes in `unknown_attributes_json` but are not validated as required. PII / organization attribute collection health is observable only on the local monitor side (loopback default-on display).

`trace_id` is not a Resource Attribute but a **source trace reference** required for referential integrity. When missing, a collection health row is emitted (sharing the `missing-required-attribute` health_check_kind in the current implementation), but this is conceptually separate from Resource Attribute required-attribute validation.

## Downstream Consumers

Normalized measurements are consumed by:

- [Candidate records](candidate-records.md)
- [Dashboard dataset](dashboard-dataset.md)
- [Raw store and normalization](../layers/raw-store-normalization.md)
