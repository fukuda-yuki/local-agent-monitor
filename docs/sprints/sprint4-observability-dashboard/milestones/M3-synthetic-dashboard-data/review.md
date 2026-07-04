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
- Self-review fix: raw input missing from `generate-dashboard-dataset` initially caused fallback buckets to use Unix epoch. This was corrected to use generation time as the fallback bucket source.
- Self-review fix: candidate rows that failed measurement mapping initially lost their source `trace_id`. This was corrected so the sanitized candidate trace id is retained while context fields remain null and collection health records the mapping gap.
- Self-review fix: raw store detection initially accepted only `.db`. It now matches `normalize-raw` by accepting `.db`, `.sqlite`, `.sqlite3`, and SQLite file headers.
- Residual risk: operation classification is intentionally minimal and synthetic-driven. Live Copilot span shape coverage remains outside M3 and should be revisited only when M4 / later prototype work needs it.
- Residual risk: backlog age is nullable because existing candidate records do not include generated timestamps.

## Verification

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~DashboardDatasetGenerationTests`
- `dotnet test CopilotAgentObservability.slnx`
- `git diff --check`

## Handoff

- M4 should compare Grafana JSON dashboard, static report, and repository-local preview using the generated four-table dataset.
- M5 should tier Outcome Linkage and decide whether candidate timestamp fields are worth adding for backlog age.
