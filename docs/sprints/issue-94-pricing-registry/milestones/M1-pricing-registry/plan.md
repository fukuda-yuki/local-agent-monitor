# M1 Pricing Registry And Estimator Plan

## Objective

Implement Issue #94 as a standalone deterministic pricing domain without
absorbing Issue #95 presentation/persistence or changing existing alert and
dashboard authorities.

## Inputs And Authority

- GitHub Issue #94 body and kickoff comment.
- Parent Issue #60 P2 handoff.
- Current `docs/requirements.md`, `docs/spec.md`, and relevant interface,
  architecture, decision, security, #61, #80, and #91 contracts.
- Reviewed official GitHub pricing/billing/plan pages on 2026-07-24.
- Anthropic official price-list document dated 2026-05-27, used as a
  conservative local effective boundary.

## Design

1. Add `pricing.registry.v1` JSON Schema and a deliberately narrow embedded
   reviewed registry for GitHub Copilot GPT-5 mini and canonical Anthropic API
   model `claude-sonnet-4-6`.
2. Add a dependency-free lower-level `CopilotAgentObservability.Pricing`
   library. It validates bundled/override append-supersede chains and selects
   one exact entry by provider, billing mode, pricing route, model/declared
   alias, and session timestamp.
3. Accept nullable, provenance-bearing usage categories. Estimate each
   rate-backed category independently with decimal arithmetic and no
   intermediate rounding.
4. Emit canonical immutable `pricing.estimate.v1` records with explicit
   status, coverage, reasons, component/rate provenance, registry provenance,
   display-rounding policy, and predecessor linkage.
5. Export the complete bundled-first/caller-ordered calculation catalog as
   canonical `pricing.catalog-snapshot.v1`, bind its SHA-256 into each estimate,
   and expose strict bounded snapshot and estimate consumers that validate
   canonical bytes, recomputed identity, and exact recalculation for #95.
6. Keep the legacy dashboard calculator untouched and mark it
   non-authoritative. Add no SQLite component/migration, route, UI, alert,
   notification, or Issue #80 schema change.

## TDD Checklist

- [x] RED: registry rejects ambiguous overlap and accepts explicit
  append/supersede override.
- [x] RED: exact canonical/alias/date matching; no case/fuzzy/latest fallback.
- [x] RED: complete token estimate has exact unrounded independent components.
- [x] RED: missing category is partial/null, explicit zero is estimated zero.
- [x] RED: request/credit multiplier is deterministic.
- [x] RED: unknown model/mode/date, subscription/custom/Codex route reasons.
- [x] RED: stale/partial-source downgrade.
- [x] RED: recalculation is a distinct record and canonical replay is
  byte-identical.
- [x] RED: canonical output has no quality/effect/currency-conversion field.
- [x] RED: strict consumer rejects unknown/noncanonical/tampered records.
- [x] RED: full catalog snapshot digest binds unselected entries and preserves
  document/entry order; strict snapshot reload rejects drift.
- [x] RED: exact decimal component/aggregate and request-credit operations fail
  closed when the result is not exactly representable.
- [x] RED: provider-route, alias-supersession, numeric, date, source-safety,
  completeness, immutability, schema-drift, and display-rounding regressions.
- [x] GREEN: implement only enough domain code and bundled data for these
  contracts.
- [x] REFACTOR: centralize validation, fixed ordering, and canonical writing
  without changing observable behavior.

## Validation

Run from repository root, stopping rather than substituting if a required
command fails:

```powershell
dotnet test tests\CopilotAgentObservability.Pricing.Tests\CopilotAgentObservability.Pricing.Tests.csproj
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Validate the registry fixture against its JSON Schema with the repository's
available JSON Schema test convention if present; otherwise test the equivalent
closed semantic contract through the production loader and record that no
alternate command was substituted.

## #91 And #95 Handoff

Issue #94 was never a `not_available` entry in the Issue #91 future registry,
so leave that registry unchanged. The #91 matrix contract requires an
immutable exact candidate SHA; this explicitly no-commit worktree must not
fabricate one. Preserve the proposed automated invariant/evidence scope in the
handoff for an integration owner to pin if an active domain row is required.
Handoff to #95 includes only the canonical record/registry/snapshot contracts,
immutable predecessor semantics, explicit reasons, and the legacy-dashboard
incompatibility. #95 must choose its own storage/UI and migration behavior.
