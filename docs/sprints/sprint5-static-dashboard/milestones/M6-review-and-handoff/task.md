# M6: Review and Handoff

## Goal

Review Sprint5 static dashboard implementation, record the operating boundary, and hand off residual environment and follow-up work.

## Reviewed Scope

- M1 source boundary and allowed / disallowed dashboard data.
- M2 static artifact contract for `generate-static-dashboard`, `index.html`, `dashboard-data.json`, and Pages layout.
- M3 local static dashboard generator.
- M4 scheduled / manual GitHub Actions publish workflow.
- M5 real-data-shaped input staging and sanitization validation.

## Completion Summary

- Sprint5 implemented the static HTML dashboard path as the primary Grafana alternative for the initial always-on dashboard.
- `generate-static-dashboard` produces `index.html` and a sanitized `dashboard-data.json` from the dashboard dataset JSON.
- The dashboard supports initial client-side date, user, client, experiment, variant, status, search, and table sorting.
- GitHub Actions can generate a preview dataset from synthetic fixtures when no staged input exists, or use `artifacts/dashboard-input/measurements.json` with optional raw / candidate inputs when staged.
- Published artifact layout keeps `/latest/` and `/YYYY-MM-DD/` snapshots.
- Real-data-derived JSON inputs remain ignored in `artifacts/dashboard-input/`, while `artifacts/dashboard-input/README.md` documents the staging contract.

## Operating Boundary

- GitHub Pages access control, repository / organization Pages settings, and first live workflow execution remain environment tasks.
- Generated daily snapshots are written to `gh-pages` and Pages artifacts, not committed to `main`.
- Dashboard output may contain aggregate metrics, reference IDs, classification attributes, `user_id`, and `user_email`.
- Dashboard output must not contain raw prompt, response, system prompt, tool arguments, tool results, source fragments, credentials, authorization headers, sensitive bundle content, or sensitive local paths.
- Email / display name mapping, repository size monitoring for long-lived snapshots, and external outcome ingestion are follow-up candidates, not Sprint5 implementation commitments.

## Verification

- `dotnet build CopilotAgentObservability.slnx`
- `dotnet test CopilotAgentObservability.slnx`
- `git check-ignore -v artifacts/dashboard-input/measurements.json`
- PowerShell check that fails if `artifacts/dashboard-input/measurements.json` is not ignored or if `artifacts/dashboard-input/README.md` is ignored.

## Handoff

Sprint5 is closed after local build / test validation. The next work should start from environment validation of the Pages workflow and explicit product / security decisions for any broader sharing, identity mapping, or external outcome linkage.
