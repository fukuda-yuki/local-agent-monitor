# M2 Review: Dashboard Dataset Contract

## Scope Reviewed

- `docs/requirements.md`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint4-observability-dashboard/README.md`
- `docs/sprints/sprint4-observability-dashboard/milestones/M2-dashboard-dataset-contract/task.md`
- `docs/sprints/sprint4-observability-dashboard/milestones/M2-dashboard-dataset-contract/dashboard-dataset-contract.md`
- Claude follow-up notes attached by the user for Sprint4 M2.

## Findings

- No blocking issues found.
- Self-review finding: The initial security boundary treated `team.id` and `department` as unconditionally forbidden real identity fields. This was too strong because project requirements collect them as Resource Attributes while excluding them from the Sprint4 initial dashboard. Fixed by keeping them out of the M2 required schema and requiring a future retention / access control / masking / communication spec before dashboard display.
- The Claude follow-up about TTFT acquisition was valid. M2 now treats TTFT as nullable and records `ttft_source`.
- The Claude follow-up about estimated cost was valid. M2 now defines cost as a unit-price-table estimate, not actual Copilot billing, and records `cost_source`.
- The Claude follow-up about stuck / long-running thresholds was valid. M2 now defines default thresholds and keeps them as dataset generation parameters.
- The Claude follow-up about `time_bucket` granularity was valid. M2 now defaults to `day` and allows `hour` / `week` for prototype validation.
- The Claude follow-up about Codex App was valid with scope limits. M2 reserves `client_kind=codex-app` for optional telemetry but does not require M2 / M3 fixture coverage.
- The Claude follow-up about Outcome Linkage priority was valid but not M2 implementation scope. M2 sends Tier grouping to M5 handoff.
- The dashboard dataset is split into four logical tables to avoid one wide schema and to match the planned Grafana / static report panels.
- Raw content, credentials, Base64 authorization headers, sensitive evidence, and real identity values remain excluded from dashboard datasets.

## Verification

- Documentation-only change.
- Product code, dependencies, fixture generation, Grafana JSON, live Langfuse, Copilot, external API, and network validation were not required.
- Ran `git diff --check`; no whitespace errors were reported.
  Git reported line-ending warnings for existing text files on Windows.

## Residual Risk

- M3 still needs synthetic fixtures to prove that nullable TTFT, cost, operation timing, and backlog fields do not block dataset generation.
- Operation-level metrics depend on raw span classification that is not implemented in M2.
- Outcome Linkage remains placeholder-only until M5 tiers future work.
