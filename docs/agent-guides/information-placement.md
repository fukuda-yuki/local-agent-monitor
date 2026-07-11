# Information Placement Guidance

This guide defines which kind of information each artifact primarily
carries. It is repository guidance, not product behavior.

Each artifact records the knowledge that only it can keep true.
Duplicated information rots: a comment that restates code drifts from the
code, and a commit message that restates the diff adds nothing the diff
does not already show.

| Artifact | Primarily records | Passing test |
| --- | --- | --- |
| Production code | **How** | Structure, naming, and types show how the requirement is met. |
| Test code | **What** | The test pins observable behavior under stated conditions. |
| Commit log | **Why** | The body explains why the change was needed and what was wrong before. |
| Code comments | **Why not** | The comment keeps a constraint or rejected alternative the code cannot show. |

## Production Code: How

Express the how through names, types, and responsibility split. Do not
narrate code in comments.

```csharp
// Bad: the comment restates the next line and will drift from it.
// Filter to active users only.
var activeUsers = users.Where(x => x.IsActive);

// Good: names and structure carry the how; no comment needed.
var activeUsers = users
    .Where(user => user.IsActive)
    .OrderBy(user => user.LastName)
    .ToList();
```

## Test Code: What

Name and assert the externally observable contract, not the
implementation.

- Keep the existing naming convention `Method_Scenario_ExpectedOutcome`
  (e.g. `Evaluate_ProjectionWorkerAbsent_IsNotReadyWithProjectionWorkerMissing`).
- Derive scope from `docs/requirements.md`, `docs/spec.md`, and the
  relevant `docs/specifications/` file.
- Assert behavior and contracts (routes, status codes, thresholds/units,
  rendered output) — not internal call counts or private structure — so
  refactors do not break tests whose behavior still holds.

## Commit Log: Why

The pinned title format `<work item>: <type>(<scope>): <subject>` carries
the searchable what. The body records the why — the one thing `git diff`
cannot reconstruct.

```text
Sprint8 ingestion hardening: fix(monitor): reset connection state before re-ingest

Reconnect processing could submit the final buffered span twice because
the previous connection state remained active. Reset the state before
starting a new ingestion cycle.
```

- `feat`, `fix`, `refactor`, `perf`: body required — why the change was
  needed and what was wrong before.
- `docs`, `chore`, `test`, `style`: body optional when the title already
  carries the why.
- Never write work-log messages ("Update IngestionService.cs",
  "Fix tests"); they cannot reconstruct the decision from history.

## Code Comments: Why Not, And Constraints Code Cannot Show

Comments record rejected alternatives, external constraints, and
invariants — what stops a future developer from "simplifying" the code
back into a past bug. Real examples already in this repository:

```powershell
# Whitespace-only so it is safe without an .editorconfig; always exits 0 so a
# formatting hiccup never fails the edit.
```

```powershell
# Any HTTP response (including 503 on an empty store) proves the monitor is up.
```

## Primary, Not Exclusive

The rule assigns each artifact's primary content, not its only content:

- Code also expresses what through naming.
- Tests may keep a short why (e.g. a regression pointer) in the name or a
  brief comment.
- Commit titles need what for searchability — the pinned format already
  enforces this.
- Comments also carry non-obvious why, external constraints, and
  invariants, not only rejected alternatives.

## Where This Rule Is Applied

- `AGENTS.md` — the four-line principle (always loaded).
- `.claude/skills/commit` — commit body requirement at commit time.
- `.claude/agents/test-writer.md` — behavior/contract assertion rule.
- `docs/agent-guides/review-workflow.md` — review perspective.
