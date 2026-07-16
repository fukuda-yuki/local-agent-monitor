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
