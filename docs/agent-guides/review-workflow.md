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

Review the final diff against [Simplicity And Change Scope](repository-workflow.md#simplicity-and-change-scope) and the [new-test gate](repository-workflow.md#new-test-gate). Unnecessary behavior, unused machinery, duplicated coverage, and extra artifacts remain blocking findings.

Use [Scope and replanning](repository-workflow.md#scope-and-replanning) for newly discovered changes. A necessary file or line within the same authorized outcome is not a reason to remove the fix or request approval solely because it was unplanned. Review Sub-agent output by the same standard.

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

Follow the single [delegation policy and Codex reviewer delivery procedure](repository-workflow.md#artifacts-delegation-and-worktrees). Review depth does not itself require a Sub-agent.
Regardless of who performs the review, inspect the diff directly, compare it with the current sources of truth, and apply the validation and result-reuse rules in [Impact-Based Validation](repository-workflow.md#impact-based-validation).
Do not describe a primary-agent self-review as independent or delegated review.
