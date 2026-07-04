# M4: Dashboard Prototype Path

## Status

Complete.

## Objective

Compare the Sprint4 dashboard prototype paths and select the implementation path for Sprint5 or later without committing to a production Grafana, Azure Monitor, or custom UI stack.

## Scope

- Compare Grafana JSON dashboard, static report, and repository-local preview against the M2 dashboard dataset contract and M3 synthetic generator output.
- Treat Grafana JSON dashboard as the first candidate because it most directly validates aggregate, percentile, status distribution, and drilldown-reference panels.
- Keep Langfuse trace viewer, raw store, and sensitive bundle as drilldown destinations rather than replacing them.
- Define the minimum handoff criteria for a later implementation milestone.
- Preserve the dashboard safety boundary: no raw prompt, response, system prompt, tool arguments, tool results, credentials, Base64 authorization headers, real user identity, or sensitive bundle path content in dashboard datasets or prototype documents.

## Non-goals

- Implementing a Grafana JSON dashboard.
- Implementing a static report generator.
- Implementing a repository-local preview app or custom Web UI.
- Selecting Grafana Cloud, Azure Managed Grafana, self-host Grafana, Application Insights, Tempo, Loki, Mimir, Datadog, or New Relic for production.
- Adding external ETL, GitHub / Notion integration, identity mapping, or HR system integration.
- Changing `generate-dashboard-dataset`, dashboard CSV / JSON schema, or runtime dependencies.

## Acceptance Criteria

- The three prototype paths are compared using the same evaluation criteria.
- The selected default path is explicit and consistent with `docs/requirements.md` and `docs/spec.md`.
- The static report and repository-local preview fallback roles are defined.
- Grafana-first does not imply production Grafana adoption, Azure Monitor adoption, or Langfuse replacement.
- The selected path uses the M2 four-table dashboard dataset and M3 synthetic output as its initial validation input.
- Drilldown references remain sanitized references only.
- Sprint5 / later implementation handoff is bounded and does not include external outcome linkage implementation.

## Verification

- Documentation review only.
- `git diff --check`.
- No product code, dependency, build, test, live Grafana, live Langfuse, Copilot, external API, or network validation was required for M4.
