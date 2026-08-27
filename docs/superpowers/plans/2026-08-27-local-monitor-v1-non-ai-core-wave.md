# Local Monitor v1 Non-AI Core Completion Wave Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use
> `superpowers:subagent-driven-development` and
> `superpowers:test-driven-development`. Implement one task at a time. A fresh
> worker implements each task, a separate worker reviews it against this plan
> and the owning specification, and the primary agent verifies the review fix
> before the next task.

**Goal:** Starting from `origin/main` at
`e197d698c18618e4e792e7b8b6062b2598f69d25`, complete the raw-default Local
Monitor v1 non-AI journey from Repository selection through Session Explorer,
Session Workspace, on-demand raw content, and deterministic two-cohort Compare.

**Branch/worktree:** `codex/local-monitor-v1-non-ai-core` in
`C:\Users\mwam0\Documents\Codex\copilot-agent-observability\.worktrees\local-monitor-v1-non-ai-core`.

**Architecture:** Keep the existing Repository, Archive, Skill, Retention, and
Workspace authorities. Migrate the Workspace projection exactly from v4 to v5
so source-authorized semantic Tool/Sub-agent objects and all v2 detail facts are
available from the coherent detail snapshot. Activate the six strict human
routes through one raw-path dispatcher and one shared state presenter. Razor
Pages and vanilla JavaScript consume only bounded application/API contracts.
Compare persists one immutable, 24-hour operational snapshot and deterministic
receipt and is removed from runtime-backup staging. Its internal service and
GET result page do not imply a preview/create HTTP wire that #165/#166 did not
define.

**Tech stack:** C#/.NET 10, ASP.NET Core/Razor Pages, Microsoft.Data.Sqlite,
vanilla JavaScript/CSS, xUnit, JSON Schema Draft 2020-12, Playwright Chromium.
No npm, frontend build chain, SPA framework, LLM, or AI settings/result/status.

**Owning authority:** the user's 2026-08-27 wave instruction; current bodies and
latest comments for #133, #134, #136, #137, #138, #139, #140, #165, #166, and
#167; `local-monitor-v1-contract-index.md`, `local-monitor-v1-ia.md`,
`local-monitor-v1-route-transport.md`, and the affected narrow specifications.
The user's sole-current v2/v5 decision supersedes the checked-in #134 v1/v4
detail contract. `/historical-analysis` remains #164-owned and is unchanged.

## Binding design rulings

- Summary, Timeline, Node, and Content use only the four `response.v2` schema
  tokens on their existing routes. There is no v1/v2 dual serve, negotiation,
  compatibility reader, or read-time fallback.
- `local_workspace_projection:5` is the only runtime Workspace reader. Exact
  v1→v2→v3→v4→v5 migration remains atomic and fail-closed.
- A source event is not itself a semantic Tool/Sub-agent object. Lifecycle
  events aggregate only through an exact source-authored/native identity.
  Name, time, proximity, count, and execution membership are never join keys.
- Claude `SubagentStart.agent_id` is a native run identity. It is never exposed
  or retained as `subagent_input`. Exact input stays unavailable unless an
  authorized input carrier exists.
- The stable Compare result route is
  `/repositories/{repositoryId}/comparisons/{comparisonId}`. Primary human
  routes remain GET/HEAD-only. The exact body-bearing preview/create transport
  is a limited owner-contract blocker: do not add a page POST or speculative
  public Compare JSON wire. Internal snapshot/application work proceeds.
- Compare canonicalizes exact selected Session IDs by ordinal opaque ID within
  cohort `a`, then `b`; this is private receipt implementation state, not a
  human alias or selection repair rule. Every frame is domain-separated and
  versioned. Decimal metric calculation never uses binary floating point.
- A Compare fact not supported by exact current authorities is preserved as
  `not_observed`, `source_unsupported`, `capture_gap`,
  `certification_pending`, `inconsistent`, or `projection_invalid`. It is never
  inferred or converted to zero. The fixed section/row remains visible.
