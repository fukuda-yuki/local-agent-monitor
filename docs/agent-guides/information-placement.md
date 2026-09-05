# Information Placement Guidance

Each artifact primarily keeps the knowledge it can maintain accurately. This is repository guidance, not product behavior; reference an owner instead of duplicating its normative text.

| Artifact | Primary responsibility |
| --- | --- |
| Production code | **How**: structure, naming, types, and responsibility split. |
| Tests | **What**: observable behavior and contracts under stated conditions. |
| Commit log | **Why**: reason for the change; the title carries its searchable what. |
| Code comments | **Why not / constraints**: rejected alternatives, external constraints, and non-obvious invariants. |
| GitHub Issue / Project | Bugs, unresolved/future work, roadmaps, and durable implementation plans. |
| PR review / Issue comment / final response | Task findings, validation, blockers, and closeout. |
| Current specification | Current product behavior, interfaces, schemas, architecture, and policy. |
| `docs/decisions.md` | Durable rationale not reconstructable from current code/specifications, not progress/status. |
| Gitignored local output | Temporary scratch and generated output, never promoted into task documentation. |
| Git history | Implementation history without a parallel status ledger. |

## Production Code: How

Express behavior through names, types, and responsibility split rather than comments narrating the code. For example, `users.Where(user => user.IsActive)` needs no comment saying it filters active users.

## Test Code: What

Use `Method_Scenario_ExpectedOutcome` names, such as `Evaluate_ProjectionWorkerAbsent_IsNotReadyWithProjectionWorkerMissing`. Derive expectations from the owning current specification under AGENTS.md precedence. Assert routes, serialized types, status codes, units, and rendered behavior rather than private structure or internal call counts, unless that interaction is itself the observable contract.

## Commit Log: Why

Use `<work item>: <type>(<scope>): <subject>`. For `feat`, `fix`, `refactor`, and `perf`, the body records why the change was needed and what was wrong before, not a file-by-file work log. For `docs`, `chore`, `test`, and `style`, the body is optional when the title already explains why.

```text
Issue #270: fix(demo): isolate synthetic ingestion from normal capture

The previous procedure stopped the default capture runtime before preparing
synthetic data. Use a task-owned runtime so demo preparation cannot imply
permission to interrupt the user's capture.
```

## Code Comments: Why Not, And Constraints Code Cannot Show

Keep rationale that code cannot express clearly: why an apparent simplification violates an invariant, why an external limitation exists, or why an alternative was rejected. Do not restate the adjacent statement.

## Primary, Not Exclusive

Names and commit titles also express what; a regression test may include a short why. These are responsibilities, not bans on useful context. Execution details belong in [repository workflow](repository-workflow.md); review reporting belongs in [review workflow](review-workflow.md).
