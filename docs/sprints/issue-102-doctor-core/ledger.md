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
| Task 1 canonical specification | Complete | `0a2542d..287f0c3` | Documentation checks: `git diff --check` PASS; required Doctor marker search PASS; exact structure audit PASS (12 fact families, 20 state tuples, 5 CLI commands, 5 HTTP routes) | Not run for this specification-only task; Task 0 baseline remains the last full result | Fresh implementer self-review PASS; independent review initially FAIL (5 Important), then PASS after correction (0 Critical/0 Important/0 Minor); root scope/identity/evidence check PASS | No unresolved Task 1 finding | #103/#104 producer behavior and #105 proxy/UI remain specified handoffs but unimplemented/unverified |
| Task 2 thin executable cross-surface slice | Complete | `287f0c3..80fd19e` | TDD RED: compiled test failed at real CLI dispatch, expected exit 3/actual 1; security-fix RED: headerless HTTP expected 200/actual 403; root final focused test PASS, 1 passed/0 failed/0 skipped | Root `dotnet build CopilotAgentObservability.slnx`: PASS, 9 projects, 0 warnings/0 errors; full solution tests not run for this task | Fresh implementer self-review PASS; independent review initially FAIL (1 Important/1 Minor), both corrected; re-review PASS (0 Critical/0 Important/0 Minor); root code/evidence check PASS | Remaining 19 states, full validation, store/lifecycle, migration, CLI/HTTP lifecycle, and safety matrix moved to Tasks 3-7 | #103/#104 source-specific producers and #105 proxy/UI remain intentionally unimplemented/unverified |
| Task 3 deterministic domain | Complete and integrated on feature branch | source `e00063f..6500866`; primary `c5bc9030..cc812a31` | Doctor domain focused PASS: 147/147; full Doctor PASS: 148/148; exact counterexample/cross-surface PASS: 92/92 | Solution build PASS, 0 warnings/0 errors | Independent review initially found unsafe-reference, required-property, lifecycle-validation, boundary, and leading-space URI issues; all corrected; final PASS (0 Critical/0 Important/0 Minor) | No unresolved Task 3 finding | Source-specific state enums and heuristic matching remain prohibited; #103/#104 supply only shared facts/candidates |
| Task 4 SQLite lifecycle and migration | Complete and integrated on feature branch | source `1fa434c..fca4c2e`; primary `851e1910..4671029d` | Doctor persistence PASS: 59/59; safety 26/26; migration 4/4; rollback 3/3; full Doctor 60/60 | Solution build PASS, 0 warnings/0 errors | Independent review initially found sanitizer, migration semantic/sentinel, rollback, and connection-cleanup gaps; all corrected; final PASS (0 Critical/0 Important/0 Minor) | Production must use the concrete completion callback and the same injected `TimeProvider`; Task 7 verified both | Source-specific observation wiring remains #103/#104; no public candidate surface exists |
| Task 5 Config CLI lifecycle | Complete and integrated on feature branch | source `fe38bb7..ac1a5c1`; primary `71bdf49b..6da6f823` | schema/optional 41/41; unsafe 60/60; boundaries 12/12; real commands 28/28; Doctor filter 152/152; Config CLI full 3,636/3,636 | Solution build PASS, 0 warnings/0 errors | Independent review initially found schema-ordering, optional-field, unsafe-reference, and command-boundary gaps; all corrected; final PASS (0 Critical/0 Important/0 Minor) | Production SQLite composition intentionally completed in Task 7 | #103/#104 do not add CLI candidate commands; #105 owns shared UI/proxy work |
| Task 6 Local Monitor Doctor API | Complete and integrated on feature branch | source `07e9cce..e607f5f`; primary `8fee2a16..52794b3` | body-boundary 2/2; Doctor HTTP 47/47; Local Monitor full 1,440/1,440 | Solution build PASS, 0 warnings/0 errors | Independent review found global Kestrel body-limit preemption; corrected with bounded per-route handling; final PASS (0 Critical/0 Important/0 Minor) | One pre-existing readiness observation race was isolated and not changed by Doctor work | No Razor, JavaScript, Canvas, proxy DTO, or public candidate route; those remain #105/non-scope |
| Task 7 production integration and hardening | Approved; ready for local commit | `52794b3` to the commit containing this row | Root fresh: Doctor 216/216; Config CLI Doctor 152/152; Local Monitor Doctor 49/49; prohibited wait/retry scan 0 matches; machine-local evidence path scan 0 matches | Implementer full solution PASS 5,294/5,294, 0 failed/0 skipped; root solution build PASS, 0 warnings/0 errors; pinned full validation will be repeated after Task 8/final review | Independent review initially FAIL (3 Important: observation trust gate, real production lifecycle matrix, local path evidence); all corrected; re-review PASS (0 Critical/0 Important/0 Minor); root diff/code/evidence check PASS | No unresolved Task 7 finding; final pinned validation and four whole-branch reviews remain | #103/#104 candidate producers are compile-shape only and not live-verified; #105 proxy/UI is absent and unverified by design; feature branch is not integrated to `main` |

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
