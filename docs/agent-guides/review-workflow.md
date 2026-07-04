# Review Workflow Guidance

This guide defines how agents should review repository changes before declaring work complete.
It is repository guidance, not product behavior.

## Review Depth

Review depth should match risk.

Use a self-review for documentation-only, typo-only, formatting-only, and other minor reversible changes.
Use a deeper review for implementation changes, behavior changes, public interface changes, security-sensitive changes, data-safety boundaries, workflow changes, and broad refactors.

Codex cannot assume subagents are available.
If subagents are unavailable, perform the review in the main chat and be explicit about the limits of that review.

## Required Perspectives

Check these perspectives before completion:

- Spec compliance and functional correctness.
- Tests, edge cases, and regression risk.
- Maintainability, readability, and unnecessary complexity.
- Data safety when telemetry, raw content, credentials, generated artifacts, or dashboard publication are involved.
- Documentation consistency when product behavior, public interfaces, or user workflows change.

## Preserved Review Records

Create a preserved review record only when the active work item calls for one or the user asks for one.
Use the active work item's review location.
If the active work item or review location is unclear, ask before creating the record.

For ordinary changes, a concise final response with the review performed and validation results is enough.

## Self-Review Format

For a recorded self-review, include:

- scope reviewed;
- files or behavior checked;
- validation commands and results;
- findings, or a clear statement that no blocking issues were found;
- residual risks or unverified scope.

Keep review notes factual.
Do not use sprint-local review notes to introduce new product behavior.

## Subagent-Independent Practice

When the user explicitly requests subagents and the active surface supports them, follow `.agents/skills/codex-subagent-dispatch/SKILL.md`.
Otherwise, do not claim delegated review.

For main-chat review, inspect the diff directly, compare it to the current sources of truth, and run the applicable validation commands.
