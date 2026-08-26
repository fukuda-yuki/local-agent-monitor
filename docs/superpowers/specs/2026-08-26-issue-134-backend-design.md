# Issue #134 Remaining Backend Design

Status: approved by the user's 2026-08-26 instruction, which delegates design
decisions to the orchestrator and requires continuous execution.

## Goal and boundary

Complete the remaining backend scope on top of the integrated collection
milestone. The change first closes the current-valid Skill aggregation gap,
then freezes the four Session-detail response contracts, migrates the Workspace
projection to v4, and implements coherent summary/timeline/node/content reads.
It adds no Razor/UI assets, primary-route activation, Compare, AI, `/traces`
retirement, Issue mutation, push, or PR.

The exact public and storage behavior is owned by
`docs/specifications/interfaces/local-monitor-v1-session-detail.md`.

## Selected architecture

The selected approach extends the existing #156 Repository snapshot
coordinator into a reusable host snapshot coordinator with collection and
target-Session contributors. This is preferred over a new detail service with
its own connection because the existing coordinator already enforces the one
publication lease/connection/transaction boundary and composes #161 archive
facts without direct catalog SQL. It is also preferred over materializing all
detail facts into one denormalized Session row because timeline paging and raw
availability need bounded child reads and independent exact identities.

The Skill reader is corrected upstream: one transaction-capable projection
produces admitted OTel and SDK invocations, exact cross-arm deduplication,
search names, aggregates, execution membership, and node facts. Workspace
collection and detail both consume it. No second SDK reader or OTel aggregate
meaning remains.

V4 stores stable execution/node identities, relationship/time authority,
sanitized metadata, edges, and raw references. It does not copy raw content.
Summary and every follow-up read use one canonical revision frame built inside
the coherent snapshot. Raw content then adds the existing committed Retention
access lease around its exact carrier read and response completion.

## Alternatives rejected

1. A separate detail snapshot service was rejected because it would duplicate
   publication/catalog/archive/Skill readers and make mixed revisions possible.
2. Read-time v3/v4 compatibility was rejected because the request requires one
   migrated v4 authority and fail-closed partial/future shapes.
3. Heuristic cross-arm or parent matching was rejected because exact producer
   identities are available and the contract requires unknown rather than
   inferred relationships.

## Data flow

```text
publication read lease
  -> one SQLite connection + read transaction
    -> target Session projection
    -> #156 assignment/catalog + #161 archive contribution
    -> #154 current-valid Skill projection
    -> v4 execution/node/edge/content-reference rows
    -> Retention/raw availability
    -> canonical workspace revision
    -> summary OR revision-checked timeline/node/content
```

Content success continues with one committed Retention access lease. Lease loss
or any carrier/state mismatch discards the candidate bytes and returns the
fixed error.

## Error handling and bounds

All limits are admission limits, never silent truncation: 256 executions,
4,096 nodes, 200 timeline/related rows per page, six content references per
node, 1 MiB raw content, and the existing response ceilings. Identity,
relationship, time, Retention, Skill, or schema inconsistency fails closed.

## Test strategy

Implementation uses red-green TDD per task. Focused suites cover the Skill
matrix, contract schemas/goldens/headers, v3-to-v4 migration and identity,
bounded snapshot/query counts, revision changes, raw lease races, and frozen
surface regressions. Final validation runs the canonical skill mirror check,
solution build, Playwright prerequisite, and one complete solution test run on
the final HEAD.

