# Issue #102 Task 1 Review Brief — Canonical Specification

## Identity

- Worktree: `C:\Users\mwam0\.codex\worktrees\0664\copilot-agent-observability`
- Branch: `codex/issue-102-doctor-core`
- Review HEAD: `0a2542d9e973429e39a4986c823996b057fb607b` plus the uncommitted Task 1 diff
- Preferred runtime: GPT-5.6 Luna with xhigh reasoning. The dispatch API cannot enforce or verify the model selection.
- Implementer brief: `.superpowers/sdd/issue-102-task-01-brief.md`
- Implementer report: `.superpowers/sdd/issue-102-task-01-implementer-report.md`
- Review report: `.superpowers/sdd/issue-102-task-01-review-report.md`

## Purpose

Independently determine whether the uncommitted Task 1 canonical specification diff is complete, internally consistent, implementation-ready, and scoped to Issue #102.

## Scope

Read AGENTS.md, repository workflow and review guidance, the approved Doctor design and implementation plan, the implementer brief/report, all seven changed canonical documents, relevant security/persistence/readiness specs, D051, D059, and the complete uncommitted diff including the untracked new interface file.

Review:

- source-of-truth placement and cross-document consistency;
- the shared DTO/producer/consumer ownership and dependency direction;
- 12 explicit-known/unknown fact families;
- exact 20-state code/severity/retryability/next-action catalog;
- deterministic blocking/terminal/advisory order, v1 reason equality, partial input, and synthetic/real separation;
- every approved limit, UUIDv7/timestamp form, and no-leak boundary;
- five CLI commands, strict parsing/body limits, human/JSON projection, and exit mapping;
- five HTTP routes, status mapping, no-store, loopback/Host/same-origin/CSRF;
- Doctor v1 schema ownership, candidates, CAS, atomicity, historical v1-v4 migration/reopen/rollback, busy/unavailable degradation;
- unchanged D051 readiness and D059 exact-binding behavior;
- #103/#104 producer handoff and #105 proxy/UI non-ownership;
- unnecessary scope, accidental compatibility behavior, contradictions, and testability.

## Non-Scope

- Do not edit canonical files, production code, tests, ledger, plan, or implementer report.
- Do not implement fixes, commit, push, create a PR, change issue status, or integrate `main`.
- Do not review later Issue implementations as if they were present.

## Constraints

- This is a read-only product/specification review. Write only the review report path.
- Findings must cite exact file and line and be classified Critical, Important, or Minor.
- Do not approve based only on the implementer report. Inspect the actual content and compare it to the approved contract.
- A missing required contract, public ambiguity that would permit incompatible implementations, security-boundary omission, or D051/D059 regression is at least Important.
- Distinguish blocking findings from optional polish.

## Completion Conditions

- Every contract family above is explicitly checked against the actual diff.
- The report gives a clear `PASS` or `FAIL` verdict.
- `PASS` requires no Critical or Important findings. Minor findings may be accepted only with rationale.
- The report lists exact commands/results and later-Issue interfaces that remain intentionally unverified.

## Verification

```powershell
git diff --check
rg -n "doctor.v1|/api/doctor|first_trace_ready|D060" docs
git status --short --branch
```

Also inspect the untracked new specification directly and count/compare the exact 20 catalog rows, 5 CLI commands, and 5 HTTP routes. Use diagnostic commands as needed; do not modify source files.

## Report

Write `.superpowers/sdd/issue-102-task-01-review-report.md` with identity, reviewed evidence, requirement-by-requirement verdict, findings, command results, final verdict, and unverified #103/#104/#105 interfaces. Then report to `/root`.
