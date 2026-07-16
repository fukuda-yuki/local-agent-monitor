# Issue #102 Task 1 Brief — Canonical Specification

## Identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability`
- Branch: `codex/issue-102-doctor-core`
- Starting HEAD: `0a2542d9e973429e39a4986c823996b057fb607b`
- Preferred runtime: GPT-5.6 Luna with xhigh reasoning. The dispatch API cannot enforce or verify the model selection.
- Report path: `.superpowers/sdd/issue-102-task-01-implementer-report.md`

## Purpose

Promote the approved Issue #102 Doctor Core design and execution plan into the canonical product requirements, specifications, architecture, and decision log before any production code is changed.

## Scope

Edit only:

- `docs/requirements.md`
- `docs/spec.md`
- `docs/specifications/interfaces/first-trace-doctor.md` (new)
- `docs/specifications/interfaces/config-cli.md`
- `docs/specifications/README.md`
- `docs/architecture.md`
- `docs/decisions.md` (new D060)

Read in full before editing:

- `AGENTS.md`
- `docs/agent-guides/repository-workflow.md`
- `docs/requirements.md`
- `docs/spec.md`
- relevant interface/security/persistence specifications
- `docs/architecture.md`
- `docs/decisions.md`
- `docs/superpowers/specs/2026-07-16-issue-102-doctor-core-design.md`
- `docs/superpowers/plans/2026-07-16-issue-102-doctor-core.md`

Use the repository `spec-update` skill and comply with its checklist.

## Required Contract

Specify the shared `DoctorResult` (`doctor.v1`), 12 explicit-known/unknown fact families, all 20 fixed states and exact severity/retryability/next-action mapping, deterministic primary/terminal/advisory ordering, and v1 reason code equality. Specify exact limits, UUIDv7/timestamp forms, CLI commands and exit codes, HTTP routes/statuses/security/no-store rules, and Doctor v1 SQLite tables/lifecycle/atomicity/migration/degradation behavior from the approved plan.

D060 must fix:

- one shared Doctor domain across direct/CLI/HTTP;
- explicit bounded verification instead of latest-trace guessing;
- Doctor verification isolation from D051 readiness and host startup;
- source-specific fact/candidate producer handoff to #103/#104 without new enums;
- proxy/UI ownership remaining in #105.

Preserve D059: unrelated schema drift alone must not fail exact verification.

## Non-Scope

- No production or test code.
- No source-specific adapters, live first trace, proxy DTO, Razor, JavaScript, Canvas, or UI.
- No dependency, schema file, fixture, migration implementation, issue status, push, PR, or `main` integration change.
- Do not infer behavior from sprint history when canonical sources disagree; report any conflict before editing that contract.

## Constraints

- Agent-facing text is English and existing target-document tone is preserved.
- Public/security behavior is specified before code.
- Avoid duplicating normative text inconsistently: requirements state product outcomes, `docs/spec.md` summarizes cross-cutting behavior, and the new interface file owns the detailed contract.
- Do not place secrets, real paths, raw payloads, or PII in examples.
- Use `apply_patch` for edits.
- Do not commit. Leave a reviewable working-tree diff for the independent reviewer.
- Do not edit the durable ledger or this brief.

## Completion Conditions

- Every approved public-contract item is discoverable in a canonical source.
- `docs/specifications/README.md` indexes the new interface.
- `config-cli.md` includes all five commands, strict parsing/body bounds, projections, and exit mapping.
- architecture dependency direction remains acyclic and identifies Doctor ownership.
- D060 is numbered and cross-referenced consistently.
- No out-of-scope files are modified.
- Self-review finds no placeholders, contradictions, silent compatibility behavior, or main-integration claim.

## Verification

```powershell
git diff --check
rg -n "doctor.v1|/api/doctor|first_trace_ready|D060" docs
git status --short --branch
```

## Report

Write the report path above with: identity verified, files changed, contract coverage, exact commands/results, self-review verdict, unresolved findings, and unverified #103/#104/#105 interfaces. Then send the root agent a concise completion message. Do not commit.
