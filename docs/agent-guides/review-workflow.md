# Review Workflow Guidance

This guide owns review and reporting rules, not product behavior.

## Review Depth

Scale review to risk: self-review is sufficient for minor reversible documentation/formatting edits; use deeper inspection for behavior, interfaces, security/data boundaries, workflows, or broad refactors. Depth alone does not require delegation.

## Required Perspectives

Inspect the actual diff against the current owning specification and requested outcome. Check functional correctness, relevant regressions/edge cases, maintainability, and unnecessary complexity. Include data safety for raw/credential/artifact changes and documentation consistency for changed contracts or user workflows. Apply [information placement](information-placement.md) to edited artifacts.

## Over-implementation blocking check

Apply [change scope](repository-workflow.md#simplicity-and-change-scope) and the [new-test gate](repository-workflow.md#new-test-gate): unnecessary behavior, unused machinery, duplicated coverage, and extra artifacts are blocking findings. A necessary unplanned file within the same authorized outcome is not one; use [scope and replanning](repository-workflow.md#scope-and-replanning). Apply the same standard to worker output. Current-specification consistency is owned here, not by an additional generic reviewer.

## Finding Placement

Keep task findings and closeout in the PR review, active Issue comment, or final response, never a repository review/status artifact. Only durable architectural or policy rationale not reconstructable from code/specifications belongs in the owning document or `docs/decisions.md`.

## Self-Review Format

Report the scope and files/behavior actually inspected, relevant validation commands/results, evidence-backed defects (or none found), and residual risks/unverified scope. Findings are confirmed defects; successful comparisons belong in a brief coverage summary, not individual `OK` findings. Unread, inaccessible, truncated, or unexecuted required evidence remains unverified, not a successful check or automatically a specification defect.

A report can find no defects in the inspected surface while retaining an unverified required boundary. It must not turn that partial result into complete verification or a passed required gate. Keep static inspection distinct from runtime execution and primary-agent self-review distinct from independent review. Update the owning specification when an authorized product-contract change is identified.

## Review Execution

Use the single [delegation and reviewer-delivery policy](repository-workflow.md#artifacts-delegation-and-worktrees) and [validation/result-reuse rules](repository-workflow.md#impact-based-validation). Assess returned findings against the diff and current authority before integrating; do not claim delegated work or permission enforcement that did not occur.
