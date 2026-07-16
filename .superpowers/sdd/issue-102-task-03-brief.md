# Issue #102 Task 3 Brief — Deterministic Domain Engine

## Identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability\.worktrees\issue-102-domain`
- Branch: `codex/issue-102-domain`
- Starting HEAD: `80fd19ebf497a092a4ae112e3321bcf70bcdd7a4`
- Preferred runtime: GPT-5.6 Luna with xhigh reasoning; dispatch cannot enforce or verify it.
- Report path: `.superpowers/sdd/issue-102-task-03-report.md`
- Report destination: `/root`

## Purpose

Implement the complete source-independent Doctor catalog, validation, deterministic evaluator, and serialization rules from the canonical Issue #102 contract.

## Scope and Ownership

- Own `src/CopilotAgentObservability.Doctor/**` except avoid unnecessary redesign of verification-store behavior.
- Own new domain-focused files in `tests/CopilotAgentObservability.Doctor.Tests/**`: `DoctorCatalogTests`, `DoctorEvaluatorTests`, `DoctorValidationTests`, and `DoctorDeterminismTests` plus narrowly shared synthetic fixtures/helpers.
- You may update the Doctor project/test project files only when required by this lane.
- Do not edit the existing Task 2 `DoctorCrossSurfaceContractTests.cs`, Config CLI, Local Monitor, Persistence.Sqlite, canonical specs, ledger, solution, or other projects.

Read AGENTS.md, repository workflow, TDD skill, requirements/spec, the complete `first-trace-doctor.md`, architecture/D060, existing Doctor source/tests, and this brief before editing.

## Required Behavior

- Preserve exactly 12 fact families with null-family and explicit `Unknown` semantics. Unknown may not become false/zero/supported/success/absence.
- Implement exactly the 20 fixed state tuples and numeric blocking precedence.
- If any blocker applies, emit blockers only in catalog order. Otherwise emit exactly `first_trace_ready` or `ready_no_real_trace`, then applicable content/sanitized/schema advisories in fixed order.
- `primary_state` is first blocker or terminal; advisories never primary.
- Implement the fixed partial projection: false/partial code, non-null evaluation, null primary, empty states, ordered nonempty missing families, null verification for direct evaluation.
- Implement applicability/cross-field rules, including known-unbound only for `session_unbound`, D059 unrelated schema drift advisory, and agent restart precedence.
- Require matching typed `real_source` observations for first-trace ingest/raw/projection/binding/completeness; synthetic probes cannot satisfy real receipt/binding/content.
- Validate schema version, source/adapter tokens, canonical lowercase UUIDv7, exact UTC round-trip timestamp serialization/input, evidence safety/length/distinct/order/cardinality, observation source/adapter/class/kind consistency, all family cross-field combinations, and no raw/PII/credential/path-bearing references.
- Enforce 16 observations/accepted references and source-neutral closed enums. Candidate maximum belongs to store tests but shared validation shape must permit it.
- Reject unknown/duplicate JSON properties, integer/noncanonical enum values, malformed/duplicate properties, and inconsistent inputs with fixed sanitized Doctor codes; never echo parser/exception/input text.
- JSON for equivalent input/result is byte deterministic. Human projection is bounded and consumes only `DoctorResult`.
- Do not add source-specific states, evidence kinds/classes, heuristic matching, fallback parsing, compatibility shims, dependencies, or current-clock/environment/database access to the evaluator.

## TDD and Completion

Write one failing behavior test at a time and observe the expected RED before production changes. At minimum cover every catalog tuple, precedence combinations, terminal/advisory order, all 12 unknown families, partial shape, real/synthetic separation, schema drift, every bound/lexical/cross-field rejection, duplicate JSON fields, deterministic repeated serialization, and unsafe-value negative cases.

Completion requires all named test classes pass, existing Task 2 cross-surface test remains green without edits, build has zero warnings/errors, diff is scoped, and self-review finds no contract gap.

## Verification

```powershell
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj --filter "FullyQualifiedName~DoctorCatalogTests|FullyQualifiedName~DoctorEvaluatorTests|FullyQualifiedName~DoctorValidationTests|FullyQualifiedName~DoctorDeterminismTests"
dotnet test tests\CopilotAgentObservability.Doctor.Tests\CopilotAgentObservability.Doctor.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
git status --short --branch
```

## Non-Scope and Commit

No SQLite/lifecycle implementation, CLI/HTTP expansion, source adapters, live trace, readiness change, proxy/UI, migration fixture change, push, PR, or main integration. Use no sleep/poll/retry.

Write the report with identity, RED/GREEN evidence, files, exact commands/results, contract matrix, self-review, unresolved items, and unverified Issue handoffs. Make one local commit after validation:

`Issue #102: feat(doctor): implement deterministic state engine`

Include a commit body explaining why the fixed domain was needed. Report the commit hash to `/root`.
