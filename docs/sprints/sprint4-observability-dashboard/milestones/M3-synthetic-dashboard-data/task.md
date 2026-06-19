# M3: Synthetic Dashboard Data

## Status

Complete.

## Objective

Generate Sprint4 M2 dashboard dataset tables from synthetic normalized measurements and candidate pipeline records, then verify nullable metrics and safety boundaries.

## Scope

- Add `generate-dashboard-dataset` to Config CLI.
- Generate JSON output as the M2 single object with four logical tables.
- Generate CSV output as one file per logical table under `--csv-dir`.
- Use optional raw OTLP / raw store input for TTFT fallback, model, operation timing, retry, approval wait, permission result, and subagent / nested agent counts.
- Join diagnosis candidate, improvement candidate, and auto-decision records to measurement context by `trace_id`.
- Preserve the M2 dashboard safety boundary by excluding raw content, credentials, Base64 auth headers, real identity values, and sensitive bundle paths.

## Acceptance Criteria

- [x] `dashboard_run_summary` is populated from normalized measurements.
- [x] `dashboard_operation_summary` is populated from synthetic raw operations when `--raw` is supplied.
- [x] `dashboard_candidate_summary` is populated from diagnosis / improvement / auto-decision records.
- [x] `dashboard_collection_health` reports missing attributes and unknown telemetry counts.
- [x] TTFT fallback, direct TTFT, estimated cost, operation timing, retry, approval wait, subagent wait, long-running trace/tool, stuck session, and nullable backlog age are covered by tests.
- [x] Output excludes raw prompt / response / tool arguments / tool results, credentials, Base64 headers, real identity values, and sensitive bundle paths.
- [x] `codex-app` remains a reserved optional client kind and is not required for M3 fixture coverage.
- [x] Grafana JSON dashboard, static report, and repository-local preview remain M4 scope.

## Verification

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~DashboardDatasetGenerationTests`

## Notes

- Backlog age remains nullable in M3 because existing candidate schemas do not carry generated timestamps.
- Estimated cost is an observability estimate from a small built-in unit price table, not actual Copilot billing.
- M3 does not add external ETL, Grafana, live Langfuse, GitHub / Notion outcome linkage, or production data handling.
