# Review Workflow Guidance

This guide defines how agents should review repository changes before declaring work complete.
It is repository guidance, not product behavior.

## Review Depth

Review depth should match risk.

Use a self-review for documentation-only, typo-only, formatting-only, and other minor reversible changes.
Use a deeper review for implementation changes, behavior changes, public interface changes, security-sensitive changes, data-safety boundaries, workflow changes, and broad refactors.

## Required Perspectives

Check these perspectives before completion:

- Spec compliance and functional correctness.
- Tests, edge cases, and regression risk.
- Maintainability, readability, and unnecessary complexity.
- Data safety when telemetry, raw content, credentials, generated artifacts, or dashboard publication are involved.
- Documentation consistency when product behavior, public interfaces, or user workflows change.
- Information placement: comments record constraints and rejected alternatives rather than restating code; commit bodies record why the change was needed (`docs/agent-guides/information-placement.md`).

## Over-implementation blocking check

Before completion, block the change unless:

- every changed file and behavior is necessary for the request, an acceptance criterion, or the affected contract;
- no unused abstraction, configuration, fallback, compatibility path, or parallel implementation remains;
- every new test covers changed observable behavior rather than duplicating existing coverage or backfilling an unrelated module;
- unplanned files, lines, artifacts, and Sub-agent output have been removed or returned to a scope decision; and
- no work was added only to make the result appear more complete.

This workflow owns current-specification consistency review. Do not duplicate that responsibility in another Sub-agent.

## Finding Placement

Put review findings in the Pull Request review, the active GitHub Issue comment, or the final response. Do not commit `review.md`, `fix-review.md`, traceability packages, validation closeout, or other task-specific review records.

Only a durable architecture or policy decision that cannot be reconstructed from the current code and specification belongs in the owning current document or `docs/decisions.md`.

## Self-Review Format

When reporting a self-review, include:

- scope reviewed;
- files or behavior checked;
- validation commands and results;
- findings, or a clear statement that no blocking issues were found;
- residual risks or unverified scope.

Keep review notes factual. Update the current owning specification when review identifies a product-contract change.

## Review Execution

Follow the subagent and delegation policy in `docs/agent-guides/repository-workflow.md`.
Regardless of who performs the review, inspect the diff directly, compare it with the current sources of truth, and run the applicable validation commands.
Do not describe a primary-agent self-review as independent or delegated review.
