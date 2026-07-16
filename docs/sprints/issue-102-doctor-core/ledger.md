# Issue #102 Doctor Core Durable Ledger

This ledger is historical implementation and review evidence. Current product
behavior belongs in `docs/requirements.md`, `docs/spec.md`, and the canonical
interface specifications.

## Branch Identity

- Branch: `codex/issue-102-doctor-core`
- Base: `8940b34f4e031b894705682dc50c079a9ed5c180`
- Main integration: not performed or claimed

## Task Ledger

| Task | State | Commit range | Focused tests | Full validation | Review | Unresolved items | Unverified Issue interfaces |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Design and contract boundary | Approved and recorded | base `8940b34f`; design commit is the commit containing this row | Not run; documentation-only design step | Not run; implementation has not started | Root self-review PASS: no placeholders, contradictions, unresolved scope, or repository-unsafe local path remains | Canonical specification promotion and implementation plan remain | #103/#104 fact producers and #105 proxy/UI remain intentionally outside #102 |
| Task 0 execution preflight and plan | Complete | `3e0760b..0a2542d` | Not applicable; planning checkpoint | `dotnet build CopilotAgentObservability.slnx`: PASS, 0 warnings/0 errors; Playwright Chromium bootstrap: PASS; `dotnet test CopilotAgentObservability.slnx`: PASS, 4,877 passed/0 failed/0 skipped | Root evidence and documentation review PASS; `git diff --check` PASS | The orchestration API exposes no model/reasoning selector, so the requested GPT-5.6 Luna xhigh preference can be stated but not enforced or verified | #103/#104 fact/candidate producers and #105 proxy/UI consumer remain intentionally unverified |
| Task 1 canonical specification | Approved; ready for local commit | `0a2542d` to the commit containing this row | Documentation checks: `git diff --check` PASS; required Doctor marker search PASS; exact structure audit PASS (12 fact families, 20 state tuples, 5 CLI commands, 5 HTTP routes) | Not run for this specification-only task; Task 0 baseline remains the last full result | Fresh implementer self-review PASS; independent review initially FAIL (5 Important), then PASS after correction (0 Critical/0 Important/0 Minor); root scope/identity/evidence check PASS | No unresolved Task 1 finding; production DTO/test evidence begins in Task 2 | #103/#104 producer behavior and #105 proxy/UI remain specified handoffs but unimplemented/unverified |

## Evidence Rules

- Record exact commands and observed counts; do not replace a failed required
  command with a different command.
- Record implementer and independent reviewer verdicts separately.
- Record atomicity, rollback, stale-state, concurrency, migration, security,
  and cross-Issue findings even when they are negative evidence.
- Do not commit raw prompts/responses, tool bodies, PII, credentials, tokens,
  sensitive local paths, runtime databases, logs, or generated artifacts.
- Distinguish verified feature-branch completion from observed integration into
  `main`.

## Execution Constraints

- Subagents receive an explicit preference for GPT-5.6 Luna xhigh, but the
  available dispatch API does not expose a model or reasoning selector.
- Each implementation report must identify worktree, branch, starting HEAD,
  final commit, focused commands, results, unresolved findings, and interfaces
  left for later Issues.
- Root verifies branch identity before dispatch, after each handoff, and before
  integration. Implementer reports are evidence inputs, not completion proof.
