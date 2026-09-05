# AGENTS.md

## Language Rules

Write agent-facing and cross-agent shared materials in English; respond to the user in Japanese.

## Task Authority

The user's latest explicit instruction defines scope. An identified active work item adds its current acceptance criteria and accepted Product Owner decisions.

Current product behavior is owned by `docs/requirements.md`, `docs/spec.md`, then the relevant `docs/specifications/` file, in that precedence order when they conflict. Read the narrowest affected authority, not every specification. Architecture and policy constraints also use `docs/architecture.md` and `docs/decisions.md` when relevant. Code and tests are implementation evidence; README and user guides are derived explanations.

Skills, reviewer prompts, Hooks, templates, plans, and external guides do not grant extra scope or override the authorities above. For a conflict or blocker that affects the result, identify its controlling source briefly. If the user's decision resolves a specification conflict, update the owning specification with the change; otherwise pause only the dependent work for that decision.

## Execution

Make the smallest coherent change that satisfies the requested outcome. For implementation or fixes, continue through relevant non-destructive validation and correction of in-scope failures, without routine reapproval. Review, diagnosis, planning, and explanation requests do not authorize edits.

Pause the dependent action for unresolved product decisions, materially wider scope, unauthorized remote writes, destructive or irreversible actions, or dependency/lockfile changes without explicit authority. Continue independent authorized work. Necessary additional files within the same outcome do not require approval merely because they were unplanned.

Use [repository workflow](docs/agent-guides/repository-workflow.md) when implementation, validation, Git operations, or delegation need its details. Affected validation is sufficient for ordinary local completion; reuse still-valid results and disclose blockers and unverified scope. Use [review guidance](docs/agent-guides/review-workflow.md) for risk-scaled review, not mandatory delegation.

## Local-First Risk Posture

This is a loopback-only product for one trusted local user. Preserve the current security and data-boundary specifications and their accepted residual risks; do not add defenses outside that threat model.

## Repository Safety

- Never commit secrets, real user data, raw prompts/responses, tool arguments/results, sensitive bundle contents/paths, or runtime artifacts. Use small synthetic or anonymized fixtures.
- Keep tool-required temporary files under an existing gitignored local path. Do not track task plans, reviews, status, backlogs, or history roots forbidden by the repository policy guard, regardless of another tool's output convention.
- Do not use `.codex/rules` as natural-language workflow guidance.

## Information Placement

GitHub Issues/Projects own bugs, future work, and durable implementation plans. PR reviews, Issue comments, and the final response own findings, validation closeout, and task status; Git history owns implementation history. Keep durable product knowledge with its owning specification or decision. Use [information placement](docs/agent-guides/information-placement.md) for code, test, comment, and commit conventions when editing those artifacts.