- `local_comparison_*` operational tables and expiry tombstones are validated
  in the source database, removed only from the private backup staging copy in
  dependency-safe order, absent from manifest/restore/export, and recreated
  empty after restore.

## Cross-cutting invariants

- Preserve frozen `/api/monitor/*`, `/api/session-workspace/*` v1, and SSE
  route/status/header/shape/order/bytes.
- Register no Razor Page, human static asset, `/api/local-monitor/v1/*`, or
  Compare application in sanitized-only composition.
- Host validation precedes every Local Monitor owner. Detail GET/HEAD
  precedence is Host → method → path/query → same-origin → Session → revision
  → execution/node/cursor → Retention.
- All raw-default human/API responses are no-store. Raw text appears only after
  an explicit content action and is inserted with `textContent` or server
  escaping. It never enters a URL, log, telemetry, error, exception, reusable
  DOM attribute, browser storage, or repository artifact.
- Keep q/model/non-default limit and pre-preview cohort selection in current
  document memory only. Safe closed filters/cursor/settings remain the only URL
  Explorer state.
- Never repair invalid/stale Repository, Session, execution, node, comparison,
  parent, Skill, Tool, or Sub-agent identity by name/time/proximity.
- Use the accepted #135 shell: 48px header, 24px content padding, no sidebar,
  accepted dark tokens. At 1366×768 there is no page horizontal scrolling.
  Inspector defaults to 380px within 360..420px; below 1180px it is an overlay.
- Shared state presentation is the only Japanese mapping for recorded,
  explicit zero with complete coverage, not observed, source unsupported,
  capture gap, certification pending, not captured, expired/deleted/read
  denied, inconsistent, and projection invalid.
- Production changes follow red-green TDD. Each logical task records the
  failing test, implements the smallest coherent change, reruns focused and
  regression tests, receives independent review, and commits with its primary
  Issue number.

---

### Task 1: Promote the owning specifications and executable contracts

**Files:**

