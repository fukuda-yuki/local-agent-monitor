# Dashboard Prototype Path

## Decision

Use a Grafana JSON dashboard as the default Sprint4 dashboard prototype path for later implementation work.

This is a prototype direction only. It does not select Grafana Cloud, Azure Managed Grafana, self-host Grafana, Application Insights, Tempo, Loki, Mimir, Azure Monitor Exporter, or any production backend.

The prototype should consume the existing M2 / M3 dashboard dataset shape:

- `dashboard_run_summary`
- `dashboard_operation_summary`
- `dashboard_candidate_summary`
- `dashboard_collection_health`

Langfuse remains the trace-detail drilldown path. Grafana panels should surface trends, distributions, outliers, and sanitized references; they should not display raw prompt / response / tool arguments / tool results.

## Comparison Criteria

| Criteria | Grafana JSON dashboard | Static report | Repository-local preview |
| --- | --- | --- | --- |
| Fit for M2 four-table dataset | Strong. Time series, tables, percentiles, and status distributions map naturally to the contract. | Medium. Good for validating calculations, weaker for interactive filtering. | Medium. Flexible, but risks creating a custom dashboard surface. |
| Fit for M3 synthetic validation | Strong. Can validate the intended panels against generated CSV / JSON fixtures. | Strong. Can validate generated metrics without a live dashboard stack. | Medium. Useful for visual inspection, but not needed to prove the data contract. |
| Setup cost | Medium. Requires a Grafana-compatible data source or import workflow during implementation. | Low. Can run from files and produce deterministic artifacts. | Medium. Requires local UI scaffolding or static assets. |
| External dependency risk | Medium. Prototype can be local, but Grafana-specific JSON and data source mapping are still required. | Low. No live service required. | Low to medium, depending on implementation. |
| Drilldown model | Strong. Panels can show `trace_id`, candidate ids, `auto_decision_id`, and sanitized `evidence_ref`. | Medium. References are visible, but drilldown is less interactive. | Medium. Can link references, but risks becoming trace viewer scope. |
| Safety boundary | Strong if limited to M2 / M3 dataset fields. | Strong if generated from M2 / M3 dataset fields. | Medium. Custom UI increases risk of scope creep into raw content display. |
| Sprint5 handoff value | Strong. Gives a concrete dashboard artifact target. | Medium. Good fallback for metric correctness and review packets. | Low to medium. Useful only if Grafana setup blocks visual review. |

## Selected Path

The selected default path is:

1. Generate M2 / M3 dashboard dataset files with `generate-dashboard-dataset`.
2. Use a Grafana JSON dashboard prototype as the primary visualization artifact.
3. Keep static report output as a fallback for deterministic, service-free review.
4. Avoid repository-local preview unless Grafana import / data source setup blocks basic visual validation.

## Initial Panel Mapping

| View | Prototype panels | Source table |
| --- | --- | --- |
| Run Overview | Run volume and status, latency distribution, token and cost trend, stuck and long-running runs | `dashboard_run_summary` |
| Agent / Tool Behavior | Top tools by count, top tools by total duration, tool reliability, subagent and approval waits | `dashboard_operation_summary` |
| Prompt / Skill / Instructions | Variant cost and token impact, variant failure and candidate impact | `dashboard_run_summary`, `dashboard_candidate_summary` |
| Baseline vs Variant | Matched task comparison, regression candidate list | `dashboard_run_summary`, `dashboard_candidate_summary` |
| Diagnosis / Improvement Loop | Candidate distribution, human review queue | `dashboard_candidate_summary` |
| Collection Health | Attribute completeness, normalization and mapping health | `dashboard_collection_health` |
| Outcome Linkage Candidate | Placeholder only | Future source, not part of M4 implementation path |

## Drilldown Boundary

Allowed dashboard references:

- `trace_id`
- `langfuse_trace_id`
- `measurement_record_ref`
- `diagnosis_candidate_id`
- `improvement_candidate_id`
- `auto_decision_id`
- `proposal_id`
- sanitized `evidence_ref`
- `sensitive_bundle_present`

Dashboard prototype artifacts must not include:

- raw prompt, response, system prompt, tool arguments, or tool results
- source code fragments or file contents from observed sessions
- credentials, secrets, tokens, API keys, passwords, or Base64 authorization headers
- real `user.id`, `user.email`, or personal identity mapping
- sensitive bundle content or local sensitive bundle paths

## Handoff

Later implementation work should start with the Grafana JSON dashboard path and use M3 synthetic dashboard data as the first validation input.

The static report path should be retained as a fallback when Grafana data source setup is not available or when deterministic review artifacts are needed.

Repository-local preview should remain a last-resort visual aid and must not become a custom trace viewer or product UI.

