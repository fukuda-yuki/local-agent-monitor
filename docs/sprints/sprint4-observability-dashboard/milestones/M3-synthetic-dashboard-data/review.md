# M3 Review: Synthetic Dashboard Data

## Scope Reviewed

- Config CLI `generate-dashboard-dataset` command.
- Dashboard dataset generator, raw operation reader, CSV / JSON writers, and auto-decision input reader.
- Synthetic dashboard dataset tests.
- Sprint4 task and README updates.

## Findings

- No blocking issues found.
- Spec compliance: M3 stays inside dashboard dataset generation and synthetic verification. It does not add Grafana, external ETL, live Langfuse validation, repository mutation automation, or outcome linkage integration.
- Safety boundary: dashboard output records sanitized references and `sensitive_bundle_present`; it does not emit raw prompt / response / tool arguments / tool results, credentials, Base64 auth headers, real identity values, or sensitive bundle paths.
- Data contract: JSON and CSV outputs follow the four M2 logical tables. Raw-derived fields remain nullable or `unavailable` when raw input is not supplied.
- Residual risk: operation classification is intentionally minimal and synthetic-driven. Live Copilot span shape coverage remains outside M3 and should be revisited only when M4 / later prototype work needs it.
- Residual risk: backlog age is nullable because existing candidate records do not include generated timestamps.

## Verification

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~DashboardDatasetGenerationTests`

## Handoff

- M4 should compare Grafana JSON dashboard, static report, and repository-local preview using the generated four-table dataset.
- M5 should tier Outcome Linkage and decide whether candidate timestamp fields are worth adding for backlog age.