- Modify: `docs/specifications/interfaces/local-monitor-v1-contract-index.md`
- Modify: `docs/specifications/interfaces/local-monitor-v1-session-detail.md`
- Modify: `docs/specifications/interfaces/local-monitor-v1-route-transport.md`
- Modify: `docs/specifications/interfaces/local-monitor-v1-ia.md`
- Modify: `docs/specifications/interfaces/runtime-backup-restore.md`
- Modify: `docs/architecture.md`
- Modify: `docs/decisions.md`
- Modify: `docs/specifications/contracts/local-monitor-v1/session-summary.response.schema.json`
- Modify: `docs/specifications/contracts/local-monitor-v1/session-timeline.response.schema.json`
- Modify: `docs/specifications/contracts/local-monitor-v1/session-node.response.schema.json`
- Create: `docs/specifications/contracts/local-monitor-v1/node-content.response.schema.json`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1SessionDetail/*`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionDetailSpecificationTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1CompareSpecificationTests.cs`

**Exact contract delta to freeze:**

- Keep the four existing detail route paths, bounds, cursor binding, coherent
  revision, raw Retention lease, strict UTF-8, and no-partial-oversize behavior.
  Change only the sole-current success graphs/tokens and the newly common
  same-origin guard.
- Schema tokens are exactly
  `local-monitor-session-summary.response.v2`,
  `local-monitor-session-timeline.response.v2`,
  `local-monitor-session-node.response.v2`, and
  `local-monitor-node-content.response.v2`. No v1 token remains in the active
  spec/schema/golden transport and no version selector exists.
- Summary adds an explicit Boolean `latest` to every execution and a closed
  signal-family capture-coverage collection sufficient for the UI to present
  recorded, complete explicit zero, not observed, source unsupported, capture
  gap, certification pending, inconsistent, and projection invalid without
  inferring from other facts.
- Timeline items add exact source references, `has_more_children`, and one
  closed collapsed-child summary while retaining stable hierarchy/time
  authority. Missing time never creates a duration.
- Node adds a single closed kind-specific metadata object. Tool covers caller,
  lifecycle/status/exit, exact MCP server/tool identity, input/result/error
  availability, retry/recovery, child activity, and exact source references.
  Skill covers current-valid state, source, trigger, inventory reference, and
  historical snapshot reference only. Sub-agent covers selected/started/
  completed/failed/deselected, exact input availability, activity/tokens/
  children, and exact source references. Error/Permission/Event/Retry receive
  closed metadata appropriate to their exact source facts; no open bag exists.
- Content success changes from `text/plain` to a closed JSON entity containing
  schema token, workspace revision, Session ID, node ID, part, state, exact
  source item/revision reference, inert text, UTF-8 byte length, Unicode scalar
  length, and `truncation:false`. Raw text remains byte-for-byte decoded strict
  UTF-8 inside the JSON string value; no normalization or partial response.
- `local_workspace_projection:5` is sole-current. The contract explicitly
  removes the v4 `SubagentStart.agent_id` → `subagent_input` mapping: `agent_id`
  is technical/native Run identity only.
- Compare specification proof freezes only the user/#165 fixed section order,
  deterministic scalar rules, named-row reachability, 24-hour exclusion from
  backup, and forbidden labels. It must not invent preview/create API paths,
  success DTOs, bounds, or errors while that owner wire is absent.

- [ ] Write failing specification tests for the four v2 schema tokens, closed
  kind metadata, content JSON, exact property order, v5-only projection text,
  fixed Compare section order, and forbidden Compare labels.
- [ ] Run the two specification suites and retain the RED output showing the
  checked-in v1/v4 artifacts.
- [ ] Update the narrow owning specs. Mark v2/v5 sole-current, name every
  Compare staging-excluded table category, and retain the explicit #164
  `/historical-analysis` exception.
- [ ] Replace detail schemas/goldens as v2, including literal Content JSON
  bytes and HEAD representation length. Do not derive expected bytes from the
  production serializer.
- [ ] Re-run specification tests and existing collection contract/golden tests.
- [ ] Review for any accidental change to collection/frozen machine contracts.
- [ ] Commit as `Issue #134: docs(contract): promote Local Monitor detail v2`.

### Task 2: Migrate `local_workspace_projection` v4→v5 and close semantic identity

**Files:**

- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionSchemaV1.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionModels.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionDetailModels.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionDetailSnapshotContributor.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/LocalWorkspaceProjectionBackupValidation.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/SqliteRuntimeBackupService.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceProjectionSchemaTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceProjectionBackfillTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceSessionDetailAuthorityMatrixTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/RuntimeBackupLocalWorkspaceProjectionTests.cs`

- [ ] Add RED migration tests for exact v4→v5, rollback at each failpoint,
  deterministic backfill, rerun idempotence, partial/future fail-closed, stable
  execution/node IDs, backup semantic validation, and restore rebuild.
- [ ] Add RED source matrices proving exact Tool lifecycle aggregation and
  exact Sub-agent lifecycle aggregation, including two same-name/concurrent
  objects that remain distinct and lifecycle events without exact identity
  that remain Event nodes.
- [ ] Add a RED regression proving `/agent_id` creates no `subagent_input`
  content reference and is retained only as the technical/native Run identity.
- [ ] Add v5 owned semantic-object/reference columns or normalized tables only
  where required. Preserve raw bytes in owner stores and store closed sanitized
  facts/reference tuples in Workspace.
- [ ] Backfill only exact source-authorized groups. Keep individual unmatched
  lifecycle events as Event/Error/Retry/Permission nodes under exact/unknown
  hierarchy; do not collapse them heuristically.
- [ ] Thread v5 refresh through existing ingestion, Retention cleanup, Skill
  generation publication, backup, restore staging, and publication gate seams.
- [ ] Run migration/backfill/authority/backup tests plus all Workspace tests.
- [ ] Independent review checks SQL bounds, migration atomicity, join keys,
  content pointer removal, and stable identities.
- [ ] Commit as `Issue #134: feat(workspace): migrate semantic projection to v5`.

### Task 3: Implement the sole-current detail v2 transport

**Files:**

- Modify: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1SessionDetailApplication.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1SessionDetailRoutes.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceNodeContentReader.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryScopeContracts.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/SqliteLocalRepositoryScopeSnapshotService.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionDetailApplicationTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionDetailRouteTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceSessionDetailRevisionMatrixTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionDetailPagingProofTests.cs`

- [ ] Add RED serializer tests for Summary `latest` and signal-family coverage;
  Timeline exact references, `has_more_children`, collapsed child summary, and
  time/hierarchy authority; Node closed Tool/Skill/Sub-agent/Error/Permission/
  Event/Retry metadata; Content closed JSON fields and Unicode lengths.
- [ ] Add RED common-security tests for same-origin on every Summary/Timeline/
  Node/Content GET/HEAD and the exact precedence. Preserve strict HEAD length,
  no-store, fixed errors, and absence of CORS/cookies/ETag/Location.
- [ ] Add RED Retention matrices for available, not captured, expired, deleted,
  read denied, oversized, malformed UTF-8, locator drift, lease loss, and no
  partial response/no raw echo.
- [ ] Extend the coherent snapshot models and contributor so the serializer
  performs no additional DB read and no heuristic join. Compute explicit latest
  execution facts rather than relying on client order.
- [ ] Serialize complete v2 entities with `Utf8JsonWriter`, closed property
  order, 8 MiB JSON ceiling, and 1 MiB raw text ceiling. Content includes inert
  text plus UTF-8 byte and Unicode scalar counts and `truncation:false`.
- [ ] Apply the common origin/no-store policy before resource resolution as
  specified, while retaining global Host precedence.
- [ ] Run detail application/route/revision/paging/Retention tests and frozen
  collection/API/SSE regressions.
- [ ] Independent review checks exact bytes, data access count, precedence,
  lease terminal, and absence of v1 token/compatibility code.
- [ ] Commit as `Issue #134: feat(api): serve Session detail response v2`.

### Task 4: Activate exact human routing, Settings history, and shared fact states

**Files:**

- Create: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1HumanRoutes.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/LocalMonitorV1.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/Pages/LocalMonitorV1.cshtml.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1FactStatePresentation.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Shared/_FactState.cshtml`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/Shared/_Layout.cshtml`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor-shell.js`
- Create: `src/CopilotAgentObservability.LocalMonitor/wwwroot/local-monitor-v1-shared.js`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1HumanRouteTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorShellPlaywrightTests.cs`

- [ ] Add RED HTTP matrices for the six exact primary routes, malformed versus
  near paths, reserved unassigned precedence, GET/HEAD/405, recovery states,
  technical route precedence, and sanitized-only absence.
- [ ] Add RED shared-state tests for all accepted states, explicit zero only
  with complete coverage, Japanese labels, and prohibition of internal tokens.
- [ ] Add RED browser tests for Settings push/back/forward/close while
  preserving the rest of each page query and focus return.
- [ ] Implement one raw-target dispatcher before Razor fallback. Dispatch exact
  technical routes first, classify primary/near paths with the existing pure
  parser, and render one bounded page shell with a closed route model.
- [ ] Register the dispatcher, Razor page, and v1 assets only in raw-default.
  Keep `/historical-analysis` unchanged.
- [ ] Implement shared fact rendering in server and browser code from one
  closed mapping. Never render an enum token directly.
- [ ] Run route/parser/security/shell tests and frozen technical route tests.
- [ ] Independent review checks no redirect/alias/framework-permissive path,
  raw-target logging, sanitized-only registration, or technical-route capture.
- [ ] Commit as `Issue #136: feat(routes): activate strict human navigation`.

### Task 5: Complete Repository selection and management entry points

**Files:**

- Replace: `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml`
- Replace: `src/CopilotAgentObservability.LocalMonitor/Pages/Index.cshtml.cs`
- Remove: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor-overview.js`
- Create: `src/CopilotAgentObservability.LocalMonitor/wwwroot/local-monitor-repositories.js`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.css`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/LocalMonitorV1.cshtml`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1RepositorySelectionPlaywrightTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`

- [ ] Add RED Playwright/HTTP tests for Repository cards, active Session count,
  safe last observed time, all/unassigned virtual scopes, archived management,
  create/rename/archive/restore, and assignment/conflict correction entry.
- [ ] Add regressions proving all/unassigned never become Repository rows and
  same-name/renamed Repositories still navigate by opaque ID.
- [ ] Render root as Repository selection using the existing collection and
  management/archive APIs. Use card text only for display and opaque local UUID
  only for route identity.
- [ ] Implement bounded pagination/empty/unavailable states and focus-visible
  keyboard navigation within the accepted shell/card geometry.
- [ ] Run Repository collection/catalog/archive tests and Repository page
  Playwright tests at 1366×768.
- [ ] Independent review checks direct-store access, locator/path leakage,
  virtual-row persistence, and identity construction.
- [ ] Commit as `Issue #167: feat(ui): complete Repository selection`.

### Task 6: Complete the shared Session Explorer and cohort handoff

**Files:**

- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/LocalMonitorV1.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/wwwroot/local-monitor-explorer.js`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.css`
- Remove: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml`
- Remove: `src/CopilotAgentObservability.LocalMonitor/Pages/Traces.cshtml.cs`
- Remove: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor-tracelist.js`
- Replace: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorTraceListPlaywrightTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionExplorerPlaywrightTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`

- [ ] Add RED journeys for Repository/all/unassigned scopes, direct row open,
  search/filter/pagination, archive default exclusion, actions, URL-safe state,
  q/model/non-default-limit reset, reload/back/forward, stale cursor, and late
  response suppression.
- [ ] Add RED Compare-mode tests for explicit-only checkboxes, `a`/`b`
  identity, `基準`/`比較対象`, exact-digest-only before/after labels, disjoint
  cohorts, invalid/duplicate/archived/excluded reasons, total ≤200, bounded
  preview model, and the exact opaque handoff boundary. Do not register a
  network handoff until the owner contract supplies its request/response wire.
- [ ] Implement one Explorer controller with safe URL authority, transient
  request state, exact 16-property POST builder, request generation/abort,
  direct-open list renderer, paginator, and transient cohort controller.
- [ ] Use the shared fact renderer for token/cache/activity/capture states.
  Search input uses `autocomplete=off`; q/model never enter URL/history/storage/
  cache/console/error/reusable dataset.
- [ ] Retire only exact `/traces` atomically after all three Explorer scopes are
  functional. Preserve `/traces/{traceId}` and descendants unchanged.
- [ ] Run collection/search/cursor/archive tests and all Explorer Playwright
  journeys, including back/reload and keyboard-only operation.
- [ ] Independent review checks transient leakage, preview absence, cursor
  repair, archive inclusion, direct Session identity, and old-route boundary.
- [ ] Commit as `Issue #138: feat(ui): complete Session Explorer cohorts`.

### Task 7: Implement deterministic 24-hour Repository Compare

**Files:**

- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Comparison/LocalComparisonSchemaV1.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Comparison/LocalComparisonModels.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Comparison/SqliteLocalComparisonStore.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Comparison/LocalComparisonApplicationService.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryScopeContracts.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/SqliteLocalRepositoryScopeSnapshotService.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/SqliteRuntimeBackupService.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Comparison/LocalComparisonPageApplication.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/LocalMonitorV1.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/wwwroot/local-monitor-compare.js`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.css`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalComparisonSchemaTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalComparisonApplicationTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalComparisonBackupTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1ComparePlaywrightTests.cs`

- [ ] Add RED schema tests for immutable snapshot/session/named/evidence rows,
  24-hour expiry, idempotent tombstones, partial/future failure, atomic cleanup,
  and empty restore/startup.
- [ ] Add RED formula tests for exact registry order, per-Session scalar facts,
  decimal median/min/max/available/session count/total, B−A difference,
  relative-difference guard/rounding, cache derivation, missing-to-zero
  prohibition, and ordinal full named unions.
- [ ] Add RED cohort tests for exact Repository membership, disjoint/nonempty
  `a`/`b`, ≤200 total, archive exclusions, immutable revisions, and no mutable
  reread after creation.
- [ ] Add RED receipt tests for versioned domain-separated canonical bytes,
  stable SHA-256 across reload/restart, and sensitivity to every frozen fact.
- [ ] Add RED backup tests proving source validation, staging-only table removal,
  manifest/restore/export absence, and no source mutation.
- [ ] Implement one coherent comparison contributor within the existing
  publication lease/read transaction. Freeze only source-authorized facts and
  exact evidence IDs; unsupported facts remain explicit states.
- [ ] Persist the accepted snapshot/result/evidence and receipt atomically.
  Reads use only frozen rows. Expiry inserts the exact tombstone and deletes
  operational rows atomically.
- [ ] Render the nine fixed sections in order with `基準`, `比較対象`, `差`, all
  named rows reachable via bounded search/pagination, exact Session/evidence
  drill-down, and fixed expired/missing states. Do not render forbidden verdict,
  ranking, composite, or narrative sections.
- [ ] Run Compare schema/application/backup/UI tests plus Repository/archive/
  Workspace coherence regressions.
- [ ] Independent review checks decimal math, evidence bounds, no current-data
  reinterpretation, no direct UI SQL, no AI/effect-comparison reuse, and backup
  exclusion.
- [ ] Commit as `Issue #166: feat(compare): add deterministic cohort snapshots`.

### Task 8: Complete the Session Workspace UI

**Files:**

- Modify: `src/CopilotAgentObservability.LocalMonitor/Pages/LocalMonitorV1.cshtml`
- Create: `src/CopilotAgentObservability.LocalMonitor/wwwroot/local-monitor-workspace.js`
- Modify: `src/CopilotAgentObservability.LocalMonitor/wwwroot/monitor.css`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionWorkspacePlaywrightTests.cs`

- [ ] Add RED Playwright fixtures/journeys for fixed Session context and
  summaries, execution headers/latest default, hierarchical timeline/lazy
  children, unknown-parent group, missing time, inspector kinds, exact deep
  link ancestor expansion/selection, stale revision refresh, and no summary
  mutation on node selection.
- [ ] Add RED raw-content dialog tests for explicit read, inert text, close,
  focus return, expiry/denial/oversize, and no automatic fetch.
- [ ] Add RED accessibility/layout tests for keyboard tree semantics, focus
  retention, non-color signals, 1366×768/no page horizontal scroll, 380px
  inspector, 360..420 clamp, and <1180 overlay/Escape/focus return.
- [ ] Implement fixed Session context/summary and execution header rendering
  from Summary v2. Open the explicit `latest` execution; never infer it from
  order and never let node selection replace Session facts.
- [ ] Implement one combined hierarchical time-axis timeline with bounded
  internal scroll and lazy child pages. Deep links fetch/expand the exact parent
  path from Node v2 and do not repair stale identity.
- [ ] Implement kind-specific Inspector views for Tool, Skill, Sub-agent, Error,
  Permission, Event, and Retry. Historical/current Skill actions call only the
  existing #158 routes and remain visually distinct.
- [ ] Implement on-demand Content v2 fetch/dialog using inert `textContent` and
  return focus to the invoker.
- [ ] Run detail route tests and full Workspace Playwright suite at both
  required viewport modes.
- [ ] Independent review checks auto-fetch/raw leaks, latest inference,
  hierarchy repair, fake durations, tree ARIA/focus, and horizontal overflow.
- [ ] Commit as `Issue #140: feat(ui): complete Session Workspace timeline`.

### Task 9: Close integrated routes and end-to-end journeys

**Files:**

- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1EndToEndPlaywrightTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSecurityBoundaryTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorUiTests.cs`
- Modify only production files required by evidence-backed defects.

- [ ] Add one seeded production-path journey covering Repository card → scoped
  Explorer → Session Workspace and one all/unassigned path with filter,
  pagination, reload, open, and back.
- [ ] Cover exact node evidence reload, raw open/close/focus return, stale
  revision explicit refresh, cohort preview/snapshot/Compare/Session drill-down,
  rename/same-name, archived Repository/Session, keyboard-only journey,
  1366×768, <1180 overlay, sanitized-only absence, `/traces` retirement, and
  retained `/traces/{traceId}`.
- [ ] Capture and inspect screenshots for Repository, Explorer, Workspace
  desktop/overlay, and Compare. Record viewport overflow/focus assertions in
  Playwright rather than relying only on visual judgement.
- [ ] Run the complete Local Monitor test project. For every failure use
  `superpowers:systematic-debugging`: establish root cause, add/retain a failing
  regression, make one fix, and rerun the affected class before the project.
- [ ] Independent final code review covers the entire base..HEAD diff against
  the user instruction and current owning specs.
- [ ] Commit any evidence-backed integration correction under its primary Issue.

### Task 10: Final documentation, history, and validation

**Files:**

- Modify: `docs/specifications/contracts/validation-matrix/v1/future-surface-registry.json`
- Modify derived docs only where current behavior changed.
- Modify no production file after final-HEAD validation begins.

- [ ] Update validation registry entries for Repository selection, Explorer,
  Workspace, and Compare from future/not-available to their exact executable
  test evidence. Keep #136 overall incomplete because #164 still owns
  `/historical-analysis` retirement.
- [ ] Run `git diff --check`, self-review the full diff, and request an
  independent final review. Resolve every Important/Blocking finding through
  red-green verification.
- [ ] Run the final pinned validation in this exact order against one unchanged
  HEAD and retain command, counts, duration, and exit code:

  1. `pwsh scripts/agent/sync-claude-skills.ps1 -Check`
  2. `dotnet build CopilotAgentObservability.slnx`
  3. `pwsh scripts/test/install-playwright-chromium.ps1`
  4. focused changed unit/integration/contract/golden tests
  5. focused Playwright tests
  6. `dotnet test CopilotAgentObservability.slnx`

- [ ] If any validation fails, times out, hangs, is manually stopped, or flakes,
  append it to the execution record before diagnosis and preserve all reruns.
  A final fresh full solution run must exit 0 on final HEAD.
- [ ] Use `superpowers:finishing-a-development-branch` to inspect integration
  options, but do not push, create a PR, tag, release, or mutate Issues.
- [ ] Confirm branch/worktree, final HEAD, clean status, commit list, validation
  evidence, unresolved items, and evidence-based closeability for each Issue.

## Execution record

- Baseline full solution run started on `e197d698` before any edits. It produced
  at least one failure in
  `CopilotSdkTelemetryCompileTests.PinnedGuidanceSample_CompilesWithoutModifyingReferencedProjectGeneratedState`:
  `IOException` while snapshotting the LocalMonitor generated
  `obj/.../copilot.tgz` concurrently held by another process. The run remained
  active after the failure; its final exit/count and the systematic root-cause
  investigation must be appended here and reported in the final failure log.
