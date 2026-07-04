# M5 Review: Review and Handoff

## Status

Complete.

## Scope Reviewed

- Sprint4 M1 dashboard requirements.
- Sprint4 M2 dashboard dataset contract.
- Sprint4 M3 synthetic dashboard dataset generator and review notes.
- Sprint4 M4 dashboard prototype path decision.
- `docs/requirements.md`, `docs/spec.md`, `docs/task.md`, and the Sprint4 README.

## Findings

- No blocking issues found.
- Spec compliance: Sprint4 remains a dashboard requirements and prototype-planning sprint. It does not add repository-changing auto-improvement, production dashboard hosting, external ETL, or outcome API integration.
- Requirements coverage: M1-M4 define dashboard purpose, non-goals, primary users, decision targets, views, panels, metrics, dimensions, filters, data sources, drilldown references, and validation boundaries.
- Dataset contract: M2 and M3 preserve the four logical dashboard tables: `dashboard_run_summary`, `dashboard_operation_summary`, `dashboard_candidate_summary`, and `dashboard_collection_health`.
- Prototype path: Grafana JSON dashboard remains the default later implementation path, with M3 synthetic dashboard data as the first validation input.
- Fallback roles: static report remains the deterministic, service-free fallback for metric correctness and review packets; repository-local preview remains a last-resort visual aid and must not become a custom trace viewer or product UI.
- Safety boundary: dashboard datasets and prototype documents continue to exclude raw prompt / response / tool arguments / tool results, credentials, Base64 authorization headers, real identity values, sensitive bundle content, and sensitive bundle paths.
- Drilldown boundary: dashboard artifacts may carry sanitized references such as trace ids, candidate ids, auto-decision ids, proposal ids, sanitized `evidence_ref`, and `sensitive_bundle_present`, with detailed investigation left to Langfuse trace viewer, raw store, or repository-external sensitive bundles.

## Sprint5 / Later Handoff

- Sprint5 candidate: implement a Grafana JSON dashboard artifact against the M2 / M3 four-table dataset.
- Sprint5 candidate: define a minimal local import or data source validation workflow for the Grafana JSON dashboard using synthetic dashboard data.
- Later fallback candidate: add static report generation only if Grafana data source setup is unavailable or deterministic review packets are needed.
- Later optional candidate: add candidate timestamp fields or a separate timestamp source if backlog age becomes important enough to make nullable values insufficient.
- Later optional candidate: use repository-local preview only if Grafana import or data source setup blocks basic visual validation.
- Out of scope until separately specified: production Grafana / Azure Managed Grafana / self-host Grafana hosting, Application Insights / Tempo / Loki / Mimir backend selection, external ETL, GitHub / Notion API integration, identity mapping, shared dashboard access control, and organization usage / ROI dashboarding.

## Outcome Linkage Tiers

| Tier | Meaning | Examples | M5 decision |
| --- | --- | --- | --- |
| Tier 0 | Not part of the Sprint4 initial dashboard | External API ingestion, production GitHub / Notion / HR integration, identity mapping, org usage / ROI dashboard | Keep out of Sprint4 outputs |
| Tier 1 | Future planning candidate with sanitized or manual references | PR / issue / CI / review placeholders, manually sanitized outcome references | May be considered for Sprint5 planning, without external API commitment |
| Tier 2 | Requires product and security decisions before implementation | Shared dashboard with team / department dimensions, real GitHub / Notion ingestion, retention and access-control policy | Block until policy, masking, retention, and user communication are specified |
| Tier 3 | Explicit non-goal for this product direction | Individual productivity scoring, labor monitoring, rankings, HR data correlation, executive usage dashboard | Do not implement under Sprint4 or initial Sprint5 dashboard work |

## Residual Risks

- Grafana implementation still needs a concrete JSON import and data source workflow.
- Operation classification is currently synthetic-driven; live Copilot / agent span shape coverage should be revisited only when later dashboard implementation needs it.
- Backlog age remains nullable because existing candidate records do not carry generated timestamps.
- Outcome linkage remains placeholder-only until privacy, identity, retention, access-control, and external integration decisions are made.
- Shared or real-data dashboards require a separate retention, masking / redaction, access-rights, and user-communication specification before use.

## Self-review Follow-up

- Finding: Outcome Linkage tiers were recorded in `docs/spec.md` and this review note, but `docs/requirements.md` still only said M5 would tier them later. This was valid because requirements are the higher source of truth. Fixed by recording the M5 tier boundary in `docs/requirements.md`.
- Finding: `docs/spec.md` still described Sprint4 as an active requirements-definition phase after M5 closeout. This was valid because implementers read the current phase section first. Fixed by marking Sprint4 complete and naming the M5 handoff boundary as part of the Sprint4 deliverable.
- Finding: The Sprint4 README still described M5 review and handoff in future tense after closeout. This was valid. Fixed by changing the M5 summary to completed wording.
- Finding: The static report handoff entry could be read as an equal-priority Sprint5 implementation candidate. This was valid. Fixed by making it a later fallback candidate that is conditional on Grafana data source setup or deterministic review packet needs.

## Verification

- Documentation review only.
- `git diff --check`
- No product code, dependency, build, test, live Grafana, live Langfuse, Copilot, external API, or network validation was required for M5.
