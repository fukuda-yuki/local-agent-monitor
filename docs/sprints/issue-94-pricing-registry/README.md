# Issue #94 Versioned Pricing Registry And Estimator

Issue: `#94 [P2] Implement versioned pricing registry and provider estimation engine`

Kickoff revision: `07dc219c4f5c5ef56e7810a23c6466a52e90aa97`

Branch: `codex/issue-94-pricing-registry`

## Scope

- Canonical `pricing.registry.v1` bundled and local-override documents.
- Canonical ordered `pricing.catalog-snapshot.v1` bytes and estimate-bound
  catalog digest.
- Exact effective-date/provider/model/billing-mode selection.
- GitHub Copilot and Claude Code estimation routes.
- Codex App and subscription/custom/unknown fail-closed reasons.
- Independent decimal components, explicit coverage, and no intermediate
  rounding.
- Canonical immutable `pricing.estimate.v1` records and append-only
  recalculation linkage.
- Strict bounded catalog-snapshot and canonical estimate consumers for #95
  persistence handoff.
- USD/two-minor-unit registry v1 admission and executable half-even display
  projection.
- Deterministic synthetic/pinned fixtures and explicit Issue #91 extension
  handling.

## Explicit Exclusions

- Issue #95 UI, HTTP, CLI, persistence, and dashboard projection (the strict
  reload consumer is the only #95-facing seam delivered here).
- Budget alerts and notifications.
- Invoice reconciliation, enterprise/custom price inference, currency
  conversion, runtime price fetching, scraping, and purchase/plan changes.
- Quality/effect claims.
- Issue #80 receipt/lifecycle changes.
- Migration of the legacy static-dashboard calculator.

## Milestone

- [M1 pricing registry and estimator](milestones/M1-pricing-registry/plan.md)

Validation evidence is recorded in
[M1 evidence](milestones/M1-pricing-registry/evidence.md) after commands run.
The immutable-record and #95 integration boundary is recorded in
[the handoff](milestones/M1-pricing-registry/handoff.md).
