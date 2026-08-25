# Task 2 implementation report

## Status

Implemented `local_workspace_projection:1`, its six durable tables, idempotent set-based Session v14 backfill/refresh, the typed production Session snapshot contributor, raw-default DI registration, and Runtime Backup component ownership and validation. No HTTP route was registered.

## TDD evidence

- RED: the focused projection command failed at compile time because `LocalWorkspaceProjectionSchemaV1`, `LocalWorkspaceSessionSnapshotContributor`, and `LocalWorkspaceProjectionRow` did not exist.
- GREEN: the focused projection/contributor/backup/registration command passed 12/12 tests.
- Regression: the required combined projection, repository scope snapshot, Session migration fixture, and Runtime Backup filter passed 1,098/1,098 tests in 3m14s.
- `git diff --check` completed without findings.

## Behavior and ownership

- Schema creation and initial backfill occur in one caller-owned transaction and fail closed for partial or malformed owned schema.
- Re-running initialization refreshes the projection without revision-seed drift for unchanged Session facts.
- Session Run token observations retain missing producer totals and unavailable cache/reasoning components rather than reconstructing them.
- Token aggregation ranks one observation per execution and reports inconsistent cache/input relationships without producing derived values.
- Instruction labels retain their exact source event identity and expiry; expired content is excluded on refresh.
- The production contributor refreshes before entering its bounded supplied read capability, returns immutable typed rows, uses stable ordinal ordering, and rejects more than 10,000 rows.
- Runtime Backup now recognizes `local_workspace_projection:1`, assigns all six `local_workspace_*` tables to it, validates its Session v14 parent and exact schema, migrates it immediately after Session, and preserves legacy Session v13 test archives by excluding the Session v14 child component.
- Sanitized-only mode remains without repository scope or Session contributor registration; raw-default mode registers the production contributor.

## Self-review

Reviewed schema ownership, transaction boundaries, Session v14 parent validation, null-state preservation, authority ranking, retention expiry, immutable contributor output, raw/sanitized registration, backup component vectors, legacy migration fixtures, and the complete scoped diff. No unrelated files or dependencies were added.

## Residual concerns

- The currently available Session v14 structured schema exposes Session Run token components but no accepted exact-linked LLM-span batch read authority. The durable schema and contributor implement the required authority rank/dedupe semantics, but the refresh currently materializes only `session_run` observations.
- Explicit 10,000-session/200-page load coverage is represented by the contributor bound and set-based reads, but this change does not add a dedicated 10,000-row performance fixture.
- Build output retains pre-existing nullable warnings in unrelated tests and one compiler capture warning in the contributor; there are no build errors.

## Fix Round 1 — NEEDS_CONTEXT

The review rejected the separate-connection pre-read refresh. Focused inspection found no accepted existing seam that can satisfy the required replacement without inventing architecture:

- `SqliteSessionStore.WriteCore` (`src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`, beginning at line 446) creates and commits its transaction internally. The last Session mutation is `ReduceSessionOutcomeAndCompleteness` at line 615 and commit is line 617. There is no injected Session transaction-participant collection or callback.
- Retention deletion is independently owned by `SessionEventContentRetentionAdapter.DeleteAsync` (`src/CopilotAgentObservability.Persistence.Sqlite/Retention/SessionEventContentRetentionAdapter.cs`). Its callback receives the Retention-owned connection/transaction, but there is no registered downstream projection participant seam.
- The #154 owner `SkillProjectionReadService` exposes only `ListCurrentInvocations(string traceId)` and `ListCurrentInventories(string traceId)`, both of which open their own connections. It exposes no batch API accepting the caller-owned SQLite connection/transaction. `ListCurrentSdkClaims(string sessionId)` is currently an empty implementation.
- `monitor_spans` contains structured token/cache/reasoning fields, but its identity is `(raw_record_id, trace_id, span_id, span_ordinal)` and it carries neither `session_id` nor Session execution identity. No owning specification or code seam found in the focused search defines the exact accepted mapping from one LLM span to one Session execution for authority dedupe.

Because the controller explicitly prohibited inventing either a transaction participant or an exact LLM-source rule, no Fix Round 1 production/test changes were made. Required product/architecture context is one of:

1. the exact existing Session/Retention transaction-participant symbol to consume, or authorization to add a named shared participant contract and wire both owners; and
2. the exact #154 batch-read symbol plus the accepted LLM-span-to-Session-execution identity rule, or an explicit decision that LLM authority is unavailable and `llm_span` must be removed from the Task 2 durable schema.

Inspection commands:

```powershell
rg -n "participant|Participant|transaction participant|Session.*Participant|ISession.*Participant|Commit.*Participant|BeforeCommit|AfterCommit" src tests -g '*.cs'
rg -n "LLM|llm_span|cache_read|cache_creation|reasoning_tokens|gen_ai.usage" src tests -g '*.cs'
rg -n "Skill.*Batch|Batch.*Skill|Read.*Skill" src tests -g '*.cs'
```
